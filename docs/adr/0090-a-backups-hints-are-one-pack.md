# ADR-0090 — A backup's hints are one pack

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-008 (measured, and met for everything but the blob covers); keeps NFR-PERF-005 and FR-MAN-003
**Related:** [ADR-0007](0007-logical-object-identifiers-in-manifests.md) Amendment 2 (the per-file hint this replaces for writing), [ADR-0088](0088-a-backups-percentage-is-what-it-has-backed-up.md) decision 4 (the sixteen-at-once writes this replaces, and the alternative it rejected), [ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md) §Decision 7 (why a hint takes the intent's sequence), [ADR-0009](0009-garbage-collection-safety.md) (the blob covers that remain), [Q21](../open-questions.md#closed), [specification 06 §11](../../specifications/repository-format/06-manifests.md#11-source-identity)

**Built:**
- The pack: `Repository.Format/Manifests/SourceIdentityPack` — the body and its codec, object type `0x11` in `Domain/ObjectType`, its key in `Repository.Packing/MetadataStoreKeys`.
- The writer: `Repository/ManifestBuilder` seals and puts a publication's packs, and `Repository/SnapshotPublication` writes one in place of a hint per created version, before the snapshot record as before.
- The readers: `Repository/SourceIdentityPackIndex` reads one device's packs up to a bound; `Repository/SourceIdentityLookup` keeps the per-file reader and asks it only for a source key no pack names, and only where per-file hints exist.
- The tests:
  - `Repository.Tests/UploadBudgetTests` — NFR-PERF-008 measured by the kind of object each request was for, first backup and incremental: the requests beside the blobs are the same for the same bytes in 64 files as in 1 024, and for 16 changed files as for 256; each blob is covered by one intent extension of its own; per GiB at the object-store blob profile, data blobs are within 10 and everything but the blob covers within 20
  - `Repository.Tests/HintPackTests` — a backup writes one pack and no per-file hint; a backup that creates nothing writes none; more hints than a pack holds become numbered parts in source-key order; the index answers the newest version at or before its bound, reads only its device's packs, and refuses a pack filed under another key; after a rebuild, renamed files find their ancestors through the pack with one read however many ask; per-file hints an older installation left still answer, alone and beside packs
  - `Repository.Tests/SourceIdentityPackCodecTests` — the body pinned to a vector built by hand from the text; canonical whatever order its entries arrive in; one entry per source key; refused when empty, too full, out of order, repeated, of an unknown schema or followed by trailing bytes
  - `Domain.Tests/ObjectTypeTests` — `0x11` assigned, `0x12` the first value above the range
  - `Repository.Tests/BackedUpProgressTests`, `InterruptionTests/TreeSnapshotInterruptionTests`, `Repository.Tests/IncrementalBackupTests` — the pack goes out before the snapshot record; the put sweep covers the eleven puts a tree publication now makes; deleting every hint, of either shape, costs only a rename's ancestry

---

## Context

A backup records, for each file version it creates, which file on disk the version came from: its **source-identity hint** (06 §11). After a catalogue rebuild the catalogue has paths but no identities, and the hints are what let a renamed file keep its history rather than start a new one.

[Q21](../open-questions.md#closed) decided how they are stored, and [ADR-0007](0007-logical-object-identifiers-in-manifests.md) Amendment 2 recorded it: **one object per created version**, at a key derived from the file's identity, so a reader lists one prefix to find one file's history. Q21 weighed that against one object per snapshot naming every file, which cost about 52 bytes per file every run, and chose the per-file object. It named the price plainly: "one store object, and on a metered store one request, per changed file". [ADR-0088](0088-a-backups-percentage-is-what-it-has-backed-up.md) then wrote them sixteen at a time, because one after another they had taken four minutes at the end of a 9 190-file first backup. It rejected packing them on the same ground Q21 gave.

The owner's first backup was 4.48 GB in 9 190 files. Its hints were 9 190 store objects: about 2 000 requests per GB, from hints alone. NFR-PERF-008 allows a backup 20 requests per GB in all. Nothing measured that half of the budget ([architecture 05 §5](../architecture/05-storage-providers.md#5-request-economics) said so), so the gap had never been seen. Concurrency hid it on a local disk; a store that charges by the request would not.

Q21 compared two shapes and there is a third. A **pack of what the backup created** costs bytes in proportion to what changed, as the per-file hint does, and one request a backup, as the whole-tree map did. What it gives up is the per-file reader's one-listing lookup.

## Decision

1. **One pack per publication.** A publication writes the source-identity hints of the versions it created as one standalone metadata record of type `0x11` at `hints/identity-pack/<device>/<captured-at>/<snapshot>/<part>` (06 §11.5). The body names the device, the snapshot, its capture time and the part, then pairs of a source key and the version captured from it, ascending by source key, each key once. Past 131 072 versions a publication writes further parts, numbered from 0. A full pack is under 7 MB, well inside the 16 MiB a standalone metadata object may be.

2. **Written where the hints were.** Before the snapshot record, under the intent's sequence number (ADR-0022 §Decision 7), and advisory. A store that refuses or faults the put costs a later reader the renames that pack would have answered, never the publication. The rule that a hardlink group's source key publishes nothing is unchanged.

3. **Read once, in the window a rebuild opens.** A publication whose catalogue has no identities for the prior snapshot reads its device's packs up to that snapshot's capture time, in key order, and a later entry for a source key replaces an earlier one. That is one listing and one read a pack, however many files ask. A pack that will not read, authenticate or parse, or whose body disagrees with its key, is passed over. The key is not covered by the AEAD, so a pack moved to an earlier time must not answer a bound its own body postdates.

4. **Per-file hints are read, never written.** Repositories hold them from every backup before this one, and the owner chose to keep reading them rather than migrate. A source key no pack names is asked of them, but only if the repository holds any, which one listing of `hints/identity/` settles. A pack's answer is taken without asking: a device's per-file hints all predate its first pack.

5. **Measured, with the remaining term named.** `Repository.Tests/UploadBudgetTests` counts every request by kind and builds the per-GiB figure from the terms, at the object-store blob profile (128 MiB):

   | Backup | Requests per GiB | Blob covers | Everything else |
   |---|---|---|---|
   | First backup, ~490 KB a file | 23 | 9 | 14 |
   | First backup, 16 KiB a file | 25 | 10 | 15 |
   | Incremental, ~490 KB a file | 25 | 9 | 16 |
   | Incremental, 16 KiB a file | 27 | 10 | 17 |

   The first row was about 2 200 before this record. The **blob covers** are what remains over 20. Every blob is preceded by the journal's intent extension that covers it (08 §4), so a blob costs two requests. Covering several blobs with one extension is GC-safety machinery ([ADR-0009](0009-garbage-collection-safety.md)) and is owed separately. The suite pins the term at one cover per blob, so that change will show, and holds everything beside it to the 20.

   > **Amended 2026-10 ([ADR-0092](0092-a-backup-names-its-blobs-a-batch-at-a-time.md)).** The blob covers are paid. A publication names its blob numbers a batch at a time, the first eight in its write intent and each later batch in one extension, so a GiB's 9 or 10 blobs cost one extension rather than 9 or 10. The totals are 15–18 per GiB, the suite holds the whole of each to 20, and NFR-PERF-008 is met.

## Consequences

- **A first backup ends sooner.** On the owner's backup, 9 190 small writes at each location become one. On a metered store, a request per created file becomes a request per backup.
- **The cold window reads more and lists less.** A reader after a rebuild reads every pack its device wrote up to the bound, which is one read per backup, and holds an entry per source key it found, about 80 bytes each. That is paid once per rebuild, by the one publication that runs while the catalogue has no identities. Against that, it no longer pays a listing and a read for each renamed or new file.
- **No format version.** The pack is a new object type under a new prefix, and hints were always advisory. A reader from before this record never lists `hints/identity-pack/`, finds no hint and matches by path, which is correct and only misses renames. The type is assigned in 02 §3.1 as the earlier hint types were.
- **Carried like any hint.** The pack lives under `hints/`, so the replicator, the copy-back and peer convergence carry it without a change.
- **The finishing count lands at once.** Contract 1.54's `hints_total` and `hints_written` count the pack's entries, and they land together when the pack does.
- **Not collected, as hints never were.** A pack names versions that later snapshots go on holding unchanged, so the pack, not the snapshot that wrote it, is what answers for them. 06 §11.4 permits collecting a hint once its snapshot is gone; nothing does.

## Alternatives considered

- **Keep the per-file hint and exempt hints from the budget.** On a store that charges by the request, a request per file is most of what a first backup costs. An exemption would make the budget describe something other than the bill.
- **One object per snapshot naming every file.** Q21's rejected shape: bytes in proportion to the repository every run, which NFR-PERF-005 forbids.
- **Shard the pack by source key.** A reader could then list one shard rather than read every pack. But a backup would write up to one object per shard, 32 for one base32 character, more than the budget's whole allowance beside the blobs. And the reader it speeds runs once per rebuild.
- **Load the packs into the catalogue when it is rebuilt.** The cold window would close: the rebuilt catalogue would have identities again. It needs a catalogue schema change and a projection step, for a window one publication long. Deferred.
- **Migrate per-file hints into packs.** The owner chose to keep reading them. A migration would rewrite advisory objects to save a listing in a window that rarely opens.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | The owner's choice of a pack per backup over per-file hints, and of reading old hints over migrating them; built with it: the pack and its codec (`Repository.Format/Manifests/SourceIdentityPack`), its key and type (`Repository.Packing/MetadataStoreKeys`, `Domain/ObjectType`), the writer (`Repository/ManifestBuilder`, `Repository/SnapshotPublication`) and the readers (`Repository/SourceIdentityPackIndex`, `Repository/SourceIdentityLookup`), measured by `Repository.Tests/UploadBudgetTests` |
| 2026-10 | Amended | The blob covers decision 5 left owed are paid by [ADR-0092](0092-a-backup-names-its-blobs-a-batch-at-a-time.md): a backup names its blobs a batch at a time, and NFR-PERF-008 is met in total |

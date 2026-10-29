# ADR-0166 — A format-3 data record is read without its blob's envelope

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-009 (amended: a sparse restore out of format-3 data blobs is within the budget)
**Related:** [ADR-0068](0068-the-catalogue-directed-restore-read.md) (the catalogue-directed read, and the sparse case it named as not covered), [ADR-0052](0052-relocatable-records-format-v3.md) (format 3: a record carries its own nonce and its own key), [ADR-0042](0042-write-only-repositories.md) (a data record's key is sealed to the repository), [ADR-0066](0066-the-format-upgrade-record.md) (a repository holds blobs of the formats it held before)

**Built:**
- The reader: `Repository.Packing/BlobReader`'s `AssumingRelocatableData` frames a data blob as format 3 frames it, from nothing read. `Repository/RepositoryReader` reads a record by its location under that framing when the blob's envelope is not in hand, and reads the envelope only when the record will not read so. The record is then framed again from the bytes already fetched.
- The tests:
  - `Repository.Tests/SparseRecordReadTests` — a format-3 data record read by its location costs one read and opens no envelope, and each further record of its blob one read more; a format-2 record is framed from its envelope at no more reads than before and with no fall back to the footer; a format-3 record that does not verify is still a damage finding; a location pointing at another record is refused and the record asked for served from the footer; a reader with no grant is answered that the content is sealed; a data blob the store answers for and will not serve is a finding from the footer path, not a throw

---

## Context

A restore reads each record straight from the location the catalogue holds ([ADR-0068](0068-the-catalogue-directed-restore-read.md)). A blob's envelope is read once, for its framing, and held for the run. Where the first record a restore wants sits close enough to the blob's start, the envelope rides the same ranged read. Where it does not — one small record out of a large blob — the envelope is a read of its own, and that blob costs two. ADR-0068 named this sparse case as the one its budget does not cover. The option it named to close it was a catalogue schema change, persisting each blob's format, and it did not take it.

At format 3 a data record carries its own nonce, in its prefix, and its own key, sealed to the repository in the same prefix ([ADR-0052](0052-relocatable-records-format-v3.md), [ADR-0042](0042-write-only-repositories.md)). What the envelope adds to reading such a record is its format version and its class. A data blob's class is already known from the store key it was found under, and its version is 3 in every repository at format 3 or later. But a repository upgraded from format 2 still holds the format-2 blobs it held, and nothing short of the envelope says which a blob is ([ADR-0066](0066-the-format-upgrade-record.md)).

## Decision

### 1. A data record is first read as format 3 frames it

When a data blob's framing is not open and no run in hand covers its envelope, a record is read by its location as format 3 frames it, with no envelope read. The record's bytes are fetched at the longest framing any container uses, as a prefetch sizes them. A record that verifies is the answer. The blob is read the same way for every record after it, one read each.

### 2. A record that does not read so is framed from its envelope, from the bytes in hand

A format-2 record read as format 3 fails closed. Its ciphertext is taken for the record's sealed key, which does not open, so the read fails authentication and returns no bytes. The same holds for any record whose framing is not format 3's. The envelope is then read, the blob framed from it and held for the run, and the record read again from the bytes already fetched. A format-2 blob costs what it cost before: two reads for its first record and one for each after. A guess the envelope corrects is not a fall back to the footer.

### 3. Nothing a read checked is given up

Nothing is taken on trust that an envelope would have proved. A framing open authenticates nothing of the envelope, and a record's additional data binds the repository, the format version, the object's type and its id however its blob was framed. The record's header is cross-checked against the location, and its plaintext against its content identifier, on this path as on every other. A record that fails them is still a damage finding, diagnosed by the footer path as before, and a location pointing at another record is still refused.

### 4. Only data blobs, and only for a reader that can open a share

A metadata record's key derives from the class key at the envelope's key generation, so a metadata blob is still framed from its envelope. A reader with no grant to open a record's share, a write-only holder, has nothing to read a record as format 3 with, and is answered as before.

> **2026-10 ([ADR-0167](0167-a-format-3-metadata-record-is-read-without-its-blobs-envelope.md)).** A metadata record is now read the same way, at the key generation the catalogue recorded for its blob, by a write-only holder as well, since the metadata class key needs no grant. A metadata blob whose generation the catalogue did not record is still framed from its envelope. The grant this section asks for is still asked of a data record.

## Consequences

- A sparse restore out of format-3 data blobs costs one read a blob, within NFR-PERF-009's budget, with no change to the catalogue or the format.
- A restore out of format-2 blobs costs what it did.
- The plan's read figure still counts each blob's envelope ([ADR-0083 Amendment 2](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md#amendment-2-2026-10--a-plan-says-what-its-run-reads)), so a sparse read now reads up to 168 bytes a blob less than its plan says, and never more.

## What this does not do

- **Metadata blobs.** Their records need the key generation the envelope gives; a sparse read of a metadata blob still costs two reads.

  > **2026-10 ([ADR-0167](0167-a-format-3-metadata-record-is-read-without-its-blobs-envelope.md)).** Done for a metadata blob the catalogue recorded: it carries the key generation the envelope gives, so a sparse read of such a blob costs one read. One the catalogue knows only by a placeholder row still costs two.
- **Other copies.** A record read from another copy of the repository (FR-RST-007) frames its blob from that copy's envelope as before.
- **The coalesced fold.** A prefetch whose first record is within the bridge still reaches down to offset 0, so the envelope is in hand and used. That costs bytes, not reads.

  > **2026-10 ([ADR-0168](0168-a-prefetch-takes-a-blobs-envelope-only-where-the-read-may-want-it.md)).** Done: once an envelope the reader framed shows format 3, and none shows an older one, a run over records it can read before their envelope starts at the first of them.

## Alternatives considered

- **Persist each blob's format in the catalogue.** Rejected, as ADR-0068 left it. It is a schema change, a row a blob that was compacted or adopted would have to keep true, and the record itself already says whether it is format 3 by whether it opens.
- **Take the repository's format as every blob's.** Rejected. A repository upgraded to format 3 still holds its format-2 blobs, and the format-upgrade record states no cutover a blob could be judged by.
- **Read every record as format 3, metadata included, and fall back on failure.** Rejected for now. A metadata record's key needs the envelope's key generation, which the catalogue holds only for blobs it knows a row of.

  > **2026-10 ([ADR-0167](0167-a-format-3-metadata-record-is-read-without-its-blobs-envelope.md)).** Taken for the blobs it named: a metadata record is read as format 3 at the generation the catalogue holds for its blob, and falls back on failure as a data record does.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A data record is read by its location as format 3 frames it when its blob's envelope is not in hand, and the envelope is read, and the record re-framed from the bytes already fetched, only when it does not read so. No format or catalogue change. |
| 2026-10 | Accepted (amended) | Noted by [ADR-0167](0167-a-format-3-metadata-record-is-read-without-its-blobs-envelope.md): a metadata record is read the same way at the key generation the catalogue recorded for its blob, so the metadata blobs this record left framed from their envelopes are, where the catalogue recorded them, read in one read too. |
| 2026-10 | Accepted (amended) | Noted by [ADR-0168](0168-a-prefetch-takes-a-blobs-envelope-only-where-the-read-may-want-it.md): the coalesced fold this record left is taken only where the read may want the envelope. |

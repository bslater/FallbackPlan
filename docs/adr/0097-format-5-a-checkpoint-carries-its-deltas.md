# ADR-0097 — Format 5: a checkpoint carries its deltas

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-MAN-008 (amended: a checkpoint carries the deltas it subsumes, and a load reads a carried delta from it); FR-MAN-009 (amended: a normal rebuild reads checkpoints plus the deltas none carries); FR-MAN-013 (two checkpoints carrying one delta apply it once); NFR-COMP-004 (amended: `fixture-repository-v5`). Does not establish FR-MAN-017: nothing retires a delta
**Related:** [ADR-0008](0008-index-generations-and-checkpoints.md) Amendment 1 (the six constraints; Amendment 2 records this), [ADR-0096](0096-format-4-an-index-generation-per-delta.md) (format 4, whose deltas format 5 carries), [ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md) §Decisions 1 and 7 (the standalone framing; sequence accounting), [ADR-0066](0066-the-format-upgrade-record.md) (the upgrade record), [ADR-0014](0014-format-versioning-and-stability.md) (refuse by name), [specification 07 §5](../../specifications/repository-format/07-index.md#5-checkpoint)

**Built:**
- The constants: `Domain/FormatVersions` (`DeltaCheckpoints` = 5) and `Domain/FormatLimits`, which create and upgrade at 5.
- The wire: `Repository.Index/CheckpointCodec` reads and writes key 10, signed, refuses an empty one, a count that disagrees with key 2, and a checkpoint carrying deltas beside entries or watermarks of its own. `Repository.Format/Descriptor/RepositoryDescriptorCodec` adds feature `0x0005`, and `Repository/RepositoryLifecycle` lists it at format 5.
- The writer: `Repository.Index/IndexPublisher`'s `PublishBundledCheckpointAsync` and `Repository.Index/IndexCheckpointer`, which carries every uncovered delta that opens once 64 have gathered, in as many checkpoints as the metadata limit needs. `Agent/BackupRunner` runs it after a format-5 set's committed backup, inside the run, so a direct-ship set's checkpoint fans out with its deltas.
- The readers: `Repository.Index/IndexLoader` opens every carried copy as it would the object at the delta's key, fetches only the listed deltas no verified copy carries, and acts on no watermark and no entry of a checkpoint's own. `Repository.Catalogue/Catalogue` ranks no checkpoint entry either.
- The tests:
  - `Repository.Tests/Index/CheckpointBundleTests`: key 10 round-trips and is signed; mismatched counts and mixed forms are refused; a load through a checkpoint reaches the same index as one through the deltas and fetches none of them; a partial bundle fetches only the rest; a damaged copy is a corrupt record and the delta is read from its key; a delta the listing no longer shows is still applied; two checkpoints carrying one delta apply it once; a watermark hides nothing; an earlier-form entry is ranked nowhere; and a catalogue rebuilt through a checkpoint locates what the deltas did
  - `Repository.Tests/Index/IndexCheckpointerTests`: nothing below the threshold; at it, every uncovered delta carried and none fetched after; a second run carries only what the first did not; a delta that does not open is never carried; several checkpoints when one would pass the budget; a delta too large to carry is not counted; and the defaults
  - `Repository.Tests/Format/RepositoryDescriptorCodecTests`, `Repository.Tests/EndToEnd/RepositoryLifecycleTests`, `Domain.Tests/FormatVersionsTests`: a format-5 descriptor lists all three features, either mismatch is a violation, format 6 is refused naming 2 to 5, a repository is born at 5, and format 4 is still creatable
  - `Repository.FuzzTests/RoundTripPropertyTests`, `Repository.FuzzTests/FuzzCorpus`: the carrying form round-trips byte for byte, and the decoder is fuzzed from a seed of it
  - `Hosts.Tests/IndexCheckpointServiceTests`, `Hosts.Tests/CreatedFormatTests`, `Hosts.Tests/FormatUpgradeTests`: a format-5 direct-ship set checkpoints after enough backups, in its own plane and at its destination, and a load at either fetches no delta; a format-4 set never checkpoints; a set created today is format 5; and an upgrade lands on the latest
  - `Repository.ConformanceTests/FixtureRepositoryV5Tests`: the committed `fixture-repository-v5` declares format 5 and its three features, carries its one delta in a checkpoint that a load reads instead, loads the same index with the delta gone from its key, and restores byte for byte

---

## Context

Every full load of the index reads every delta a repository has ever published. A load happens at archive open, at a catalogue rebuild, at a rollback heal, at adoption, and wherever a restore source is opened from a destination, which every recovery drill does. Each delta is one object, so the cost grows with the number of backups and compaction passes, and at a remote destination it is paid in round trips.

A checkpoint was meant to bound it. [ADR-0008 Amendment 1](0008-index-generations-and-checkpoints.md#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first) found that the specified form could not be published safely and set six constraints any checkpoint must meet first:

1. Its entries must rank where the entries they stand for ranked.
2. Every entry must keep its writer and sequence.
3. A watermark must stand only where every number below it is accounted for.
4. It must be readable at the size it is.
5. The blob digests and Merkle roots must survive it.
6. Retiring a delta must survive the copies.

The specified form fails the first five by construction. It re-encodes the entries it subsumes as its own, so they rank at the checkpoint's generation, writer and watermark, and lose their writer, sequence, digests and roots. It hides every delta at or below a watermark, whether or not its builder's listing saw it. It has no bound below the metadata limit and relies on shards for a split no reader honours.

## Decision

### 1 A checkpoint carries the deltas it subsumes, verbatim

A format-5 checkpoint carries, at key 10 (`subsumed_deltas`), each delta it subsumes as its stored object: the sealed bytes exactly as they sit at the delta's key, framing, nonce and tag included ([07 §5](../../specifications/repository-format/07-index.md#5-checkpoint)). Key 10 is parallel to key 2, so each copy sits beside its identifier.

- **Nothing of its own.** A checkpoint that carries deltas carries no entries, shards, shard hashes or watermarks. The codec refuses the two forms mixed, whether writing or reading.
- **Signed.** Key 10 sorts after the signature, key 9, and is signed all the same: the signing form omits key 9 alone. A store that swapped a copy would break the checkpoint's signature as well as the copy's own.
- **Never empty.** A checkpoint carrying no deltas omits the key. An empty array would be a second encoding of the same fact.
- **Never below format 5.** A reader that predates the key refuses the checkpoint as an unknown key and reports it as damage, so a repository such a reader is entitled to read never holds one.

Verbatim is the choice that meets constraints 1, 2 and 5 at once, and without a rule of its own. A carried delta is the delta. Its entries rank at its own index generation, writer and sequence; its covered blobs keep their signed digests and roots; and the rollback witness reads its sequence. Nothing about a delta is restated, so nothing can be restated wrongly.

### 2 A load reads a carried delta from the checkpoint

The loader reads every checkpoint at every generation, as before ([07 §6](../../specifications/repository-format/07-index.md#6-concurrent-checkpoints)). For each delta a checkpoint carries, it opens the copy exactly as it would the object at the delta's key: framing, object type, authentication, identifier, decoding and signature. A copy that opens is applied. Then it lists the deltas and fetches only those no verified copy carries.

- **A copy that does not open is reported and set aside.** The finding is the one the object at the key would earn, and the delta is fetched from its key instead. A damaged copy costs one fetch and never a delta.
- **A carried delta is applied whether or not the listing shows it.** The copy is the delta.
- **Each delta once.** Two checkpoints carrying one delta, or a checkpoint and the listing, apply it once, keyed by its identifier.
- **Each delta keeps its identifier.** Nothing in a delta names it; a load takes the identifier from the delta's key, or from key 2 beside its copy.

### 3 A checkpoint's own entries and watermarks are not acted on

The earlier form's semantics go, because they are what made it unsafe.
- **A watermark hides nothing.** The loader applied no delta at or below the highest watermark any checkpoint gave its writer, and began its gap accounting above it. A builder whose listing missed a delta hid it for good (constraint 3). The loader now skips only the deltas a verified copy carries, and accounts for every number from 1.
- **An entry a checkpoint carries for itself ranks nowhere.** It ranked at the checkpoint's generation, writer and watermark, and outranked or underranked deltas by accident of who built it (constraint 1). Neither the loader nor the catalogue applies one.

Nothing ever published either: the product never called the earlier form's publisher. The codec still decodes it, so a checkpoint of that form is read and reported as applied, and contributes nothing.

### 4 A writer checkpoints what it can carry, once 64 deltas have gathered

`IndexCheckpointer` builds checkpoints:
1. It reads the checkpoints it has not read before and notes which deltas they carry.
2. It lists the deltas and counts those none carries that would fit a checkpoint at all.
3. Below the threshold it stops, having read nothing but the two listings and any new checkpoints.
4. At it, it fetches each uncovered delta and keeps those that open and verify. A delta that does not open is never carried, so a checkpoint never holds what no reader could read.
5. It orders them by writer and sequence and publishes them in as many checkpoints as the budget needs.

- **The threshold is 64.** A checkpoint then replaces at least 64 fetches with one, and a set backed up hourly checkpoints a few times a week.
- **The budget is the metadata limit less a 64 KiB margin,** the margin the delta budget leaves for a delta's header. A checkpoint is a standalone record and the framing refuses one over the limit at read (constraint 4). A delta larger than the budget is never carried and never counted.
- **A checkpoint draws a sequence number** from the writer's space, accounted for by its own cleartext counter, as before ([ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md) §Decision 7). One whose publication fails leaves the number outstanding, and the next publication voids it.
- **What it has read is a cache.** The service keeps one per set, so a backup reads only the checkpoints published since the last. A delta the cache wrongly counts as carried is merely not checkpointed, and a load reads it from its key.

### 5 After a committed backup, inside the run

The service checkpoints a format-5 set after its backup commits, inside the run. A direct-ship set's checkpoint therefore reaches its destinations through the same fan-out its deltas did, and a staging set's through the next sync, as every index object does. It is best effort: a checkpoint changes how many objects a load reads and nothing it reaches, so one that fails is logged and the backup still ran. `ServiceOptions.CheckpointThresholdOverride` lowers the threshold for a test.

### 6 Format 5 names itself, and the product creates at it

A format-5 descriptor lists `0x0005` (`delta-checkpoints`) beside `0x0003` and `0x0004`. An earlier descriptor never lists it, and either mismatch is a format violation ([01 §3.2](../../specifications/repository-format/01-object-layout.md#32-body)). So a reader that predates format 5 refuses a repository created at it by name, at open.

Format 5 changes the checkpoint and nothing else. Its deltas are format 4's and its blobs and records format 3's, stamped 3 ([00 §5](../../specifications/repository-format/00-conventions.md#5-versioning-and-feature-negotiation)). `FormatLimits.FormatVersion` is 5, so a repository is born at 5, and formats 2 to 4 remain creatable on request and readable for ever. A set below format 5 is upgraded on request by [ADR-0066](0066-the-format-upgrade-record.md)'s record, to the latest, and checkpoints from its next backup.

## The six constraints

| | Constraint | How format 5 meets it |
|---|---|---|
| 1 | Rank | A carried delta's entries rank at its own index generation, writer and sequence. No checkpoint entry is ranked |
| 2 | Writer and sequence | Kept: the copy is the delta, and the catalogue takes each through `ApplyDelta` as it would the delta at its key |
| 3 | Watermarks | No watermark is acted on. The loader skips only the deltas a verified copy carries, and accounts for every number from 1 |
| 4 | Size | Each checkpoint is held under the metadata limit; a delta too large for one stays at its key. No reader depends on shards |
| 5 | Digests and roots | Kept: they are signed inside the delta, and the delta is carried whole |
| 6 | Retirement | **Not met.** Nothing retires a delta. It is owed, under FR-MAN-017 |

## Consequences

**Positive**
- **A load reads one object where it read up to 64.** At a destination, where each object is a round trip, that is the difference that matters, and every drill opens a restore source there.
- **Nothing a load reaches changes.** The same entries rank the same way, with the same digests and roots, whether a delta is read from its key or from a checkpoint.
- **A watermark can no longer hide a delta,** and the earlier form's ranking defect is gone with its entries.

**Negative**
- **Every delta is stored twice.** Nothing retires a delta, so a checkpoint adds its deltas' bytes again. The index roughly doubles in bytes while halving nothing but object count. Retirement is what pays this back.
- **A load still lists every delta.** It fetches only the uncovered ones, but the listing grows with the history, as before.
- **The first backup after a service start reads every checkpoint,** to learn what they carry. Later backups read only new ones.
- **An older reader of an upgraded repository reports each checkpoint as damage.** The descriptor of a repository upgraded to 5 still says what it was created at, so an older build opens it, refuses each checkpoint's key 10, and reports an index object it cannot read. It still reads every delta at its key, so it loses nothing but the speed. That is ADR-0066's stated price, as at format 4. A repository created at 5 is refused at the door.

## Alternatives considered

- **Re-encode the entries, keeping each one's rank.** A checkpoint could carry each entry with its index generation, writer and sequence, and each covered blob with its digest and root. That restates every field of a delta in a second schema, which every reader must rank and every writer must reproduce exactly. A copy restates nothing.
- **Make the watermark safe.** A builder could account for every number below a watermark before placing it. Journal records and blob counters share the space, and a builder that misjudged one would hide a delta for good. Skipping only what a copy carries cannot hide anything.
- **Checkpoint per shard.** Smaller checkpoints, loaded by shard. No reader resolves by shard today, and the per-shard watermarks constraint 4 would need are watermarks.
- **Skip the copies: name the deltas, and let a load skip fetching them.** A checkpoint that only named its deltas would save no fetch, because the loader needs the deltas' contents.

## What this record does not do

- **Retire a delta.** Every delta stays at its key, at staging and at every destination. Retirement must survive the copies (constraint 6), and is owed under FR-MAN-017.
- **Stop listing every delta.** A load lists the delta prefix whole, as before.
- **Refuse to write above the newest format this build knows.** As at format 4, an upgrade record naming a version above 5 sets the effective version above it, and this build would still write format 5's objects. That is owed.

## Status history

| Date | Status | Note |
|---|---|---|
| 2026-10 | Accepted; built | Key 10 is written by every format-5 checkpointer and read by every reader; a load reads each carried delta from its checkpoint and fetches only the rest; no watermark and no checkpoint entry is acted on; the service checkpoints a format-5 set after a committed backup once 64 deltas are uncovered; a repository is born at 5 and lists `0x0005`. Recorded in [ADR-0008 Amendment 2](0008-index-generations-and-checkpoints.md#amendment-2-2026-10--format-5-meets-five-of-the-six) |

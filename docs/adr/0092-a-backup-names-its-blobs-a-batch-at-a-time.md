# ADR-0092 — A backup names its blobs a batch at a time

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-008 (met); keeps FR-GC-003 and NFR-REL-001
**Related:** [ADR-0090](0090-a-backups-hints-are-one-pack.md) (the measurement that named the blob covers as owed), [ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md) §Decision 7 (amended: a fifth way a number is accounted for), [ADR-0009](0009-garbage-collection-safety.md) (the collector an intent protects blobs from), [ADR-0016](0016-blob-identifier-formation.md) (why a blob's id can be named before the blob exists), [ADR-0029](0029-pipeline-and-service-concurrency.md) §2 and §4 (concurrent uploads, and a cancelled run's numbers discharged by the next), [ADR-0067](0067-the-keyless-compactor.md) (the compactor), [specification 08 §3–§5](../../specifications/repository-format/08-journal.md#3-write-intent)

**Built:**
- The schedule: `Repository/BlobCounterReservation` — eight numbers in the write intent, then batches that double up to 64.
- The scope: `Repository/ReservingIntentScope` reserves each batch from the writer's sequence, numbers every blob from the batches, names a batch in one extension before the first blob numbered from it is put, and settles every named number once the Completed retirement is durable. `Repository.Index/WriterSequence` allocates a batch and accounts for one with a single write of its state.
- The publications: `Repository/SnapshotPublication` and `Repository/PublicationOrchestrator` open their intent through the scope, for a staging run and a direct-ship run alike, and settle after retiring it. `Repository/CompactionPass` names every blob a pass sealed in one extension.
- The tests:
  - `Repository.Tests/IntentReservationTests` — a small backup's intent names its blobs and no extension is written; a larger backup's extensions follow the doubling, each durable before the blobs it names; a completed backup owes nothing it reserved, so the next writes no void delta; a backup killed before retiring keeps every uploaded blob covered and owes only what it reserved and did not use, which the next run voids; a reader that consults the journal accounts for every number a completed backup took, and without the fifth case exactly the unused ones are gaps; the schedule itself
  - `Repository.Tests/UploadBudgetTests` — NFR-PERF-008 in total: a backup within the first batch writes two journal objects, its intent and its retirement, and every per-GiB figure is within 20
  - `InterruptionTests/CompactionInterruptionTests` — a pass that produces several blobs names them in one extension
  - `InterruptionTests/StorePutSweepTests`, `InterruptionTests/TreeSnapshotInterruptionTests` — the store dies at every put of the shorter order, and nothing durable is collectable
  - `InterruptionTests/ConcurrentUploadTests` — with uploads in flight together, the record naming each blob is durable before it

---

## Context

[ADR-0090](0090-a-backups-hints-are-one-pack.md) measured NFR-PERF-008's upload half and found two terms over the 20 requests per GiB it allows. One was a request per file, which it removed. The other it named and left: **the blob covers**.

A writer must not upload a blob before an unretired intent naming it is durable (08 §3.1). The engine wrote its intent naming nothing, then, before each blob's put, an extension naming that blob alone. So a blob cost two requests. At the object-store target of 128 MiB that is 8 data blobs a GiB and 8 extensions beside them, and with the metadata blobs' covers a backup came to 23–27 requests per GiB.

The extension per blob was the obvious shape because a capture does not know how many blobs it will write. Dedup and compression decide that as it goes, and 08 §4 says as much: "a job's blob set is rarely known up front". But a blob's identity does not depend on the blob. It is the writer's number, `writer_id[0..8] ‖ u64(counter)` ([ADR-0016](0016-blob-identifier-formation.md)), and the writer can allocate numbers before it has anything to put under them. 08 §3.1 already relies on that: an intent names blobs that do not exist yet.

What stood in the way was accounting. Every number in the writer's sequence must be accounted for, or a reader sees a gap ([07 §4](../../specifications/repository-format/07-index.md#4-sequence-gaps-and-void-deltas)). ADR-0022 §Decision 7 lists four ways, and the one for a blob counter needs the blob: a number is accounted for by a durable blob that embeds it and that an intent names. A number named in advance and never used embeds in no blob. Under those four cases it would be owed a void delta by every next run, one delta each: the same requests again, moved to the next backup.

## Decision

1. **The intent names the first batch.** A publication reserves eight numbers from the writer's sequence before it writes its write intent, and the intent's `intended_blob_ids` names the blobs those numbers make. Every blob the publication numbers comes from a reserved batch.

2. **Each later batch is named by one extension.** When a batch runs out, the next is reserved, twice the size of the last up to 64: 8, then 16, 32, 64, 64 and so on. One extension names it, durable before the first blob numbered from it is put (08 §3.1, §4). Uploads run concurrently ([ADR-0029](0029-pipeline-and-service-concurrency.md) §2), so a blob whose batch is not yet named waits for that extension, and batches are named in the order they were reserved. A blob the publication did not number, a spool an earlier run left and this one resumed (05 §6.3), is named by an extension of its own, as every blob was before.

3. **A Completed retirement accounts for what its intent named.** ADR-0022 §Decision 7 gains a fifth case: a number is accounted for when an intent or one of its extensions named a blob that embeds it and that intent's retirement with outcome Completed is durable, whether or not the blob exists. Completed says the work the intent covered is reachable (08 §5), so a named number with no blob is one the writer never used, not one it lost. The writer marks the whole named set accounted for once the retirement is acknowledged. A blob uploaded under a live intent is still accounted for the moment its put is acknowledged (case 4), so a run that dies or is cancelled owes only what it reserved and did not upload: the blobs it had in flight, as before, and the rest of its current batch, at most 63 numbers. The next publication voids those as it voids any leftover (07 §4; ADR-0029 §4).

4. **A compaction pass names its output in one extension.** The compactor seals every blob before it puts any ([ADR-0067](0067-the-keyless-compactor.md)), so a pass knows its whole output before the first upload. One extension names all of it, durable before the first put. Its intent still names nothing: the compactor numbers its blobs as it seals them, so there is nothing to reserve.

5. **Measured.** `Repository.Tests/UploadBudgetTests` builds the per-GiB figure from the terms, as ADR-0090 recorded it, at the object-store blob profile (128 MiB):

   | Backup | Requests per GiB | Blob covers | Everything else |
   |---|---|---|---|
   | First backup, ~490 KB a file | 15 | 1 | 14 |
   | First backup, 16 KiB a file | 16 | 1 | 15 |
   | Incremental, ~490 KB a file | 17 | 1 | 16 |
   | Incremental, 16 KiB a file | 18 | 1 | 17 |

   A GiB's 9 or 10 blobs are named by the intent and one extension. The suite now holds the whole total to 20, covers included.

## Consequences

- **NFR-PERF-008 is met.** The covers were 9–10 requests per GiB and are now one. A backup of up to eight blobs writes no extension at all: its intent, which it had to write anyway, names them. The owner's 4.48 GB first backup, at 128 MiB blobs, is about 40 blobs: two extensions where there were about 40.
- **A backup spends more numbers than it uses.** One that writes two blobs takes eight. The space is 64 bits wide, and a number costs nothing until it is owed, so this costs nothing that matters. What it changes is what a reader sees: a gap that a completed intent named is accounted for by the journal, as a blob counter already was. A reader that does not consult the journal treats such a gap as it treats any other it cannot account for, unresolved within its patience (07 §4), and the loaders in the product pass no journal-backed check, as before.
- **A run that dies owes more void deltas than before.** At most the unused rest of the batch it was in, 63 at the most, on top of the blobs in flight. That is a crash's or a cancellation's cost, paid once by the next publication. A backup that completes owes none.
- **The order 08 §3.1 asks for is unchanged.** Every blob is named by a durable record before it is put. A collector surveying at any moment keeps everything a live intent names (08 §8), and a named number with no blob costs it nothing.
- **No format change.** `intended_blob_ids` and `additional_blob_ids` always named blobs that did not exist yet. An older reader reads these intents as it read the others.
- **A lost state directory still recovers.** A batch is reserved before the record naming it is published, so that record's own number is above the batch. The highest number the repository attests is therefore above every number in a blob that reached the store, and a writer that adopts it (NFR-SEC-005) cannot hand one out again.

## Alternatives considered

- **Name every blob in the intent, counted before the capture.** The counting pass knows bytes, not blobs. Dedup and compression decide the blob count as the capture runs, so an estimate either reserves numbers a backup that dies then owes, or falls short and needs extensions anyway.
- **A fixed batch.** Sixty-four at once leaves a small backup that dies owing up to 63 voids. Eight at once costs a large backup an extension a GiB. Doubling from eight keeps a small backup's reservation small and a large backup's extensions few.
- **Void the unused numbers when the backup completes.** Each void is a delta, so this moves the requests from before the blobs to after them.
- **List the unused numbers in the retirement.** It would be a new key in 08 §5's payload, so a format change, to say what the Completed outcome already says.
- **An intent per batch.** Each would need its own retirement: twice the records an extension costs.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | The blob covers ADR-0090 left owed: a publication names its blob numbers a batch at a time and a Completed retirement accounts for the ones it never used (ADR-0022 §Decision 7 amended); built with it: the schedule (`Repository/BlobCounterReservation`), the scope (`Repository/ReservingIntentScope`), batch allocation and accounting (`Repository.Index/WriterSequence`), the publications (`Repository/SnapshotPublication`, `Repository/PublicationOrchestrator`) and compaction's one extension (`Repository/CompactionPass`), measured by `Repository.Tests/UploadBudgetTests` |

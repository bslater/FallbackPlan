# ADR-0088 — A backup's percentage is what it has backed up

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-SVC-006
**Related:** [ADR-0048](0048-determinate-backup-progress.md) (the counted plan; this replaces what its meter divides and what its estimate runs on), [ADR-0046](0046-direct-to-destination-publication.md) (a blob's put returns when every destination of the run holds it), [ADR-0029 §2](0029-pipeline-and-service-concurrency.md) (uploads leave the archive loop), [ADR-0034](0034-hub-and-spoke-destinations.md) (catch-up to a destination outside a run), [specification 06 §11](../../specifications/repository-format/06-manifests.md#11-source-identity) (source-identity hints), [architecture 10 §3](../architecture/10-observability.md#3-job-state-machine) (job states)

**Built:**
- The measure: `Repository/BackedUpTally` counts the plan's bytes in the order they were archived and releases them as blobs are acknowledged. `Repository/ArchiveSession` tells it what each segment did and when each blob opens, seals and lands. `Repository/SnapshotPublication` gives each planned file its budget, counts an unchanged or renamed file at once, reports the tally with every report and whenever an acknowledgement moves it, and counts the hints while it writes them. `Repository/ManifestBuilder` writes the hints up to sixteen at once.
- The wire: `Domain/JobProgress` gains the bytes backed up and the hints' count (contract 1.54, `Api/ContractVersion`). The job row carries the bytes backed up (`Api/Results`, `Application/JobStateStore`), recorded at every terminal transition by `Agent/BackupRunner`, and the CLI's `jobs <id>` prints it (`Cli/CliApplication`).
- The console: the set card, its glance line, a direct-ship destination's ring and the jobs card show one figure, what is backed up over the plan's bytes, held at 99 while the run is live, with the finishing step and its count beside it.
- The tests:
  - `Repository.Tests/BackedUpProgressTests` — against a store that holds each data blob's put until released: nothing is backed up while every file is read but no blob acknowledged; releasing the first blob counts what it carried and nothing past it; an unchanged file counts before any upload; the hints' count appears once the run is publishing and its last report has them all; the hints go out several at a time, never more than sixteen, and all before the snapshot record
  - `Web.DomTests/BackupProgressDomTests` — the jobs card and the overview's live row and glance line show bytes backed up rather than files read, and hold at 99 while the run finishes, naming the step and its count
  - `Web.Tests/ConsoleProgressScriptTests` — the shared meter divides what is backed up by the plan's bytes, holds below 100, and falls back to files for a service without the measure; the estimate runs on the meter's measure
  - `Api.Tests/ContractAdditiveFieldsTests`, `Api.Tests/ConfigurationContractTests` — the fields' wire names, null from a pre-1.54 service, and the version pinned at 1.54
  - `Hosts.Tests/JobsVerbServiceTests` — a committed run's row says it backed up its whole plan, and the CLI's report prints it

---

## Context

The owner watched a first backup of 9,190 files, 4.48 GB, to a local folder. The console said 100% at about 01:08:40. The service went on writing to the destination until 01:12:24, published at 01:12:37, and finished its checks at 01:13:04. The run took about nine and a half minutes, and its last four and a half were spent at 100%.

The meter was [ADR-0048](0048-determinate-backup-progress.md)'s: files handled divided by the counted plan's files. A file is handled when the walk has read it and packed its segments. Two kinds of work were still to come.

- **Uploads still queued.** Since [ADR-0029 §2](0029-pipeline-and-service-concurrency.md), a sealed blob is handed to upload workers and the walk goes on. A queue of concurrency + 1 sealed blobs, the workers' own and the open blob can all be read but unstored: about 384 MiB at the default 64 MiB blobs and concurrency of two. The last of them is sealed only when the walk ends.
- **The publication's own writes.** After the last content byte, a publication writes its manifests, an index delta, one source-identity hint per new file version ([specification 06 §11](../../specifications/repository-format/06-manifests.md#11-source-identity)), the snapshot record and the journal entry, then updates the local catalogue. The hints were written one after another, each a store round trip. A direct-ship set writes each to its local metadata store and then to every destination. The log showed about 24 ms each: 9,190 of them took three minutes and forty seconds.

The owner's direction was that the percentage must be what is backed up and stored at every destination, and that the bar is the job's progress. Asked how the bar should behave while the run finishes, the owner chose to hold it at 99% beside the step and its count. Giving the finishing work its own share of the bar was the other option, and was not taken.

One fact makes "stored at every destination" measurable at a single point. In a direct-ship run, the sink's put of a blob returns only once every destination the run writes to holds it ([ADR-0046](0046-direct-to-destination-publication.md) §1). Its first copy lands at the highest-priority destination, and the rest land together. A destination outside the run is brought up to date afterwards by its own catch-up job ([ADR-0034](0034-hub-and-spoke-destinations.md)). That covers a peer, a destination with no baseline yet, and one dropped mid-run.

## Decision

1. **What "backed up" means.** A run's bytes backed up are the plan's logical bytes whose content the store has acknowledged, plus those whose content the store already held. For a direct-ship set, acknowledged means held at every destination the run writes to.
   - **Unchanged and renamed files** count the moment the run decides to reuse them.
   - **Captured content** counts in the order it was archived, and only once the blob holding it, and every blob opened before that one, has been acknowledged. Blobs upload several at a time and land in any order, so the count is a watermark: the plan bytes archived before the oldest blob still unacknowledged. It never moves back.
   - **Each planned file** contributes exactly its planned length. Its segments count up to that length and no further, so a file read again because it changed under the reader, or archived beside its alternate streams, never counts twice. Whatever its segments did not cover counts where the file ended: a sparse file's holes, or a file that failed partway or shrank.
   - **A segment reused from another this run claimed** but has not yet appended counts where that claimant is appended, never ahead of the blob that will hold it.
2. **The wire, additively.** Contract 1.54 adds three fields.
   - **`bytes_backed_up`** rides every report once the run's archive session exists. It is also reported on each acknowledgement that moves it, rather than coalesced, because at 64 MiB blobs that is a few times a minute.
   - **`hints_total` and `hints_written`** appear once the run starts writing its hints, and the last hint is always reported.
   - **The job row's `bytes_backed_up`** is recorded at every terminal transition: the whole plan for a committed run, and how far a failed or cancelled one got.

   A pre-1.54 service sends none of them.
3. **What a client shows.**
   - **One figure** for the bar and the percentage: bytes backed up over the plan's bytes, rounded down. A live job holds at 99, because a run backs up its whole plan before its snapshot is published, and 100% is a published snapshot.
   - **While the run is publishing,** the words beside it say "Finishing", with the hints' count once there is one.
   - **The estimate** runs on the same measure, bytes backed up per second against what remains, and is left out while the run finishes.
   - **Where it shows:** the console's set card, its glance line, a direct-ship destination's ring and the jobs card. The CLI's `jobs <id>` prints a run's figure.
   - **A pre-1.54 service** is divided by files against the plan, as before, and also held at 99.
4. **The hints are written several at a time.** Up to `HintWritesInFlight`, sixteen, are at the store at once, and all of them land before the snapshot record, as specification 06 §11 requires. Each stays advisory: a store fault on one is passed over, as it was.

## Consequences

**Positive**
- The percentage is what is stored at the destination, and 100% means published.
- An incremental of mostly unchanged files climbs as fast as it reuses them, so the estimate no longer needs files as a stand-in to avoid promising hours.
- The finishing work is named and counted rather than hidden behind a full bar, and it is shorter: the hints' round trips overlap instead of queueing.
- A failed or cancelled run's row now says how much of its plan reached the store.

**Negative**
- **The meter moves in steps and lags the reader.** It moves in blob-sized steps, 64 MiB by default and about 1.5% of a tree like the owner's. It lags the reader by up to the upload queue, so a first backup's bar is behind where the old one was at the same moment. That is the point of it, but it reads as slower.
- **Sixteen hint writes at once** make a short burst of small writes on the destination disk at the end of a run.
- **The last 1% is not proportional to time.** It covers the finishing work, however long that takes. The count beside it, not the bar, says how far through it is.
- **Not every destination is in the figure.** A peer, a destination without a baseline and one dropped mid-run are left out. Their catch-up is a separate job, and its live progress is owed: the rest of the owner's "every destination".

**Neutral**
- Pre-1.54 clients ignore the fields.
- The files counts stay on the wire and in the job row, and the console still shows them as files processed.
- The hub, the coalescing interval and the replay are unchanged.

## Alternatives considered

- **Count files, as before, but only once stored.** A file is a bad unit for "how much is stored": a 4 GB file would count at once, at its end. Bytes are what being stored means.
- **Count a file only when every blob of it has landed.** This is as honest, but the meter would step by the largest file in flight. Counting in archive order per segment gives the same truth more smoothly.
- **Give the finishing work a share of the bar.** The owner was offered this and declined it. The percentage would then measure the job's storing work rather than bytes stored, and the share would be a guess.
- **Pack the hints into one object per snapshot.** Fewer objects, but [Q21](../open-questions.md#closed) chose one object per new file version on purpose. A later reader lists one source key's prefix to find a file's history, and a per-snapshot table pays for the whole tree every run.
- **A bar that is elapsed time over estimated total time.** That is honest about time but not about what is stored, which is what the owner asked the number to mean.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Written from the owner's direction after a first backup showed 100% for its last four and a half minutes |
| 2026-10 | Built | The measure (`Repository/BackedUpTally`, `Repository/ArchiveSession`, `Repository/SnapshotPublication`), the concurrent hints (`Repository/ManifestBuilder`), contract 1.54 (`Domain/JobProgress`, `Api/ContractVersion`, `Api/Results`, `Application/JobStateStore`), the job row and the CLI's report (`Agent/BackupRunner`, `Cli/CliApplication`), and the console's meters, pinned by `Repository.Tests/BackedUpProgressTests`, `Web.DomTests/BackupProgressDomTests`, `Web.Tests/ConsoleProgressScriptTests`, `Api.Tests/ContractAdditiveFieldsTests` and `Hosts.Tests/JobsVerbServiceTests` |

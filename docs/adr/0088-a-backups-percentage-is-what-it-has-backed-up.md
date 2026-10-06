# ADR-0088 — A backup's percentage is what it has backed up

**Status:** Accepted, amended (Amendment 1, 2026-10)
**Date:** 2026-10
**Requirements:** FR-SVC-006
**Related:** [ADR-0048](0048-determinate-backup-progress.md) (the counted plan; this replaces what its meter divides and what its estimate runs on), [ADR-0046](0046-direct-to-destination-publication.md) (a blob's put returns when every destination of the run holds it), [ADR-0029 §2](0029-pipeline-and-service-concurrency.md) (uploads leave the archive loop), [ADR-0034](0034-hub-and-spoke-destinations.md) (catch-up to a destination outside a run), [specification 06 §11](../../specifications/repository-format/06-manifests.md#11-source-identity) (source-identity hints), [architecture 10 §3](../architecture/10-observability.md#3-job-state-machine) (job states)

**Built:**
- The measure: `Repository/BackedUpTally` counts the plan's bytes in the order they were archived and releases them as blobs are acknowledged. `Repository/ArchiveSession` tells it what each segment did and when each blob opens, seals and lands. `Repository/SnapshotPublication` gives each planned file its budget, counts an unchanged or renamed file at once, reports the tally with every report and whenever an acknowledgement moves it, and counts the hints while it writes them. `Repository/ManifestBuilder` writes the hints up to sixteen at once.
- The wire: `Domain/JobProgress` gains the bytes backed up and the hints' count (contract 1.54, `Api/ContractVersion`). The job row carries the bytes backed up (`Api/Results`, `Application/JobStateStore`), recorded at every terminal transition by `Agent/BackupRunner`, and the CLI's `jobs <id>` prints it (`Cli/CliApplication`).
- The console: the set card, its glance line, a direct-ship destination's ring and the jobs card show one figure, what is backed up over the plan's bytes, held at 99 while the run is live, with the finishing step and its count beside it. Amendment 1 replaced that figure with two (below).
- Amendment 1, the job's stages: `Repository/BackedUpTally` also counts files, each once everything archived up to its end, in records, has been acknowledged; `Repository/SnapshotPublication` reports both measures together. `Domain/JobProgress` and the job row (`Api/Results`, `Application/JobStateStore`, `Agent/BackupRunner`) carry the count (contract 1.55, `Api/ContractVersion`), and the CLI's `jobs <id>` prints it (`Cli/CliApplication`).
- Amendment 1, what each destination holds: `Repository.Catalogue/Catalogue` works out a destination's files of a snapshot from its ledger watermark and lists a snapshot's files as the blobs they are read from. `Agent/DeliveredFiles` remembers the first until either input moves. `Agent/FileHoldingCounter` and `Agent/LiveHoldings` count a sync's files as they land, fed by `Replication/StoreToStoreCopier` and `Agent/ReplicationInitiator` through `Agent/FanOut`. `Agent/DestinationShipSink` names the destinations a live run writes to, and `Agent/ServiceCommandHandler` puts all of it on each status row.
- Amendment 1, the console: the job's bar is its three stages; each destination's circle and line are what it holds, a live run folded in; the set's summary carries its least complete destination's circle.
- The tests:
  - `Repository.Tests/BackedUpProgressTests` — against a store that holds each data blob's put until released: nothing is backed up while every file is read but no blob acknowledged; releasing the first blob counts what it carried and nothing past it; an unchanged file counts before any upload; the hints' count appears once the run is publishing and its last report has them all; the hints go out several at a time, never more than sixteen, and all before the snapshot record
  - `Web.DomTests/BackupProgressDomTests` — the jobs card and the overview's live row and glance line show bytes backed up rather than files read, and hold at 99 while the run finishes, naming the step and its count
  - `Web.Tests/ConsoleProgressScriptTests` — the shared meter divides what is backed up by the plan's bytes, holds below 100, and falls back to files for a service without the measure; the estimate runs on the meter's measure
  - `Api.Tests/ContractAdditiveFieldsTests`, `Api.Tests/ConfigurationContractTests` — the fields' wire names, null from a pre-1.54 service, and the version pinned at 1.54
  - `Hosts.Tests/JobsVerbServiceTests` — a committed run's row says it backed up its whole plan, and stored every file of it, and the CLI's report prints both
  - Amendment 1: `Repository.Tests/BackedUpProgressTests` also counts files — none until the store acknowledges them, an unchanged file at once, one that failed never. `Hosts.Tests/DestinationFilesHeldTests` — a direct-ship backup's destination holds the newest backup whole; one that missed a backup holds exactly the files it left alone; one never delivered to is not counted; a held run names the destination it writes to; a sync's count is the status's while it runs, and counts up from what the destination held as content lands. `Web.DomTests/BackupProgressDomTests` — the job's three stages, a circle at 100% through a run that changes nothing, dips only for files found changed, a first backup's circle that stops at 99%, the set's circle as its least complete destination's, and each circle at rest

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

   > **Amended 2026-10 (Amendment 1).** One figure became two. The bar is
   > the job's progress: three equal stages over its counted plan's files.
   > A destination's circle is what it holds of the newest backup, in files,
   > and the set's circle is its least complete destination's. What follows
   > describes the figure as first built.

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

  > **Amended 2026-10 (Amendment 1).** Every destination now has its own
  > circle, a catch-up sync's counted live, and the set's circle is the
  > least of them.

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

## Amendment 1 (2026-10) — a job's three stages, and what each destination holds

The owner saw the first figure and asked for two, kept apart. The circle is
"the % of files backed up across all destinations". A job's progress is "the
% complete across scanning for files, processing of files, and packing and
storing files across the available destinations in the current job". Two
screenshots made the difference plain. A first backup's destination ring read
100% while the set had never committed a snapshot. During a second run, a
destination that already held every file showed the run's 52%, and its line
repeated the job's counts word for word. Asked how a file should count towards
the circle, the owner chose that a file counts only once every destination
holds it. Asked how the three stages should weigh, they chose equal thirds by
files. Then they gave the destination's line its shape: what it holds, against
the files scanned and what it lacks, then a dash and what the job is doing.

1. **A job's bar is three equal stages over its counted plan's files.** It
   counts scanned, processed and stored, over three times the plan's files:
   (scanned + processed + stored) / (3 × files).
   - **Scanned** is the plan once the counting walk ends. Until then there is
     no total, so the bar is indeterminate and the words count what the scan
     has found.
   - **Processed** is files done or failed.
   - **Stored** is a new count, files backed up: those whose content the store
     has acknowledged at every destination the run writes to, and those it
     already held.

   The tally places each published file at its end, measured in records
   appended rather than bytes, so content the plan does not count still waits
   for its blob. An unchanged or renamed file counts at once. A file that
   failed never counts, because none of it reached the store. The bar holds at
   99 until the snapshot is published, as before. A 1.54 service counts no
   files stored, so its bytes backed up stand in for that stage; an older
   service's files processed do. The estimate still runs on bytes, because the
   storing stage ends the job and bytes predict its length better than files of
   any size.

2. **A destination's circle is the share of the set's newest backup's files it
   holds.** A file is held when all its content is there. The figure is
   computed three ways:
   - **At rest**, the service works it out from two facts it already keeps.
     The ledger's watermark says the destination holds everything published
     at or before it. The catalogue says which content each file needs. One
     detail decides the answer. The watermark is a snapshot record's counter,
     which is its run's write intent, and the run publishes its content later,
     in its index delta. So the watermark is first moved to the delta that
     published the next snapshot record after it. Measured against the raw
     watermark, a destination would always seem to lack the run it had just
     received. Nothing is listed to answer a status poll, and the answer is
     remembered until the newest snapshot or the watermark moves.
   - **While a sync runs**, its own count takes over. The local copy lists the
     destination's blobs once, then counts each key the copier holds. A peer
     push starts from the inventory the peer declares, then counts each object
     it sends. Each file's blobs are its content's winning locations, the ones
     a restore reads from.
   - **While a backup runs**, the console folds the run in. The status says
     which destinations the run writes to. Of the files the run has scanned, a
     destination holds three groups:
     - those the run has stored there, where it writes there;
     - those the run found unchanged, which it already held at the share it
       held of the last backup;
     - of the files not reached yet, that same share.

     A run that changes nothing therefore leaves a circle at 100% while the
     job's bar is part way through, and a first backup's circle climbs file by
     file.

   A circle reads 100 only when the destination holds the newest backup whole,
   including the snapshot record a copy sends last. It never reads 100 while a
   run has stored something there that is not yet published. A destination
   nothing was ever delivered to is not counted, which is not the same as
   holding none.

3. **The set's circle is its least complete destination's.** A destination
   that has not been counted leaves the set's circle uncounted too. This is
   exactly the share every destination holds when what they lack nests, which
   it does wherever destinations are filled from the same history in the same
   order. When two destinations lack different files, the true share is lower,
   and the circle overstates it.

4. **A destination's line says its circle in words, then what is moving files
   there.** For example: "92% of 400 files backed up · 30 missing — backing
   up", or "— syncs once this backup is published" for a destination the run
   does not write to, or "— syncing". It never repeats the job, which the
   set's live row shows in full. At rest it says "holds all 400 files" or
   "370 of 400 files", and when it last synced. The bytes a destination holds
   of its own keep-set, the old completion figure, stay as a detail line.

5. **The wire, additively (contract 1.55).**
   - `JobProgress` and the job row gain `files_backed_up`.
   - Each destination row gains `files_held` and `files_total`, which are null
     when not counted; `holds_newest`; `in_run`; and `syncing`.

   A pre-1.55 service sends none of them.

**Consequences.**
- **Positive:** the circle answers how much of the backup is safe at each
  destination, and the bar answers how far the job has got. Neither borrows
  the other's number.
- **Negative:** a circle drops at the start of an incremental run, to what the
  run has confirmed so far plus the unreached files at the share held before,
  and it can dip slightly at publication when the newest backup becomes the
  measure for a destination the run did not reach. The at-rest figure for a
  destination whose last sync failed part-way is the one its watermark
  supports, which can be lower than what it actually holds, until the next
  sync counts it.

**Alternatives considered.**
- Each circle as the job's own progress, as before: the owner's screenshot is
  the reason not to.
- The set's circle as the exact intersection of every destination's files:
  that needs each destination's missing blobs kept somewhere, and in the
  usual case the least complete destination's figure is that intersection
  anyway.
- Listing a destination on each status poll: that costs a directory walk, or
  a peer round trip, every few seconds to redraw a circle.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Written from the owner's direction after a first backup showed 100% for its last four and a half minutes |
| 2026-10 | Built | The measure (`Repository/BackedUpTally`, `Repository/ArchiveSession`, `Repository/SnapshotPublication`), the concurrent hints (`Repository/ManifestBuilder`), contract 1.54 (`Domain/JobProgress`, `Api/ContractVersion`, `Api/Results`, `Application/JobStateStore`), the job row and the CLI's report (`Agent/BackupRunner`, `Cli/CliApplication`), and the console's meters, pinned by `Repository.Tests/BackedUpProgressTests`, `Web.DomTests/BackupProgressDomTests`, `Web.Tests/ConsoleProgressScriptTests`, `Api.Tests/ContractAdditiveFieldsTests` and `Hosts.Tests/JobsVerbServiceTests` |
| 2026-10 | Amended | Amendment 1, from the owner's direction: a job's bar is its three stages over its files, and each destination's circle is what it holds of the newest backup (contract 1.55; `Repository.Catalogue/Catalogue`, `Agent/FileHoldingCounter`, `Agent/LiveHoldings`, `Agent/DeliveredFiles`, `Agent/DestinationShipSink`), pinned by `Hosts.Tests/DestinationFilesHeldTests` and `Web.DomTests/BackupProgressDomTests` |

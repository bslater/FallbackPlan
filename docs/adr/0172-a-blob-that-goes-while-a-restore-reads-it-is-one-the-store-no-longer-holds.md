# ADR-0172 — A blob that goes while a restore reads it is one the store no longer holds

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-007 (amended: a blob that goes from the own store during the run is read around as not held), FR-RST-005 (amended: with no copy to read it from, the files it held fail and the run keeps its receipt; by Amendment 2, they fail as not held), FR-GC-003 (amended by Amendment 3: a collection pass holds its blobs while the service has a restore of the set under way; by Amendment 6, while a restore in direct mode of the set's repository is too; by Amendment 7, as the installation's state directory says, where the service runs on one of its own), FR-GC-010 (amended by Amendment 4: a destination keeps its blobs through a sync while the service has a restore of the set under way; by Amendment 5, a peer is instructed by no granted run while one is; by Amendment 6, both hold beside a restore in direct mode too)
**Related:** [ADR-0075](0075-a-restore-reads-around-damage.md) §5 (what a copy passed over is called), [ADR-0068](0068-the-catalogue-directed-restore-read.md) (the catalogue-directed read), [ADR-0009](0009-garbage-collection-safety.md) (the collector), [ADR-0034](0034-hub-and-spoke-destinations.md) §6 (the staging trim), the [prior-art review](../review/2026-08-prior-art-learnings.md)'s T-4

**Built:**
- The reader: `Repository/RepositoryReader` asks the store, where a read of a blob fails as damage fails, whether it still holds the blob. If not, it forgets where the blob is, and the read is a miss.
- The tests:
  - `Repository.Tests/RestoreBesideCollectionTests` — at formats 2 and 5, a blob that goes after the reader read from it is read from a copy that holds it and is no finding, or fails the record without throwing. So does one under a reader that loaded every footer. Through the executor, with the blob swept after its first, second or third read, a run with a copy completes byte for byte, and one without fails only what it could no longer read and keeps its receipt. The review's `Compaction_RunsWhileARestoreIsReading_NeverRemovesAnObjectTheRestoreNeeds`, at formats 3 and 5, reads the record from where it went.
  - `TestSupport/FaultInjectionStores` gains a store that deletes an object before the read after its first few.

---

## Context

The review's T-4 asked for a test that compaction run while a restore is reading never removes an object the restore needs. The review's case already held. A restore resolves each record through the live catalogue when it reads it ([ADR-0068](0068-the-catalogue-directed-restore-read.md)), and a compaction publishes the move into that catalogue before the blob it drained can go. So a record that moved is read from where it went.

What a reader keeps for the run is about blobs, not records:

- where each blob is, and how long it is;
- how it is framed;
- for a reader that opened footers, the blob's record table.

Two things remove a blob while a restore of the same set runs, and neither waits for one:

- the collector sweeping what an expired snapshot alone reached;
- the staging trim removing what every destination holds.

A restore takes no set gate, so retention runs beside it.

A record read from such a blob after it went asks the store for a range of an object that is not there. The blob reader answers that as a blob that will not frame, which is how damage answers. On the code before this record:

- **The run ended.** A format-2 set's read, and any read through a reader that loaded every footer, threw a `BlobFormatException`. That is a `FormatException`, and the reader's read-around and the executor's per-item containment both catch `IOException`. The whole restore ended with no receipt, though another copy held the blob.
- **A blob that went was filed as damage.** A later format reads a blob first at an assumed framing, and that path caught the failure. The reader then listed the blob as skipped for damage. Reading around, it named the own store's copy as damaged, a finding about a blob that was simply gone.

## Decision

### 1. A read that fails as damage asks the store

The blob reader cannot tell a blob that went from one that will not frame. The store can. So where a read of a blob fails as damage fails, the reader asks the store whether it still holds the blob. There are three such places:

- the read at a record's location;
- the footer it opens to fall back;
- the record table it already holds.

The question costs one metadata call, and only on a read that has already failed.

### 2. A blob the store no longer holds is forgotten, and the read is a miss

The reader forgets where the blob is and how long it is. Its framing and footer stay: another copy holds the same bytes at the same offsets, and a read-around finds the record there by them. The read becomes a miss:

- with another copy, the record is read from it, and the own store is passed over as not holding the blob (ADR-0075 §5), which is no finding;
- with none, the record fails as one no loaded blob carries, and the run goes on to the next item.

The next record that blob held asks where it is, finds nothing, and is a miss too.

### 3. A blob the store still holds is what it was

Damage stays damage. The blob reader is unchanged. Its other callers, the loader and verification among them, still take a blob that will not frame as damage, which for them it is.

## Consequences

- A restore never ends because a blob went under it. It reads the blob's records from another copy or fails the files that needed them, and its receipt says which.
- A staging trim during a restore costs nothing: every destination holds what the trim removed, so the run reads those records from a destination.
- A blob that went is never a damage finding against the store it went from.

## What this does not do

- **Hold the collector off a restore.** A snapshot that expires and is swept while it is being restored loses the files whose blobs went, with no copy to read them from: destinations converge to the same policy. They fail, named, and the receipt says so. The sweep's grace spans two retention applies and a publication between them, and a person runs an apply, so the window is a restore that outlives all three.

  > **Built 2026-10 for the set's own pass ([Amendment 3](#amendment-3-2026-10--a-retention-pass-holds-its-blobs-beside-a-restore-of-the-set)).** A retention pass, and a person's deletion, hold every blob while the service has a restore of the set under way, and say so. A sync's convergence at the destinations, and a restore run in direct mode, are not held for.

  > **2026-10 ([Amendment 4](#amendment-4-2026-10--a-sync-keeps-a-destinations-blobs-beside-a-restore-of-the-set)).** A sync's convergence is held too: a destination keeps its blobs while the service has a restore of the set under way. A restore in direct mode is still not held for.

  > **2026-10 ([Amendment 6](#amendment-6-2026-10--a-restore-in-direct-mode-says-it-is-under-way-where-the-service-looks)).** It is now. A restore in direct mode holds a marker of its repository where the service looks, and every hold reads it.
- **Say "not held" in the failure.** A record no loaded blob carries is reported as a format violation, as it was for a blob missing before the run began.

  > **Built 2026-10 ([Amendment 2](#amendment-2-2026-10--a-record-no-store-holds-fails-as-not-held)).** A record whose blob the store does not hold fails as not held, whether the blob went during the run or before it, and the receipt says so. A record nothing places at all is still a format violation.
- **Tell a blob that went from damage at another copy.** A blob that goes from a destination while the run reads around to it is still passed over there as damaged, so it lands on that destination's ledger as a finding.

  > **Built 2026-10 ([Amendment 1](#amendment-1-2026-10--at-another-copy-too)).** A copy is now asked as the own store is, and a blob that went from it is passed over there as not held.

## Alternatives considered

- **Hold every blob a restore in flight might read, as a backup in flight holds blobs (ADR-0009 Amendment 8 rule 3).** Rejected for now. A restore can run for hours over a slow peer, and holding the trim and the sweep for its length holds space a destination already holds a copy of. It would also not help a restore of the snapshot being expired, which a person chose to expire.
- **Make the blob reader answer a missing range as an I/O error.** Rejected. Every caller of the blob reader would then take a blob that went for a transient fault, and the loader would fail a load over a blob it could have skipped.
- **Re-resolve every record before reading it.** It already is. What a reader keeps is about the blob, and the blob is what went.

## Amendment 1 (2026-10) — at another copy too

A restore of a set's own archive reads around to the set's destinations, and a destination's own retention converges while the run reads. So a blob can go from the copy the run is reading as well as from its own store. At a copy it failed in the same two ways:

- **It was filed as damage.** The location read failed as the store's NotFound, and the footer opened to fall back would not read either. The copy was passed over as damaged, and through the service that finding goes on the destination's ledger, so a sync would set about repairing a replica that had only done what its policy said.
- **It ended the run.** Where the copy's footer was already held, from a record that failed its checks there earlier, the record read through it threw. The copy read catches only an I/O error.

**Decision.** Both places ask the copy's store whether it still holds the blob, as §1 has the own store asked:

- if it does not, the copy is passed over as not holding the blob, which is no finding, and the run goes on;
- a footer that would not open because its blob had gone is not kept, since it says nothing about the blob were it to come back;
- if the store still holds the blob, nothing changes.

**Built:** `Repository/RepositoryReader`; `Repository.Tests/RestoreBesideCollectionTests`, at formats 2 and 5:

- a copy's blob that goes after it served a record is not held there, and no copy is named damaged;
- one that goes after its footer was read is not held there, and nothing throws;
- through the executor, with the copy's blob swept after its first, second or third read, the run keeps its receipt, fails only what no copy could still read, and holds no damage against the copy.

## Amendment 2 (2026-10) — a record no store holds fails as not held

§2 left a record whose blob had gone, with no copy to read it from, failing as one no loaded blob carries. The reader reported that under the record read's format violation, the outcome a torn or forged record earns, so:

- the restore's receipt named each such file as failed for "FormatViolation", the words it gives a blob that will not frame;
- a blob gone before the run began, at a location the catalogue still named, failed in the same words.

The receipt then read as though the archive were damaged, when the blob had only gone, swept or trimmed.

**Decision.**

- **The record read gains "not held":** the store does not hold the blob the record is in. Nothing is damaged, and nothing was read.
- **The own store's read answers it wherever the reader knows the blob is gone:**
  - a location whose blob the store does not hold, which the metadata call the fallback already makes tells it, so the store is asked nothing more;
  - a blob forgotten once a read of it failed and the store no longer held it (§2).

  Its detail names the blob and the object.
- **Reading around.** The own store's "not held" is passed over as not holding the blob, as a forgotten blob already was (ADR-0075 §5). With no copy that holds the blob, the record fails as not held, and the receipt names the file as failed for "NotHeld".
- **A reader that loaded every footer.** Such a reader keeps a blob's record table after the blob goes, so the own store still had a place for the record. Reading around, it filed the own store as damaged, against §2. No caller in the product reads around with such a reader, since the service's restore asks a location source. The outcome now decides it, so the reader does not depend on that.
- **What stays a format violation:**
  - an object no location and no loaded blob places, which is not a blob that went;
  - a blob the store still holds that will not frame, which is damage and is still skipped as damage (§3).

Every other reader of the outcome compares it against success, or leaves what it does not name unread. The replica verifier neither proves nor fails a blob that is not held, as it did a format violation. No contract, format or schema change: a receipt's detail is text.

**Built:** `Repository.Packing/RecordReadResult`, `Repository/RepositoryReader`; `Repository.Tests/RestoreBesideCollectionTests`, at formats 2 and 5:

- a record whose blob went, with no copy, fails as not held, under a reader that asks a location source and under one that loaded every footer, and its detail says the store does not hold the blob;
- so does one whose blob went before the restore began;
- a reader that loaded every footer reads such a record from a copy, and passes the own store over as not held, where it filed it as damaged;
- the control: a blob the store still holds, cut short inside the record, fails as damage, not as not held, and is skipped as damage;
- through the executor, with a blob swept partway and no copy, every failed item's detail names "NotHeld".

## Amendment 3 (2026-10) — a retention pass holds its blobs beside a restore of the set

"What this does not do" left the collector free to run beside a restore. A restore reads the blobs of the snapshot it restores as it goes, and nothing in the store says it is reading them. A snapshot that expired, or that a person deleted, while a restore of it ran could have its blobs swept from under the run. The files they held then failed as not held (Amendment 2), with no copy to read them from where the destinations had converged to the same policy.

A pass already holds every blob beside a backup in flight, on the journal's word or on the service's that the set's backup is queued, running or parked ([ADR-0009](0009-garbage-collection-safety.md) Amendment 8). The service knows its restores the same way: it records each one under way from before it writes until it ends ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)).

**Decision.**

- **The service says whether a restore of a set is under way.** `RestoreRunJournal` answers it from the runs under way in this process, by the set they restore. A run on record when the journal opens was left by a process that has gone, and is not under way.
- **A pass holds every blob while one is,** as beside a backup. It writes no blob tombstone, and the sweep and the trim delete no blob; snapshots still expire. The service asks before each retention apply and each person's deletion, the two places it runs a pass.
- **The report says so,** on a line of its own: "held beside a restore", with what was not tombstoned, deleted or trimmed. A pass held beside both a backup and a restore says both.
- **No time bound.** A restore through the service ends, finished or not, and its record says it is no longer under way. A process that dies takes the hold with it.

**Built:** `Application/RestoreRunJournal`, `Retention/RetentionRunner`, `Agent/ServiceCommandHandler`:

- `Application.Tests/RestoreRunJournalTests` — a run under way says its set is being restored, and another set is not; one that ended, finished or not, says nothing; a run on record when the journal opens says nothing.
- `Retention.Tests/CollectionBesideABackupTests` — a pass told a restore of the set is under way holds a blob whose grace has run, and its line names a restore and not a backup; the next pass, told nothing, deletes it.
- `Hosts.Tests/RetentionBesideARestoreTests` — through the service, an apply and a person's deletion beside a run of the set say they held; an apply beside a run of another set holds nothing, and one after the run has ended holds nothing.

**Not done.**

- **A sync's convergence.** A destination still drops what its policy no longer keeps while a restore of the set is under way. Under direct-ship the destinations are the only holders, and a restore reads around to them.

  > **2026-10: taken up by [Amendment 4](#amendment-4-2026-10--a-sync-keeps-a-destinations-blobs-beside-a-restore-of-the-set).**
- **A restore in direct mode.** The command line restores outside the service, and nothing tells a pass it is running.

  > **2026-10: taken up by [Amendment 6](#amendment-6-2026-10--a-restore-in-direct-mode-says-it-is-under-way-where-the-service-looks).**

## Amendment 4 (2026-10) — a sync keeps a destination's blobs beside a restore of the set

Amendment 3 held the set's own pass and left a sync's convergence alone. A destination whose own policy keeps less than staging drops, on every sync, the blobs of the snapshots its policy no longer keeps. A restore under way may read around to that copy, and under direct-ship the destinations hold the only copies there are.

**Decision.**

- **While the service has a restore of the set under way, a sync drops no blob at a destination.** Every blob joins the convergence's spares, beside the closure a sibling is still owed ([ADR-0046](0046-direct-to-destination-publication.md), FR-GC-009). Snapshots still go, as they do from staging. A blob a dropped snapshot leaves unnamed waits for the next pass.
- **The hold moves the keep fingerprint** the reconciliation gate compares ([ADR-0056](0056-incremental-reconciliation.md)). The first sync after the restore, with nothing published since and a keep-set that has not moved, would otherwise find the pair level and skip, and keep for good what only the restore held. With it, that pass sees the fingerprint move and converges.

**Built:** `Agent/FanOut`; `Hosts.Tests/ConvergenceBesideARestoreTests` — through the service, a staging set whose destination keeps only the newest of the day's snapshots takes two backups. A sync beside a run of the set under way drops the first snapshot at the destination but none of its blobs. Once the run has ended, the next sync drops them, with nothing published between. `Hosts.Tests/DirectShipConvergeSpareTests` — under direct-ship, a sync beside a restore of the set still spares the snapshots a sibling destination is owed.

**Not done.** A restore in direct mode is still held for by nothing. A peer converges on the retention apply's signed instruction rather than on a sync, and is not held for.

> **2026-10: the peer is taken up by [Amendment 5](#amendment-5-2026-10--a-granted-run-instructs-no-peer-beside-a-restore-of-the-set).** A restore in direct mode is still held for by nothing.

> **2026-10: the restore in direct mode is taken up by [Amendment 6](#amendment-6-2026-10--a-restore-in-direct-mode-says-it-is-under-way-where-the-service-looks).**

## Amendment 5 (2026-10) — a granted run instructs no peer beside a restore of the set

Amendment 4 left a peer as it was. A restore reads around to a peer's copy: the set's other copies include each peer, dialled for the run. A peer drops what an instruction signed under a person's grant tells it to, and a granted run, whether a retention apply or a person's deletion, instructs every peer under rules ([ADR-0055](0055-reclaim-authority.md) §6).

**Decision.**

- **While the service has a restore of the set under way, no granted run instructs a peer.** Both kinds of run converge a peer through one call, and that call sends nothing while a restore of the set is under way.
- **The run says so** for each peer it held: "held beside a restore", and that the next granted run converges it.
- **The peer is held whole,** where staging and a sync hold blobs and let snapshots go. Its instruction is a keep-set the peer converges to, and one that kept every blob would be the whole listing. A person's deletion held this way stays pending at the peer, and staging keeps the snapshot until every copy has let it go (FR-GC-013), so the next granted run finishes it.

**Built:** `Agent/FanOut`; `Hosts.Tests/PeerConvergenceBesideARestoreTests` — through two services and a live peer, three days of backups land whole at the peer. A granted apply beside a run of the set under way says the peer was held and leaves it every key it had. The next apply converges it to the one snapshot its policy keeps.

**Not done.** A restore in direct mode is still held for by nothing.

> **2026-10: taken up by [Amendment 6](#amendment-6-2026-10--a-restore-in-direct-mode-says-it-is-under-way-where-the-service-looks).**

## Amendment 6 (2026-10) — a restore in direct mode says it is under way where the service looks

Amendments 3 to 5 hold beside a restore the service runs, on the word of its run journal. The command line restores in direct mode outside the service. It reads the repository itself, takes no writer role, and by default keeps its state in a folder for the repository that no service reads. A retention pass, a sync or a granted run beside one could take a blob it was about to read.

**Decision.**

- **A restore in direct mode holds a marker while it runs.** It holds it from just before it writes until it ends, however it ends, as the service names its own runs ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)). The marker is a file named by the run under `restores-under-way/<repository>/` in a state directory, held open with no sharing. It is kept by repository, not by set: the command line restores a repository, and may know no set by name.
- **Where it is held.** In the state directory the command works in. Where the command named none, also in the one the installation's service keeps (FR-SVC-016), found as every path-less process finds it. That directory is never made: a restore makes no installation. A command that names a state directory names the installation it works with.
- **The handle is the truth,** as the writer role's is ([ADR-0028](0028-service-boundary-and-deployment-topologies.md) §4). A run that dies lets go with its process, so no clock decides when a marker is stale.
  - A marker nobody holds says nothing, and the first look that finds it so clears it.
  - A handle deletes its file before it lets it go.
  - A run that opened a file just as a look cleared it finds the file gone once it holds it, and takes it again.

  So a look never clears a marker a run still holds.
- **The service holds on either word.** It asks whether it has a restore of the set under way, or whether any marker for the set's repository is held in its state directory. It asks before each retention apply, each person's deletion, each sync's convergence and each peer instruction. The lines say "held beside a restore", as before.
- **A marker that cannot be held is passed over, and said.** The restore goes ahead, and a warning names the state directory where it could not say it was under way. The likely case is a command line that may not write the service's state directory. Refusing the restore would keep the operator from their own files for the sake of what a later pass might take.

**Built:** `Application/RestoresUnderWay`, `Cli/OperationGateway`, `Agent/ServiceRuntime`, `Agent/FanOut`, `Agent/ServiceCommandHandler`:

- `Application.Tests/RestoresUnderWayTests`:
  - a run held says its repository is being restored, and no other;
  - one let go says nothing and leaves no file;
  - two runs of one repository hold it until both end;
  - a marker nobody holds says nothing and is cleared;
  - a run not named by lowercase hex digits is refused and marks nothing.
- `Cli.Tests/DirectRestoreUnderWayTests`:
  - a restore holds its repository under way in its state directory while it runs, and not after;
  - a run cut short lets its marker go;
  - a state directory that cannot hold one still restores, and the console warns;
  - the installation's state directory is added only where none was named and it exists, and is never made.
- `Hosts.Tests/RetentionBesideARestoreTests`:
  - through the service, an apply and a person's deletion beside a marker of the set's repository say they held;
  - one of another repository holds nothing;
  - once it is let go, nothing is held.
- `Hosts.Tests/ConvergenceBesideARestoreTests` — a sync beside a marker keeps the blobs the destination's policy drops, and the next sync drops them.
- `Hosts.Tests/PeerConvergenceBesideARestoreTests` — a granted apply beside a marker instructs no peer.

**Not done.**

- **A service and a command line that do not meet.** A service started on a state directory of its own naming is held only by a command line that names that same directory.

  > **2026-11: taken up by [Amendment 7](#amendment-7-2026-11--a-service-on-a-state-directory-of-its-own-looks-in-the-installations-too)** for a command line that names none: such a service looks in the installation's state directory too.
- **A command line that may not write the service's state directory** restores unheld. It warns as it does.
- **A restore on another machine.** A marker is seen only by a service whose state directory holds it, so a restore from a destination elsewhere is not held for at the service that keeps its source.

## Amendment 7 (2026-11) — a service on a state directory of its own looks in the installation's too

Under Amendment 6, a restore in direct mode that names no state directory holds its marker in the installation's too, and the service looks in its own. A service started on a state directory of its own naming never looked in the installation's. That is how a second service, a harness, or a service unit naming its own paths runs. So a command line that named none, restoring a repository such a service keeps, was not held for there. A retention pass, a sync or a granted run beside it could take a blob the restore was about to read.

**Decision.**

- **The service also looks in the installation's state directory,** found as every path-less process finds it (FR-SVC-016). It looks where that directory stands and is not the service's own, and looking makes nothing.
- **A marker there holds as one in the service's own does,** before each retention apply, each person's deletion, each sync's convergence and each peer instruction. The four ask one question, as before.
- **An installation's state directory the service may not read is passed over, without a word.**
  - A service running unprivileged beside a machine-wide installation may not list the installation's state directory.
  - Failing every retention pass over that would cost more than the hold protects.
  - A line on every pass would say nothing new.

**Built:** `Agent/ServiceRuntime`, `Api/InstallationDefaults`, `Cli/OperationGateway`:

- `Hosts.Tests/RetentionBesideARestoreTests`:
  - a service on a state directory of its own, beside a marker of the set's repository held in the installation's, holds an apply's blobs and says so; once the marker is let go, nothing is held;
  - where the installation has no state directory, nothing is held and none is made;
  - on Linux and macOS, where the installation's may not be read, an apply goes ahead and holds nothing. Only a process not running as root is kept from reading it, as on CI.
- `Cli.Tests/DirectRestoreUnderWayTests` — the command line finds the installation's state directory by the same rule, now `InstallationDefaults.StateDirectoryWithoutMaking`, and never makes it.

**Not done.**

- **A command line that names a state directory of its own** is held for only by a service on that same directory. Amendment 6 decided that a command that names one names the installation it works with.
- **A restore on another machine** is still not held for (Amendment 6).

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The review's compaction case held as it stood. A blob that goes while a restore reads it no longer ends the run or reads as damage. No contract, format or schema change. |
| 2026-10 | Amended | Amendment 1: a blob that goes from another copy while the run reads around to it is passed over there as not held, and a read through that copy's footer no longer ends the run. `Repository/RepositoryReader`; `Repository.Tests/RestoreBesideCollectionTests` |
| 2026-10 | Amended | Amendment 2: with no copy to read it from, a record whose blob the store does not hold fails as not held, not as a format violation, whether the blob went during the run or before it. `Repository.Packing/RecordReadResult`, `Repository/RepositoryReader`; `Repository.Tests/RestoreBesideCollectionTests` |
| 2026-10 | Amended | Amendment 3: a retention pass and a person's deletion hold every blob while the service has a restore of the set under way, and say so on a line of their own; snapshots still expire. `Application/RestoreRunJournal`, `Retention/RetentionRunner`, `Agent/ServiceCommandHandler`; `Application.Tests/RestoreRunJournalTests`, `Retention.Tests/CollectionBesideABackupTests`, `Hosts.Tests/RetentionBesideARestoreTests` |
| 2026-10 | Amended | Amendment 4: a sync drops no blob at a destination while the service has a restore of the set under way, and the hold moves the keep fingerprint so the first sync after it converges. `Agent/FanOut`; `Hosts.Tests/ConvergenceBesideARestoreTests`, `Hosts.Tests/DirectShipConvergeSpareTests` |
| 2026-10 | Amended | Amendment 5: a granted run, a retention apply or a person's deletion, instructs no peer while the service has a restore of the set under way, and says so; the next granted run converges it. `Agent/FanOut`; `Hosts.Tests/PeerConvergenceBesideARestoreTests` |
| 2026-10 | Amended | Amendment 6: a restore in direct mode holds a marker of its repository in its state directory and, where it named none, in the installation's; the service holds a retention pass, a deletion, a sync's convergence and a peer instruction on such a marker as on its own runs. `Application/RestoresUnderWay`, `Cli/OperationGateway`, `Agent/ServiceRuntime`; `Application.Tests/RestoresUnderWayTests`, `Cli.Tests/DirectRestoreUnderWayTests`, `Hosts.Tests/RetentionBesideARestoreTests`, `Hosts.Tests/ConvergenceBesideARestoreTests`, `Hosts.Tests/PeerConvergenceBesideARestoreTests` |
| 2026-11 | Amended | Amendment 7: a service on a state directory of its own also holds on a marker of the set's repository in the installation's state directory, found as every path-less process finds it and never made; one it may not read is passed over. `Agent/ServiceRuntime`, `Api/InstallationDefaults`; `Hosts.Tests/RetentionBesideARestoreTests` |

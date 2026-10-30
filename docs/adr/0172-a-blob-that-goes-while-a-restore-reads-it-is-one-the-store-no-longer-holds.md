# ADR-0172 — A blob that goes while a restore reads it is one the store no longer holds

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-007 (amended: a blob that goes from the own store during the run is read around as not held), FR-RST-005 (amended: with no copy to read it from, the files it held fail and the run keeps its receipt; by Amendment 2, they fail as not held)
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

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The review's compaction case held as it stood. A blob that goes while a restore reads it no longer ends the run or reads as damage. No contract, format or schema change. |
| 2026-10 | Amended | Amendment 1: a blob that goes from another copy while the run reads around to it is passed over there as not held, and a read through that copy's footer no longer ends the run. `Repository/RepositoryReader`; `Repository.Tests/RestoreBesideCollectionTests` |
| 2026-10 | Amended | Amendment 2: with no copy to read it from, a record whose blob the store does not hold fails as not held, not as a format violation, whether the blob went during the run or before it. `Repository.Packing/RecordReadResult`, `Repository/RepositoryReader`; `Repository.Tests/RestoreBesideCollectionTests` |

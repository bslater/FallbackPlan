# ADR-0172 — A blob that goes while a restore reads it is one the store no longer holds

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-007 (amended: a blob that goes from the own store during the run is read around as not held), FR-RST-005 (amended: with no copy to read it from, the files it held fail and the run keeps its receipt)
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
- **Tell a blob that went from damage at another copy.** A blob that goes from a destination while the run reads around to it is still passed over there as damaged, so it lands on that destination's ledger as a finding.

## Alternatives considered

- **Hold every blob a restore in flight might read, as a backup in flight holds blobs (ADR-0009 Amendment 8 rule 3).** Rejected for now. A restore can run for hours over a slow peer, and holding the trim and the sweep for its length holds space a destination already holds a copy of. It would also not help a restore of the snapshot being expired, which a person chose to expire.
- **Make the blob reader answer a missing range as an I/O error.** Rejected. Every caller of the blob reader would then take a blob that went for a transient fault, and the loader would fail a load over a blob it could have skipped.
- **Re-resolve every record before reading it.** It already is. What a reader keeps is about the blob, and the blob is what went.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The review's compaction case held as it stood. A blob that goes while a restore reads it no longer ends the run or reads as damage. No contract, format or schema change. |

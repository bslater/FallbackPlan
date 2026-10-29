# ADR-0171 — A destination that refuses a delete keeps what it reaches, and the sync goes on

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-GC-010 (amended: a delete a destination does not carry out is reported, holds back what lies beneath it, and does not fail the sync), FR-GC-006 (its rule for the staging sweep, now held at a destination too)
**Related:** [ADR-0034](0034-hub-and-spoke-destinations.md) §4 (retention at a destination is hub-planned deletion), [ADR-0009 Amendment 8](0009-garbage-collection-safety.md#amendment-8-2026-10--a-backup-builds-on-more-than-its-intent-names) (a snapshot whose delete was refused keeps the blobs it reaches, at the staging sweep), the [prior-art review](../review/2026-08-prior-art-learnings.md)'s T-12

**Built:**
- The converge: `Replication/StoreToStoreCopier` counts a delete the destination did not carry out as refused and does not fail on it. Once any delete in a phase is refused, the rest of that phase runs and no deeper phase is dropped that pass. A store that is busy or unreachable still fails the pass.
- The notice: `Agent/FanOut` raises one that names the destination, the set and the count. It is resolved by the next pass that refuses nothing.
- The tests:
  - `Replication.Tests/ConvergeRefusalTests` — a refused snapshot delete, as the store's refusal and as a path that may not be written, keeps the snapshot and the blob only it reaches, and the pass goes on; healed, the next pass deletes both. A delete answered "not found" for an object still there keeps what it reaches. A refused blob delete leaves the snapshot above it dropped. A busy store fails the pass and drops nothing beneath.
  - `Hosts.Tests/ObjectStoreDestinationTests`, for S3 and Azure Blob — a store that refuses every DELETE with 403 under a policy that drops the first snapshot stays in sync, raises the notice and reads back whole, and once it will delete the next sync does and the notice goes

---

## Context

Retention at a destination is hub-planned deletion (ADR-0034 §4). Converge carries it out. It deletes what the destination's keep-set no longer holds, phase by phase in reverse dependency order, snapshots first and blobs last, so that the replica is lagging-but-valid at every interruption point.

The prior-art review's T-12 set the criterion for a destination that refuses deletes. It must leave reclamation reporting unreclaimed space, never failing the run, and never leaving a partly applied reclamation. A converge met neither half:

- **A refusal failed the sync, every time.** A store that answers a delete with a refusal throws. That is a bucket under object lock, an immutability policy, a credential without the right to delete, or a local path that may not be written. The exception left the converge and the sync was Failed. It failed again on every pass once retention asked for anything, though everything new had been copied. The replica stayed valid only because the throw stopped the deletes.
- **A delete answered "not found" was believed.** The answer was logged, and the pass went on into the phases beneath. If the store still held the snapshot, the blobs it reaches were deleted under it. The replica then listed a snapshot it could no longer restore. FR-GC-006 already holds the staging sweep to the opposite: a snapshot whose delete does not happen holds back the blobs it reaches.

## Decision

### 1. A delete the destination did not carry out is refused, not failed

A delete is refused when the store answered no:

- an `IOException` that is not a `StoreUnavailableException`, which is how the S3 and Azure Blob providers answer a 403 or a 409;
- an `UnauthorizedAccessException`, which is how a local path that may not be written answers.

A delete answered "not found" for an object the pass listed a moment before is refused too. It has not been shown gone.

A store that is busy or unreachable has not answered. That pass fails and is tried again, as before.

### 2. Nothing beneath a refusal is dropped that pass

Once any delete in a phase is refused, the rest of that phase runs. Snapshots do not reach snapshots, and blobs do not reach blobs. No deeper phase is dropped that pass. What a refused delete kept may reach anything beneath it, and the converge cannot tell what: it carries no keys. So the replica restores every snapshot it lists.

The cost is space: objects a deleted snapshot alone reached wait for a pass with no refusals.

### 3. The refusal is said, and the sync is not failed for it

- The converge's outcome counts the refusals, and each is logged with its key.
- Fan-out raises a notice naming the destination, the set and the count. It says the destination still restores what it lists while holding space its policy released, and the next sync that refuses nothing resolves it.
- The sync itself stays in sync, because everything owed was copied.

## Consequences

- A destination under a write-once policy, or reached with a credential that cannot delete, is usable as a destination. Its syncs succeed and its notice says why its space does not shrink.
- No refused delete leaves a replica listing a snapshot it cannot restore.
- A destination that refuses only for a while catches up on its own: the first pass with no refusals deletes everything the policy dropped.

## What this does not do

- **Report the unreclaimed space in bytes.** The notice counts deletes, not the bytes they would have freed, or the objects held back beneath them.
- **The staging sweep's figure.** The sweep already holds back a refused snapshot's blobs (ADR-0009 Amendment 8); it does not total what it could not reclaim either.
- **Tell a versioned bucket's delete marker from a delete.** An S3 bucket with versioning answers a delete with success and keeps the object's bytes, so the space is not freed and nothing says so. The replica is valid, since the object no longer lists or reads.
- **Judge a direct-ship sink per destination.** A delete through the sink reports Deleted when any one destination deleted.

## Alternatives considered

- **Fail the sync, as before, and let a person change the policy.** Rejected. A write-once destination is a legitimate choice, often the point of one, and a sync that fails forever hides every other failure behind it.
- **Delete beneath a refused snapshot anyway, trusting the next pass to retry the snapshot.** Rejected. Between the passes the replica lists a snapshot it cannot restore, and a write-once store never lets it go.
- **Read the refused snapshot's closure and hold back only that.** Rejected for now. The converge carries no keys, and reading a closure needs them. Holding back every deeper phase costs space for one pass at a destination that refuses, and nothing at one that does not.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A delete a destination does not carry out is counted, logged and noticed, holds back every deeper phase that pass, and does not fail the sync; a busy or unreachable store still does. No contract or format change. |

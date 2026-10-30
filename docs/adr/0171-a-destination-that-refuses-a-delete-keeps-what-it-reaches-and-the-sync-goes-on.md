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

  > **Built 2026-10 ([Amendment 1](#amendment-1-2026-10--said-in-bytes)).** Both now say how many bytes a refusal holds. What a refused object reaches, held back with it, is still not sized.
- **Tell a versioned bucket's delete marker from a delete.** An S3 bucket with versioning answers a delete with success and keeps the object's bytes, so the space is not freed and nothing says so. The replica is valid, since the object no longer lists or reads.

  > **Built 2026-10 ([Amendment 2](#amendment-2-2026-10--a-delete-a-bucket-keeps-is-said)).** The S3 provider reports what a delete marker hid, and the sync says how much the bucket kept. An Azure Blob account that keeps versions or soft-deletes is not yet told apart.
  >
  > **Built 2026-10 ([Amendment 4](#amendment-4-2026-10--a-delete-an-account-keeps-under-soft-delete-is-counted-and-noticed-nowhere)).** An account under soft delete is told apart, counted and logged, with no notice. One with versioning on gives no sign on the delete.
- **Judge a direct-ship sink per destination.** A delete through the sink reports Deleted when any one destination deleted.

  > **Held 2026-10 ([Amendment 3](#amendment-3-2026-10--the-sinks-delete-is-not-done-where-a-destination-refused-it)).** Read against the sink, the answer is per key and true. A destination that refuses makes the delete fail, not succeed, and what it refused stays listed for the next pass.

## Alternatives considered

- **Fail the sync, as before, and let a person change the policy.** Rejected. A write-once destination is a legitimate choice, often the point of one, and a sync that fails forever hides every other failure behind it.
- **Delete beneath a refused snapshot anyway, trusting the next pass to retry the snapshot.** Rejected. Between the passes the replica lists a snapshot it cannot restore, and a write-once store never lets it go.
- **Read the refused snapshot's closure and hold back only that.** Rejected for now. The converge carries no keys, and reading a closure needs them. Holding back every deeper phase costs space for one pass at a destination that refuses, and nothing at one that does not.

## Amendment 1 (2026-10) — said in bytes

The review's T-12 asks that reclamation report the space it could not reclaim. This record and the sweep before it said that a delete did not happen, and nothing said how much space that holds, which is what a person needs to judge whether it matters.

**Decision.** Each reclaim path sizes what its refusals hold:

- **A destination.** When a destination refuses a delete, or answers "not found" for an object the pass listed a moment before, it is asked for the object's length. The converge's outcome carries the total, and the notice states it: it refused N deletes, "holding B byte(s) its policy released". The notice says what those objects reach is held on top of that figure, since §2 drops no deeper phase that pass and does not size what it holds back.
- **The staging sweep.** A snapshot or blob delete that does not happen is counted as deferred, at the length the store gives the object once it has refused. The report's "swept:" line ends ", N deferred (B byte(s) not reclaimed)" when there are any.
  - The blobs held behind a standing snapshot have their own finding and are not in the figure.
  - Neither is a tombstone's own delete.

A store that refuses a delete still answers a read, so a length costs one metadata call, and only on a refusal. Where the store will not say, the object is counted and its length is not. Both figures are formatted in the invariant culture.

**Built:** `Replication/StoreToStoreCopier`, `Agent/FanOut`, `Retention/StagingSweep` and `Retention/RetentionRunner`. The tests:

- `Replication.Tests/ConvergeRefusalTests`: each refusal counts the refused object's length, and a healed pass counts none.
- `Hosts.Tests/ObjectStoreDestinationTests`, for S3 and Azure Blob: the notice states a byte figure above nothing.
- `Retention.Tests/SweepFailureTests`: the deferred objects and the sum of their lengths are on the outcome and on the "swept:" line.

## Amendment 2 (2026-10) — a delete a bucket keeps is said

The review's T-12 names a bucket under object lock as the usual defence, and §1 does not reach one on S3. Object lock needs versioning, and the bucket does not refuse a delete without a version id. It answers with success and writes a delete marker. The object no longer lists or reads, and its bytes stay as an earlier version until the bucket's own rules release them. A bucket with versioning on does the same. The converge took each such delete as space released, and nothing said otherwise.

**Decision.** A delete the store answers and keeps all the same is a delete that says so:

- **The contract.** `DeleteResult` gains `RetainedBytes`: the bytes a store kept although it answered the delete, null where it kept none or does not say. It is optional, so every provider and wrapper that builds a plain result is unchanged.
- **The S3 provider.** It already asks for an object's metadata before it deletes it. When the API's answer to the delete carries `x-amz-delete-marker: true`, it reports that object's length as retained.
- **The converge.** A retained delete is a delete. Nothing lists or reads the object after, so nothing beneath it is held back for it, and it is not a refusal. It is also counted as retained, in objects and bytes.
- **The notice.** Fan-out raises one per destination and set, keyed `destination-deletes-retained:{set}:{destination}`. It says how many objects and bytes the store kept as earlier versions, that every snapshot it lists still restores, and that the space comes back only by the store's own rule for earlier versions. A pass whose deletes kept none resolves it. A pass that deleted nothing leaves it as it stands, having learned nothing.

What this does not do:

- **An Azure Blob account that keeps versions or soft-deletes.** Its delete keeps the blob as a previous version or a deleted one, and its answer is not read for that. The space is held and nothing says so, as it was for S3.
- **Size what a bucket kept before.** The figure is each pass's own deletes, not what earlier ones left.

**Built:** `Storage.Abstractions/StoreResults`, `Storage.S3/S3ObjectStore`, `Replication/StoreToStoreCopier` and `Agent/FanOut`. The tests:

- `Storage.ContractTests/S3ObjectStoreTests`: a delete in a bucket that keeps versions is a delete that says the length of what it hid, and the object no longer reads. One in a bucket that keeps none is a plain delete.
- `Replication.Tests/ConvergeRefusalTests`: at a destination that keeps versions, the older snapshot and the blob only it reaches are deleted, nothing is refused, and the outcome counts both as retained, in objects and bytes.
- `Hosts.Tests/S3DestinationTests`: a sync under a retention policy to a bucket that keeps versions stays in sync, verifies whole and raises the notice with the bytes the bucket kept. Once the bucket keeps none, the next sync's deletes resolve it.

The S3-compatible test server answers a delete in a bucket that keeps versions as the API does, with the delete marker's header and a version id.

## Amendment 3 (2026-10) — the sink's delete is not done where a destination refused it

The review's T-12 left one item at the sink: a delete through a direct-ship set's sink reports Deleted when any one destination deleted. Read against `Agent/DestinationShipSink`, that is true and is not a hazard:
- **The order.** The sink deletes at every destination it can reach, priority first, and at the metadata store last.
- **A refusal propagates.** A destination that refuses throws. The delete then fails rather than reading as done, at whatever point the refusal came, and the staging sweep defers it as Amendment 1 counts.
- **What was refused stays listed,** so the next pass condemns it again. A metadata object stays in the metadata store, which is deleted last. A blob stays in the union the sink lists, which includes what each destination holds.
- **Deleted is true wherever it is said.** It is said only when no destination refused, and a destination that answers "not found" holds nothing.
- **What the sink reaches.** Outside a run that is local paths, which keep nothing a delete removes, so no retained figure is lost. A run reaches peers too.
- **What it never reaches.** An object-store destination of a direct-ship set is left to the sync that follows the run. That destination's deletes are the convergence's, which judges each one (§1, Amendment 2).

**Decision.** Held as it stands, by `Hosts.Tests/DirectShipTests.ASinkDeleteOneDestinationRefuses_AfterAnotherDeleted_IsNotDone_AndTheNextDeleteFinishesIt`. The case gives a direct-ship set two local destinations, and the second refuses deletes. For a snapshot and for a blob:
- the delete fails;
- the first destination has deleted, and the second keeps its copy;
- the object still lists through the sink;
- the metadata store keeps the snapshot.

With the refusal lifted, the next delete is Deleted and the object is gone from both destinations and the metadata store.

It passed as the code stood. Each of three changes to the sink fails it:
- deleting at the metadata store first, which leaves the refused snapshot listed nowhere;
- passing over a destination's refusal, which reads as done;
- deleting at the first destination alone, which reads as done.

## Amendment 4 (2026-10) — a delete an account keeps under soft delete is counted, and noticed nowhere

The review's T-12 left one item open: an Azure Blob account that keeps versions or soft-deletes. Read against the API, the two are not alike:
- **Soft delete is on by default** for a new account. A delete answers 202 with `x-ms-delete-type-permanent: false`. The account keeps the bytes until its retention period ends, then releases them itself. Nothing lists or reads the blob meanwhile.
- **Versioning gives no sign** on the delete at all. The account keeps the version, and the answer is the same as a permanent delete's.

The provider read neither, so the sync took each soft delete as space released.

It is counted, and noticed nowhere. Amendment 2's notice is for space a store keeps until its own rule for earlier versions releases it, which a person has to act on. A soft delete's space comes back on its own. A notice for it would stand on every default account, and teach people to pass notices by.

**Decision.**

- **The contract.** `DeleteResult` gains `SoftDeleted`: the store keeps what it deleted only for a period of its own, and then releases it. Such a store need not say how many bytes, so `RetainedBytes` may be null beside it. It is optional and last, so every provider and wrapper that builds a plain result is unchanged.
- **The Azure Blob provider** reads the permanent header from the delete's own answer, and false is a soft delete. It still sends one request, and still never looks before it deletes.
- **The convergence** takes the destination's listing with the lengths it already carries, since a soft-deleted object cannot be asked its length once it is gone. A soft delete is counted as deleted, and on `ConvergeOutcome.SoftDeleted` and `SoftDeletedBytes`, sized by what the store said or else by the listing. It is not counted as retained. Event 3008 logs the figure at Information, once per pass.
- **The notice is unchanged.** It reads what was retained alone, so an account under soft delete raises nothing.

**Built:** in `Storage.Abstractions/StoreResults`, `Storage.AzureBlob/AzureBlobObjectStore` and `Replication/StoreToStoreCopier`. Held by:
- **`Storage.ContractTests/AzureBlobObjectStoreTests`:** a delete under soft delete is Deleted, soft-deleted, and one request. One the header calls permanent is a plain Deleted.
- **`Replication.Tests/ConvergeRefusalTests`:** at a destination that soft-deletes, what the policy released is deleted. Nothing is refused or retained, both objects are counted as soft-deleted with the listing's bytes, and event 3008 says so.
- **`Hosts.Tests/AzureBlobDestinationTests`:** a sync to a container under soft delete stays in sync and verifies whole, and no destination-deletes notice stands while the account keeps bytes. That case passed as the code stood; it fails the change that would route a soft delete into what was retained.

`TestSupport/AzureBlobTestServer` puts a container's account under soft delete, and every delete it answers carries the permanent header.

**Not done.** An account with versioning on keeps a version that nothing on the delete reveals, so it is not told apart.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A delete a destination does not carry out is counted, logged and noticed, holds back every deeper phase that pass, and does not fail the sync; a busy or unreachable store still does. No contract or format change. |
| 2026-10 | Amended | Amendment 1: a destination's refusals and the staging sweep's deferrals are said in bytes, in the notice and on the report's "swept:" line. `Replication/StoreToStoreCopier`; `Retention/StagingSweep`; `Retention.Tests/SweepFailureTests` |
| 2026-10 | Amended | Amendment 2: a delete a store answers and keeps as an earlier version, as an S3 bucket under object lock or with versioning does, is counted as retained, in objects and bytes, and noticed. It is a delete, not a refusal. `Storage.Abstractions/StoreResults`; `Storage.S3/S3ObjectStore`; `Replication.Tests/ConvergeRefusalTests`; `Hosts.Tests/S3DestinationTests` |
| 2026-10 | Amended | Amendment 3: the direct-ship sink's delete, read against the code, is per key and true. A destination's refusal fails the delete, what it refused stays listed for the next pass, and the metadata store is deleted last. Held as it stands. `Agent/DestinationShipSink`; `Hosts.Tests/DirectShipTests` |
| 2026-10 | Amended | Amendment 4: a delete an Azure Blob account keeps under soft delete is told apart by the delete's own answer, counted with its bytes from the convergence's listing, and logged, and raises no notice, because the account releases it by itself. One with versioning on gives no sign and is not told apart. `Storage.Abstractions/StoreResults`; `Storage.AzureBlob/AzureBlobObjectStore`; `Replication/StoreToStoreCopier`; `Storage.ContractTests/AzureBlobObjectStoreTests`; `Hosts.Tests/AzureBlobDestinationTests` |

# ADR-0104 — A replica's readers take an archived object as archived

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-VER-001 (amended: a sampled object the store holds only in an archival tier is counted neither proven nor failed), FR-VER-002 (amended: the deep sweep passes over such a blob, and its circuit closes)
**Related:** [ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md) (the store contract's archived fact and refusal, and the record that left this owed), [ADR-0035](0035-destination-fitness.md) Amendment 1 (the deep sweep, and a blob that will not read as a stall), [ADR-0091](0091-an-s3-compatible-destination.md) Amendment 1 and [ADR-0093](0093-an-azure-blob-destination.md) (the object stores, swept on a stated cadence), [architecture 09 §5.5](../architecture/09-replication-and-peers.md#55-re-reading-what-is-held-and-repairing-it)

**Built:**
- The readers: `Replication/ReplicaVerifier` counts a sample the replica refuses as archived among `VerificationOutcome`'s archived keys, through the range comparison and the sealed proof alike, and `Repository/RepositoryReader` skips an archived blob as archived in both its loads. `Repository/ReplicaSweep` passes over an archived blob, not reading it and not stopping at it, and closes its circuit past it.
- The record: `Application/DestinationSyncStore` (schema 11) counts what the sweep passed over in the circuit under way and keeps when the readers first met an archived object in the run of such meetings that stands.
- The service: `Agent/FanOut` and `Agent/ReplicaSweepJob` say it in one notice for the pair, through `Agent/ArchivedReplicaNotice`, which a circuit that met nothing archived resolves. `Agent/ServiceCommandHandler`'s `verify-destination` line says how many it could not read.
- The tests:
  - `Replication.Tests/ReplicaVerifierTests` — an archived sample is counted neither way, through the comparison and the sealed proof, and a read-back of nothing but archived samples proves nothing
  - `Repository.Tests/ArchivedBlobLoadTests` — either load skips an archived blob as archived and loads the rest; the targeted load does not ask to read a blob its store says is archived, and skips one archived between its HEAD and its read
  - `Repository.Tests/ReplicaSweepTests` — an archived blob is passed over, not read and not repaired, and the circuit closes; a segment ending at one resumes past it and spends none of its byte budget on it; one that stops at a blob after passing over one still names it
  - `Application.Tests/DestinationSyncStoreTests` — schema 11: a schema-10 row migrates with nothing standing; a circuit that met something archived keeps the run standing from when it began, and the next that meets none ends it; a read-back starts a run once; a stall counts what it passed over, and starts a run where none stands
  - `Hosts.Tests/ObjectStoreDestinationTests`, run by `Hosts.Tests/S3DestinationTests` and `Hosts.Tests/AzureBlobDestinationTests` — the sync stays in sync, the sweep closes its circuit and repairs nothing, the notice stands until a circuit meets none, and a run after that is news again; a sweep that stops at a blob after passing over archived ones still says so; a store whose data objects land archived keeps its pair in sync and says so; `verify-destination` says how many it could not read

---

## Context

[ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md) taught the store contract to say when an object is held only in an archival tier, and to refuse a read of one as archived. It named the restore's plan as the place that says so, and left the replica's other readers as they were. Two of them read a destination's objects back on their own:

- **A sync's read-back** proves a sample of what it sent, the newest snapshot always among it. A sample its store would not serve counted as a failed proof. One archived blob in the sample failed every sync that drew it, and the pair was failed, its status degraded, for as long as the blob stayed archived.
- **The deep sweep** re-reads every blob over a circuit. A blob that would not read stopped the segment there, and the next attempt began with the same blob ([ADR-0035](0035-destination-fitness.md) Amendment 1). An archived blob reads no differently on the next attempt, so the circuit stopped there for good. Nothing after it was re-read, and after three stalls a notice said the blob "would not read".

Neither was damage, and nothing repaired an archived blob, because a read that fails is never a finding. But both said the wrong thing, and one of them failed a destination that held everything it was sent.

## Decision

### 1. An archived sample is no proof and no finding

A sync's read-back counts a sample its store refuses as archived apart from what it proved and what it failed. That holds through the range comparison and through the sealed proof, where the targeted load skips an archived blob as archived rather than as damage. A pass with nothing but archived samples has proved nothing, and is recorded as such a pass is. A pass with no failures is in sync.

### 2. The sweep passes over an archived blob

The deep sweep does not read an archived blob and does not stop at it. It names the blob, moves its cursor past it, spends none of the segment's byte budget on it, and counts it neither sound nor damaged, so nothing repairs it. A repair would delete and put the blob, bringing it out of the tier at the owner's cost and against their rule. A circuit that met archived blobs closes as any other does.

### 3. One notice says it, dated from when it began

Either reader that meets an archived object raises one notice for the pair. It says:
- the store holds some of the set's objects only in an archival tier;
- they are not read back or re-read, so nothing proves them, and nothing is known to be altered;
- a restore from there names the files that need them, and those need rehydrating first.

The ledger keeps when the readers first met an archived object, in the run of such meetings that has not ended, and the notice is dated from then. Its text is the same on every pass of one run, so an acknowledgement holds through the run. A run that starts after an earlier one ended has another date, and is news again.

### 4. Only a circuit that met nothing archived ends it

Only the deep sweep reads every blob, so only a circuit that closes having met no archived blob ends the run and resolves the notice. The ledger counts what the circuit under way has passed over. A read-back that met none ends nothing, because it proves nothing of the blobs it did not sample. A store with no sweep cadence is swept when a person runs `verify-destination --full`, which ends a run the same way.

### 5. A person's full read says how many it could not read

A person's `verify-destination` names how many objects it confirmed and how many held only in an archival tier it did not read. It does not say every stored object has been checked.

## Consequences

- A destination whose store archives a backup's older blobs stays in sync, its newer objects proven as before.
- Its sweep closes every circuit, re-reading everything that is not archived, rather than stopping at the first archived blob for good.
- A person hears once per run that the store holds some objects only archived, rather than a stall notice that blames a blob for not reading.
- A full load of a store that holds an archived blob skips it as archived and loads the rest, rather than failing for want of it. Whatever decides from a full load what to delete takes it as it takes a blob that would not open: collection vetoes at it, and convergence keeps it.
- The ledger moves to schema 11. A build before it sets a newer ledger aside, as for every schema step.

## What this does not do

- **Say it in the status row.** The row's sweep still says when a circuit last closed. A circuit that passed over archived blobs closes too, so the closing no longer means every stored object was read back. The notice says that, and the contract is unchanged.

  > **2026-10 ([ADR-0106](0106-a-closed-circuit-says-what-it-did-not-read.md)).** The row now says it too. Contract 1.66's `circuit_archived` counts what the closed circuit passed over, and only a circuit that passed over none is shown as every stored object read back.
- **Count it per object or per snapshot.** The notice names no object and no file. A restore's plan names the files ([ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md)).
- **Take a drill's restore as archived.** A drill restores sample files from the destination, and a file whose content is archived fails it, saying why in the provider's words. Telling a drill that met only archived content from one that met damage is not done here.

  > **2026-10 ([ADR-0105](0105-a-drill-takes-archived-content-as-a-stated-limit.md)).** A drill at a store with an archival tier now plans each sampled file first. One whose content the store holds only archived is proved as far as that content under a stated limit, and the drill says it in this record's notice.
- **Read other objects around their tier.** An index object a lifecycle rule archived is met by whatever reads it, as before.

  > **2026-10 ([ADR-0112](0112-a-store-that-archives-a-replicas-metadata-restores-nothing.md)).** The readers this record changed now tell a metadata blob from a data blob, and every sync to an object store asks after the replica's format. An object of the replica's own held archived is named on the status row and in a notice of its own, which says nothing restores from there until it is rehydrated. Other objects are still met by whatever reads them.

## Alternatives considered

- **Leave the stall to do the saying.** Rejected. The sweep would stop at the first archived blob for good, and nothing after it would ever be re-read.
- **Count an archived sample as a failure, as before.** Rejected. A failure says the destination cannot be trusted with what it holds. It holds everything it was sent, under a rule its owner set.
- **Skip archived objects when the sample is drawn.** Rejected. Knowing which are archived costs a request per candidate before any is read, and the read itself already answers.
- **One notice per run with no date, resolved by a circuit.** Rejected. A notice that has been resolved and says the same thing again is treated as one already seen, so a store that archived again later would never be named.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A sync's read-back counts an archived sample neither proven nor failed, and the deep sweep passes over an archived blob and closes its circuit, repairing nothing. One notice for the pair, dated from when the run of such meetings began, says so until a circuit meets none (ledger schema 11). A person's `verify-destination` says how many it could not read. |
| 2026-10 | Accepted | [ADR-0105](0105-a-drill-takes-archived-content-as-a-stated-limit.md) takes up the drill this record left as it was: a drill takes archived content as a stated limit, and is a third reader that raises the pair's notice. |
| 2026-10 | Accepted | [ADR-0106](0106-a-closed-circuit-says-what-it-did-not-read.md) takes up the status row this record left as it was: a closed circuit's sweep says how many blobs it passed over held only archived (ledger schema 12, contract 1.66). |
| 2026-10 | Accepted | [ADR-0112](0112-a-store-that-archives-a-replicas-metadata-restores-nothing.md) takes up the other objects this record left to whatever reads them: the sync's read-back and the deep sweep tell an archived metadata blob from a data blob, a sync asks after the replica's format, and the destination says nothing restores from it until the object is rehydrated. This record's notice is still the one a data blob raises. |

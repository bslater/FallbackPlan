# ADR-0199 — A backup is proven at each destination it lands at

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-VER-002 (amended: the pass that finds a destination holding the newest backup whole proves there every blob that backup needs which the backup last proven there did not, beside the sample and never in its place, a bounded share a pass, the pair due until the proof is carried to its end)
**Related:** [ADR-0035](0035-destination-fitness.md) §5 (the sample's rotation, which this sits beside), [ADR-0198](0198-a-sync-copies-only-from-a-source-that-holds-the-newest-backup-whole.md) Amendment 2 (a destination is in sync only once it holds the newest backup whole, which is where this proof starts), [ADR-0012](0012-storage-provider-contract.md) Amendment 8 (listings in key order, which the proof's cursor resumes through)

**Built:**

- The request: `Agent/NewBackupProof` names the blobs the newest backup needs that the backup last proven at the destination did not, from `Repository.Catalogue/Catalogue`'s closure of each (`ClosureOf`), and says where the proof stands after a pass.
- The sample: `Replication/VerificationSampler` finds those blobs by name in the listing it already reads, in key order after the proof's cursor, up to the pass's share, beside the rotation and never in its place. `Replication/ReplicaVerifier` says which keys it proved.
- The pass: `Agent/FanOut`'s sync to a local path or a store asks about them with the rotation's samples, fails the pass as a failed sample does when one does not prove, and otherwise records where the proof stands, logging event 3705. `Agent/Scheduler` finds a pair with a proof under way due.
- The ledger: `Application/DestinationSyncStore` keeps the backup last proven, the backup being proven and its cursor (schema 14).
- The tests:
  - `Hosts.Tests/NewBackupProofTests`: a first backup leaves its local path proven through it, staging and direct-ship alike; a data blob of a second backup damaged on its way is found by the pass that sent it, with the sample asking only about the record; a backup larger than a pass's share is proven over the passes after it, the pair due while it is; a second backup proves only the blobs the first did not need.
  - `Hosts.Tests/NewBackupProofStepTests`: where the proof stands after a pass, a blob left unproven asked again, a newer backup's proof started from its first blob.
  - `Replication.Tests/VerificationSamplerTests`: the blobs found by name whatever their class and never a non-blob, in key order whatever the listing's, after the cursor, up to the share, none the destination drops or the source lacks, and the rotation drawn as before.
  - `Application.Tests/DestinationSyncStoreTests`: the three fields kept, and a schema-13 ledger migrating with no backup proven.

---

## Context

FR-VER-002 asks that verification always cover the most recent snapshot's dependencies. A sync's sample covers the newest snapshot's record and then rotates through the rest of the key space, sixteen keys a pass, with a random share at a store or a peer ([ADR-0035](0035-destination-fitness.md) §5). The blobs a new backup sends are proven only when the rotation reaches them.

The full re-read reaches every blob, but on a cadence. A local path's circuit rests a configured interval between circuits. A store and a peer are swept only on a cadence their operator states, and on none by default (FR-VER-002, FR-VER-008).

So at a store swept on no cadence, a blob a new backup sent and the store holds damaged is found when the rotation reaches it. Over an archive of thousands of blobs that is weeks. The backup is called verified meanwhile, because its record was.

## Decision

1. **What is proven.** The pass that finds a destination holding the newest backup whole ([ADR-0198](0198-a-sync-copies-only-from-a-source-that-holds-the-newest-backup-whole.md) Amendment 2) proves there the blobs that backup needs which the backup last proven there did not. That is the newest backup's closure less the closure of the backup last proven. A first backup's is the whole of it; a daily backup's is the handful it added.
2. **How.** As the sample proves a blob (FR-VER-001): opened where it sits, its footer authenticating and one record's tag, or by the chunk or digest tier where the records are sealed. The blobs are found by name in the listing the sample already reads, and asked beside the rotation, never in its place. A blob that does not prove fails the pass as a failed sample does, naming it.
3. **A share a pass.** A pass asks about at most 256 of them, in key order. A backup with more is proven over the passes after it, each resuming after the last blob proven with none before it unproven, so a blob the digest tier's byte budget left over is asked again. A pair with a proof under way is due a pass until the proof ends. A newer backup that lands first takes the proof over, from its own first blob, less what the backup last proven needed.
4. **Kept.** The ledger keeps, per destination, the backup it was last proven through, and the backup being proven with its cursor. A proven backup the catalogue no longer holds, its retention having let it go, subtracts nothing from the next.
5. **Where.** A local path's sync and an object store's.

## Consequences

- A blob a backup sent damaged is found by the pass that sent it, or by the passes after it for a backup larger than a pass's share, rather than when the rotation reaches it.
- The cost is a few ranged reads of each new blob: its footer and one record, never the whole blob, except where the digest tier proves a sealed one under its byte budget. A daily backup adds a handful of blobs. A first backup of a terabyte, at the default blob size of 64 MiB, adds some sixteen thousand, proven over 64 passes.
- An installation upgraded to this has proven no backup whole at any destination, so each destination proves its newest backup once, as a first backup is proven.
- The rotation is drawn exactly as before, and the coverage it reports is unchanged in kind; its count of what the pass proved now includes the new backup's blobs.

## Not done

- **A peer.** Its read-back proves a rotation over the keys it declared, opened whole ([ADR-0058](0058-peer-write-adapter.md) §8). Proving a new backup's blobs there, over the retrieval session and under the peer's transfer limit, is the next step.
- **A pass brought current from the metadata here** ([ADR-0198](0198-a-sync-copies-only-from-a-source-that-holds-the-newest-backup-whole.md) Amendment 1) samples nothing, so proves nothing. The next pass from a whole source proves the backup.
- **The status** does not say how far a proof under way has got, nor which backup a destination was last proven through.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built: the pass that finds a local path or a store holding the newest backup whole proves there the blobs it needs that the backup last proven did not, a share of 256 a pass beside the rotation, the pair due while a proof is under way. `Agent/NewBackupProof`; `Replication/VerificationSampler`; `Agent/FanOut`; `Agent/Scheduler`; `Application/DestinationSyncStore`; `Hosts.Tests/NewBackupProofTests` |

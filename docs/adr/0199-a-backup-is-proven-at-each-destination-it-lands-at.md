# ADR-0199 — A backup is proven at each destination it lands at

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-VER-002 (amended: the pass that finds a destination holding the newest backup whole proves there every blob that backup needs which the backup last proven there did not, beside the sample and never in its place, a bounded share a pass, the pair due until the proof is carried to its end); FR-VER-002 (amended by [Amendment 1](#amendment-1-2026-11--a-backup-is-proven-at-each-peer-it-lands-at): at a peer too, by the challenge the push makes or the read-back over the retrieval session); FR-VER-003 (amended by [Amendment 2](#amendment-2-2026-11--the-status-says-how-far-a-backups-proof-has-got): a destination's status row names the backup it was last proven through, and a proof under way with how many of the backup's blobs are proven and how many are left); FR-VER-002 (amended by [Amendment 3](#amendment-3-2026-11--a-backup-brought-current-from-the-metadata-here-is-proven-by-the-next-pass): a pass that brings a destination current from the metadata here leaves the newest backup's proof owed, so the pair is due at once and the next pass proves it); FR-VER-005 (amended by [Amendment 4](#amendment-4-2026-11--a-failed-proof-names-the-backup): a blob of the backup under proof that does not prove fails the pass naming that backup as not proven there and the newest backup that is, in the failure, its notice and the status)
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

   > **Amended 2026-11 ([Amendment 1](#amendment-1-2026-11--a-backup-is-proven-at-each-peer-it-lands-at)).** And a peer's: by the challenge its push already makes, or by the read-back where the set's content is only at the peer.

## Consequences

- A blob a backup sent damaged is found by the pass that sent it, or by the passes after it for a backup larger than a pass's share, rather than when the rotation reaches it.
- The cost is a few ranged reads of each new blob: its footer and one record, never the whole blob, except where the digest tier proves a sealed one under its byte budget. A daily backup adds a handful of blobs. A first backup of a terabyte, at the default blob size of 64 MiB, adds some sixteen thousand, proven over 64 passes.
- An installation upgraded to this has proven no backup whole at any destination, so each destination proves its newest backup once, as a first backup is proven.
- The rotation is drawn exactly as before, and the coverage it reports is unchanged in kind; its count of what the pass proved now includes the new backup's blobs.

## Not done

- **A peer.** Its read-back proves a rotation over the keys it declared, opened whole ([ADR-0058](0058-peer-write-adapter.md) §8). Proving a new backup's blobs there, over the retrieval session and under the peer's transfer limit, is the next step.

  > **2026-11: done ([Amendment 1](#amendment-1-2026-11--a-backup-is-proven-at-each-peer-it-lands-at)).**
- **A pass brought current from the metadata here** ([ADR-0198](0198-a-sync-copies-only-from-a-source-that-holds-the-newest-backup-whole.md) Amendment 1) samples nothing, so proves nothing. The next pass from a whole source proves the backup.

  > **2026-11: done ([Amendment 3](#amendment-3-2026-11--a-backup-brought-current-from-the-metadata-here-is-proven-by-the-next-pass)).** The pass leaves the backup's proof owed, so the next pass comes at once.
- **The status** does not say how far a proof under way has got, nor which backup a destination was last proven through.

  > **2026-11: done ([Amendment 2](#amendment-2-2026-11--the-status-says-how-far-a-backups-proof-has-got)).**

## Amendment 1 (2026-11) — a backup is proven at each peer it lands at

The record left a peer as its first "not done": a peer's pass proved the rotation's sample and nothing more of a new backup. A peer is checked one of two ways, and both stood where a local path's sample did.

- **By the challenge.** A staging set's peer, and a direct-ship set's where a copy of the content is here, answer the keyed range challenge the push makes once it is acknowledged (peer-protocol 04). The challenge is judged against the bytes here.
- **By the read-back.** A set whose content is only at the peer cannot be judged against anything here. Its proof is a rotation over the keys the peer declared, opened over the retrieval session ([ADR-0058](0058-peer-write-adapter.md) §8).

**Decision.** A peer is proven as a local path and a store are. The pass that finds it holding the newest backup whole proves there every blob that backup needs which the backup last proven there did not, the same share a pass, carried over the passes after it.

- **The challenge** asks a range of each such blob beside the rotation's keys, each key once. A blob whose answer does not hold fails the pass, naming it.
- **The read-back** finds them among the keys the peer declared holding, and opens them as it opens the rotation's: a footer and one record each, or a leaf where the peer offers chunk possession, or the whole blob under the digest tier's byte budget where the records are sealed. A background pass is paced at the peer's transfer limit.
- **A pass that challenges nothing**, because the peer is excused proving or nothing here can judge its answer, and a read-back that proves nothing, record nothing of the proof.

**What it costs the peer.** A challenge is one round trip, carrying a keyed digest of a range and never the range. The read-back reads each new blob's footer and one record over the peer's link.

**Built:** `Replication/VerificationSampler`'s rotation over keys in hand takes the same request as its sampling of a listing; `Agent/ReplicationInitiator`'s challenge names the keys it proved; `Agent/FanOut`'s push asks about the new backup's blobs on both routes:

- `Hosts.Tests/PeerNewBackupProofTests`:
  - a staging set's peer is proven through the backup its push sent, and a set whose content is only at its peer through the backup its run sent;
  - on either route, a blob of the backup the proof has not reached yet, rotted at the peer in place, fails the next pass naming it, and the backup is not called proven;
  - a backup larger than a pass's share is proven at the peer over the passes after it.
- `Replication.Tests/VerificationSamplerTests`: the rotation over keys in hand asks about a new backup's blobs in key order after the cursor, up to the share, and leaves the rotation as it was.

## Amendment 2 (2026-11) — the status says how far a backup's proof has got

The record's last "not done" but one: the ledger kept the backup a destination was last proven through and a proof under way, and no surface said either. A first backup of a terabyte is proven over some sixty passes. For all that time nothing a person could read said so, nor when it had ended.

**Decision.** A destination's status row says where its proof of new backups stands (contract 1.91):

- **The backup it was last proven through.** It is named as the snapshot listing names it and dated by when it was taken. A backup the catalogue no longer holds, its retention having let it go, is named by the identity its record key carries, undated.
- **The backup whose proof is under way.** It is named the same way, with how many of the blobs it needs beyond the backup proven before it are proven and how many are left. Each pass that asks about them counts them, and a newer backup's proof counts from none. A proof begun before the ledger counted is reported as under way, uncounted, until it ends: counted from none, its whole would read short.
- **Neither.** Where no backup was proven there and none is being proven, the row carries nothing, never a destination proven through nothing.

A proof under way is progress, and every surface says it as progress. The CLI prints a line under the row: "proving the backup of 2026-11-21 09:00: 3 of 10 blobs; proven through the backup of 2026-11-20 14:03". The console draws a "Proven backup" row on the destination's card: "checking the backup of …", with the count and the backup proven before it.

**Kept.** The ledger keeps the two counts beside the proof under way (schema 15), and forgets them once the backup is proven.

**Built:** `Agent/NewBackupProof` counts as it moves the proof on; `Application/DestinationSyncStore` keeps the counts; `Agent/ServiceCommandHandler` names the backups from the catalogue; `Cli/CliApplication` prints the line; the console draws the row:

- `Hosts.Tests/NewBackupProofStatusTests`:
  - a proof under way is counted in the backup's blobs, one more each pass, the whole the same pass to pass, over as many passes as the count said;
  - once proven, the row names the backup by the listing's identity and capture time;
  - a newer backup's proof names both backups and counts only the blobs it added;
  - a proof under way before the ledger counted stays uncounted through the passes after it;
  - a backup the catalogue no longer holds is named by its identity, undated, and a destination proven through no backup with none under way reports nothing.
- `Hosts.Tests/NewBackupProofStepTests`: a resumed proof counts on, a newer one counts from none, and one under way before the ledger counted stays uncounted.
- `Application.Tests/DestinationSyncStoreTests`: the counts kept and forgotten, and a schema-14 ledger migrating uncounted.
- `Api.Tests/ContractAdditiveFieldsTests`: contract 1.91's wire names and the pre-1.91 default.
- `Cli.Tests/StatusNewBackupProofLineTests`: the CLI's line.
- `Web.DomTests/NewBackupProofDomTests`: the console's row.

## Amendment 3 (2026-11) — a backup brought current from the metadata here is proven by the next pass

The record's last "not done". Beside a peer that is away, a store that holds every blob the set's index locates is brought current from the metadata on this machine ([ADR-0198](0198-a-sync-copies-only-from-a-source-that-holds-the-newest-backup-whole.md) Amendment 1). That pass reads nothing that is a copy of the store's blobs, so it samples nothing and proves nothing. The next pass proves the backup: by then the ledger names the store whole, so the pass reads the store as its own source and opens the blobs where they sit.

But nothing made that pass come. A store already proven through an earlier backup is challenged again only when its proof's age comes due. Until then the new backup sat at the store unproven, though the store read as in sync.

**Decision.** A pass that brings a destination current from the metadata here leaves the newest backup's proof owed. It records the backup as being proven from its first blob, counted as none of its blobs proven. Only the blobs it needs beyond the backup last proven there are counted. A pair with a proof under way is due a pass (decision 3), so the next pass comes at once and proves the backup as any pass does.

- **A proof of the same backup already under way** keeps its place and its count.
- **A destination already proven through the backup** owes nothing.
- **The pass itself still proves nothing,** and records no verification.

**Built:** `Agent/FanOut`'s sync to a local path or a store leaves the proof owed when its source proves nothing:

- `Hosts.Tests/StoreBesideAPeerTests`:
  - a store brought current with the peer away owes its first backup's proof, counted from none;
  - a store proven through a first backup is brought a second from the metadata here with the peer away. The second's proof is owed, counting only the blobs it added, and a scheduler pass a minute later, with no challenge due, proves the second backup there.

## Amendment 4 (2026-11) — a failed proof names the backup

A blob of the backup under proof that did not prove failed the pass, naming the blob and nothing more. A blob's key says nothing to a person about which recovery point to distrust, nor which still stands at that destination. And a pass that failed recorded no step of the proof. A first pass that failed left the ledger and the status naming no backup under proof at all.

**Decision.** When a blob that does not prove is one the proof asked about, the failure names the backup:

- **The failure and its notice** say which backup the blob belongs to, by when it was taken and the start of its identity, and that it is not proven at the destination. They also name the newest backup proven there, or say that none is. A backup the catalogue no longer holds is named by its identity alone.
- **The ledger** moves the proof past what the pass did prove, up to the first blob it did not, and keeps that blob (schema 16). The next pass asks about it again; one that proves it clears it.
- **The status** carries that blob on the row's proof (contract 1.92). The CLI says which backup failed its proof and at which blob, and what is proven through. The console draws the failure as one, never as a check under way.

**What it does not do.** A blob that does not prove which the proof did not ask about, found by the rotation, may belong to any backup kept. Its failure names no backup, as before.

**Built:** `Agent/FanOut` names the backup on each route; `Agent/NewBackupProof` names a backup as a person reads it; `Application/DestinationSyncStore` keeps the blob; `Agent/ServiceCommandHandler` carries it on the status row; `Cli/CliApplication` and the console say it:

- `Hosts.Tests/FailedNewBackupProofTests`:
  - a damaged blob of a second backup at a local path names that backup and the first as the newest proven, in the failure, the notice, the ledger and the status;
  - one of a first backup says no backup is proven there yet;
  - a blob repaired at the destination is proven by the next pass, which forgets the failure;
  - a damaged blob of a backup already proven, found by the rotation, names none.
- `Hosts.Tests/PeerNewBackupProofTests`: on both peer routes, the rotted blob is the one that failed the proof, and the failure names its backup.
- `Application.Tests/DestinationSyncStoreTests`: the blob kept and cleared; a schema-15 ledger migrating with none.
- `Api.Tests/ContractAdditiveFieldsTests`: contract 1.92's `failed_blob`.
- `Cli.Tests/StatusNewBackupProofLineTests` and `Web.DomTests/NewBackupProofDomTests`: the CLI's line and the console's row for a failed proof.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built: the pass that finds a local path or a store holding the newest backup whole proves there the blobs it needs that the backup last proven did not, a share of 256 a pass beside the rotation, the pair due while a proof is under way. `Agent/NewBackupProof`; `Replication/VerificationSampler`; `Agent/FanOut`; `Agent/Scheduler`; `Application/DestinationSyncStore`; `Hosts.Tests/NewBackupProofTests` |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--a-backup-is-proven-at-each-peer-it-lands-at): a peer is proven as a local path and a store are, by the challenge its push makes or by the read-back where the set's content is only at the peer; decision 5 and the first "not done" noted. `Replication/VerificationSampler`; `Agent/ReplicationInitiator`; `Agent/FanOut`; `Hosts.Tests/PeerNewBackupProofTests` |
| 2026-11 | Amended | [Amendment 2](#amendment-2-2026-11--the-status-says-how-far-a-backups-proof-has-got): a destination's status row says the backup it was last proven through and how far a proof under way has got, in the backup's blobs (contract 1.91, ledger schema 15); the status "not done" noted. `Agent/NewBackupProof`; `Application/DestinationSyncStore`; `Agent/ServiceCommandHandler`; `Cli/CliApplication`; `Hosts.Tests/NewBackupProofStatusTests` |
| 2026-11 | Amended | [Amendment 3](#amendment-3-2026-11--a-backup-brought-current-from-the-metadata-here-is-proven-by-the-next-pass): a pass that brings a destination current from the metadata here leaves the newest backup's proof owed, so the pair is due at once and the next pass proves it; the last "not done" noted. `Agent/FanOut`; `Hosts.Tests/StoreBesideAPeerTests` |
| 2026-11 | Amended | [Amendment 4](#amendment-4-2026-11--a-failed-proof-names-the-backup): a blob of the backup under proof that does not prove fails the pass naming that backup and the newest proven, in the failure, its notice and the status (contract 1.92, ledger schema 16). `Agent/FanOut`; `Agent/NewBackupProof`; `Application/DestinationSyncStore`; `Hosts.Tests/FailedNewBackupProofTests` |

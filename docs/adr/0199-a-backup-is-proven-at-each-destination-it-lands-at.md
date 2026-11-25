# ADR-0199 — A backup is proven at each destination it lands at

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-VER-002 (amended: the pass that finds a destination holding the newest backup whole proves there every blob that backup needs which the backup last proven there did not, beside the sample and never in its place, a bounded share a pass, the pair due until the proof is carried to its end); FR-VER-002 (amended by [Amendment 1](#amendment-1-2026-11--a-backup-is-proven-at-each-peer-it-lands-at): at a peer too, by the challenge the push makes or the read-back over the retrieval session); FR-VER-003 (amended by [Amendment 2](#amendment-2-2026-11--the-status-says-how-far-a-backups-proof-has-got): a destination's status row names the backup it was last proven through, and a proof under way with how many of the backup's blobs are proven and how many are left); FR-VER-002 (amended by [Amendment 3](#amendment-3-2026-11--a-backup-brought-current-from-the-metadata-here-is-proven-by-the-next-pass): a pass that brings a destination current from the metadata here leaves the newest backup's proof owed, so the pair is due at once and the next pass proves it); FR-VER-005 (amended by [Amendment 4](#amendment-4-2026-11--a-failed-proof-names-the-backup): a blob of the backup under proof that does not prove fails the pass naming that backup as not proven there and the newest backup that is, in the failure, its notice and the status); FR-VER-002 (amended by [Amendment 5](#amendment-5-2026-11--the-blobs-a-backup-was-proven-through-outlive-its-deletion): a proof that ends keeps the names of the blobs its backup needed there, which the next backup's proof subtracts once the catalogue no longer holds that backup); FR-RST-003 (amended by [Amendment 6](#amendment-6-2026-11--a-restores-plan-says-when-its-backup-is-not-proven-where-it-is-read): a restore's plan read through a destination names it where the backup the plan restores is not proven there, with where that destination's proof stands); FR-RST-003 (amended by [Amendment 7](#amendment-7-2026-11--the-wizard-offers-the-newest-backup-proven-where-the-plan-reads): the wizard offers the newest backup proven where its plan reads, in place of one that is not); FR-DRL-002 (amended by [Amendment 8](#amendment-8-2026-11--a-drill-says-when-the-backup-it-drilled-was-not-yet-proven): a passing drill of a backup not yet proven where it read it states that as a limit, with how far the proof had got and the newest backup proven there); FR-RST-001 (amended by [Amendment 9](#amendment-9-2026-11--the-command-line-reads-a-destinations-copy-and-says-its-proof): the command line reads a restore and its plan through a destination's copy with `--from`, and the plan says when its backup is not proven there); FR-RST-008 (amended by [Amendment 10](#amendment-10-2026-11--a-resume-reads-a-destinations-copy-too): a resume by its run's name reads a destination's copy with `--from`, its plan saying when the run's backup is not proven there and offering no other); FR-DRL-002 (amended by [Amendment 11](#amendment-11-2026-11--a-drill-restores-the-newest-backup-proven-where-it-reads): a drill restores the newest backup proven where it reads, where the destination holds it, and says which, and that the newest was not yet proven there); FR-VER-002 (amended by [Amendment 12](#amendment-12-2026-11--a-rebuilt-catalogue-re-asks-nothing-proven-there-already): a new backup's proof subtracts the names kept for the backup proven there as well as what the catalogue lists for it, so a catalogue rebuilt without that backup, or short of its records, re-asks nothing proven there already); FR-VER-002 (amended by [Amendment 13](#amendment-13-2026-11--a-destinations-share-of-a-new-backups-proof-is-a-setting): the most of a new backup's blobs one pass proves at a destination is that destination's own share where it states one, and 256 where it does not); FR-VER-003 (amended by [Amendment 14](#amendment-14-2026-11--a-proof-under-way-says-how-many-passes-it-still-needs): a counted proof under way says at least how many more passes it needs at the destination's share); FR-VER-002 (amended by [Amendment 15](#amendment-15-2026-11--the-diagnostic-bundle-carries-a-destinations-proof-share): the diagnostic bundle's configuration carries a destination's share)
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

   > **Amended 2026-11 ([Amendment 13](#amendment-13-2026-11--a-destinations-share-of-a-new-backups-proof-is-a-setting)).** 256 is the default. A destination may state its own share, `new_backup_proof_share`, and a pass asks there about at most that many.
4. **Kept.** The ledger keeps, per destination, the backup it was last proven through, and the backup being proven with its cursor. A proven backup the catalogue no longer holds, its retention having let it go, subtracts nothing from the next.

   > **Amended 2026-11 ([Amendment 5](#amendment-5-2026-11--the-blobs-a-backup-was-proven-through-outlive-its-deletion)).** Not retention: its expiry keeps a backup's row in the catalogue, which still answers for it. The catalogue lets a backup go when a person deletes it, or when it is rebuilt without it. Such a backup subtracts the names of the blobs kept when its proof ended there.
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

  > **Amended 2026-11 ([Amendment 5](#amendment-5-2026-11--the-blobs-a-backup-was-proven-through-outlive-its-deletion)).** A person's deletion, not retention, lets a backup go from the catalogue; retention's expiry keeps its row.
- **The backup whose proof is under way.** It is named the same way, with how many of the blobs it needs beyond the backup proven before it are proven and how many are left. Each pass that asks about them counts them, and a newer backup's proof counts from none. A proof begun before the ledger counted is reported as under way, uncounted, until it ends: counted from none, its whole would read short.
- **Neither.** Where no backup was proven there and none is being proven, the row carries nothing, never a destination proven through nothing.

A proof under way is progress, and every surface says it as progress. The CLI prints a line under the row: "proving the backup of 2026-11-21 09:00: 3 of 10 blobs; proven through the backup of 2026-11-20 14:03". The console draws a "Proven backup" row on the destination's card: "checking the backup of …", with the count and the backup proven before it.

> **Amended 2026-11 ([Amendment 14](#amendment-14-2026-11--a-proof-under-way-says-how-many-passes-it-still-needs)).** A counted proof under way also says at least how many more passes it needs at the destination's share: "3 of 10 blobs, at least 4 more passes".

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
  - a damaged blob of a backup already proven, found by the rotation, names none, nor does one only an older backup needed, met while a newer is being proven.
- `Hosts.Tests/PeerNewBackupProofTests`: on both peer routes, the rotted blob is the one that failed the proof, and the failure names its backup.
- `Application.Tests/DestinationSyncStoreTests`: the blob kept and cleared; a schema-15 ledger migrating with none.
- `Api.Tests/ContractAdditiveFieldsTests`: contract 1.92's `failed_blob`.
- `Cli.Tests/StatusNewBackupProofLineTests` and `Web.DomTests/NewBackupProofDomTests`: the CLI's line and the console's row for a failed proof.

## Amendment 5 (2026-11) — the blobs a backup was proven through outlive its deletion

Decision 4 said a proven backup the catalogue no longer holds subtracts nothing from the next, and put that down to retention. The cause was wrong. Retention's expiry keeps a backup's row in the catalogue (`Agent/ServiceCommandHandler`), so the catalogue still answers for that backup, and the next backup's proof subtracts its closure as before. The catalogue lets a backup go when a person deletes it (FR-GC-013), or when it is rebuilt without it.

The consequence was right. A deletion converges a local path and a peer before it forgets the backup, and that pass proves the newest backup against the one being deleted. But the proof does not always end in that pass. A backup larger than a pass's share is proven over the passes after it, and a store is converged by its next sync, not by the deletion. Either way, once the catalogue has let the deleted backup go, the ledger still names it as proven there. The next pass then asked about every blob the newest backup needs, including those the deleted backup was proven through. At a peer that is an archive's worth over the peer's link, for blobs proven there already.

**Decision.**

- **Kept when a proof ends.** A proof that ends keeps the names of the blobs its backup needed at that destination. They sit in one file per set and destination in the state directory, replaced when a newer backup's proof ends there.
- **Subtracted where the catalogue cannot answer.** The next backup's proof subtracts the closure of the backup last proven there, from the catalogue, where the catalogue holds that backup, as before. Where it does not, the proof subtracts the names kept for that backup. It subtracts nothing where none were kept, where the file was kept for another backup, or where it does not read.

  > **Amended by [Amendment 12](#amendment-12-2026-11--a-rebuilt-catalogue-re-asks-nothing-proven-there-already).** The kept names are subtracted wherever they were kept for that backup, beside whatever the catalogue lists for it, not only where it lists nothing: a catalogue rebuilt short of the backup's records lists only part of what it needed.
- **Best effort.** Names that cannot be kept cost only a fuller proof later, never the pass that ended the proof.

**What it does not do.** A destination proven before this was built has no names kept, so its next proof after a deletion is as full as before. Every proof that ends from now on keeps them.

**Built:** `Agent/ProvenBlobsStore` keeps the names; `Agent/FanOut` keeps them on each route when a proof ends; `Agent/NewBackupProof` subtracts them:

- `Hosts.Tests/ProvenBlobsAfterDeletionTests`:
  - a proof that ends keeps the names of the blobs its backup needed, and gives none for another backup;
  - a vault is proven through a first backup and a second is taken. The first is deleted while the second's proof is under way. The second's proof then asks about none of the blobs the first was proven through.

## Amendment 6 (2026-11) — a restore's plan says when its backup is not proven where it is read

A destination's status row says how far its proof of new backups has got (Amendment 2). A restore planned through that destination said nothing of it. A person picks a destination as the wizard's source and plans the newest backup. The backup may have landed an hour ago, with its check a few blobs in. It read exactly like one proven there a week ago.

**Decision.** A plan read through a destination says when the backup it restores is not proven there (contract 1.93). It names the destination, and where that destination's proof stands, in the same terms as the status row. The ledger knows a backup is not proven there when:

- **it is the backup under proof there;**
- **no backup is proven there;**
- **it was taken after the newest backup proven there,** whose proof a newer backup's took over.

It holds nothing back. The backup restores as any backup does, each file checked as it is written; the person learns before the restore that no check has yet found all of it whole there. The console says it on the plan: how far the backup's own check has got, or that no check of it there has finished, and the newest backup proven there.

**What it says nothing of.**

- **A plan read from the set's own archive.** That is where every copy came from.
- **A backup proven there, or taken before the newest proven there.** The ledger keeps only the newest proven. An older backup's own proof may have ended, or have been taken over, and nothing records which.
- **A destination with no proof recorded.** On a machine set up again to restore, the ledger is new and knew nothing of that destination, so nothing is said.
- **A proven backup the destination no longer holds.** It cannot be dated, and nothing is dated against it.

The service's own words for a backup the catalogue no longer holds are corrected as Amendment 5 corrected this record's: a person's deletion lets it go, not retention.

**Built:** `Agent/NewBackupProof` holds the rule; `Agent/ServiceCommandHandler` applies it to a plan read through a destination; the console draws it:

- `Hosts.Tests/RestorePlanProofTests`:
  - a vault proven one blob a pass. A second backup under proof there is said not proven, beside the backup proven there and the count;
  - so is a second backup whose proof a third's took over;
  - the backup proven there, one taken before it, and a plan read from the set's own archive are said nothing of;
  - the rule itself, including nothing said where no proof is recorded or the proven backup cannot be dated.
- `Api.Tests/ContractAdditiveFieldsTests`: contract 1.93's `not_proven_at` and the pre-1.93 default.
- `Web.DomTests/RestoreWizardDomTests`:
  - the wizard counts a check as this backup's only when it is, and says the newest backup proven there;
  - it names none where none is, and the warning holds nothing back.

## Amendment 7 (2026-11) — the wizard offers the newest backup proven where the plan reads

A plan read through a destination says when the backup it restores is not proven there, and names the newest backup that is (Amendment 6). A person who reads that wants the proven backup, and the wizard made them find it themselves. They went back to the date, guessed the day it was taken, and ticked the files again.

**Decision.** Where the warning names a proven backup the source holds, the wizard offers it: "Choose the backup of … instead". Choosing it goes back to the files of that backup, with a note that it is the newest backup proven at that destination. What was ticked stays ticked where that backup holds it, and the output and its policy stay as they were. Continuing plans the proven backup as any plan is made.

- **Back at the files, not straight to a plan.** A selection is compiled from the folders of the backup it is made in. Planning the proven backup without showing them would drop a ticked file it does not hold, and say nothing of it.
- **Only where the source holds it.** A proven backup the source no longer holds cannot be chosen from it, and is not offered.
- **Only for a fresh plan.** A resume restores what its run began, from the backup it began with, and offers no other.

**Built:** the console's restore wizard:

- `Web.DomTests/RestoreWizardDomTests`:
  - choosing the offered backup returns to its files with the file ticked, and the plan made next is of the proven backup, with the same files and no warning;
  - no offer where the source does not hold the proven backup;
  - no offer on a resume's plan, which still says the warning.

## Amendment 8 (2026-11) — a drill says when the backup it drilled was not yet proven

A destination's restore drill ([ADR-0054](0054-scheduled-restore-drills.md)) opens its copy, takes the newest backup there, and restores a sample of its files. A drill that passes proves those files come back. It does not prove every blob the backup needs is whole at that destination: that is what this record's proof does, and for a large backup the proof runs over many passes. A drill of the newest backup while its proof was still under way read as a plain pass, inviting the reading that the backup was proven there.

**Decision.** A passing drill whose backup the ledger knows was not proven at the destination states it as a limit.

> **Amended by [Amendment 11](#amendment-11-2026-11--a-drill-restores-the-newest-backup-proven-where-it-reads).** Where an older backup is proven there and the destination still holds it, the drill no longer drills the newest while its proof is under way: it restores the proven one, and its limit names both. This note stands for the drill of the newest, where no backup is proven there yet or the one proven is no longer held. The rule is the one a restore's plan follows (Amendment 6). The note goes beside the drill's other stated limits (sealed content, archived content, a peer's byte cap), so every surface that shows a drill's limit shows it: the ledger row, the drill's report line, the status row, the console and the diagnostic bundle. It reads, in the past tense because the limit stands until the next drill and the proof may end before then: "The backup drilled, the backup of … UTC (…), was not yet proven here: its check had proven 3 of 10 blobs; the newest backup proven here was the backup of … UTC (…)."

- **Only a passing drill that proved files.** A drill that fails states its failure alone, and one of a backup with no file states nothing.
- **Named from the set's own catalogue.** A backup the catalogue does not hold is not dated against, and a lookup that fails states nothing rather than fail the drill.
- **No notice.** A limit raises none, as before; only a failed drill does.

**The console's drill cell.** It drew any drill with a limit as "proved to the sealed content", which only a write-only set's drill is. It now draws that label only for the sealed-content limit. Any other limit (archived content, a byte cap, this note) is the drill's own words beside the files it restored.

**Built:** `Agent/RecoveryDrillJob` states the limit; the console's drill cell draws it:

- `Hosts.Tests/DrillProofTests`:
  - a drill of a backup whose proof is under way records the note, with the count and the newest proven backup, on the ledger row, in the report line and on the status row;
  - a drill after the proof ends states nothing of it;
  - a drill that fails states its failure alone.
- `Web.DomTests/NewBackupProofDomTests`:
  - the note is drawn beside the files restored, never as sealed content;
  - a write-only set's drill is still drawn as proved to the sealed content.

## Amendment 9 (2026-11) — the command line reads a destination's copy and says its proof

A restore's plan read through a destination says when the backup it restores is not proven there (Amendment 6), and the console's wizard says so. The command line never could. Its restore and plan always read the set's own archive, so a person at a terminal had no way to read a destination's copy, and so no plan that would say.

**Decision.** `restore --from <destination>` reads the run, and its plan, through that destination's copy, as the console's source step opens it: through the service, under the grant the passphrase derives. Without `--from`, a restore reads the set's own archive as before.

- **The plan says it.** Where the service says the backup is not proven at the destination, the plan prints a "not proven:" line, worded as the console words it. The line counts the check only when the check under way is this backup's, and names the newest backup proven there. A "proven there:" line names the restore of that backup with `--from`.
- **It holds nothing back.** The plan's outcome and exit are as they were. The restore still checks each file as it writes it.
- **A destination the service refuses** is refused in the service's own words, with nothing planned or run.
- **Direct mode** opens no source, so `--from` is refused there before anything is read. A person points `--repo` at the destination's copy instead.

**What it does not do.** A resume, whether `restore --resume` or `restore-resume`, still reads the set's own archive, as its run was asked.

> **Amended by [Amendment 10](#amendment-10-2026-11--a-resume-reads-a-destinations-copy-too).** This held for `restore-resume` only: `restore --resume` took `--from` as any restore does, and its plan offered the backup proven there, which a resume cannot take. `restore-resume` now takes `--from` too, and a resume's plan offers no other backup.

**Built:** `Cli/OperationGateway` (`RestoreRequest.From`, and both gateways), `Cli/GrantedSources`, `Cli/RestorePlanReport` and `Cli/CliApplication`:

- `Cli.Tests/GatewayRestoreReportTests`:
  - a plan from a destination opens that destination's copy and prints the line, counted and with the hint, holding the plan's outcome;
  - where a newer backup is under proof, the plan says no check of this one finished, and names nothing proven;
  - a restore from a destination runs through that copy, and one without it through the set's archive;
  - a destination the service does not declare is refused in its words.
- `Cli.Tests/RestoreFromCommandTests`: direct mode refuses `--from`, the plan and the run alike, before anything is read.

## Amendment 10 (2026-11) — a resume reads a destination's copy too

Amendment 9 ended with a resume still reading the set's own archive. That held for `restore-resume`, which opened the set's archive every time. It did not hold for `restore --resume`, which took `--from` as any restore does. Its plan then offered the backup proven there, which a resume cannot take, since a resume continues the backup its run restores. The console's resume reads through whichever source its source step chooses, and offers no other backup (Amendment 7). The service has always planned and run a resume through the source it is handed.

**Decision.** `restore-resume <run> --from <destination>` reads the run's backup from that destination's copy, for the resume and its plan, as the console's resume does. The run is asked again as it was: where a resume reads is the caller's to say each time, and is not kept with the run. A run cut short at the machine's archive can then be finished from the vault.

- **The plan says it.** As in Amendment 9, a "not proven:" line says when the run's backup is not proven at the destination, naming the newest backup proven there. No "proven there:" line offers that backup, from `restore-resume` or from `restore --resume`, since a resume continues the backup its run restores.
- **A destination the service refuses** is refused in the service's own words, before anything is resumed, and the run is left as it was.
- **`--from` beside no run, `--forget` or `--forget-all` is refused.** A list of runs and a letting go read no backup.
- **Direct mode** names no run, so `restore-resume` is refused there as before, `--from` or not.

No wire change: the service's resume and its plan already take the source they read through (contract 1.71, 1.72).

**Built:** `Cli/OperationGateway` (`ResumeRestoreAsync` and `PlanResumeRestoreAsync` take the destination, both gateways), `Cli/CliApplication` (`restore-resume --from`) and `Cli/RestorePlanReport` (no other backup offered to a resume):

- `Cli.Tests/RestoreResumeVerbTests`:
  - a resume from a destination opens that destination's copy and one without it the set's archive, each read through the source opened for it;
  - a destination the service does not declare is refused in its words, with nothing resumed;
  - `--from` beside no run, `--forget-all` or `--forget` is refused.
- `Cli.Tests/GatewayRestoreReportTests`: a run left unfinished, planned from a destination by its name alone, says its backup is not proven there and offers no other; so does `restore --resume --plan --from`.
- `Hosts.Tests/RestoreUnfinishedServiceTests`: through the command line and a running service, a run cut short is refused from an undeclared destination, plan and resume alike, and left as it was. With the vault holding the backup and the set's own archive spoiled, `restore-resume --from vault` plans the resume and then finishes it.

## Amendment 11 (2026-11) — a drill restores the newest backup proven where it reads

A destination's drill restored a sample of the newest backup there (ADR-0054 §7). While that backup's proof was under way, as it is over the passes a large backup takes, the drill read a backup whose blobs were not yet known whole there. Amendment 8 could only add that it was not yet proven. The backup proven there is the one whose every blob the destination is known to hold whole, so it is the one a drill's samples speak for.

**Decision.** A drill restores the newest backup proven at the destination, as the ledger names it when the drill begins, wherever the destination still holds it. Where that is not the newest there, the drill restored another backup than the newest, and says so. Its limit says so, in the past tense as Amendment 8's does: "The newest backup, the backup of … UTC (…), was not yet proven here: its check had proven 3 of 10 blobs; the drill restored the newest backup proven here, the backup of … UTC (…)." The clause on the check is the one Amendment 8 uses, and is absent where the newest is not the backup under proof.

- **Where the newest is proven**, the drill restores it and states nothing of its proof, as before.
- **Where nothing is proven there yet**, or the destination no longer holds the backup proven there, the drill restores the newest, and Amendment 8's note says whatever the ledger knows of its proof.
- **Never unsaid.** A drill that restored another backup than the newest says so. A backup the set's catalogue does not hold is named by its identity.
- **Older snapshots trimmed by a destination's retention** (ADR-0054 §7) do not reach this: the backup proven there is the newest proven, which retention keeps, and one the destination no longer holds is never chosen.

**Built:** `Agent/RecoveryDrillJob` chooses the backup and states the limit:

- `Hosts.Tests/DrillProofTests`:
  - where the newest backup's proof is under way, the drill restores the newest backup proven there, and its limit names both on the ledger row, in the report line and on the status row;
  - where the newest is proven, the drill restores it and states nothing of its proof;
  - where nothing is proven yet, the drill restores the newest and says no backup was proven here yet;
  - where the destination no longer holds the backup proven there, the drill restores the newest.

  Which backup was restored is read from the restores the drill asks of a recording service wrapped round its own handler.

## Amendment 12 (2026-11) — a rebuilt catalogue re-asks nothing proven there already

Amendment 5 said the kept names answer where the catalogue no longer holds the backup proven there: a person deleted it, "or it was rebuilt without it". Only the deletion was tested. The subtraction also took the kept names only where the catalogue listed nothing of that backup. A set's catalogue is rebuilt from its archive when the file is new, of another schema, or healed from a destination (FR-MAN-002), and a rebuild that cannot read every record it needs comes out short. Such a catalogue can keep the proven backup's row while listing only part of the blobs it needed. The next backup's proof then subtracted only that part, and asked again about blobs already proven there.

**Decision.** A new backup's proof takes away both the blobs the catalogue lists for the backup proven there and the names kept when that backup's proof ended, wherever they were kept for it. The kept names are what that backup needed whole, so taking them away beside a full list changes nothing, and beside a short or missing one it takes away what the list lost. Nothing else changes: names kept for another backup, or a file that does not read, still say nothing, and the proof then asks about what the catalogue does not list.

**Built:** `Agent/NewBackupProof`'s `OwedOf` draws the subtraction out of the catalogue lookup:

- `Hosts.Tests/ProvenBlobsAfterDeletionTests`:
  - a vault is proven through a first backup and a second is taken. The set's catalogue is then rebuilt without the first, whose record is gone. The second's proof asks about none of the blobs the first was proven through;
  - where no names were kept, as for a destination proven before any were, the catalogue's own list of the backup proven there still spares its blobs.
- `Hosts.Tests/NewBackupProofStepTests`: what a proof owes spares both what the catalogue lists and what was kept, so a catalogue that lists the proven backup short re-asks nothing; each spares what it says where only one says anything.

## Amendment 13 (2026-11) — a destination's share of a new backup's proof is a setting

Decision 3 fixed the share at 256 blobs a pass at every destination. The right share is not the same everywhere. A first backup of a terabyte at the default blob size is some sixteen thousand blobs, proven over 64 passes. At a fast local disk a person may want it proven in a few. At a store that charges by the request, or a peer on a slow link, they may want it spread more thinly. Nothing could change it.

**Decision.** The share is a setting of each destination, `new_backup_proof_share`, a positive number of blobs. A destination that states none takes the default of 256. Each pass that proves a new backup at a destination asks there about at most its share:

- a local path's or a store's sync;
- a staging set's push to a peer;
- the read-back of a set whose content is only at the peer.

The configuration refuses a share of zero or less at load, naming the field: a pass that proved none would keep the pair due with the proof never moving on.

The setting travels as the drill cadence does (contract 1.94). The destination descriptor carries it. On an upsert, null keeps what the file says, zero removes it, and a negative share is refused naming the field. `destination-settings --proof-share` sets it, and the read-out says it or the default. The console's destination form carries it beside the transfer limit and the drill cadence.

Nothing else changes. The rotation is drawn as before, a proof under way resumes from its cursor whatever the share, and a changed share applies from the next pass.

**Built:** `Application/DestinationConfiguration` holds the setting and its default, `Application/ClientConfiguration` refuses a share of none, and `Agent/FanOut` asks each proof for the destination's share. `Agent/ServiceCommandHandler` carries it on the descriptor, and `Cli/CliApplication` sets and shows it. The tests:

- `Hosts.Tests/NewBackupProofShareTests`: a share of one proves one blob a pass at a local path, at a staging set's peer and at a peer-only set's peer; a share larger than the backup proves it in the pass that sent it, with the default narrowed to one; a destination stating none proves as far as the default.
- `Hosts.Tests/ConfigurationCommandTests`: an upsert writes and lists the share, keeps it where an edit is silent, removes it with zero and refuses a negative one naming it.
- `Hosts.Tests/ClientModeTests`: the command line sets the share keeping the destination's other settings, shows it and the default, and removes it with zero.
- `Application.Tests/ClientConfigurationTests`: a share of zero or less is refused naming it, and the field is written only where a destination states one.
- `Api.Tests/ContractAdditiveFieldsTests`: the wire name, and a pre-1.94 frame reading as no share stated.
- `Web.Tests/SettingsRelayNamesTests`, `Web.DomTests/ConfigEditingDomTests`: the console reads the share, sends it, and sends the zero that clears it.

## Amendment 14 (2026-11) — a proof under way says how many passes it still needs

Amendment 2 counted a proof under way in the backup's blobs. Amendment 13 let each destination state its own share a pass, and with it the blobs left stopped saying how long the proof would take. 900 blobs left is four passes at the default and nine hundred at a share of one, and nothing said which.

**Decision.** A counted proof under way says at least how many more passes it needs: the blobs left over the destination's share a pass, rounded up (contract 1.95, `passes_left`).

- **"At least".** A pass proves no more than the share. It proves fewer where the digest tier's byte budget leaves some over, so the proof can take more passes, never fewer.
- **The share.** It is the destination's own where it states one, and the default where it does not or is no longer declared.
- **No count.** A proof nothing counted carries none, and so does one that has ended.
- **Both surfaces.** The status row and a restore plan's word on the proof carry the count alike.

The CLI's line says "3 of 10 blobs, at least 4 more passes", one pass in the singular, and nothing where none is left. The console's card says the same beside the count. A failed proof says no passes: its next pass asks the failed blob again, and the line says it failed.

**Built:** `Agent/ServiceCommandHandler` counts the passes at `Agent/FanOut`'s share for the destination, `Cli/CliApplication` prints them, and the console draws them. The tests:

- `Hosts.Tests/NewBackupProofStatusTests`:
  - the passes left are the blobs left over the share, rounded up, a count at the top of an int's range included;
  - a proof nothing counted, and one that has ended, has none;
  - a vault whose own share is one counts as many passes as blobs left, where the default would have said one.
- `Hosts.Tests/RestorePlanProofTests`: a plan's word on the proof carries the same count.
- `Cli.Tests/StatusNewBackupProofLineTests`: the line, the singular, none for none, and none for a failed proof.
- `Web.DomTests/NewBackupProofDomTests`: the card, the same cases.
- `Api.Tests/ContractAdditiveFieldsTests`: the wire name, and a pre-1.95 frame reading as none counted.

## Amendment 15 (2026-11) — the diagnostic bundle carries a destination's proof share

Amendment 13 made the share a setting of each destination. A proof that is taking a long time is a question a person sends a diagnostic bundle to have answered ([ADR-0081](0081-diagnostic-bundle.md)), and the bundle's configuration entry projected each destination's drill cadence and transfer limit but not its share.

**Decision.** The bundle's configuration entry carries each destination's `new_backup_proof_share`: the number where the destination states one, and null where it states none and the default applies. A share is a count with nothing in it to withhold, so it is rendered as the entry's other numbers are, with no redaction of its own (NFR-PRIV-003 is unchanged).

**Built:** `Agent/DiagnosticBundle` projects the share. `Hosts.Tests/DiagnosticBundleTests`: the configuration entry carries a stated share, and null for none.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built: the pass that finds a local path or a store holding the newest backup whole proves there the blobs it needs that the backup last proven did not, a share of 256 a pass beside the rotation, the pair due while a proof is under way. `Agent/NewBackupProof`; `Replication/VerificationSampler`; `Agent/FanOut`; `Agent/Scheduler`; `Application/DestinationSyncStore`; `Hosts.Tests/NewBackupProofTests` |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--a-backup-is-proven-at-each-peer-it-lands-at): a peer is proven as a local path and a store are, by the challenge its push makes or by the read-back where the set's content is only at the peer; decision 5 and the first "not done" noted. `Replication/VerificationSampler`; `Agent/ReplicationInitiator`; `Agent/FanOut`; `Hosts.Tests/PeerNewBackupProofTests` |
| 2026-11 | Amended | [Amendment 2](#amendment-2-2026-11--the-status-says-how-far-a-backups-proof-has-got): a destination's status row says the backup it was last proven through and how far a proof under way has got, in the backup's blobs (contract 1.91, ledger schema 15); the status "not done" noted. `Agent/NewBackupProof`; `Application/DestinationSyncStore`; `Agent/ServiceCommandHandler`; `Cli/CliApplication`; `Hosts.Tests/NewBackupProofStatusTests` |
| 2026-11 | Amended | [Amendment 3](#amendment-3-2026-11--a-backup-brought-current-from-the-metadata-here-is-proven-by-the-next-pass): a pass that brings a destination current from the metadata here leaves the newest backup's proof owed, so the pair is due at once and the next pass proves it; the last "not done" noted. `Agent/FanOut`; `Hosts.Tests/StoreBesideAPeerTests` |
| 2026-11 | Amended | [Amendment 4](#amendment-4-2026-11--a-failed-proof-names-the-backup): a blob of the backup under proof that does not prove fails the pass naming that backup and the newest proven, in the failure, its notice and the status (contract 1.92, ledger schema 16). `Agent/FanOut`; `Agent/NewBackupProof`; `Application/DestinationSyncStore`; `Hosts.Tests/FailedNewBackupProofTests` |
| 2026-11 | Amended | [Amendment 5](#amendment-5-2026-11--the-blobs-a-backup-was-proven-through-outlive-its-deletion): a proof that ends keeps the names of the blobs its backup needed at that destination, and the next backup's proof subtracts them where the catalogue no longer holds that backup, a person having deleted it; decision 4's cause corrected, retention's expiry keeping the backup's row. `Agent/ProvenBlobsStore`; `Agent/FanOut`; `Agent/NewBackupProof`; `Hosts.Tests/ProvenBlobsAfterDeletionTests` |
| 2026-11 | Amended | [Amendment 6](#amendment-6-2026-11--a-restores-plan-says-when-its-backup-is-not-proven-where-it-is-read): a restore's plan read through a destination names it where the backup it restores is not proven there, with where that destination's proof stands (contract 1.93), and the console says so before the restore, holding nothing back. `Agent/NewBackupProof`; `Agent/ServiceCommandHandler`; `Hosts.Tests/RestorePlanProofTests`; `Web.DomTests/RestoreWizardDomTests` |
| 2026-11 | Amended | [Amendment 7](#amendment-7-2026-11--the-wizard-offers-the-newest-backup-proven-where-the-plan-reads): where a plan says its backup is not proven where it reads, the wizard offers the newest backup proven there that the source holds; choosing it returns to that backup's files with the ticks kept, and a resume offers no other backup. `Web.DomTests/RestoreWizardDomTests` |
| 2026-11 | Amended | [Amendment 8](#amendment-8-2026-11--a-drill-says-when-the-backup-it-drilled-was-not-yet-proven): a passing drill of a backup the ledger knows was not yet proven where it read it states so as a limit, with how far its proof had got and the newest backup proven there; the console draws a limit as sealed content only where it is. `Agent/RecoveryDrillJob`; `Hosts.Tests/DrillProofTests`; `Web.DomTests/NewBackupProofDomTests` |
| 2026-11 | Amended | [Amendment 9](#amendment-9-2026-11--the-command-line-reads-a-destinations-copy-and-says-its-proof): `restore --from <destination>` reads the run and its plan through that destination's copy, through the service, and the plan prints when its backup is not proven there with the newest proven backup to restore instead; direct mode refuses `--from`. `Cli/OperationGateway`; `Cli/GrantedSources`; `Cli/RestorePlanReport`; `Cli.Tests/GatewayRestoreReportTests`; `Cli.Tests/RestoreFromCommandTests` |
| 2026-11 | Amended | [Amendment 10](#amendment-10-2026-11--a-resume-reads-a-destinations-copy-too): `restore-resume <run> --from <destination>` resumes, and plans the resume of, a run left unfinished from that destination's copy; a resume's plan offers no other backup, `restore --resume` included; Amendment 9's note on a resume corrected with a scoped note. `Cli/OperationGateway`; `Cli/CliApplication`; `Cli/RestorePlanReport`; `Cli.Tests/RestoreResumeVerbTests`; `Cli.Tests/GatewayRestoreReportTests`; `Hosts.Tests/RestoreUnfinishedServiceTests` |
| 2026-11 | Amended | [Amendment 11](#amendment-11-2026-11--a-drill-restores-the-newest-backup-proven-where-it-reads): a drill restores the newest backup proven at the destination wherever it still holds it, and where that is not the newest its limit says the newest was not yet proven there and which backup it restored; elsewhere it restores the newest, Amendment 8's note standing. `Agent/RecoveryDrillJob`; `Hosts.Tests/DrillProofTests` |
| 2026-11 | Amended | [Amendment 12](#amendment-12-2026-11--a-rebuilt-catalogue-re-asks-nothing-proven-there-already): a new backup's proof subtracts the names kept for the backup proven there beside whatever the catalogue lists for it, so a catalogue rebuilt without that backup, or short of its records, re-asks nothing proven there already; the rebuild Amendment 5 named is tested. `Agent/NewBackupProof`; `Hosts.Tests/ProvenBlobsAfterDeletionTests`; `Hosts.Tests/NewBackupProofStepTests` |
| 2026-11 | Amended | [Amendment 13](#amendment-13-2026-11--a-destinations-share-of-a-new-backups-proof-is-a-setting): the most of a new backup's blobs one pass proves at a destination is that destination's own share where it states one, and 256 where it does not, set through the service (contract 1.94), the command line and the console, a share of none refused; decision 3 noted. `Application/DestinationConfiguration`; `Agent/FanOut`; `Agent/ServiceCommandHandler`; `Cli/CliApplication`; `Hosts.Tests/NewBackupProofShareTests` |
| 2026-11 | Amended | [Amendment 14](#amendment-14-2026-11--a-proof-under-way-says-how-many-passes-it-still-needs): a counted proof under way says at least how many more passes it needs, the blobs left over the destination's share rounded up, on the status row and a restore plan (contract 1.95), the CLI's line and the console's card; Amendment 2's surfaces noted. `Agent/ServiceCommandHandler`; `Agent/FanOut`; `Cli/CliApplication`; `Hosts.Tests/NewBackupProofStatusTests`; `Cli.Tests/StatusNewBackupProofLineTests` |
| 2026-11 | Amended | [Amendment 15](#amendment-15-2026-11--the-diagnostic-bundle-carries-a-destinations-proof-share): the diagnostic bundle's configuration entry carries each destination's share, null where none is stated. `Agent/DiagnosticBundle`; `Hosts.Tests/DiagnosticBundleTests` |

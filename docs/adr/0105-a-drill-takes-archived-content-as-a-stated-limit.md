# ADR-0105 — A drill takes archived content as a stated limit

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-DRL-002 (amended: at a store with an archival tier, a sampled file whose content the store holds only there is proved as far as that content and stated as a limit, never read and never failed)
**Related:** [ADR-0054](0054-scheduled-restore-drills.md) (the drill, and Amendment 2's pass with a stated limit), [ADR-0104](0104-a-replicas-readers-take-an-archived-object-as-archived.md) (the replica's other readers, the one notice for the pair, and the record that left this owed), [ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md) (the plan that names what is archived), [ADR-0089](0089-a-backups-file-names-need-the-passphrase.md) Amendment 1 (a drill's words name no file), [architecture 08 §6](../architecture/08-restore-and-recovery.md#6-what-must-survive-a-clean-machine)

**Built:**
- The drill: `Agent/RecoveryDrillJob` plans each sampled file before it restores it, at a destination whose store has an archival tier. A file the plan names archived is not read: it is counted proved as far as its content, the drill passes with a limit saying how many, and `Agent/ArchivedReplicaNotice` says it in the pair's one notice. A file the plan finds an object missing for fails the drill, as before, in words that name no file.
- The tests:
  - `Hosts.Tests/ObjectStoreDestinationTests`, run by `Hosts.Tests/S3DestinationTests` and `Hosts.Tests/AzureBlobDestinationTests` — a store holding the sampled content only archived passes its drill under a stated limit, asks for none of that content, raises no drill notice and raises the archived one; a store missing the content fails its drill, naming no file; a store holding everything readable states no archived limit
  - `Hosts.Tests/RecoveryDrillTests` — a write-only set whose plan finds objects missing fails its drill, naming no file, though every restore stopped at sealed content

---

## Context

[ADR-0104](0104-a-replicas-readers-take-an-archived-object-as-archived.md) taught a sync's read-back and the deep sweep to take an object the store holds only in an archival tier as archived. It left the drill as it was, and said so.

A drill restores a sample of files from the destination's own replica. At a store whose lifecycle rule had archived the data blobs, every sampled file's restore was refused. The drill recorded a failure and raised its notice, the loudest this product has, saying it "could not bring back a file". It said so on every drill, for a replica that held everything it was sent.

Three facts shaped the answer.

- **A pass with a stated limit already exists.** On a write-only set the drill proves the road back only as far as the sealed content, and records a pass with a limit ([ADR-0054](0054-scheduled-restore-drills.md) Amendment 2). That limit is earned only where nothing else stands between the drill and the bytes.
- **A plan already tells archived from missing.** It asks the store, by a HEAD of each object, and names what every copy holds only archived apart from what is missing ([ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md)).
- **A refused restore says nothing a drill could trust.** The refusal reaches the drill as the provider's words in a failure line. A file with one archived segment and one rotted one could fail on either. Reading the words afterwards would risk turning damage the drill did find into a limit.

## Decision

### 1. At a store with an archival tier, the drill plans first

Before it restores a sampled file, a drill of an S3-compatible or Azure Blob destination plans it. The plan costs a HEAD of each object the file needs, which the restore then reads from.

- A file the plan names **archived**, with nothing missing, is not restored. Nothing of it is asked for, and the drill moves to the next file.
- A file the plan finds an object **missing** for fails the drill. It is damage, however the drill finds it.
- Every other file is restored as before. On a write-only set, the plan already made is the one Amendment 2's limit asks for.

A local path and a peer have no archival tier, so their drills restore first, as before.

### 2. An archived file is proved as far as its content

The replica opens, its index and catalogue rebuild, and the store holds every object the file needs. That is the road back proved as far as the content, as for a write-only set, and the file is counted among those proved.

The drill passes with a stated limit. The limit says how many sampled files the store holds only archived, how far the road back to them was proved, and that a restore of them needs them rehydrated first. No bytes are counted for them. The drill's notice is not raised, and one a failed drill raised is resolved, as for any pass.

### 3. The drill is one of the replica's readers

A drill that met archived content says it in the pair's one notice ([ADR-0104](0104-a-replicas-readers-take-an-archived-object-as-archived.md) §3). The notice is dated from when the run of such meetings began. As for a read-back, a drill ends no run, because it samples a few files and a circuit reads every blob.

### 4. A drill's words name no file

A plan names what is missing by path. The drill's failure says that a sampled file is missing objects at the replica, and the path is kept beside the notice for whoever unlocks the set with its passphrase ([ADR-0089](0089-a-backups-file-names-need-the-passphrase.md) Amendment 1). Before this record, the write-only set's failure named the first missing path.

## Consequences

- A destination whose store archives a backup's older blobs keeps a passing drill. Its row says how much of the sample was archived, and its notice for archived objects says the rest.
- A drill at a store asks a HEAD of each object a sampled file needs before it reads the file. That is a request per object, beside the reads of the file itself.
- A drill whose every sampled file is archived proves no content at all, and says so in its limit rather than in a plain pass.

## What this does not do

- **Draw the sample around archived content.** Knowing which files are archived takes a plan of each, and a drill draws a few files at random. A drill that met only archived files states that, and the next draws again.
- **Read around to another copy.** A drill reads the destination's own replica, opened as a stranger would open it. Staging holding the content readable proves nothing about this destination.
- **Take an archived index or metadata object as archived.** The replica opens before any file is planned, and the plan answers for what a file's own records and content need. What a drill makes of a replica whose index a rule archived is unchanged by this record.

## Alternatives considered

- **Leave it a failure.** Rejected. The notice tells a person a destination cannot bring a file back, when the destination holds every byte under a rule its owner set.
- **Read the failure lines afterwards.** Rejected. The provider's words would be matched as text. A file that also met damage could be passed off as archived.
- **Plan every drill's files, at every kind.** Rejected. A local path and a peer have no archival tier, and at a peer every HEAD crosses somebody else's link.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. At a store with an archival tier a drill plans each sampled file first; a file the store holds only archived is proved as far as its content and stated as a limit, never read and never failed, and the drill says it in the pair's notice for archived objects. A missing object still fails the drill, in words that name no file. |

# ADR-0112 — A store that archives a replica's metadata restores nothing

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-DEST-020 (new: a cloud destination whose store holds an object of the replica's own, not a data blob, only in an archival tier says that nothing restores from it until the object is rehydrated, and what a lifecycle rule must leave served)
**Related:** [ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md) (an object held only in an archival tier, and a restore's plan naming what it costs), [ADR-0104](0104-a-replicas-readers-take-an-archived-object-as-archived.md) (a replica's readers take an archived object as archived, the record that left the other objects to whatever reads them), [ADR-0105](0105-a-drill-takes-archived-content-as-a-stated-limit.md) (the drill), [ADR-0091](0091-an-s3-compatible-destination.md), [ADR-0093](0093-an-azure-blob-destination.md), [architecture 05 §3](../architecture/05-storage-providers.md#3-capabilities)

**Built:**
- The look: `Agent/FanOut` asks an object store, after each sync's copy lands, whether it holds the replica's `repository-format` only archived, and the object last found so if that is another, with a head each.
- The readers: the sync's read-back and the deep sweep (`Agent/ReplicaSweepJob`) tell an archived metadata blob from an archived data blob, through `Agent/ArchivedMetadataNotice`.
- The record: `Application/DestinationSyncStore` (schema 13) keeps the first such object met and when, until that object is found served again.
- The notice: `Agent/ArchivedMetadataNotice`, apart from the data notice `Agent/ArchivedReplicaNotice`.
- The wire: contract 1.70's `DestinationStatusDescriptor.ArchivedMetadata` and `ArchivedMetadataSince`, filled by `Agent/ServiceCommandHandler`.
- The surfaces: `Cli/CliApplication` prints a line under the row, and the console's destination card carries a badge on its summary and the object in its body.
- The tests:
  - `Hosts.Tests/ObjectStoreDestinationTests`, run by `Hosts.Tests/S3DestinationTests` and `Hosts.Tests/AzureBlobDestinationTests` — a sync to a store that archived the replica's format names it with one head a pass, raises the notice and prints the line, and clears both once it is rehydrated; a sweep past archived data blobs names nothing and past an archived metadata blob names it; a store that archives every blob as it lands is named by the first read-back
  - `Application.Tests/DestinationSyncStoreTests` — schema 13: a schema-12 row migrates with none; the first object stands until that object is served, and a later meeting is news again
  - `Api.Tests/ContractAdditiveFieldsTests`, `Api.Tests/ConfigurationContractTests` — the wire names and the pre-1.70 default
  - `Cli.Tests/StatusArchivedMetadataLineTests` — the line, and none where nothing is named
  - `Web.DomTests/ArchivedMetadataDomTests`, `Web.Tests/ConsoleDestinationCardTests`, `Web.Tests/StatusRelayNamesTests` — the badge on the summary and the object in the card, none on a local path's, and the names the console reads

---

## Context

A bucket or a container can move objects to an archival tier by a lifecycle rule: past an age, under a prefix, or both. The tier costs a fraction of the standard one and serves nothing until an object is rehydrated, which takes hours. [ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md) has a restore's plan name the files whose data the store holds only archived. [ADR-0104](0104-a-replicas-readers-take-an-archived-object-as-archived.md) has the replica's other readers take such an object as archived rather than damaged. Both were written for the replica's data blobs.

ADR-0104 left the rest to whatever reads it, as before. But a rule that moves objects past an age moves everything the replica holds. The oldest object goes first: `repository-format`, which every opening of the replica reads before anything else. A rule set on `blobs/` moves the metadata blobs every backup's tree is read from. Either way no restore can open the replica: not one file, and not the set on a clean machine.

Tried against both stores, the service then said:
- **The sync:** every pass landed its copy and called the destination in sync.
- **The notice:** ADR-0104's notice, where a reader met the archived object, said "a restore from there names the files that need them". A restore that cannot open the replica names nothing.
- **A full verify:** it counted an archived metadata blob among the blobs it passed over, as it does a data blob.
- **A drill:** only a drill found out, recording that "the replica would not open". At a cloud destination a drill runs only on a cadence someone stated.

## Decision

### 1. Metadata is everything under the replica but its data blobs

A replica's data blobs, under `blobs/data/`, are the one kind of object a store may hold archived and the replica still open. Such a blob costs the files that need it, which a restore's plan names (ADR-0103). Everything else is metadata: the format, the index, the snapshots, the journal, the hints and the metadata blobs. Every opening of the replica reads some of it before it can name a file.

### 2. The sync looks, and the readers tell the two apart

- **The sync's look.** After a sync to an object store lands its copy, it asks the store, with a head, whether it holds `repository-format` only archived. The format is the oldest object the replica holds, so it is the first a rule that moves objects past an age moves. If an earlier reader found another object archived, that object is asked after too. Two heads at most a pass. A store that does not answer leaves what was recorded as it stood: the copy has landed, and the next pass asks again.
- **The readers.** When the sync's read-back or the deep sweep passes over an archived blob, and the blob is a metadata blob, the pair's row records it. An archived data blob is said as ADR-0104 says it.

### 3. The first object met stands until it is served

The ledger's row keeps the first metadata object met, and when (schema 13). That object is what a person is told to rehydrate, and what the next sync asks after. A second one met meanwhile does not take its place. Only that object found served again, or gone, clears it. Another archived object met later is then news again.

### 4. The destination says nothing restores from it

- **A notice of its own,** apart from ADR-0104's. It names the object and when, and says nothing can be restored from there, not one file and not the set on a clean machine, however in sync the copy reads. It says what to do: rehydrate the object at the store, and leave everything under the replica but `blobs/data/` out of the store's lifecycle rule.
- **The status row** names the object and when (contract 1.70), and stays in sync. The copy did land: only a restore would find it cannot be opened.
- **`status`** prints a line under the row.
- **The console's destination card** carries a "restores nothing" badge on its summary, seen without opening the card, because the circle is full and the state in sync. The card names the object and the rule.

## Consequences

- A person learns that a lifecycle rule has made a destination unrestorable on the next sync after the rule moves the replica's format, not when a restore fails.
- The guidance a person needs to write the rule is in the notice: only the data blobs may move.
- A sync to an object store costs one more request, a head, or two while an object stands archived.

## What this does not do

- **Find every archived metadata object.** The sync asks after the format, and the readers meet the metadata blobs they read. An index object a rule archived while the format stays served is met by a drill or a restore, as before. A rule that leaves the oldest object of the replica served while it moves a newer one is not one an age or the `blobs/` prefix makes.
- **Change the drill.** When the drill cannot open the replica it already records a failure that names the object, and raises its own notice.
- **Rehydrate anything.** Rehydration is asked for by a person at the store, costs money and takes hours. The service never asks for it.
- **Demote the destination's state or the set's protection.** A drill's failure does not either. The row says the copy landed, and the line beside it says what a restore would meet.
- **Read the store's lifecycle rule.** Reading it needs a permission the destination's credential is not asked to hold, and the rule says what will move, not what has.

## Alternatives considered

- **Leave it to the drill.** Rejected. A cloud destination is drilled only on a cadence someone stated, and a store can be unrestorable for months before one runs.
- **Ask after every metadata object each pass.** Rejected. A replica holds hundreds of index objects, and a head each would cost more requests a pass than the backup that made them. The format is the first an age moves.
- **Call the destination failed.** Rejected. The copy did land and holds every byte. Failed would send a person to look at the transfer, and the next sync would land it again and say failed again.
- **Read the lifecycle rule.** Rejected, as above.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A sync to an object store asks after the replica's format with a head, and the sync's read-back and the deep sweep tell an archived metadata blob from a data blob. The ledger keeps the first such object met until it is served again (schema 13), and a notice and the status row say nothing restores from the destination until it is rehydrated, and what a lifecycle rule must leave served (contract 1.70). The command line and the console say it on the destination's row. |

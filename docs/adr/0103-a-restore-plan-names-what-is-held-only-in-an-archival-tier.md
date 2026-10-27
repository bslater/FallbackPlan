# ADR-0103 — A restore plan names what is held only in an archival tier

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: the plan names what every copy holds only in an archival tier, apart from what is missing)
**Related:** [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning) (a plan reports objects held only in an archival tier requiring rehydration), [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (the plan, and the record that named this as owed), [ADR-0012](0012-storage-provider-contract.md) (the store contract whose metadata gains the fact, and whose `ArchivalTiers` hazard nobody had declared), [ADR-0091](0091-an-s3-compatible-destination.md) and [ADR-0093](0093-an-azure-blob-destination.md) (the two providers whose stores have such tiers), [ADR-0075](0075-a-restore-reads-around-damage.md) (a run reads around a copy that will not serve a record), [architecture 05 §3](../architecture/05-storage-providers.md#3-capabilities)

**Built:**
- The contract: `Storage.Abstractions/StoreResults.cs`'s `ObjectMetadata` says whether a store holds an object only in an archival tier, and `Storage.Abstractions/StoreObjectArchivedException` is the refusal of a read of one.
- The providers: `Storage.S3/S3ObjectStore` reads it from the storage class, the Intelligent-Tiering archive status and the restore status a HEAD answers with, and `Storage.AzureBlob/AzureBlobObjectStore` from the access tier. Each refuses a read of an archived object as archived, and each declares `ArchivalTiers`.
- The plan: `Restore/FirstHolderStore` passes over an archived copy for one that holds the object readable. `Restore/RestoreBlobSet` names the paths every copy holds only archived, apart from the missing ones, and counts nothing of them as read. `Agent/ServiceCommandHandler` sends them as contract 1.65's `archived_objects`, and the console's restore wizard lists them before the restore is armed.
- The tests:
  - `Storage.ContractTests/S3ObjectStoreTests` — an object in Glacier Flexible Retrieval, Deep Archive or an Intelligent-Tiering archive tier is archived, and so is one whose restore is under way; one whose restore has finished, and one never archived, are not, and the restored one reads; a read of an archived object is refused as archived, once
  - `Storage.ContractTests/AzureBlobObjectStoreTests` — a blob in the archive tier is archived, and so is one whose rehydration is pending; one rehydrated to an online tier, and one never archived, are not; a read of an archived blob is refused as archived, once
  - `Repository.Tests/RestoreArchiveTests` — the plan names files whose content, and items whose own records, are held only archived, apart from the missing; content another copy holds readable is neither, and is counted as read; content archived at every copy is named; a file with content lost is missing however much else of it is archived; a run fails an archived file saying so, and reads around an archived copy to a readable one
  - `Hosts.Tests/ObjectStoreDestinationTests`, run by `Hosts.Tests/S3DestinationTests` and `Hosts.Tests/AzureBlobDestinationTests` — a plan from the store names what it holds only archived, and the run says why those files fail
  - `Api.Tests/ContractAdditiveFieldsTests`, `Api.Tests/ConfigurationContractTests` — contract 1.65's wire name, and a pre-1.65 plan naming none
  - `Web.DomTests/RestoreWizardDomTests` — the wizard lists what is archived before the restore is armed, and says nothing where the plan names nothing

---

## Context

Both cloud stores the product writes to have a tier that costs a fraction of their standard one, and serves nothing until an object is rehydrated. An S3 bucket's lifecycle rule moves objects to Glacier Flexible Retrieval or Deep Archive, and Intelligent-Tiering moves objects nobody has read to an archive tier of its own. An Azure container's lifecycle policy moves blobs to the archive tier. Either is a line in the store's settings, set by whoever pays for the store, and a backup's old blobs are what such a rule moves first.

A rehydration is asked for by a person at the store, one object at a time or in bulk, and takes from minutes to two days. Until then a read is refused: S3 answers 403 `InvalidObjectState`, and Azure answers 409 `BlobArchived`. The object stays listed, and a HEAD of it answers with its length and its tier.

Four facts shaped the rest.

- **Nothing in the product knew.** The store contract carried `ArchivalTiers`, a hazard a store declares. No provider declared it, and no metadata said which object was archived. A restore that met one failed that file part-way through. S3's refusal read as a fault of the credential, and Azure's as a refusal of the request.
- **The plan already asks.** It asks every blob a run needs whether the store holds it, to name what is missing before anything moves ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)). The HEAD that answers that also answers whether the blob is archived.
- **An archived object is held, not lost.** Rehydrating it brings it back, which nothing does for a missing one. Naming it among the missing would send a person to the wrong remedy.
- **Another copy may hold it readable.** A set has a staging archive and destinations, and a run reads around a copy that will not serve a record ([ADR-0075](0075-a-restore-reads-around-damage.md)).

## Decision

### 1. The store contract says when an object is archived

`ObjectMetadata` gains `Archived`. It is true where the store holds the object only in a tier that serves nothing until it is rehydrated. An object whose rehydration has finished is not archived, whatever the store still calls its tier. A store with no such tier never says it. Both cloud providers declare `ArchivalTiers`.

A read of an archived object is refused with `StoreObjectArchivedException`, an `IOException` of its own. The message names the object, says the store will not serve it until it is rehydrated, and names the two remedies: rehydrate it at the store, or restore from another copy. It is not a fault of the credential and not a gap that closes on its own, so it is neither a refusal nor `StoreUnavailableException`.

### 2. Each provider reads its own API's words

- **S3.** An object is archived when one of two things is true:
  - its storage class is `GLACIER` or `DEEP_ARCHIVE`;
  - its `x-amz-archive-status` is `ARCHIVE_ACCESS` or `DEEP_ARCHIVE_ACCESS`.

  It is not archived where `x-amz-restore` says a restore has finished. Glacier Instant Retrieval is read like any other class. A 403 `InvalidObjectState` is the archived refusal.
- **Azure Blob.** A blob is archived while its `x-ms-access-tier` is `Archive`. That stays so until a rehydration has copied it back to an online tier, so a pending rehydration is still archived. A 409 `BlobArchived` is the archived refusal.

### 3. The plan names what every copy holds only archived

A plan already looks for each blob a run needs at the source, then at the set's other copies. An archived answer is passed over for a copy that holds the blob readable, as the run will read around it. An archived answer is kept only when every copy that holds the blob holds it archived.

A path any of whose records is held only archived is named as archived: a file's manifest, a segment or a stream, or a folder's own record. Missing wins: a path that needs a record no copy holds is missing, however much else of it is archived. Nothing an archived path needs is counted in what the run will read.

### 4. It crosses as `archived_objects` (contract 1.65)

The plan result gains `archived_objects`, beside `missing_objects`. It is null when there are none, and a service before 1.65 sends none. The console's restore wizard lists them, with how many there are, before the restore can be armed. It says they will fail unless they are rehydrated first.

### 5. The run is unchanged but for what it says

A run that reaches an archived record reads around it to a copy that holds it readable, as for any copy that will not serve it. With no such copy the file fails, and the receipt's detail is the provider's refusal, naming the archival tier.

## Consequences

- A restore from a store a lifecycle rule has thinned says which files need rehydrating before it starts. A person can rehydrate them first, choose another copy, or restore the rest knowingly.
- A restore read through the set's own archive does not stop at an archived destination when staging or another destination holds the blob.
- A plan costs no more requests: the HEAD it already made answers the question.
- `ArchivalTiers` is now declared by both cloud stores. Nothing yet reads it beyond `StoreCapabilities.Intersect`, which **or**s it.

## What this does not do

- **Rehydrate anything.** A rehydration is billed to the store's owner and chosen per object and per speed, and the service holds a credential only for the reads and writes a backup needs.
- **Wait for a rehydration.** A plan names what is archived now. One made after the store has rehydrated the objects names none of them.
- **Keep a backup's objects out of an archival tier, or change how the other readers of a replica take one.** Which objects move is the store's lifecycle rule, which its owner sets. A sync's read-back, the deep sweep and a drill read a replica back and meet the same refusal; this record does not change how they report it.

  > **2026-10 ([ADR-0104](0104-a-replicas-readers-take-an-archived-object-as-archived.md)).** A sync's read-back now counts an archived sample neither proven nor failed, and the deep sweep passes over an archived blob and closes its circuit. One notice for the pair says so. A drill is as it was.

  > **2026-10 ([ADR-0105](0105-a-drill-takes-archived-content-as-a-stated-limit.md)).** A drill now plans each sampled file at such a store, and takes a file whose content is archived as a stated limit rather than a failure.
- **Report an archival tier at a local path or a peer.** Neither has one.
- **Export a plan or resume one**, which architecture 08 §2 still asks for.

## Alternatives considered

- **Name archived paths among the missing.** Rejected. Missing says the content is gone, and rehydration is a remedy a person has. One list would hide which files can be brought back.
- **Ask the store to rehydrate what a plan needs.** Rejected. It would bill the store's owner without their asking, from a service given a credential for backups, and choose a speed and a cost on their behalf.
- **Leave the plan to the run's failures.** Rejected. A rehydration takes hours. The plan is the one moment it can be started before a restore needs it.
- **Name every object a provider reports in an archival storage class.** Rejected. A finished restore makes an object readable while its class still names the archive, and Glacier Instant Retrieval is an archival class that is read at once.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The store contract says when an object is held only in an archival tier, and refuses a read of one as archived. The S3 and Azure Blob providers read it from their APIs and declare `ArchivalTiers`. A plan passes over an archived copy for a readable one and names what every copy holds only archived, apart from the missing (contract 1.65), and the console's wizard lists them before a restore is armed. |
| 2026-10 | Accepted | [ADR-0104](0104-a-replicas-readers-take-an-archived-object-as-archived.md) takes up what this record left to the replica's other readers: a sync's read-back and the deep sweep take an archived object as archived, and say so in one notice for the pair. |

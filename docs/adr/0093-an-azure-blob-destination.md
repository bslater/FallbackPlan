# ADR-0093 — An Azure Blob destination

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-DEST-005 (amended: `azure-blob` is served), FR-REP-002 (amended: its Azure half, so met), NFR-PRIV-001 (amended: a second HTTP client, named and confined as the first), NFR-SEC-009 (amended: the credential verb's envelope carries an account key or a shared access signature too); keeps NFR-SEC-006, NFR-SEC-012 and NFR-OPS-003
**Related:** [ADR-0091](0091-an-s3-compatible-destination.md) (the S3-compatible destination, whose every decision this one follows where the two APIs allow it), [ADR-0012](0012-storage-provider-contract.md) (the store contract as the provider seam), [ADR-0034](0034-hub-and-spoke-destinations.md) §5 (the cloud kinds reserved until a provider exists), [ADR-0042](0042-write-only-repositories.md) decision 6 (the sealed envelope), [ADR-0027](0027-services-scheduling-status-telemetry.md) §3 and [ADR-0043](0043-structured-logging-and-diagnostics.md) (the HTTP clients in the build), [ADR-0035](0035-destination-fitness.md) Amendment 2 and [ADR-0054](0054-scheduled-restore-drills.md) Amendment 3 (cadences on a link somebody else pays for), [ADR-0061](0061-adopt-a-destinations-archives.md) (adoption), [architecture 05](../architecture/05-storage-providers.md), [threat model T-6 and T-19](../threat-model.md#t-6-deletion-by-compromised-store-credentials)

**Built:**
- The provider: `Storage.AzureBlob/AzureBlobObjectStore` over the platform's HTTP client, `Storage.AzureBlob/AzureBlobRequestSigner`, `Storage.AzureBlob/AzureBlobLocation`, `Storage.AzureBlob/AzureBlobCredentials`, `Storage.AzureBlob/AzureBlobStoreUnreachableException`.
- What the two object stores share, in the contract's assembly: `Storage.Abstractions/IPrefixedObjectStore` and `Storage.Abstractions/StoreUnreachableException`, which `Storage.S3/S3ObjectStore` now implements too.
- The credential: `Repository.Crypto/WriteOnlyProvisioning` seals and opens an account key and a shared access signature, `Agent/DestinationCredentialStore` holds either beside an access key, and `Agent/ServiceCommandHandler` stores it on `set_destination_credentials` (contract 1.61).
- The service: `Agent/StoreComposition` opens either object store through one method, and every site that served a bucket serves a container through it: `Agent/FanOut`, `Agent/DestinationShipSink`, `Agent/DestinationProbe`, `Agent/RecoveryDrillJob`, `Agent/Scheduler`, `Agent/ReplicaSweepJob`, `Agent/SetCopies`, and adoption and restore sources in `Agent/ServiceCommandHandler`.
- The configuration: `Application/DestinationConfiguration`, `Application/ClientConfiguration` (schema 10).
- The surfaces: `Cli/CliApplication` (`destination-credentials` with `--account-key-env` or `--sas-env`), `Web/WebConsoleHost` and `Web/ConsoleRestoreGate` (`/api/destination-credentials` takes a kind), and the console's destination editor and table.
- The tests:
  - `Storage.ContractTests/AzureBlobRequestSigningTests` — the Shared Key signature pinned to six vectors computed by the API's reference client library, from its own signing policy and given exactly the headers the provider sends; a shared access signature carried in the query as issued, with no `Authorization` header
  - `Storage.ContractTests/AzureBlobObjectStoreContractTests` — the shared contract suite against a store that speaks the API in process, once under the account key and once under a signature, and against a container named in the environment when one is
  - `Storage.ContractTests/AzureBlobObjectStoreTests` — a create of a held blob is a 409 read as `AlreadyExists`; every put carries its body's `Content-MD5`; a delete of nothing is answered in one request; a range past the end is refused as the contract says; a missing container is a fault, not an empty store; a lapsed signature is refused before it is sent; listings page from markers only the store can read
  - `Application.Tests/AzureBlobDestinationConfigurationTests` — the address round-trips at schema 10 with no secret in the file; what an `azure-blob` destination needs and what no other kind may carry; a schema-9 file loads unchanged
  - `Repository.Tests/Crypto/AzureCredentialEnvelopeTests` — each envelope opens only for the destination it was sealed for, and neither opens as the other, as an access key or as any other envelope
  - `Hosts.Tests/ObjectStoreDestinationTests`, run by `Hosts.Tests/AzureBlobDestinationTests` and `Hosts.Tests/S3DestinationTests` — every host case of ADR-0091 against a container as against a bucket; and for a container alone, a signature carried end to end and reported with its expiry, refused when it has already lapsed, and failed with nothing sent once it lapses after storing
  - `Hosts.Tests/ObjectStoreAdoptionTests`, run by `Hosts.Tests/AzureBlobAdoptionTests` and `Hosts.Tests/S3AdoptionTests` — discovery and adoption from a container, read under a signature
  - `Hosts.Tests/ClientModeTests` — the CLI seals an account key or a signature from a named variable, exactly one per call, and says a store with no cadence is never drilled
  - `Hosts.Tests/DirectShipTests`, `Hosts.Tests/DeepSweepCadenceTests` — a direct-ship set cannot ship to a container alone; a container is swept only on a stated cadence
  - `Web.Tests/DestinationCredentialsCeremonyTests`, `Web.DomTests/ConfigEditingDomTests` — the console seals either credential in its own process, and its editor and table handle a container
  - `Api.Tests/ConfigurationContractTests`, `Api.Tests/KeyMaterialConfinementTests` — the wire names at 1.61, and no new member named for key material
  - `ArchitectureTests/TelemetrySilenceTests`, `ArchitectureTests/DependencyRuleTests`, `ArchitectureTests/LoggingShapeTests` — the second HTTP client, its closure, its one composer and its event ids

---

## Context

FR-DEST-005 reserved `s3`, `azure-blob` and `dropbox` as kinds the configuration accepts and the runtime refuses, a stated incapacity rather than a failure ([ADR-0034](0034-hub-and-spoke-destinations.md) §5). FR-REP-002 asks for both S3-compatible stores and Azure Blob containers through the common abstraction, proven by the shared contract suite. [ADR-0091](0091-an-s3-compatible-destination.md) served the S3 half and named the Azure half as owed.

The Blob API is the S3 API's problem again, with different answers in places that matter.

- **The address is not a bucket.** A container lives in a storage account, and the account's name is part of the host at the public service. A store on this machine names the account as the first segment of the path instead.
- **There are two credentials worth accepting.** The account key signs every request and is never sent; it opens every container in the account. A shared access signature is issued for a container, with the permissions and lifetime the person who issued it chose, and is sent with every request as it was issued.
- **The semantics differ again.** A create of a held blob is a 409, not a 412. A range is asked in an `x-ms-range` header. A delete of nothing is a 404 in one request. A listing pages by an opaque marker, so it cannot resume after a key the caller names. A missing container is a 404 that is not about the blob.
- **The service had grown S3-only branches.** Every site ADR-0091 taught to serve a bucket asked for the `s3` kind by name.

## Decision

1. **A second provider assembly, built as the first.** `Storage.AzureBlob` speaks the Blob REST API, pinned at version 2024-11-04, over the platform's `HttpClient`, and signs each request with the API's Shared Key scheme, which is about a hundred lines checked against vectors from the API's reference client library. No package is added. It joins `Storage.S3` on the HTTP-client allowlist in `TelemetrySilenceTests`, which is now two entries long and still fails when an entry stops needing its client. It depends on the store contract and Domain alone, opens no socket of its own, and only the service composes it (`DependencyRuleTests`). It logs in 2750–2799, the half of the S3 provider's block it never used. Redirects, cookies and decompression are off, and an endpoint must be `https`, except on this machine.

2. **The contract's semantics, bridged in the provider.**
   - Every put is a create: `If-None-Match: *` and a block blob. A 409 `BlobAlreadyExists` or a 412 is `AlreadyExists`, so nothing the service writes overwrites anything.
   - Every put carries its body's `Content-MD5`, which the store checks. Content that can seek is hashed and rewound; content that cannot is spooled while it is hashed. Either way the factory is read once (05 §2.1).
   - A range is asked in `x-ms-range`. A 206 is checked against its `Content-Range`, and a range the store answers short is `RangeNotSatisfiable`, as a 416 is, which is what the contract and the other providers say.
   - A delete is one request: 202 is `Deleted`, and a 404 `BlobNotFound` is `NotFound`.
   - A 404 whose error code is `ContainerNotFound` is a fault, not an absence, so a container deleted or misspelt is never read as an empty replica.
   - Listing pages by the store's marker and is declared strongly consistent. A resume after a named key is applied as the pages arrive, because a marker cannot be made from a key.

     > **Amended 2026-10 ([ADR-0012](0012-storage-provider-contract.md#amendment-5-2026-10--the-revisit-three-providers-and-the-faults-this-record-named) Amendment 5).** The API can start a listing at a name (`startFrom`, version 2023-05-03 and later, which the pinned version is). A resume after a key now asks the store to start there and passes over the key itself, which the API includes, rather than reading the pages before it. A throttle that outlasts the attempts is a busy store, unavailable rather than failed.
   - A refusal that may not last is retried from the content already read, and a store that never answers is an `AzureBlobStoreUnreachableException`, which is a `StoreUnreachableException` and an `IOException`.
   - One request a blob, up to 5000 MiB, so block lists are not used.

   The shared contract suite passes against `TestSupport/AzureBlobTestServer`, an in-process store that checks every Shared Key signature with a verifier of its own and holds a signature to the container, permissions and expiry it issued, under each credential. It also passes against a container named in the environment (`ConfiguredAzureBlobStore`), and passed against a Blob API emulator run locally while this was built, under an account key and under a signature that emulator issued.

3. **The address in the configuration.** Schema 10 gives an `azure-blob` destination `account` and `container`, the `prefix` an `s3` one has, and an optional `endpoint`. With no endpoint it is the account's host at the public service. An endpoint's path is empty or the account's name, which is how a store on this machine is addressed. It must name an account and a container, may not decline verification, and no other kind may carry its fields. The configuration judges an address without the provider, and a parity test holds that judgement to `AzureBlobLocation.DefectOf` over every combination of good and bad parts.

4. **Either credential, sealed, and the kind said (contract 1.61).** A person stores the account key or a shared access signature for the container. Each crosses as an envelope sealed to the service's recipient key under a purpose of its own, bound to the destination's name, so neither opens as the other, as an access key, or for another destination (NFR-SEC-009). `set_destination_credentials` gains `kind`: `access-key`, which is what a request naming none means, `shared-key` or `sas`. An access key id goes with an access key only. The descriptor gains `account`, `container`, `authorised_by`, which says which credential is held, and `signature_expires`; `access_key_stored` now answers for either object store.

   A signature's expiry is read from the token itself and kept to. One that has already lapsed is refused when it is stored. One that lapses later is failed at the next sync, saying when it lapsed and how to store a new one, and no request is sent under it. The listing reports when it lapses, and the console marks it once it has.

   The held file says which credential it holds, and each destination kind reads only the credentials it can use. A destination whose kind is changed after a credential was stored is told it holds none rather than being handed another API's secret.

   The new members are named for what they say, not for the secret. `KeyMaterialConfinementTests` refuses any contract member whose name contains "credential" unless it is argued for by name. These carry no key material, and a name that keeps the guard as narrow as it was is better than an exception to it.

5. **Served as a bucket is, through one check.** `DestinationKinds.IsObjectStore` is true of `s3` and `azure-blob`, and every place ADR-0091 taught to serve a bucket now asks it instead of naming a kind. A container is synced through the local path's copy and read back with the peer's random share. A direct-ship run leaves it behind for the sync after the run. It is restored from by listing its prefix when staging is lost, drilled and swept only on a cadence its operator states, repaired by delete and put, probed with one listing, trusted for the staging trim through its ledger, and adopted from into a staging set. Its failures are the bucket's three rows: no credential, a refusal and no answer. A lapsed signature is a fourth, and is failed like a refusal. The notice for an object the sweep found altered names who else could have altered it: whoever holds the account key or a signature for the container.

   The host tests became one suite for both stores. Every case `Hosts.Tests/S3DestinationTests` and `Hosts.Tests/S3AdoptionTests` held is now in `Hosts.Tests/ObjectStoreDestinationTests` and `Hosts.Tests/ObjectStoreAdoptionTests`, run once against a bucket and once against a container. The two stores are therefore held to one behaviour, not two that could drift.

6. **Failure domain.** Independent unless declared otherwise, as [ADR-0018](0018-replica-failure-domains.md) Amendment 2 says of a cloud kind.

## Consequences

- **`azure-blob` is a served kind.** `dropbox` alone stays reserved, refused as before.
- **The build has two HTTP clients.** Each is in one assembly, named, closed off from the engine, composed only by the service, and sent only to the endpoint a person declared. A default run still makes no HTTP request (`DefaultBuildSilenceTests`, unchanged).
- **What a store learns.** Object keys, which are keyed identifiers and never file names; their sizes and timing; the account's name; and under a signature, the signature, which the account issued. Every object is sealed before it leaves the machine (T-9 unchanged).
- **The account key is the widest credential this product holds for anyone.** It opens every container in the account, not one bucket, and does not lapse. The service never overwrites, but whoever holds the service account can use the key directly (T-6, T-19). A signature issued for the one container, with only the permissions the sync needs and an expiry, is the narrower thing to give the service. The console and the CLI accept both, because a person may have only the key.
- **A signature lapses, and the service only says so after it has.** Until then the listing and the console report when it will. A notice ahead of the lapse is not built.
- **The recovery tool does not dial a container.** A container's replica has a local path's layout under its prefix, so a copy of that folder brought to local disk opens as any replica does ([architecture 08 §5](../architecture/08-restore-and-recovery.md)).
- **FR-REP-002 is met.** Both halves pass the shared contract suite, in process and against a real store when one is named.

## Alternatives considered

- **The vendor's SDK.** Rejected, for ADR-0091's reasons: the largest dependency closure in the product, with the retry, credential-discovery and telemetry machinery the pinned set exists to keep out, for a protocol this product uses five verbs of.
- **The account key alone.** Rejected. It is the account's master credential, and the narrower thing an operator should be able to hand the service is a signature for the one container that lapses when they choose.
- **A signature alone.** Rejected. A signature lapses, and an installation meant to run for years would need a person to issue and store a new one on a schedule. Some operators have only the key to give.
- **Tokens from the account's directory service.** Rejected for now. The service would need a second endpoint to dial, a refresh flow to run unattended, and a client registration, for a credential that, once issued, authorises the same requests.
- **One `object-store` kind with a field naming the API.** Rejected. The two addresses share only an endpoint and a prefix, and one kind would make every validation rule a branch. The configuration keeps a kind per API; the runtime treats them alike through `IsObjectStore`.
- **The credential members named for what they protect.** Rejected, see decision 4: the guard against key material on the wire is worth more than the more obvious name.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built end to end: the provider over the platform's HTTP client with a Shared Key signer of its own, the shared contract suite against an in-process store under both credentials, the account key or shared access signature sealed into the state directory (contract 1.61, schema 10), every site that served a bucket serving a container through one check, the CLI's options and the console's editor; [ADR-0091](0091-an-s3-compatible-destination.md), [ADR-0012](0012-storage-provider-contract.md), [ADR-0027](0027-services-scheduling-status-telemetry.md) §3, [ADR-0034](0034-hub-and-spoke-destinations.md) §5, [ADR-0035](0035-destination-fitness.md), [ADR-0042](0042-write-only-repositories.md) decision 6, [ADR-0043](0043-structured-logging-and-diagnostics.md) and [ADR-0061](0061-adopt-a-destinations-archives.md) amended |
| 2026-10 | Amended (related) | [ADR-0012](0012-storage-provider-contract.md#amendment-5-2026-10--the-revisit-three-providers-and-the-faults-this-record-named) Amendment 5: a resumed listing starts at the store (`startFrom`) rather than reading the pages before the key, and a throttle that outlasts the attempts is a busy store, unavailable rather than failed |

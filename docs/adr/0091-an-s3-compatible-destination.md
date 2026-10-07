# ADR-0091 — An S3-compatible destination

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-DEST-005 (amended: `s3` is served), FR-REP-002 (amended: its S3 half), NFR-PRIV-001 (amended: one HTTP client, named and confined), NFR-SEC-009 (amended: the access key's envelope joins the named verbs); keeps NFR-SEC-006, NFR-SEC-012 and NFR-OPS-003
**Related:** [ADR-0012](0012-storage-provider-contract.md) (the store contract as the provider seam, and Amendment 2 the fan-out's), [ADR-0034](0034-hub-and-spoke-destinations.md) §5 (the cloud kinds reserved until a provider exists), [ADR-0042](0042-write-only-repositories.md) decision 6 (the sealed envelope), [ADR-0027](0027-services-scheduling-status-telemetry.md) §3 and [ADR-0043](0043-structured-logging-and-diagnostics.md) (no HTTP client in the build), [ADR-0046](0046-direct-to-destination-publication.md) (direct-ship), [ADR-0054](0054-scheduled-restore-drills.md) Amendment 3 and [ADR-0035](0035-destination-fitness.md) Amendment 2 (cadences on a link somebody else pays for), [ADR-0018](0018-replica-failure-domains.md) Amendment 2 (a cloud kind is independent), [architecture 05](../architecture/05-storage-providers.md), [threat model T-6 and T-19](../threat-model.md#t-6-deletion-by-compromised-store-credentials)

**Built:**
- The provider: `Storage.S3/S3ObjectStore` over the platform's HTTP client, `Storage.S3/S3RequestSigner`, `Storage.S3/S3Location`, `Storage.S3/S3Credentials`, `Storage.S3/S3StoreUnreachableException`.
- The key: `Repository.Crypto/WriteOnlyProvisioning` seals and opens it, `Agent/DestinationCredentialStore` holds it, `Agent/ServiceCommandHandler` stores it on `set_destination_credentials` (contract 1.60).
- The service: `Agent/StoreComposition` opens a destination's store; `Agent/FanOut` syncs to it through the copy a local path uses; `Agent/DestinationShipSink`, `Agent/DestinationProbe`, `Agent/RecoveryDrillJob`, `Agent/Scheduler` and `Agent/SetCopies` serve it.
- The configuration: `Application/DestinationConfiguration`, `Application/ClientConfiguration` (schema 9).
- The surfaces: `Cli/CliApplication` (`destination-credentials`), `Web/WebConsoleHost` (`/api/destination-credentials`) and the console's destination editor.
- The tests:
  - `Storage.ContractTests/S3ObjectStoreContractTests` — the shared contract suite against a store that speaks the API in process, and against a store named in the environment when one is
  - `Storage.ContractTests/S3RequestSigningTests` — the signature pinned to vectors computed by an independent implementation that reproduces the API's published examples
  - `Storage.ContractTests/S3ObjectStoreTests` — every put a create; a retry sent from content read once; a refusal that lasts not retried, and named without the secret; a short body a fault; a store nobody answers told apart; a prefix that holds one destination's folders and no other's
  - `Application.Tests/S3DestinationConfigurationTests` — the address round-trips at schema 9 with no secret in the file, what an `s3` destination needs and what no other kind may carry, and a schema-8 file loads unchanged
  - `Repository.Tests/Crypto/AccessKeyEnvelopeTests` — the envelope opens only for the destination and key id it was sealed for, and no other envelope opens as it
  - `Hosts.Tests/S3DestinationTests` — a staging set's archive lands under the prefix, read back and proven; a second sync writes nothing; no key, an unreachable store and a refused signature are three different rows; a restore after staging is lost finds the replica by listing; a direct-ship run leaves the store to the sync after it; a drill brings a sample back, and the schedule drills only on a stated cadence; the read-back's random share stops its rotation short of a local path's; the key is held owner-only and is in nothing the service says
  - `Hosts.Tests/ClientModeTests` — the CLI seals the secret from a named environment variable
  - `Web.Tests/DestinationCredentialsCeremonyTests`, `Web.DomTests/ConfigEditingDomTests` — the console seals it in its own process and the relay is never sent it
  - `Api.Tests/ConfigurationContractTests`, `Api.Tests/KeyMaterialConfinementTests` — the wire names at 1.60, and the envelope's place on the fence
  - `ArchitectureTests/TelemetrySilenceTests`, `ArchitectureTests/DependencyRuleTests`, `ArchitectureTests/LoggingShapeTests` — the one HTTP client, its closure, its one composer and its event ids

---

## Context

FR-DEST-005 made `s3`, `azure-blob` and `dropbox` destinations the configuration accepts and the runtime refuses, a stated incapacity rather than a failure, so that configuration, status and retention were designed once ([ADR-0034](0034-hub-and-spoke-destinations.md) §5). FR-REP-002 asks for S3-compatible stores through the common abstraction, proven by the shared contract suite. The contract was written for this ([architecture 05](../architecture/05-storage-providers.md)): a provider declares conditional create, ranged reads and how consistent its listing is, and the fan-out copies between any two stores ([ADR-0012](0012-storage-provider-contract.md) Amendment 2).

Four things stood between the reservation and a served kind.

- **How to speak the API.** A vendor SDK is a dependency the pinned package set (NFR-PRIV-001) does not admit, and it brings its own configuration, retry and telemetry surfaces with it. The platform's HTTP client is admitted by no package, but `TelemetrySilenceTests` forbids an HTTP client in every assembly, because nothing had needed one.
- **Where the key lives, and how it arrives.** The configuration file is exportable without secrets (NFR-OPS-003). Nothing may cross the command surface as raw key material (NFR-SEC-009).
- **Where the API and the contract differ.** A put overwrites unless told not to; a range that runs past the end is answered short; a delete of nothing succeeds; the signature needs the body's hash before the body.
- **What the service does with a destination on the far side of a network** that is neither a disk it can see nor a peer that answers challenges.

## Decision

1. **The platform's HTTP client and a signer of this product's own, in one assembly.** `Storage.S3` speaks the S3 API over `HttpClient` and signs each request with the API's version-4 signature, about a hundred lines checked against an independent implementation's vectors. No package is added. It is the one assembly allowed an HTTP client: an allowlist of one in `TelemetrySilenceTests`, which fails when the assembly stops needing it. It opens no socket of its own, depends on the store contract and Domain alone, and only the service composes it (`DependencyRuleTests`). Redirects, cookies and decompression are off, and a request is bounded by its caller's token rather than a clock. An endpoint must be `https`, except on this machine.

2. **The contract's semantics, bridged in the provider.**
   - Every put is a create, sent `If-None-Match: *` whatever its conditions, and a 412 is `AlreadyExists`. Nothing the service writes overwrites anything at the store.
   - The range served is read back from `Content-Range`, and a body that ends early is a fault.
   - A delete asks first, so the contract can report `NotFound`.
   - Content that can seek is hashed and rewound; content that cannot is spooled while it is hashed. Either way the factory is read once (05 §2.1).
   - Listing is ListObjectsV2, page by page, and is declared strongly consistent, which the API has promised since 2020.
   - A refusal that may not last is retried from the content already read: a throttle, a server error, a timeout, a dropped connection. One that will last, such as a refused signature, is a fault at once.
   - A store that never answers after every attempt is an `S3StoreUnreachableException`. It is still an `IOException`, so every caller survives it, but a caller can tell an outage from a refusal.
   - One request a blob, up to five GiB. The format's blobs are a fraction of that, so multipart upload is not used.

   The shared contract suite passes against `TestSupport/S3CompatibleTestServer`, an in-process store that checks every signature independently. It also passes against a real store named in the environment (`ConfiguredS3Store`), and passed against a third-party S3-compatible gateway run locally while this was built.

3. **The address in the configuration, the key in the state directory.** Schema 9 gives an `s3` destination `bucket`, `region` (default `us-east-1`), `prefix` and `addressing` (`path` or `virtual-host`), and its `endpoint` is the store's base URL. It must name an endpoint and a bucket, may not decline verification (the hub reads it back on every sync), and no other kind may carry its fields. The configuration judges an address without the provider. A parity test holds that judgement to the provider's own `S3Location.DefectOf`.

   The key is held at `<state>/destination-credentials/<destination id>.json`: owner-only, replaced atomically, by id so a rename keeps it, and forgotten when the destination is deleted (NFR-SEC-012). It is in no configuration file, export, listing, diagnostic bundle or log. The listing says only whether one is held (`access_key_stored`).

4. **The key crosses only sealed (contract 1.60).** `set_destination_credentials` carries the destination's name, the access key id in clear, and the secret as an envelope sealed to the service's recipient key under a purpose of its own. The destination's name and the key id are bound into the envelope's associated data, so it cannot be stored into another destination's credentials, or beside another key id, and it opens as no other envelope does. It joins the envelope verbs by decision (NFR-SEC-009, `KeyMaterialConfinementTests`). The CLI reads the secret from an environment variable it is told the name of, never from an argument. The console seals it in its own process at `/api/destination-credentials`, the third of its endpoints permitted a secret, and never sends it through the command relay.

5. **Served as a local path is, wherever a store allows it.**
   - **Sync.** The local path's sync, over the store: the heal from a destination ahead of the archive, the keep-set, the copy or converge, the repair of what was found damaged, and a sample read back before the pair is called in sync. One routine serves both kinds (`FanOut`). The replica is at `<prefix>/<repository id>/`. It is missing when the prefix holds nothing, which a one-key listing says. There is no free-space floor, because a store does not report one. The read-back takes the peer's random share of the sample: a store has a party on its far side, and the rotation alone is predictable from what was asked before.
   - **Outcomes.** No key stored, a defective address, or a store that answers and refuses: failed, because waiting does not change the answer. A store that does not answer: unavailable, a gap that closes itself (FR-DEST-003).
   - **Direct-ship.** A run never writes through to a store. It records the store behind, and the sync that follows the run fills it from what the run shipped. A direct-ship set still needs a local path or a peer to write through.
   - **Restore.** A replica opens as a restore source by the repository id staging names. With staging lost, each folder under the prefix is tried. The repair and the restore fallback read it as one of the set's copies.
   - **Drill.** Only on a cadence the operator states, and on the peer's sample budget, because every read is a request and at a provider a cost. A person's drill runs at any time.
   - **Probe.** One listing of one key: the endpoint answers, the key signs, the bucket is there.
   - **Staging trim.** Trusted through its ledger claim, backed by the read-back stamp every sync earns, as a peer is.
   - **Not swept, and not adopted from.** The deep sweep does not read a store yet; every sync reads a sample back. An archive is not adopted from a store; it is restored from one.
   - **Failure domain.** Independent unless declared otherwise, as ADR-0018 Amendment 2 already said of a cloud kind.

## Consequences

- **`s3` is a served kind.** `azure-blob` and `dropbox` stay reserved, refused as before.
- **The build has an HTTP client.** It is in one assembly, named, closed off from the engine, composed only by the service, and sent only to the endpoint a person declared for a destination. A default run still makes no HTTP request: `DefaultBuildSilenceTests` backs up to a local path and observes nothing, unchanged.
- **What a store learns.** Object keys, which are keyed identifiers and never file names; their sizes and timing; and the access key id. Every object is sealed before it leaves the machine (T-9 unchanged).
- **The access key is now in the service account's keeping.** Whoever holds the service account can use it to delete or alter objects at the store directly, which no repository key prevents (T-6, T-19). The service's own writes never overwrite. A key scoped by the operator to the bucket and prefix, and the provider's object lock or versioning, are the operator's measures. Provider object lock is still the later phase T-6 names.
- **The recovery tool does not dial a store.** A store's replica has a local path's layout under its prefix, so a copy of that folder brought to local disk opens as any replica does ([architecture 08 §5](../architecture/08-restore-and-recovery.md)).
- **Owed:** a deep sweep of a store's replica, adopting an archive from a store, and the Azure half of FR-REP-002.

## Alternatives considered

- **A vendor SDK.** Rejected. It would add the largest dependency closure in the product, and it would bring the retry, credential-discovery and telemetry machinery the pinned set exists to keep out, for a protocol this product uses five verbs of.
- **The key in the configuration file.** Rejected. The file is exported and copied between machines (NFR-OPS-003), and a secret in it is a secret in every copy.
- **The key from the service's environment.** Rejected. A person at the console could neither set it nor see whether it was set, and it would differ by how the service was installed.
- **The key sealed under the passphrase.** Rejected. The service must sign unattended, and a key it can only open with the passphrase would need the passphrase at every sync.
- **Puts that may overwrite, made safe by asking first.** Rejected. Asking and then writing races, and the contract's create-only semantics are what the copier and the journal rely on.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built end to end: the provider over the platform's HTTP client with a signer of its own, the shared contract suite against an in-process store, the access key sealed into the state directory (contract 1.60, schema 9), the service's sync, restore, drill, probe and direct-ship catch-up, the CLI verb and the console's editor; [ADR-0027](0027-services-scheduling-status-telemetry.md) §3, [ADR-0034](0034-hub-and-spoke-destinations.md) §5, [ADR-0042](0042-write-only-repositories.md) decision 6 and [ADR-0043](0043-structured-logging-and-diagnostics.md) amended |

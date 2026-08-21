# Implementation status

**Status:** maintained · **Checked by:** [`eng/check-adr-status.py`](../eng/check-adr-status.py)

---

Thirty-seven decision records say what this system should do. This says which of them the code actually does, and — where the answer is "some of it" — which part.

It exists because the two drift apart silently and in one direction. An ADR is written before the work and is never wrong afterwards; nothing in it goes red when the thing it decided turns out to be half-built. The [traceability matrix](requirements/traceability.md) had exactly this failure and had to be rebuilt from fiction: 73 of its 86 test citations named classes nobody had written. That repair is the reason this page cites files rather than intentions, and the reason a checker resolves it on every run.

**A row claims only what a named file establishes.** Where a decision is partly built, the row says so and the section below it names the missing half. "Specified only" is not a criticism — most of those are phase 3 and 4 work that is correctly not started — but it is never left to be inferred from silence.

**Legend**

| State | Means |
|-------|-------|
| **Built** | The decision is in the code and tests hold it to it |
| **Partly built** | A named part shipped and a named part did not — see the notes below |
| **Specified only** | Decided and written down; nothing implements it yet |
| **Applied** | Not code: a licence, a policy, or a build arrangement that is in force |

---

## By decision

| ADR | Decision | State | Where it is |
|-----|----------|-------|-------------|
| [0001](adr/0001-licence-and-contribution-model.md) | Licence and contribution model | **Applied** | [`LICENSE`](../LICENSE), [`LICENSING.md`](../LICENSING.md), [`CONTRIBUTING.md`](../CONTRIBUTING.md) |
| [0002](adr/0002-segmentation-strategy.md) | Segmentation strategy | **Built** | `Repository.Segmentation/FixedSegmentReader`, `Repository.Segmentation/CdcSegmentReader` · `Repository.ConformanceTests/SegmentationConformanceTests` |
| [0003](adr/0003-canonical-metadata-encoding.md) | Canonical metadata encoding | **Built** | `Repository.Format/Cbor/CanonicalCbor*` · `Repository.FuzzTests/ParserFuzzTests` |
| [0004](adr/0004-segment-hash-function.md) | Segment hash function | **Built** | `Repository.Crypto/ContentHasher`, `Domain/Profiles/ContentHashProfile` · `Repository.ConformanceTests/IdentifierConformanceTests` |
| [0005](adr/0005-aead-suite-and-nonce-construction.md) | AEAD suite and nonce construction | **Built** | `Repository.Crypto/RecordCipher`, `Repository.Crypto/BlobKeyDeriver` · six requirements, all traced |
| [0006](adr/0006-object-identifiers-and-dedup-trust-domains.md) | Object identifiers and dedup trust domains | **Built** | `Repository/DedupTrustGate` · [notes](#0006--the-integrity-guard-is-built-and-one-thing-is-deliberately-not) |
| [0007](adr/0007-logical-object-identifiers-in-manifests.md) | Manifests carry logical identifiers only | **Built** | `Repository.Format/Manifests/*`, `Repository.Format/Manifests/SourceIdentityHint`, `Repository/SourceIdentityLookup` · `Repository.Tests/Index/IndexPrecedenceTests`, `Repository.Tests/Format/SourceIdentityHintCodecTests` · [notes](#0007--device-specific-facts-live-outside-the-manifest-and-one-of-the-two-is-built) |
| [0008](adr/0008-index-generations-and-checkpoints.md) | Index generations, deltas, checkpoints | **Built** | `Repository.Index/CheckpointCodec`, `Repository.Index/IndexDeltaCodec`, `Repository.Index/WriterSequence` |
| [0009](adr/0009-garbage-collection-safety.md) | Garbage collection safety | **Partly built** | `Repository.Index/Journal/IntentLifecycle`, `Retention/StagingSweep` · `Retention.Tests/RetentionCycleTests` · [notes](#0009--the-collector-is-built-compaction-is-not) |
| [0010](adr/0010-local-store-separation.md) | Local store separation | **Built** | `Application/LocalState` · `Repository.Tests/EndToEnd/LocalStateSeparationTests` |
| [0011](adr/0011-commit-versus-replication-semantics.md) | Commit versus replication semantics | **Built** | `Application/DestinationSyncStore` (the per-replica half), `Repository/SnapshotPublication` (the commit half) · [notes](#0011-0018--commit-is-per-replica-and-there-are-now-many-replicas) |
| [0012](adr/0012-storage-provider-contract.md) | Storage provider contract | **Partly built** | `Storage.Abstractions`, `Storage.Local` · `Storage.ContractTests` · [notes](#0012--the-contract-is-real-it-has-one-provider) |
| [0013](adr/0013-recovery-kit.md) | Recovery kit contents and format | **Built** | `FallbackPlan.Recovery`, [`specifications/recovery-kit/`](../specifications/recovery-kit/README.md) · `Repository.ConformanceTests/RecoveryKitConformanceTests` |
| [0014](adr/0014-format-versioning-and-stability.md) | Format versioning and pre-1.0 posture | **Built** | `Repository/RepositoryLifecycle` · `Repository.Tests/EndToEnd/RepositoryLifecycleTests` |
| [0015](adr/0015-legacy-importer-isolation.md) | Legacy importer isolation | **Partly built** | `FallbackPlan.Import.Abstractions` · [notes](#0015--the-seam-is-the-decision-and-the-seam-is-built) |
| [0016](adr/0016-blob-identifier-formation.md) | Blob identifiers are writer-allocated | **Built** | `Domain/Identifiers/BlobId`, `Domain/IBlobCounterAllocator` · `InterruptionTests/SequenceRollbackTests` holds the refusal when an identifier is ever reused |
| [0017](adr/0017-index-entry-supersession.md) | Index entry supersession and precedence | **Built** | `Repository.Index/IndexEntry`, `Repository.Index/IndexLoader` · `Repository.Tests/Index/IndexPrecedenceTests` |
| [0018](adr/0018-replica-failure-domains.md) | Replica failure domains | **Built** | `Application/StatusModel` · `Repository.Tests/EndToEnd/ApplicationServiceTests` · [notes](#0011-0018--commit-is-per-replica-and-there-are-now-many-replicas) |
| [0019](adr/0019-third-party-dependency-policy.md) | Third-party dependency policy | **Applied** | `ArchitectureTests/DependencyRuleTests` — the policy is a test, not a promise |
| [0020](adr/0020-ed25519-signing-key-semantics.md) | Ed25519 signing key semantics | **Built** | `Repository.Crypto/RepositorySigner` · `Repository.ConformanceTests/Ed25519ConformanceTests` |
| [0021](adr/0021-consume-bodu-via-committed-package-feed.md) | Bodu from a committed local feed | **Applied** | [`external/packages/`](../external/packages/README.md), [`nuget.config`](../nuget.config) |
| [0022](adr/0022-standalone-metadata-records-and-index-identifiers.md) | Standalone records and index identifiers | **Built** | `Repository.Format/Records/*`, `Repository.Index/IndexDeltaCodec` · `Repository.FuzzTests/ParserFuzzTests`, `Repository.Tests/Index/IndexPlaneTests` |
| [0023](adr/0023-cdc-v1-rabin-parameters.md) | cdc-v1 Rabin fingerprint parameters | **Built** | `Repository.Segmentation/RabinFingerprint` · `Repository.FuzzTests/CdcPropertyTests` |
| [0024](adr/0024-include-exclude-rule-dialect.md) | Include/exclude rule dialect | **Built** | `Domain/PathRules` · `Repository.ConformanceTests/PathRulesConformanceTests` |
| [0025](adr/0025-compaction-reseals-records.md) | Compaction re-seals records | **Specified only** | [notes](#0025--nothing-compacts-yet-so-nothing-re-seals-yet) |
| [0026](adr/0026-phase-1-capture-shapes.md) | Phase-1 capture shapes | **Partly built** | `Filesystem.Local/LocalFileSystemSource`, `Filesystem.Local/PosixInterop`, `Filesystem.Local/PosixHandleInterop`, `Filesystem.Local/PosixDirectoryScope` · `Filesystem.Tests/LocalScanTests` · [notes](#0026--the-shapes-are-captured-the-posix-traversal-is-handle-relative-and-one-gap-is-left) |
| [0027](adr/0027-services-scheduling-status-telemetry.md) | Scheduling, job state, status, telemetry | **Built** | `FallbackPlan.Agent`, `Application/JobStateStore` · `Hosts.Tests/*` |
| [0028](adr/0028-service-boundary-and-deployment-topologies.md) | The service boundary | **Partly built** | `FallbackPlan.Api`, `Cli/OperationGateway` · [ADR §Implementation status](adr/0028-service-boundary-and-deployment-topologies.md#implementation-status-2026-08) |
| [0029](adr/0029-pipeline-and-service-concurrency.md) | Pipeline and service concurrency | **Built** | `Repository/ArchiveSession` · [ADR §Implementation status](adr/0029-pipeline-and-service-concurrency.md#implementation-status-2026-08) |
| [0030](adr/0030-peer-identity-and-pairing.md) | Peer identity and pairing | **Partly built** | `FallbackPlan.Protocol`, `Protocol/PairingInvite.cs` · [notes](#0030--the-socket-exists) |
| [0031](adr/0031-exception-messages-are-resources.md) | Exception messages are resources | **Built** | `Domain/Resources/Strings.g.cs`, `Repository.Format/Resources/Strings.g.cs`, [`eng/generate-resources.py`](../eng/generate-resources.py) · CI: accessors match their resx |
| [0032](adr/0032-mstest-as-the-test-framework.md) | MSTest is the test framework | **Built** | `TestSupport/PlatformFacts.cs`, `TestSupport/PropertyCheck.cs`, `TestSupport/SequenceAssert.cs` · 966 tests, count verified identical across the move |
| [0033](adr/0033-hosting-under-an-os-service-manager.md) | Hosting under an OS service manager | **Partly built** | `Agent/ServiceProcessHost.cs`, `Agent/WindowsServiceHost.cs`, `Agent/ServiceUnit.cs` · [notes](#0033--the-os-can-own-the-process) |
| [0034](adr/0034-hub-and-spoke-destinations.md) | Hub-and-spoke destinations | **Built** | `FallbackPlan.Replication`, `Agent/FanOut`, `Application/DestinationSyncStore`, `Retention/StagingTrim` · `Repository.Tests/EndToEnd/AgentPassTests`, `InterruptionTests/StoreCopyOrderTests`, `Retention.Tests/StagingTrimTests` · [notes](#0034--the-hub-fans-out-ages-and-trims) |
| [0035](adr/0035-destination-fitness.md) | Destination fitness | **Built** | `Agent/DestinationProbe.cs`, `Agent/PeerAddress.cs`, `Agent/ReplicaSweepJob.cs`, `Repository/ReplicaSweep.cs`, `Replication/VerificationSampler.cs`, `Application/DestinationCapacity.cs` · `Retention.Tests/DestinationConvergenceTests`, `Replication.Tests/VerificationSamplerTests`, `Hosts.Tests/PeerQuotaTests` · [notes](#0035--a-destination-has-to-earn-being-relied-on) |
| [0036](adr/0036-local-web-console.md) | The local web console | **Built** | `FallbackPlan.Web`, `Web/WebConsoleHost.cs`, `Web/ConsoleAuth.cs` · `Web.Tests/ConsoleAuthTests`, `Web.Tests/CommandRelayTests`, `Web.Tests/EventStreamTests`, `ArchitectureTests/DependencyRuleTests` · [notes](#0036--the-first-front-end-beyond-the-cli) |
| [0037](adr/0037-configuration-over-the-command-contract.md) | Configuration over the command contract | **Built** | `Agent/ServiceCommandHandler.Configuration.cs`, `Agent/ServiceCommandHandler.Pairing.cs`, `Protocol/PairingInvite.cs` · `Hosts.Tests/ConfigurationCommandTests`, `Hosts.Tests/InvitePairingCommandTests`, `Protocol.Tests/InvitePairingTests`, `Api.Tests/ConfigurationContractTests` · [notes](#0037--the-configuration-lifecycle-joins-the-contract) |
| [0038](adr/0038-set-change-rescan-and-notice.md) | Set changes rescanned | **Built** | `Repository/SourceComparer.cs`, `Repository/ChangeDetection.cs`, `Agent/SetChangeScan.cs` · `Repository.Tests/SourceComparerTests`, `Hosts.Tests/SetChangeTests` · [notes](#0038--a-set-edit-answers-with-its-meaning) |
| [0039](adr/0039-console-operator-loop.md) | The console's operator loop | **Built** | `Agent/PeerUnpairing.cs`, `Agent/ServiceCommandHandler.cs`, `Agent/ServiceCommandHandler.Pairing.cs`, `FallbackPlan.Web` · `Hosts.Tests/NoticeCommandTests`, `Hosts.Tests/UnpairCommandTests`, `Hosts.Tests/DirectoryChangeTests` · [notes](#0039--the-loops-close-where-the-operator-lives) |
| [0040](adr/0040-multi-root-backup-sets.md) | Multi-root backup sets | **Built** | `Filesystem/MultiRootScan.cs`, `Filesystem/ScanRoot.cs`, `Application/ClientConfiguration.cs`, `Agent/ServiceCommandHandler.cs`, `FallbackPlan.Web` · `Repository.Tests/MultiRootPublicationTests`, `Hosts.Tests/MultiRootSetTests` · [notes](#0040--several-folders-one-snapshot) |
| [0041](adr/0041-guided-restore-and-peer-retrieval.md) | The guided restore and peer retrieval | **Built** | `Restore/RestoreExecutor.cs`, `Agent/RestoreSourceRegistry.cs`, `Agent/RetrievalResponder.cs`, `Protocol/PeerRetrievalMessages.cs`, `Web/ConsoleRestoreGate.cs` · `Repository.Tests/RestoreBreadthTests`, `Hosts.Tests/RestoreSourceTests`, `Hosts.Tests/PeerRetrievalTests`, `Web.Tests/RestoreGateTests` · [notes](#0041--restore-walks-in-through-the-front-door) |
| [0042](adr/0042-write-only-repositories.md) | Write-only repositories (format v2) | Built | `Repository.Crypto/WriteOnlyDerivation` · `Repository.Packing/SealedContentKey` · `Agent/WriteOnlyServiceState` · [notes](#0042--the-hub-that-cannot-read-what-it-keeps) |
| [0043](adr/0043-structured-logging-and-diagnostics.md) | Structured logging and client diagnostics | **Specified only** | [notes](#0043--the-decision-is-made-the-abstraction-is-not-yet-threaded) |
| [0044](adr/0044-first-run-setup.md) | First-run setup and the installation passphrase | Built | `Domain/Configuration/PassphraseStrength` · `Agent/WriteOnlyServiceState` · `Agent/ServiceCommandHandler.Setup.cs` · `Web/ConsoleRestoreGate` · [notes](#0044--the-ceremony-that-two-requirements-have-been-waiting-for) |

---

## Where "partly" is doing work

### 0006 — the integrity guard is built, and one thing is deliberately not

Object identifiers, the keyed derivation behind them, the domain enumeration — and now the guard the enumeration was for.

`DedupTrustGate` decides every reuse, segments and metadata objects alike, and it reads **writer attribution first, domain second**. A segment this writer wrote is reused in every domain with no read at all, which is what makes the default affordable and is the literal text of FR-DED-002's acceptance: a second backup of an unchanged single-writer tree issues **zero** store reads, measured rather than asserted. Another writer's object is refused outright under `device`, referenced unread under `repository-unverified`, and under the default `repository` is fetched, decrypted and confirmed before it is referenced. The confirmation is the record read's own 04 §6 step 7, so there is no second verification path that could disagree with the first.

**This is [C3](review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard)'s remedy, present.** A record that reads and does not verify is written again from the bytes this device holds and reported as a damage finding against the object — detection at write time, while the source data is still there. [T-10](threat-model.md) is mitigated under the default and closed under `device`. Six end-to-end tests in `Repository.Tests/DedupTrustDomainTests` hold it, and they are the only suite in the repository with two writers, because with one writer all three domains are indistinguishable by design.

**What is deliberately not built** is a durable home for verification outcomes. They live in the catalogue, so deleting it re-imposes the read once. The alternative was a repository object recording them — format surface frozen into v1 before anything consumes it, to avoid a cost that only exists in a multi-writer repository. [PT-12](review/2026-08-fix-pressure-test.md#pt-12--device-attribution-and-verify-on-reuse-state-live-only-in-a-disposable-cache) offered both and this takes the second; FR-DED-003's acceptance criterion was amended to say so rather than left claiming otherwise.

`FR-DED-004` is the row that stays unmet: `repository-unverified` works, but nothing requires the acknowledgement that turning it on means accepting another member can corrupt your backup. That gate belongs where the domain is chosen, which is a client, not the engine.

### 0007 — device-specific facts live outside the manifest, and one of the two is built

ADR-0007's rule is that a manifest carries logical identifiers only, and its [amendment](adr/0007-logical-object-identifiers-in-manifests.md) settled what happens to the device-specific facts that rule excludes: they become separate optional objects per snapshot, so a manifest's bytes stay identical across devices and cross-device deduplication keeps working.

Two such objects are specified. The **source-identity hint** ([06 §11](../specifications/repository-format/06-manifests.md#11-source-identity)) is built — written by `PublicationOrchestrator`, read by `SourceIdentityLookup`, and consulted when the catalogue cannot say which prior version an inode belongs to. That is the case a catalogue rebuild produces, and without it a file renamed in that window would record no `parent_version` at all, losing its history permanently because a disposable cache was cold. Three end-to-end tests hold it: the rename keeps its ancestry across a rebuild, a file untouched for several snapshots still finds its ancestor when it moves, and deleting every hint costs exactly that ancestry and nothing else.

It is keyed by **source key** rather than by snapshot, and the difference is the whole of [Q21](open-questions.md#closed): one object per file version created, so per-snapshot cost follows what changed. The first shape named every file the snapshot contained and cost ~52 bytes per file every run — the growth NFR-PERF-005 forbids, and the reason that requirement could not be asserted on total store bytes until this changed.

A renamed file no longer pays for its move in reads either. `PriorManifestSource` resolves the prior version's location through the catalogue and opens that one blob through its recovery footer, so the publisher can fetch the prior manifest and rewrite it under the new name instead of re-reading the file — a handful of range reads in place of the whole file. It is best-effort: a manifest that cannot be fetched or decoded sends the file down the ordinary capture path and raises no finding. It also needs the catalogue, so after a rebuild the hints recover the ancestry and the content is read again, because a hint names an object and not its location.

The **placement hint** ([06 §10](../specifications/repository-format/06-manifests.md#10-placement-hint)) is specified and not built. It is a `MAY`, and the thing it accelerates — single-file emergency recovery without an index — has no implementation to accelerate yet; it is worth writing alongside that path rather than before it.

### 0009 — the collector is built; compaction is not

The half that protects data came first: write-intent journal records, the intent lifecycle, and the rule that any component creating a blob publishes an intent first — including the collector, per [PT-3](review/2026-08-fix-pressure-test.md). Leases are advisory, as decided.

The collector now exists and reclaims space (`FallbackPlan.Retention`): `StagingMark` walks the protected closure, `StagingSweep` runs the signed-tombstone → grace-by-publication → revalidate → delete cycle, and `StagingTrim` drops historic data blobs every entitled destination verifiably holds — see [0034](#0034--the-hub-fans-out-ages-and-trims) for the whole engine. Every deletion honours the intent survey, so the safety machinery finally protects against a process that runs. What remains of this decision is **compaction** (architecture 07 steps 6–9): partially-live blobs are kept whole and reported as the stated backlog, and nothing re-packs them — that is still phase 4.

### 0011, 0018 — commit is per-replica, and there are now many replicas

The decision that a snapshot commits per destination rather than globally is in the publication model, and everything that makes it *matter* has since arrived with the hub-and-spoke arc: a set declares several destinations, the sync ledger (`Application/DestinationSyncStore`) carries per-`(set, destination)` state, and failure domains are compared by device identity (`Application/StatusModel`) rather than assumed — the PT-8 placeholder replaced. `Protected` is earned only by an in-sync destination outside the source's failure domain, which is ADR-0018's rule in force. What has no dedicated test yet is `FR-SNP-007`'s full five-state per-destination snapshot lifecycle; the ledger's coarser states stand in for it and the traceability matrix says so.

### 0012 — the contract is real; it has one provider

`Storage.Abstractions` defines the contract, `Storage.ContractTests` is a reusable suite any provider must pass, and `Storage.Local` passes it. This is the shape the decision asked for, and the shape is what protects the design.

It is still one provider. A contract with a single implementation has not yet been tested by the thing it exists for — the second implementation that disagrees with it. Azure and S3 are phase 3, and `NFR-PORT-002` is traced against the architecture tests and the contract suite rather than against a provider that proves portability by being different.

### 0015 — the seam is the decision, and the seam is built

ADR-0015's decision was to isolate a legacy importer behind a boundary, not to write one. `FallbackPlan.Import.Abstractions` is that boundary, and phase 0's exit criteria proved it with a synthetic adapter feeding an arbitrary byte stream through the same pipeline ([roadmap](roadmap.md#phase-0--archive-engine-vertical-slice)).

No legacy reader exists and none should yet: it is phase 5 and gated on a legal review that has not happened. The row reads "partly built" rather than "built" so that nobody reads the seam's existence as the feature's.

### 0025 — nothing compacts yet, so nothing re-seals yet

The decision is sound and unexercised because the collector, though built, deliberately stops before compaction (architecture 07 steps 6–9): deletion-only GC never moves a record, so nothing re-seals. What *is* built is the constraint the decision protects — the record ordinal stays in the AAD, and `Repository.Tests/Index/IndexPrecedenceTests` holds the supersession rules a compaction would rely on.

### 0026 — the shapes are captured, the POSIX traversal is handle-relative, and one gap is left

All ten shapes are built and tested: hardlink groups, the diagnostics vocabulary, capture-status triggers, special files, alternate streams, directory entries, the filesystem capability record, and the catalogue casefold key.

The traversal underneath them is now handle-relative on POSIX (`Filesystem.Local/PosixDirectoryScope`, `Filesystem.Local/PosixHandleInterop`). Each directory is held open and its children are listed, stat'd, descended into, opened, and readlink'd by raw name bytes against that descriptor, with `O_NOFOLLOW` throughout — so the object that was classified is the object that is read, and revalidation stats the same handle rather than resolving the name again. An object carrying both a directory marker and a link marker is still classified as a link first, which is what keeps a junction from walking the scanner out of the approved root. Windows keeps the path-based walk and gains the identity check instead: a name that has come to mean a different object is recorded as `captured-identity-changed` and not re-read.

What is left is **capturing a POSIX name that is not valid UTF-8**, which the scanner can now open but the pipeline above it cannot carry: the relative path is a host string all the way through rules, the catalogue's path tables, and restore. [Specification 06 §4.3](../specifications/repository-format/06-manifests.md#43-what-name-must-contain) records why storing a lossy one would be worse than refusing it.

The **decision** that half of it depended on is now made rather than pending: where a host string is unavoidable, such a name renders **percent-encoded**, which is the only convention of the three considered that is lossless, valid UTF-8, and typeable back in. The remaining half is cost, and it is real — a byte-native relative path end to end means a catalogue schema bump, a receipt schema bump, byte-native rule matching, and native `openat`/`mkdirat` writes in a restore path that does not reference `Filesystem.Local` at all today. **Deferred past the format freeze deliberately**: the format needs nothing here, today's behaviour is a clean refusal rather than silent loss, and the freeze gate has no claim on it. Nothing will be built against a guess in the meantime, because the guess has been replaced by a rule.

### 0028 — the local binding, not the remote one

Recorded in the ADR's own [implementation status](adr/0028-service-boundary-and-deployment-topologies.md#implementation-status-2026-08) and not duplicated here. In short: writer-role exclusion, the versioned command contract, status aggregation, keystore unlock, per-job progress, and a CLI that asks a running service and falls back to direct mode. The remote binding — once a terminal refusal that bound nothing — now binds a real socket once an administrator names an interface; see [0030](#0030--the-socket-exists) for the transport it waited on.

The [restore pipeline review](review/2026-08-restore-pipeline-review.md) closed the gap that "falls back to direct mode" had hidden: the direct-mode restore was a second, uncontained implementation of the read path, and it now routes through the same `RestorePlanner`/`RestoreExecutor` the service uses — so ADR-0028 §3's "the same operation performs identically through either path" is enforced rather than asserted. The service also now carries the restore outcome across the contract and namespaces each run's displaced store.

### 0030 — the socket exists

Built, in `FallbackPlan.Protocol`: peer identity and fingerprints; the pairing ceremony's key agreement, transcript, short authentication string and confirmation signatures, **and the four messages that carry them**; the grant store, its pinning and revocation, and the destination's terms; frame encoding and refusal; session hello, accept and refuse; version selection and feature negotiation; and — after [Amendment 1](adr/0030-peer-identity-and-pairing.md#amendment-1-2026-08--authentication-moves-out-of-tls) — the channel-bound authentication that replaced RFC 7250, with a test that runs the man-in-the-middle it defeats.

And now **the transport that carries it.** `PeerTlsConnection` opens TLS 1.3 over TCP with the ephemeral certificate — a container for a per-connection key that authenticates nobody — and `PeerSessionDriver` drives the four-state machine over that real duplex stream: both authentication messages sent without waiting, each decoded frame admitted only in a state that permits it, every body length bounded before allocation, and every protocol violation answered with a stated refusal before the socket closes. A device's key persists in `<state>/peer.key` (`PeerKeypairStore`), so its identity survives a restart. `PairingCeremony` runs the ceremony over that stream, holds a confirmation that arrives before the local human has approved, and pins the grant only on mutual approval. The whole session and pairing layer is exercised over loopback TCP in `FallbackPlan.Protocol.Tests` — including the man-in-the-middle relay reproduced through two real TLS connections — and the ceremony is performed by two real operating-system processes in `FallbackPlan.Hosts.Tests`.

The service side binds it. `RemoteServiceListener` (in `FallbackPlan.Agent`) accepts on an interface named by an explicit administrative act — `fallbackplan-agent run --remote-interface <addr> --remote-port <n>`, off by every default — admits only a peer it has a grant for, and then runs the ADR-0028 command contract over the opened session through the same dispatch the local binding uses (`ServiceConnectionPump`). `RemoteServiceClient` (in `FallbackPlan.Cli`) is the paired console's other end, and the shipped CLI now drives it: `fallbackplan <verb> --connect <host:port> --fingerprint <fp> --state <dir>` routes `backup`, `verify`, `check`, `restore`, `snapshots`, `ls`, `status`, `sync` and `retention` to a remote paired service, naming the pinned service by fingerprint because a grant records a key, never an address. The two exit criteria this was blocked on now hold end to end in `FallbackPlan.Hosts.Tests`, through both the client directly and the CLI surface: an unpaired console is refused as `not_paired` while the local binding still answers, and a restore commanded from a paired console **writes on the service's machine** — the console is told the counts and the path, never sent the files.

And now the cargo the wire was built for: **peer replication** ([specification 03](../specifications/peer-protocol/README.md#documents)). Over an Open session, `ReplicationInitiator` (source) and `ReplicationResponder` (destination) move a repository's immutable objects — the source offers the repository, the destination declares what it already holds, and the source streams the rest in chunked frames, each object committed whole so an interrupted run resumes with no checkpoint. `RemoteServiceListener` routes on the peer's grant role: a peer entitled to store objects here speaks replication, a console speaks the command contract. The hub's fan-out drives it on every backup, and `fallbackplan-agent sync` drives it on demand — either way forwarding ciphertext the destination cannot read. `FallbackPlan.Hosts.Tests` proves it end to end over loopback: a source's objects mirror to a destination byte for byte, and the standalone recovery tool restores the original files from the replica — a source destroyed and recovered from its destination, the Phase-2 peer criterion in its first concrete form. `Hosts.Tests/AlternateSiteTests` now holds that criterion as an operator lives it — two live services paired by spoken invite, configured over the contract, the backup fanning out unattended, possession proven by the wire challenge, and both points in time restored after the source's archive is deleted, including a destination that was offline when the backup ran.

And after [Amendment 2](adr/0030-peer-identity-and-pairing.md#amendment-2-2026-08--the-pairing-lifecycle-completes-roles-on-the-wire-endings-announced-terms-enforced), **the storage roles are negotiated in the ceremony**: each side declares, on the wire, the role it will record for the other (spec 01 §2.2 key 7), both declarations ride the transcript — so an intermediary that altered who-stores-for-whom would alter the string the humans compare, pinned by a test — and both pair verbs take `--role stores-here|stores-for-us|both`, showing the peer's declaration at the approval prompt. A build predating the negotiated role is refused as malformed, with pairing again as the stated fix.

And the peering **ends as deliberately as it began**. `fallbackplan-agent unpair --to <host:port>` announces the ending over an authenticated session with the feature-gated `PeeringTermination` (spec 01 §3.1, type 10) before revoking locally — `--no-notify` skips the dial — and every revocation leaves a fingerprint tombstone (`revoked-peers.json`), so a hub that was away when its spoke ended the peering is refused `revoked`, not `not_paired`, at its next dial. Both deliveries — the announcement received, the refusal inferred — land as durable notices (`Application/NoticeStore`, `notices.json`) surfaced in `status` and the `notices` verb until a human acknowledges them (FR-DEST-008). The refusing side now lingers after writing any refusal, draining until the peer closes, so the refusal is read rather than purged by a transport reset — the difference between learning `revoked` and learning "broken pipe". Both directions are proven end to end over real sockets in `PeerReplicationTests`.

And the terms are now **enforced, not merely stored** ([specification 05](../specifications/peer-protocol/README.md#documents), completing Amendment 2): a destination attributes each replica to the peer that offered it (`Application/ReplicaOwnerStore`, `replica-owners.json`), refuses `terms_refused` at the object boundary when the peer's quota — its total across every repository it owns here, quota 0 declaring no ceiling — would be crossed, and refuses the new `storage_exhausted` (code 12) when its own storage fails, because quota is policy the lender chose and disk trouble is a fault the lender fixes. The source records the three stops distinctly: quota ⇒ failed with a durable notice, disk ⇒ unavailable and retried under back-off, wire ⇒ unavailable. Every hello from a destination carries its current terms for the authenticated peer, the source adopts them into its grant, and a narrowing raises a durable notice before the first refusal would (`Narrows()`, finally called). `pair --quota <bytes>` sets the ceiling at pairing. `PeerQuotaTests` proves all three stops end to end: refusal at the boundary with nothing partial, resumption when the ceiling lifts, and disk trouble told apart from policy.

What is left is the console features gated on the two open questions of ADR-0028 — streaming restored content to the operator (Q18) and per-operator identity on a shared console (Q19). Hub-planned retention against a spoke landed with the hub-and-spoke arc ([spec 06](../specifications/peer-protocol/README.md#documents), [0034](#0034--the-hub-fans-out-ages-and-trims)), and destination verification ([spec 04](../specifications/peer-protocol/04-verification.md)) landed with the Phase-2 close-out: every sync challenges a bounded random sample — the newest snapshot always included — a peer answers keyed range proofs over the wire, a local-path replica answers to direct read-back, and the sync ledger carries `verified_at`/`verified_sequence`/`verified_objects`/`verified_population` so `verified` in a status line is coverage and age from bytes actually read at the destination, never the destination's word. A failed proof marks the pair `Failed`, raises a durable notice, and withholds the success that would have advanced the trim gate.

### 0033 — the OS can own the process

Built, in `FallbackPlan.Agent`: the agent now behaves as a service the operating system starts and stops. `ServiceProcessHost` routes Ctrl+C and the `SIGTERM` that systemd and launchd send onto the one cancellation token the run loop and listeners already unwind cleanly — so a manager's stop is a clean shutdown (exit 0, writer lock freed) rather than the default terminate, proven by a test that spawns the shipped apphost and signals it. `WindowsServiceHost` bridges the Windows Service Control Manager through `ServiceBase` without adopting the Generic Host (ADR-0033). `ServiceUnit` and the `install` verb generate the registration an operator applies — a systemd unit, a launchd plist, or the Windows `sc.exe` commands, printed and never performed — from the same `--repo`/`--state` surface the agent runs with, so the unit cannot drift from the CLI.

Not built, and honestly so: the Windows SCM and launchd *lifecycles* cannot run on this Linux CI, so their live Start/Stop is verified manually while their testable parts — the generation, and the Windows adapter's cancel path — are unit-tested. Self-contained publishing and signed installers remain a Phase 4 concern; the generated artifacts reference whatever executable path is deployed.

### 0034 — the hub fans out, ages and trims

The arc is built end to end; the sections below walk it in the order it landed. **Configuration schema v2** (`Application/ClientConfiguration.cs`, `Application/DestinationConfiguration.cs`): named destinations (the cloud kinds schema-reserved), per-set destination references with optional retention overrides; refuses a destination-less set, a dangling reference, and the v1 schema — the last with the migration in the message. Pinned by `ClientConfigurationTests`.

**Per-set staging archives** (`Agent/ServiceRuntime.cs`, `Agent/ArchiveHandle.cs`): the service takes an `--archives` root and holds one archive handle per backup set — opened lazily, created on the set's first backup, each with its own writer sequence, catalogue and spool, keyed on disk by repository id so the CLI's direct mode (`Cli/CliSession.cs`) names the same files for the same archive. Snapshots, restore, verify, check and status answer across every set's archive. `AgentPassTests` proves two sets get two independent archives with distinct identities.

**Fan-out to local-path destinations** (`FallbackPlan.Replication/StoreToStoreCopier.cs`, `Agent/FanOut.cs`, `Application/DestinationSyncStore.cs`): after each pass's backups, every `(set, destination)` pair converges on the transfer lane — the third `JobScheduler` lane (ADR-0029 amendment) — coalesced by job identity, retried under exponential back-off, every outcome durable in `destinations.json` (FR-DEST-003/004). The copier moves immutable objects in dependency phases — identity, blobs, metadata, snapshots last — so `StoreCopyOrderTests` can kill it after every possible put count and find a lagging-but-valid replica each time, and a re-run converges from the destination's own inventory. `AgentPassTests` proves a destination receives a byte-identical, independently openable archive; an offline destination is recorded `Unavailable` and catches up on a later pass with no command issued; two destinations both converge. **Peer destinations fan out the same way**: the pass pushes the set's archive over the replication exchange (peer-protocol 03) to the endpoint the configuration names under the grant the pairing pinned, recording unreachable and refused distinctly — `PeerReplicationTests` proves a peer converges with no command issued, byte for byte.

**The status matrix** (`Application/StatusModel.cs`, the ADR-0027 amendment made real): the derivation's input is per set, per destination — sync state and verification stamps from the ledger, kind from the configuration, and the four-value failure domain of FR-SNP-007 (declared `failure_domain`, or derived by kind: device-identity comparison for a local path, `same-site` for a peer, `independent` for cloud kinds — [ADR-0018 Amendment 2](adr/0018-replica-failure-domains.md)). `Protected` is earned only by an in-sync destination whose domain survives losing the machine; `Verified` additionally requires a proof covering what the sync delivered, and carries coverage and age. One laggard becomes a warning naming it, every supported destination behind or unreachable is `Degraded`, a reserved kind is a stated incapacity that never manufactures one. The command surface carries the matrix rows under each set's roll-up and the CLI renders them; `ApplicationServiceTests` pins the rollup table, `ServiceTests` pins the rows.

**The retention engine is built and proven against real archives** (`FallbackPlan.Retention`, the architecture 11 placeholder activated). `RetentionPlanner` selects with stated reasons — an absent rule keeps everything, `min_generations` is the floor the other rules cannot override (FR-GC-001). `ReplicationGate` holds any policy-expired snapshot a configured destination has not received, comparing publication sequences to the sequence each sync recorded at its start (`destinations.json` carries it) — never a clock — and a laggard beyond `deferral_days` turns the quiet hold into a warning (FR-GC-009). `StagingMark` surveys the store's own snapshot objects and walks the protected closure; `CollectionPlanner` produces the mandatory dry-run report, treats every intent-covered blob as reachable (FR-GC-003), keeps partially-live blobs whole as the stated compaction backlog, and lets any damage veto the entire pass. `StagingSweep` writes the signed tombstones of [specification 11 §3](../specifications/repository-format/11-lifecycle-objects.md#3-tombstone), waits out a grace counted in the writer's publication sequence ([ADR-0009 Amendment 5](adr/0009-garbage-collection-safety.md#amendment-5-2026-08--the-grace-generation-realised)), revalidates against a world read after the grace check, and only then makes the first production calls to `DeleteAsync`. `RetentionCycleTests` proves the cycle end to end: dry run deletes nothing, apply tombstones and still deletes nothing, the sweep after the next publication removes exactly the condemned snapshots, and the archive keeps walking clean and publishing. The surface is `fallbackplan-agent retention [--apply]` and the `RetentionCommand` on the service contract — dispatched on the writer lane so a pass serialises against backups, with a hold past its deferral bound raised as a durable notice — and a paired console commands the same pass remotely through the CLI `retention` verb.

**Local-path destinations converge under their own policies** (FR-GC-010): fan-out and retention are one operation — `StoreToStoreCopier.ConvergeAsync` pushes a destination's keep-closure and deletes what its policy dropped, in reverse dependency order so an interrupted pass leaves a lagging-but-valid replica; the keep decision is the hub's plan (`Retention/DestinationConvergence`), never the replica's own reachability; staging-only lifecycle objects never replicate; and the gate holds staging expiry only for destinations whose own policy still keeps the snapshot — pushing one a destination would immediately converge away is futile, so it never gates. `DestinationConvergenceTests` proves two destinations of one set holding different ranges, both walking clean, with nothing re-pushed after a drop.

**Peer replicas age under the same plan** ([specification 06](../specifications/peer-protocol/README.md#documents), FR-GC-010): after the object exchange — whose inventory is the ground truth the drop-list is computed from — the hub sends the feature-gated `RetentionOffer` naming exactly the store keys the destination's policy dropped, snapshots first, and the spoke deletes exactly those and answers with the count. The floor is enforced at the spoke's own edge on ciphertext — it counts snapshot objects by prefix, subtracts the named deletions, and refuses the whole instruction below its granted floor — which is the one safeguard that holds when the hub is compromised. `PeerRetentionTests` proves both halves over real sockets: a peer converging to its keep-set with no command issued, and a floor-breaching instruction refused whole with the reason durable at the hub.

**Staging trims to the current generation** ([ADR-0034 §6](adr/0034-hub-and-spoke-destinations.md#6-the-costs-accepted)): every retention pass plans the trim and `--apply` deletes the HISTORIC data blobs every entitled destination verifiably holds — a reachable local-path replica probed key by key (`GetMetadataAsync` per blob), a peer trusted through its sync-ledger claim, anything unverifiable blocking everything it is entitled to. The newest snapshot's closure never trims (it is the dedup cache the next backup reuses against), all metadata stays, and both convergence drop paths condemn only keys staging still lists — a key only the destination holds may be a trimmed blob's last copy. The restore plan is honest about the other half: it follows each manifest's segment references and names the files whose data now lives only at destinations. `StagingTrimTests` proves the flagship (historic blobs trim, the set publishes on, convergence deletes nothing at the replica, the next pass trims what the next publication superseded) and every blocking rule.

**The operator surface caught up**: `sync [--set] [--destination]` converges declared destinations on demand — `SyncCommand`/`SyncResult` at contract 1.2, one transfer-lane pass per pair, answered from the refreshed ledger — as an agent verb and a remote console verb alike; `retention [--apply]` is likewise commandable from a paired console. `replicate` is gone (its pointer error names `sync`), and `ReplicationStateStore` went with it, superseded by the sync ledger.

Three postures are accepted and worth finding here rather than re-deriving: a peer's trim-time ledger claim rests on `SyncedSequence` alone, not the pair's current state — the same trust the replication gate holds, since a later failure does not unmake a completed sync (and a pass whose clock sits behind the last sync refuses the claim outright); the restore PLAN reports absence, never damage — a manifest that is present but will not read is verify's finding, and the plan passes over it; and minor-version feature probing does not exist yet, so a newer console's `sync` against an older service dies as a disconnect rather than a clean "this service is too old" — a known pre-1.0 limitation.

Nothing is still ahead in this arc: FR-DEST-007's destination-removal warnings, the last named remainder, landed with [ADR-0037](adr/0037-configuration-over-the-command-contract.md) — `delete_destination` refuses while referenced and otherwise names what remains at the address. Quotas are enforced with the peer slices — see [0030](#0030--the-socket-exists). The negotiated pairing role and the termination notices landed with the peer slices — either side's ending now surfaces durably on both ends, and a fan-out refused `revoked` raises the notice itself — see [0030](#0030--the-socket-exists).

---

### 0043 — the decision is made, the abstraction is not yet threaded

ADR-0043 is accepted and nothing has been built against it. The repository has no logging today: no `ILogger`, no log file, and two untyped `Action` delegates in the Agent standing in for one. What is decided is the shape — the abstraction package in every library and the sinks in the hosts, `[LoggerMessage]` partials with allocated event-id ranges, redaction by declared type applied where a record crosses the trust boundary, and contract 1.13's diagnostics verbs with a local-full, remote-redacted split. The requirement rows (FR-SVC-010, NFR-OPS-007) name what has to become true, and their traceability rows carry an explicit unmet marker until it is.

## By phase

| Phase | State |
|-------|-------|
| [0 — Archive engine](roadmap.md#phase-0--archive-engine-vertical-slice) | Complete; every exit criterion traced to a named test — with one stated qualifier: the compaction criterion is discharged by its preconditions (nothing physical decodes; supersession converges), the compactor itself being phase 4 |
| [1 — Snapshot and local repository](roadmap.md#phase-1--snapshot-and-local-repository-mvp) | Complete, both pushes |
| [2 — Peer-to-peer and the service boundary](roadmap.md#phase-2--peer-to-peer-backup-and-the-service-boundary) | Complete except deferred-not-planned items (LAN discovery, relay, bandwidth schedules, multi-instance console, Q18/Q19): service boundary on both bindings, peer protocol over a real socket, replication with recovery drill, roles/termination/quotas/retention via the hub-and-spoke arc, and destination verification (spec 04) with `verified` earned from read-back and the four-value failure domains (FR-SNP-007). The web UI, deferred at the phase close, has since landed as the local web console ([ADR-0036](adr/0036-local-web-console.md)) |
| [Hub-and-spoke arc](roadmap.md#the-hub-and-spoke-arc--multi-destination-backup-sets-built) | Built ([ADR-0034](adr/0034-hub-and-spoke-destinations.md)): configuration schema v2, per-set staging archives, local-path and peer fan-out, the status matrix, termination notices, quota enforcement, retention against staging, local-path and peer destinations, the staging trim, and the `sync`/`retention` operator verbs — see [0034](#0034--the-hub-fans-out-ages-and-trims) |
| 3 — Cloud object stores | Not started; reframed as destination kinds behind the arc's fan-out |
| 4 — Retention, GC, compaction | Retention pulled forward into the hub-and-spoke arc; compaction and healing remain here — see [0025](#0025--nothing-compacts-yet-so-nothing-re-seals-yet) |
| 5 — Legacy archive import | Not started, gated on legal review |

---

## What keeps this true

[`eng/check-adr-status.py`](../eng/check-adr-status.py) refuses a build where an ADR is missing from the table above, where a row names an ADR that does not exist, where a state is not one of the four in the legend, or — the one that matters — **where a cited project, directory or type is not on disk.** It is the same discipline `eng/check-requirements.py` applies to the traceability matrix, adopted for the same reason: a status page nobody verifies becomes a status page nobody can trust, and the failure is invisible until someone acts on it.


### 0044 — the ceremony that two requirements have been waiting for

Built, and both gaps it originally scoped out are now closed.

The passphrase half: a service with no passphrase says so on `describe_service`, and the first client to connect walks the operator through choosing one. What made a passphrase-only ceremony possible is that the passphrase was never per-set — `WriteOnlyDerivation` takes no repository identifier, so one `(passphrase, salt, params)` triple stamps every archive an installation will ever create. Setup provisions the installation; each set's staging archive is created from that credential on its first backup, replacing the silent format-1 fallthrough in `ServiceRuntime.ArchiveForAsync`.

**FR-KIT-004** was blocked by the kit *format*, not by the product: a kit demanded a repository id, which demanded an archive, a set and a destination. Kit format v2 drops it ([ADR-0013](adr/0013-recovery-kit.md)'s amendment), so the kit is generated inside the ceremony that already holds the passphrase — one Argon2id pass produces both it and the provisioning envelope — and setup stays in a `kit_required` state until the operator confirms saving it. Backups run in that state deliberately: stopping them over an unsaved kit would lose data to enforce a habit.

**FR-SNP-007** lands on `validate_set_draft`, which the console already calls live while editing, so a set whose every destination sits inside its source's failure domain is warned at the moment it is chosen. It warns on every edit rather than only at first run, since the belief the requirement guards against can form at any point.

Contract 1.14 carries `provision_installation`, `confirm_recovery_kit`, the setup state and device identity on `describe_service`, and the draft's roots and destinations. `CallerScope` is new and is the fact the code was missing — one handler served both listeners and `RemoteBindingState` said only whether the remote binding was on; the pending ADR-0043 diagnostics work reuses it. Q14 is answered: a floor of twelve plus a modest estimate, enforced where a passphrase is chosen and never in `Passphrase.Create`, which is on the restore path.

Proven by drill rather than by test alone: setup writes both kit forms, two sets back up, the **entire state directory is deleted**, and the recovery tool opens each archive from the kit and the passphrase alone — restoring byte-identical files, and opening a second archive the kit was never generated against.

**Two requirements remain unmet and say so in their own traceability rows.** FR-KIT-003: the transcribable text form is built and fixtured, the QR half is not — [recovery-kit §5](../specifications/recovery-kit/README.md#5-qr-form) pins the parameters and defers the rendering. FR-KIT-005: setup records a durable confirmation and nothing more; never-generated/saved/stale as a continuously surfaced status is unbuilt, and since an installation kit carries no destinations, whether it can go stale at all is itself undecided.

### 0035 — a destination has to earn being relied on

The whole arc is built but for one named piece. Before it, the sixteen-range challenge was very nearly the hub's only assurance about a destination, and everything else it knew it learned by trying to use the thing.

**Admission** (`Application/DestinationConfiguration.cs`, `Agent/DestinationProbe.cs`, `Agent/PeerAddress.cs`): a declared address is checked for the defects findable without touching the world — a relative local path, a fingerprint no encoder could produce, an endpoint that is not host:port — and each is reported on the destination's status row. It is emphatically not a validation rule: the load path re-reads and re-validates `config.json` on every property access, several times per scheduler pass, so a throw there would stop every set backing up over one typo. `verify-destination --probe` then answers the question no depth of byte-reading can answer before the first sync: a local path must exist, be a directory and accept a write; a peer must resolve to a grant and a dialable address and complete the handshake including the verification feature. A probe records its failures and never a success, because reaching a destination is not syncing to it.

**Shortfall** (`Agent/FanOut.cs`, `Agent/ReplicationInitiator.cs`): a destination that has been emptied since its last recorded success is caught by collapse in what it declares already holding — the one signal that does not false-positive on a widened keep-set, a resumed partial sync, or a peer that has just gained the retention feature. For a local path the same question is asked of the replica root, checked before the fan-out creates it. Separately, a destination acknowledging fewer objects than it was sent now refuses the session: an under-count without a refusal means a responder bug or a desynchronised stream, and a soft warning there would be learned and ignored.

**No silent fallback** (`Retention/DestinationConvergence.cs`): the keep filter answers with a reason rather than a bare null, so "no policy", "the spoke lacks the feature" and "the graph would not walk" stop being spelled identically. Only the third is a fault; it now raises a self-resolving notice instead of quietly taking a whole copy.

**Confirmation on a schedule** (`Repository/ReplicaSweep.cs`, `Agent/ReplicaSweepJob.cs`, `Agent/Scheduler.cs`): a third scheduler phase re-reads a local-path replica's stored objects against their seals, bounded per pass and resuming from a cursor in `destinations.json`, weekly by default and overridable per destination. It runs on the transfer lane, not the reader lane, because a reader-lane sweep would race a concurrent convergence and manufacture failures. It also compares each swept key's length against the source's, which is not redundancy: the blob reader does not bind the store key to the envelope's blob id, so a valid sealed blob stored under another's key passes the digest check entirely. `verify-destination [--full]` drives the same engine on demand.

**Accumulating coverage** (`Replication/VerificationSampler.cs`): the sync-time challenge rotates from a persisted cursor instead of re-drawing a uniform sample every pass — FR-VER-002's "weighted towards those longest unverified", specified since it was written and until now unimplemented. It sorts its candidates rather than trusting listing order, wraps within the same pass so a completed lap still writes a stamp, and keeps part of a peer's budget unpredictable because a peer answers its own challenge.

**Age and capacity** (`Application/StatusModel.cs`, `Application/DestinationCapacity.cs`, `Protocol/PeerReplicationMessages.cs`): a proof past its bound — seven days local, thirty peer — is named in the warnings without moving `ProtectionState`. A quota-bound peer reports its remaining headroom on the replication inventory frame, and the source warns below a tenth of the loan rather than refusing, because the existing boundary stop already refuses the exact object at the exact moment and keeps the partial progress. A local copy does not start below a 64 MiB free-space floor, recorded `Unavailable` so freeing space heals it unbidden.

**What is not built: peer-side deep verification.** A peer replica has no readable object store this side of the wire — only the range challenge — so re-reading its bytes needs the session-establishment half of the push extracted first. The admission probe took the first half of that extraction (`Agent/PeerAddress.cs`); the rest is deferred and named rather than quietly omitted.

### 0036 — the first front end beyond the CLI

The local web console is built, and it is a client the way FR-SVC-001 always
meant: `FallbackPlan.Web` references the command contract and nothing below it
— a whitelist `ArchitectureTests/DependencyRuleTests` pins to exactly one
project reference — connects over the same local binding the CLI uses, and has
**no direct mode**, because a web server holding the writer role would be a
second writer with a network face. `WebConsoleHost` binds `127.0.0.1` only,
with no flag to widen it; `ConsoleAuth` is the per-run 256-bit token printed in
the start-up URL plus the loopback `Host` check, which together close the
cross-site-request and DNS-rebinding classes a local HTTP console invites. One
endpoint relays any `ServiceCommand` and returns the service's result verbatim;
one bridges `WatchAsync` onto server-sent events; the page itself — status
matrix, snapshot browser, live jobs, notices, and the action surface with
restore and retention-apply behind typed confirmations — is three embedded
static files behind a `default-src 'self'` policy, no framework, nothing
fetched at runtime. An unreachable service renders as **stale with the age of
last contact**, never healthy and never failed (NFR-OPS-006). `Web.Tests`
drives it over real loopback HTTP against a fake client; the refusals are
asserted on status codes and closed-set error codes, not prose.

**What is deliberately not built:** notice acknowledgement (the contract has no
verb for it — the page says so and names the agent verb that does it) and
anything Q18/Q19 gate — restored content never streams to the page, and the
console serves the one operator who launched it. The set-editing UI, a stated
non-goal at first shipping, has since landed with [ADR-0037](adr/0037-configuration-over-the-command-contract.md)
— see the note below.

### 0037 — the configuration lifecycle joins the contract

Contract 1.7: set CRUD with retention and per-destination overrides riding the
descriptor (null preserves, an empty policy clears — a 1.6 client's upsert
changes nothing it cannot say), destination CRUD, `list_pairings`,
`browse_folders` (names only, files on request for the selection tree),
`validate_set_draft` (rule defects verbatim, schedules answered with their next
occurrences), and ADR-0030 Amendment 4's invite verbs. The schedule is parsed
at the boundary and refused with the parser's own defect — before this, a typo
saved cleanly and failed permanently at the next pass. Deletes never cascade
and never erase: a set removal names the staging archive and the copies each
destination keeps (FR-DEST-007, met); a referenced destination refuses,
naming its sets. Edits preserve list position; a destination rename follows
through to referencing sets.

Two things landed under this decision that are bigger than verbs. **Include
rules are now enforced at capture** — `IsCaptured` had no production caller,
so a set could say "photos/** only" in its signed policy manifest and capture
everything; the filter now lives in `Repository/SnapshotPublication.cs`'s tree
publisher, files skipped and empty non-captured directories folded away,
proven by `Repository.Tests/SnapshotPublicationTests` failing first. And the
**invite-authenticated pairing** of ADR-0030 Amendment 4: `PairingInviteStore`
beside the grants, the ceremony's MAC-for-string substitution over transcript
and channel bindings, the listener routing a first-frame `PairOffer` to it,
and the whole path proven over real sockets — the Mallory relay defeated in
invite mode, a spent code refused, nothing pinned on any failure
(`Protocol.Tests/InvitePairingTests`, two real services in
`Hosts.Tests/InvitePairingCommandTests`) — and the arc the invite exists for,
invite to destination to unattended fan-out to restore after the source's
loss, held together end to end in `Hosts.Tests/AlternateSiteTests`.

The web console grew its Configuration surface over all of it: sets with the
folder picker, the selection tree compiling to rules-v1, the schedule builder
previewing real next runs, retention with full-replacement overrides,
destination management, and the invite/pair-with-invite flows.

### 0038 — a set edit answers with its meaning

Contract 1.8. `preview_set_changes` walks a set's source — under its saved
root and rules or a draft's — and diffs it against the last snapshot using
the tree publisher's **own** unchanged predicates (`Repository/ChangeDetection.cs`,
extracted, not copied), so the preview and the next backup are one judgement:
new, updated, metadata-only, moved, deleted, and — kept deliberately apart —
`no_longer_included`, the file a rule edit stopped capturing, which is not a
loss the next backup would even see. Counts are exact; paths sample up to a
cap because the result crosses the contract. A material `upsert_backup_set`
(root or rules changed) answers `configuration_change` naming the edit and
queues a fire-and-forget reader-lane rescan whose counts land as one durable
per-set notice (`set-changed:{id}`), refreshed by later edits and resolved by
the next backup that completes for the set. The CLI grew `changes`, the web
set editor a preview button and a saved-with-meaning dialog — and a material
edit saves only through a two-step confirmation: the comparison shown first,
files that would stop being included called out with a danger-styled Apply,
Back returning to the editor with the draft intact. And `run_backup`'s
`full` flag — accepted and **silently dropped by the service** while direct
mode honoured it — is plumbed through scheduler and runner, proven by a test
that reads `FilesReused == 0` off the progress channel. Recorded costs, not
fixed: a re-included file re-captures with severed ancestry (manifests are
immutable), a root change reads as delete-all-plus-new-all, and a hand-edit
of `config.json` bypasses the rescan hook — the next backup still applies it.

### 0039 — the loops close where the operator lives

Contract 1.9. Notices are a contract surface at last: `list_notices` answers
the ledger structured — id, key, message, raised-at, acknowledged-at, oldest
first, history behind a flag — and `acknowledge_notice` stamps rather than
erases; the console's Notices view acknowledges per row, and the agent's
`notices --ack` now routes through a listening service's live store
(falling back to the file only when nothing holds the state directory),
ending the second-writer race on `notices.json`. A pairing can be **ended**
from a console: `unpair` shares the agent verb's mechanics verbatim
(`Agent/PeerUnpairing.cs` — resolve-or-refuse-ambiguity, best-effort
15-second-bounded termination announcement, revoke, tombstone), is refused
while a configured destination references the fingerprint, and carries an
optional endpoint because the honest order deletes the address book first.
And `list_directory` became a small time machine: per-entry modification
times, new/changed/same markers by recorded object identity against the
set's previous snapshot, the names deleted since it as their own list, and
the predecessor's id — folders deliberately claim nothing, because access
times ride the tree head and the scan itself moves them. The wire `kind`
finally says `directory` (the enum's `directoryplaceholder` had leaked, and
no client's check matched it). The console grew the Pairings table, the
explorer's badges, times, deleted rows and older/newer rail, folder pickers
for restore output and destination paths, a confirmed full re-capture, and
"what changed?" on the overview card.

### 0040 — several folders, one snapshot

Contract 1.10, configuration schema 3. A backup set now captures **several
folders into one snapshot**: one root keeps the pre-multi-root byte shape
exactly, and past one, `Filesystem/MultiRootScan.cs` stitches per-root scans
under a synthetic `/` whose children are label-named directories in raw
UTF-8 byte order, with rule subjects `<label>/<relative>` fed through
`ScanOptions.SubjectPrefix` so walk pruning still works. The snapshot's one
`source_filesystem` map is the conservative intersection of the per-root
probes. Labels are materialised **once, at upsert** (leaf name, `root`
fallback, numeric dedupe) and persisted — never derived on read — and the
1↔N transitions re-anchor the saved rules (label prefix gained or lost; a
stripped single component becomes an exact-path regex rather than widening
to the any-depth shorthand), narrated in the material-change answer. The
runner refuses recoverably naming every missing root; the preview verb
gained draft `roots` and answers a brand-new set against an empty baseline;
the failure domain answers for the weakest root. Restore needed no change:
`<output>/<label>/…` through the existing planner. The console's editor
became a machine-wide checkbox tree — marks inherit from the nearest marked
ancestor, tri-state where a subtree disagrees — compiling to roots plus
excludes only (the deepest fully-ticked folder *is* the root); re-ticking
under an unticked parent triggers the exclude-wins wall (sibling
enumeration plus the new-arrivals warning), and feedback is an instant
summary plus a debounced draft-roots preview walk. Proven live end to end:
reopen, second root, wall, save, backup, labelled browse.

### 0041 — restore walks in through the front door

Contract 1.11, receipt schema 4. Restore became a guided wizard — passphrase,
source, effective date, files, target, run — and every step landed as engine
or contract surface rather than page logic. The passphrase gate runs **in the
console process** against the staging archive's own key files
(`Web/ConsoleRestoreGate.cs`, a real KEK derivation), so NFR-SEC-009's wall
stands untouched; the console dependency rule gained exactly one named
exception for that class. Restore **sources** are server-side handles
(`open_restore_source`): the staging archive, a local-path destination's
replica, or a **paired peer's replica over the wire** — the latter via the
new peer-protocol retrieval feature ([spec 07](../specifications/peer-protocol/07-retrieval.md),
messages 272–277, owner inventory, identical refusals), with
`Agent/PeerRetrievalObjectStore.cs` adapting the session so the repository
open, catalogue rebuild-plus-projection and restore run over the wire
unchanged — proven by a service-level drill that deletes the staging archive
outright and restores byte-identical files from the peer. The planner takes a
prefix **list** (one run, one receipt, ancestor subsumption); the run options
finally reach the wire: `target: original` maps label slices back onto the
set's configured roots, `existing: rename` is the new `WriteBeside` policy
(`name (restored 2026-08-18).ext`, existing file untouched), `overwrite` is
`Replace`, and absent options reproduce the old behaviour byte for byte. The
receipt persists to `<state>/receipts/<run>.json` on every run. Runs against
a source load only the plan's own blobs — a restore-sized transfer, not a
repository-sized one. Proven live: a Playwright walk of all six steps,
wrong-passphrase refusal included, ending on restored bytes and the receipt
path.

### 0042 — the hub that cannot read what it keeps

Built end to end. Repository format v2 severs writing from reading: one
passphrase — entered at setup, at adoption onto a new service instance,
and at restore, and persisted nowhere — derives via Argon2id an X25519
public key that seals file contents, plus the symmetric write bundle
(metadata, content-id, key-id, signing) the service keeps so browsing,
planning, dedup bookkeeping, trim, replication and structural
verification work without it. The private key is never stored in any
form; restore re-derives it, and the grant lives only inside a
restore-source handle. Data-blob footers sit on the structure plane so a
write-only holder still reads its own blobs' record tables; the sealed
spool resumes via the checkpointed content key; verify-on-reuse answers
`Unavailable` rather than pretending. Contract 1.12 carries the two
ceremonies as sealed envelopes to the service's published recipient key
— the one permitted transit shape (NFR-SEC-009 as amended, fenced both
ways by `KeyMaterialConfinementTests`) — and the service starts without
a passphrase when its sets are provisioned. The CLI creates with
`init --write-only --acknowledge-loss` and derives its direct-mode
authority from `--passphrase-env`; the console runs both ceremonies in
its own process. Proven by the end-to-end drills in
`WriteOnlyRepositoryTests` (repository), `WriteOnlySetTests` (service —
including the machine-migration adoption: metadata unreadable on the new
machine until the passphrase re-enters, wrong passphrase refused by
public-key mismatch), `WriteOnlyCommandTests` (CLI),
`WriteOnlyCeremonyTests` (console), the committed
`fixture-repository-v2` read contract, and a live Playwright walk from
the provisioning dialog to byte-identical restored files. Losing the
passphrase loses the backup, acknowledged at setup; v2 has no
passphrase change (03 §7).

What the checker cannot do is judge whether "built" is generous. That is a reading, and it is repeated whenever a phase closes. It also deliberately does not compare these states against each ADR's `Status:` line: that line records whether a *decision* was accepted, which is a different question from whether the code does it, and collapsing the two would lose both.

---

**See also:** [Abandoned choices](decisions-abandoned.md) — what was considered and rejected, and why · [Traceability](requirements/traceability.md) — requirements to tests · [Roadmap](roadmap.md)

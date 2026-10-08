# Implementation status

**Status:** maintained · **Checked by:** [`eng/check-adr-status.py`](../eng/check-adr-status.py)

---

Ninety-two decision records say what this system should do. This says which of them the code actually does, and — where the answer is "some of it" — which part.

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
| [0004](adr/0004-segment-hash-function.md) | Segment hash function; SHA-256 kept, and no field records it (Amendment 1) | **Built** | `Repository.Crypto/ContentHasher`, `Domain/Profiles/ContentHashProfile` · `Repository.ConformanceTests/IdentifierConformanceTests` · measured by `PerformanceTests/HashThroughputBenchmark` |
| [0005](adr/0005-aead-suite-and-nonce-construction.md) | AEAD suite and nonce construction | **Built** | `Repository.Crypto/RecordCipher`, `Repository.Crypto/BlobKeyDeriver` · six requirements, all traced |
| [0006](adr/0006-object-identifiers-and-dedup-trust-domains.md) | Object identifiers and dedup trust domains | **Built** | `Repository/DedupTrustGate` · [notes](#0006--the-integrity-guard-is-built-and-one-thing-is-deliberately-not) |
| [0007](adr/0007-logical-object-identifiers-in-manifests.md) | Manifests carry logical identifiers only | **Built** | `Repository.Format/Manifests/*`, `Repository.Format/Manifests/SourceIdentityHint`, `Repository/SourceIdentityLookup` · `Repository.Tests/Index/IndexPrecedenceTests`, `Repository.Tests/Format/SourceIdentityHintCodecTests` · [notes](#0007--device-specific-facts-live-outside-the-manifest-and-one-of-the-two-is-built) |
| [0008](adr/0008-index-generations-and-checkpoints.md) | Index generations, deltas, checkpoints | **Built** | `Repository.Index/CheckpointCodec`, `Repository.Index/IndexDeltaCodec`, `Repository.Index/WriterSequence`, `Repository.Index/ObservedHead` — the watermarks the decision put in checkpoints are now also read back as a rollback witness, so a writer whose allocation state fell behind its own published history adopts the repository's head at archive open (`Hosts.Tests/ObservedHeadAdoptionTests`); and where the whole state directory rolled back, the destination's journal keys are the witness ([ADR-0062](adr/0062-the-destination-is-the-rollback-witness.md), `Hosts.Tests/DirectoryRollbackTests`) |
| [0009](adr/0009-garbage-collection-safety.md) | Garbage collection safety | **Built** | `Repository.Index/Journal/IntentLifecycle`, `Retention/StagingSweep`, `Retention/CollectionPlanner`, `Retention/RetentionRunner`, `Repository/DedupTrustGate`, `Repository/TombstoneKeys` · `Retention.Tests/RetentionCycleTests`, `Retention.Tests/ClockSkewTests`, `Retention.Tests/CollectionBesideABackupTests`, `Retention.Tests/SweepFailureTests`, `Application.Tests/ClockSkewMarginTests`, `Hosts.Tests/ClockSkewMarginServiceTests`, `Hosts.Tests/RetentionBesideABackupTests`, `InterruptionTests/CompactionInterruptionTests`, `Retention.Tests/CollectionInterruptionTests`, `Hosts.Tests/PeerRetentionInterruptionTests` · [notes](#0009--the-collector-is-built-and-now-so-is-compaction) |
| [0010](adr/0010-local-store-separation.md) | Local store separation | **Built** | `Application/LocalState` · `Repository.Tests/EndToEnd/LocalStateSeparationTests` · `Repository.Tests/Catalogue/CatalogueTests` · `Repository.Tests/Catalogue/CataloguePoolingTests` (catalogue connections are unpooled, [ADR §Amendment 3](adr/0010-local-store-separation.md#amendment-3-2026-09--the-catalogues-connections-are-not-pooled)) · `Agent/ServiceRuntime` · `Agent/CatalogueRebuild` · `Hosts.Tests/CatalogueRebuildAtOpenTests` (a discarded or lost catalogue is rebuilt at open, [ADR §Amendment 4](adr/0010-local-store-separation.md#amendment-4-2026-09--a-catalogue-discarded-is-rebuilt-before-it-is-read)) · `Agent/ArchiveHandle` · `ArchitectureTests/CatalogueConnectionTests` · `Repository.Tests/Catalogue/CatalogueConnectionsTests` (a set's backup has the archive's catalogue connection to itself, and a sync, retention, a deletion and a heal each open their own, [ADR §Amendment 5](adr/0010-local-store-separation.md#amendment-5-2026-10--a-sets-backup-has-the-catalogues-connection-to-itself)) |
| [0011](adr/0011-commit-versus-replication-semantics.md) | Commit versus replication semantics | **Built** | `Application/DestinationSyncStore` (the per-replica half), `Repository/SnapshotPublication` (the commit half) · [notes](#0011-0018--commit-is-per-replica-and-there-are-now-many-replicas) |
| [0012](adr/0012-storage-provider-contract.md) | Storage provider contract | **Partly built** | `Storage.Abstractions`, `Storage.Local`, `Storage.S3`, `Storage.AzureBlob`, `Storage.Abstractions/IPrefixedObjectStore`, `Storage.Abstractions/StoreUnreachableException`, `Storage.Abstractions/StoreUnavailableException`, `Storage.Abstractions/StoreBusyException`, `Storage.Abstractions/StoreFullException`, `Storage.Abstractions/StoreQuotaExceededException`, `Repository/StoreAdmission`, `Repository/RepositoryLifecycle`, `Repository/ReplicaSweep`, `Agent/DestinationShipSink` · `Storage.ContractTests`, `Storage.ContractTests/S3ObjectStoreContractTests`, `Storage.ContractTests/AzureBlobObjectStoreContractTests`, `Storage.ContractTests/S3ObjectStoreTests`, `Storage.ContractTests/AzureBlobObjectStoreTests`, `Repository.Tests/StoreAdmissionTests`, `Retention.Tests/EventualListingTests`, `Storage.ContractTests/CapabilityIntersectionTests`, `Hosts.Tests/ShipSinkCapabilityTests`, `Hosts.Tests/ObjectStoreDestinationTests`, `Hosts.Tests/S3DestinationTests` · [notes](#0012--the-contract-is-real-it-has-three-providers) |
| [0013](adr/0013-recovery-kit.md) | Recovery kit contents and format | **Applied** | Superseded by [ADR-0060](adr/0060-the-passphrase-is-the-recovery-credential.md): no kit exists to be built. What the record set in motion and still stands is the standalone tool, `Recovery/RecoverySession`, which now opens from the passphrase and the descriptor; [notes](#0060--the-passphrase-is-the-recovery-credential) |
| [0014](adr/0014-format-versioning-and-stability.md) | Format versioning and pre-1.0 posture; format 1 withdrawn before freeze (Amendment 1) | **Built** | `Domain/FormatLimits` · `Repository.Format/Descriptor/RepositoryDescriptorCodec` · `Repository/RepositoryLifecycle` · `Repository.Tests/EndToEnd/RepositoryLifecycleTests`, `Repository.Tests/Format/RepositoryDescriptorCodecTests` · [notes](#0014--one-format-and-a-refusal-by-name) |
| [0015](adr/0015-legacy-importer-isolation.md) | Legacy importer isolation | **Partly built** | `FallbackPlan.Import.Abstractions` · [notes](#0015--the-seam-is-the-decision-and-the-seam-is-built) |
| [0016](adr/0016-blob-identifier-formation.md) | Blob identifiers are writer-allocated | **Built** | `Domain/Identifiers/BlobId`, `Domain/IBlobCounterAllocator` · `InterruptionTests/SequenceRollbackTests` holds the refusal when an identifier is ever reused |
| [0017](adr/0017-index-entry-supersession.md) | Index entry supersession and precedence | **Built** | `Repository.Index/IndexEntry`, `Repository.Index/IndexLoader` · `Repository.Tests/Index/IndexPrecedenceTests` |
| [0018](adr/0018-replica-failure-domains.md) | Replica failure domains | **Built** | `Application/StatusModel` · `Repository.Tests/EndToEnd/ApplicationServiceTests` · [notes](#0011-0018--commit-is-per-replica-and-there-are-now-many-replicas) |
| [0019](adr/0019-third-party-dependency-policy.md) | Third-party dependency policy | **Applied** | `ArchitectureTests/DependencyRuleTests` — the policy is a test, not a promise |
| [0020](adr/0020-ed25519-signing-key-semantics.md) | Ed25519 signing key semantics | **Built** | `Repository.Crypto/RepositorySigner` · `Repository.ConformanceTests/Ed25519ConformanceTests` |
| [0021](adr/0021-consume-bodu-via-committed-package-feed.md) | Bodu as prebuilt packages — from nuget.org since Amendment 2, and a package released alone is taken alone since Amendment 3 | **Applied** | [`nuget.config`](../nuget.config), [`Directory.Packages.props`](../Directory.Packages.props), [`external/packages/`](../external/packages/README.md) |
| [0022](adr/0022-standalone-metadata-records-and-index-identifiers.md) | Standalone records and index identifiers | **Built** | `Repository.Format/Records/*`, `Repository.Index/IndexDeltaCodec` · `Repository.FuzzTests/ParserFuzzTests`, `Repository.Tests/Index/IndexPlaneTests` · Decision 7's fifth case, a Completed retirement accounting for every number its intent named ([ADR-0092](adr/0092-a-backup-names-its-blobs-a-batch-at-a-time.md)): `Repository/ReservingIntentScope`, `Repository.Index/WriterSequence` · `Repository.Tests/IntentReservationTests` |
| [0023](adr/0023-cdc-v1-rabin-parameters.md) | cdc-v1 Rabin fingerprint parameters | **Built** | `Repository.Segmentation/RabinFingerprint` · `Repository.FuzzTests/CdcPropertyTests` |
| [0024](adr/0024-include-exclude-rule-dialect.md) | Include/exclude rule dialect | **Built** | `Domain/PathRules` · `Repository.ConformanceTests/PathRulesConformanceTests` |
| [0025](adr/0025-compaction-reseals-records.md) | Compaction re-seals records | **Built** | `Repository/BlobCompactor`, `Repository/CompactionPublication`, `Repository/CompactionPass`, `Retention/CompactionPolicy` — but as [0067](#0067--the-rewrite-that-holds-no-key)'s keyless rewrite, not as this record's re-sealing, which format 3 superseded and format 2 never reached · `Repository.Tests/Index/CompactionIndexTests`, `InterruptionTests/CompactionInterruptionTests`, `Hosts.Tests/CompactionRetentionTests` · [notes](#0025--decrypt-and-reseal-superseded-for-format-3) |
| [0026](adr/0026-phase-1-capture-shapes.md) | Phase-1 capture shapes | **Partly built** | `Filesystem.Local/LocalFileSystemSource`, `Filesystem.Local/PosixInterop`, `Filesystem.Local/PosixHandleInterop`, `Filesystem.Local/PosixDirectoryScope` · `Filesystem.Tests/LocalScanTests` · [notes](#0026--the-shapes-are-captured-the-posix-traversal-is-handle-relative-and-one-gap-is-left) |
| [0027](adr/0027-services-scheduling-status-telemetry.md) | Scheduling, job state, status, telemetry | **Built** | `FallbackPlan.Agent`, `Application/JobStateStore`, `Domain/Diagnostics/EngineDiagnostics` · `Hosts.Tests/*`, `ArchitectureTests/TelemetrySilenceTests`, `Hosts.Tests/DefaultBuildSilenceTests` |
| [0028](adr/0028-service-boundary-and-deployment-topologies.md) | The service boundary | **Partly built** | `FallbackPlan.Api`, `Cli/OperationGateway` · [ADR §Implementation status](adr/0028-service-boundary-and-deployment-topologies.md#implementation-status-2026-08) · `Hosts.Tests/ClientModeTests` — §3's rule holds for writes as well as reads: a backup naming a set and no repository is run by the local service, and a missing one is refused naming both ways forward |
| [0029](adr/0029-pipeline-and-service-concurrency.md) | Pipeline and service concurrency | **Built** | `Repository/ArchiveSession` · `Domain/Configuration/CapturePolicy` (its concurrency bound — the only configured limit the product has) · `Agent/JobScheduler` (a backup cancelled before it starts leaves the queue at the command; a sync or sweep answers only once the queue has released its pair) · [ADR §Implementation status](adr/0029-pipeline-and-service-concurrency.md#implementation-status-2026-08) · [ADR §Amendment (2026-09)](adr/0029-pipeline-and-service-concurrency.md#amendment-2026-09-the-cpu-cap-this-record-anticipated-was-never-built) · [ADR §Amendment 5](adr/0029-pipeline-and-service-concurrency.md#amendment-5-2026-09-a-queued-cancel-is-immediate) · [ADR §Amendment 6](adr/0029-pipeline-and-service-concurrency.md#amendment-6-2026-09-a-job-answers-once-it-has-left-the-queue) |
| [0030](adr/0030-peer-identity-and-pairing.md) | Peer identity and pairing | **Partly built** | `FallbackPlan.Protocol`, `Protocol/PairingInvite.cs` · [notes](#0030--the-socket-exists) |
| [0031](adr/0031-exception-messages-are-resources.md) | Exception messages are resources | **Built** | `Domain/Resources/Strings.g.cs`, `Repository.Format/Resources/Strings.g.cs`, [`eng/generate-resources.py`](../eng/generate-resources.py) · CI: accessors match their resx |
| [0032](adr/0032-mstest-as-the-test-framework.md) | MSTest is the test framework | **Built** | `TestSupport/PlatformFacts.cs`, `TestSupport/PropertyCheck.cs`, `TestSupport/SequenceAssert.cs` · 966 tests, count verified identical across the move · `Hosts.Tests/Parallelism.cs`, `Repository.Tests/Parallelism.cs` and their like in every test project but PerformanceTests and Web.DomTests (classes run concurrently since the 2026-09 amendments) · `Hosts.Tests/TestHookScopeTests` (the product's two test hooks belong to the flow that sets them) |
| [0033](adr/0033-hosting-under-an-os-service-manager.md) | Hosting under an OS service manager | **Partly built** | `Agent/ServiceProcessHost.cs`, `Agent/WindowsServiceHost.cs`, `Agent/ServiceUnit.cs` · [notes](#0033--the-os-can-own-the-process) |
| [0034](adr/0034-hub-and-spoke-destinations.md) | Hub-and-spoke destinations | **Built** | `FallbackPlan.Replication`, `Agent/FanOut`, `Application/DestinationSyncStore`, `Retention/StagingTrim` · `Repository.Tests/EndToEnd/AgentPassTests`, `InterruptionTests/StoreCopyOrderTests`, `Retention.Tests/StagingTrimTests` · [notes](#0034--the-hub-fans-out-ages-and-trims) |
| [0035](adr/0035-destination-fitness.md) | Destination fitness — and, since Amendments 1 to 3, a sweep that reads a whole replica each circuit, a peer's on a stated cadence, repairs what it finds at a local path, and is reported on each destination's status row; an S3-compatible store's on a stated cadence too, repaired as a local path's is ([0091](adr/0091-an-s3-compatible-destination.md) Amendment 1) | **Built** | `Agent/DestinationProbe.cs`, `Agent/PeerAddress.cs`, `Agent/ReplicaSweepJob.cs`, `Repository/ReplicaSweep.cs`, `Repository/ReplicaRepair.cs`, `Agent/ReplicaRepairer.cs`, `Replication/VerificationSampler.cs`, `Application/DestinationCapacity.cs` · `Retention.Tests/DestinationConvergenceTests`, `Replication.Tests/VerificationSamplerTests`, `Hosts.Tests/PeerQuotaTests`, `Hosts.Tests/DeepSweepTests`, `Hosts.Tests/PeerDeepSweepTests`, `Hosts.Tests/DeepSweepCadenceTests`, `Hosts.Tests/S3DestinationTests`, `Cli.Tests/StatusSweepTokenTests`, `Repository.Tests/ReplicaRepairTests` · [notes](#0035--a-destination-has-to-earn-being-relied-on) |
| [0036](adr/0036-local-web-console.md) | The local web console | **Built** | `FallbackPlan.Web`, `Web/WebConsoleHost.cs`, `Web/ConsoleAuth.cs` · `Web.Tests/ConsoleAuthTests`, `Web.Tests/CommandRelayTests`, `Web.Tests/EventStreamTests`, `ArchitectureTests/DependencyRuleTests` · [notes](#0036--the-first-front-end-beyond-the-cli) |
| [0037](adr/0037-configuration-over-the-command-contract.md) | Configuration over the command contract | **Built** | `Agent/ServiceCommandHandler.Configuration.cs`, `Agent/ServiceCommandHandler.Pairing.cs`, `Protocol/PairingInvite.cs` · `Hosts.Tests/ConfigurationCommandTests`, `Hosts.Tests/ServiceSettingsCommandTests`, `Hosts.Tests/InvitePairingCommandTests`, `Protocol.Tests/InvitePairingTests`, `Api.Tests/ConfigurationContractTests`, `Hosts.Tests/LocalPlacementTests`, `Web.DomTests/NewSetWizardDomTests` · [notes](#0037--the-configuration-lifecycle-joins-the-contract) |
| [0038](adr/0038-set-change-rescan-and-notice.md) | Set changes rescanned, and since Amendment 2 backed up at once under the new settings, after any run still capturing under the earlier ones | **Built** | `Repository/SourceComparer.cs`, `Repository/ChangeDetection.cs`, `Agent/SetChangeScan.cs`, `Agent/SetSettingsGenerations`, `Agent/Scheduler` · `Repository.Tests/SourceComparerTests`, `Hosts.Tests/SetChangeTests` · [notes](#0038--a-set-edit-answers-with-its-meaning) |
| [0039](adr/0039-console-operator-loop.md) | The console's operator loop | **Built** | `Agent/PeerUnpairing.cs`, `Agent/ServiceCommandHandler.cs`, `Agent/ServiceCommandHandler.Pairing.cs`, `FallbackPlan.Web` · `Hosts.Tests/NoticeCommandTests`, `Hosts.Tests/UnpairCommandTests`, `Hosts.Tests/DirectoryChangeTests` · [notes](#0039--the-loops-close-where-the-operator-lives) |
| [0040](adr/0040-multi-root-backup-sets.md) | Multi-root backup sets | **Built** | `Filesystem/MultiRootScan.cs`, `Filesystem/ScanRoot.cs`, `Application/ClientConfiguration.cs`, `Agent/ServiceCommandHandler.cs`, `FallbackPlan.Web` · `Repository.Tests/MultiRootPublicationTests`, `Hosts.Tests/MultiRootSetTests` · [notes](#0040--several-folders-one-snapshot) |
| [0041](adr/0041-guided-restore-and-peer-retrieval.md) | The guided restore and peer retrieval — its targeted blob load is no longer what a restore uses ([0068](adr/0068-the-catalogue-directed-restore-read.md)), and its passphrase gate is the service's, checked in the console against what the service publishes (Amendment 1, [0089](adr/0089-a-backups-file-names-need-the-passphrase.md)) | **Built** | `Restore/RestoreExecutor.cs`, `Agent/RestoreSourceRegistry.cs`, `Agent/RetrievalResponder.cs`, `Protocol/PeerRetrievalMessages.cs`, `Web/ConsoleRestoreGate.cs` · `Repository.Tests/RestoreBreadthTests`, `Hosts.Tests/RestoreSourceTests`, `Hosts.Tests/PeerRetrievalTests`, `Web.Tests/RestoreGateTests` · [notes](#0041--restore-walks-in-through-the-front-door) |
| [0042](adr/0042-write-only-repositories.md) | Write-only repositories (format v2) — since 2026-09 the only format | Built | `Repository.Crypto/WriteOnlyDerivation` · `Repository.Crypto/RepositoryWriteCredential` · `Repository.Packing/SealedContentKey` · `Repository/RepositoryLifecycle` · `Agent/WriteOnlyServiceState` · [notes](#0042--the-hub-that-cannot-read-what-it-keeps) |
| [0043](adr/0043-structured-logging-and-diagnostics.md) | Structured logging and client diagnostics; amended by [0081](adr/0081-diagnostic-bundle.md), the redacted rendering is fail-closed, and by [0082](adr/0082-the-recovery-tools-diagnostic-bundle.md), its rule lives in Domain and the recovery tool's bundle renders through it | Built | `Diagnostics/LogRing`, `Diagnostics/RollingFileSink`, `Diagnostics/LoggingComposition`, `Diagnostics/LogRecordRenderer`, `Domain/Diagnostics/LogLevels`, `Domain/Diagnostics/LogLabel`, `Agent/Log.cs` (and one per project), `Application/ClientConfiguration` (schema 4) · `Diagnostics.Tests`, `Application.Tests/LoggingConfigurationTests`, `ArchitectureTests/LoggingShapeTests`, `Repository.Tests/LogPrivacyTests`, `Repository.Tests/EnginePlaneLoggingTests`, `Replication.Tests/CopierLoggingTests`, `ArchitectureTests/TelemetrySilenceTests`, `Hosts.Tests/CommandTraceLoggingTests`, `Web.Tests/SetupCeremonyLoggingTests` · [notes](#0043--the-engine-logs-a-client-reads-it-and-every-declared-message-is-emitted) |
| [0044](adr/0044-first-run-setup.md) | First-run setup and the installation passphrase | Built | `Domain/Configuration/PassphraseStrength` · `Agent/WriteOnlyServiceState` · `Agent/ServiceCommandHandler.Setup.cs` · `Web/ConsoleRestoreGate` · [notes](#0044--the-ceremony-that-two-requirements-have-been-waiting-for). The ceremony ends at the passphrase and the first account: the recovery-kit step, its confirmation and the public-parameters record that let a kit be rebuilt are withdrawn with the kit (ADR-0060), and the installation's public derivation parameters ride the describe verb (contract 1.28) from `Agent/ServiceCommandHandler` instead |
| [0045](adr/0045-client-authentication.md) | Client authentication: username, password, session | Built | `Repository.Crypto/PasswordHash` · `Agent/UserStore` · `Agent/SessionRegistry` · `Agent/AuthenticatingService` · `Cli/SessionCache` · `Repository.Tests/PasswordHashTests`, `Hosts.Tests/UserStoreTests`, `Hosts.Tests/AuthenticationGateTests`, `Hosts.Tests/UnattendedWorkTests`, `Cli.Tests/SessionVerbTests`, `Web.Tests/SessionRelayTests` · [notes](#0045--the-product-can-say-who-is-acting) |
| [0046](adr/0046-direct-to-destination-publication.md) | Direct-to-destination publication: the ship sink, no staging archive | **Partly built** | `Agent/DestinationShipSink` · `Agent/ArchiveHandle` · `Agent/ServiceRuntime` · `Agent/BackupRunner` · `Hosts.Tests/DirectShipTests`, `Hosts.Tests/DirectShipMigrationTests` — the write path, run scoping, sibling catch-up, the no-destination refusal, the destination-backed read paths, and the migration (flip, seed, retire_staging); the direct-ship is the default for new local-path sets (contract 1.23, retention drill run through `Hosts.Tests/DirectShipRetentionTests`); Amendment 1's converge spare keeps a narrow sibling from trimming the last copy of history a wide destination is still owed (`Retention.Tests/DestinationConvergenceTests`, `Hosts.Tests/DirectShipConvergeSpareTests`); the peer write adapter it once named as the remaining tail has landed as [0058](adr/0058-peer-write-adapter.md), and what keeps this row partly built is that a peer-only set still defaults to staging by that record's §8 decision, so the staging machinery has not retired. The hardened round (`Hosts.Tests/DirectShipFaultSweepTests`) runs the 04 §5.1 kill matrix through a two-destination sink and pins behind-exclusion, per-destination seeding drops, the capacity floor, and seed-recorded-as-behind. `Web/ConsoleRestoreGate` resolves repositories through one root list for all three of its ceremonies, so the recovery-kit rebuild and write-only adoption see a direct-ship set's metadata store as the restore gate already did (`Web.Tests/FirstRunSetupTests`, `Web.Tests/WriteOnlyCeremonyTests`), and a direct-ship restore source is named rather than called staging |
| [0047](adr/0047-backup-pool-and-priorities.md) | The backup pool: concurrency, priorities, a pass that never waits for transfers — preemption (Amendments 1–2): a higher-priority arrival suspends the lowest-ranked running backup at a file boundary and resumes it when a slot frees, escalating past an unresponsive victim, with generation-stamped expiry and pause/resume on the progress stream — and one run per set enforced atomically at the enqueue for every trigger door (Amendment 3) | Built | `Agent/JobScheduler` · `Agent/Scheduler` · `Agent/PauseGate` · `Domain/Jobs/IPauseGate` · `Application/ClientConfiguration` (schema 5) · `Application/DestinationSyncStore` (schema 2) · `Hosts.Tests/JobSchedulerPoolTests`, `Hosts.Tests/SchedulerStarvationTests`, `Hosts.Tests/PreemptionTests`, `Hosts.Tests/JobSchedulerPreemptionTests`, `Hosts.Tests/StatusBaselineTests` (contract 1.19's full-backup facts on the status matrix), `Hosts.Tests/ConfigurationCommandTests`, `Hosts.Tests/BackupConcurrencyTests`, `Application.Tests/DestinationSyncStoreTests`  The ledger also carries how much of what each destination is owed it holds (`Application/DestinationSyncStore`, contract 1.24): counted by the sync pass, which lists both sides anyway, rather than by the status read, and reported as uncounted rather than zero where no pass has reached a destination (`Hosts.Tests/DestinationCompletenessTests`); for a peer, which the pass cannot list, counted only under the peer's signed replication receipt ([ADR-0064](adr/0064-replication-receipts.md), `Hosts.Tests/PeerReplicationTests`) |
| [0048](adr/0048-determinate-backup-progress.md) | Determinate backup progress: a backup counts its work before archiving, the plan rides every report (contract 1.20), the hub replays each live job's latest snapshot to a new subscriber, a hung-up watcher is reaped at once, and the console divides by the plan with a time estimate on the jobs page and overview. Amendment 1: the meter divides what is backed up rather than files handled ([0088](adr/0088-a-backups-percentage-is-what-it-has-backed-up.md)) | Built | `Repository/PublicationOrchestrator` (the counting pass and coalesced incremental reporting) · `Domain/Jobs/JobProgress` · `Agent/ProgressHub` · `Api/Transport/ServiceConnectionPump` · `Repository.Tests/SnapshotPublicationTests`, `Hosts.Tests/ProgressHubTests`, `Api.Tests/AbandonedCommandTests`, `Web.Tests/ConsoleProgressScriptTests`, `Web.Tests/EventStreamTests` |
| [0049](adr/0049-service-lifecycle-hygiene.md) | Service lifecycle hygiene: the journal reconciled at start with a notice, cancel settling a run the queue no longer knows, deletion deferring only to queue-active runs, the Owner-only in-process `restart_service` (contract 1.21) on the console and CLI, and the startup configuration record with provenance | Built | `Application/JobStateStore` · `Agent/ServiceRuntime` · `Agent/AgentHost` (the recycle loop, events 3760–3763, and the lost-pass notice with events 3786–3787, FR-SVC-019) · `Agent/AuthenticatingService` · `Hosts.Tests/JournalReconciliationTests`, `Hosts.Tests/RestartServiceTests`, `Hosts.Tests/AgentServiceLifetimeTests`, `Hosts.Tests/AgentHostTests`, `Web.Tests/ConsoleAdminScriptTests` |
| [0050](adr/0050-completed-run-record-and-drill-down.md) | The completed-run record and drill-down: terminal numbers persisted on every journal row, the run diff (`job_changes`) and failure listing (`job_failures`) read from the repository on demand (contract 1.22), the bounded `list_jobs`, every behind demotion carrying its cause with the compared operand on the wire, (Amendment 2) a pair of a set with no snapshot yet reading as awaiting its first backup rather than in sync (contract 1.57), the live feed naming the file being processed, and the error-manifest decoder brought to specification 06 §8.1 | Built | `Application/JobStateStore` · `Agent/BackupRunner` · `Agent/ServiceCommandHandler` · `Application/StatusModel` · `Cli/CliApplication` · `Repository/SnapshotPublication` · `Repository.Format/Manifests/PolicyManifest.cs` · `Hosts.Tests/JobDrilldownTests`, `Hosts.Tests/RecoveryDrillTests`, `Application.Tests/JobRunRecordTests`, `Application.Tests/DestinationStatusTests`, `Api.Tests/ContractAdditiveFieldsTests`, `Web.Tests/ConsoleJobsScriptTests`, `Web.Tests/ConsoleProgressScriptTests`, `Cli.Tests/JobsVerbTests` |
| [0051](adr/0051-local-destination-placement.md) | A local destination lives on its own drive: drive separation as the condition of choosing (volume hard, physical drive where the platform can say), and the protection boundary moved from machine to volume — a second drive earns `protected` with its residue named | Built | `Application/LocalDestinationPlacement` · `Filesystem.Local/PhysicalDisk` · `Filesystem.Local/WindowsInterop` · `Agent/ServiceCommandHandler` · `Application/StatusModel` · `Application.Tests/LocalDestinationPlacementTests`, `Hosts.Tests/LocalPlacementTests`, `Hosts.Tests/LocalPlacementRealVolumeTests`, `Filesystem.Tests/WindowsFileIdentityTests`. Amendment 1 (2026-10) gave the Windows volume probe its native struct layout: it had called every path volume 0, refusing every local destination there. Since ADR-0037 Amendment 2 a draft that names its set is told the same refusal before it is saved. Amendment 2 (2026-10) lets a Debug build allow the binding instead, saying what a Release build refuses, at the save, the draft, a path edit and an adoption: `Agent/BuildConfiguration` answers per build, and `Hosts.Tests/DestinationAdoptionTests` and `Web.DomTests/ConfigEditingDomTests` join the suites above |
| [0052](adr/0052-relocatable-records-format-v3.md) | Format v3: a sealed record stops encoding where it lives — and, with [ADR-0065](adr/0065-merkle-commitment-and-chunk-possession.md), the index commits to a blob in a form a party with neither the blob nor a key can check | **Partly built** | `Domain/FormatVersions` · `Repository.Crypto/RecordKeyDeriver` · `Repository.Packing/SealedRecordKey`, `Repository.Packing/RecordFraming`, `Repository.Packing/SealedContentKeyOpener` · `Repository.Packing/BlobWriter`, `Repository.Packing/BlobReader` · `Repository.Tests/Packing/RelocatableBlobTests`, `Repository.ConformanceTests/FixtureRepositoryV3Tests` · [notes](#0052--the-record-blob-and-index-planes-are-built-and-the-compactor-they-were-for) |
| [0053](adr/0053-peer-claim-and-configuration-recovery.md) | Peer replica claim, and the set's shape in the kit | **Built** | `Protocol/PeerReplicationMessages`, `Agent/ClaimResponder`, `Cli/CliApplication`, `Application/ReplicaOwnerStore`, `Repository.Crypto/WriteOnlyDerivation` — the two-phase ceremony and the key; `Agent/ReplicaReattribution` and `Agent/AgentHost` — §3's operator re-attribution on the contract, at the shell and on the console; `Agent/ReplicationResponder` and `Agent/RemoteServiceListener` — Amendment 4's claim held for the destination operator's acknowledgement, FR-DR-005; §4 closed as will-not-do, its intent met by ADR-0061; [notes](#0053--the-claim-is-built-the-shape-is-not) |
| [0054](adr/0054-scheduled-restore-drills.md) | Recovery drilled on a cadence: a sampled file restored out of each local destination's own replica, recorded per pair with its age and its reason, three states kept apart on the wire (contract 1.25) and in the console, a failure raising a notice rather than blaming the copy, (Amendment 1) a drill interrupted by the service stopping recording nothing at all, (Amendment 4) any other ending recorded as a drill that did not complete and retried on a back-off, (Amendment 5) a set with no snapshot yet never copied, swept or drilled, and a failed drill checked again on the back-off once its replica has synced with no damage standing, and (Amendment 3) a peer drilled on a cadence its source's operator states, never by default, under a byte cap, and (Amendment 6) a drill a person runs now, from the console, the CLI or the service's own command line, recorded and announced as the schedule's, one drill of a pair at a time | Built | `Agent/RecoveryDrillJob` · `Agent/DrillFlights` · `Agent/Scheduler` · `Agent/ServiceRuntime` · `Agent/ServiceCommandHandler` · `Application/DestinationSyncStore` · `Application/DestinationConfiguration` · `Api/Results.cs` · `Hosts.Tests/RecoveryDrillTests`, `Hosts.Tests/DrillNowTests`, `Application.Tests/DestinationSyncStoreTests`, `Hosts.Tests/PeerRecoveryDrillTests`, `Api.Tests/ContractAdditiveFieldsTests`, `Web.Tests/ConsoleDestinationCardTests`; [notes](#0054--what-the-scheduled-drill-does-not-prove) |
| [0055](adr/0055-reclaim-authority.md) | Reclaim authority: tombstones signed under their own derivation domain, withheld from a write-only service's write credential, announced by a required repository feature, granted for one collection run at a time, and carried to a keyless peer as a published public key its retention instructions are signed against | Built | `Repository.Crypto/RepositoryWriteCredential` · `Repository.Crypto/WriteOnlyDerivation` · `Repository.Crypto/ReclaimAuthority` · `Repository.Crypto/RepositoryWriteCredential` · `Repository.Format/Descriptor/RepositoryDescriptorCodec.cs` · `Retention/StagingSweep` · `Agent/ServiceCommandHandler.WriteOnly.cs` · `Agent/AgentHost` · `Web/ConsoleRestoreGate` · `Cli/OperationGateway` · `Protocol/PeerReplicationMessages.cs` · `Application/ReplicaOwnerStore` · `Repository.Tests/ReclaimAuthorityTests`, `Retention.Tests/ReclaimAuthoritySweepTests`, `Retention.Tests/PeerRetentionTests`, `Hosts.Tests/WriteOnlySetTests`, `Hosts.Tests/ClientModeTests`, `Web.Tests/RetentionApplyCeremonyTests`, `Web.DomTests/RetentionApplyDomTests`, `Protocol.Tests/ReplicationMessageTests`, `Application.Tests/ReplicaOwnerStoreTests`; [notes](#0055--what-the-split-defends-and-what-it-does-not) |
| [0056](adr/0056-incremental-reconciliation.md) | A replication pass costs what changed: each dependency phase listed under its own prefix, a gate that skips a pair the last pass left level, a reading-through that comes due on its own cadence, and the publication sequence recorded by the run that shipped it | Built | `Replication/StoreToStoreCopier` · `Application/ReconciliationGate` · `Application/DestinationSyncStore` · `Agent/DestinationShipSink` · `Agent/FanOut` · `Retention/DestinationConvergence` · `Replication.Tests/CopierListingCostTests`, `Application.Tests/ReconciliationGateTests`, `Hosts.Tests/IncrementalSyncTests`; [notes](#0056--what-a-skip-claims-and-what-checks-it) |
| [0057](adr/0057-resumable-object-transfer.md) | A peer transfer cut inside an object resumes: the destination declares what it part holds with a digest of exactly those bytes, the source verifies that claim against its own copy before skipping anything, and the staged prefix is keyed, quota-counted and swept | Built | `Protocol/PeerReplicationMessages.cs` · `Protocol/PeerSessionNegotiation` · `Agent/PartialSpool` · `Agent/ReplicationResponder` · `Agent/ReplicationInitiator` · `Hosts.Tests/PeerResumeTests`, `Protocol.Tests/ReplicationMessageTests`; [notes](#0057--what-resuming-trusts) |
| [0058](adr/0058-peer-write-adapter.md) | A direct-ship set ships to a peer over one replication session held open for the run: the inventory answers what is already there, the acknowledged count must equal what was sent, reads travel a lazily dialled retrieval session, a set with no independent copy of its content is proved by reading the replica back instead — its sealed data plane by the digest tier, which a rebuilt catalogue feeds — and a peer-only set still defaults to staging for reasons the record names | Built | `Agent/PeerShipStore` · `Agent/DestinationShipSink` · `Agent/BackupRunner` · `Agent/FanOut` · `Agent/ServiceCommandHandler` · `Replication/ReplicaVerifier` · `Repository.Catalogue/Catalogue` · `Hosts.Tests/DirectShipPeerTests`, `Hosts.Tests/DirectShipTests`, `Hosts.Tests/PeerReadBackVerificationTests`, `Hosts.Tests/DirectShipVerificationTests`, `Replication.Tests/ReplicaVerifierTests`; [notes](#0058--what-the-adapter-does-not-carry) |
| [0059](adr/0059-session-bound-deletion-authority.md) | A retention instruction is signed over the session it is sent in, and the requirement to sign is gated on the reclaim key the spoke recorded rather than on a feature the sender chooses to offer | Built | `Protocol/SessionBinding` · `Protocol/PeerAuthenticator` · `Protocol/PeerSessionDriver` · `Protocol/PeerReplicationMessages.cs` · `Protocol/PeerSessionNegotiation` · `Agent/ReplicationResponder` · `Agent/RemoteServiceListener` · `Agent/FanOut` · `Hosts.Tests/PeerRetentionReplayTests`, `Protocol.Tests/PeerWireTests`, `Protocol.Tests/ReplicationMessageTests`; [notes](#0059--the-hole-under-the-hole) |
| [0061](adr/0061-adopt-a-destinations-archives.md) | Adopt a destination's archives: the policy manifest records the set's shape, its own retention included since Amendment 1, `discover_archives` / `adopt_archive` take an archive back under its original repository and set ids with the passphrase, the writer identity is resumed, the next backup is incremental; since Amendment 2 the recovered configuration is previewed and takes effect only as confirmed (FR-DR-009); console, CLI and peers, and an S3-compatible store into a staging set ([0091](adr/0091-an-s3-compatible-destination.md) Amendment 1); contract 1.30, retention on the answer at 1.40, the preview and its confirmation at 1.42 | Built | `Repository.Format/Manifests/PolicyManifest` · `Agent/ServiceCommandHandler.Adoption.cs` · `Application/LocalState` · `Web/ConsoleRestoreGate` · `Web/WebConsoleHost` · `Cli/CliApplication` · `Cli/OperationGateway` · `Hosts.Tests/DestinationAdoptionTests`, `Hosts.Tests/PeerAdoptionTests`, `Hosts.Tests/S3AdoptionTests`, `Web.Tests/AdoptionCeremonyTests`, `Web.DomTests/ConfigEditingDomTests`, `Cli.Tests/AdoptVerbValidationTests`, `Repository.Tests/ManifestCodecTests` · `Agent/RecordedRetentionMapping` · [notes](#0061--the-rebuilt-machine-resumes) |
| [0062](adr/0062-the-destination-is-the-rollback-witness.md) | The destination is the rollback witness: a fan-out pass reads the destination's journal head for this writer — a local path's by listing, a peer's from the inventory every push already declares (Amendment 1) — moves the sequence past it, deletes nothing there on the sync pass or the granted collection run, and heals the set in place from the destination — a direct-ship set's metadata store and catalogue, a staging set's content as well, bounded by the closure of the history it lacks (Amendment 2) — over the retrieval session for a peer, one chunk at a time | Built | `Agent/FanOut` · `Agent/ReplicationInitiator` · `Agent/ServiceRuntime` · `Agent/PeerRetrievalObjectStore` · `Repository.Index/ObservedHead` · `Agent/CatalogueRebuild` · `Hosts.Tests/DirectoryRollbackTests`, `Hosts.Tests/PeerRollbackTests`, `Repository.Tests/ObservedHeadTests` · [notes](#0062--the-destination-is-the-witness) |
| [0063](adr/0063-deletion-receipts.md) | Deletion receipts: a destination that deletes on a retention instruction answers with a statement signed under its own device key — the session, the commander, each page as accepted, the keys removed and the count never held — carried in the acknowledgement, verified by the commander against what it sent, filed by both parties and read back by a file-direct verb | Built | `Protocol/DeletionReceipt` · `Protocol/DeletionReceiptStore` · `Protocol/DeletionReceiptReport` · `Protocol/PeerReplicationMessages.cs` · `Agent/ReplicationResponder` · `Agent/RemoteServiceListener` · `Agent/ReplicationInitiator` · `Agent/FanOut` · `Agent/AgentHost` · `Cli/CliApplication` · `Protocol.Tests/DeletionReceiptStoreTests`, `Hosts.Tests/DeletionReceiptVerificationTests`, `Hosts.Tests/PeerRetentionReplayTests`, `Retention.Tests/PeerRetentionTests`, `Cli.Tests/ReceiptsVerbValidationTests`; [notes](#0063--the-peer-planes-audit-record) Since the amendment the pile is bounded by a stated rule swept from file names alone, at filing and at service start (`Protocol/ReceiptRetentionPolicy`, `Protocol/PeerReceiptFiles`, `Agent/ServiceRuntime`, `Protocol.Tests/ReceiptSweepTests`), and a listing is priced by what was asked for rather than by what has accumulated |
| [0064](adr/0064-replication-receipts.md) | Replication receipts: a destination that takes a push answers with a statement signed under its own device key — the session, the commander, the keys it committed and the count, and what it holds for the repository afterwards — carried in the acknowledgement, verified by the commander against what it sent and what the inventory declared, filed by both parties, and the one thing the ledger ever counts a peer complete on; both kinds of receipt read by `receipts --kind`, `list_receipts` (contract 1.33) and the console's Receipts card | Built | `Protocol/ReplicationReceipt` · `Protocol/PeerReceiptFiles` · `Protocol/ReplicationReceiptStore` · `Protocol/ReceiptReport` · `Protocol/PeerReplicationMessages.cs` · `Agent/ReplicationResponder` · `Agent/ReplicationInitiator` · `Agent/FanOut` · `Agent/ServiceCommandHandler.Receipts.cs` · `Agent/AgentHost` · `Cli/CliApplication` · the console's Receipts card · `Protocol.Tests/ReplicationReceiptStoreTests`, `Hosts.Tests/ReplicationReceiptVerificationTests`, `Hosts.Tests/PeerReplicationTests`, `Hosts.Tests/ReceiptsCommandTests`, `Web.Tests/ConsoleReceiptsScriptTests`; [notes](#0064--the-peer-counted-on-its-own-word) Since the amendment the direct-ship run verifies, files and counts the receipt its own acknowledgement carries rather than waiting for a sync pass (`Agent/PeerShipStore`, `Agent/DestinationShipSink`, `Hosts.Tests/DirectShipPeerTests`) |
| [0065](adr/0065-merkle-commitment-and-chunk-possession.md) | The Merkle commitment and the chunk possession challenge: a sealed blob gains an RFC 6962 root over one-mebibyte leaves beside its flat digest, bound to the preimage's length and published as index-delta key 11 by a format-3 writer only; a peer is then asked for one leaf and its authentication path instead of the blob, and the leaf's **bytes** are what the source checks against the root the writer signed | Built | `Repository.Crypto/BlobMerkle` · `Repository.Packing/BlobWriter` · `Repository.Index/IndexDeltaCodec` · `Repository.Catalogue/CatalogueSchema` · `Repository.Catalogue/Catalogue` · `Protocol/PeerRetrievalMessages.cs` · `Protocol/PeerSessionNegotiation` · `Agent/RetrievalResponder` · `Agent/PeerRetrievalClient` · `Replication/ReplicaVerifier` · `Agent/FanOut` · `Application/DestinationSyncStore` (schema 4) · `Api/ContractVersion` (1.34) · `Repository.Tests/Packing/BlobMerkleTests`, `Repository.Tests/Crypto/BlobMerkleBoundaryTests`, `Repository.ConformanceTests/MerkleConformanceTests`, `Protocol.Tests/RetrievalMessageTests`, `Hosts.Tests/PeerReadBackVerificationTests`; [notes](#0065--one-leaf-instead-of-the-blob) |
| [0066](adr/0066-the-format-upgrade-record.md) | The format-upgrade record: a repository moves to a newer format by an appended signed object rather than by a rewritten descriptor — which no copy would accept — so the move rides every ordinary replication path, takes effect at the next sealed object, and leaves everything already sealed exactly as it is; format 3 is what the product creates, and an existing format-2 set is upgraded on request and never pushed | Built | `Repository.Format/Lifecycle/FormatUpgradeRecord` · `Repository/RepositoryLifecycle` · `Domain/FormatLimits` · `Agent/ServiceRuntime` · `Agent/ServiceCommandHandler.Configuration.cs` · `Agent/ReplicationResponder` · `Agent/AgentHost` · `Recovery/RecoverySession`, `Recovery/RecoveryHost` · `Api/ContractVersion` (1.36) · the console's notice control · `Repository.Tests/Format/FormatUpgradeRecordTests`, `Repository.Tests/EndToEnd/EffectiveFormatTests`, `Hosts.Tests/FormatUpgradeTests`, `Web.Tests/ConsoleFormatUpgradeScriptTests`; [notes](#0066--two-objects-carry-one-version) |
| [0067](adr/0067-the-keyless-compactor.md) | The keyless compactor: a blob holding a live minority is rewritten by copying its live records' sealed bytes verbatim into a fresh blob — no content key is held, because at format 3 a record's key is its object's and its nonce rides its own prefix — and the pass publishes supersessions and deletes nothing, leaving the collector to condemn the drained blob on its own terms once every record it held resolves elsewhere | Built | `Retention/CompactionPolicy` · `Retention/CollectionPlanner` · `Retention/RetentionRunner` · `Repository.Packing/BlobReader` · `Repository.Packing/BlobWriter` · `Repository/BlobCompactor` · `Repository/CompactionPublication` · `Repository/CompactionPass` · `Repository.Packing/BlobStoreKeys` · `Repository.Catalogue/Forensic/ForensicRebuilder` · `Agent/ServiceCommandHandler` · `Retention.Tests/CompactionPolicyTests`, `Repository.Tests/Packing/BlobCompactionTests`, `Repository.Tests/Index/CompactionIndexTests`, `Repository.Tests/EndToEnd/CompactedRestoreTests`, `InterruptionTests/CompactionInterruptionTests`, `Retention.Tests/CompactionCollectionTests`, `Hosts.Tests/CompactionRetentionTests`; [notes](#0067--the-rewrite-that-holds-no-key) |
| [0068](adr/0068-the-catalogue-directed-restore-read.md) | The catalogue-directed restore read: a restore loads nothing, reads each record straight from the location the catalogue holds, opens a blob through its footer only when a fast read fails, and coalesces neighbouring records into one ranged read whose first run reaches down to the envelope — 11 GETs over 11 blobs where the same restore cost 93 | Built | `Repository/PrefetchPolicy` · `Repository/RepositoryReader` · `Repository/RestoreEngine` · `Repository.Packing/BlobReader` · `Repository.Packing/RecordFraming` · `Restore/RestoreExecutor` · `Restore/RestoreBlobSet` · `Agent/ServiceCommandHandler` · `Cli/OperationGateway` · `Repository.Tests/RestoreBreadthTests`; [notes](#0068--a-restore-fetches-what-it-needs) |
| [0069](adr/0069-the-background-window.md) | The background window: the first of NFR-PERF-013's four named limits to exist. `HH:mm-HH:mm` in local time, installation-wide in the configuration file, absent meaning always open; it gates exactly what the scheduler starts with nobody waiting and never gates a person. A capture running when the window shuts parks through ADR-0047's own pause gate and the pool holds every background run down until it opens — the suspension reused whole, not rebuilt | Built | `Application/BackgroundWindow` · `Application/ClientConfiguration` · `Agent/Scheduler` · `Agent/JobScheduler` · `Agent/PauseGate` · `Agent/Log` · `Agent/ServiceCommandHandler` · `Api/Results.cs` · `Api/ContractVersion.cs` · `Cli/CliApplication` · `Application.Tests/BackgroundWindowTests`, `Hosts.Tests/BackgroundWindowTests`, `Hosts.Tests/BackgroundHoldTests`, `Web.Tests/ConsoleBackgroundWindowScriptTests`, `Api.Tests/ConfigurationContractTests`, `Api.Tests/ContractAdditiveFieldsTests`; [notes](#0069--one-of-four) |
| [0060](adr/0060-the-passphrase-is-the-recovery-credential.md) | The passphrase is the recovery credential: the recovery kit withdrawn, the recovery tool opening from the passphrase and the archive's own descriptor, first-run setup ending at the passphrase and the first account, contract 1.29 | Built | `Recovery/RecoverySession` · `Recovery/RecoveryHost` · `Repository.Crypto/WriteOnlyDerivation` · `Agent/AgentHost` · `Agent/ServiceRuntime` · `Web/ConsoleRestoreGate` · `Api/ContractVersion` · `Hosts.Tests/RecoveryHostTests`, `Repository.Tests/PassphraseDrillTests`, `Hosts.Tests/FirstRunSetupTests`, `Web.Tests/SetupWizardScriptTests` · [notes](#0060--the-passphrase-is-the-recovery-credential) |
| [0070](adr/0070-replica-claim-after-total-loss.md) | Disaster recovery: the passphrase claims a peer's replica | **Applied** | Superseded by [ADR-0053](adr/0053-peer-claim-and-configuration-recovery.md) Amendment 2: the same property was reached independently on this branch and built there, with `Hosts.Tests/PeerClaimTests` and the claim cases in `Protocol.Tests/ReplicationMessageTests` establishing it. The implementation this record describes was removed in the merge rather than carried across; [notes](#0070--the-disaster-recovery-path-is-written-down-and-not-yet-built) |
| [0071](adr/0071-recovering-operation-after-total-loss.md) | Disaster recovery: the repository carries the set's shape, sealed | **Applied** | Superseded by [ADR-0061](adr/0061-adopt-a-destinations-archives.md), which carries the set's shape in the policy manifest and is established by `Hosts.Tests/DestinationAdoptionTests`. The implementation this record describes was removed in the merge rather than carried across; [notes](#0071--recovering-the-data-was-only-half-of-it) |
| [0072](adr/0072-snapshot-based-capture.md) | Snapshot-based capture: a privileged helper, and what each platform is promised | **Specified only** | [notes](#0072--the-two-things-live-capture-cannot-do) |
| [0073](adr/0073-a-browser-suite-for-the-console.md) | A browser suite for the console | **Built** | `Web.DomTests/SetupCeremonyDomTests` walks the ceremony in real Chromium; `Web.DomTests/RestoreWizardDomTests` walks the wizard against a real archive's gate; views, sign-in, configuration editing and the chrome live beside them; `TestSupport/BrowserFacts` is the skip gate; the dedicated CI job installs the browser and opts in |
| [0074](adr/0074-background-byte-rate-limits.md) | Background byte-rate limits: a destination's `transfer_limit` and the installation's `background_read_limit` pace what the scheduler starts with nobody waiting — never a person; schema 7, contract 1.43 | **Built** | `Application/ByteRate` · `Application/ByteRateLimiter` · `Application/PacedStream` · `Application/PacingClock` · `Agent/BackgroundPacing` · `Agent/PacedObjectStore` · `Agent/PacedFileSystemSource` · `Agent/FanOut` · `Agent/DestinationShipSink` · `Agent/ReplicaSweepJob` · `Agent/RecoveryDrillJob` · `Application.Tests/ByteRateTests`, `Application.Tests/ByteRateLimiterTests`, `Hosts.Tests/BackgroundRateLimitTests`, `Hosts.Tests/PeerRateLimitTests`, `Web.Tests/ConsoleBackgroundLimitsScriptTests` · [notes](#0074--two-more-of-four) |
| [0075](adr/0075-a-restore-reads-around-damage.md) | A restore reads around damage: a restore of a set's own archive reads a record its own store will not serve from the set's other copies, nearest first and verified as any other; receipt schema 5, contract 1.45 | **Built** | `Repository/RepositoryReader` · `Repository/CopySource` · `Restore/RestoreExecutor` · `Restore/RestoreBlobSet` · `Restore/FirstHolderStore` · `Agent/SetCopies` · `Agent/ReplicaRepairer` · `Agent/ServiceCommandHandler` · `Agent/RestoreSourceRegistry` · `Cli/OperationGateway` · `Repository.Tests/ReadAroundTests`, `Hosts.Tests/RestoreReadAroundTests`, `Cli.Tests/GatewayRestoreReportTests`, `Web.Tests/ConsoleRestoreResultScriptTests` · [notes](#0075--one-copy-was-never-the-only-one) |
| [0076](adr/0076-damage-is-traced-to-what-needs-it.md) | Damage is traced to what needs it: catalogue schema 8 indexes what each file version and snapshot is made of, the per-snapshot status degrades only what the damage reaches, and the words count the files and snapshots, the files' names kept for whoever unlocks the set (ADR-0089 Amendment 1); ledger schema 7 | **Built** | `Repository.Catalogue/Catalogue` · `Repository.Catalogue/CatalogueSchema` · `Repository/DamageScope` · `Repository/SnapshotPublication` · `Repository/CatalogueProjector` · `Repository.Catalogue/Forensic/ForensicRebuilder` · `Application/SnapshotReplication` · `Application/DestinationSyncStore` · `Agent/DamageReachText` · `Agent/ReplicaSweepJob` · `Agent/FanOut` · `Agent/ServiceCommandHandler` · `Repository.Tests/CatalogueReachTests`, `Repository.Tests/DamageReachTests`, `Application.Tests/SnapshotReplicationTests`, `Hosts.Tests/DamageScopeTests` · [notes](#0076--a-scope-a-person-can-act-on) |
| [0077](adr/0077-observed-clock-skew.md) | Observed clock skew: a peer's verified replication receipt, bracketed by this machine's clock, is a reading of how far the two clocks stood apart. It waits on the ledger (schema 8), and the set's next capture signs it into its manifest as key 14, read back per snapshot through catalogue schema v9 and contract 1.47 | **Built** | `Application/ClockObservation` · `Application/DestinationSyncStore` · `Agent/ReplicationInitiator` · `Agent/PeerShipStore` · `Agent/ReplicationResponder` · `Agent/FanOut` · `Agent/DestinationShipSink` · `Agent/BackupRunner` · `Repository/SnapshotPublication` · `Repository/CatalogueProjector` · `Repository.Catalogue/Catalogue` · `Repository.Catalogue/CatalogueSchema` · `Repository.Catalogue/Forensic/ForensicRebuilder` · `Agent/ServiceCommandHandler` · `Cli/CliApplication` · `Domain/Status/ObservedClockSkewText` · `Recovery/RecoveryHost` · `Application.Tests/ObservedClockSkewTests`, `Repository.Tests/ObservedClockSkewTests`, `Hosts.Tests/ObservedClockSkewServiceTests`, `Cli.Tests/SnapshotClockTokenTests`, `Web.Tests/ConsoleSnapshotClockScriptTests` · [notes](#0077--a-clock-can-only-be-compared) |
| [0078](adr/0078-implausible-capture-times.md) | Implausible capture times: a snapshot whose recorded time does not fit the order its writer published it in, by more than the configured skew margin, is flagged and kept. It is never expired on that time, fills no min-generations place and represents no bucket, at the hub and in every destination's keep-set. The report, contract 1.48's listing and a notice say so | **Built** | `Retention/RetentionPlanner` · `Retention/StagingMark` · `Retention/RetentionRunner` · `Retention/DestinationConvergence` · `Retention/StagingTrim` · `Application/NoticeStore` · `Agent/ImplausibleCaptureNotice` · `Agent/ServiceCommandHandler` · `Agent/FanOut` · `Agent/BackupRunner` · `Api/Results` · `Api/ContractVersion` · `Cli/CliApplication` · `Retention.Tests/ImplausibleCaptureTimeTests`, `Retention.Tests/ImplausibleCaptureRetentionTests`, `Hosts.Tests/ImplausibleCaptureServiceTests`, `Application.Tests/NoticeStoreTests`, `Cli.Tests/SnapshotImplausibleTimeTokenTests`, `Web.Tests/ConsoleSnapshotImplausibleTimeScriptTests` · [notes](#0078--the-order-a-writer-published-in-is-the-one-a-clock-cannot-move) |
| [0079](adr/0079-sparse-restore.md) | Sparse restore: a hole is hashed as the zeroes it reads as and skipped rather than written, in the engine's spool, in what the engine emits and in the recovery tool, with the length set once the last piece is placed. On Windows a file that will have a hole is marked sparse first. A destination that cannot seek, or already holds bytes where the file goes, is given the zeroes written out. Capture now describes a file that is one hole | **Built** | `Domain/SparseFile` · `Repository/RestoreEngine` · `Repository/SnapshotPublication` · `Recovery/RecoverySession` · `Restore/RestoreExecutor` · `Cli/CliApplication` · `Repository.Tests/SparseRestoreTests`, `Domain.Tests/SparseFileTests`, `TestSupport/AllocatedSize` · [notes](#0079--a-hole-and-a-written-zero-read-the-same) |
| [0080](adr/0080-a-person-deletes-a-snapshot.md) | A person deletes a snapshot, from staging and every copy: `delete_snapshots` (contract 1.51) under the set's reclaim grant. The request is a tombstone of reason *requested* that every survey reads and every plan expires. Staging keeps the snapshot until every declared destination has converged since the request, one with no rules included, and one command converges every copy, carries the deletion through in two passes and records who asked. A set keeps something to restore from | **Built** | `Retention/SnapshotDeletion` · `Retention/StagingMark` · `Retention/RetentionPlanner` · `Retention/ReplicationGate` · `Retention/StagingSweep` · `Retention/RetentionRunner` · `Repository.Format/Manifests/Tombstone` · `Repository.Index/Journal/JournalRecordCodec` · `Repository.Catalogue/Catalogue` · `Application/DestinationSyncStore` · `Agent/FanOut` · `Agent/ReplicationInitiator` · `Agent/ServiceCommandHandler.Deletion` · `Agent/AuthenticatingService` · `Api/Commands` · `Api/Results` · `Api/ContractVersion` · `Cli/OperationGateway` · `Cli/CliApplication` · `Web/WebConsoleHost` · `Retention.Tests/SnapshotDeletionPlanTests`, `Retention.Tests/SnapshotDeletionCycleTests`, `Retention.Tests/SnapshotDeletionFanOutTests`, `Retention.Tests/SnapshotDeletionPeerTests`, `Retention.Tests/SnapshotDeletionServiceTests`, `Repository.ConformanceTests/TombstoneConformanceTests`, `Web.Tests/SnapshotDeletionCeremonyTests`, `Web.DomTests/SnapshotDeletionDomTests` · [notes](#0080--the-request-is-a-tombstone-and-a-missing-row-is-not-a-missing-copy) |
| [0081](adr/0081-diagnostic-bundle.md) | A diagnostic bundle, and a redacted rendering that withholds what no type declares: `export_diagnostics` (contract 1.52) builds one zip whose every field is classified by the types the log uses, paths only by a per-bundle opt-in a paired console may not make; the redacted rendering is fail-closed, `LogLabel` vouches for safe words, identifiers held as text are `LogId`s, and every product log hole is classified | **Built** | `Diagnostics/LogRecordRenderer` · `Domain/Diagnostics/LogLabel` · `Domain/Diagnostics/LogId` · `Storage.Abstractions/ObjectKey` · `Agent/DiagnosticBundle` · `Agent/ServiceCommandHandler.Diagnostics` · `Api/Commands` · `Api/Results` · `Api/ContractVersion` · `Cli/CliApplication` · `Hosts.Tests/DiagnosticBundleTests`, `Diagnostics.Tests/RedactedRenderingTests`, `Hosts.Tests/DiagnosticsCommandTests`, `ArchitectureTests/LoggingShapeTests`, `Cli.Tests/DiagnosticsExportVerbTests`, `Web.Tests/DiagnosticsRelayTests`, `Web.DomTests/ConsoleViewsDomTests` · [notes](#0081--a-bundle-needed-the-redaction-to-be-true-first) |
| [0082](adr/0082-the-recovery-tools-diagnostic-bundle.md) | The recovery tool's diagnostic bundle: every verb takes a bundle file and writes a report of that run however it ended, through the rule the service's log uses, which moved to Domain so the tool could reach it; no passphrase, key, salt, sealing key, creator or machine name, identifiers shortened, paths only by an opt-in for that run | **Built** | `Domain/Diagnostics/RedactedRendering` · `Recovery/RecoveryBundle` · `Recovery/RecoveryRun` · `Recovery/RecoveryNote` · `Recovery/RecoverySession` · `Recovery/RecoveryHost` · `Hosts.Tests/RecoveryBundleTests`, `Domain.Tests/RedactedRenderingRuleTests`, `ArchitectureTests/DependencyRuleTests`, `Repository.Tests/RecoveryContainmentTests` · [notes](#0082--the-tool-that-runs-on-the-worst-day-says-what-happened) |
| [0083](adr/0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) | A restore says whether it fits, and what it will not write back. The plan measures the room each volume the run writes to needs, against what is free there: files in whole clusters with their holes skipped, a cluster a directory, room for the largest file in the engine's working copy, and credit only for what the existing-file policy frees. The run refuses before writing anything unless told to ignore free space (contract 1.53). The plan counts each captured attribute the target will not get back and names the privilege ownership needs, and receipt schema 6 names it per item; since [0084](adr/0084-a-restore-writes-back-the-times-it-can-set.md) the times are written back and the list records what each write did | **Built** | `Restore/RestoreSpace` · `Restore/RestoreMetadata` · `Restore/RestoreBlobSet` · `Restore/RestoreExecutor` · `Agent/ServiceCommandHandler` · `Agent/ServiceRuntime` · `Api/Commands` · `Api/Results` · `Api/ContractVersion` · `Cli/OperationGateway` · `Cli/CliApplication` · `Repository.Tests/RestoreSpaceTests`, `Repository.Tests/RestoreMetadataHonestyTests`, `Repository.Tests/RestoreBreadthTests`, `Hosts.Tests/RestoreHonestyServiceTests`, `Api.Tests/ConfigurationContractTests`, `Cli.Tests/RestoreHonestyCommandTests`, `Web.DomTests/RestoreWizardDomTests` · [notes](#0083--a-plan-that-knew-the-disk) |
| [0084](adr/0084-a-restore-writes-back-the-times-it-can-set.md) | A restore writes back the times it can set, and records what each write did: access times everywhere, and creation times on Windows and macOS through a call that refuses where it cannot set one rather than writing the modification time in its place. Each attribute is written on its own after the content, so a write the platform refuses, or a time no file can carry, is listed as not applied and neither fails the item nor ends the run. Receipt schema 6 keeps its shape, and its list now says what landed | **Built** | `Domain/FileTimes` · `Restore/RestoreExecutor` · `Restore/RestoreMetadata` · `Restore/RestorePlan` · `Domain.Tests/FileTimesTests`, `Repository.Tests/RestoreMetadataHonestyTests`, `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` · [notes](#0084--the-receipt-says-what-landed) |
| [0085](adr/0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) | A restore gives a file back to its owner where it may: owner and group, captured by name, are resolved on the target and given where the restoring account may give them, which is to anyone for root or CAP_CHOWN and otherwise only to the account itself and its groups. Each is written apart, before the permissions. A set-id bit is kept only with the owner or group it runs as, and dropped and reported otherwise. The plan predicts each file, and declares privilege and a name that resolves to nothing apart. Amendment 1: a quarantine restore keeps set-id bits as any restore does, a decision documented rather than a change | **Built** | `Domain/FileOwnership` · `Restore/RestoreAccount` · `Restore/RestoreMetadata` · `Restore/RestoreExecutor` · `Restore/RestoreBlobSet` · `Domain.Tests/FileOwnershipTests`, `Repository.Tests/RestoreMetadataHonestyTests`, `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests`, `TestSupport/FileOwner`, `TestSupport/PosixAccount` · [notes](#0085--whose-file-it-is) |
| [0086](adr/0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) | A restore gives a folder its own metadata back, once nothing more lands in it: each folder's tree is read for what was captured with the folder, and a folder the run made gets its times, permissions and ownership back by the file's rule once the run has written everything else, deepest folder first. A folder already at the destination keeps its own, nothing is applied through a link, and a folder whose tree will not read is still made. The plan reads each folder's tree, names one the store does not hold, and counts folders apart from files | **Built** | `Restore/RestoreExecutor` · `Restore/RestoreBlobSet` · `Restore/RestoreMetadata` · `Domain/FileTimes` · `Repository.Tests/RestoreFolderMetadataTests`, `Domain.Tests/FileTimesTests`, `Hosts.Tests/RestoreHonestyServiceTests` · [notes](#0086--a-folders-own) |
| [0087](adr/0087-a-restore-writes-back-the-extended-attributes-it-may.md) | A restore writes back the extended attributes it may: each captured attribute of a file or a folder is written alone by its captured name, never through a link, after its owner and group and before its permissions, and one left is named in the item's detail. An ACL that names accounts by number comes back only where this installation captured the snapshot, macOS gets no POSIX ACL, and a restore that is not root leaves the security and trusted namespaces. Where an ACL does not come back the group gets no more than it gave. The plan declares each with counts | **Built** | `Domain/ExtendedAttributes` · `Restore/RestoreExecutor` · `Restore/RestoreMetadata` · `Restore/RestoreBlobSet` · `Restore/RestorePlan` · `Restore/RestoreAccount` · `Agent/ServiceCommandHandler` · `Cli/OperationGateway` · `Repository.Tests/RestoreExtendedAttributesTests`, `Domain.Tests/ExtendedAttributesTests`, `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` · [notes](#0087--extended-attributes) |
| [0088](adr/0088-a-backups-percentage-is-what-it-has-backed-up.md) | A backup's percentage is what it has backed up: the plan's bytes whose content the store has acknowledged at every destination the run writes to, or already held, counted in archive order once a blob and every earlier one have landed, with an unchanged or renamed file counted the moment it is reused and each planned file contributing exactly its planned length. Contract 1.54 carries the figure and the finishing work's count, and the source-identity hints went out up to sixteen at once, all before the snapshot record, until [0090](adr/0090-a-backups-hints-are-one-pack.md) made them one pack. Amendment 1 makes two figures of it: a job's bar is its three stages over the plan's files (scanned, processed, backed up, contract 1.55), and each destination's circle is how many of the newest backup's files it holds, worked out from its ledger watermark at rest, counted live during a sync, with a live run folded in by the console; the set's circle is its least complete destination's | **Built** | `Repository/BackedUpTally` · `Repository/ArchiveSession` · `Repository/SnapshotPublication` · `Repository/ManifestBuilder` · `Domain/JobProgress` · `Api/ContractVersion` · `Application/JobStateStore` · `Agent/BackupRunner` · `Repository.Catalogue/Catalogue` · `Agent/DeliveredFiles` · `Agent/FileHoldingCounter` · `Agent/LiveHoldings` · `Agent/DestinationShipSink` · `Repository.Tests/BackedUpProgressTests`, `Hosts.Tests/DestinationFilesHeldTests`, `Web.DomTests/BackupProgressDomTests`, `Web.Tests/ConsoleProgressScriptTests`, `Api.Tests/ContractAdditiveFieldsTests`, `Hosts.Tests/JobsVerbServiceTests` · [notes](#0088--what-is-backed-up) |
| [0089](adr/0089-a-backups-file-names-need-the-passphrase.md) | A backup's file names need the passphrase: the service names a file a backup holds only through a restore source opened under a verified grant, held by the session that opened it and of the set asked about, refusing a snapshot's listing, a restore's plan and run, and a run's changes and failures without one; a change preview without one counts what only the backup names and leaves the names out (contract 1.56). The console derives a grant per set under the published facts and asks at every look, slowing a run of wrong passphrases per account; the CLI derives under `--passphrase-env`. Amendment 1: the notices the service raises, a drill's failure on the status matrix and verify-destination's line count the files they concern, and a notice's files are answered only through a source the set's passphrase unlocked (contract 1.59) | **Built** | `Agent/ServiceCommandHandler` · `Agent/AuthenticatingService` · `Agent/RestoreSourceRegistry` · `Agent/RecoveryDrillJob` · `Agent/DamageReachText` · `Application/NoticeStore` · `Api/ContractVersion` · `Web/ConsoleRestoreGate` · `Web/RestoreGateThrottle` · `Web/WebConsoleHost` · `Cli/GrantedSources` · `Cli/OperationGateway` · `Hosts.Tests/PassphraseGateTests`, `Hosts.Tests/NoticeNamesTests`, `Application.Tests/NoticeStoreTests`, `Cli.Tests/NoticeNamesVerbTests`, `Web.Tests/RestoreGateTests`, `Web.DomTests/PassphraseGateDomTests`, `Web.DomTests/RestoreWizardDomTests`, `Web.Tests/ConsolePassphraseGateScriptTests`, `Api.Tests/ContractAdditiveFieldsTests` · [notes](#0089--the-names-behind-the-passphrase) |
| [0090](adr/0090-a-backups-hints-are-one-pack.md) | A backup's hints are one pack: a publication writes the source-identity hints of the versions it created as one object, type `0x11`, before its snapshot record, rather than one object a version, so they cost a request a backup rather than a request a file. After a catalogue rebuild a reader takes its device's packs once and asks the per-file hints older backups left only for a source key no pack names. NFR-PERF-008 is measured for the first time: no request grows with the number of files, and everything but the blob covers is within the budget. The covers are paid by [0092](adr/0092-a-backup-names-its-blobs-a-batch-at-a-time.md) | **Built** | `Repository.Format/Manifests/SourceIdentityPack` · `Domain/ObjectType` · `Repository.Packing/MetadataStoreKeys` · `Repository/ManifestBuilder` · `Repository/SnapshotPublication` · `Repository/SourceIdentityPackIndex` · `Repository/SourceIdentityLookup` · `Repository.Tests/UploadBudgetTests`, `Repository.Tests/HintPackTests`, `Repository.Tests/SourceIdentityPackCodecTests`, `Domain.Tests/ObjectTypeTests`, `Repository.Tests/BackedUpProgressTests`, `InterruptionTests/TreeSnapshotInterruptionTests` · [notes](#0090--one-pack-a-backup) |
| [0091](adr/0091-an-s3-compatible-destination.md) | An S3-compatible destination: the `s3` kind is served by a provider over the platform's HTTP client with a request signer of its own, the one assembly allowed an HTTP client, every put a create and the shared contract suite passed against a store that speaks the API in process. The address is in the configuration (schema 9) and the access key in the service's state directory, owner-only, arriving only as an envelope sealed to the service for that destination and key id (contract 1.60) from the CLI's `destination-credentials` or the console's editor. A store is synced through the local path's copy and read back with the peer's random share, restored from by listing its prefix when staging is lost, drilled on a stated cadence, probed with one listing and trusted for the staging trim through its ledger; a direct-ship run leaves it behind for the sync after the run. Since Amendment 1 a store is swept on a cadence its operator states, as a peer is, what the sweep finds there is repaired by delete and put, and an archive is adopted from one into a staging set | **Built** | `Storage.S3/S3ObjectStore` · `Storage.S3/S3RequestSigner` · `Storage.S3/S3Location` · `Repository.Crypto/WriteOnlyProvisioning` · `Agent/DestinationCredentialStore` · `Agent/StoreComposition` · `Agent/FanOut` · `Agent/DestinationShipSink` · `Agent/DestinationProbe` · `Agent/RecoveryDrillJob` · `Agent/SetCopies` · `Agent/ReplicaSweepJob` · `Agent/ReplicaRepairer` · `Agent/Scheduler` · `Agent/ServiceCommandHandler.Adoption.cs` · `Application/DestinationConfiguration` · `Application/ClientConfiguration` · `Api/ContractVersion` · `Cli/CliApplication` · `Web/ConsoleRestoreGate` · `Web/WebConsoleHost` · `Storage.ContractTests/S3ObjectStoreContractTests`, `Storage.ContractTests/S3RequestSigningTests`, `Storage.ContractTests/S3ObjectStoreTests`, `Application.Tests/S3DestinationConfigurationTests`, `Repository.Tests/Crypto/AccessKeyEnvelopeTests`, `Hosts.Tests/S3DestinationTests`, `Hosts.Tests/S3AdoptionTests`, `Hosts.Tests/ObjectStoreDestinationTests`, `Hosts.Tests/ObjectStoreAdoptionTests`, `Hosts.Tests/DeepSweepCadenceTests`, `Hosts.Tests/ClientModeTests`, `Web.Tests/DestinationCredentialsCeremonyTests`, `Web.Tests/AdoptionCeremonyTests`, `Web.Tests/ConsoleServiceSettingsScriptTests`, `Web.DomTests/ConfigEditingDomTests`, `Api.Tests/KeyMaterialConfinementTests`, `ArchitectureTests/TelemetrySilenceTests` · [notes](#0091--a-bucket-as-a-destination) |
| [0092](adr/0092-a-backup-names-its-blobs-a-batch-at-a-time.md) | A backup names its blobs a batch at a time: a publication reserves blob numbers before it uses them, its write intent naming the first eight and one extension each later batch, doubling up to 64, durable before the first blob numbered from it is put; a blob it did not number, a resumed spool, is named alone. A Completed retirement accounts for every number the intent named, used or not (ADR-0022 §Decision 7, fifth case), so a completed backup owes nothing and one that dies owes only what it reserved and did not upload. A compaction pass names its whole output in one extension. NFR-PERF-008 is met: 15–18 requests per GiB | **Built** | `Repository/BlobCounterReservation` · `Repository/ReservingIntentScope` · `Repository.Index/WriterSequence` · `Repository/SnapshotPublication` · `Repository/PublicationOrchestrator` · `Repository/CompactionPass` · `Repository.Tests/IntentReservationTests`, `Repository.Tests/UploadBudgetTests`, `InterruptionTests/CompactionInterruptionTests`, `InterruptionTests/StorePutSweepTests`, `InterruptionTests/TreeSnapshotInterruptionTests`, `InterruptionTests/ConcurrentUploadTests` · [notes](#0092--a-batch-at-a-time) |
| [0093](adr/0093-an-azure-blob-destination.md) | An Azure Blob destination: the `azure-blob` kind is served by a second provider over the platform's HTTP client with a Shared Key signer of its own, pinned to six vectors from the API's reference client library, and joins the first on the HTTP-client allowlist. Every put is a create carrying its body's MD5; a missing container is a fault, never an empty replica; and the shared contract suite passes under the account key and under a shared access signature. The address is in the configuration (schema 10): an account, a container, a prefix, and an endpoint only for an account the public service does not host. The credential is either of the two, sealed to the service for that destination under a purpose of its own and held where an access key is (contract 1.61), from the CLI's `destination-credentials` or the console's editor; a signature's expiry is read from the token, reported in the listing, refused when already past, and kept to with no request sent once it passes. A container is served by every routine that serves a bucket, through one object-store check, and the host suites hold the two stores to one behaviour | **Built** | `Storage.AzureBlob/AzureBlobObjectStore` · `Storage.AzureBlob/AzureBlobRequestSigner` · `Storage.AzureBlob/AzureBlobLocation` · `Storage.AzureBlob/AzureBlobCredentials` · `Storage.Abstractions/IPrefixedObjectStore` · `Storage.Abstractions/StoreUnreachableException` · `Repository.Crypto/WriteOnlyProvisioning` · `Agent/DestinationCredentialStore` · `Agent/StoreComposition` · `Agent/FanOut` · `Agent/DestinationShipSink` · `Agent/DestinationProbe` · `Agent/RecoveryDrillJob` · `Agent/SetCopies` · `Agent/ReplicaSweepJob` · `Agent/Scheduler` · `Agent/ServiceCommandHandler.DestinationCredentials.cs` · `Agent/ServiceCommandHandler.Adoption.cs` · `Application/DestinationConfiguration` · `Application/ClientConfiguration` · `Api/ContractVersion` · `Cli/CliApplication` · `Web/ConsoleRestoreGate` · `Web/WebConsoleHost` · `Storage.ContractTests/AzureBlobRequestSigningTests`, `Storage.ContractTests/AzureBlobObjectStoreContractTests`, `Storage.ContractTests/AzureBlobObjectStoreTests`, `Application.Tests/AzureBlobDestinationConfigurationTests`, `Repository.Tests/Crypto/AzureCredentialEnvelopeTests`, `Hosts.Tests/ObjectStoreDestinationTests`, `Hosts.Tests/AzureBlobDestinationTests`, `Hosts.Tests/ObjectStoreAdoptionTests`, `Hosts.Tests/AzureBlobAdoptionTests`, `Hosts.Tests/ClientModeTests`, `Hosts.Tests/DirectShipTests`, `Hosts.Tests/ConfigurationCommandTests`, `Web.Tests/DestinationCredentialsCeremonyTests`, `Web.Tests/AdoptionCeremonyTests`, `Web.DomTests/ConfigEditingDomTests`, `Api.Tests/ConfigurationContractTests`, `Api.Tests/KeyMaterialConfinementTests`, `ArchitectureTests/TelemetrySilenceTests`, `ArchitectureTests/DependencyRuleTests`, `ArchitectureTests/LoggingShapeTests` · [notes](#0093--a-container-as-a-destination) |

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

### 0009 — the collector is built, and now so is compaction

The half that protects data came first: write-intent journal records, the intent lifecycle, and the rule that any component creating a blob publishes an intent first — including the collector, per [PT-3](review/2026-08-fix-pressure-test.md). Leases are advisory, as decided.

The collector now exists and reclaims space (`FallbackPlan.Retention`): `StagingMark` walks the protected closure, `StagingSweep` runs the signed-tombstone → grace-by-publication → revalidate → delete cycle, and `StagingTrim` drops historic data blobs every entitled destination verifiably holds — see [0034](#0034--the-hub-fans-out-ages-and-trims) for the whole engine. Every deletion honours the intent survey, so the safety machinery finally protects against a process that runs. **Compaction** (architecture 07 steps 6–9) is built too, as [0067](#0067--the-rewrite-that-holds-no-key): the partially-live blobs the planner used to report as a stated backlog are now named rather than counted, and a `retention --apply` pass rewrites the ones worth rewriting. The intent discipline above is what makes that safe — a half-written compaction blob is covered by the collector's own step 4, so the thing that would delete it is the thing that protects it. For direct-ship sets ([ADR-0046](adr/0046-direct-to-destination-publication.md)) the retention traversal reads through the ship sink and per-destination convergence is the deleting half; the staging trim applies only while a staging archive exists.

**A backup's reuse is covered too** ([Amendment 8](adr/0009-garbage-collection-safety.md#amendment-8-2026-10--a-backup-builds-on-more-than-its-intent-names)). An intent names the blobs a backup creates, not the ones it reuses, and a pass beside a backup of the same set could delete a reused blob. It condemned the blob from the snapshots already published, the backup's own intent ran out the grace, and the pass revalidated against snapshots it had read before the journal. Proving Phase 4 found it, and four rules now close it:
- `DedupTrustGate` refuses a blob that carries a tombstone, so the backup stores those bytes again.
- `RetentionRunner` reads the journal before the snapshots.
- A pass collects no blob while a backup of the set is in flight. That means a live intent inside its declared duration and the margin, or the service's word that the set's backup is queued, running or parked (`Scheduler.LiveBackupOf`).
- A refused snapshot delete holds back the blobs that snapshot reaches.

**Interruption at every write is proven.** A collection pass is killed in front of each write it makes, and so are the staging trim, a destination's convergence and a peer applying a retention instruction; a compaction is killed after each of its steps. At every cut each snapshot still listed restores byte for byte, and the next pass finishes the work (`Retention.Tests/CollectionInterruptionTests`, `Retention.Tests/StagingTrimTests`, `Retention.Tests/DestinationConvergenceTests`, `Hosts.Tests/PeerRetentionInterruptionTests`, `Hosts.Tests/CompactionRetentionTests`). The kill is a cancellation in front of the write, because the sweep and the trim read every other fault as a refusal to report and carry on from. A resumed pass may clear a tombstone a pass sooner than an uncut one: the tail is counted from the tombstone's eligibility, which specification [11 §3.2](../specifications/repository-format/11-lifecycle-objects.md#32-what-a-collector-must-do-before-deleting) now says. The spoke's deletes are cut through `Agent/ReplicationResponder`'s `RetentionStoreDecorator`, and the service's compactions through `Agent/ServiceCommandHandler`'s `CompactionObserver`, both test hooks scoped to the flow that sets them.

**The intent margin is configured** ([Amendment 7](adr/0009-garbage-collection-safety.md#amendment-7-2026-09--the-skew-margin-configured)). Both surveys of the journal, the collector's pass and the check, added a fixed five minutes to an intent's declared duration. They now read `clock_skew_margin_hours`, a day when the configuration file states none, so a collector a day ahead holds an intent for its writer's whole declared hour once its generation has passed. Nothing a pass deletes can show the margin yet, because the key generation never advances and so no intent expires; a pass logs the margin beside that generation instead, and the check names it.

### 0011, 0018 — commit is per-replica, and there are now many replicas

The decision that a snapshot commits per destination rather than globally is in the publication model, and everything that makes it *matter* has since arrived with the hub-and-spoke arc: a set declares several destinations, the sync ledger (`Application/DestinationSyncStore`) carries per-`(set, destination)` state, and failure domains are compared by device identity (`Application/StatusModel`) rather than assumed — the PT-8 placeholder replaced. `Protected` is earned only by an in-sync destination outside the source's failure domain, which is ADR-0018's rule in force. Direct-ship sets ([ADR-0046](adr/0046-direct-to-destination-publication.md)) sharpen the same rule: each destination is a whole repository from its first byte, and the run-scope rules refuse to hand a destination a snapshot without its closure. What has no dedicated test yet is `FR-SNP-007`'s full five-state per-destination snapshot lifecycle; the ledger's coarser states stand in for it and the traceability matrix says so.

### 0012 — the contract is real; it has three providers

`Storage.Abstractions` defines the contract, `Storage.ContractTests` is a reusable suite any provider must pass, and `Storage.Local` passes it. This is the shape the decision asked for, and the shape is what protects the design.

It was one provider for most of its life, and a contract with a single implementation has not been tested by the thing it exists for: the second implementation that disagrees with it. Since 2026-10 there is one ([ADR-0091](adr/0091-an-s3-compatible-destination.md)). `Storage.S3` is a store on the far side of a network whose API differs from the contract in four places — a put overwrites, a range runs short, a delete of nothing succeeds, a body must be hashed before it is sent — and each is bridged in the provider rather than the contract, which did not change. It passes the same suite (`Storage.ContractTests/S3ObjectStoreContractTests`).

A third followed ([ADR-0093](adr/0093-an-azure-blob-destination.md)). `Storage.AzureBlob` speaks the Azure Blob API, which disagrees with the contract in places of its own: a create of a held blob is a 409, a range is asked in a header, and a listing resumes only from a marker the store made. Again each is bridged in the provider, and again the contract did not change. What the two network providers share went into the contract's assembly rather than being written twice: a store under a prefix that can name the folders beneath it (`Storage.Abstractions/IPrefixedObjectStore`), and a store that never answered told apart from one that refused (`Storage.Abstractions/StoreUnreachableException`). It passes the same suite under each of its credentials (`Storage.ContractTests/AzureBlobObjectStoreContractTests`). The record is still *Proposed*, and the revisit it set for itself once a second provider existed is now due.

**The capabilities are read now**, which they were not. Every reader of `StoreCapabilities` in the product asked for the maximum object size, and `ListingConsistency` was read by nothing at all — so two behaviours this record promised for a degraded provider (`Repository/StoreAdmission` Amendment 3 withdraws them) had never been built, and a store declaring no conditional create would have been admitted and would have answered `Created` to a put that overwrote. `Repository/StoreAdmission` now refuses by name at `Repository/RepositoryLifecycle`, split by whether the caller writes or only reads, and `Retention/CollectionPlanner` and `Retention/DestinationConvergence` refuse to reason from absence against a listing that may lag.

**And a store standing in front of others now answers for them.** `Agent/DestinationShipSink` forwarded the local metadata store's capabilities for a store whose blob reads and writes the destinations answer; `Storage.Abstractions/StoreCapabilities.Intersect` is the rule it uses instead — the weakest answer its targets give, with the archival-tier hazard the one member **or**ed rather than **and**ed. That change immediately found `Agent/PeerShipStore` and `Agent/PeerRetrievalObjectStore` declaring a zero maximum object size, by leaving the member at its struct default, which had every direct-ship run to a peer validating its capture policy against a ceiling of nought the moment anything read it. It is the second declaration of that shape this contract has caught in two slices, and both say the same thing: a capability nobody reads is a capability nobody has to get right.

**What that is worth, stated precisely, because a gate no provider can trip is easy to overrate.** The only provider promises everything the engine asks, so both refusals are reachable today only through `TestSupport/LaggingObjectStore` and `TestSupport/DegradedObjectStore`. What has changed is not that a bad provider is stopped — there is none — but that the contract's claims are now falsifiable, and one of them turned out to be false: a collection pass against a lagging snapshot listing condemned the newest backup's blobs and wrote the tombstones. Two obligations are recorded rather than closed: `Agent/DestinationShipSink` forwards the local metadata store's capabilities instead of intersecting them with its destinations', and collection on an eventually-consistent store needs a completeness witness the repository does not have.


**The revisit it asked for is made** ([Amendment 5](adr/0012-storage-provider-contract.md#amendment-5-2026-10--the-revisit-three-providers-and-the-faults-this-record-named)), and the decision is accepted. Three providers pass one suite, and the two network ones are held to every fault case the record named, each answered by a test or as not applicable. Two things the contract left unsaid are now said:
- **A resume token is its entry's key**, and `ListOptions.ResumeAfter` takes any key. Every store already worked this way, and the peer retrieval wire relies on it.
- **A refusal is told apart where a person's action differs.** A store that is busy or full is unavailable, as one that does not answer is (`Storage.Abstractions/StoreUnavailableException`). A quota is a failure with a notice (`Storage.Abstractions/StoreQuotaExceededException`).

Before this, a throttle that outlasted a request's attempts was recorded as failed. The revisit also found the deep sweep listing every blob for each segment it read: a circuit's requests grew with the square of the archive (`Repository/ReplicaSweep`).

**What remains** is the obligation Amendment 3 left, an attested witness that a snapshot listing is complete. Without one, a store whose listings may lag cannot be collected at. No served store is such a store: all three promise strong listings, and the two network ones are held to it through their APIs. The row stays partly built for that alone; nothing a served destination meets is missing.

### 0014 — one format, and a refusal by name

Format 1 was withdrawn before any freeze ([ADR-0014 Amendment 1](adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)), with no installed base
to migrate: the one live installation went through setup and only ever wrote
format 2. `Domain/FormatLimits` named one format version when this was written and now
names two — **3 at creation** and 2 still accepted, both readable
([0052](#0052--the-record-blob-and-index-planes-are-built-and-the-compactor-they-were-for)) —
and `Repository.Format/Descriptor/RepositoryDescriptorCodec` refuses a
descriptor stamped `1` as its own finding — *refuse, never misread* — naming
re-seeding as the remedy; `Repository.Tests/EndToEnd/RepositoryLifecycleTests` and
`Repository.Tests/Format/RepositoryDescriptorCodecTests` hold both halves.
Since [Amendment 2](adr/0014-format-versioning-and-stability.md#amendment-2-2026-09--a-repositorys-version-is-carried-by-two-objects)
a repository's version is carried by **two** objects rather than one — the
descriptor says what it was created at and a signed format-upgrade record says
what it writes ([0066](#0066--two-objects-carry-one-version)) — and the freeze
gate reads against every version a repository may hold, which after an upgrade
is both of them in one repository.

One fact is recorded because it looks like an error until it is explained:
format 2's symmetric containers — metadata blobs and standalone records —
still stamp `1` in their envelopes and associated data. (A format-3
repository stamps its metadata blobs `3`; its standalone records stay `1`,
because a standalone record is never inside a blob and so is never relocated.
`Domain/FormatVersions.ContainerVersion` is the one place that arithmetic
lives.) The symmetric
construction is the one format 1 defined and format 2 kept byte for byte, and
the stamp is authenticated data over bytes already on disk, so
`Domain/FormatLimits` carries it as `SymmetricFormatVersion` beside the format
version proper. The committed `fixture-repository-v2` is the guard: change
either and it stops opening.

### 0015 — the seam is the decision, and the seam is built

ADR-0015's decision was to isolate a legacy importer behind a boundary, not to write one. `FallbackPlan.Import.Abstractions` is that boundary, and phase 0's exit criteria proved it with a synthetic adapter feeding an arbitrary byte stream through the same pipeline ([roadmap](roadmap.md#phase-0--archive-engine-vertical-slice)).

No legacy reader exists and none should yet: it is phase 5 and gated on a legal review that has not happened. The row reads "partly built" rather than "built" so that nobody reads the seam's existence as the feature's.

### 0025 — decrypt-and-reseal, superseded for format 3

**The constraint this record protects is built and the mechanism it decided is not, which is why the row says Built and this section says which.** For format 2 the record ordinal stays in the AAD; `Repository.Tests/Index/IndexPrecedenceTests` holds the supersession rules, and `Repository.Tests/Index/CompactionIndexTests` is now the first thing in the product ever to *write* one.

What compacts is [0067](#0067--the-rewrite-that-holds-no-key)'s keyless rewrite over `Repository.Packing/BlobWriter`'s `AppendSealedRecordAsync`, not this record's decrypt-and-reseal. Format 3 superseded that decision ([0052](#0052--the-record-blob-and-index-planes-are-built-and-the-compactor-they-were-for)), and at format 2 it was never reachable in the first place: a service holds the structure key and not the content key, so re-sealing there needs a passphrase nobody is present to type. A format-2 set is therefore refused by name and pointed at `upgrade_set_format`.

**Amendment 2's twelve exit criteria are answered**, each by a named case: 1, 2, 3, 5, 6, 8, 9, 10 and 11 in `Repository.Tests/Index/CompactionIndexTests`; 4 and 12 in `InterruptionTests/CompactionInterruptionTests`; 7 in `Repository.Tests/EndToEnd/CompactedRestoreTests` and `Retention.Tests/CompactionCollectionTests`. Criterion 9 was not a formality — it caught `ForensicRebuilder` filing one delta per record against a ledger unique on `(writer, sequence)`, which dropped every record after a blob's first.

### 0026 — the shapes are captured, the POSIX traversal is handle-relative, and one gap is left

All ten shapes are built and tested: hardlink groups, the diagnostics vocabulary, capture-status triggers, special files, alternate streams, directory entries, the filesystem capability record, and the catalogue casefold key.

Two of them were only partly true until a coverage pass went looking. Failure reason 4, *changed during read*, was assigned and never produced by any code path; and a file deleted while it was being read was recorded as a clean capture, because revalidation returning "I cannot see it" short-circuited to "nothing changed". Both are fixed, and decision 2 carries an [amendment](adr/0026-phase-1-capture-shapes.md) saying which reading of "no complete read" it meant — the other reading would have made every backup of every machine with an active log file partial, and retention keeps no partial capture as a set's only survivor. `capture_status` still means exactly what decision 3 says, so nothing that consumes it moved. `Repository.Tests/AdverseCaptureTests` covers the mechanism and `Repository.Tests/LocalTreeAdverseCaptureTests` the outcome against real files, including the pair that records where POSIX and Windows genuinely differ: a file held without sharing, or deleted under the reader, is captured cleanly where the walk holds its own content descriptor and is not where capture goes by name.

The traversal underneath them is now handle-relative on POSIX (`Filesystem.Local/PosixDirectoryScope`, `Filesystem.Local/PosixHandleInterop`). Each directory is held open and its children are listed, stat'd, descended into, opened, and readlink'd by raw name bytes against that descriptor, with `O_NOFOLLOW` throughout — so the object that was classified is the object that is read, and revalidation stats the same handle rather than resolving the name again. An object carrying both a directory marker and a link marker is still classified as a link first, which is what keeps a junction from walking the scanner out of the approved root. Windows keeps the path-based walk and gains the identity check instead: a name that has come to mean a different object is recorded as `captured-identity-changed` and not re-read.

What is left is **capturing a POSIX name that is not valid UTF-8**, which the scanner can now open but the pipeline above it cannot carry: the relative path is a host string all the way through rules, the catalogue's path tables, and restore. [Specification 06 §4.3](../specifications/repository-format/06-manifests.md#43-what-name-must-contain) records why storing a lossy one would be worse than refusing it.

The **decision** that half of it depended on is now made rather than pending: where a host string is unavoidable, such a name renders **percent-encoded**, which is the only convention of the three considered that is lossless, valid UTF-8, and typeable back in. The remaining half is cost, and it is real — a byte-native relative path end to end means a catalogue schema bump, a receipt schema bump, byte-native rule matching, and native `openat`/`mkdirat` writes in a restore path that does not reference `Filesystem.Local` at all today. **Deferred past the format freeze deliberately**: the format needs nothing here, today's behaviour is a clean refusal rather than silent loss, and the freeze gate has no claim on it. Nothing will be built against a guess in the meantime, because the guess has been replaced by a rule.

### 0028 — the local binding, not the remote one

Recorded in the ADR's own [implementation status](adr/0028-service-boundary-and-deployment-topologies.md#implementation-status-2026-08) and not duplicated here. In short: writer-role exclusion, the versioned command contract, status aggregation, per-job progress, and a CLI that asks a running service and falls back to direct mode. The keystore unlock of §9 is retired ([ADR-0014 Amendment 1](adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)): the service holds the write credential setup stores and nothing else. The remote binding — once a terminal refusal that bound nothing — now binds a real socket once an administrator names an interface; see [0030](#0030--the-socket-exists) for the transport it waited on.

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

> **Scope note:** this section describes the staging architecture — capture locally, fan out, age, trim. A set flagged `direct_ship` ([ADR-0046](adr/0046-direct-to-destination-publication.md), [see 0046 below](#0046--the-set-that-never-stages)) replaces the WRITE path: publication ships straight to the destinations and the agent keeps a metadata store, while the fan-out below remains its catch-up pump and the retention machinery reads through the sink.

The arc is built end to end; the sections below walk it in the order it landed. **Configuration schema v2** (`Application/ClientConfiguration.cs`, `Application/DestinationConfiguration.cs`): named destinations (the cloud kinds schema-reserved), per-set destination references with optional retention overrides; refuses a destination-less set, a dangling reference, and the v1 schema — the last with the migration in the message. Pinned by `ClientConfigurationTests`.

**Per-set staging archives** (`Agent/ServiceRuntime.cs`, `Agent/ArchiveHandle.cs`): the service takes an `--archives` root and holds one archive handle per backup set — opened lazily, created on the set's first backup, each with its own writer sequence, catalogue and spool, keyed on disk by repository id so the CLI's direct mode (`Cli/CliSession.cs`) names the same files for the same archive. Snapshots, restore, verify, check and status answer across every set's archive. `AgentPassTests` proves two sets get two independent archives with distinct identities.

**Fan-out to local-path destinations** (`FallbackPlan.Replication/StoreToStoreCopier.cs`, `Agent/FanOut.cs`, `Application/DestinationSyncStore.cs`): after each pass's backups, every `(set, destination)` pair converges on the transfer lane — the third `JobScheduler` lane (ADR-0029 amendment) — coalesced by job identity, retried under exponential back-off, every outcome durable in `destinations.json` (FR-DEST-003/004). The copier moves immutable objects in dependency phases — identity, blobs, metadata, snapshots last — so `StoreCopyOrderTests` can kill it after every possible put count and find a lagging-but-valid replica each time, and a re-run converges from the destination's own inventory. `AgentPassTests` proves a destination receives a byte-identical, independently openable archive; an offline destination is recorded `Unavailable` and catches up on a later pass with no command issued; two destinations both converge. **Peer destinations fan out the same way**: the pass pushes the set's archive over the replication exchange (peer-protocol 03) to the endpoint the configuration names under the grant the pairing pinned, recording unreachable and refused distinctly — `PeerReplicationTests` proves a peer converges with no command issued, byte for byte.

**The status matrix** (`Application/StatusModel.cs`, the ADR-0027 amendment made real): the derivation's input is per set, per destination — sync state and verification stamps from the ledger, kind from the configuration, and the four-value failure domain of FR-SNP-007 (declared `failure_domain`, or derived by kind: device-identity comparison for a local path, `same-site` for a peer, `independent` for cloud kinds — [ADR-0018 Amendment 2](adr/0018-replica-failure-domains.md)). `Protected` is earned only by an in-sync destination whose domain survives losing the machine; `Verified` additionally requires a proof covering what the sync delivered, and carries coverage and age. One laggard becomes a warning naming it, every supported destination behind or unreachable is `Degraded`, a reserved kind is a stated incapacity that never manufactures one. The command surface carries the matrix rows under each set's roll-up and the CLI renders them; `ApplicationServiceTests` pins the rollup table, `ServiceTests` pins the rows.

**The retention engine is built and proven against real archives** (`FallbackPlan.Retention`, the architecture 11 placeholder activated). `RetentionPlanner` selects with stated reasons — an absent rule keeps everything, `min_generations` is the floor the other rules cannot override (FR-GC-001). `ReplicationGate` holds any policy-expired snapshot a configured destination has not received, comparing publication sequences to the sequence each sync recorded at its start (`destinations.json` carries it) — never a clock — and a laggard beyond `deferral_days` turns the quiet hold into a warning (FR-GC-009). `StagingMark` surveys the store's own snapshot objects and walks the protected closure; `CollectionPlanner` produces the mandatory dry-run report, treats every intent-covered blob as reachable (FR-GC-003), keeps partially-live blobs whole as the stated compaction backlog, and lets any damage veto the entire pass. `StagingSweep` writes the signed tombstones of [specification 11 §3](../specifications/repository-format/11-lifecycle-objects.md#3-tombstone), waits out a grace counted in the writer's publication sequence ([ADR-0009 Amendment 5](adr/0009-garbage-collection-safety.md#amendment-5-2026-08--the-grace-generation-realised)), revalidates against a world read after the grace check, and only then makes the first production calls to `DeleteAsync`. `RetentionCycleTests` proves the cycle end to end: dry run deletes nothing, apply tombstones and still deletes nothing, the sweep after the next publication removes exactly the condemned snapshots, and the archive keeps walking clean and publishing. The surface is `fallbackplan-agent retention [--apply]` and the `RetentionCommand` on the service contract — dispatched on the writer lane, where it may run beside a backup of the same set, which is why a pass beside a live backup collects no blob ([ADR-0009 Amendment 8](adr/0009-garbage-collection-safety.md#amendment-8-2026-10--a-backup-builds-on-more-than-its-intent-names)), with a hold past its deferral bound raised as a durable notice — and a paired console commands the same pass remotely through the CLI `retention` verb.

**Local-path destinations converge under their own policies** (FR-GC-010): fan-out and retention are one operation — `StoreToStoreCopier.ConvergeAsync` pushes a destination's keep-closure and deletes what its policy dropped, in reverse dependency order so an interrupted pass leaves a lagging-but-valid replica; the keep decision is the hub's plan (`Retention/DestinationConvergence`), never the replica's own reachability; staging-only lifecycle objects never replicate; and the gate holds staging expiry only for destinations whose own policy still keeps the snapshot — pushing one a destination would immediately converge away is futile, so it never gates. `DestinationConvergenceTests` proves two destinations of one set holding different ranges, both walking clean, with nothing re-pushed after a drop.

**Peer replicas age under the same plan** ([specification 06](../specifications/peer-protocol/README.md#documents), FR-GC-010): after the object exchange — whose inventory is the ground truth the drop-list is computed from — the hub sends the feature-gated `RetentionOffer` naming exactly the store keys the destination's policy dropped, snapshots first, and the spoke deletes exactly those and answers with the count. The floor is enforced at the spoke's own edge on ciphertext — it counts snapshot objects by prefix, subtracts the named deletions, and refuses the whole instruction below its granted floor — which is the one safeguard that holds when the hub is compromised. `PeerRetentionTests` proves both halves over real sockets: a peer converging to its keep-set with no command issued, and a floor-breaching instruction refused whole with the reason durable at the hub.

**Staging trims to the current generation** ([ADR-0034 §6](adr/0034-hub-and-spoke-destinations.md#6-the-costs-accepted)): every retention pass plans the trim and `--apply` deletes the HISTORIC data blobs every entitled destination verifiably holds — a reachable local-path replica probed key by key (`GetMetadataAsync` per blob), a peer trusted through its sync-ledger claim, anything unverifiable blocking everything it is entitled to. The newest snapshot's closure never trims (it is the dedup cache the next backup reuses against), all metadata stays, and both convergence drop paths condemn only keys staging still lists — a key only the destination holds may be a trimmed blob's last copy. The restore plan is honest about the other half: it follows each manifest's segment references and names the files whose data now lives only at destinations. `StagingTrimTests` proves the flagship (historic blobs trim, the set publishes on, convergence deletes nothing at the replica, the next pass trims what the next publication superseded) and every blocking rule.

**The operator surface caught up**: `sync [--set] [--destination]` converges declared destinations on demand — `SyncCommand`/`SyncResult` at contract 1.2, one transfer-lane pass per pair, answered from the refreshed ledger — as an agent verb and a remote console verb alike; `retention [--apply]` is likewise commandable from a paired console. `replicate` is gone (its pointer error names `sync`), and `ReplicationStateStore` went with it, superseded by the sync ledger.

Three postures are accepted and worth finding here rather than re-deriving: a peer's trim-time ledger claim rests on `SyncedSequence` alone, not the pair's current state — the same trust the replication gate holds, since a later failure does not unmake a completed sync (and a pass whose clock sits behind the last sync refuses the claim outright); the restore PLAN reports absence, never damage — a manifest that is present but will not read is verify's finding, and the plan passes over it; and minor-version feature probing does not exist yet, so a newer console's `sync` against an older service dies as a disconnect rather than a clean "this service is too old" — a known pre-1.0 limitation.

Nothing is still ahead in this arc: FR-DEST-007's destination-removal warnings, the last named remainder, landed with [ADR-0037](adr/0037-configuration-over-the-command-contract.md) — `delete_destination` refuses while referenced and otherwise names what remains at the address. Quotas are enforced with the peer slices — see [0030](#0030--the-socket-exists). The negotiated pairing role and the termination notices landed with the peer slices — either side's ending now surfaces durably on both ends, and a fan-out refused `revoked` raises the notice itself — see [0030](#0030--the-socket-exists).

---

### 0043 — the engine logs, a client reads it, and every declared message is emitted

Built: the abstraction in all twenty-four projects, a `Log.cs` of `[LoggerMessage]` partials per project with allocated event-id ranges, and `FallbackPlan.Diagnostics` holding the ring (Bodu's `ConcurrentCircularBuffer`), the async rolling file and the redacting renderer. Every host now composes a real `ILoggerFactory`: the agent and the console through `FallbackPlan.Diagnostics`, the web console and the recovery tool through a forty-line console sink each, because `DependencyRuleTests` pins their closures and a project reference would break both. The two untyped `Action` delegates are **deleted**, not deprecated, and their fifteen call sites are typed and levelled.

The level is settable from three of its four sources: `--log-level` on every host, `FALLBACKPLAN_LOG_LEVEL`, and the `logging` object in `config.json` (schema 4 — level, per-category overrides, retention, file size, ring capacity). Precedence is decided in one place, `LoggingOptions.TryResolveLevel`, and a name nobody recognises is refused by name rather than ignored. The vocabulary itself sits in `Domain.Diagnostics.LogLevels` so a file and a flag cannot drift into accepting different spellings.

The client half landed too. Contract **1.15** — not the 1.13 the ADR named, since 1.13 went to first-run setup and 1.14 to the recovery kit — carries `get_diagnostics`, a paginated `read_log` and `set_log_level`, reaching a `fallbackplan logs` verb (`--level`, `--since`, `--tail`, `--follow`) and the console's seventh view. A paired console reads redacted and may not set a level at all. FR-SVC-010's row is filled in.

**Every declaration is called, and that is now a build rule.** Phase 2 declared 107 `[LoggerMessage]` messages and wired 46 of them; the other 61 read exactly like messages the engine emits while emitting nothing. They were frozen in `ArchitectureTests/LoggingShapeTests` as a register that could only shrink, and it has since been emptied — twelve declarations deleted with their reasons recorded in ADR-0043, several more reshaped where the declared message promised a count nothing computes or named something the code does not do, and the rest wired with a logger threaded from the host to the call site. The register and the "or is a known debt" half of the test are gone with it: the rule is simply that every declaration is called. Because a declaration having a call site does not prove a logger reaches it, `Repository.Tests/EnginePlaneLoggingTests`, `Replication.Tests/CopierLoggingTests` and the retention drill assert by event id that records arrive through real publications, copies and passes. `Hosts.Tests/CommandTraceLoggingTests` and `Web.Tests/SetupCeremonyLoggingTests` hold the two trace tiers to the same rule, through a real dispatch and real requests.

**And a sink that reaches the network was never covered.** `ArchitectureTests/LoggingShapeTests` proves only `FallbackPlan.Diagnostics` takes the concrete logging package, which stops a sink arriving as a dependency; it says nothing about a sink that posts records somewhere, because `System.Net.Http` needs no package reference. `ArchitectureTests/TelemetrySilenceTests` closes it, and in doing so closed a gap in the guard beside it: `DependencyRuleTests.AllSourceAssemblies` named twenty-one of the twenty-four `src` projects, omitting `Diagnostics`, `Replication` and `Retention` — the three with no `AssemblyMarker` — under a comment warning that a hand-picked subset is how an unguarded reference got in last time. The logging sinks were the assembly a telemetry rule most needed to cover and the one the list could not see.

### 0046 — the set that never stages

Everything the row names is held by tests, including the 04 §5.1 kill matrix through a two-destination sink (`Hosts.Tests/DirectShipFaultSweepTests`). The gate has since been discharged for local-path sets (ADR-0046 Decision 7's amendment): `direct_ship` rides the contract (1.23) and the console's set editor, a shape flip migrates in-process with its seed queued at once, the retention-with-trimming drill ran (`Hosts.Tests/DirectShipRetentionTests` — and caught the sink stopping sweep deletes at the metadata store, now fanned to the destinations under the replication gate's licence), and a **new set referencing a local-path destination is born direct-ship**. Verification was corrected in 2026-09 on two counts, both of which bit the default shape of a new local-path set. It never ran: challenges live in the sync path and `Agent/Scheduler` queues a pair only when there is something to copy, and a direct-ship set has nothing to copy the moment it converges — so a destination was challenged once and then never again. And what would have run proved nothing: the verifier compared the replica against `Agent/DestinationShipSink`, whose blob reads the destinations themselves answer, so with one destination it was a replica against itself. Challenges are now due on the age of the last proof, and a sampled blob is proved by being *opened* at the replica — its footer and a record's AEAD tag, evidence the destination never held the key to forge (`Hosts.Tests/DirectShipVerificationTests`). Retirement's gate was regated in 2026-09 (Amendment 2) after a live install could not use it: it demanded every non-lifecycle object staging held be present at a destination, and nothing carries an object no live snapshot reaches, so the archive was refused for ever and its disk space held. It now refuses only over a blob the live history reaches that no destination has, or a non-blob object the flip's migration never carried across, and names example keys instead of a bare count. What keeps the row at **Partly built** is one tail: the peer write adapter (a declared peer is a stated `NotSupported` ledger row for direct-ship; peer-only sets default to staging until it lands).

### 0052 — the record, blob and index planes are built, and the compactor they were for

Under [ADR-0025](adr/0025-compaction-reseals-records.md) a record's key comes
from its blob, its nonce is its position in that blob, and its AAD binds that
position again — so a record cannot be moved without being opened, and
compaction is decrypt-and-reseal. Format 3 reverses all three: the key is
`HKDF-Expand(class_key, "fbp/record/v3" ‖ u8(object_type) ‖ object_id)`, the
nonce is drawn per record and carried in the record's prefix, and the AAD is
51 bytes without the ordinal. `Repository.Tests/Packing/RelocatableBlobTests`
copies a sealed record into another blob under a different salt, writer and
derived blob key, at a different ordinal, and reads it back;
`Repository.ConformanceTests/FixtureRepositoryV3Tests` does the same against
bytes frozen in the repository, which is the only version of that claim a
future reader can check.

**What is built.** `Domain/FormatVersions` holds the questions the code asks
of a version — including `ContainerVersion`, since a repository's version and
a blob's stamp are different numbers — and `Domain/FormatLimits` carries
`LatestFormatVersion` beside the creation default.
`Repository.Format/Descriptor/RepositoryDescriptorCodec` implements feature
`0x0003` and the rule that a format-3 descriptor must list it and a format-2
one must not. `Repository.Crypto/RecordKeyDeriver`,
`Repository.Packing/SealedRecordKey` and `Repository.Packing/RecordFraming`
are the record primitives; `Repository.Packing/BlobWriter` and `BlobReader`
are the blob plane, `AppendSealedRecordAsync` included. `Repository/ArchiveSession`
and `Repository/ManifestBuilder` stamp what the repository's effective version
says. **Format 3 is what the product creates**: `Domain/FormatLimits`'s
creation default moved to it and `Agent/ServiceRuntime.ArchiveFormatVersion`
stopped being a test-only seam ([0066](#0066--two-objects-carry-one-version)),
so `Cli/CliApplication`'s `init --format-version 2` is now the way to ask for
the older one rather than the newer. The one live installation is still format
2 and stays there until someone upgrades it — both formats are supported and
nothing pushes.

The index plane followed as
[ADR-0065](#0065--one-leaf-instead-of-the-blob): a format-3 delta publishes a
Merkle root per covered blob, which is what lets a peer be challenged for one
leaf rather than read back whole.

**And the compactor the whole design was for** is built over
`AppendSealedRecordAsync` as [ADR-0067](#0067--the-rewrite-that-holds-no-key),
with [ADR-0025](#0025--decrypt-and-reseal-superseded-for-format-3)'s twelve
exit criteria answered one by one. **What is not.** Nothing compacts a
format-2 repository, and nothing will: a service holds no content key, so
re-sealing there is not expensive but impossible. Format-2 repositories are
read in place forever, so there is no migration waiting to be run — the
remedy for a set that wants the compactor is the append-only upgrade
([ADR-0066](#0066--two-objects-carry-one-version)).

### 0053 — the claim is built, the shape is not

**Decisions 1–3 are built**, and building them changed two of them; ADR-0053's
[Amendment 1](adr/0053-peer-claim-and-configuration-recovery.md) is the record.
`Hosts.Tests/PeerClaimTests` is the drill: archive, configuration, state
directory, installation credential and device keypair all destroyed, a fresh
install paired afresh, and the replica claimed back and read from the
passphrase alone.

Two things the attempt found, worth keeping because they are the kind of thing
that gets re-derived:

**The claim key could not be what §1 said it was.** It derived from the
repository's master key, and a claimant that has lost the repository holds an
installation kit — no repository id, no key object, every key re-derived from
the passphrase and the kit's public salt. So the key is the *installation's*,
`fbp/claim/v2`. (`fbp/claim/v1`, off a format-1 repository's master key, went
with format 1 — [ADR-0014 Amendment 1](adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze).)

**And then the kit went too** ([Amendment 2](adr/0053-peer-claim-and-configuration-recovery.md#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else)),
which took the salt with it. The ceremony is two phases in one session: the
claimant opens with an empty `ReplicationClaimOpen`, the destination answers
`ReplicationClaimParameters` — the distinct KDF salts and costs behind every
replica here whose attribution carries a claim key, read from each replica's
descriptor and never the repository id or the sealing public key — and the
claimant sends one `ReplicationClaim` entry per pair. Argon2id runs in the
`claim` verb after the dial. A wrong passphrase at a peer reads as "nothing
claimable", by design.

**The claim can name no repository either**, for the same reason, and the owner
inventory cannot tell it one because that path is itself gated on attribution.
The claim public key is the selector: `Agent/ClaimResponder` re-attributes
every repository recorded against it and names them in its answer.

**§3's operator re-attribution is built** ([Amendment 3](adr/0053-peer-claim-and-configuration-recovery.md#amendment-3-2026-09--the-operators-re-attribution-is-a-stated-verb)),
for a replica attributed before the claim key existed by a machine that then
died — it self-heals on one more offer from an updated source, and otherwise
needs the destination's operator. `list_replica_attributions` and
`reattribute_replica` (contract 1.31), `fallbackplan-agent reattribute`, and
the console's Re-point control share `Agent/ReplicaReattribution`; the ledger
is the runtime's and the listener borrows it, so the re-point is served
without a restart. Refused by name for a replica that carries a claim key,
Owner-only, local callers only. §4, the set's shape in the kit, **will not
be**: there is no kit, and the shape travels in the archive itself
([ADR-0061](adr/0061-adopt-a-destinations-archives.md)), which is how a
claimed replica is adopted back under its original ids.

**A claim is now held** ([Amendment 4](adr/0053-peer-claim-and-configuration-recovery.md#amendment-4-2026-09--a-claim-is-held-until-the-destinations-operator-acknowledges-it)),
which is FR-DR-005 and the one decision of the superseded ADR-0070 this
record's ceremony did not have. A claim that moves an attribution records it
as awaiting acknowledgement and raises a notice before the claimant is told it
succeeded. Until the destination's Owner acknowledges it — contract 1.41's
`acknowledge_replica_claim`, `fallbackplan-agent acknowledge-claim`, or the
console's Acknowledge claim control beside the row and the notice — the
claimant's retention instructions are refused whole and nothing is deleted.
Reading, adopting and pushing are untouched. `Hosts.Tests/ClaimedReplicaRetentionTests`
runs it end to end: a rebuilt machine claims with the verb, adopts and
restores while nobody at the friend's end does anything, and its deletion is
refused until the operator acknowledges. The notice forced a fix on the way:
the listener had been raising notices into a second copy of the ledger beside
the runtime's, invisible to the running service and overwritten by its next
write. It now raises into the runtime's, which fixed the same loss for the
invite-redeemed and peering-ended notices (`Hosts.Tests/UnpairCommandTests`).

An earlier latent trap this record closed still stands:
`Repository.Format/RecoveryKit` decided "is this an installation kit" by
`version >= 2`, so the version number meant both how new a kit is and which of
the two shapes it has. The test is an equality, pinned in
`Repository.Tests/InstallationKitCodecTests`.

**A defect the seventh member found in the sixth.** Adding the claim public key
to `Repository.Crypto/RepositoryWriteCredential` turned up two live faults the
reclaim key had left behind. The credential's two containers —
`Agent/WriteOnlyServiceState`'s stored provisioning and
`Repository.Crypto/WriteOnlyProvisioning`'s sealed envelope — each pinned one
exact total length, so widening the credential had made every bundle an older
build wrote unreadable; an installation would have reported its own credential
as damage, with no way back, because saving deliberately never overwrites. And
`ToBytes` always wrote the current shape with an absent member left zero, so a
round trip — which `RepositoryWriteCredential.Clone` performs on every open — turned
"no reclaim key" into 32 bytes of zeros, which a peer would have recorded
permanently and then demanded signatures under. Both are fixed: the containers
ask the credential how long it is, and a credential writes the shape it holds.

### 0054 — what the scheduled drill does not prove

Built, and deliberately narrower than the operator drill it sits beside. The
scheduled one restores a sampled file from a destination's own replica through
the same guided-restore verbs a person would use — its own store, its own
repository open, a catalogue rebuilt from its own index plane — so it proves
the read path is open for that destination, repeatedly, without anybody
remembering to ask.

It does **not** exercise the standalone recovery tool's dependency closure,
and cannot delete the state directory it is running out of. Both remain
[the committed recovery drill](../eng/recovery-drill.sh)'s, which is not
superseded ([ADR-0054](adr/0054-scheduled-restore-drills.md) §5) and which,
since [ADR-0060](adr/0060-the-passphrase-is-the-recovery-credential.md),
recovers from the destination and the passphrase alone.

Nor, on a write-only set, does it read content: the service holds no content
key, so the scheduled drill proves the road back as far as the sealed content
— the replica opens, its index and catalogue rebuild, every sampled file's
manifest and segment records are found — and records that as a pass with a
stated limit, on the ledger and the status matrix as `drill_limit` (contract
1.27), never as a failure ([Amendment 2](adr/0054-scheduled-restore-drills.md#amendment-2--a-drill-on-a-write-only-set-proves-the-road-as-far-as-the-sealed-content-2026-09)).
Damage before the content plane still fails and still raises the notice.
`Agent/RecoveryDrillJob`, `Hosts.Tests/RecoveryDrillTests`.

A peer destination is drilled only on a cadence its source's operator writes
down for it, never by default, and under a byte cap ([Amendment 3](adr/0054-scheduled-restore-drills.md#amendment-3--a-peer-is-drilled-on-a-stated-cadence-and-under-a-byte-cap-2026-09)):
the read path across the protocol already existed — the drill opens its
source by destination name, which dispatches to the peer over the retrieval
session — and only the scheduler's kind filter kept peers out. The consent
question is answered on the source side, since a cadence is a standing cost
on somebody else's link; a file over the cap is left unsampled and said, not
blamed. `Agent/Scheduler`, `Agent/RecoveryDrillJob`,
`Hosts.Tests/PeerRecoveryDrillTests`.

A fourth answer was added by [Amendment 1](adr/0054-scheduled-restore-drills.md#amendment-1--an-interrupted-drill-is-not-a-failed-drill-2026-09),
and it is silence: a drill interrupted because the service is stopping records
nothing. It was written the other way first, and the cost of that was a false
"a restore drill could not bring back a file" notice every time a drill was in
flight at shutdown — the loudest thing the product says, about the most
ordinary thing it does. `Agent/RecoveryDrillJob` now translates a cancelled
command answer back into the cancellation it was, `Agent/AgentPass` waits for
the drill phase before tearing the runtime down, and `Agent/JobScheduler`
refuses work once it has stopped rather than posting to disposed semaphores.

[Amendment 4](adr/0054-scheduled-restore-drills.md#amendment-4--a-drill-that-did-not-complete-says-so-2026-09)
narrowed that silence to the case it was written for. The drill had kept it
for any cancellation or disposed object, and a fault inside a running
service ends a drill the same way: the SQLite pool race could dispose a
connection under a running drill, and it is the likeliest reading of one in
CI that left no trace. Now a drill says nothing
only when the service is stopping or its pass was cancelled. Anything else
is a drill that did not complete: it is recorded as a failure naming what it
was doing, it raises the notice, and the pair is drilled again an hour
later, the wait doubling while it keeps failing and never passing the
interval. `Agent/RecoveryDrillJob`, `Agent/Scheduler`,
`Application/DestinationSyncStore`, `Hosts.Tests/RecoveryDrillTests`.

[Amendment 5](adr/0054-scheduled-restore-drills.md#amendment-5--a-set-with-no-snapshot-is-not-drilled-and-a-failed-drill-is-checked-again-once-its-replica-has-synced-2026-10)
closed the other way a drill spoke falsely. The pass copied any set whose
archive existed, and a first backup creates its archive when it starts, so a
pass during that backup copied an empty archive, called the destination in
sync, and drilled it into a "drill-failed" notice that stood for thirty days.
The pass now carries only a set whose archive's catalogue lists a snapshot,
and the status reads such a set's destinations as awaiting its first backup
([ADR-0050](adr/0050-completed-run-record-and-drill-down.md) Amendment 2).
A failed drill no longer waits out its interval regardless: once a sync has
succeeded since it with no damage standing there, it is checked again on
the same back-off, so a notice raised in error, or about a fault since put
right, clears within the hour. `Agent/ServiceRuntime`, `Agent/Scheduler`,
`Agent/RecoveryDrillJob`, `Application/DestinationSyncStore`,
`Hosts.Tests/RecoveryDrillTests`.

[Amendment 6](adr/0054-scheduled-restore-drills.md#amendment-6--a-person-can-drill-now-2026-10)
lets a person drill a pair now: `run_drill` (contract 1.58), a button on
each destination row in the console, and a `drill` verb on both command
lines. It is the schedule's drill, recorded and announced as one, so a
passing drill clears the notice a failed one raised without waiting for the
schedule; the back-offs stay for the installation nobody is watching. A
peer with no stated cadence may be drilled by asking, and a pair with
nothing there to restore is said and not drilled. Every drill now goes
through one gate per pair, so an ask while the pair is being drilled joins
that drill and is told its answer, and a person's drill runs on the
service's lifetime, so giving up the wait does not cut it short.
`Agent/DrillFlights`, `Agent/RecoveryDrillJob`,
`Agent/ServiceCommandHandler`, `Hosts.Tests/DrillNowTests`.

### 0055 — what the split defends, and what it does not

Built on both planes. A tombstone signs under a reclaim key on its own
derivation domain; a write-only service's write credential is deliberately not
given that domain, so a service that can publish for ever cannot author a
deletion. Such a set still collects, under a grant sealed to the service and
zeroed with the run — a service compromised between runs holds nothing that
deletes. On the wire, the repository's reclaim **public** key is published on
the `ReplicationOffer` and recorded beside the attribution (recorded once,
never replaceable by a later offer), and each `RetentionOffer` page carries a
signature the destination checks against it.

Three limits, stated because the alternative is a reader inferring more:

- **The limit ADR-0055 §3 stated — an ordinary format-1 service gains
  nothing, because it holds the master key — has closed** with format 1
  ([ADR-0014 Amendment 1](adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)): no service derives the reclaim key now, and the split defends
  every repository. The destination retention floor stays the safeguard that
  holds against a compromised *grant*.
- **A page signature does not bind the session.** Forgery and editing are
  closed; replay of a page captured inside an authenticated session is not, and
  [06 §4.1](../specifications/peer-protocol/06-retention.md#41-retentionoffer)
  says so rather than leaving it to be assumed.
- ~~**A destination keeps no signed record of what it deleted**, only the count
  it acknowledges.~~ Closed by [ADR-0063](adr/0063-deletion-receipts.md): the
  destination answers every instruction with a deletion receipt under its own
  device key, and both parties file it beside the tombstone's repository-plane
  record.

Every client that applies retention derives the grants where the passphrase
was typed, one per set ([Amendment 3](adr/0055-reclaim-authority.md#amendment-3-2026-10--a-grant-per-set-from-every-client-proved-against-the-archives-key)):
the console's Apply dialog through its own endpoint (`Web/ConsoleRestoreGate`),
the CLI's `retention --apply --passphrase-env <VAR>` against a running service
(`Cli/OperationGateway`), and the agent's own verb (`Agent/AgentHost`). Each
derives under the facts published for each set, once per distinct salt, proves
the derivation against the sealing key published beside them, and sends only
sealed envelopes. A passphrase that opens no set is refused by the client, and
without one `--apply` is refused naming what it needs; a dry run needs nothing.
The service proves every grant against the reclaim public key its archive's
credential carries before any set runs, so a wrong grant is refused even on an
archive with no tombstone yet, and a set the grants leave out is reported as
not applied (`Hosts.Tests/WriteOnlySetTests`, `Hosts.Tests/ClientModeTests`,
`Web.Tests/RetentionApplyCeremonyTests`). Until this, neither the console nor
the CLI sent a grant, so applying was refused from both.

The same run is where a write-only set's **peers** converge ([Amendment 2](adr/0055-reclaim-authority.md#amendment-2-2026-09--a-write-only-sets-peers-converge-under-the-grant)): the scheduled sync holds no authority to delete, so it pushes whole copies and raises a notice naming the grant, and the granted retention run pushes and instructs each peer under rules with pages signed by the grant — `Agent/FanOut` (`ConvergePeersAsync`), inside the run, before the grant is zeroed. Found when the peer retention fixtures moved onto a set-up installation: the scheduled sync had been sending the instruction unsigned, and the spoke refused it whole on every pass.

Two compatibility rules carry the migration, and both are the load-bearing
part rather than politeness. A repository written before the decision has
tombstones signed under the publication key and keeps verifying them that way;
the descriptor decides, so the choice cannot be downgraded per object. And a
spoke whose attribution carries no reclaim key cannot manufacture a verdict
from an absence, so it proceeds as it always did — which is why the
requirement is a negotiated feature rather than an assumption.

### 0056 — what a skip claims, and what checks it

Built. A pass over a pair the last one left level costs the publication
sequence read that establishes it: no listing of either side, where before it
listed the whole source namespace once per dependency phase and the whole
destination once more. A pass with work lists each phase under that phase's own
prefix, and releases the phase's key set when the phase ends.

A skip is a claim about a destination made without looking at it, so what
bounds it matters more than what it saves:

- **It expires.** Only a pass that read both inventories through stamps
  `last_reconciled_at`, and the stamp is good for a day. A destination that
  loses an object can therefore be wrong for up to that long, where every pass
  used to find it.
- **It is not the only thing watching.** Verification reads bytes at the
  destination on its own cadence and the drill restores a file from it. A pair
  that fails either stops claiming to be level, and a pair that is not level is
  never skipped — which is what actually caught the deleted blob in the test
  written to prove the expiry.
- **It cannot hide a migration.** A direct-ship set still carrying the staging
  archive it migrated from reads through on every pass, because its runs speak
  only for the objects they shipped.

Two inputs move without anything being published, and both are fingerprinted
rather than assumed: a retention window expiring with the clock, and a spare
released when the sibling it was held for catches up.

The reconciliation age is recorded and is **not** on the status contract, so no
surface yet says when a destination was last read through. That is an additive
contract change nobody has made.

### 0057 — what resuming trusts

Built. A transfer cut inside an object now costs its tail rather than the whole
object, which on a domestic uplink is the difference between a large blob
eventually arriving and never arriving at all.

The trust question is the only interesting one, and the answer is that the
destination's claim binds nothing. It declares what it part holds and a digest
of exactly those bytes; the source hashes its own prefix of the same object and
compares; a mismatch sends the object whole. The destination cannot perform
that check itself — it holds no repository keys, and a store key is a keyed
rendering of an identifier rather than of the bytes — so the side that has the
object is the side that decides. A peer therefore cannot talk a source into
skipping bytes it has not proved it holds, and every disagreement lands on the
behaviour that existed before.

Three limits worth stating:

- **It is the peer wire only.** A local-path destination re-copies from local
  disk, where a restart costs seconds. Peers are served for staging sets
  today; a direct-ship set's peer destination is still refused until the peer
  write adapter lands ([ADR-0046](adr/0046-direct-to-destination-publication.md)).
- **Staged bytes are charged to the peer's quota**, so a peer near its ceiling
  can be refused for an object it would previously have been admitted for.
  Uncounted bytes would be a ceiling that does not hold.
- **A resumed commit is assembled from two sessions' bytes.** The prefix is
  checked by digest and the tail by the session, so no part is unchecked — but
  the object is no longer the product of one uninterrupted read.

## By phase

| Phase | State |
|-------|-------|
| [0 — Archive engine](roadmap.md#phase-0--archive-engine-vertical-slice) | Complete; every exit criterion traced to a named test — the compaction criterion discharged by the compactor itself since [0067](#0067--the-rewrite-that-holds-no-key), rather than by its preconditions alone |
| [1 — Snapshot and local repository](roadmap.md#phase-1--snapshot-and-local-repository-mvp) | Complete, both pushes |
| [2 — Peer-to-peer and the service boundary](roadmap.md#phase-2--peer-to-peer-backup-and-the-service-boundary) | Complete except deferred-not-planned items (LAN discovery, relay, bandwidth schedules, multi-instance console, Q18/Q19): service boundary on both bindings, peer protocol over a real socket, replication with recovery drill, roles/termination/quotas/retention via the hub-and-spoke arc, and destination verification (spec 04) with `verified` earned from read-back and the four-value failure domains (FR-SNP-007). The web UI, deferred at the phase close, has since landed as the local web console ([ADR-0036](adr/0036-local-web-console.md)) |
| [Hub-and-spoke arc](roadmap.md#the-hub-and-spoke-arc--multi-destination-backup-sets-built) | Built ([ADR-0034](adr/0034-hub-and-spoke-destinations.md)): configuration schema v2, per-set staging archives, local-path and peer fan-out, the status matrix, termination notices, quota enforcement, retention against staging, local-path and peer destinations, the staging trim, and the `sync`/`retention` operator verbs — see [0034](#0034--the-hub-fans-out-ages-and-trims). For a `direct_ship` set the staging half of this arc is replaced by the direct-to-destination row below |
| Direct-to-destination arc | Partly built ([ADR-0046](adr/0046-direct-to-destination-publication.md), [ADR-0047](adr/0047-backup-pool-and-priorities.md)): the ship sink and metadata store, destination-backed restore/verify/retention reads, migration and staging retirement, the pool with priorities and true suspend/resume, and the kill sweep — the trimming drill run, the flag on the contract and console, and new local-path sets born direct-ship — the peer write adapter landed as [ADR-0058](adr/0058-peer-write-adapter.md), and a rebuilt machine adopts a destination's archives back under their original ids ([ADR-0061](adr/0061-adopt-a-destinations-archives.md)), and a whole state directory rolled back is witnessed, protected and healed from the destination ([ADR-0062](adr/0062-the-destination-is-the-rollback-witness.md)); see [0046](#0046--the-set-that-never-stages) |
| 3 — Cloud object stores | Not started; reframed as destination kinds behind the arc's fan-out |
| 4 — Retention, GC, compaction | Retention pulled forward into the hub-and-spoke arc; compaction and healing remain here — see [0025](#0025--decrypt-and-reseal-superseded-for-format-3) |
| 5 — Legacy archive import | Not started, gated on legal review |

---

## What keeps this true

[`eng/check-adr-status.py`](../eng/check-adr-status.py) refuses a build where an ADR is missing from the table above, where a row names an ADR that does not exist, where a state is not one of the four in the legend, or — the one that matters — **where a cited project, directory or type is not on disk.** It is the same discipline `eng/check-requirements.py` applies to the traceability matrix, adopted for the same reason: a status page nobody verifies becomes a status page nobody can trust, and the failure is invisible until someone acts on it.


### 0058 — what the adapter does not carry

Built. A direct-ship set ships to a paired peer over one replication session
held open for the run, which discharges [ADR-0046](adr/0046-direct-to-destination-publication.md)'s
last stated tail. A set whose backups live at a friend's house and nowhere
else no longer has to keep a staging copy of them on the machine they exist to
survive.

Three things it deliberately does not do, so nobody reads the row as more than
it is.

**It does not challenge.** A challenge is answered by the peer and judged
against bytes this side reads for itself, and a set shipping only to a peer
has none — the sink can offer the metadata plane it keeps locally and no
content at all. Sampling that population would prove nine small objects and
stamp the pair verified, which is the emptiness the verification-independence
work already found once. So the pass challenges nothing and **reads the
replica back** instead (§8 as amended): a sample of the blobs the spoke
declared, opened over the retrieval session, each proved by a record's AEAD
tag where the service can open one and by the **digest tier** where it cannot
— a write-only set's sealed data plane, hashed whole against the digest the
writer signed into the index. The ledger says which tier proved what
(contract 1.32). The read-back rotates through the peer's declared inventory
on the same cursor the local path walks, under a peer byte budget of its own.
A digest challenge on the wire, in which the peer hashes its own copy, was
considered and refused as a self-report the source cannot verify — the
answer is one the peer could have cached at receipt — so the bytes crossing
the wire stay the proof. The commitment that makes a cheaper challenge sound
was then built as [ADR-0065](#0065--one-leaf-instead-of-the-blob): at format 3
the index carries a Merkle root, the peer is asked for one **leaf** and its
authentication path, and the leaf's bytes — which a cached path cannot
supply — are what the source checks. A format-2 blob at a peer is still read
back whole.

**It does not read a peer outside a run.** A peer shipment is a live session,
not a directory, so `ReadOrder` still resolves local paths only. A catch-up
copy that would source bytes from a peer therefore has nothing to read; for a
mixed set the local sibling answers, and for a peer-only set there is nothing
to catch up to. Restores go the restore-source path, which is where a peer's
replica has always been read.

**It does not become the default for a peer-only set.** The boundary stops
refusing such a set for having a peer where a local path was demanded, and the
default stays staging, because for a set with one peer destination the staging
archive buys a capture that does not wait on the link, a resumable transfer,
and the second copy the paragraph above is about. Direct-ship is now a choice
that set can make, with three stated costs.

**It does not resume.** The ship session withholds `partial-object-resume`
([ADR-0057](adr/0057-resumable-object-transfer.md)) on purpose: a run holds no
object it could resume, because a run cut mid-blob seals a differently
identified blob the next time. Offering it would park prefixes against the
peer's quota for a week awaiting a second half that never comes. The fan-out's
own push still resumes, because it reads from a store that keeps its objects.

### 0059 — the hole under the hole

Built, and it closed two things rather than one.

The expected half was replay: a signed retention page covered its own bytes,
which say who authorised an instruction and never when, so a page recorded from
one session verified in the next. It now covers the session identifier as well,
and that identifier cost nothing to obtain — every session already built the
material to authenticate with and threw it away.

The unexpected half was found writing the test for the first. The requirement
to sign was gated on a **negotiated feature**, and negotiation is an
intersection of what two sides offer, and a listener cannot require a feature.
So the party the check defends against decided whether it applied: a source
that omitted `signed-retention` from its hello had an unsigned, freshly
composed drop-list obeyed. That is forgery rather than replay, needing no
captured page and no reclaim key — only the device key a compromised
write-only service holds.

The fix is the durable fact the spoke wrote down for itself at first
attribution. If it holds a reclaim public key for a repository, it requires a
signature, whatever the hello said. [ADR-0055](adr/0055-reclaim-authority.md)
§4 had already made this argument on the repository plane and had not carried
it to the wire; [02 §6](../specifications/peer-protocol/02-session.md#6-feature-negotiation)
now states it as a rule of the protocol, because it is not about retention.

**What this costs.** An older commander signs the unbound encoding and a
current spoke will not accept it — accepting both encodings is accepting the
replayable one — so its retention is refused by name until it is upgraded. That
is a deletion not made, never a backup not taken, and it is the right way round
for a backup product.

**What was still not met, and now is.** `FR-GC-008` also promises signed audit
records, and this note used to say a destination kept no signed record of what
it deleted — that a receipt would be a different artefact under the
destination's own device key, with its own lifetime and reader. It is, and it
exists ([ADR-0063](adr/0063-deletion-receipts.md)): filed under
`<state>/receipts/deletions` at both ends, read by `receipts`.

### 0045 — the product can say who is acting

Built. The console's authority used to be a bearer token minted per run, so everyone holding the URL was indistinguishably "the operator" — no action attributable, no single person revocable — and the local socket authenticates a *uid*, which is a fact about a process rather than about a person. Contract **1.16** adds login, resume_session, logout and the four account verbs behind a decorator built per accepted connection.

The ADR settles what the build has to honour: person-identity sits **inside** an already-authenticated channel, so [ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md) §5's "no password, no token file, no port" is amended rather than reversed — no new listener, no credential needed to reach the socket, and a session that lives only in memory precisely because a token file is the failure mode §5 named. `PasswordHash` goes in `Repository.Crypto`, where Argon2id already lives and is cross-verified each CI run, rather than widening the two-assembly third-party-cryptography allowlist. Repeated failures throttle and never lock, because for a backup product being denied your own backups by someone else's deliberate failures is the worse outcome.

Three things the build corrected in the decision, each recorded as an ADR amendment. A session cannot ride a connection — the web console opens a fresh one per relayed request — so it is minted once and *presented* by a connection through `resume_session`. Enforcement engages only once an installation has accounts, because refusing everything without a session bricks an existing installation on upgrade and leaves no door for the first account; `describe_service` reports `users_required` instead. And the browser holds a viewer's session rather than the console process, or one console would make every action attributable to whoever signed in first.

The key-material canary caught six new contract members and was right to; they are carved out by exact name in the shape ADR-0042 established, with a second test that each carved-out name still exists and is still a string.

### 0060 — the passphrase is the recovery credential

Built, as the last slice of the format-1 withdrawal, and the register's
shortest way to say what changed is what a person must now keep: **the
passphrase, and knowing where the backups are.** Nothing else.

The recovery tool opens an archive from the passphrase and the archive's own
descriptor — `Recovery/RecoverySession` through
`Repository.Crypto/WriteOnlyDerivation`'s `TryDeriveVerified`, the one
derive-and-compare gate the engine, the console and the tool now share, placed
in Crypto because the tool deliberately links no engine. Its usage is
`open | snapshots | restore --repo --passphrase-env`; `--kit` is refused by
name. Event 3100 records the descriptor read.

Everything the kit touched is gone rather than dormant: the kit format, codec
and text form in `Repository.Format`, the factory, the conformance vectors and
fuzz seeds, `specifications/recovery-kit`, the CLI's `key-export`, the setup
verb's `--kit-output`, the console's kit step, rebuild endpoint and
Maintenance card, `confirm_recovery_kit`, `kit_status`, `kit_confirmed_at`,
the `kit_required` state, `recovery-kit.confirmed` and
`installation-public.json`. Contract 1.29 admits the removals under the
pre-release rule.

`eng/recovery-drill.sh` is rewritten to the sequence the record describes —
setup, two direct-ship sets, the machine destroyed, each archive opened,
enumerated and restored byte-identically from the destination and the
passphrase, a wrong passphrase and a foreign passphrase refused, a `--kit`
refused by name — and is green on the Release binaries.
`Hosts.Tests/RecoveryHostTests` and `Repository.Tests/PassphraseDrillTests`
are the in-process halves; `Hosts.Tests/PeerClaimTests` is the peer half,
through [ADR-0053 Amendment 2](adr/0053-peer-claim-and-configuration-recovery.md).

**The follow-up the record named is built as [ADR-0061](adr/0061-adopt-a-destinations-archives.md):**
a rebuilt machine pointed at an existing destination discovers what it holds
and adopts each archive under its original ids, resuming rather than
re-seeding — see [0061](#0061--the-rebuilt-machine-resumes). What a person
must still know is *where* the backups are: a forgotten destination is a
passphrase that opens nothing they can find.

### 0061 — the rebuilt machine resumes

Built, over six commits, and the drill is the shortest way to say what it
does: `eng/recovery-drill.sh` step 8 destroys the machine, sets it up again
under the same passphrase and a new salt, points it at the vault with no sets
declared, and the service discovers both archives by descriptor, adopts each
under its original ids from the shape the archive records, and backs both
sets up again — fourteen kilobytes into the same two archives, not the
history.

The archive carries the set's shape since this record: policy-manifest keys
10 `roots`, 11 `set_name` and 12 `schedule`
(`Repository.Format/Manifests/PolicyManifest`), optional, with the nine-key
form byte-identical for every archive written before. Adoption
(`Agent/ServiceCommandHandler.Adoption.cs`) proves the envelope's derivation
against the descriptor before it writes anything, rebuilds the catalogue at
the runtime's real path over the replica, copies the metadata, resumes the
archive's writer identity when nothing here has published
(`Application/LocalState`), stores the credential, appends the set as
direct-ship and seeds the ledger at the replica's own head. The console
derives in its own process against the discovered row
(`Web/ConsoleRestoreGate`); the CLI's `discover` and `adopt` speak to the
service, and its routed restore derives the grant per set
(`Cli/OperationGateway`), because an adopted set keeps the salt its archive
was born under. At a peer the owner inventory is the enumerator and the
retrieval session the store; nothing below the resolve step changed for it.

Not recorded, on purpose: priority, destinations, and a destination's
retention override (FR-DEST-006); the set's own retention is, since
Amendment 1 (FR-DR-006). Not guessed at: a recorded root missing on this
machine is flagged and left for the person to re-point or restore into.

**Amendment 2: shown, then confirmed (FR-DR-009).**
- `preview_adoption` runs adoption's steps up to the key proof, reads the
  recorded shape through a catalogue in a scratch directory, and writes
  nothing to the state directory.
- It answers with each root's recorded path, flagged where it does not
  resolve here, the retention the set would delete by, and what adoption
  would say about this installation.
- It also answers a confirmation: a digest of what it showed and of the
  archive's newest snapshot.
- `adopt_archive` refuses without one (contract 1.42, deliberately not
  additive), and refuses one the archive has since moved on from. A root
  re-pointed at confirmation is the path captured from.
- The CLI's `adopt` previews, and adopts only with `--confirm`.
- The console's one secret-bearing endpoint answers in two phases
  (`Web/WebConsoleHost`).
- The drill runs the preview before it adopts.


### 0062 — the destination is the witness

Built over four commits. The row this closes had been Unproved since the
proof-obligation table was written: for a direct-ship set the catalogue, the
sequence file, the sync ledger and the metadata store all live in the state
directory, so a rollback of the whole directory rolled the witness slice 3.2
built back with it. The destination did not roll back, and its journal keys
carry this writer's sequence in the clear.

The allocator is the detector (`Agent/FanOut`): once the replica store
exists, the pass reads the destination's journal head for this writer
(`Repository.Index/ObservedHead`'s `JournalHeadAsync` — one listing, no
reads, no key) and offers it to the writer sequence, which only ever rises
and answers `Adopted` only for a number the writer has not yet allocated.
Per writer, so a second device's progress never reads as this one's
rollback; before the reconciliation gate, whose ledger rolled back too. A
detecting pass computes no keep-set — so nothing is converged or spared —
reads through, and raises `destination-ahead:<set>:<destination>`, never
auto-resolved.

A direct-ship set is then healed in place (`Agent/ServiceRuntime`'s
`HealFromDestinationAsync`): the destination's metadata copied back with the
if-absent copy adoption uses, the catalogue rebuilt over the live handle
(`Agent/CatalogueRebuild`, whose every write is an upsert), and the writer
moved past what the healed archive attests. The trigger is the metadata
plane — the destination's journal head above the local store's — so a heal
that failed is retried on every pass, which a trigger keyed on the
already-moved sequence never would be. A staging set was protected and told,
not healed, until [Amendment 2](adr/0062-the-destination-is-the-rollback-witness.md#amendment-2--a-staging-set-is-healed-too-bounded-by-the-history-it-lacks-2026-09):
what it lacks is content, and the next converging pass would have trimmed
the destination to a keep-set the rolled-back archive could see. It is now
healed too — `ServiceRuntime.CopyBackAsync` copies the destination's newer
history in publication order, blobs before the manifests that reference
them, every put if-absent, and the staging arm admits exactly the data
blobs the closure of the snapshots the archive lacks lives in (walked at
the destination with the same `StagingMark` convergence uses) plus every
metadata blob, so what staging retirement shed never comes back. A
destination snapshot that will not decode fails the heal rather than
narrowing it, which keeps the pass from converging. Over a peer the same
copy rides the retrieval session, and `Agent/PeerRetrievalObjectStore` now
serves any read one chunk at a time, which restore inherits.
`Hosts.Tests/DirectoryRollbackTests` holds the heal, the bound and the
ordering; `Hosts.Tests/PeerRollbackTests` the same over the wire with a
blob longer than one chunk, byte for byte.

`Hosts.Tests/DirectoryRollbackTests` is the drill, with the state directory
copied aside between two backups and put back; one fixture fact is worth
keeping — a backup run stamps the ledger with its scheduled time, so a sync
stamped with the real clock records an earlier success and the sink refuses
the destination as one that missed a run.

A peer is witnessed too, and more cheaply than the record expected
([Amendment 1](adr/0062-the-destination-is-the-rollback-witness.md#amendment-1--the-peer-is-a-witness-too-from-the-inventory-it-already-declares-2026-09)):
every push already reads the peer's complete inventory, journal keys
included, so the head is a fold over keys in hand. What mattered was the
order inside a push — the retention instruction is decided before the
inventory arrives — so `Agent/ReplicationInitiator` takes a hook invoked
between the inventory and the first filtered object, and a pass that adopts
the head withholds convergence for the session on the scheduled sync and on
the granted collection run alike. The harm on this path is a shared blob
dropped by a granted run under a rolled-back keep-set on a mixed set, and a
journal that silently diverges; `Hosts.Tests/PeerRollbackTests` holds both.
The heal dials the retrieval session and hands `HealFromDestinationAsync` a
`PeerRetrievalObjectStore`; a dial failure is a failed pass, retried, never a
finding.

### 0063 — the peer plane's audit record

Built over four commits, one seam at a time. The statement first
(`Protocol/DeletionReceipt`): a fixed encoding under its own label, parsed
as its exact inverse so that trailing bytes are refused, capped at 4096
listed keys and 4096 page digests with the count and the digests standing
for the rest; two additive keys on the `RetentionAck` (present together or
not at all; no feature, because an absence can only mean less); and a
store (`Protocol/DeletionReceiptStore`) in the protocol library, since the
CLI must reach it and does not reference the agent, whose every read
re-checks the signature so that a file edited after filing reads as
unverified rather than as something the peer attested.

Then the destination (`Agent/ReplicationResponder`): it hashes each page's
signed bytes as it validates them, deletes as before, keeps the keys it
actually removed and counts the ones it never held, signs under the device
key the listener already holds, files first and acks second. The session
it names is the real identifier whatever the pages' signatures were bound
to. A copy it cannot write is a warning (event 3725), never a refusal — the
deletion has happened, and the commander's copy is the commander's.

Then the commander (`Agent/ReplicationInitiator`, `Agent/FanOut`): the
push digests each page as it goes out, and `VerifyReceipt` holds the
answer against the pinned identity, the session, the repository, its own
key, the digests in order, the acknowledged count and the drop list — in
that order, stopping at the first failure and naming it. A verified receipt
is filed with the set and destination names; a rejected one raises
`deletion-receipt-invalid:<set>:<destination>`, never auto-resolved, and is
logged (3726); a verified one that could not be written is logged (3727)
and said in the report. The granted run's line per peer now reads
"converged under the grant: N object(s) deleted, receipt <file>", or says
that nothing was to delete, that the receipt was rejected and why, or that
the peer predates receipts.

Then the reader: `receipts` on both hosts, file-direct, rendered by one
routine (`Protocol/DeletionReceiptReport`) so they print the same thing from
the same bytes, and refusing a mistyped state directory by path rather than
answering "no deletion receipts" — the one answer the verb must never give
by accident.

The commander's checks are held at their pure seam
(`Hosts.Tests/DeletionReceiptVerificationTests`) rather than over a live
listener, because a dishonest destination cannot be built from this
repository's own responder without teaching it to lie; the honest path is
held end to end by `Hosts.Tests/PeerRetentionReplayTests` off a real
listener and by `Retention.Tests/PeerRetentionTests` off a granted run.
One test found the row's v1 exception stale rather than unproved: format 1
being withdrawn, no service derives the reclaim key, so proof row 69 goes
to Proved with nothing left in it that is "not true".

Since the 2026-09 amendment the pile has an end. One receipt is filed per
instruction at both ends and nothing removed one, so a pair exchanging on a
schedule filed for ever. The rule is three numbers and not one — the newest 8
whatever their age, at most 4096 per repository, 365 days between the two —
because a count alone discards a year of history from a busy pair and an age
alone leaves a quiet one with nothing recent, which is when its last receipts
matter most. The sweep reads file names only, so a pile that has gone
unreadable is still bounded and a copied state directory still ages by the
issue time the names carry rather than by stamps the copy rewrote; it runs at
filing, which keeps a live pair bounded between restarts, and at service start,
which is what reaches a pair that has stopped filing
(`Protocol/ReceiptRetentionPolicy`, `Protocol/PeerReceiptFiles`,
`Agent/ServiceRuntime`; `Protocol.Tests/ReceiptSweepTests`). The reader is
bounded by the same names, which turned out to be a property rather than an
economy: a receipt tampered with cannot drop out of a bounded window by
becoming unreadable, as it could when the newest rows were taken after reading
(`Protocol.Tests/DeletionReceiptStoreTests`).

### 0064 — the peer counted on its own word

The deletion receipt's three choices carried to the push, and one
question the deletion receipt never had: what the ledger may do with a
peer's signed count. Built over four commits. The statement
(`Protocol/ReplicationReceipt`): the session, the repository, the
commander, what the push created (the count, and up to 4096 keys in
commit order) and what the replica holds afterwards (objects and bytes),
under its own label, parsed as its exact inverse; two additive keys on
the `ReplicationAck`; and the filing both receipt stores share
(`Protocol/PeerReceiptFiles`), each envelope naming its kind so a file
of one kind found among the other is reported rather than misread, and a
kind-less envelope from before this record still reading as a deletion.

Then the destination (`Agent/ReplicationResponder`): the inventory walk
it already makes counts objects and bytes as it lists, the receive loop
keeps the keys it commits, and "held after" is the walk plus this
session's creates with no third pass over the replica. A receipt is
issued on every push, an empty one included; a copy this side cannot
keep composes with the deletion receipt's problem on the outcome and is
a warning (3725), never a refusal.

Then the commander (`Agent/ReplicationInitiator`, `Agent/FanOut`): the
push loop keeps the keys it sent and sums what the peer is owed, and
`VerifyReplicationReceipt` holds the answer — before the exchange goes on
to anything else, because most pushes end there — against the pinned
identity, the session, the repository, its own key, the acknowledged
count, the sent keys, and a held figure no smaller than declared plus
committed. A verified receipt is filed with the set and destination
names and the ledger's completeness figures are written for the peer —
held equals owed — which is the **first time they are written for a peer
at all**; the record calls the figure attested and never possession. A
rejected receipt raises `replication-receipt-invalid:<set>:<destination>`
(3778), never auto-resolved, and counts nothing; a peer that sends none
stays uncounted, which `Hosts.Tests/PeerReplicationTests` pins with a
destination served as every build before receipts served it — the real
accept, session and responder, handed no issuer.

Then the readers: `receipts --kind` on both hosts over one report;
`list_receipts` at contract 1.33 (`Agent/ServiceCommandHandler.Receipts.cs`)
answering facts and the service's own verdict on each signature, no path
and no signed or key bytes crossing, to any signed-in role and any caller
scope; and the console's Receipts card on the Maintenance view, fetched
on entering the view and never by the pollers, three states rendered
distinctly and never derived from the absence of a problem string.

Done since the 2026-09 amendment, and left undone by the four commits above:
a direct-ship set shipping to a peer through the write adapter
([ADR-0058](adr/0058-peer-write-adapter.md)) used to receive the receipt with
the run's acknowledgement and read only its count, leaving the pair uncounted
until a sync pass over the same peer fell due. The run now verifies through
`Agent/ReplicationInitiator`'s own seam, files its copy and counts the pair
(`Agent/PeerShipStore`, `Agent/DestinationShipSink`;
`Hosts.Tests/DirectShipPeerTests`) — which matters for when the statement is
made rather than for tidiness, a source being unable to list a peer's replica
cheaply enough to learn the same thing later. A replication receipt is kept
under the same shape of rule as a deletion receipt and for less time — 8 /
1024 / 90 days — because one is issued on every push, including one that
committed nothing, and each is superseded by the next.

### 0044 — the ceremony that two requirements have been waiting for

Built. The passphrase half: a service with no passphrase says so on
`describe_service`, and the first client to connect walks the operator through
choosing one. What made a passphrase-only ceremony possible is that the
passphrase was never per-set — `WriteOnlyDerivation` takes no repository
identifier, so one `(passphrase, salt, params)` triple stamps every archive an
installation will ever create. Setup provisions the installation; each set's
archive is created from that credential on its first backup.

**FR-SNP-007** lands on `validate_set_draft`, which the console already calls
live while editing, so a set whose every destination sits inside its source's
failure domain is warned at the moment it is chosen. It warns on every edit
rather than only at first run, since the belief the requirement guards against
can form at any point.

**The ceremony ends at the passphrase and the first account.** For a year it
ended at a saved recovery kit: a `kit_required` state, a `confirm_recovery_kit`
verb recording the kit's checksum, a kit status on every describe, a console
step handing the kit over in two forms, a rebuild endpoint for a ceremony
closed before saving, and a public-parameters file so the rebuild could
happen before the first backup. All of it went with the kit
([ADR-0060](adr/0060-the-passphrase-is-the-recovery-credential.md), contract
1.29): the passphrase is the whole recovery credential, so there is nothing
to save and nothing for the ceremony to wait for. `setup_state` is
`setup_required` or `ready`, the wizard is three steps, and `--kit-output` is
refused by name. FR-KIT-004 and FR-KIT-005, which the kit step existed to
meet, are deleted rather than left unmet.

Contract 1.13 carries `provision_installation`, the setup state and device
identity on `describe_service`, and the draft's roots and destinations.
`CallerScope` is new and is the fact the code was missing — one handler
served both listeners and `RemoteBindingState` said only whether the remote
binding was on. Q14 is answered: a floor plus a modest estimate — twelve at
decision, sixteen with composition rules since ADR-0044's second amendment —
enforced where a passphrase is chosen and never in `Passphrase.Create`, which
is on the restore path.

Proven by drill rather than by test alone: setup runs, two sets back up, the
**entire state directory is deleted**, and the recovery tool opens each
archive from the passphrase alone — restoring byte-identical files from two
archives the passphrase was never told about.

### 0035 — a destination has to earn being relied on

The whole arc is built. Amendment 1 closed three gaps in what was, and Amendment 2 built the one piece it had named as missing, for a peer on a stated cadence. Before it, the sixteen-range challenge was very nearly the hub's only assurance about a destination, and everything else it knew it learned by trying to use the thing.

**Admission** (`Application/DestinationConfiguration.cs`, `Agent/DestinationProbe.cs`, `Agent/PeerAddress.cs`): a declared address is checked for the defects findable without touching the world — a relative local path, a fingerprint no encoder could produce, an endpoint that is not host:port — and each is reported on the destination's status row. It is emphatically not a validation rule: the load path re-reads and re-validates `config.json` on every property access, several times per scheduler pass, so a throw there would stop every set backing up over one typo. `verify-destination --probe` then answers the question no depth of byte-reading can answer before the first sync: a local path must exist, be a directory and accept a write; a peer must resolve to a grant and a dialable address and complete the handshake including the verification feature. A probe records its failures and never a success, because reaching a destination is not syncing to it.

**Shortfall** (`Agent/FanOut.cs`, `Agent/ReplicationInitiator.cs`): a destination that has been emptied since its last recorded success is caught by collapse in what it declares already holding — the one signal that does not false-positive on a widened keep-set, a resumed partial sync, or a peer that has just gained the retention feature. For a local path the same question is asked of the replica root, checked before the fan-out creates it. Separately, a destination acknowledging fewer objects than it was sent now refuses the session: an under-count without a refusal means a responder bug or a desynchronised stream, and a soft warning there would be learned and ignored.

**No silent fallback** (`Retention/DestinationConvergence.cs`): the keep filter answers with a reason rather than a bare null, so "no policy", "the spoke lacks the feature" and "the graph would not walk" stop being spelled identically. Only the third is a fault; it now raises a self-resolving notice instead of quietly taking a whole copy.

**Confirmation on a schedule** (`Repository/ReplicaSweep.cs`, `Agent/ReplicaSweepJob.cs`, `Agent/Scheduler.cs`): a third scheduler phase re-reads a local-path replica's stored objects against their seals, bounded per segment and resuming from a cursor in `destinations.json`, a circuit weekly by default and overridable per destination. It runs on the transfer lane, not the reader lane, because a reader-lane sweep would race a concurrent convergence and manufacture failures. It also compares each swept key's length against the source's, which is not redundancy: the blob reader does not bind the store key to the envelope's blob id, so a valid sealed blob stored under another's key passes the digest check entirely. `verify-destination [--full]` drives the same engine on demand.

**Accumulating coverage** (`Replication/VerificationSampler.cs`): the sync-time challenge rotates from a persisted cursor instead of re-drawing a uniform sample every pass — FR-VER-002's "weighted towards those longest unverified", specified since it was written and until now unimplemented. It sorts its candidates rather than trusting listing order, wraps within the same pass so a completed lap still writes a stamp, and keeps part of a peer's budget unpredictable because a peer answers its own challenge.

**Age and capacity** (`Application/StatusModel.cs`, `Application/DestinationCapacity.cs`, `Protocol/PeerReplicationMessages.cs`): a proof past its bound — seven days local, thirty peer — is named in the warnings without moving `ProtectionState`. A quota-bound peer reports its remaining headroom on the replication inventory frame, and the source warns below a tenth of the loan rather than refusing, because the existing boundary stop already refuses the exact object at the exact moment and keeps the partial progress. A local copy does not start below a 64 MiB free-space floor, recorded `Unavailable` so freeing space heals it unbidden.

**A circuit carried to its end, and damage repaired** ([Amendment 1](adr/0035-destination-fitness.md#amendment-1-2026-09--a-circuit-is-carried-to-its-end-and-damage-is-repaired)). As first built, the scheduler read one segment of sixty-four blobs per interval, so a replica of ten thousand blobs was read once in about three years; a finding asked for the damaged objects to be re-copied, and the sync promised as the repair copied nothing, because the copier counts a present key as held and a local store never overwrites; and that sync then called the pair in sync over the finding. Now an open circuit is due on every pass and the interval rests between circuits, and a segment is bounded in bytes as well — about a minute's worth at a destination's transfer limit. A damaged blob is replaced from a copy proven sound where it sits — its digest intact and its envelope naming the key's blob — drawn from the staging archive, the set's other local paths, or its peers over the retrieval session (`Repository/ReplicaRepair.cs`, `Agent/ReplicaRepairer.cs`), staged and proven before anything at the replica is deleted. With no sound copy the damaged blob stays, the ledger lists it (`damaged_keys`, schema 6), and the pair stays failed through any writer's success until the sync that re-checks it finds the damage gone (`Hosts.Tests/DeepSweepTests`, `Repository.Tests/ReplicaRepairTests`). A blob that will not read is not taken for damage: the segment stops at it, keeping what it read, the stall is counted on the ledger, and the next attempt waits the sync's back-off instead of meeting the blob again on every pass, which the every-pass circuit would otherwise have done for as long as it would not read. Three stalls in a row raise a notice, withdrawn once a segment reads past it.

**A peer swept on a stated cadence** ([Amendment 2](adr/0035-destination-fitness.md#amendment-2-2026-09--a-peer-is-swept-on-a-stated-cadence-and-what-is-found-there-is-held)). A peer's replica is re-read in full over the retrieval session only when its declaration states `deep_verify_interval_days` — the drill's rule, because the link is somebody else's — paced by its transfer limit and bounded to about a minute's worth at it a segment, or 256 MiB without one. What it finds is held against the pair and named with the remedy the peer's owner can carry out: remove the objects, and the next push sends them again, which is what clears them (`Agent/FanOut.cs`). A peer that will not serve the session is told of in a notice and waits its interval, never failed. One that has gone between syncs is recorded unreachable by the segment that meets it, as a sync would record it, so the scheduler does not dial it again until a sync finds it back; a blob it will not give up stalls the circuit as a local one does. `verify-destination` reads any peer on demand, and says what continues a read of a peer that no cadence sweeps (`Hosts.Tests/PeerDeepSweepTests`).

**A circuit reported where the status is** ([Amendment 3](adr/0035-destination-fitness.md#amendment-3-2026-09--a-circuit-is-reported-where-the-status-is)). When a circuit last closed was on the ledger and in `verify-destination`'s line, and nowhere a person looks routinely. Contract 1.46 puts it on each destination's status row, with the circuit under way, a stall, and the cadence, and the CLI and the console say it.

Two distinctions carry the design. No sweep at all — a reserved kind, an undeclared destination, an older service — is not a sweep that has not run, so the first draws nothing and the second says never. And a peer read only on request is not never either, because never would look overdue. The cadence rule is now stated once, for the scheduler that keeps it and the row that reports it.

**What is not built: repairing a peer.** Replacing an object at a peer needs protocol work — the retrieval session reads, a push only creates, and a deletion there needs reclaim authority a write-only service holds only under a grant. The cheaper chunk-challenge circuit the scoping offered is named in Amendment 2 and not built.

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

**Edits keep what no client can see (2026-09, FR-SVC-020).** The destination
upsert rebuilt the declaration from the descriptor and kept only the
verification policy from the stored one. So the two fields later records
added without a wire field were dropped by the first edit through the
service: a peer's drill cadence ([ADR-0054](adr/0054-scheduled-restore-drills.md)
Amendment 3) and a destination's transfer limit
([ADR-0074](adr/0074-background-byte-rate-limits.md)). A console save of a
deep-verify interval was enough to stop a peer's drills or lift its limit.
Both are kept now, and `Hosts.Tests/ConfigurationCommandTests` holds every
field a destination persists to being either carried by the descriptor or
kept by an edit, so the next such field cannot be forgotten the same way.

**The installation's own settings (2026-09, Amendment 1, FR-SVC-021).**
Contract 1.44 lets five settings be changed through the service, which until
now could be set only in the configuration file:
- the background window, the background read limit and the backup pool's
  width, through `get_service_settings` and `update_service_settings`;
- a destination's transfer limit and drill cadence, on its descriptor.

How a request behaves:
- Null keeps a setting, and an empty text or a zero clears it.
- A request applies whole or not at all.
- A refusal quotes the parser's own defect, never the configuration file's
  path.
- The pool's width applies at the next start. Until then `ServiceRuntime`'s
  `BackupPoolWidth` is reported beside the stored value.

The CLI's settings and destination-settings commands, and the console's
Service settings card and destination form, are clients of the two verbs. The
drill cadence gained a load-time refusal of its own; it had been reported
through the deep-verify interval's message.

**A new set in six steps, and placement in the draft (2026-10, Amendment 2,
FR-SVC-022).** The console makes a new set in steps: its name, what it backs up
with its selection filters, and its destinations, each answered before the
next; then its exclusions, retention and other settings, each optional.
Create is offered from the destinations step and sends the one upsert. A step
asks `validate_set_draft` about the draft before the walk goes on, and holds on
a defect. Editing a set keeps the summary. Contract 1.49 lets the draft name
its set, so it is judged for ADR-0051's placement as the save judges it, by the
same code in `Agent/ServiceCommandHandler`. A draft that names no set is not
judged for placement.

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

Amendment 2 (2026-10) made the save act. The owner asked that a saved change
to a set's sources, filters or exclusions start a scan that settles what needs
backing up, so a material edit now queues a backup under the new settings at
once, as a person's request, with the set's fan-out after it. A run already
queued or under way captures the settings it was queued with, so it cannot
stand in: the edit's backup follows it as soon as it settles, and several
edits during one run owe one run after it. The notice now follows the
settings rather than the clock. Each set's settings have a generation that a
material edit moves on (`Agent/SetSettingsGenerations`), and only a run that
captured the current generation resolves the notice. A rescan raises it only
while its generation is current and uncaptured, so a rescan held behind a busy
reader lane cannot raise a notice the backup has already answered. The tests
hold runs at the start of scanning and occupy the reader lane, so each
ordering is one the test arranges rather than one it hopes for. Purging stays
with retention, as the owner chose: deleted and excluded files leave the
backup from the new snapshot on.

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
console process** against the archive's own descriptor
(`Web/ConsoleRestoreGate.cs`, a real Argon2id derivation compared against the
sealing public key), so NFR-SEC-009's wall stands untouched; the console dependency rule gained exactly one named
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
receipt persists to `<state>/receipts/<run>.json` on every run. Proven live: a
Playwright walk of all six steps, wrong-passphrase refusal included, ending on
restored bytes and the receipt path.

The targeted blob load this record added — open the plan's own blobs rather
than every footer in the store — was the right half of the answer and was
reached by one of the three restore paths. It is no longer what a restore
uses at all: [ADR-0068](adr/0068-the-catalogue-directed-restore-read.md) opens
no blob on the happy path, because opening one costs three ranged reads before
a byte of payload. `LoadBlobsAsync(blobStoreKeys, …)` stays, and its callers
are now the two that genuinely want a footer in hand —
`Replication/ReplicaVerifier` and `Agent/CatalogueRebuild`.

The gate this record put in the console process checked the passphrase against
the archive on the console's own disk, so it worked only beside the service,
and anywhere else it offered to continue unchecked; the service itself
answered any signed-in caller. Amendment 1 moves the gate to the service
([ADR-0089](adr/0089-a-backups-file-names-need-the-passphrase.md)). The
wizard's check now reads the facts the service publishes and proves a grant
per set against each set's sealing key, wherever the console runs, and there
is no way past a passphrase it could not check.

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
`init --acknowledge-loss` and derives its direct-mode authority from
`--passphrase-env`; the console runs both ceremonies in its own process. Proven by the end-to-end drills in
`WriteOnlyRepositoryTests` (repository), `WriteOnlySetTests` (service —
including the machine-migration adoption: metadata unreadable on the new
machine until the passphrase re-enters, wrong passphrase refused by
public-key mismatch), `WriteOnlyCommandTests` (CLI),
`WriteOnlyCeremonyTests` (console), the committed
`fixture-repository-v2` read contract, and a live Playwright walk from
the provisioning dialog to byte-identical restored files. Losing the
passphrase loses the backup, acknowledged at setup; there is no
passphrase change (03 §7).

**Since 2026-09 this is the only format.** Format 1 was withdrawn before any
freeze ([ADR-0014 Amendment 1](adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)): the opt-in at creation is gone, `Domain/FormatLimits`
names one format version, `Repository/RepositoryLifecycle` has one create
from a credential, one create from a passphrase, one open with the credential
and one open for reading, and `Repository.Crypto/RepositoryWriteCredential` is
the hierarchy — `KeyHierarchy` and the master-key half of `Repository.Crypto`
are deleted. The service's passphrase mode, its keystore and the
`unlock`/`lock` verbs went with the only archive they could open;
`Repository.Tests/EndToEnd/RepositoryLifecycleTests` holds the refusal of a
format-1 descriptor by name.

A restore routed through a set-up service — the CLI's `restore` in
client mode and over `--connect` — runs the same ceremony the console
does, from the shell: `describe_service` publishes the installation's
public derivation parameters and sealing public key (contract 1.28), the
CLI derives the authority from `--passphrase-env`, proves it against the
published key before sending anything, opens a restore source under the
sealed grant and restores through it (`Cli/OperationGateway`,
`Hosts.Tests/ClientModeTests`, `Hosts.Tests/RemoteConsoleTests`). Without
the passphrase the verb is refused naming the flag rather than restoring
nothing.
### 0070 — the disaster-recovery path is written down and not yet built

Specified only, and deliberately filed as such rather than folded into
0041's "built": the decision, the peer-protocol exchange and the
architecture section exist; no code implements the claim, and no test
exercises it.

The gap it answers was found by a coverage review, and is worth recording
because it read as covered. Two drills come close and neither reaches it.
`Hosts.Tests/PeerRetrievalTests` and `Hosts.Tests/AlternateSiteTests`
destroy the source's **archives** and recover from a peer — but both keep
the state directory, so the device keypair and the pairing survive with
them. 0044's installation-kit drill deletes the **entire state
directory** — but recovers with the standalone tool against an archive
still present on local disk. Total loss is the intersection neither
covers: the state directory *and* the archives gone, the only copy at a
peer, and therefore a device identity the peer has never seen.

Under today's rules that recovery cannot succeed, and every rule
involved is individually correct — attribution is keyed to the lost
fingerprint ([peer-protocol 05
§2](../specifications/peer-protocol/05-quotas.md)), the owner inventory
answers for the dialling identity ([07
§3.5](../specifications/peer-protocol/07-retrieval.md)), and the restore
path matches candidates on a `backup_set_id` the lost configuration
held. `Agent/RetrievalResponder.cs` and
`Application/ReplicaOwnerStore.cs` are where the first two live today.

What remains to build: the claim key derivation, the claim frames and
their negotiated feature, the ledger's token and public-key fields, the
`claim_replicas` verb, and the drill that destroys both stores and
recovers over the wire. The one requirement it still owes — FR-DR-005 —
carries an honest unbuilt marker in the
[traceability matrix](requirements/traceability.md) until then.

**FR-DR-005 is built (2026-09)**, on ADR-0053's ceremony rather than this
record's, which the merge removed: this record's decision 7, reading
unattended and deleting only once the destination's operator acknowledges
the claim, is [ADR-0053 Amendment 4](adr/0053-peer-claim-and-configuration-recovery.md#amendment-4-2026-09--a-claim-is-held-until-the-destinations-operator-acknowledges-it).
See [0053's notes](#0053--the-claim-is-built-the-shape-is-not).

### 0072 — the two things live capture cannot do

Specified only. Written after the adverse-I/O coverage pass put numbers on
live capture's limits, and it has two of them. A file that is written
continuously exhausts the read budget, so the last read is published torn with
a diagnostic and a clean `capture_status`. And on Windows a file held with
`FILE_SHARE_NONE` is not captured at all — the POSIX walk reads a descriptor it
already holds, so a lock on the name cannot reach it, but Windows takes no such
handle and the open simply fails. That is Outlook PSTs and live database files
absent from Windows backups, which is not a degradation but a hole.

A snapshot fixes both, and the obstacle is privilege rather than API. The agent
runs as a named ordinary account because the keystore is scoped to that account
([0033](#0033--the-os-can-own-the-process)); elevating it to reach VSS or
`lvcreate` costs unattended unlock. The decision is a separate privileged
helper that creates and releases a snapshot and knows nothing else — no
repository, no keystore, no key material.

The VSS interop path is deliberately left open and gates the Windows half:
there is no COM anywhere in this solution, every P/Invoke is source-generated,
and the usual managed wrapper is unmaintained on an end-of-life target
framework. Nothing here is testable on this project's CI and the ADR says so
plainly rather than implying otherwise.

### 0071 — recovering the data was only half of it

Specified only, alongside [0070](#0070--the-disaster-recovery-path-is-written-down-and-not-yet-built),
and filed separately because the two are separable: a claim is useful on its
own, for a restore. Neither finishes the disaster alone.

0046 got a rebuilt machine to its data. This one answers what that machine
knows afterwards, which today is nothing: capture rules and root labels are
readable from the replica's own manifests, but the set's name, the **paths**
its labels pointed at, its schedule and its retention policy lived only in
`config.json` and died with the machine. A user looking at restored files has
no way to tell whether anything is protecting them again.

The answer is a **set-configuration object** — type `0x10` at
`/config/<backup-set-id>/…`, [specification 11
§5](adr/0071-recovering-operation-after-total-loss.md)
— written on every publication and on every configuration change, with its
payload **sealed to an asymmetric recipient** so only the passphrase opens it.
That second seal is the load-bearing part: a v2 service is granted the whole
structure plane by design (ADR-0042), so an unsealed record would hand a
compromised write-only hub the user's folder layout, schedule and rules.
`Repository.Crypto/ContentSealing` already provides the envelope, and both
formats already have a recipient key, so no new construction is introduced.

Two findings from writing it are worth keeping, because both were nearly
implemented the other way:

**A configuration change must not publish a snapshot.**
`Retention/RetentionPlanner` buckets snapshots by time and keeps the newest per
bucket, and `SnapshotFact` carries no kind — so a configuration snapshot
published later the same day would be the newest in that day's bucket and would
**expire the day's real backup**. A separate namespace avoids the interaction
rather than teaching the component that decides deletions a new exception.

**The object needs a stated collection root.** Nothing references it, so a
reachability walk alone would collect every one of them and silently disarm
recovery of operation — invisibly, since nothing else reads them while running.
Recorded as [ADR-0009 Amendment
6](adr/0009-garbage-collection-safety.md).

What remains to build: the `fbp/recovery/v1` derivation, the object codec, the
publication and configuration-change write paths, the reconstruction verb, and
the console step that confirms recovered paths rather than capturing from them.
FR-DR-009 carries an honest unbuilt marker in the
[traceability matrix](requirements/traceability.md) until then.

> **2026-09:** FR-DR-009 is built by [ADR-0061 Amendment 2](adr/0061-adopt-a-destinations-archives.md#amendment-2-2026-09--a-recovered-configuration-is-confirmed-before-it-takes-effect),
> not by the reconstruction verb this record specified. The step that
> confirms recovered paths is adoption's own preview. See
> [0061](#0061--the-rebuilt-machine-resumes).

What the checker cannot do is judge whether "built" is generous. That is a reading, and it is repeated whenever a phase closes. It also deliberately does not compare these states against each ADR's `Status:` line: that line records whether a *decision* was accepted, which is a different question from whether the code does it, and collapsing the two would lose both.

---

**See also:** [Abandoned choices](decisions-abandoned.md) — what was considered and rejected, and why · [Traceability](requirements/traceability.md) — requirements to tests · [Roadmap](roadmap.md)

### 0065 — one leaf instead of the blob

A source with no second copy proved a peer held a sealed blob by pulling the
whole blob back and hashing it. The cheaper message —  the peer hashes its own
copy and answers — was refused by
[ADR-0058](#0058--what-the-adapter-does-not-carry) as a self-report, because a
bare digest is an answer the peer may have cached at receipt and kept after
discarding the bytes.

**What is built.** `Repository.Crypto/BlobMerkle` is the commitment: an RFC
6962 tree over one-mebibyte leaves of the flat digest's own preimage, with the
preimage's length hashed into the published root under a prefix of its own.
Since [ADR-0065](adr/0065-merkle-commitment-and-chunk-possession.md) Amendment 1
the tree, the bound root, the path and the verifier are
`Bodu.Security.Cryptography`'s `MerkleTree`, and `BlobMerkle` is the format's
binding of it ([ADR-0019](adr/0019-third-party-dependency-policy.md)
Amendment 4). `BlobMerkleAccumulator` rides the calls that already feed the
blob's digest, streaming each leaf into its hash so the tree costs no second
pass and holds no leaf, and the spool resume hands it over as it hands over the
hash. `Repository.Index/IndexDeltaCodec` carries it as delta key 11,
parallel to `covered_blob_ids` and never alone; `Repository/SnapshotPublication`
publishes it only at repository format 3 or above, because an unknown key in a
delta is refused rather than skipped and a format-2 repository must stay
readable to builds that predate the key. `Repository.Catalogue/Catalogue` keeps
it at schema 7 beside the digest, upserted by its own statement so a later
digest-only delta cannot erase it.

On the wire, `Protocol/PeerRetrievalMessages.cs` types 278–279 under the
`chunk-possession` feature: `Agent/RetrievalResponder` streams its own copy,
hashes every leaf and answers with the challenged leaf's bytes and its path;
`Replication/ReplicaVerifier` checks the bytes against the signed root and
counts the proof as its own tier, ahead of the whole-blob digest tier and
falling through to it. `Application/DestinationSyncStore` schema 4 and contract
1.34 carry the count apart from the digest tier's, because a chunk proof
samples the blob where a digest proof reads all of it.

**The binding is the part worth remembering.** Plain RFC 6962 takes the tree
size from its caller, and a four-leaf tree's first path verifies under a
claimed size of three. At a peer the size comes from the length the destination
declares for its own copy, so without the length in the root a destination
could understate its copy by a leaf and exempt that leaf from ever being drawn.

**What is not.** The service still creates format 2, so no live installation
publishes a root. Since [0066](#0066--two-objects-carry-one-version) that is
every set the product creates, so this tier is reachable by an ordinary
installation rather than only through a test-only seam on
`Agent/ServiceRuntime`; a set still at format 2 publishes no root and is
proved by reading the whole blob back. A local-path destination is not challenged this way
— there is no link to spare — and a chunk proof establishes one leaf, never the
blob.

### 0066 — two objects carry one version

Two slices built format 3 and nothing live wrote it: the only creation surface
was `init --format-version 3`, which the service never calls, so no repository
published a Merkle root and no peer could be challenged for one leaf. The
creation default moved — `Domain/FormatLimits` and
`Agent/ServiceRuntime.ArchiveFormatVersion`, which stopped being a test-only
seam — and the other half of the question is what happens to a repository that
already exists.

**Not a rewritten descriptor, and this was established by reading rather than
argued.** `Agent/DestinationShipSink` seeds a destination with the descriptor
if absent and never again, a peer commits an object it lacks and keeps the one
it has, and `repository-format` may not be named by a retention instruction —
so a rewrite moves the source alone and leaves every copy claiming the older
format over newer blobs. An append-only signed record under `format-upgrade/`
needs none of that, and propagation turned out to cost nothing: there are no
accept lists to widen. `Replication/StoreToStoreCopier` keeps a deny list with
a catch-all phase, `Agent/ReplicationResponder` validates that a committed key
parses and nothing else, and `Agent/DestinationShipSink` branches on `blobs/`
and forwards the rest.

**What is built.** `Repository.Format/Lifecycle/FormatUpgradeRecord` is the
record, its encoding and `EffectiveVersion` — the pure decision the engine and
the recovery tool share, each doing its own listing, so
`FallbackPlan.Recovery`'s dependency closure does not widen.
`Repository/RepositoryLifecycle` reads it on every open and writes it under
the repository's **signing** key, not the reclaim key: an upgrade changes what
the writer emits and destroys nothing, so a set-up installation upgrades
without the passphrase. `Agent/ServiceRuntime.UpgradeSetFormatAsync` writes
the record to the set's own store and evicts the cached archive handle in one
method, because the effective version is fixed at open and a write without the
eviction would leave the service sealing the older format until it restarted.
`Agent/ReplicationResponder` refuses an instruction that names the record —
the one real work item, and the one that stops a commander reverting a
replica's format claim. The way in is contract 1.36 `upgrade_set_format`, the
`upgrade-format` agent verb and a console control on the notice the service
raises at archive open.

**What is not, and what it costs.** There is no downgrade.
An older build meets an upgraded repository at its **first newer blob** rather
than at the door, because the descriptor's feature list is unchanged — it
refuses, so *refuse, never misread* holds, but it reports damage rather than a
format it does not know
([0014](#0014--one-format-and-a-refusal-by-name)). And a destination holds
newer blobs before it holds the record that explains them, until the next
reconciling pass: a capture ships what it wrote, and the record is an ordinary
immutable object no capture produces. That costs nothing, because every blob
declares its own container, and `Hosts.Tests/FormatUpgradeTests` pins both
sides of the window rather than hiding it behind a sync.

### 0067 — the rewrite that holds no key

**Built end to end**, in five commits, and the backlog it acts on had been
printed by the planner since the planner was written. `Retention/CollectionPlanner`
now names the partly-live blobs rather than counting them;
`Retention/CompactionPolicy` prices them by what a rewrite *reads* — live
bytes plus dead — and picks the ones past a dead fraction and a reclaim floor,
under a per-pass byte budget; `Repository/BlobCompactor` copies each live
record's sealed bytes through `Repository.Packing/BlobWriter`'s
`AppendSealedRecordAsync`, re-framing only the header;
`Repository/CompactionPublication` publishes the supersessions with the
covered digests and Merkle roots, split at blob boundaries because
`Repository.Index/IndexDeltaCodec` is the one metadata codec with no size
guard; `Repository/CompactionPass` orders the whole thing — intent, seal,
upload under extensions, publish, retire last — and
`Agent/ServiceCommandHandler` runs it as the third phase of `retention --apply`,
beside the peer convergence and inside the set gate.

**The compactor holds no content key**, which is the claim the record is named
for and is asserted rather than argued: `Repository.Tests/Packing/BlobCompactionTests`
constructs it with nothing that could decrypt and watches it complete, and the
relocated ciphertext is byte-identical to the source's.
`Repository.Packing/BlobReader`'s `ReadSealedRecordAsync` is the one read in
the product with no key path at all — it shares the header/table cross-check,
so a compactor cannot faithfully relocate corruption.

**The pass deletes nothing.** `Retention/CollectionPlanner` learned the one
thing that makes the space come back: a record whose location the index has
moved is dead where its bytes still are — guarded so that a supersession into
a blob nobody holds condemns nothing, which is the inverse of
[0025](#0025--decrypt-and-reseal-superseded-for-format-3)'s exit criterion 12
and the half that loses data if it is got wrong. The tombstone, the grace and
the sweep are the collector's, unchanged, so the reclaim lands two passes
after the rewrite rather than one.

**What is not.** A format-2 set is refused by name and told the remedy, and
quietly when there was nothing worth rewriting anyway. A peer's replica is
never compacted: outside a run `Agent/DestinationShipSink`'s read order takes
local paths only, so the limit is stated rather than coded, and a set whose
destinations are all peers — or all away — plans nothing rather than failing.

**And the tombstone now says which of the two it was.** This record's first
named follow-up was withdrawn rather than deferred: it claimed the collector
"does not know provenance", and `Retention/CollectionPlanner` was already
computing the distinction, because separating an object nothing reaches from
one the index has moved is what decides condemnation at all. Deriving the
reason needed no coupling, no durable state and no message from the compactor.
Looking for it found that the reason had never carried information at all —
both of `Retention/StagingSweep`'s call sites hard-coded *unreferenced*, so
three of specification 11 §3's four values were declared, encoded, decoded and
round-tripped by tests while nothing wrote them. The reason is inside the
signed bytes, which makes it the repository plane's half of FR-GC-008's audit
record rather than an annotation.

**And it takes data blobs only**
([amendment](adr/0067-the-keyless-compactor.md#amendment-2026-10-compaction-takes-data-blobs-only)).
Proving Phase 4 found the policy choosing a metadata blob. A metadata
record carries no sealed record key, so copied into the data blob the
compactor writes it was framed eighty bytes wrong, and
`Repository.Packing/BlobWriter`'s mismatch error ended the retention command
with no report, on every pass after. `Retention/CompactionPolicy` now takes data
blobs only, reading the class with `Repository.Packing/BlobStoreKeys`'s
`ClassOf`; `Repository/BlobCompactor` refuses any other source by name before it
writes; and the dry run counts metadata blobs on a line of their own. A
metadata blob's dead bytes come back when the whole blob dies. The test that
found it is the first to compare bytes after compaction: compacting every data
blob in an archive changes no byte of any snapshot or manifest
(`InterruptionTests/CompactionInterruptionTests`).

**Its order is proved against the service** (decision 2). A direct-ship set's
compaction, cut after each of its five steps through the sink, still restores
and is finished by the next pass (`Hosts.Tests/CompactionRetentionTests`). A
cut before the index moves leaves the blob that pass uploaded to its unretired
intent, which covers it until the intent expires.

### 0068 — a restore fetches what it needs

NFR-PERF-009 budgets a restore at 1.2× the distinct blobs holding the segments
it needs, and had never been met. Three terms, and the arithmetic says all
three were needed: the load was proportional to the **repository** rather than
to the restore; each blob it read from was opened through its locator and
footer, which is three GETs before a byte of payload; and every manifest and
every segment was a ranged read of its own, although a file's records sit next
to each other in the blob they were written into. Coalescing alone reaches
`2(B + M)`, which does not fit `1.2B` either, so the first run of a blob
reaches down to offset 0 and takes the envelope with it — the fold is required
rather than an optimisation.

`Repository.Catalogue/Catalogue.ResolveLocation` answers every field a ranged
record read wants, so `Repository/RepositoryReader` reads from there and opens
a blob's footer only when that fails. It is safe because it fails **closed**:
a record is sealed with its identity in the AAD, so a wrong offset produces a
tag failure and never silent corruption, and the catalogue was already a
disposable cache ([ADR-0010](adr/0010-local-store-separation.md)) rather than
an authority. What it gives up is the footer as a *second* statement of where
a record is, and with it the sharper diagnosis — which the lazy fallback hands
back on the one path that wants it, which is damage.

Two findings the design turned on. A read takes its key and its AAD from the
record's **own** header, so a location pointing at a different but perfectly
valid record in the same blob decrypts, authenticates and content-verifies —
the object-id comparison is the whole of what stops a neighbour's bytes being
served under the requested object's name, and it is held on the coalesced path
as well as the dedicated one. And prefetching only the file being restored
costs one read per *(file, blob)* pair, because consecutive files share the
blob they were written into — so runs outlive the call that fetched them and
`Restore/RestoreExecutor` reads ahead in bounded waves.

Measured: **11 GETs over 11 blobs holding 60 records, against a budget of 14**,
where the same restore cost 93. What is **not** covered is stated rather than
absorbed: a sparse restore of one small record out of large blobs cannot fold
the envelope, so it costs two reads a blob; and the plan verb's reachability
probe (`Restore/RestoreBlobSet`, FR-RST-003) still opens each metadata blob
through its footer, which is a different question asked before anything moves.

### 0069 — one of four

NFR-PERF-013 promises that background activity observes configured CPU, disk,
network and **time-window** limits, and until this record none of the four
existed. The round that filled the last unmeasured performance rows went
looking for the CPU cap to measure and found nothing to measure, so the row
was corrected from *unmeasured* to **unbuilt**; it was the only **Unproved**
row on the [proof page](proof-obligations.md). This is the first of the four,
and it is the one a person actually asks for — *don't back up while I'm
working* — and the one a container can settle deterministically, CPU being
exactly the figure NFR-PERF-007 already discounts as container measurement.

**The keystone is that the suspension already existed and already did the
right thing.** [ADR-0047](adr/0047-backup-pool-and-priorities.md) Amendment 1's
preemption pauses a running job through `Agent/PauseGate`, waits for it to
park at a file boundary, and bounds the park with a max-pause cap whose own
doc comment describes self-cancelling "to the interruption-safe re-run path".
That is precisely what a window closing over a running capture needs, and it
was already built and already tested. The window needed somewhere to ask —
and one rule that is the least obvious part of the design.

**The hold is a standing state of the pool, not a per-job ask.**
`Agent/JobScheduler`'s writer pump resumes the best-ranked parked run *the
moment a worker frees*, which is exactly what a park does — so a one-shot
`Pause()` from the pass would be undone within milliseconds by the worker the
park itself released. `HoldBackgroundAsync`/`ReleaseBackground` stand instead,
and while the hold stands the pump neither resumes a parked background run nor
starts a queued one. The queued half costs one peek: the lane's key sorts
user-initiated first, so a background head means every entry behind it is
background too.

Two live defects surfaced while building it and were fixed here. A person's
pass **deadlocked against the hold it had just placed** — `RunPassAsync`
hard-coded `userInitiated: false` at every enqueue site, so `agent run --once`
inside a shut window was rightly let through the gate and then refused by the
pool it had itself held; the pass's initiation now travels to the work it
queues. And the journal's park reason was **about to become a lie**:
`PauseGate` hard-coded *"suspended for a higher-priority run"* into the row a
park writes, which is the sentence a person reads the next morning to answer
why their backup stopped at ten. The reason is carried per ask now, first ask
winning it.

What is **not** built, and stays named: the other three limits — of which the
disk and network ones have since been built, as
[0074](#0074--two-more-of-four), leaving CPU. And two limits
of the window itself — only a capture parks, because only writer-lane jobs
carry a pause gate, so a fan-out or a drill already in flight runs to
completion; and the window is enforced to the granularity of a pass tick. The
window is **reported** on `get_status` (contract 1.39), the CLI and the
console, and **edited in the configuration file only**, as
`max_concurrent_backups` is; the console control is owed rather than smuggled
in behind a status field. That control has since been built as
[0037](#0037--the-configuration-lifecycle-joins-the-contract)'s Amendment 1, so
the window is set through the service, the CLI and the console too.

### 0074 — two more of four

The disk and network limits, built together, because they are one mechanism
at two seams: a rate, a limiter every job it governs shares, and a stream that
charges what passes. A destination's `transfer_limit` paces what background
work moves to or from it, and `background_read_limit` paces what background
captures read from the source. A person is never paced; that is the window's
predicate, `userInitiated: false`, now carried into the capture, the fan-out,
the sweep and the drill.

The limiter is a token bucket with a one-second burst (`Application/ByteRateLimiter`),
on a clock the runtime takes through its options, so the host suites prove a
rate by the waits it asked for. The seams are the replica store for a local
path, **the session's own stream** for a peer's push — the wire, which counts
exactly what crosses the link — the ship sink's write targets for a
direct-ship run, and the restore source a drill reads through, on the handler
the drill builds for itself.

Two paths are unpaced on purpose, and the record says why: the ship sink's
reads, because a direct-ship set's archive store is also what a person's
restore reads through, and the push a retention apply makes, because a person
starts it. The first cut's tests for the local-path copy and the direct-ship
capture passed with their seam removed — the pass's drill read the replica
back through the same limiter and paid for the bytes — and now run their own
job alone. CPU remains unbuilt and named.

### 0075 — one copy was never the only one

A restore read exactly one store, and every other copy of the same bytes sat
unused while it failed a file. Blobs are immutable and a replica holds them key
for key, so a record sits at the same offset of the same blob at every copy.
The reader now reads a record its own store will not serve at that one location
from the set's other copies, and through the same checks
(`Repository/RepositoryReader`). The copies are the repair's own list, nearest
and cheapest first (`Agent/SetCopies`), so the repair and the restore cannot
disagree about where a sound copy may be. A copy is opened only once every
earlier one has failed, and a peer is dialled only then.

It is the set's own archive that reads around. A destination named as the
source is read alone, because the drill restores through it to prove that copy.
A restore that let a named source read around its damage turns exactly the
named-restore and drill tests red.

Three things came out of reading the code rather than out of the plan. First,
a copy's failed location read is not damage until that copy's own footer
agrees: a footer that does not list the record means the location was wrong.
The rule already held at the own store (ADR-0068 §2), and now holds at every
copy. Second, a direct-ship set's own store is a read path over its
destinations rather than a copy, so it is never named, and each destination is
named when it is tried in its own right. Third, ADR-0034 §6 had accepted that
a trimmed snapshot could not be restored from staging. The same change retires
that cost, and the plan now counts a file missing only when no copy holds it,
so the plan and the run agree.

What a restore finds damaged at a destination goes on its ledger row, as the
sweep's findings do: the next sync repairs a local path's objects, and holds a
peer's with its owner's remedy. Damage in the staging archive is a notice
alone, since nothing repairs it in place.

### 0076 — a scope a person can act on

A destination holding damage nothing could replace degraded every snapshot
held there, and its notice named blob keys. The first overstated the harm and
the second understated what a person needed to know. The catalogue could go
from a blob to its objects, but not from a segment back to the versions using
it, because that list lives in sealed manifests.

Catalogue schema 8 now keeps that list the other way round: which content
objects each version needs, and which records each snapshot is made of,
continuations included. The capture writes it, and so do the projection and
the forensic rebuild. A test over each of the three holds that every blob a
capture wrote traces to what needs it, so no route can leave the index out
without being noticed.

The trace runs whenever a status is read, and is never stored with the
finding. A capture that finds content already held reuses those objects,
damaged ones included, so a snapshot taken after the finding can need the
damage too. A stored scope would miss it.

The trace takes no write lock on the catalogue, so a status read never waits
behind a capture. Review found the first version taking one for its scratch
table, and a test that traces while another connection holds the lock came
before the fix.

What could not be traced counts against every snapshot, rather than none.
That includes a store key the catalogue locates nothing in.

The ledger records whether a pair's failure is its damage alone (schema 7).
Only then is the degradation scoped to the snapshots the damage reaches.
Three rules keep that honest:

- A damage finding recorded over another kind of failure does not narrow it.
- A sync that copies everything and still finds the damage standing does
  narrow it. That write appeared during implementation, because without it a
  failure unrelated to the damage would have kept the whole copy suspect for
  as long as the damage stood.
- A finding whose every object was replaced degrades nothing.

A peer's finding also says whether the staging archive or a local path holds
the objects sound. It proves them there and never dials another peer to find
out.

The ADR states three limits. A pruned snapshot's rows linger in the catalogue,
so a count can run high. The sample is ordinal, not ranked. A directory is
counted, not named.

The catalogue schema change needed the rebuild at open to land first
([ADR-0010 Amendment 4](adr/0010-local-store-separation.md#amendment-4-2026-09--a-catalogue-discarded-is-rebuilt-before-it-is-read)).

### 0077 — a clock can only be compared

The format has had a place for observed clock skew since phase 0, and nothing
filled it, because nothing here had another clock to compare with. One
exchange does. A peer signs its replication receipt with `issued_at` by its
own clock, between the hub's `ReplicationComplete` and the peer's
acknowledgement. The hub now reads its own clock either side of that
exchange. A verified receipt's stamp against the midpoint is a reading, good
to half the round trip.

On both hub paths, a run's manifest is signed before the run meets its peer.
So the reading waits on the pair's ledger row (schema 8), and the set's next
capture records it. A reading more than a day old, or dated in this clock's
future, is not recorded. A capture with no reading leaves key 14 absent. It
never records 0, which every surface would read as a clock in step.

The end-to-end tests run a real peer whose receipt clock is three hours
ahead, once on each hub path. The first capture records nothing. The second
records three hours behind, within five seconds, and `list_snapshots` reads
it back for that snapshot. Removing the recording on either path, or the
capture's, turns them red.

Two things surfaced on the way:

- **The capture's own catalogue row had its own capture time.** It wrote the
  capture's start as `captured_at`, while both rebuilds wrote the manifest's
  completion stamp, so a snapshot's time moved the first time its catalogue
  was rebuilt. Both now write the completion stamp, and a test holds the
  capture's row and a projected one to the same value.
- **Filed receipts age by the peer's clock.** Receipt retention compares
  `issued_at` with this clock, so a peer whose clock is wrong has its
  receipts aged early or late here. The floor of newest receipts holds
  whatever the clocks say. The ADR records this and changes nothing.

The standalone recovery tool's snapshot listing prints the reading too, read
from the manifest alone. It shipped a little after the rest, because the
token was the CLI's and the recovery tool may not reference the CLI. The
token now lives in Domain, which both reach, so the two listings say the same
thing by construction.

### 0078 — the order a writer published in is the one a clock cannot move

Architecture 04 §7 promised that a snapshot whose recorded time is
implausible beside its neighbours is flagged rather than silently expired.
The planner judged every snapshot by the time its writer's clock stamped, so
a capture taken while the clock read 2001 expired at the first pass after the
clock was put right. It also expired at every destination on the next sync,
because convergence runs the same planner. A capture taken while the clock
ran ahead took a min-generations place from the real newest.

A writer's publication sequence reads no clock, so the yardstick is the
longest run of a writer's snapshots whose times never fall as that sequence
rises.

- **Off the run by more than the margin:** implausible, behind or ahead.
- **Two runs as long:** the later times win, because the other choice is
  the one that could let a run expire.
- **Across writers, or without a sequence:** nothing is judged.

This machine's clock decides only which captures may anchor the run. A rule
that flagged anything dated a day ahead of the collector was measured first,
and it changed 25 of the retention suite's 108 tests. Those suites capture
at the live clock and collect at a fixed August date.

Two things surfaced on the way:

- **The survey never carried the writer.** Sequences are one writer's own,
  and an archive adopted under a new identity starts a second sequence near
  1. Read as one order, every one of the new writer's snapshots would have
  been out of step. The survey now reads the writer from the standalone
  record's cleartext, as it already read the sequence, and the order is
  judged writer by writer.
- **A stamp past the calendar threw.** `DateTimeOffset` cannot hold a time
  past the year 9999. The window rules handed one straight to it, and the
  largest stamp a manifest can carry read as 1969 and expired as ancient.
  Both are now the calendar's last instant, as a stamp from the future
  always was.

A misdated snapshot stays misdated once the clock is put right, so its
notice is not raised again after a person acknowledges it unchanged
(`RaiseUnlessAcknowledged`). Retention keeps a flagged snapshot
indefinitely. Since [ADR-0080](adr/0080-a-person-deletes-a-snapshot.md) a
person can delete one.

### 0079 — a hole and a written zero read the same

FR-ARCH-013 said sparse extents restore "without materialising zero
payload", and specification 09 §4 said holes restore as holes. The engine
wrote every hole into its spool as zeroes and copied the spool out, and the
recovery tool did the same. So a restored sparse file was fully allocated,
and a 100 GiB image holding 10 GiB of data needed 100 GiB of temporary space
besides. Every restore test compared bytes, which cannot tell a hole from
written zeroes, and that is how the gap went unseen. The oracle now
is allocation: `TestSupport/AllocatedSize`, which asks each platform how much
of a file it holds and shares no code with the product's own stat interop.

Skipping a range is correct everywhere, because every platform reads zeroes
from what an extending write skipped. Only allocation differs.

- **Linux** leaves the skipped range unallocated wherever the filesystem
  holds holes, with nothing asked.
- **APFS** leaves it unallocated if it is 16 MiB or longer. A shorter one it
  fills with zeroes and allocates once it writes the file back, however the
  file was written. The first macOS CI run found it: the tests' holes were
  4, 15 and 11 MiB, every one was filled, and the tests that passed had
  measured before write-back. So the oracle now writes a file back before
  it measures, the suites' holes are 32 MiB, and a macOS-only test pins the
  16 MiB.
- **NTFS** allocates and zero-fills it unless the file was marked sparse
  first. So `Domain/SparseFile` marks a file that will have a hole, and only
  such a file, because a dense file carrying the sparse attribute is not the
  file that was captured.

The engine leaves holes in its spool and in what it emits. It emits that way
only into a destination that can seek and holds nothing past its position,
because a skipped range keeps what it held. Anything else gets the zeroes
written out, as before.

The suite found a capture bug on the way. A file that is one hole from end
to end was described by no segment and no extent, which the codec refuses.
Capture had recorded extents only for a file that also had data.

Each piece was removed in turn and its test went red: the spool's skip, the
emission's skip, its guard against existing bytes, each of the three length
changes, the recovery tool's skip and the capture fix. What remains is recorded rather
than done. Windows capture still reads holes as data, so only a manifest
from POSIX restores sparse there. On APFS a hole shorter than 16 MiB is
still allocated, by the filesystem's own choice. The engine's spool still defaults to the
system temporary directory, which caps the largest dense file a restore can
produce where that directory is small or RAM-backed.

### 0080 — the request is a tombstone, and a missing row is not a missing copy

A person could not delete a snapshot. Retention keeps what its rules keep,
and since ADR-0078 it keeps a misdated snapshot whatever they say. The verb
had three facts of the collector to work around:

- a tombstone is revalidated against a fresh plan, so one pass's decision is
  undone by the next;
- a copy drops only the keys staging still lists;
- a copy with no rules never drops anything.

So the request is a tombstone of the snapshot's manifest, reason 5
*requested*, and every survey reads it. It is verified against the reclaim
public key the write credential carries, so a scheduled sync honours it
without the passphrase. While a request stands every destination converges,
one with no rules included, and records `converged_sequence` in the ledger
(schema 9). Staging holds the snapshot until every declared destination has
converged since the request.

Three things were found on the way, and each has its own commit.

- **A missing ledger row had been read as a destination never written to.**
  Rows are keyed by the destination's name. A rename carries the sets'
  references to the new name but not the row, so a renamed drive holding
  everything had no row, and a ledger that cannot be read is set aside and
  starts empty. Either way, a retention pass could have let staging go of a
  requested snapshot that a copy still held. Every declared destination now
  holds it until it has converged, and a missing row counts as never
  converged. The routed CLI test, which declared a vault it never created,
  now plugs its vault in, and a service test pins that a drive no copy has
  reached holds the deletion and is named.
- **The last-snapshot guard counted only complete snapshots.** A set whose
  every capture is partial had none for it to keep. Such a set now keeps its
  last snapshot of any kind.
- **The console's ceremony test spoke snake_case**, where every ceremony
  endpoint speaks camelCase. It now posts what the page posts.

The command runs two passes, with a gc-pass audit record between them. The
first pass condemns what only the deleted snapshots held, and that content's
grace waits for a publication. Without the second pass it would wait for a
retention run nobody may ever start. Both passes carry out requests alone:
the set's policy is set aside, so asking to delete one snapshot never
expires another.

A scheduled push to a peer holds no grant. While a request stands it now
sends everything but the requested snapshots and instructs no drop. Before,
a peer with no rules would have been sent a requested snapshot it lacked,
and kept it.

What remains is recorded rather than done. A request cannot be withdrawn,
and the agent has no verb. Requests are verified against generation zero's
reclaim public key, which holds while nothing rotates reclaim keys. A
destination that never returns holds the deletion until it is removed from
the set.

### 0081 — a bundle needed the redaction to be true first

The diagnostic bundle architecture 10 §4 promised is mostly the log, and the
log's redacted rendering was meant to be safe already. Reading it before
building on it showed it was fail-open: a value no type classified crossed as
written, and an exception's message was appended in every mode. On a real
filesystem that message is "Access to the path '…' is denied.", so the path a
`LogPath` hole had just hashed came back whole beside it, in the record's text
and in the filesystem's own `{Reason}`. Of about a hundred and fifty string
holes, the service's carried peer fingerprints, set, repository and writer
identities, and its state and archives directories at startup. A paired
console was reading all of it.

So the slice turned the rendering default-deny before it built anything on
it. A value crosses only when its declared type says how; `LogLabel` is a call
site vouching for words safe everywhere, and has no implicit conversion;
`LogId` wraps an identifier already held as text; `ObjectKey` became
redactable; every product hole was classified; and a paired caller's
`read_log` withholds an exception's message. `LoggingShapeTests` refuses a
hole named for an identifier or a path declared as a bare string. That lint
guards against an identifier being withheld rather than shortened; the
renderer is what stops a leak.

Tests that asserted a reclassified hole's raw value now expect the label:
the same words, a different declared type. `LogPrivacyTests` allows a label
as it allows a string, only when it is not path-shaped.

The bundle itself is `export_diagnostics` (contract 1.52): a zip of the log,
environment, configuration, status, open notices and recent jobs, built from
typed projections rendered by the same rule, so a configuration field added
later is absent until somebody classifies it. The bytes cross as base64,
because no contract member carries raw bytes, and the service writes no file.
Paths come only by a per-bundle opt-in, refused to a paired console.

What remains is recorded rather than done. The opt-in releases text no type
classifies, which can quote an identifier in full, and its consent says so.
The bundle carries the ring, not the rolled files: those were rendered in full
when written, and redacting them now would mean matching strings. The
standalone recovery tool, which architecture 08 §5 also asks for a bundle,
has one now ([0082](#0082--the-tool-that-runs-on-the-worst-day-says-what-happened)).

### 0082 — the tool that runs on the worst day says what happened

Architecture 08 §5 asks the standalone recovery tool for a diagnostic bundle
containing no secrets, and 0081 left it owed: the tool has no service, no ring
and no state, and its project references are an exact whitelist that the
redaction rule's project was not on. The rule moved instead of being copied. It
now lives in Domain beside the types it reads, the service's renderer applies
it to records, and an architecture canary fails if the tool ever renders
through a copy.

Every verb takes `--diagnostic-bundle <file>`. The tool writes the bundle after
the run however it ended, because the run that failed is the one worth
sending. It is a report, not a log. It covers what was asked; what the
descriptor said; whether the passphrase reproduced the keys; which blobs did not
open; the snapshots; what a restore did with every file it could not restore;
and the stage and reason the run stopped at, recorded where the tool refuses
rather than read back from a message. The record the tool keeps of a run holds
only what the bundle classifies, and from the descriptor it takes what
describes the archive and leaves what identifies the installation. That means
the salt and the sealing public key, the same in every archive an installation
writes, and the creator, which the full client fills with the machine's name.
The inspection test caught that last one by planting the machine's name exactly
there.

A recovery note became fields, not a sentence: the terminal still prints the
sentence, and the bundle renders the path, the record and the reader's words
each by its type. The inspection test damages every data blob of a real
archive whose backup holds a telling folder, so the restore fails by name, and
reads every entry back as text and as decoded JSON. Paths come only with
`--include-paths`, which the tool states before it starts, and identifiers
still shorten.

What remains is recorded rather than done. A default bundle withholds the
format codec's own explanations along with the platform's, because both are
text no type classifies. The stage and the reason say which kind of failure it
was, and the opt-in says the rest.

### 0083 — a plan that knew the disk

FR-RST-003 has always asked a restore plan to report free space and the
privileges a restore needs, and to report a space shortfall before any byte is
written. FR-RST-004 asks the receipt for every degraded attribute.
Architecture 08's Built line claimed its §2 whole. None of it existed. The plan
summed logical lengths and never looked at a disk, so a restore that could not
fit started anyway and failed file by file as the disk filled. The executor
wrote back a modification time and, on a POSIX target, a mode, and dropped the
rest without a word.

A plan told where the run would write now measures what the run writes there,
volume by volume. That is each file's segments in whole clusters, its holes
skipped, and a cluster for each directory it creates. It adds room for the
largest file where the engine holds it until its hash verifies, which is the
volume's own figure when the two share it. In place, only what the
existing-file policy frees is credited. The plan already decodes every
manifest, so its figure is exact. The run measures from logical lengths and
reads manifests only when those say it is short, so a restore that fits pays
nothing for the check. One that will not fit is refused before it writes
anything, its folder included, naming the space needed, the space free and
the folder.

A person may go on regardless: through the service with ignore_free_space,
through the CLI with --ignore-free-space, and in the console's wizard by
ticking "restore anyway". The estimate errs high, and a volume that compresses
what it stores holds more than it is asked to.

One rule says what a target gets back, and the plan and the receipt both read
it. The plan declares each captured attribute the tree carries and the target
will not get back, with how many files carry it, and names root or CAP_CHOWN
for ownership. Receipt schema 6 names it per landed item, and the answer
summarises it, a line an attribute. Outcomes do not move for metadata alone.

Every plan over real files now lists access times, and most list creation
times and ownership. That is the truth, and it is the next slice's work.
Still owed: writing those back, which the times have since been
([0084](#0084--the-receipt-says-what-landed)); architecture 06 §3's refusal
for a security descriptor; hard links, which restore as separate files
without saying so; the physical transfer size; plan export and resume;
archival-tier reporting; and the recovery tool's own restore.

### 0084 — the receipt says what landed

0083 named, per item, what a restore did not write back, and left writing it
back as the next slice. This is the first half of that slice: the times. A
restored file reads back its captured access time on every platform, and its
creation time on Windows and macOS. Linux has no call that sets a creation
time, so there it is still listed, and the plan still declares it.

.NET has a creation-time setter on every platform, and on two of them it does
something else. On Linux, and on a macOS volume that keeps no creation times,
it writes the modification time instead. A restore that used it would replace
the time it had just put back and say nothing. `FileTimes` calls
`setattrlist` itself on macOS and answers a refusal as not set, and on Linux
it attempts nothing.

Writing the times back turned up three defects in the path that already
existed. Metadata was applied inside the catch that lands a file, so a
refused write failed an item whose content had landed and verified, and a
drill counts that as a failed drill. A captured time past the year 9999
threw outside every per-item catch and ended the run with no receipt, from a
manifest a restore treats as untrusted. And the receipt's list came from the
rule, so a write that failed would still have read as applied. Each
attribute is now written on its own, after the content and outside the
landing's catch. An impossible time is nothing to apply. The executor
returns what landed, and the list is what was captured minus that.

The plan still predicts by the rule, because it cannot know what a write will
do. The target profile gains `SupportsCreationTimes`, and the executor tries
a creation time only where the profile allows one, so the two agree. Access
times are no longer declared anywhere. The service and CLI tests had anchored
on access times as the attribute every file carries and no target applied.
They now anchor on the owner on a POSIX host and the attribute bits on
Windows, and assert that access times are gone.

The creation-time call is exercised only where it exists, so its proof is
the macOS and Windows legs of the CI matrix.

What remains is recorded rather than done. Ownership was the other half of
the slice, and has since landed
([0085](#0085--whose-file-it-is)). A directory's own
metadata was neither written back nor named in its receipt item, which is a
silent drop architecture 06 §3 rules out; writing it had to wait until the
directory's children had landed, and has since landed too
([0086](#0086--a-folders-own)). A symlink's own metadata, Windows
attribute bits, security descriptors and alternate streams are still listed;
extended attributes have since landed ([0087](#0087--extended-attributes)). The recovery tool and the CLI's `restore-file`
write no metadata at all.

### 0085 — whose file it is

0084 wrote back a file's times and left its owner and group as the other half
of the slice. They are captured by name, and until now every restore listed
both as not applied, even for the account that owned the files. A file now
gets its owner and group back on a POSIX target where the names resolve there
and the account running the restore may give them. Root, or a Linux process
holding CAP_CHOWN, may give a file to anyone. Any other account keeps a file
as its own and gives it a group it is in. Owner and group are written apart,
so a refusal of one leaves the other.

Ownership goes first, before the permissions, because changing it clears the
set-id bits. Writing that order down turned up a hazard older than the slice.
A restore wrote a captured mode whole, so one running as root landed every
set-user-id file it restored as a root-owned set-user-id file, whoever had
owned it. A set-id bit is now kept only where the owner or group it runs as
was given back. Otherwise it is dropped, and the permissions are reported as
not applied. GNU tar keeps the same rule.

The plan predicts each file the way the run will, from the names and mode its
probe already decodes. It declares ownership for one of two reasons, because
their remedies differ. A name the account may not give needs root or
CAP_CHOWN. A name that resolves to nothing needs an account, which no
privilege creates. The plan also counts the set-id bits the run will drop,
and the receipt summary's privilege hint now goes only to an account
without the privilege.

The service and CLI tests had anchored on the owner as the attribute no
target applied, and the account running them now gets its files back. On
POSIX they anchor on a symlink, whose own metadata is still never written
back.

The proof is split by privilege. A suite running as root, as it does in a
container, proves the privileged half. CI's runners, which are unprivileged,
prove the other. Neither runs both.

What remains is recorded rather than done. A directory's ownership was owed
with the rest of a directory's metadata, which has since landed
([0086](#0086--a-folders-own)), and a symlink's is owed with the rest of a
symlink's. A Windows file's owner lives in the security descriptor, which is
captured and not applied. A restore running as root into the default
quarantine folder still recreates a root-owned set-user-id program
faithfully, in a folder other accounts may reach. Whether a quarantine restore
should keep set-id bits at all was left for a later decision. It is now
decided: it keeps them, as any restore does, because quarantine decides where
content lands and not what comes back. Architecture 08 §3.1, the threat model
and SECURITY.md say what that means for the folder, and that a historical
system tree restored as root belongs in one only the restoring account can
reach ([ADR-0085 Amendment 1](adr/0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md#amendment-1-2026-10--a-quarantine-restore-keeps-set-id-bits)).

### 0086 — a folder's own

A tree manifest's first record has always carried what was captured with its
folder. No restore read it. Each folder came back with the time the restore
made it, the restoring process's default permissions and the restoring account
as its owner, and its receipt item said nothing of it. 0084 recorded that as
owed.

The run now reads each folder's tree as it reads a file's manifest, and the
read-ahead fetches it with the files' manifests from the same metadata blobs,
so the read budget is unchanged. A folder the run makes is held to the file's
rule: owner and group where the account may give them, then permissions that
keep a set-id bit only with its owner or group, then the times. It gets them
only once the run has written everything else, deepest folder first, because
every item landing in a folder moves its modification time, and a folder
captured read-only would refuse what was still to come.

Three cases keep a folder from being given anything, and its item lists
everything captured with the reason. A folder already at the destination keeps
its own, because no existing-file policy reaches a folder and a live folder's
permissions are its owner's current choice. Anything that has taken the place
of a folder the run made is given nothing, because the permissions and times
are written by path and would land on whatever a link points at; the check is
made just before the write. A folder whose tree will not read is still made,
since it holds what restored under it, and its item says why. Metadata alone
still changes no outcome.

The plan reads each folder's tree for the same reason, names a folder whose
tree the store does not hold, and counts folders apart from files. On Windows
each folder's attribute bits are now listed with the files', so the service and
CLI tests count three items where they counted two.

The swap check is proven by a store that moves the folder aside and plants a
link in its place while the run reads its first file. Without the check, the
folder's permissions land on the link's target.

What remains is recorded rather than done. The snapshot's root is restored as
its contents, into the folder the restore writes into, and that folder keeps
its own metadata; the plan does not declare the root's. The run writes by
path, so an account that can change the tree while it runs can still redirect
a write in the moment between a check and the write it guards. The threat
model now says so. A symlink's own metadata, Windows attribute bits, security
descriptors and alternate streams are still listed, for folders as for files;
extended attributes have since landed ([0087](#0087--extended-attributes)).

### 0087 — extended attributes

Capture has always recorded every extended attribute a file or folder carries
on Linux and macOS: a Linux file's ACL and a folder's default ACL, SELinux
labels and file capabilities, and macOS's quarantine flag, Finder information
and resource forks. No restore wrote one back. Each was listed as not applied.

Each is now written back alone, by its captured name and never through a link,
after the owner and group and before the permissions. After the owner, because
giving a file away strips its capabilities. Before the permissions, because a
mode that takes away the owner's write bit would refuse the owner's own
attributes, and because an ACL sets the mode's group bits that the permissions
then set as captured. One the account may not write or the volume refuses is
listed, and the item's detail names it and why. The rest still land.

Three rules keep some back, and the plan declares each with counts. An ACL
names accounts by number, and a number names the account it meant only on the
machine that captured it, so an ACL that names one comes back only where this
installation captured the snapshot. The service and the CLI compare the device
a snapshot names with their own; that device is attribution by claim, which
the threat model notes under T-18. macOS keeps no POSIX ACLs, so none is
written there as an attribute that would grant nothing. And on Linux a restore
that is not root leaves the security and trusted namespaces, as its plan says,
even where a security policy would let a label through.

Writing that rule down turned up a widening older than the slice. While a file
has an ACL its mode's group bits are the mask, which bounds every account the
ACL names. Every earlier restore wrote that mode back without the ACL, so a
file shared with one account for writing came back writable by its whole
group. Wherever an ACL does not come back now, the group gets only what the ACL
gave it, and the permissions are listed.

The proof is split by privilege, as ownership's is. A container running as
root proves the capability surviving the ownership write, the trusted
namespace written, and the rule that withholds what root could write. CI's
unprivileged runners prove the refusal and the read-only mode. macOS's leg
proves the macOS rules. Each ordering and rule was broken in turn to see its
test fail.

What remains is recorded rather than done. An ACL's entries are captured as
numbers; capturing their names would let one come back on any machine where
the names resolve, and is owed. macOS's own access lists are not captured,
Windows security descriptors are not applied, and Windows keeps no extended
attributes a restore writes. A symlink's own metadata is still owed, and the
recovery tool and the CLI's `restore-file` still write no metadata.

### 0088 — what is backed up

A first backup of 9,190 files, 4.48 GB, to a local folder showed 100% for the
last four and a half minutes of a nine-and-a-half-minute run. The meter
divided files handled by the plan, and a file is handled once the walk has
packed it. Two kinds of work were still to come. Up to the upload queue's
worth of sealed blobs was waiting to reach the destination, about 384 MiB at
the defaults. Then the publication wrote one source-identity hint per new
file, one after another, each a round trip to the metadata store and the
destination: 9,190 of them in three minutes and forty seconds.

What a backup reports now is what it has backed up. Its archive session keeps
a tally of the plan's bytes in the order they were archived, and releases them
only once the blob holding them, and every blob opened before that one, has
been acknowledged. In a direct-ship run, acknowledged means held at every
destination the run writes to. An unchanged or renamed file counts the moment
the run reuses it. Each planned file contributes exactly its planned length,
so a re-read or an alternate stream never counts twice, and a sparse file's
holes, or a file that failed or shrank, still count where the file ended.

The console shows one figure for the bar and the percentage, held at 99 until
the job settles. While the run is publishing, the words beside it say
"Finishing" and count the hints. The hints were written sixteen at a time, all
before the snapshot record; since [0090](#0090--one-pack-a-backup) they are one
pack, still written before it. The figure rides every report, and is reported
again each time an acknowledged blob moves it, so the meter moves while one
large file is still being read. The CLI's `jobs <id>` prints how much of its
plan a run backed up, which for a failed or cancelled run is how far it got.

The proof holds uploads still. A store holds each data blob's put until the
test releases it, so "read but not stored" is a state the test stands in
rather than a race it hopes to catch. The same tests show that releasing one
blob counts what it carried and nothing past it, and that an unchanged file
counts before any upload.

What remained was the owner's "every destination", and the owner then
separated two questions the one figure had blurred. Amendment 1 answers each
on its own.

**How far the job has got** is its bar: three equal stages over the plan's
files, scanned, processed and backed up. The tally now counts files beside
bytes, each once everything archived up to its end has been acknowledged.
An unchanged file counts at once, and a file that failed never counts.

**How much of the backup each destination holds** is its circle: how many of
the newest backup's files have all their content there. At rest it is worked
out from the ledger's watermark and the catalogue, with nothing listed.
Getting that right turned on one fact. A snapshot record's counter is its
run's write intent, and the run's content is published later, in its index
delta, so the watermark is first moved to the delta of the next snapshot
after it. Measured against the raw watermark, every destination would seem to
lack the run it had just received. While a sync runs, its own count takes
over, from the destination's listing or a peer's declared inventory, and then
from each key as it lands. While a backup runs, the console folds the run in:
what it stored, where it writes, and elsewhere what it found unchanged, at
the share held before. So a run that changes nothing leaves the circle at
100%, and a first backup's circle stops at 99%. A destination's line says the
circle in words, then what is moving files there, and no longer repeats the
job. The set's summary carries its least complete destination's circle.

Writing the status side turned up an older fault. The status took a set's
latest snapshot as the last row of a newest-first listing, so its
partial-capture warning described the first backup ever taken. It now takes
the first row.

The proof again holds things still. A held run names the destination it
writes to. A missed backup leaves exactly its unchanged files counted. A sync
is watched counting up from nothing.

What remains is recorded rather than done. The set's circle is the least
complete destination's, which overstates the share every destination holds
only when two destinations lack different files. A destination whose last
sync failed part-way is counted at what its watermark supports until a sync
counts it again. The last percent of a job's bar still covers the finishing
work whatever its length, with the count beside it.

### 0089 — the names behind the passphrase

The owner found that anyone signed in could read the name of every file a
backup holds without knowing the passphrase: by browsing a snapshot, by
opening a run's "what changed" or "failures", or by planning a restore. The
restore wizard did ask for the passphrase, but it checked it against archive
files on the console's own disk, and a console anywhere else offered to go on
without the check. The service answered every signed-in caller.

The service cannot be kept from the names. It reads the structure plane on
the write bundle so that it can back up and apply retention with no one
present, and its catalogue keeps paths in the clear. So the gate is a rule the
service keeps: it names a file a backup holds only to a caller holding a
restore source opened under a verified restore grant. That grant is the proof
a restore already used, the set's sealing scalar derived where the passphrase
was typed and checked by the service against the set's sealing key. Listing a
snapshot, planning or running a restore, and a run's changes and failures are
refused without such a source. Opening one without a grant is refused before a
replica is opened or a peer dialled.

A source serves only the session that opened it. The service's connection
gate stamps every command with a digest of the session's token, which never
crosses the wire, and the source records it. Another session naming the
source is refused, even the same account signed in elsewhere, and closing
another session's source closes nothing. One set's source proves nothing about
another set, which matters for a set adopted from a destination under a
different passphrase.

The set editor's comparison was the one place a refusal would have broken
something, because it asks at every tick of a rule. It now answers without a
source, counting everything and naming what is on disk now, and leaves out
only the names of deleted files and of files the rules stop capturing, saying
that it did. The set's own "What changed?" asks for the passphrase and names
everything.

The console asks at every look, as the owner chose. A browse, a run's report,
a comparison and a restore each open a source and close it when the look
ends. The gate derives a grant per set under the facts the service publishes
and proves each against that set's sealing key, so it works wherever the
console runs, and nothing goes past a passphrase it could not check. A run of
wrong passphrases from one account is slowed, three free and then one, two,
four seconds and so on up to thirty, and never locked out. The count is the
account's, so signing in again does not start it over, and the account's tries
take turns, so a burst of them cannot all spend one free count. The CLI
derives the same way under `--passphrase-env` for `ls --connect`,
`jobs <id> --changes` and `--failures`, `changes` and `restore`.

The recovery drill restores samples with no person present, so it runs in the
service's own caller scope, which no listener builds, as backup and retention
do.

What remains is recorded rather than done, as
[T-23](threat-model.md#t-23-a-signed-in-account-reads-a-backups-file-names).
The gate is policy, so whoever controls the service account reads the
catalogue directly. Any signed-in account can read a set's salt, cost and
sealing key, and so can test guesses offline at Argon2id's cost per guess; the
throttle slows only guesses made at the console. A grant taken from the page
while a look is open is a bearer proof until the service's recipient key
changes.

Amendment 1 closed the residual this record first left standing: the names
the service raised on its own. A drill that could not bring a file back wrote
its path into the notice, the pair's row and the status matrix, and a damage
notice and verify-destination's line named a sample of the files the damage
reached, all to anyone signed in. Those words now count. A drill's failure
says "a sampled file" and keeps the engine's diagnosis with the path taken
out, as the snapshot names it and as the platform writes it. A damage notice
says how many snapshots and files the damage reaches. The names are kept
beside the notice in the notice ledger, with the set they belong to, and
`notice_names` (contract 1.59) answers them only through a source of that set
the caller's session opened under a verified grant, refusing in the same
words as the other looks. A listed notice says how many names it withheld and
for which set, so the console's notice list offers **Show files…** there and
unlocks the set first, and the CLI's `notice-names` derives under
`--passphrase-env`. A notice an older service raised keeps its words until it
is resolved or acknowledged, because nothing can tell which of its words are a
name.

### 0090 — one pack a backup

Built. The first measurement of NFR-PERF-008's upload half found one term
that grew with the number of files: the source-identity hints, one store
object per new file version. On the 4.48 GB, 9 190-file first backup that
showed the cost, that was about 2 000 requests per GB against a budget of 20.
A publication now writes them as one pack. The bytes still follow what
changed, and a request per file became a request per backup.

`Repository.Tests/UploadBudgetTests` counts every request a first backup and
an incremental make, by the kind of object each was for. A test cannot write
a GiB, so it measures the terms apart and adds them up at the object-store
blob profile:

| Backup | Requests per GiB | Blob covers | Everything else |
|---|---|---|---|
| First backup, ~490 KB a file | 23 | 9 | 14 |
| First backup, 16 KiB a file | 25 | 10 | 15 |
| Incremental, ~490 KB a file | 25 | 9 | 16 |
| Incremental, 16 KiB a file | 27 | 10 | 17 |

So NFR-PERF-008 was met for data blobs (8 per GiB against 10) and for
everything beside the blob covers, but not in total. Every blob was
preceded by the journal's intent extension that covers it, each its own
request. Covering several blobs with one was a change to the machinery
garbage collection depends on, and was owed separately; it is
[0092](#0092--a-batch-at-a-time), and the total is now 15–18 per GiB.

Repositories keep the per-file hints every earlier backup wrote, and they
are still read. The reader that runs after a catalogue rebuild takes its
device's packs once, then asks the per-file form only for a source key no
pack names, and only if the repository holds any per-file hints at all.
Neither shape is collected, as hints never were.

### 0091 — a bucket as a destination

Built. The `s3` kind was the first the configuration accepted and the runtime
refused (FR-DEST-005), and it is now served end to end: synced, read back,
restored from, drilled and probed, through the same routines a local path
uses wherever a store allows it.

**No package was added.** The provider speaks the S3 API over the platform's
HTTP client and signs requests itself, checked against an independent
implementation's vectors. That makes `Storage.S3` the one assembly in the
product with an HTTP client. `ArchitectureTests/TelemetrySilenceTests` names it
in an allowlist of one, and the rest of the file is what keeps the allowance
narrow: it opens no socket, depends on the store contract alone, and only the
service composes it (`ArchitectureTests/DependencyRuleTests`). The captured
default run (`Hosts.Tests/DefaultBuildSilenceTests`) backs up to a local path
and still makes no HTTP request.

**The access key is the slice's one secret, and it travels like the
others.** It is typed into the console or named by an environment variable
for the CLI, sealed there to the service's recipient key for one destination
and key id, and opened into the service's state directory, owner-only. It is
in no configuration file, export, listing, bundle or log;
`Hosts.Tests/S3DestinationTests` looks for it in each.

**Three failures are three rows.** No key stored, a store that refuses the
signature, and a store that does not answer were one "failed" before a store
could be reached at all. A missing key and a refusal are failed, because
waiting changes neither. An unreachable store is unavailable, which closes
itself when the store answers again.

What is not built, by decision: a direct-ship run does not write through a
store (the sync after the run fills it). A real store can be put through the
contract suite by naming it in `FALLBACKPLAN_S3_TEST_*`
(`TestSupport/ConfiguredS3Store`); without that, the suite runs against the
in-process store, which checks every signature itself.

**Amendment 1: swept, and adopted from.** The record first left two more
things unbuilt: the deep sweep did not read a store, and an archive could not
be adopted from one. Both are built now.

- **The sweep.** A store is swept on a peer's rule: only on a cadence its
  operator states, because every read is a request its provider may charge
  for. A segment reads the peer's 256 MiB share. A store that does not answer
  is recorded unavailable, as a sync records it. One that refuses is a stall
  with no blob named, waited out under the back-off rather than asked every
  minute. What the sweep finds is repaired as at a local path, except that the
  damaged object is deleted and put again, since a put to a store never
  overwrites.
- **Adoption.** Discovery, preview and adoption read a bucket through the same
  steps as a directory. The adopted set is a staging set, not direct-ship,
  because a run never writes through a store. Its staging archive is seeded
  with what a trimmed one keeps: the metadata, and the data the newest backup's
  files are read from. So the next backup sends only what changed, and an older
  snapshot's data is read from the bucket when it is restored
  (`Hosts.Tests/S3AdoptionTests`).

### 0092 — a batch at a time

Built. ADR-0090's measurement left one term over NFR-PERF-008's 20 requests
per GiB: the blob covers. A blob must be named by a durable intent before it
is put (08 §3.1), and each blob had been named by a journal extension of its
own, so every blob cost two requests.

A blob's identity is the writer's number, so it can be named before the blob
exists. A publication now reserves numbers in batches and names each batch
once: its write intent names the first eight, and when they run out one
extension names the next batch, twice the last up to 64. The extension is
durable before the first blob numbered from it is put, and uploads running
together wait for it. A compaction pass seals everything before it puts
anything, so it names its whole output in one extension.

The cost was accounting. A number named and never used is in no blob, and
ADR-0022 §Decision 7's four cases could not account for it, so every next run
would have voided it. A fifth case closes that: a Completed retirement
accounts for every number its intent and the intent's extensions named, used
or not. A completed backup therefore owes nothing. One that dies owes what it
reserved and did not upload, at most the rest of one batch beyond the blobs it
had in flight, and the next run voids those as it voids any leftover.

| Backup | Requests per GiB | Blob covers | Everything else |
|---|---|---|---|
| First backup, ~490 KB a file | 15 | 1 | 14 |
| First backup, 16 KiB a file | 16 | 1 | 15 |
| Incremental, ~490 KB a file | 17 | 1 | 16 |
| Incremental, 16 KiB a file | 18 | 1 | 17 |

`Repository.Tests/UploadBudgetTests` now holds each whole total to 20, so
NFR-PERF-008 is met. `Repository.Tests/IntentReservationTests` holds the
order blob by blob, the doubling, what a completed backup owes and what a
killed one owes. No format changed: an intent always named blobs that did not
exist yet.

### 0093 — a container as a destination

Built. `azure-blob` was the second kind the configuration accepted and the
runtime refused (FR-DEST-005), and it is now served by every routine that
serves a bucket. With it, both halves of FR-REP-002 are built.

**Still no package.** The provider speaks the Blob API over the platform's
HTTP client and signs requests with the API's Shared Key scheme itself. The
signer is pinned to six vectors the API's reference client library computed,
given exactly the headers the provider sends. `Storage.AzureBlob` is the
second entry on the HTTP-client allowlist in
`ArchitectureTests/TelemetrySilenceTests`, held to the first entry's closure
and single composer. A default run still makes no HTTP request.

**Two credentials, because they are not the same thing.** The account key
opens every container in the account and never lapses. A shared access
signature is issued for one container, with the permissions and the lifetime
the person who issued it chose. Either is sealed to the service where it was
typed and held where an access key is. A signature's expiry is read from the
token itself: one already past is refused when it is stored, the listing says
when one lapses, and once it has, the next sync fails saying so and sends
nothing under it. What is not built is a warning ahead of the lapse.

**One check, not two copies.** Every place ADR-0091 taught to serve a bucket
asked for the `s3` kind by name. They now ask whether a destination is an
object store, so a container is synced, read back, restored from, drilled,
swept, repaired, probed, trimmed against and adopted from by the same code.
The host suites were made one as well:
`Hosts.Tests/ObjectStoreDestinationTests` and
`Hosts.Tests/ObjectStoreAdoptionTests` run every case once against a bucket
and once against a container, so the two cannot drift apart unnoticed.

**Proven against a real store, not only the one written here.** The in-process
`TestSupport/AzureBlobTestServer` checks every signature with a verifier of
its own. The contract suite also passed, under both credentials, against a
Blob API emulator run locally while this was built, and passes against any
container named in `FALLBACKPLAN_AZURE_TEST_*`
(`TestSupport/ConfiguredAzureBlobStore`).


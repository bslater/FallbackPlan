# Implementation status

**Status:** maintained · **Checked by:** [`eng/check-adr-status.py`](../eng/check-adr-status.py)

---

Forty-seven decision records say what this system should do. This says which of them the code actually does, and — where the answer is "some of it" — which part.

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
| [0008](adr/0008-index-generations-and-checkpoints.md) | Index generations, deltas, checkpoints | **Built** | `Repository.Index/CheckpointCodec`, `Repository.Index/IndexDeltaCodec`, `Repository.Index/WriterSequence`, `Repository.Index/ObservedHead` — the watermarks the decision put in checkpoints are now also read back as a rollback witness, so a writer whose allocation state fell behind its own published history adopts the repository's head at archive open (`Hosts.Tests/ObservedHeadAdoptionTests`) |
| [0009](adr/0009-garbage-collection-safety.md) | Garbage collection safety | **Partly built** | `Repository.Index/Journal/IntentLifecycle`, `Retention/StagingSweep` · `Retention.Tests/RetentionCycleTests` · [notes](#0009--the-collector-is-built-compaction-is-not) |
| [0010](adr/0010-local-store-separation.md) | Local store separation | **Built** | `Application/LocalState` · `Repository.Tests/EndToEnd/LocalStateSeparationTests` |
| [0011](adr/0011-commit-versus-replication-semantics.md) | Commit versus replication semantics | **Built** | `Application/DestinationSyncStore` (the per-replica half), `Repository/SnapshotPublication` (the commit half) · [notes](#0011-0018--commit-is-per-replica-and-there-are-now-many-replicas) |
| [0012](adr/0012-storage-provider-contract.md) | Storage provider contract | **Partly built** | `Storage.Abstractions`, `Storage.Local` · `Storage.ContractTests` · [notes](#0012--the-contract-is-real-it-has-one-provider) |
| [0013](adr/0013-recovery-kit.md) | Recovery kit contents and format | **Applied** | Superseded by [ADR-0060](adr/0060-the-passphrase-is-the-recovery-credential.md): no kit exists to be built. What the record set in motion and still stands is the standalone tool, `Recovery/RecoverySession`, which now opens from the passphrase and the descriptor; [notes](#0060--the-passphrase-is-the-recovery-credential) |
| [0014](adr/0014-format-versioning-and-stability.md) | Format versioning and pre-1.0 posture; format 1 withdrawn before freeze (Amendment 1) | **Built** | `Domain/FormatLimits` · `Repository.Format/Descriptor/RepositoryDescriptorCodec` · `Repository/RepositoryLifecycle` · `Repository.Tests/EndToEnd/RepositoryLifecycleTests`, `Repository.Tests/Format/RepositoryDescriptorCodecTests` · [notes](#0014--one-format-and-a-refusal-by-name) |
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
| [0028](adr/0028-service-boundary-and-deployment-topologies.md) | The service boundary | **Partly built** | `FallbackPlan.Api`, `Cli/OperationGateway` · [ADR §Implementation status](adr/0028-service-boundary-and-deployment-topologies.md#implementation-status-2026-08) · `Hosts.Tests/ClientModeTests` — §3's rule holds for writes as well as reads: a backup naming a set and no repository is run by the local service, and a missing one is refused naming both ways forward |
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
| [0042](adr/0042-write-only-repositories.md) | Write-only repositories (format v2) — since 2026-09 the only format | Built | `Repository.Crypto/WriteOnlyDerivation` · `Repository.Crypto/RepositoryWriteCredential` · `Repository.Packing/SealedContentKey` · `Repository/RepositoryLifecycle` · `Agent/WriteOnlyServiceState` · [notes](#0042--the-hub-that-cannot-read-what-it-keeps) |
| [0043](adr/0043-structured-logging-and-diagnostics.md) | Structured logging and client diagnostics | Built | `Diagnostics/LogRing`, `Diagnostics/RollingFileSink`, `Diagnostics/LoggingComposition`, `Domain/Diagnostics/LogLevels`, `Agent/Log.cs` (and one per project), `Application/ClientConfiguration` (schema 4) · `Diagnostics.Tests`, `Application.Tests/LoggingConfigurationTests`, `ArchitectureTests/LoggingShapeTests`, `Repository.Tests/LogPrivacyTests`, `Repository.Tests/EnginePlaneLoggingTests`, `Replication.Tests/CopierLoggingTests` · [notes](#0043--the-engine-logs-a-client-reads-it-and-every-declared-message-is-emitted) |
| [0044](adr/0044-first-run-setup.md) | First-run setup and the installation passphrase | Built | `Domain/Configuration/PassphraseStrength` · `Agent/WriteOnlyServiceState` · `Agent/ServiceCommandHandler.Setup.cs` · `Web/ConsoleRestoreGate` · [notes](#0044--the-ceremony-that-two-requirements-have-been-waiting-for). The ceremony ends at the passphrase and the first account: the recovery-kit step, its confirmation and the public-parameters record that let a kit be rebuilt are withdrawn with the kit (ADR-0060), and the installation's public derivation parameters ride the describe verb (contract 1.28) from `Agent/ServiceCommandHandler` instead |
| [0045](adr/0045-client-authentication.md) | Client authentication: username, password, session | Built | `Repository.Crypto/PasswordHash` · `Agent/UserStore` · `Agent/SessionRegistry` · `Agent/AuthenticatingService` · `Cli/SessionCache` · `Repository.Tests/PasswordHashTests`, `Hosts.Tests/UserStoreTests`, `Hosts.Tests/AuthenticationGateTests`, `Hosts.Tests/UnattendedWorkTests`, `Cli.Tests/SessionVerbTests`, `Web.Tests/SessionRelayTests` · [notes](#0045--the-product-can-say-who-is-acting) |
| [0046](adr/0046-direct-to-destination-publication.md) | Direct-to-destination publication: the ship sink, no staging archive | **Partly built** | `Agent/DestinationShipSink` · `Agent/ArchiveHandle` · `Agent/ServiceRuntime` · `Agent/BackupRunner` · `Hosts.Tests/DirectShipTests`, `Hosts.Tests/DirectShipMigrationTests` — the write path, run scoping, sibling catch-up, the no-destination refusal, the destination-backed read paths, and the migration (flip, seed, retire_staging); the direct-ship is the default for new local-path sets (contract 1.23, retention drill run through `Hosts.Tests/DirectShipRetentionTests`); Amendment 1's converge spare keeps a narrow sibling from trimming the last copy of history a wide destination is still owed (`Retention.Tests/DestinationConvergenceTests`, `Hosts.Tests/DirectShipConvergeSpareTests`); the peer write adapter is the remaining tail. The hardened round (`Hosts.Tests/DirectShipFaultSweepTests`) runs the 04 §5.1 kill matrix through a two-destination sink and pins behind-exclusion, per-destination seeding drops, the capacity floor, and seed-recorded-as-behind. `Web/ConsoleRestoreGate` resolves repositories through one root list for all three of its ceremonies, so the recovery-kit rebuild and write-only adoption see a direct-ship set's metadata store as the restore gate already did (`Web.Tests/FirstRunSetupTests`, `Web.Tests/WriteOnlyCeremonyTests`), and a direct-ship restore source is named rather than called staging |
| [0047](adr/0047-backup-pool-and-priorities.md) | The backup pool: concurrency, priorities, a pass that never waits for transfers — preemption (Amendments 1–2): a higher-priority arrival suspends the lowest-ranked running backup at a file boundary and resumes it when a slot frees, escalating past an unresponsive victim, with generation-stamped expiry and pause/resume on the progress stream — and one run per set enforced atomically at the enqueue for every trigger door (Amendment 3) | Built | `Agent/JobScheduler` · `Agent/Scheduler` · `Agent/PauseGate` · `Domain/Jobs/IPauseGate` · `Application/ClientConfiguration` (schema 5) · `Application/DestinationSyncStore` (schema 2) · `Hosts.Tests/JobSchedulerPoolTests`, `Hosts.Tests/SchedulerStarvationTests`, `Hosts.Tests/PreemptionTests`, `Hosts.Tests/JobSchedulerPreemptionTests`, `Hosts.Tests/StatusBaselineTests` (contract 1.19's full-backup facts on the status matrix), `Hosts.Tests/ConfigurationCommandTests`, `Hosts.Tests/BackupConcurrencyTests`, `Application.Tests/DestinationSyncStoreTests`  The ledger also carries how much of what each destination is owed it holds (`Application/DestinationSyncStore`, contract 1.24): counted by the sync pass, which lists both sides anyway, rather than by the status read, and reported as uncounted rather than zero where no pass has reached a destination (`Hosts.Tests/DestinationCompletenessTests`) |
| [0048](adr/0048-determinate-backup-progress.md) | Determinate backup progress: a backup counts its work before archiving, the plan rides every report (contract 1.20), the hub replays each live job's latest snapshot to a new subscriber, a hung-up watcher is reaped at once, and the console divides by the plan with a time estimate on the jobs page and overview | Built | `Repository/PublicationOrchestrator` (the counting pass and coalesced incremental reporting) · `Domain/Jobs/JobProgress` · `Agent/ProgressHub` · `Api/Transport/ServiceConnectionPump` · `Repository.Tests/SnapshotPublicationTests`, `Hosts.Tests/ProgressHubTests`, `Api.Tests/AbandonedCommandTests`, `Web.Tests/ConsoleProgressScriptTests`, `Web.Tests/EventStreamTests` |
| [0049](adr/0049-service-lifecycle-hygiene.md) | Service lifecycle hygiene: the journal reconciled at start with a notice, cancel settling a run the queue no longer knows, deletion deferring only to queue-active runs, the Owner-only in-process `restart_service` (contract 1.21) on the console and CLI, and the startup configuration record with provenance | Built | `Application/JobStateStore` · `Agent/ServiceRuntime` · `Agent/AgentHost` (the recycle loop and events 3760–3763) · `Agent/AuthenticatingService` · `Hosts.Tests/JournalReconciliationTests`, `Hosts.Tests/RestartServiceTests`, `Hosts.Tests/AgentServiceLifetimeTests`, `Hosts.Tests/AgentHostTests`, `Web.Tests/ConsoleAdminScriptTests` |
| [0050](adr/0050-completed-run-record-and-drill-down.md) | The completed-run record and drill-down: terminal numbers persisted on every journal row, the run diff (`job_changes`) and failure listing (`job_failures`) read from the repository on demand (contract 1.22), the bounded `list_jobs`, every behind demotion carrying its cause with the compared operand on the wire, the live feed naming the file being processed, and the error-manifest decoder brought to specification 06 §8.1 | Built | `Application/JobStateStore` · `Agent/BackupRunner` · `Agent/ServiceCommandHandler` · `Application/StatusModel` · `Repository/SnapshotPublication` · `Repository.Format/Manifests/PolicyManifest.cs` · `Hosts.Tests/JobDrilldownTests`, `Application.Tests/JobRunRecordTests`, `Application.Tests/DestinationStatusTests`, `Api.Tests/ContractAdditiveFieldsTests`, `Web.Tests/ConsoleJobsScriptTests`, `Cli.Tests/JobsVerbTests` |
| [0051](adr/0051-local-destination-placement.md) | A local destination lives on its own drive: drive separation as the condition of choosing (volume hard, physical drive where the platform can say), and the protection boundary moved from machine to volume — a second drive earns `protected` with its residue named | Built | `Application/LocalDestinationPlacement` · `Filesystem.Local/PhysicalDisk` · `Agent/ServiceCommandHandler` · `Application/StatusModel` · `Application.Tests/LocalDestinationPlacementTests`, `Hosts.Tests/LocalPlacementTests` |
| [0052](adr/0052-relocatable-records-format-v3.md) | Format v3: a sealed record stops encoding where it lives | **Specified only** | [notes](#0052--nothing-writes-v3-and-that-is-the-point) |
| [0053](adr/0053-peer-claim-and-configuration-recovery.md) | Peer replica claim, and the set's shape in the kit | **Partly built** | `Protocol/PeerReplicationMessages`, `Agent/ClaimResponder`, `Cli/CliApplication`, `Application/ReplicaOwnerStore`, `Repository.Crypto/WriteOnlyDerivation` — the two-phase ceremony and the key; §3's operator re-attribution is not built and §4 will not be; [notes](#0053--the-claim-is-built-the-shape-is-not) |
| [0054](adr/0054-scheduled-restore-drills.md) | Recovery drilled on a cadence: a sampled file restored out of each local destination's own replica, recorded per pair with its age and its reason, three states kept apart on the wire (contract 1.25) and in the console, a failure raising a notice rather than blaming the copy, and (Amendment 1) an interrupted drill recording nothing at all | Built | `Agent/RecoveryDrillJob` · `Agent/Scheduler` · `Application/DestinationSyncStore` · `Api/Results.cs` · `Hosts.Tests/RecoveryDrillTests`, `Api.Tests/ContractAdditiveFieldsTests`, `Web.Tests/ConsoleDestinationCardTests`; [notes](#0054--what-the-scheduled-drill-does-not-prove) |
| [0055](adr/0055-reclaim-authority.md) | Reclaim authority: tombstones signed under their own derivation domain, withheld from a write-only service's write credential, announced by a required repository feature, granted for one collection run at a time, and carried to a keyless peer as a published public key its retention instructions are signed against | Built | `Repository.Crypto/RepositoryWriteCredential` · `Repository.Crypto/WriteOnlyDerivation` · `Repository.Crypto/ReclaimAuthority` · `Repository.Crypto/RepositoryWriteCredential` · `Repository.Format/Descriptor/RepositoryDescriptorCodec.cs` · `Retention/StagingSweep` · `Agent/ServiceCommandHandler.WriteOnly.cs` · `Protocol/PeerReplicationMessages.cs` · `Application/ReplicaOwnerStore` · `Repository.Tests/ReclaimAuthorityTests`, `Retention.Tests/ReclaimAuthoritySweepTests`, `Retention.Tests/PeerRetentionTests`, `Hosts.Tests/WriteOnlySetTests`, `Protocol.Tests/ReplicationMessageTests`, `Application.Tests/ReplicaOwnerStoreTests`; [notes](#0055--what-the-split-defends-and-what-it-does-not) |
| [0056](adr/0056-incremental-reconciliation.md) | A replication pass costs what changed: each dependency phase listed under its own prefix, a gate that skips a pair the last pass left level, a reading-through that comes due on its own cadence, and the publication sequence recorded by the run that shipped it | Built | `Replication/StoreToStoreCopier` · `Application/ReconciliationGate` · `Application/DestinationSyncStore` · `Agent/DestinationShipSink` · `Agent/FanOut` · `Retention/DestinationConvergence` · `Replication.Tests/CopierListingCostTests`, `Application.Tests/ReconciliationGateTests`, `Hosts.Tests/IncrementalSyncTests`; [notes](#0056--what-a-skip-claims-and-what-checks-it) |
| [0057](adr/0057-resumable-object-transfer.md) | A peer transfer cut inside an object resumes: the destination declares what it part holds with a digest of exactly those bytes, the source verifies that claim against its own copy before skipping anything, and the staged prefix is keyed, quota-counted and swept | Built | `Protocol/PeerReplicationMessages.cs` · `Protocol/PeerSessionNegotiation` · `Agent/PartialSpool` · `Agent/ReplicationResponder` · `Agent/ReplicationInitiator` · `Hosts.Tests/PeerResumeTests`, `Protocol.Tests/ReplicationMessageTests`; [notes](#0057--what-resuming-trusts) |
| [0058](adr/0058-peer-write-adapter.md) | A direct-ship set ships to a peer over one replication session held open for the run: the inventory answers what is already there, the acknowledged count must equal what was sent, reads travel a lazily dialled retrieval session, a set with no independent copy of its content is proved by reading the replica back instead, and a peer-only set still defaults to staging for reasons the record names | Built | `Agent/PeerShipStore` · `Agent/DestinationShipSink` · `Agent/BackupRunner` · `Agent/FanOut` · `Agent/ServiceCommandHandler` · `Replication/ReplicaVerifier` · `Hosts.Tests/DirectShipPeerTests`, `Hosts.Tests/DirectShipTests`, `Hosts.Tests/PeerReadBackVerificationTests`; [notes](#0058--what-the-adapter-does-not-carry) |
| [0059](adr/0059-session-bound-deletion-authority.md) | A retention instruction is signed over the session it is sent in, and the requirement to sign is gated on the reclaim key the spoke recorded rather than on a feature the sender chooses to offer | Built | `Protocol/SessionBinding` · `Protocol/PeerAuthenticator` · `Protocol/PeerSessionDriver` · `Protocol/PeerReplicationMessages.cs` · `Protocol/PeerSessionNegotiation` · `Agent/ReplicationResponder` · `Agent/RemoteServiceListener` · `Agent/FanOut` · `Hosts.Tests/PeerRetentionReplayTests`, `Protocol.Tests/PeerWireTests`, `Protocol.Tests/ReplicationMessageTests`; [notes](#0059--the-hole-under-the-hole) |
| [0060](adr/0060-the-passphrase-is-the-recovery-credential.md) | The passphrase is the recovery credential: the recovery kit withdrawn, the recovery tool opening from the passphrase and the archive's own descriptor, first-run setup ending at the passphrase and the first account, contract 1.29 | Built | `Recovery/RecoverySession` · `Recovery/RecoveryHost` · `Repository.Crypto/WriteOnlyDerivation` · `Agent/AgentHost` · `Agent/ServiceRuntime` · `Web/ConsoleRestoreGate` · `Api/ContractVersion` · `Hosts.Tests/RecoveryHostTests`, `Repository.Tests/PassphraseDrillTests`, `Hosts.Tests/FirstRunSetupTests`, `Web.Tests/SetupWizardScriptTests` · [notes](#0060--the-passphrase-is-the-recovery-credential) |

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

The collector now exists and reclaims space (`FallbackPlan.Retention`): `StagingMark` walks the protected closure, `StagingSweep` runs the signed-tombstone → grace-by-publication → revalidate → delete cycle, and `StagingTrim` drops historic data blobs every entitled destination verifiably holds — see [0034](#0034--the-hub-fans-out-ages-and-trims) for the whole engine. Every deletion honours the intent survey, so the safety machinery finally protects against a process that runs. What remains of this decision is **compaction** (architecture 07 steps 6–9): partially-live blobs are kept whole and reported as the stated backlog, and nothing re-packs them — that is still phase 4. For direct-ship sets ([ADR-0046](adr/0046-direct-to-destination-publication.md)) the retention traversal reads through the ship sink and per-destination convergence is the deleting half; the staging trim applies only while a staging archive exists.

### 0011, 0018 — commit is per-replica, and there are now many replicas

The decision that a snapshot commits per destination rather than globally is in the publication model, and everything that makes it *matter* has since arrived with the hub-and-spoke arc: a set declares several destinations, the sync ledger (`Application/DestinationSyncStore`) carries per-`(set, destination)` state, and failure domains are compared by device identity (`Application/StatusModel`) rather than assumed — the PT-8 placeholder replaced. `Protected` is earned only by an in-sync destination outside the source's failure domain, which is ADR-0018's rule in force. Direct-ship sets ([ADR-0046](adr/0046-direct-to-destination-publication.md)) sharpen the same rule: each destination is a whole repository from its first byte, and the run-scope rules refuse to hand a destination a snapshot without its closure. What has no dedicated test yet is `FR-SNP-007`'s full five-state per-destination snapshot lifecycle; the ledger's coarser states stand in for it and the traceability matrix says so.

### 0012 — the contract is real; it has one provider

`Storage.Abstractions` defines the contract, `Storage.ContractTests` is a reusable suite any provider must pass, and `Storage.Local` passes it. This is the shape the decision asked for, and the shape is what protects the design.

It is still one provider. A contract with a single implementation has not yet been tested by the thing it exists for — the second implementation that disagrees with it. Azure and S3 are phase 3, and `NFR-PORT-002` is traced against the architecture tests and the contract suite rather than against a provider that proves portability by being different.


### 0014 — one format, and a refusal by name

Format 1 was withdrawn before any freeze ([ADR-0014 Amendment 1](adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)), with no installed base
to migrate: the one live installation went through setup and only ever wrote
format 2. `Domain/FormatLimits` names one format version, and
`Repository.Format/Descriptor/RepositoryDescriptorCodec` refuses a descriptor
stamped `1` as its own finding — *refuse, never misread* — naming re-seeding as
the remedy; `Repository.Tests/EndToEnd/RepositoryLifecycleTests` and
`Repository.Tests/Format/RepositoryDescriptorCodecTests` hold both halves.
The freeze gate's items are unchanged and read against format 2.

One fact is recorded because it looks like an error until it is explained:
format 2's symmetric containers — metadata blobs and standalone records —
still stamp `1` in their envelopes and associated data. The symmetric
construction is the one format 1 defined and format 2 kept byte for byte, and
the stamp is authenticated data over bytes already on disk, so
`Domain/FormatLimits` carries it as `SymmetricFormatVersion` beside the format
version proper. The committed `fixture-repository-v2` is the guard: change
either and it stops opening.

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

**The retention engine is built and proven against real archives** (`FallbackPlan.Retention`, the architecture 11 placeholder activated). `RetentionPlanner` selects with stated reasons — an absent rule keeps everything, `min_generations` is the floor the other rules cannot override (FR-GC-001). `ReplicationGate` holds any policy-expired snapshot a configured destination has not received, comparing publication sequences to the sequence each sync recorded at its start (`destinations.json` carries it) — never a clock — and a laggard beyond `deferral_days` turns the quiet hold into a warning (FR-GC-009). `StagingMark` surveys the store's own snapshot objects and walks the protected closure; `CollectionPlanner` produces the mandatory dry-run report, treats every intent-covered blob as reachable (FR-GC-003), keeps partially-live blobs whole as the stated compaction backlog, and lets any damage veto the entire pass. `StagingSweep` writes the signed tombstones of [specification 11 §3](../specifications/repository-format/11-lifecycle-objects.md#3-tombstone), waits out a grace counted in the writer's publication sequence ([ADR-0009 Amendment 5](adr/0009-garbage-collection-safety.md#amendment-5-2026-08--the-grace-generation-realised)), revalidates against a world read after the grace check, and only then makes the first production calls to `DeleteAsync`. `RetentionCycleTests` proves the cycle end to end: dry run deletes nothing, apply tombstones and still deletes nothing, the sweep after the next publication removes exactly the condemned snapshots, and the archive keeps walking clean and publishing. The surface is `fallbackplan-agent retention [--apply]` and the `RetentionCommand` on the service contract — dispatched on the writer lane so a pass serialises against backups, with a hold past its deferral bound raised as a durable notice — and a paired console commands the same pass remotely through the CLI `retention` verb.

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

**Every declaration is called, and that is now a build rule.** Phase 2 declared 107 `[LoggerMessage]` messages and wired 46 of them; the other 61 read exactly like messages the engine emits while emitting nothing. They were frozen in `ArchitectureTests/LoggingShapeTests` as a register that could only shrink, and it has since been emptied — twelve declarations deleted with their reasons recorded in ADR-0043, several more reshaped where the declared message promised a count nothing computes or named something the code does not do, and the rest wired with a logger threaded from the host to the call site. The register and the "or is a known debt" half of the test are gone with it: the rule is simply that every declaration is called. Because a declaration having a call site does not prove a logger reaches it, `Repository.Tests/EnginePlaneLoggingTests`, `Replication.Tests/CopierLoggingTests` and the retention drill assert by event id that records arrive through real publications, copies and passes.

### 0046 — the set that never stages

Everything the row names is held by tests, including the 04 §5.1 kill matrix through a two-destination sink (`Hosts.Tests/DirectShipFaultSweepTests`). The gate has since been discharged for local-path sets (ADR-0046 Decision 7's amendment): `direct_ship` rides the contract (1.23) and the console's set editor, a shape flip migrates in-process with its seed queued at once, the retention-with-trimming drill ran (`Hosts.Tests/DirectShipRetentionTests` — and caught the sink stopping sweep deletes at the metadata store, now fanned to the destinations under the replication gate's licence), and a **new set referencing a local-path destination is born direct-ship**. Verification was corrected in 2026-09 on two counts, both of which bit the default shape of a new local-path set. It never ran: challenges live in the sync path and `Agent/Scheduler` queues a pair only when there is something to copy, and a direct-ship set has nothing to copy the moment it converges — so a destination was challenged once and then never again. And what would have run proved nothing: the verifier compared the replica against `Agent/DestinationShipSink`, whose blob reads the destinations themselves answer, so with one destination it was a replica against itself. Challenges are now due on the age of the last proof, and a sampled blob is proved by being *opened* at the replica — its footer and a record's AEAD tag, evidence the destination never held the key to forge (`Hosts.Tests/DirectShipVerificationTests`). Retirement's gate was regated in 2026-09 (Amendment 2) after a live install could not use it: it demanded every non-lifecycle object staging held be present at a destination, and nothing carries an object no live snapshot reaches, so the archive was refused for ever and its disk space held. It now refuses only over a blob the live history reaches that no destination has, or a non-blob object the flip's migration never carried across, and names example keys instead of a bare count. What keeps the row at **Partly built** is one tail: the peer write adapter (a declared peer is a stated `NotSupported` ledger row for direct-ship; peer-only sets default to staging until it lands).

### 0052 — nothing writes v3, and that is the point

The record is a **design**, taken at the moment when taking it is cheap. Under
[ADR-0025](adr/0025-compaction-reseals-records.md) a record's key comes from
its blob, its nonce is its position in that blob, and its AAD binds that
position again — so a record cannot be moved without being opened, and
compaction is decrypt-and-reseal. ADR-0052 scopes the record key to the object
identifier, makes the nonce constant and drops the ordinal from the AAD, for
format v3 only.

**Nothing implements it and nothing should yet.** `Domain/FormatLimits`
carries version 2 (and the symmetric containers' stamp of 1, which is a fact
about bytes on disk rather than a second format — [ADR-0014 Amendment 1](adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)),
`Repository.Crypto/BlobKeyDeriver` still mixes the blob's salt, writer and
counter, and `Repository.Format/Records/RecordNonce` still writes the ordinal.
Format-2 repositories are read in place forever, so there is no migration
waiting to be run and no half-state to be in.

What makes the timing the argument: [0025](#0025--nothing-compacts-yet-so-nothing-re-seals-yet)
is *Specified only*, so nothing compacts, so reversing its decision costs a
format revision. After a compactor ships the same change costs a data
migration. The window closes on its own, which is why the record exists before
the code rather than alongside it.

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

What is **not** built: §3's operator re-attribution, for a replica attributed
before the claim key existed by a machine that then died — it self-heals on one
more offer from an updated source, and otherwise needs the destination's
operator. §4, the set's shape in the kit, **will not be**: there is no kit, the
set is re-declared after a rebuild, and add-destination-then-adopt is the named
follow-up.

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

Peer destinations are not drilled: restoring across the wire on a cadence the
peer never agreed to is peer-protocol work rather than a schedule, and the gap
is carried openly on [proof obligations](proof-obligations.md) rather than
implied by an absence.

A fourth answer was added by [Amendment 1](adr/0054-scheduled-restore-drills.md#amendment-1--an-interrupted-drill-is-not-a-failed-drill-2026-09),
and it is silence: a drill interrupted because the service is stopping records
nothing. It was written the other way first, and the cost of that was a false
"a restore drill could not bring back a file" notice every time a drill was in
flight at shutdown — the loudest thing the product says, about the most
ordinary thing it does. `Agent/RecoveryDrillJob` now translates a cancelled
command answer back into the cancellation it was, `Agent/AgentPass` waits for
the drill phase before tearing the runtime down, and `Agent/JobScheduler`
refuses work once it has stopped rather than posting to disposed semaphores.

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
- **A destination keeps no signed record of what it deleted**, only the count
  it acknowledges. The signed audit record exists on the repository plane — the
  tombstone — and not on the spoke's side of the instruction.

The headless operator has the same grant the console sends, without building
it by hand: `fallbackplan-agent retention --apply --passphrase-env <VAR>` on a
set-up installation re-derives the reclaim sub-root from the passphrase under
the installation's own salt, proves the passphrase against the stored
credential before anything is authored — a fresh archive holds no tombstone
for the sweep's own proof to disagree with — seals it to the service's
recipient key and sends it with the command (`Agent/AgentHost`,
`Hosts.Tests/RetentionTrimVerbTests`). Without the passphrase, `--apply` is
refused naming what it needs; a dry run needs nothing.

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
| [0 — Archive engine](roadmap.md#phase-0--archive-engine-vertical-slice) | Complete; every exit criterion traced to a named test — with one stated qualifier: the compaction criterion is discharged by its preconditions (nothing physical decodes; supersession converges), the compactor itself being phase 4 |
| [1 — Snapshot and local repository](roadmap.md#phase-1--snapshot-and-local-repository-mvp) | Complete, both pushes |
| [2 — Peer-to-peer and the service boundary](roadmap.md#phase-2--peer-to-peer-backup-and-the-service-boundary) | Complete except deferred-not-planned items (LAN discovery, relay, bandwidth schedules, multi-instance console, Q18/Q19): service boundary on both bindings, peer protocol over a real socket, replication with recovery drill, roles/termination/quotas/retention via the hub-and-spoke arc, and destination verification (spec 04) with `verified` earned from read-back and the four-value failure domains (FR-SNP-007). The web UI, deferred at the phase close, has since landed as the local web console ([ADR-0036](adr/0036-local-web-console.md)) |
| [Hub-and-spoke arc](roadmap.md#the-hub-and-spoke-arc--multi-destination-backup-sets-built) | Built ([ADR-0034](adr/0034-hub-and-spoke-destinations.md)): configuration schema v2, per-set staging archives, local-path and peer fan-out, the status matrix, termination notices, quota enforcement, retention against staging, local-path and peer destinations, the staging trim, and the `sync`/`retention` operator verbs — see [0034](#0034--the-hub-fans-out-ages-and-trims). For a `direct_ship` set the staging half of this arc is replaced by the direct-to-destination row below |
| Direct-to-destination arc | Partly built ([ADR-0046](adr/0046-direct-to-destination-publication.md), [ADR-0047](adr/0047-backup-pool-and-priorities.md)): the ship sink and metadata store, destination-backed restore/verify/retention reads, migration and staging retirement, the pool with priorities and true suspend/resume, and the kill sweep — the trimming drill run, the flag on the contract and console, and new local-path sets born direct-ship — the peer write adapter is the one remaining tail; see [0046](#0046--the-set-that-never-stages) |
| 3 — Cloud object stores | Not started; reframed as destination kinds behind the arc's fan-out |
| 4 — Retention, GC, compaction | Retention pulled forward into the hub-and-spoke arc; compaction and healing remain here — see [0025](#0025--nothing-compacts-yet-so-nothing-re-seals-yet) |
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

**It does not verify.** A challenge is answered by the peer and judged against
bytes this side reads for itself, and a set shipping only to a peer has none —
the sink can offer the metadata plane it keeps locally and no content at all.
Sampling that population would prove nine small objects and stamp the pair
verified, which is the emptiness the verification-independence work already
found once. So the pass challenges nothing, stamps nothing, and raises a
durable notice. A second destination closes it today; closing it for a single
peer wants the record-tag proof reaching through the retrieval session, and
that is not built.

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

**What is still not met.** `FR-GC-008` also promises signed audit records, and
a destination keeps no signed record of what it deleted, only the count it
acknowledges. A receipt would be signed under the destination's own device key,
since it holds no repository keys — a different artefact with its own lifetime.
It stays in the requirement as not met.

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

**What is not built, and is named in the record:** the flow that would let a
rebuilt machine be pointed at an existing destination and adopt what it finds
there under the original repository ids. Until it exists, a person who has
forgotten where their backups are holds a passphrase that opens nothing they
can find.

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

What the checker cannot do is judge whether "built" is generous. That is a reading, and it is repeated whenever a phase closes. It also deliberately does not compare these states against each ADR's `Status:` line: that line records whether a *decision* was accepted, which is a different question from whether the code does it, and collapsing the two would lose both.

---

**See also:** [Abandoned choices](decisions-abandoned.md) — what was considered and rejected, and why · [Traceability](requirements/traceability.md) — requirements to tests · [Roadmap](roadmap.md)

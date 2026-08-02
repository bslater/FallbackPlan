# Traceability matrix

**Status:** draft · **Resolves:** [M5](../review/2026-08-architecture-review.md#m5--most-requirements-are-not-testable-as-written-and-the-promised-traceability-does-not-exist)

---

The original proposal opened §3.4 by stating that requirement IDs existed "so they can be traced into architecture decisions, implementation work items, and tests". No such mapping was present. This is it.

Every requirement maps to at least one architecture section. Requirements arising from a contested decision also name an ADR. Test classes are planned names — they do not exist yet, and the mapping is what will tell us when one is missing.

**Legend:** *Arch* = architecture section · *ADR* = decision record · *Test* = planned test project and class · *Phase* = [roadmap](../roadmap.md) phase that must satisfy it.

---

## Functional

### Archive engine

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-ARCH-001 | [02 §3](../architecture/02-repository-format.md#3-segmentation) | [0002](../adr/0002-segmentation-strategy.md) | `Repository.Tests/SegmentationProfileTests` | 0 |
| FR-ARCH-002 | [02 §3.4](../architecture/02-repository-format.md#34-capture-algorithm) | — | `PerformanceTests/BoundedMemoryTests` | 0 |
| FR-ARCH-003 | [03 §4](../architecture/03-crypto.md#4-object-identifiers) | [0004](../adr/0004-segment-hash-function.md) | `Repository.Tests/ContentIdentifierTests` | 0 |
| FR-ARCH-004 | [02 §3.4](../architecture/02-repository-format.md#34-capture-algorithm) | — | `Repository.Tests/SegmentReuseTests` | 0 |
| FR-ARCH-005 | [02 §4](../architecture/02-repository-format.md#4-compression) | — | `Repository.Tests/CompressionPolicyTests` | 0 |
| FR-ARCH-006 | [02 §5.1](../architecture/02-repository-format.md#51-purpose-and-sizing) | — | `Repository.Tests/CrossBlobFileTests` | 0 |
| FR-ARCH-007 | [02 §5.1](../architecture/02-repository-format.md#51-purpose-and-sizing) | — | `Repository.Tests/WriteProfileValidationTests` | 0 |
| FR-ARCH-008 | [03 §2](../architecture/03-crypto.md#2-key-hierarchy) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `Repository.Tests/EncryptionProfileTests` | 0 |
| FR-ARCH-009 | [02 §5.2](../architecture/02-repository-format.md#52-layout) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `Repository.FuzzTests/RecordCorruptionTests` | 0 |
| FR-ARCH-010 | [02 §6.2](../architecture/02-repository-format.md#62-manifests-hold-logical-facts-only) | [0007](../adr/0007-logical-object-identifiers-in-manifests.md) | `Repository.ConformanceTests/ManifestShapeTests` | 0 |
| FR-ARCH-011 | [02 §5.3](../architecture/02-repository-format.md#53-spooling-and-sealing) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `InterruptionTests/BlobSpoolResumeTests` | 0 |
| FR-ARCH-012 | [02 §5.2](../architecture/02-repository-format.md#52-layout) | — | `Repository.Tests/BlobFooterTests` | 0 |
| FR-ARCH-013 | [02 §3.5](../architecture/02-repository-format.md#35-configuration-envelope) | — | `Repository.Tests/EdgeCaseFileTests` | 0 |
| FR-ARCH-014 | [02 §3.1](../architecture/02-repository-format.md#31-profiles), [§3.3](../architecture/02-repository-format.md#33-the-freeze-gate) | [0002](../adr/0002-segmentation-strategy.md) | `Repository.ConformanceTests/ProfileFixtureTests` | 0 → freeze |

### Manifests, indexes, catalogue

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-MAN-001 | [02 §6](../architecture/02-repository-format.md#6-manifests) | — | `EndToEndTests/RestoreWithoutCatalogueTests` | 0 |
| FR-MAN-002 | [02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild) | [0010](../adr/0010-local-store-separation.md) | `Repository.Tests/CatalogueRebuildTests` | 0 |
| FR-MAN-003 | [02 §6.1](../architecture/02-repository-format.md#61-immutable-metadata-objects) | [0007](../adr/0007-logical-object-identifiers-in-manifests.md) | `Repository.ConformanceTests/FileVersionManifestTests` | 0 |
| FR-MAN-004 | [02 §6.3](../architecture/02-repository-format.md#63-sharding-and-encoding) | [0003](../adr/0003-canonical-metadata-encoding.md) | `Repository.Tests/TreeShardingTests` | 1 |
| FR-MAN-005 | [02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild) | — | `PerformanceTests/PathLookupLatencyTests` | 1 |
| FR-MAN-006 | [03 §5](../architecture/03-crypto.md#5-deduplication-trust-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `PerformanceTests/DedupLookupLatencyTests` | 0 |
| FR-MAN-007 | [02 §5.2](../architecture/02-repository-format.md#52-layout) | — | `Repository.Tests/RecoveryFooterTests` | 0 |
| FR-MAN-008 | [02 §7.2](../architecture/02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing) | [0008](../adr/0008-index-generations-and-checkpoints.md) | `Repository.Tests/IndexDeltaChainTests` | 0 |
| FR-MAN-013 | [02 §7.2](../architecture/02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing) | [0008](../adr/0008-index-generations-and-checkpoints.md) | `Repository.Tests/CheckpointMergeTests` | 0 |
| FR-MAN-009 | [02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild) | — | `Repository.Tests/ForensicRebuildTests` | 0 |
| FR-MAN-010 | [02 §8.2](../architecture/02-repository-format.md#82-forensic-rebuild) | — | `IntegrationTests/PartialRebuildRestoreTests` | 1 |
| FR-MAN-011 | [02 §8.2](../architecture/02-repository-format.md#82-forensic-rebuild) | — | `Repository.FuzzTests/RebuildValidationTests` | 0 |
| FR-MAN-012 | [02 §8.3](../architecture/02-repository-format.md#83-rebuild-never-repairs) | — | `Repository.Tests/DamageReportTests` | 1 |
| FR-MAN-014 | [02 §8.3](../architecture/02-repository-format.md#83-rebuild-never-repairs) | — | `Repository.Tests/RebuildImmutabilityTests` | 0 |

### Deduplication trust

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-DED-001 | [03 §5.2](../architecture/03-crypto.md#52-the-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Repository.Tests/TrustDomainTests` | 2 |
| FR-DED-002 | [03 §5.2](../architecture/03-crypto.md#52-the-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Repository.Tests/TrustDomainDefaultTests` | 0 |
| FR-DED-003 | [03 §5.2](../architecture/03-crypto.md#52-the-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Repository.Tests/VerifyOnReuseTests` | 2 |
| FR-DED-004 | [03 §5.2](../architecture/03-crypto.md#52-the-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Repository.Tests/TrustDomainAcknowledgementTests` | 2 |

### Snapshots

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-SNP-001 | [04 §6](../architecture/04-concurrency-and-publication.md#6-commit-versus-replication) | [0011](../adr/0011-commit-versus-replication-semantics.md) | `IntegrationTests/CommitSemanticsTests` | 1 |
| FR-SNP-002 | [01 §5](../architecture/01-domain-model.md#5-replication-not-synchronisation) | — | `IntegrationTests/DeletionHistoryTests` | 1 |
| FR-SNP-003 | [04 §6.1](../architecture/04-concurrency-and-publication.md#61-the-distinction) | [0011](../adr/0011-commit-versus-replication-semantics.md) | `Repository.Tests/ReplicationStateTests` | 2 |
| FR-SNP-004 | [04 §4](../architecture/04-concurrency-and-publication.md#4-write-intent) | [0009](../adr/0009-garbage-collection-safety.md) | `InterruptionTests/WriteIntentTests` | 0 |

### Restore and recovery kit

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-RST-001 | [08 §1](../architecture/08-restore-and-recovery.md#1-restore-paths) | — | `EndToEndTests/RestoreSelectorTests` | 1 |
| FR-RST-002 | [08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) | — | `Repository.Tests/RestoreVerificationTests` | 0 |
| FR-RST-003 | [08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning) | — | `Restore.Tests/RestorePlanTests` | 1 |
| FR-RST-004 | [08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) | — | `Restore.Tests/RestoreReceiptTests` | 1 |
| FR-RST-005 | [08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) | — | `Restore.Tests/PartialFailureTests` | 1 |
| FR-RST-006 | [08 §3.1](../architecture/08-restore-and-recovery.md#31-quarantine-by-default) | — | `Restore.Tests/QuarantineDefaultTests` | 1 |
| FR-KIT-001..006 | [08 §4](../architecture/08-restore-and-recovery.md#4-recovery-kit) | [0013](../adr/0013-recovery-kit.md) | `Repository.ConformanceTests/RecoveryKitTests`, `EndToEndTests/CleanMachineRecoveryTests` | 1 |

### Replication and verification

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-REP-001 | [09 §1](../architecture/09-replication-and-peers.md#1-what-replication-moves) | [0011](../adr/0011-commit-versus-replication-semantics.md) | `IntegrationTests/ReplicationTests` | 2 |
| FR-REP-002 | [05 §4](../architecture/05-storage-providers.md#4-providers) | [0012](../adr/0012-storage-provider-contract.md) | `Storage.ContractTests/*` | 3 |
| FR-REP-003 | [04 §5.1](../architecture/04-concurrency-and-publication.md#51-interruption-at-each-step) | — | `InterruptionTests/TransferResumeTests` | 2 |
| FR-REP-004 | [09 §2.1](../architecture/09-replication-and-peers.md#21-version-skew) | [0014](../adr/0014-format-versioning-and-stability.md) | `Protocol.Tests/VersionSkewTests` | 2 |
| FR-VER-001..005 | [09 §5](../architecture/09-replication-and-peers.md#5-destination-verification) | — | `Protocol.Tests/ChallengeResponseTests`, `IntegrationTests/VerificationSamplingTests` | 2 |

### Retention, GC, quotas

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-GC-001 | [07 §1](../architecture/07-retention-and-gc.md#1-retention-selects-collection-deletes) | — | `Retention.Tests/RetentionSelectionTests` | 1 |
| FR-GC-002 | [07 §3](../architecture/07-retention-and-gc.md#3-garbage-collection) | [0009](../adr/0009-garbage-collection-safety.md) | `Retention.Tests/GenerationCutoffTests` | 4 |
| FR-GC-003 | [07 §3.1](../architecture/07-retention-and-gc.md#31-step-4-is-the-one-that-matters) | [0009](../adr/0009-garbage-collection-safety.md) | `InterruptionTests/GcDuringBackupTests` | 4 |
| FR-GC-004 | [07 §3.2](../architecture/07-retention-and-gc.md#32-step-6-is-only-possible-because-of-c1) | [0007](../adr/0007-logical-object-identifiers-in-manifests.md) | `Retention.Tests/CompactionImmutabilityTests` | 4 |
| FR-GC-005 | [07 §3](../architecture/07-retention-and-gc.md#3-garbage-collection) | — | `Retention.Tests/DryRunTests` | 4 |
| FR-GC-006 | [07 §3](../architecture/07-retention-and-gc.md#3-garbage-collection) | [0009](../adr/0009-garbage-collection-safety.md) | `InterruptionTests/GcStepInterruptionTests` | 4 |
| FR-GC-007 | [07 §5](../architecture/07-retention-and-gc.md#5-destructive-change-safeguards) | — | `IntegrationTests/RetentionFloorTests` | 4 |
| FR-GC-008 | [07 §5](../architecture/07-retention-and-gc.md#5-destructive-change-safeguards) | — | `IntegrationTests/DestructiveAuditTests` | 4 |
| FR-QUOTA-001..002 | [09 §6](../architecture/09-replication-and-peers.md#6-quotas-and-exhaustion) | [0012](../adr/0012-storage-provider-contract.md) | `Storage.ContractTests/QuotaExhaustionTests` | 2 |

### Governance and import

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-GOV-001..004 | — | [0001](../adr/0001-licence-and-contribution-model.md) | Release checklist | 0 / pre-1.0 |
| FR-CP-001..006 | [11 §4](../architecture/11-solution-structure.md#4-import-isolation) | [0015](../adr/0015-crashplan-importer-isolation.md) | `Import.CrashPlan.Tests/*`, `ArchitectureTests/ImportIsolationTests` | 5 |

---

## Non-functional

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| NFR-PERF-001..003 | [02 §3.4](../architecture/02-repository-format.md#34-capture-algorithm) | — | `PerformanceTests/PipelineBenchmarks` | 0 |
| NFR-PERF-004, 010, 011 | [02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild) | [0010](../adr/0010-local-store-separation.md) | `PerformanceTests/CatalogueBenchmarks` | 0 |
| NFR-PERF-005 | [02 §6.3](../architecture/02-repository-format.md#63-sharding-and-encoding) | — | `PerformanceTests/MetadataGrowthTests` | 1 |
| NFR-PERF-006, 008, 009 | [05 §5](../architecture/05-storage-providers.md#5-request-economics) | [0012](../adr/0012-storage-provider-contract.md) | `Storage.ContractTests/RequestCountTests` | 3 |
| NFR-PERF-007 | [02 §3](../architecture/02-repository-format.md#3-segmentation) | [0002](../adr/0002-segmentation-strategy.md) | `PerformanceTests/ThroughputBenchmarks` | 0 |
| NFR-PERF-012 | [02 §8.2](../architecture/02-repository-format.md#82-forensic-rebuild) | — | `PerformanceTests/RebuildBenchmarks` | 0 |
| NFR-PERF-013 | [10 §2](../architecture/10-observability.md#2-technical-metrics) | — | `PerformanceTests/ResourceLimitTests` | 1 |
| NFR-REL-001, 005 | [04 §5.1](../architecture/04-concurrency-and-publication.md#51-interruption-at-each-step) | — | `InterruptionTests/*` | 0 |
| NFR-REL-002, 007 | [11 §3](../architecture/11-solution-structure.md#3-local-state-separation) | [0010](../adr/0010-local-store-separation.md) | `IntegrationTests/LocalStateSeparationTests` | 1 |
| NFR-REL-003, 006 | [02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild) | — | `Repository.Tests/ForensicRebuildTests` | 0 |
| NFR-REL-004 | [02 §8.3](../architecture/02-repository-format.md#83-rebuild-never-repairs) | — | `Repository.FuzzTests/CorruptionScopeTests` | 0 |
| NFR-REL-008 | [08 §5](../architecture/08-restore-and-recovery.md#5-emergency-recovery) | [0014](../adr/0014-format-versioning-and-stability.md) | Release checklist | pre-1.0 |
| NFR-SEC-001, 004 | [03 §4](../architecture/03-crypto.md#4-object-identifiers) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Repository.Tests/IdentifierExposureTests` | 0 |
| NFR-SEC-002 | [03 §2](../architecture/03-crypto.md#2-key-hierarchy) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `Repository.Tests/SuiteValidationTests` | 0 |
| NFR-SEC-003 | [03 §3](../architecture/03-crypto.md#3-nonce-and-key-construction) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `Repository.Tests/NonceUniquenessProperties`, `InterruptionTests/BlobSpoolResumeTests` | 0 |
| NFR-SEC-005 | [03 §6](../architecture/03-crypto.md#6-authentication-of-repository-state) | [0008](../adr/0008-index-generations-and-checkpoints.md) | `Repository.FuzzTests/RollbackDetectionTests` | 1 |
| NFR-SEC-006 | [10 §4](../architecture/10-observability.md#4-diagnostics) | — | `IntegrationTests/SecretRedactionTests` | 1 |
| NFR-SEC-007 | [03 §5](../architecture/03-crypto.md#5-deduplication-trust-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Repository.Tests/VerifyOnReuseTests` | 2 |
| NFR-PRIV-001..003 | [10 §4](../architecture/10-observability.md#4-diagnostics), [§5](../architecture/10-observability.md#5-telemetry) | — | `IntegrationTests/TelemetryPrivacyTests` | 1 |
| NFR-TIME-001, 002 | [04 §7](../architecture/04-concurrency-and-publication.md#7-time-and-clock-skew) | [0009](../adr/0009-garbage-collection-safety.md) | `IntegrationTests/ClockSkewTests` | 4 |
| NFR-COMP-001..003 | [02 §2](../architecture/02-repository-format.md#2-object-classes) | [0014](../adr/0014-format-versioning-and-stability.md) | `Repository.ConformanceTests/FeatureNegotiationTests` | 0 |
| NFR-COMP-004 | [02](../architecture/02-repository-format.md) | [0014](../adr/0014-format-versioning-and-stability.md) | Independent reader — gates freeze | freeze |
| NFR-COMP-005 | [05 §1](../architecture/05-storage-providers.md#1-principles) | [0012](../adr/0012-storage-provider-contract.md) | `Storage.ContractTests/ProviderNeutralityTests` | 3 |
| NFR-COMP-006 | [09 §2.1](../architecture/09-replication-and-peers.md#21-version-skew) | [0014](../adr/0014-format-versioning-and-stability.md) | `Protocol.Tests/VersionSkewTests` | 2 |
| NFR-COMP-007 | — | [0014](../adr/0014-format-versioning-and-stability.md) | Release checklist | 0 |
| NFR-OPS-001, 002 | [10](../architecture/10-observability.md) | — | `IntegrationTests/StatusModelTests` | 1 |
| NFR-OPS-003 | [11 §3](../architecture/11-solution-structure.md#3-local-state-separation) | [0010](../adr/0010-local-store-separation.md) | `IntegrationTests/ConfigValidationTests` | 1 |
| NFR-OPS-004 | [02 §5.1](../architecture/02-repository-format.md#51-purpose-and-sizing) | — | `PerformanceTests/ConsumerHardwareTests` | 1 |
| NFR-OPS-005 | [08 §6](../architecture/08-restore-and-recovery.md#6-what-must-survive-a-clean-machine) | [0013](../adr/0013-recovery-kit.md) | `EndToEndTests/CleanMachineRecoveryTests` | 1 |
| NFR-UX-001, 002 | [10 §1](../architecture/10-observability.md#1-user-level-status) | — | Accessibility and pseudo-localisation audits | 6 |
| NFR-PORT-001 | [11 §5](../architecture/11-solution-structure.md#5-technology) | [0010](../adr/0010-local-store-separation.md) | CI matrix | 0 |
| NFR-PORT-002 | [11 §2](../architecture/11-solution-structure.md#2-dependency-rules) | — | `ArchitectureTests/*` | 0 |
| NFR-PORT-003 | — | [0003](../adr/0003-canonical-metadata-encoding.md) | `Repository.ConformanceTests/*` | 0 |
| NFR-PORT-004 | [05 §2](../architecture/05-storage-providers.md#2-the-store-interface) | [0012](../adr/0012-storage-provider-contract.md) | `ArchitectureTests/ApiShapeTests` | 0 |
| NFR-SUP-001..004 | — | [0001](../adr/0001-licence-and-contribution-model.md) | Release pipeline checks | pre-1.0 |

---

## Coverage

Every FR and NFR appears exactly once above. Requirements with no ADR are ones where no decision was contested — the architecture section is sufficient.

Four items are satisfied by process rather than a test project (FR-GOV-001..004, NFR-REL-008, NFR-COMP-007, NFR-SUP-001..004). Those belong on the release checklist, and the checklist is itself a deliverable of Phase 0 governance work.

---

**Previous:** [Non-functional requirements](non-functional.md)

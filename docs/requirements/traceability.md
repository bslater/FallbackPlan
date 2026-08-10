# Traceability matrix

**Status:** draft · **Resolves:** [M5](../review/2026-08-architecture-review.md#m5--most-requirements-are-not-testable-as-written-and-the-promised-traceability-does-not-exist)

---

The original proposal opened §3.4 by stating that requirement IDs existed "so they can be traced into architecture decisions, implementation work items, and tests". No such mapping was present. This is it.

Every requirement maps to at least one architecture section. Requirements arising from a contested decision also name an ADR.

**The Test column names test classes that exist.** It did not always: the column was written as *planned* names, which licensed it to drift until 73 of its 86 citations named classes nobody had written — a matrix that looked like coverage and was mostly fiction. The names now come from the tests themselves, by way of the requirement IDs their doc comments cite, and `eng/check-requirements.py` resolves every cell on each run so the column cannot quietly become a wish again. A requirement no test cites says so, in the open, with the phase that owes it.

That marker is a statement about *traceable* coverage, not proof of its absence: a test may exercise a requirement without citing its ID, and it will read as untested here until it does. The first pass read 57 requirements as tested; auditing every phase-0 and phase-1 marker against the tests that actually exist raised it to 75, because most of that gap was uncited coverage rather than missing coverage. `eng/check-requirements.py --drift` reports the remainder — requirements a test proves that its row does not name — and is the tool for repeating the exercise rather than re-deriving it.

Two markers survived that audit for reasons worth keeping distinct from "nobody wrote a test". A **manual harness** is not a test: `PerformanceTests/MemoryBoundProof` demonstrated FR-ARCH-002 to anyone who ran it, and nothing ran it — `Repository.Tests/BoundedMemoryTests` now asserts the same property at a scale the suite can afford, and the harness stays for the multi-gibibyte figure. A **benchmark is not an assertion**: NFR-PERF-007 is measured by `ThroughputBenchmarks` and still unmet, because every figure is container measurement and the requirement is stated against a reference machine. Both cells name the thing that falls short, so the shortfall is legible instead of implied.

**NFR-PERF-005 is met on both terms, and it took two changes.** The manifest plane became incremental when reuse was extended to manifests: an object the index already locates is not written again, so a one-file backup of a 1 024-file tree rewrites four records rather than every directory in the repository. The **source-identity hint** ([06 §11](../../specifications/repository-format/06-manifests.md#11-source-identity)) was the other term — it once described the whole snapshot at ~52 bytes per file every run — and is now keyed by source key and written only for a version it creates. The test asserts total store growth, not just metadata bytes, which is the assertion the old shape made impossible: 7 386 bytes for that one-file backup, against ~57 200 before.

A test may also need to **name a requirement in order to disclaim it**, which happens when a cell used to claim coverage it did not have. A doc comment saying *"this class does not establish FR-…"* would otherwise reinstate the very claim it withdraws, and `--drift` would report the file as proving what it just denied. The phrase `does not establish` is honoured by the extractor for that reason, and is fixed so it can be grepped.

**Legend:** *Arch* = architecture section · *ADR* = decision record · *Test* = the test classes citing this requirement, or an explicit untested marker · *Phase* = [roadmap](../roadmap.md) phase that must satisfy it.

---

## Functional

### Archive engine

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-ARCH-001 | [02 §3](../architecture/02-repository-format.md#3-segmentation) | [0002](../adr/0002-segmentation-strategy.md) | `Repository.ConformanceTests/SegmentationConformanceTests`, `Repository.Tests/ArchiveRoundTripTests` | 0 |
| FR-ARCH-002 | [02 §3.4](../architecture/02-repository-format.md#34-capture-algorithm) | — | `Repository.Tests/BoundedMemoryTests` *(reduced scale: four times the input, not two tebibytes; `PerformanceTests/MemoryBoundProof` remains the multi-gibibyte harness)* | 0 |
| FR-ARCH-003 | [03 §4](../architecture/03-crypto.md#4-object-identifiers) | [0004](../adr/0004-segment-hash-function.md) | `Repository.ConformanceTests/IdentifierConformanceTests`, `Repository.Tests/ArchiveRoundTripTests` | 0 |
| FR-ARCH-004 | [02 §3.4](../architecture/02-repository-format.md#34-capture-algorithm) | — | `Repository.Tests/CdcSecondBackupTests`, `Repository.Tests/SecondBackupReuseTests` | 0 |
| FR-ARCH-005 | [02 §4](../architecture/02-repository-format.md#4-compression) | — | `Repository.ConformanceTests/CompressionConformanceTests`, `Repository.Tests/ArchiveRoundTripTests` | 0 |
| FR-ARCH-006 | [02 §5.1](../architecture/02-repository-format.md#51-purpose-and-sizing) | — | `Repository.Tests/ArchiveRoundTripTests`, `Repository.Tests/BlobWriterAndReaderTests` | 0 |
| FR-ARCH-007 | [02 §5.1](../architecture/02-repository-format.md#51-purpose-and-sizing) | — | `Domain.Tests/BlobWriteProfileTests`, `Domain.Tests/CapturePolicyValidationTests` | 0 |
| FR-ARCH-008 | [03 §2](../architecture/03-crypto.md#2-key-hierarchy) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `Repository.ConformanceTests/KekConformanceTests`, `Repository.ConformanceTests/KeyHierarchyConformanceTests` | 0 |
| FR-ARCH-009 | [02 §5.2](../architecture/02-repository-format.md#52-layout) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `Repository.ConformanceTests/RecordCipherConformanceTests`, `Repository.ConformanceTests/RecordFramingConformanceTests` | 0 |
| FR-ARCH-010 | [02 §6.2](../architecture/02-repository-format.md#62-manifests-hold-logical-facts-only) | [0007](../adr/0007-logical-object-identifiers-in-manifests.md) | `Repository.Tests/ManifestCodecTests` | 0 |
| FR-ARCH-011 | [02 §5.3](../architecture/02-repository-format.md#53-spooling-and-sealing) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `InterruptionTests/BlobSpoolResumeTests`, `Repository.Tests/SpoolCheckpointTests` | 0 |
| FR-ARCH-012 | [02 §5.2](../architecture/02-repository-format.md#52-layout) | — | `Repository.Tests/ArchiveRoundTripTests`, `Repository.Tests/BlobWriterAndReaderTests` | 0 |
| FR-ARCH-013 | [02 §3.5](../architecture/02-repository-format.md#35-configuration-envelope) | — | `Repository.Tests/FixedSegmentReaderTests` (boundary arithmetic), `Repository.Tests/SnapshotPublicationTests` (sparse round trip), `Repository.Tests/ArchiveRoundTripTests` (empty file); the sparse clause "without materialising zero payload" is not met by the restore path — see [Q22](../open-questions.md#q22--sparse-restore-materialises-zeroes) | 0 |
| FR-ARCH-014 | [02 §3.1](../architecture/02-repository-format.md#31-profiles), [§3.3](../architecture/02-repository-format.md#33-the-freeze-gate) | [0002](../adr/0002-segmentation-strategy.md) | `Repository.Tests/CdcSecondBackupTests`, `Repository.Tests/CdcSegmentReaderTests` | 0 → freeze |

### Manifests, indexes, catalogue

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-MAN-001 | [02 §6](../architecture/02-repository-format.md#6-manifests) | — | `Repository.Tests/CatalogueRebuildTests` | 0 |
| FR-MAN-002 | [02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild) | [0010](../adr/0010-local-store-separation.md) | `Repository.Tests/CatalogueTests` | 0 |
| FR-MAN-003 | [02 §6.1](../architecture/02-repository-format.md#61-immutable-metadata-objects) | [0007](../adr/0007-logical-object-identifiers-in-manifests.md) | `Repository.Tests/ManifestCodecTests`, `Repository.Tests/ManifestRoundTripTests` | 0 |
| FR-MAN-004 | [02 §6.3](../architecture/02-repository-format.md#63-sharding-and-encoding) | [0003](../adr/0003-canonical-metadata-encoding.md) | `Repository.Tests/SnapshotPublicationTests` | 1 |
| FR-MAN-005 | [02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild) | — | `Repository.Tests/CatalogueTests` | 1 |
| FR-MAN-006 | [03 §5](../architecture/03-crypto.md#5-deduplication-trust-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Repository.Tests/IncrementalBackupTests`, `Repository.Tests/SecondBackupReuseTests` | 0 |
| FR-MAN-007 | [02 §5.2](../architecture/02-repository-format.md#52-layout) | — | `Repository.Tests/BlobWriterAndReaderTests` | 0 |
| FR-MAN-008 | [02 §7.2](../architecture/02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing) | [0008](../adr/0008-index-generations-and-checkpoints.md) | `Repository.Tests/IndexPlaneTests` | 0 |
| FR-MAN-013 | [02 §7.2](../architecture/02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing) | [0008](../adr/0008-index-generations-and-checkpoints.md) | `Repository.Tests/IndexPrecedenceTests` | 0 |
| FR-MAN-015 | [02 §7.2](../architecture/02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing) | [0017](../adr/0017-index-entry-supersession.md) | `Repository.Tests/IndexPrecedenceTests` | 0 |
| FR-MAN-016 | [02 §7.2](../architecture/02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing) | [0008](../adr/0008-index-generations-and-checkpoints.md) | `Repository.Tests/IndexPlaneTests` | 0 |
| FR-MAN-017 | [02 §7.2](../architecture/02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing) | [0008](../adr/0008-index-generations-and-checkpoints.md) | `Repository.Tests/IndexPlaneTests` | 0 |
| FR-MAN-009 | [02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild) | — | `Repository.Tests/ForensicRebuildTests` | 0 |
| FR-MAN-010 | [02 §8.2](../architecture/02-repository-format.md#82-forensic-rebuild) | — | `Repository.Tests/ForensicRebuildTests`, `Repository.Tests/RestorePlanTests` | 1 |
| FR-MAN-011 | [02 §8.2](../architecture/02-repository-format.md#82-forensic-rebuild) | — | `InterruptionTests/CorruptionHarnessTests` | 0 |
| FR-MAN-012 | [02 §8.3](../architecture/02-repository-format.md#83-rebuild-never-repairs) | — | `Repository.Tests/ForensicRebuildTests` | 1 |
| FR-MAN-014 | [02 §8.3](../architecture/02-repository-format.md#83-rebuild-never-repairs) | — | `Repository.Tests/ForensicRebuildTests` | 0 |

### Deduplication trust

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-DED-001 | [03 §5.2](../architecture/03-crypto.md#52-the-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Repository.Tests/DedupTrustDomainTests` | 2 |
| FR-DED-002 | [03 §5.2](../architecture/03-crypto.md#52-the-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Repository.Tests/DedupTrustDomainTests` | 0 |
| FR-DED-003 | [03 §5.2](../architecture/03-crypto.md#52-the-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md), [0026 §9](../adr/0026-phase-1-capture-shapes.md) — outcome durability explicitly deferred: v1 re-verifies after a catalogue rebuild | `Repository.Tests/DedupTrustDomainTests` | 2 |
| FR-DED-004 | [03 §5.2](../architecture/03-crypto.md#52-the-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | — *(unmet; the unverified domain is selectable and behaves as specified — `Repository.Tests/DedupTrustDomainTests` holds that — but no acknowledgement gate exists, so nothing stops it being enabled silently. The gate belongs where the domain is chosen, which is the client, not the engine)* | 2 |

### Snapshots

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-SNP-001 | [04 §6](../architecture/04-concurrency-and-publication.md#6-commit-versus-replication) | [0011](../adr/0011-commit-versus-replication-semantics.md) | `Repository.Tests/PublicationOrchestratorTests` | 1 |
| FR-SNP-002 | [01 §5](../architecture/01-domain-model.md#5-replication-not-synchronisation) | — | `Repository.Tests/IncrementalBackupTests` | 1 |
| FR-SNP-003 | [04 §6.1](../architecture/04-concurrency-and-publication.md#61-the-distinction) | [0011](../adr/0011-commit-versus-replication-semantics.md) | — *(untested; phase 2)* | 2 |
| FR-SNP-007 | [04 §6.4](../architecture/04-concurrency-and-publication.md#64-protected-requires-an-independent-failure-domain) | [0018](../adr/0018-replica-failure-domains.md) | — *(untested; phase 2)* | 2 |
| FR-SNP-004 | [04 §4](../architecture/04-concurrency-and-publication.md#4-write-intent) | [0009](../adr/0009-garbage-collection-safety.md) | `InterruptionTests/ConcurrentUploadTests` | 0 |
| FR-SNP-005 | [04 §4.2.1](../architecture/04-concurrency-and-publication.md#421-expiry-needs-two-conditions-not-one) | [0009](../adr/0009-garbage-collection-safety.md) | `Repository.Tests/JournalTests` | 4 |
| FR-SNP-006 | [02 §5.3](../architecture/02-repository-format.md#53-spooling-and-sealing) | [0016](../adr/0016-blob-identifier-formation.md) | `Repository.Tests/BlobStoreKeysTests`, `InterruptionTests/SequenceRollbackTests` | 0 |

### Restore and recovery kit

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-RST-001 | [08 §1](../architecture/08-restore-and-recovery.md#1-restore-paths) | — | `Repository.Tests/RestorePlanTests` *(in part: snapshot, path and destination end to end; the device, time and file-version selectors do not exist yet — phase 2)* | 1 |
| FR-RST-002 | [08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) | — | `Repository.Tests/ArchiveRoundTripTests`, `Repository.Tests/ForensicRebuildTests`, `Repository.Tests/RestoreAssemblyTests` | 0 |
| FR-RST-003 | [08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning) | — | `Repository.Tests/RestorePlanTests` | 1 |
| FR-RST-004 | [08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) | — | `Repository.Tests/RestorePlanTests` | 1 |
| FR-RST-005 | [08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) | — | `Repository.Tests/ArchiveCorruptionTests` | 1 |
| FR-RST-006 | [08 §3.1](../architecture/08-restore-and-recovery.md#31-quarantine-by-default) | — | `Repository.Tests/RestorePlanTests` | 1 |
| FR-KIT-001..006 | [08 §4](../architecture/08-restore-and-recovery.md#4-recovery-kit) | [0013](../adr/0013-recovery-kit.md) | `Cli.Tests/CommandTests`, `Hosts.Tests/RecoveryHostTests`, `Repository.ConformanceTests/RecoveryKitConformanceTests` | 1 |

### Replication and verification

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-REP-001 | [09 §1](../architecture/09-replication-and-peers.md#1-what-replication-moves) | [0011](../adr/0011-commit-versus-replication-semantics.md) | — *(untested; phase 2)* | 2 |
| FR-REP-002 | [05 §4](../architecture/05-storage-providers.md#4-providers) | [0012](../adr/0012-storage-provider-contract.md) | `Repository.Tests/RepositoryDescriptorCodecTests`, `Repository.Tests/RepositoryLifecycleTests` | 3 |
| FR-REP-003 | [04 §5.1](../architecture/04-concurrency-and-publication.md#51-interruption-at-each-step) | — | — *(untested; phase 2)* | 2 |
| FR-REP-004 | [09 §2.1](../architecture/09-replication-and-peers.md#21-version-skew) | [0014](../adr/0014-format-versioning-and-stability.md) | — *(untested; phase 2)* | 2 |
| FR-VER-001..005 | [09 §5](../architecture/09-replication-and-peers.md#5-destination-verification) | — | — *(untested; phase 2)* | 2 |

### Retention, GC, quotas

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-GC-001 | [07 §1](../architecture/07-retention-and-gc.md#1-retention-selects-collection-deletes) | — | `InterruptionTests/ConcurrentCollectionTests`, `Repository.Tests/JournalTests` | 1 |
| FR-GC-002 | [07 §3](../architecture/07-retention-and-gc.md#3-garbage-collection) | [0009](../adr/0009-garbage-collection-safety.md) | — *(untested; phase 4)* | 4 |
| FR-GC-003 | [07 §3.1](../architecture/07-retention-and-gc.md#31-step-4-is-the-one-that-matters) | [0009](../adr/0009-garbage-collection-safety.md) | — *(untested; phase 4)* | 4 |
| FR-GC-004 | [07 §3.2](../architecture/07-retention-and-gc.md#32-step-7-is-only-possible-because-of-c1) | [0007](../adr/0007-logical-object-identifiers-in-manifests.md) | — *(untested; phase 4)* | 4 |
| FR-GC-005 | [07 §3](../architecture/07-retention-and-gc.md#3-garbage-collection) | — | — *(untested; phase 4)* | 4 |
| FR-GC-006 | [07 §3](../architecture/07-retention-and-gc.md#3-garbage-collection) | [0009](../adr/0009-garbage-collection-safety.md) | — *(untested; phase 4)* | 4 |
| FR-GC-007 | [07 §5](../architecture/07-retention-and-gc.md#5-destructive-change-safeguards) | — | — *(untested; phase 4)* | 4 |
| FR-GC-008 | [07 §5](../architecture/07-retention-and-gc.md#5-destructive-change-safeguards) | — | — *(untested; phase 4)* | 4 |
| FR-QUOTA-001..002 | [09 §6](../architecture/09-replication-and-peers.md#6-quotas-and-exhaustion) | [0012](../adr/0012-storage-provider-contract.md) | — *(untested; phase 2)* | 2 |
| FR-GC-009 | [07 §2.1](../architecture/07-retention-and-gc.md#21-retention-must-not-outrun-replication) | [0011](../adr/0011-commit-versus-replication-semantics.md) | — *(untested; phase 4)* | 4 |

### Governance and import

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| FR-SVC-001..008 | [00 §6.1](../architecture/00-overview.md#61-process-model), [04 §9](../architecture/04-concurrency-and-publication.md#9-two-different-concurrencies-and-why-conflating-them-is-dangerous), [11 §2](../architecture/11-solution-structure.md#2-dependency-rules) | [0028](../adr/0028-service-boundary-and-deployment-topologies.md) | `Api.Tests/ContractVersionTests`, `Api.Tests/LocalBindingTests`, `Application.Tests/StateDirectoryLockTests`, `Hosts.Tests/ClientModeTests`, `Hosts.Tests/ProcessRaceTests`, `Hosts.Tests/ProgressHubTests`, `Hosts.Tests/ServiceTests` | 2 |
| FR-GOV-001..004 | — | [0001](../adr/0001-licence-and-contribution-model.md) | — *(not a test; release checklist)* | 0 / pre-1.0 |
| FR-CP-001..006 | [11 §4](../architecture/11-solution-structure.md#4-import-isolation) | [0015](../adr/0015-crashplan-importer-isolation.md) | `Repository.Tests/LegacyImportTests` | 5 |

---

## Non-functional

| ID | Arch | ADR | Test | Phase |
|----|------|-----|------|-------|
| NFR-PERF-001..003 | [02 §3.4](../architecture/02-repository-format.md#34-capture-algorithm) | — | `Domain.Tests/CapturePolicyValidationTests`, `Filesystem.Tests/LocalScanTests`, `InterruptionTests/ConcurrentUploadTests` | 0 |
| NFR-PERF-004, 010, 011 | [02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild) | [0010](../adr/0010-local-store-separation.md) | `Repository.Tests/CatalogueRebuildTests`, `Repository.Tests/CatalogueTests` | 0 |
| NFR-PERF-005 | [02 §6.3](../architecture/02-repository-format.md#63-sharding-and-encoding) | — | `Repository.Tests/IncrementalBackupTests` *(metadata records and total store growth both bounded against the first snapshot)* | 1 |
| NFR-PERF-006, 008, 009 | [05 §5](../architecture/05-storage-providers.md#5-request-economics) | [0012](../adr/0012-storage-provider-contract.md) | — *(untested; phase 3)* | 3 |
| NFR-PERF-007 | [02 §3](../architecture/02-repository-format.md#3-segmentation) | [0002](../adr/0002-segmentation-strategy.md), [0029](../adr/0029-pipeline-and-service-concurrency.md) | — *(unmet; measured by `PerformanceTests/ThroughputBenchmarks`, but every figure is container measurement and the ≥400 MB/s is stated against a reference machine none of it has run on)* | 1 |
| NFR-PERF-012, 015 | [02 §8.2](../architecture/02-repository-format.md#82-forensic-rebuild) | [0007](../adr/0007-logical-object-identifiers-in-manifests.md) | `Repository.Tests/ForensicRebuildTests`, `Repository.Tests/RestorePlanTests` | 0 |
| NFR-PERF-014 | [02 §7.1](../architecture/02-repository-format.md#71-structure) | [0007](../adr/0007-logical-object-identifiers-in-manifests.md) | `Repository.Tests/IndexSizeTests` *(marginal cost ~68 B/object; the fixed 65 536-shard hash table dominates below ~200 000 objects)* | 0 |
| NFR-PERF-013 | [10 §2](../architecture/10-observability.md#2-technical-metrics) | — | — *(untested; ADR-0029's implementation notes say the CPU cap should be measured rather than inferred from the setting's name, and it has not been)* | 1 |
| NFR-REL-001, 005 | [04 §5.1](../architecture/04-concurrency-and-publication.md#51-interruption-at-each-step) | — | `InterruptionTests/BlobSpoolResumeTests`, `InterruptionTests/CancellationTests`, `InterruptionTests/PublicationInterruptionTests`, `InterruptionTests/SequenceRollbackTests`, `InterruptionTests/SessionDisposalTests`, `InterruptionTests/SpoolHygieneTests`, `InterruptionTests/StoreFaultTests`, `InterruptionTests/StorePutSweepTests`, `InterruptionTests/VoidObligationTests`, `Repository.FuzzTests/ParserFuzzTests` | 0 |
| NFR-REL-002, 007 | [11 §3](../architecture/11-solution-structure.md#3-local-state-separation) | [0010](../adr/0010-local-store-separation.md) | `Repository.Tests/CatalogueRebuildTests`, `Repository.Tests/LocalStateSeparationTests` | 1 |
| NFR-REL-003, 006 | [02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild) | — | `Repository.Tests/ForensicRebuildTests` | 0 |
| NFR-REL-004 | [02 §8.3](../architecture/02-repository-format.md#83-rebuild-never-repairs) | — | `InterruptionTests/CorruptionHarnessTests`, `InterruptionTests/StoreFaultTests`, `Repository.Tests/ArchiveCorruptionTests` | 0 |
| NFR-REL-008 | [08 §5](../architecture/08-restore-and-recovery.md#5-emergency-recovery) | [0014](../adr/0014-format-versioning-and-stability.md) | — *(untested; phase pre-1.0)* | pre-1.0 |
| NFR-SEC-001, 004 | [03 §4](../architecture/03-crypto.md#4-object-identifiers) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Domain.Tests/GuardClauseTests`, `Repository.ConformanceTests/IdentifierConformanceTests`, `Repository.Tests/ObjectIdDeriverTests` | 0 |
| NFR-SEC-002 | [03 §2](../architecture/03-crypto.md#2-key-hierarchy) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `Domain.Tests/ProfileTests` | 0 |
| NFR-SEC-003 | [03 §3](../architecture/03-crypto.md#3-nonce-and-key-construction) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `InterruptionTests/BlobSpoolResumeTests`, `Repository.ConformanceTests/RecordFramingConformanceTests` | 0 |
| NFR-SEC-005 | [03 §6](../architecture/03-crypto.md#6-authentication-of-repository-state) | [0008](../adr/0008-index-generations-and-checkpoints.md) | `InterruptionTests/CorruptionHarnessTests` | 1 |
| NFR-SEC-006 | [10 §4](../architecture/10-observability.md#4-diagnostics) | — | `Repository.Tests/LocalStateSeparationTests`, `Repository.Tests/TelemetryPrivacyTests` | 1 |
| NFR-SEC-007 | [03 §5](../architecture/03-crypto.md#5-deduplication-trust-domains) | [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | `Repository.Tests/DedupTrustDomainTests` — a failed verification is re-written from this device's bytes and reported, in both the repository and device domains; cross-device *replication* scenarios remain phase 2 | 2 |
| NFR-SEC-008 | [03 §3.1](../architecture/03-crypto.md#31-the-construction) | [0005](../adr/0005-aead-suite-and-nonce-construction.md) | `Repository.ConformanceTests/KeyHierarchyConformanceTests` | 0 |
| NFR-SEC-009 | [00 §6.2](../architecture/00-overview.md#62-installation-topologies) | [0028](../adr/0028-service-boundary-and-deployment-topologies.md) | `Api.Tests/KeyMaterialConfinementTests`, `ArchitectureTests/DependencyRuleTests` | 2 |
| NFR-PRIV-001..003 | [10 §4](../architecture/10-observability.md#4-diagnostics), [§5](../architecture/10-observability.md#5-telemetry) | — | `Repository.Tests/TelemetryPrivacyTests` | 1 |
| NFR-TIME-001, 002 | [04 §7](../architecture/04-concurrency-and-publication.md#7-time-and-clock-skew) | [0009](../adr/0009-garbage-collection-safety.md) | `Repository.Tests/ApplicationServiceTests` | 4 |
| NFR-COMP-001..003 | [02 §2](../architecture/02-repository-format.md#2-object-classes) | [0014](../adr/0014-format-versioning-and-stability.md) | `Repository.Tests/RepositoryDescriptorCodecTests` | 0 |
| NFR-COMP-004 | [02](../architecture/02-repository-format.md) | [0014](../adr/0014-format-versioning-and-stability.md) | `Repository.ConformanceTests/FixtureRepositoryTests`, `Repository.Tests/CanonicalCborRejectionTests` | freeze |
| NFR-COMP-005 | [05 §1](../architecture/05-storage-providers.md#1-principles) | [0012](../adr/0012-storage-provider-contract.md) | `ArchitectureTests/DependencyRuleTests` | 3 |
| NFR-COMP-006 | [09 §2.1](../architecture/09-replication-and-peers.md#21-version-skew) | [0014](../adr/0014-format-versioning-and-stability.md) | — *(untested; phase 2)* | 2 |
| NFR-COMP-007 | — | [0014](../adr/0014-format-versioning-and-stability.md) | `Repository.Tests/RepositoryLifecycleTests` | 0 |
| NFR-OPS-001, 002 | [10](../architecture/10-observability.md) | — | `Api.Tests/StatusAggregationTests` | 1 |
| NFR-OPS-003 | [11 §3](../architecture/11-solution-structure.md#3-local-state-separation) | [0010](../adr/0010-local-store-separation.md) | `Domain.Tests/CapturePolicyValidationTests` | 1 |
| NFR-OPS-004 | [02 §5.1](../architecture/02-repository-format.md#51-purpose-and-sizing) | — | `Domain.Tests/CapturePolicyValidationTests` | 1 |
| NFR-OPS-005 | [08 §6](../architecture/08-restore-and-recovery.md#6-what-must-survive-a-clean-machine) | [0013](../adr/0013-recovery-kit.md) | `ArchitectureTests/DependencyRuleTests` | 1 |
| NFR-OPS-006 | [10 §3.1](../architecture/10-observability.md#31-how-a-client-learns-any-of-this) | [0028](../adr/0028-service-boundary-and-deployment-topologies.md) | `Api.Tests/StatusAggregationTests` | 2 |
| NFR-UX-001, 002 | [10 §1](../architecture/10-observability.md#1-user-level-status) | [0031](../adr/0031-exception-messages-are-resources.md) — externalisation built: 328 messages in per-assembly resx, drift checked in CI | — *(not a test; the remaining half is an accessibility and pseudo-localisation audit)* | 6 |
| NFR-PORT-001 | [11 §5](../architecture/11-solution-structure.md#5-technology) | [0010](../adr/0010-local-store-separation.md) | `ArchitectureTests/DependencyRuleTests` | 0 |
| NFR-PORT-002 | [11 §2](../architecture/11-solution-structure.md#2-dependency-rules) | — | `ArchitectureTests/DependencyRuleTests`, `Storage.ContractTests/ObjectStoreContractTests` | 0 |
| NFR-PORT-003 | — | [0003](../adr/0003-canonical-metadata-encoding.md) | `Repository.Tests/CanonicalCborRejectionTests`, `Repository.Tests/CanonicalCborRoundTripTests` | 0 |
| NFR-PORT-004 | [05 §2](../architecture/05-storage-providers.md#2-the-store-interface) | [0012](../adr/0012-storage-provider-contract.md), [0029](../adr/0029-pipeline-and-service-concurrency.md) | `Api.Tests/LocalBindingTests`, `ArchitectureTests/ApiShapeTests` | 1 |
| NFR-SUP-001..004 | — | [0001](../adr/0001-licence-and-contribution-model.md) | — *(not a test; release pipeline checks)* | pre-1.0 |

---

## Coverage

Every FR and NFR appears exactly once above. Requirements with no ADR are ones where no decision was contested — the architecture section is sufficient.

Four items are satisfied by process rather than a test project (FR-GOV-001..004, NFR-REL-008, NFR-COMP-007, NFR-SUP-001..004). Those belong on the release checklist, and the checklist is itself a deliverable of Phase 0 governance work.

---

**Previous:** [Non-functional requirements](non-functional.md)

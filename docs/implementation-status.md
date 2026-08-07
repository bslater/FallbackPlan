# Implementation status

**Status:** maintained · **Checked by:** [`eng/check-adr-status.py`](../eng/check-adr-status.py)

---

Thirty decision records say what this system should do. This says which of them the code actually does, and — where the answer is "some of it" — which part.

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
| [0006](adr/0006-object-identifiers-and-dedup-trust-domains.md) | Object identifiers and dedup trust domains | **Partly built** | `Repository.Crypto/ObjectIdDeriver` · [notes](#0006--dedup-domains-exist-the-cross-device-domain-has-nothing-to-cross) |
| [0007](adr/0007-logical-object-identifiers-in-manifests.md) | Manifests carry logical identifiers only | **Built** | `Repository.Format/Manifests/*` · `Repository.Tests/Index/IndexPrecedenceTests` · [Q11](open-questions.md#q11--physical-hints-in-segment-references) still open |
| [0008](adr/0008-index-generations-and-checkpoints.md) | Index generations, deltas, checkpoints | **Built** | `Repository.Index/CheckpointCodec`, `Repository.Index/IndexDeltaCodec`, `Repository.Index/WriterSequence` |
| [0009](adr/0009-garbage-collection-safety.md) | Garbage collection safety | **Partly built** | `Repository.Index/Journal/IntentLifecycle` · [notes](#0009--the-intents-are-written-nothing-collects-yet) |
| [0010](adr/0010-local-store-separation.md) | Local store separation | **Built** | `Application/LocalState` · `Repository.Tests/EndToEnd/LocalStateSeparationTests` |
| [0011](adr/0011-commit-versus-replication-semantics.md) | Commit versus replication semantics | **Partly built** | `Repository/SnapshotPublication` · [notes](#0011-0018--commit-is-per-replica-and-there-is-one-replica) |
| [0012](adr/0012-storage-provider-contract.md) | Storage provider contract | **Partly built** | `Storage.Abstractions`, `Storage.Local` · `Storage.ContractTests` · [notes](#0012--the-contract-is-real-it-has-one-provider) |
| [0013](adr/0013-recovery-kit.md) | Recovery kit contents and format | **Built** | `FallbackPlan.Recovery`, [`specifications/recovery-kit/`](../specifications/recovery-kit/README.md) · `Repository.ConformanceTests/RecoveryKitConformanceTests` |
| [0014](adr/0014-format-versioning-and-stability.md) | Format versioning and pre-1.0 posture | **Built** | `Repository/RepositoryLifecycle` · `Repository.Tests/EndToEnd/RepositoryLifecycleTests` |
| [0015](adr/0015-crashplan-importer-isolation.md) | CrashPlan importer isolation | **Partly built** | `FallbackPlan.Import.Abstractions` · [notes](#0015--the-seam-is-the-decision-and-the-seam-is-built) |
| [0016](adr/0016-blob-identifier-formation.md) | Blob identifiers are writer-allocated | **Built** | `Domain/Identifiers/BlobId`, `Domain/IBlobCounterAllocator` |
| [0017](adr/0017-index-entry-supersession.md) | Index entry supersession and precedence | **Built** | `Repository.Index/IndexEntry`, `Repository.Index/IndexLoader` · `Repository.Tests/Index/IndexPrecedenceTests` |
| [0018](adr/0018-replica-failure-domains.md) | Replica failure domains | **Specified only** | [notes](#0011-0018--commit-is-per-replica-and-there-is-one-replica) |
| [0019](adr/0019-third-party-dependency-policy.md) | Third-party dependency policy | **Applied** | `ArchitectureTests/DependencyRuleTests` — the policy is a test, not a promise |
| [0020](adr/0020-ed25519-signing-key-semantics.md) | Ed25519 signing key semantics | **Built** | `Repository.Crypto/RepositorySigner` · `Repository.ConformanceTests/Ed25519ConformanceTests` |
| [0021](adr/0021-consume-bodu-via-committed-package-feed.md) | Bodu from a committed local feed | **Applied** | [`external/packages/`](../external/packages/README.md), [`nuget.config`](../nuget.config) |
| [0022](adr/0022-standalone-metadata-records-and-index-identifiers.md) | Standalone records and index identifiers | **Built** | `Repository.Format/Records/*` · `Repository.FuzzTests/ParserFuzzTests` |
| [0023](adr/0023-cdc-v1-rabin-parameters.md) | cdc-v1 Rabin fingerprint parameters | **Built** | `Repository.Segmentation/RabinFingerprint` · `Repository.FuzzTests/CdcPropertyTests` |
| [0024](adr/0024-include-exclude-rule-dialect.md) | Include/exclude rule dialect | **Built** | `Domain/PathRules` · `Repository.ConformanceTests/PathRulesConformanceTests` |
| [0025](adr/0025-compaction-reseals-records.md) | Compaction re-seals records | **Specified only** | [notes](#0025--nothing-compacts-yet-so-nothing-re-seals-yet) |
| [0026](adr/0026-phase-1-capture-shapes.md) | Phase-1 capture shapes | **Built** | `Filesystem.Local/LocalFileSystemSource`, `Filesystem.Local/PosixInterop` · `Filesystem.Tests/LocalScanTests` |
| [0027](adr/0027-services-scheduling-status-telemetry.md) | Scheduling, job state, status, telemetry | **Built** | `FallbackPlan.Agent`, `Application/JobStateStore` · `Hosts.Tests/*` |
| [0028](adr/0028-service-boundary-and-deployment-topologies.md) | The service boundary | **Partly built** | `FallbackPlan.Api`, `Cli/OperationGateway` · [ADR §Implementation status](adr/0028-service-boundary-and-deployment-topologies.md#implementation-status-2026-08) |
| [0029](adr/0029-pipeline-and-service-concurrency.md) | Pipeline and service concurrency | **Built** | `Repository/ArchiveSession` · [ADR §Implementation status](adr/0029-pipeline-and-service-concurrency.md#implementation-status-2026-08) |
| [0030](adr/0030-peer-identity-and-pairing.md) | Peer identity and pairing | **Partly built** | `FallbackPlan.Protocol` · [notes](#0030--everything-above-the-socket-nothing-at-it) |

---

## Where "partly" is doing work

### 0006 — dedup domains exist; the cross-device domain has nothing to cross

Object identifiers, the keyed derivation behind them, and the `repository` trust domain are built and traced. The `device` domain and verify-on-reuse are specified and unexercised, because cross-device deduplication needs a second device to deduplicate against, and replication is not built. `FR-DED-001` through `FR-DED-004` read untested in the matrix for that reason, and correctly so.

### 0009 — the intents are written; nothing collects yet

The half that protects data is built: write-intent journal records, the intent lifecycle, and the rule that any component creating a blob publishes an intent first — including the collector, per [PT-3](review/2026-08-fix-pressure-test.md). Leases are advisory, as decided.

The collector itself does not exist. There is no mark, no sweep, no compaction; `grep` for a collector in `src/` returns the journal record type that *describes* one. That is phase 4 and on plan. The consequence worth stating plainly is that **nothing currently reclaims space**, so a repository grows monotonically, and the safety machinery in place is protecting against a process that has not been written.

### 0011, 0018 — commit is per-replica, and there is one replica

The decision that a snapshot commits per destination rather than globally is in the publication model, and a local repository exercises the single-replica case. Everything that makes the decision *matter* — a second destination, per-destination replication state, failure domains that differ — arrives with replication. ADR-0018 is therefore specified only: `FR-SNP-007` has no test because the situation it describes cannot yet occur.

### 0012 — the contract is real; it has one provider

`Storage.Abstractions` defines the contract, `Storage.ContractTests` is a reusable suite any provider must pass, and `Storage.Local` passes it. This is the shape the decision asked for, and the shape is what protects the design.

It is still one provider. A contract with a single implementation has not yet been tested by the thing it exists for — the second implementation that disagrees with it. Azure and S3 are phase 3, and `NFR-PORT-002` is traced against the architecture tests and the contract suite rather than against a provider that proves portability by being different.

### 0015 — the seam is the decision, and the seam is built

ADR-0015's decision was to isolate a CrashPlan importer behind a boundary, not to write one. `FallbackPlan.Import.Abstractions` is that boundary, and phase 0's exit criteria proved it with a synthetic adapter feeding an arbitrary byte stream through the same pipeline ([roadmap](roadmap.md#phase-0--archive-engine-vertical-slice)).

No CrashPlan reader exists and none should yet: it is phase 5 and gated on a legal review that has not happened. The row reads "partly built" rather than "built" so that nobody reads the seam's existence as the feature's.

### 0025 — nothing compacts yet, so nothing re-seals yet

The decision is sound and unexercised for the same reason as 0009: compaction is part of the collector. What *is* built is the constraint the decision protects — the record ordinal stays in the AAD, and `Repository.Tests/Index/IndexPrecedenceTests` holds the supersession rules a compaction would rely on.

### 0028 — the local binding, not the remote one

Recorded in the ADR's own [implementation status](adr/0028-service-boundary-and-deployment-topologies.md#implementation-status-2026-08) and not duplicated here. In short: writer-role exclusion, the versioned command contract, status aggregation, keystore unlock, per-job progress, and a CLI that asks a running service and falls back to direct mode. The remote binding validates and binds nothing, because it waits on 0030's transport.

### 0030 — everything above the socket, nothing at it

Built, in `FallbackPlan.Protocol` with 78 tests: peer identity and fingerprints; the pairing ceremony's key agreement, transcript, short authentication string and confirmation signatures; the grant store, its pinning and revocation, and the destination's terms; frame encoding and refusal; session hello, accept and refuse; version selection and feature negotiation; and — after [Amendment 1](adr/0030-peer-identity-and-pairing.md#amendment-1-2026-08--authentication-moves-out-of-tls) — the channel-bound authentication that replaced RFC 7250, with a test that runs the man-in-the-middle it defeats.

Not built: **the transport that carries any of it.** Nothing opens a TCP or QUIC connection, negotiates TLS, presents the ephemeral certificate, or drives the state machine over a real socket. Nor is there a user-facing pairing flow — no command shows a short authentication string to a human, and the ceremony has never been performed by two people.

That is the honest shape of it: the protocol is implemented and has never spoken to another machine. Everything above has unit tests that construct both sides in one process, which proves the constructions agree with each other and proves nothing about a network.

---

## By phase

| Phase | State |
|-------|-------|
| [0 — Archive engine](roadmap.md#phase-0--archive-engine-vertical-slice) | Complete; every exit criterion traced to a named test |
| [1 — Snapshot and local repository](roadmap.md#phase-1--snapshot-and-local-repository-mvp) | Complete, both pushes |
| [2 — Peer-to-peer and the service boundary](roadmap.md#phase-2--peer-to-peer-backup-and-the-service-boundary) | Service boundary built on the local binding; peer protocol built to the session layer and not yet carried over a socket |
| 3 — Cloud object stores | Not started |
| 4 — Retention, GC, compaction | Not started — see [0009](#0009--the-intents-are-written-nothing-collects-yet) |
| 5 — CrashPlan import | Not started, gated on legal review |

---

## What keeps this true

[`eng/check-adr-status.py`](../eng/check-adr-status.py) refuses a build where an ADR is missing from the table above, where a row names an ADR that does not exist, where a state is not one of the four in the legend, or — the one that matters — **where a cited project, directory or type is not on disk.** It is the same discipline `eng/check-requirements.py` applies to the traceability matrix, adopted for the same reason: a status page nobody verifies becomes a status page nobody can trust, and the failure is invisible until someone acts on it.

What the checker cannot do is judge whether "built" is generous. That is a reading, and it is repeated whenever a phase closes. It also deliberately does not compare these states against each ADR's `Status:` line: that line records whether a *decision* was accepted, which is a different question from whether the code does it, and collapsing the two would lose both.

---

**See also:** [Abandoned choices](decisions-abandoned.md) — what was considered and rejected, and why · [Traceability](requirements/traceability.md) — requirements to tests · [Roadmap](roadmap.md)

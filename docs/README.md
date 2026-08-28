# FallbackPlan documentation

Encrypted, versioned backup from one computer to another — with no vendor cloud in the middle.

> **Status: Phases 0 and 1 implemented; Phase 2 in progress.** The engine vertical slice is complete — all eleven phase-0 exit criteria trace to named tests ([phase-0 plan](phase-0-execution-plan.md)) — and [Phase 1](phase-1-execution-plan.md) is done: push 1 (filesystem capture, multi-file incremental snapshots, CLI, restore planning, recovery kit and standalone recovery tool) with every exit criterion traced to a named test, and push 2 (Agent scheduling, the job-state journal, the 10 §1 status model, and privacy-bounded OpenTelemetry instrumentation) per [ADR-0027](adr/0027-services-scheduling-status-telemetry.md). Lookup measurements: [phase-1-benchmarks.md](phase-1-benchmarks.md). **Phase 2 is under way** — the service holds the writer role exclusively, hosts a versioned command contract on a local socket or named pipe, and unlocks itself from the platform keystore ([ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md)); blob upload has left the archive loop ([ADR-0029](adr/0029-pipeline-and-service-concurrency.md) §2). What is built, what is not, and **where to pick up next** are in the [phase-2 plan](phase-2-execution-plan.md#where-to-pick-up); measurements are in [phase-2-benchmarks.md](phase-2-benchmarks.md). Decision by decision, what the code actually does is in [implementation status](implementation-status.md), and every architecture document now opens by saying whether it describes code that exists. Before the remote binding is built, the [pipeline integrity review](review/2026-08-pipeline-integrity-review.md) read the write path back against these documents — immutability, atomicity, verified restorability, sudden stop — and fixed everything it graded test-first, including one critical path to an unrestorable committed snapshot; what it left is proof debt, itemised in the pickup list. The most recent round is the **direct-to-destination re-architecture** ([ADR-0046](adr/0046-direct-to-destination-publication.md), [ADR-0047](adr/0047-backup-pool-and-priorities.md)): a `direct_ship` set writes its backups straight to its destinations through the ship sink with the agent holding metadata only, the writer lane is a configurable pool with set and destination priorities, and a higher-priority run suspends and resumes a lower one — built end to end behind a default-off flag, with the peer write adapter and a trimming drill gating the flip ([roadmap arc](roadmap.md#the-direct-to-destination-arc--no-staging-copy-a-backup-pool-priorities-and-preemption-built-behind-a-default-off-flag)).

---

## Start here

| If you want to… | Read |
|-----------------|------|
| Understand what this is | [Overview](architecture/00-overview.md) |
| See how the pieces fit together | [Worked example](architecture/12-worked-example.md) — one file, end to end |
| Know what a word means | [Domain model](architecture/01-domain-model.md) — normative glossary |
| See what changed and why | [Architecture review](review/2026-08-architecture-review.md), then the [pressure test](review/2026-08-fix-pressure-test.md) |
| Know what is still undecided | [Open questions](open-questions.md) |
| Know what is actually built | [Implementation status](implementation-status.md) |
| Know why something *isn't* the design | [Abandoned choices](decisions-abandoned.md) |
| See the calls the format freeze forced | [Freeze-gate decisions](freeze-gate-decisions-2026-08.md) |
| See the delivery plan | [Roadmap](roadmap.md) |
| Pick up where the last round stopped | [Phase 2 plan — where to pick up](phase-2-execution-plan.md#where-to-pick-up) |
| Start building | [Phase 2 execution plan](phase-2-execution-plan.md) (in progress) · [Phase 1 execution plan](phase-1-execution-plan.md) · [Phase 0 execution plan](phase-0-execution-plan.md) (implemented) · [format specification](../specifications/repository-format/README.md) |

## Architecture

| # | Document | Covers |
|---|----------|--------|
| 00 | [Overview](architecture/00-overview.md) | Vision, scope, principles, lessons from prior art |
| 01 | [Domain model](architecture/01-domain-model.md) | Normative glossary, object relationships, snapshot semantics |
| 02 | [Repository format](architecture/02-repository-format.md) | Objects, segmentation, compression, blobs, manifests, indexes, rebuild |
| 03 | [Cryptography](architecture/03-crypto.md) | Key hierarchy, nonce construction, object identifiers, dedup trust domains |
| 04 | [Concurrency and publication](architecture/04-concurrency-and-publication.md) | Writers, write intent, publication order, commit vs replication, clock skew |
| 05 | [Storage providers](architecture/05-storage-providers.md) | Store contract, capabilities, providers, request economics |
| 06 | [Filesystem capture](architecture/06-filesystem-capture.md) | Scanner, path handling, metadata matrix, change detection, consistency |
| 07 | [Retention and GC](architecture/07-retention-and-gc.md) | Retention policy, mark and sweep, compaction, safeguards, healing |
| 08 | [Restore and recovery](architecture/08-restore-and-recovery.md) | Restore paths, planning, verification, recovery kit, emergency recovery |
| 09 | [Replication and peers](architecture/09-replication-and-peers.md) | Peer exchange, pairing, durability policy, verification challenges, quotas |
| 10 | [Observability](architecture/10-observability.md) | User status model, metrics, job state machine, diagnostics, telemetry |
| 11 | [Solution structure](architecture/11-solution-structure.md) | Project layout, dependency rules, local state separation, technology |
| 12 | [Worked example](architecture/12-worked-example.md) | One file, end to end — split, hash, dedup, compress, encrypt, pack, publish, restore |

## Requirements

- [Functional](requirements/functional.md) — FR-*, each with an observable acceptance criterion
- [Non-functional](requirements/non-functional.md) — NFR-*, with quantitative targets and a reference machine
- [Traceability](requirements/traceability.md) — every requirement mapped to architecture, ADR, test, and phase
- [Shared Bodu recurrence package](bodu-recurrence-requirements.md) — consumer-side requirements on the upstream `Bodu.Globalization.Recurrence` package serving FallbackPlan scheduling and similar hosts; **satisfied upstream**, verified by semantic probe in three timezones

## Decisions

Status per record. **Forty-three of the forty-eight are Accepted.** Among them: 0005, 0006, 0008, 0009, 0011 and 0016–0018 following the [pressure test](review/2026-08-fix-pressure-test.md); 0019–0029 on the evidence recorded in them (0028 amended once implementation decided what "or an equivalent" means on Linux); 0001 — dual AGPL-3.0-only + commercial, with `specifications/` under Apache-2.0 ([LICENSING.md](../LICENSING.md)); 0003, 0010, 0013 and 0030 at the freeze-gate pass (0030 amended once when RFC 7250 proved unreachable on the platform); and 0031–0048 as their slices were built, through the direct-to-destination pair 0046/0047 to the determinate-progress record 0048.

**Five remain `Proposed`, each for a stated reason rather than by neglect:** 0002 and 0004 await the corpus benchmark and the hash decision ([Q5](open-questions.md#q5--segmentation-default), [Q6](open-questions.md#q6--segment-hash-function)); 0012 awaits a second storage provider to test the contract against; 0014 is provisional by design until the format freezes; 0015 is gated on the legal review in [Q2](open-questions.md#q2--third-party-reader-licence-and-reuse-posture).

**A decision's status is not its implementation state**, and the two are tracked separately on purpose: `Status:` says whether the decision was accepted, and [implementation status](implementation-status.md) says whether the code does it. The two can differ in both directions, and do: three of the five records still marked `Proposed` are built and tested, and 0030 is Accepted while the transport that would carry it does not exist.

- **[Implementation status](implementation-status.md)** — every ADR mapped to the code and tests that establish it, with the partly-built ones saying which half is missing
- **[Abandoned choices](decisions-abandoned.md)** — what was rejected and why, and separately, what was *the plan* and was given up
- **[Freeze-gate decisions](freeze-gate-decisions-2026-08.md)** — two calls that had to be made before v1 froze rather than when the code reached them: where verify-on-reuse outcomes live, and how a filename with no valid decoding is rendered

| ADR | Decision |
|-----|----------|
| [0001](adr/0001-licence-and-contribution-model.md) | Licence: dual AGPL-3.0-only + commercial; `specifications/` Apache-2.0 |
| [0002](adr/0002-segmentation-strategy.md) | Segmentation: `fixed-v1` default, `cdc-v1` specified, benchmark before freeze |
| [0003](adr/0003-canonical-metadata-encoding.md) | Canonical metadata encoding |
| [0004](adr/0004-segment-hash-function.md) | Segment hash function |
| [0005](adr/0005-aead-suite-and-nonce-construction.md) | AEAD suite and nonce construction |
| [0006](adr/0006-object-identifiers-and-dedup-trust-domains.md) | Object identifiers and dedup trust domains |
| [0007](adr/0007-logical-object-identifiers-in-manifests.md) | Manifests reference logical object identifiers only |
| [0008](adr/0008-index-generations-and-checkpoints.md) | Index generations, deltas, and checkpoints |
| [0009](adr/0009-garbage-collection-safety.md) | Garbage collection safety |
| [0010](adr/0010-local-store-separation.md) | Local store separation |
| [0011](adr/0011-commit-versus-replication-semantics.md) | Commit versus replication semantics |
| [0012](adr/0012-storage-provider-contract.md) | Storage provider contract |
| [0013](adr/0013-recovery-kit.md) | Recovery kit contents and format |
| [0014](adr/0014-format-versioning-and-stability.md) | Format versioning and pre-1.0 stability |
| [0015](adr/0015-legacy-importer-isolation.md) | Legacy importer isolation and licensing gate |
| [0016](adr/0016-blob-identifier-formation.md) | Blob identifiers are writer-allocated, not content-derived |
| [0017](adr/0017-index-entry-supersession.md) | Index entry supersession and precedence |
| [0018](adr/0018-replica-failure-domains.md) | Replica failure domains |
| [0019](adr/0019-third-party-dependency-policy.md) | Third-party dependency policy, and the Bodu adoption |
| [0020](adr/0020-ed25519-signing-key-semantics.md) | Ed25519 signing: seed interpretation, repository scope |
| [0021](adr/0021-consume-bodu-via-committed-package-feed.md) | Consume Bodu as prebuilt packages from a committed local feed |
| [0022](adr/0022-standalone-metadata-records-and-index-identifiers.md) | Standalone metadata records, index identifiers, and phase-0 gap resolutions |
| [0023](adr/0023-cdc-v1-rabin-parameters.md) | cdc-v1 Rabin fingerprint parameters |
| [0024](adr/0024-include-exclude-rule-dialect.md) | Include/exclude rule dialect (rules-v1) |
| [0025](adr/0025-compaction-reseals-records.md) | Compaction re-seals records; the ordinal stays in the AAD |
| [0026](adr/0026-phase-1-capture-shapes.md) | Phase-1 capture shapes: hardlinks, diagnostics, special files, capabilities |
| [0027](adr/0027-services-scheduling-status-telemetry.md) | Push-2 service shapes: scheduling, job-state store, status model, instrumentation |
| [0028](adr/0028-service-boundary-and-deployment-topologies.md) | The service boundary: deployment topologies, process ownership, transport, unlock |
| [0029](adr/0029-pipeline-and-service-concurrency.md) | Pipeline and service concurrency: the ordering barrier, the bound, the order of work |
| [0030](adr/0030-peer-identity-and-pairing.md) | Peer identity and pairing: a transport keypair the repository knows nothing about |
| [0031](adr/0031-exception-messages-are-resources.md) | Exception messages are resources |
| [0032](adr/0032-mstest-as-the-test-framework.md) | MSTest is the test framework |
| [0033](adr/0033-hosting-under-an-os-service-manager.md) | Hosting under an OS service manager |
| [0034](adr/0034-hub-and-spoke-destinations.md) | Hub-and-spoke destinations: per-set staging archives, whole-archive replicas |
| [0035](adr/0035-destination-fitness.md) | Destination fitness: admission, capacity, shortfall, scheduled proof |
| [0036](adr/0036-local-web-console.md) | The local web console |
| [0037](adr/0037-configuration-over-the-command-contract.md) | Configuration over the command contract |
| [0038](adr/0038-set-change-rescan-and-notice.md) | Set changes answered with their meaning: rescan and notice |
| [0039](adr/0039-console-operator-loop.md) | The console's operator loop |
| [0040](adr/0040-multi-root-backup-sets.md) | Multi-root backup sets |
| [0041](adr/0041-guided-restore-and-peer-retrieval.md) | The guided restore and peer retrieval |
| [0042](adr/0042-write-only-repositories.md) | Write-only repositories (format v2) |
| [0043](adr/0043-structured-logging-and-diagnostics.md) | Structured logging and client diagnostics |
| [0044](adr/0044-first-run-setup.md) | First-run setup and the installation passphrase |
| [0045](adr/0045-client-authentication.md) | Client authentication: username, password, session |
| [0046](adr/0046-direct-to-destination-publication.md) | Direct-to-destination publication: the ship sink, no staging archive |
| [0047](adr/0047-backup-pool-and-priorities.md) | The backup pool: concurrency, priorities, preemption, a pass that never waits |
| [0048](adr/0048-determinate-backup-progress.md) | Determinate backup progress: a counted plan, a replayed snapshot, a stream that idles |

Template: [0000](adr/0000-template.md)

## Specification

The normative on-disk format lives outside `docs/`, in [`specifications/repository-format/`](../specifications/repository-format/README.md), with [conformance vectors](../specifications/repository-format/conformance/README.md). The [recovery-kit format](../specifications/recovery-kit/README.md) is specified alongside it, and the [peer protocol](../specifications/peer-protocol/README.md) — how two devices come to trust one another and open a session — is specified end to end: identity and pairing, the session layer, replication, verification, quotas, retention and retrieval. The [command contract](../specifications/command-contract/README.md) — the client↔service surface — has a register and version history there too, with the code as its wire truth.

Architecture documents explain *why*; the specification says *what bytes*. Where they disagree about format, the specification wins.

## Security

- [Threat model](threat-model.md) — trust boundaries, threats in scope, residual leaks, and what backup software cannot solve

## Review

- [Architecture review, August 2026](review/2026-08-architecture-review.md) — 6 critical, 7 high, 8 medium findings against the original proposal
- [Pressure test, August 2026](review/2026-08-fix-pressure-test.md) — the six fixes read back as an implementation contract: 3 critical, 7 high, 5 medium. No fix reversed; two were unsound as written
- [Prior-art learnings, August 2026](review/2026-08-prior-art-learnings.md) — another engine's fifteen years of field defects read as a test-design input: 14 themes, 22 tests to add, one gap serious enough to fix before Phase 2 closes
- [Prior-art release notes, August 2026](review/2026-08-prior-art-release-notes.md) — the same engine's *shipped fixes* rather than its bug reports, which is a verdict rather than a report: 11 classes, of which 4 were live defects here — a portless IPv6 endpoint accepted, a newline in a filename escaping an exclude rule, a metadata-only change discarded, and the locale nothing was keeping invariant
- [Prior-art changelog ledger, August 2026](review/2026-08-prior-art-changelog-ledger.md) — the corpus drained properly: all 805 distinct fix entries from `changelog.txt`, Nov 2015 to Aug 2026, dispositioned. Supersedes the release-notes review's claim to completeness, which had read 12 of ~28 pages and cited 21 notes — 2.6%. 34 engine mechanisms; 21 proven, 10 open, a 29-entry compaction cluster deferred to phase 4
- [Pipeline integrity review, August 2026](review/2026-08-pipeline-integrity-review.md) — the first pass to read the built pipeline rather than its documents: 1 critical, 2 high, 4 medium, every graded finding fixed test-first in the same pass; 5 candidates cleared and recorded; the test infrastructure it did not invent is itemised in the [phase-2 pickup list](phase-2-execution-plan.md#where-to-pick-up)
- [Restore pipeline review, August 2026](review/2026-08-restore-pipeline-review.md) — the same treatment for the read path: 1 critical (the CLI's uncontained restore), 3 high, 3 medium, all fixed test-first, plus a repair to the coverage audit that had been blind since the MSTest move and a sparse-restore [open question](open-questions.md#q22--sparse-restore-materialises-zeroes)
- [Original proposal](review/2026-08-original-proposal.md) — superseded; preserved in substance, with third-party product names generalised per [naming and attribution](naming-and-attribution.md)

---

## The six critical findings

The review found six places where the original proposal contradicted itself. Each would have surfaced as data loss or a cryptographic failure months into implementation, and each is cheap to fix on paper and expensive to fix once real repositories exist. In short:

| | Finding | Fix |
|---|---------|-----|
| **C1** | Immutable manifests embedded physical blob locations that compaction changes | Manifests carry logical object identifiers only; the index owns location |
| **C2** | Nonce uniqueness was required but never constructed | Per-blob key derivation; record ordinal as nonce |
| **C3** | Cross-device deduplication had no integrity guard | Dedup trust domains with verify-on-reuse (default changed to `repository` by [PT-11](review/2026-08-fix-pressure-test.md#pt-11--the-stated-rationale-for-the-device-dedup-default-does-not-distinguish-it-from-repository)) |
| **C4** | GC could delete blobs belonging to an in-flight snapshot | Write-intent journal records; leases demoted to advisory |
| **C5** | One offline destination stalled all protection | Commit is per-replica; replication is separate state |
| **C6** | Checkpoint compaction needed a listing the design forbade relying on | Per-writer delta chains; checkpoints enumerate what they subsume |

Full analysis, including the original wording of everything that changed, is in the [review](review/2026-08-architecture-review.md).

## Then the fixes were pressure-tested

Those six fixes were written quickly, by one author, and three of them touch the same objects during maintenance — so they were read back with the same scepticism. All six held directionally and **none was reversed**, but two were unsound as written:

| | Finding | Fix |
|---|---------|-----|
| **PT-1** | C2's resume guarantee assumed recompression is bit-reproducible; a crash, an upgrade, and a resume would reuse a nonce with different plaintext — the exact catastrophe C2 prevents | Spool checkpoint stores sealed record bytes, not a plaintext offset |
| **PT-2** | C6's merge rule rested on commutativity, which C1 made false: compaction remaps an object identifier, and order then decides | Explicit generation precedence; relocations typed as supersessions |
| **PT-3** | The garbage collector creates blobs during compaction and published no intent for them, so a second collector could delete them | The collector is a writer — any component creating a blob publishes an intent first |

Plus seven high and five medium findings, and one decision reopened for the maintainer ([Q11](open-questions.md#q11--physical-hints-in-segment-references)). Full analysis in the [pressure test](review/2026-08-fix-pressure-test.md).

## Conventions

- **Normative terminology** is defined in [01 — Domain model](architecture/01-domain-model.md). The nouns are *segment* and *blob* — never *chunk*, *block*, or *pack*. *Packing* remains fine as a verb.
- **No third-party product is named** anywhere in this repository — see [naming and attribution](naming-and-attribution.md) for the vocabulary used instead, and for why an argument that rests on a mechanism beats one that rests on a brand.
- **Requirement IDs** are stable. Changed and new requirements are marked, and original wording is quoted in the review.
- **ADR status** is `Proposed` until explicitly accepted. "Accepted (amended)" means the decision stands and the amendment is already applied — not that it is pending.
- Documents cross-reference by relative link; every link resolves.

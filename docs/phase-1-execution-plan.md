# Phase 1 — Execution plan

**Status:** in progress — push 1 · **Scope:** [Phase 1](roadmap.md#phase-1--snapshot-and-local-repository-mvp) — snapshot and local repository MVP · **Predecessor:** [Phase 0 plan](phase-0-execution-plan.md) (implemented)

---

## What Phase 1 is for

Turn the proven engine into a backup tool: capture a real filesystem tree — metadata, symlinks, hardlinks, sparse files, failures and all — publish it as an immutable snapshot, and restore it reliably, from a working machine or a clean one holding nothing but the repository and a recovery kit.

Phase 0 proved the vertical slice on synthetic single-file inputs. Every format codec Phase 1 needs already exists and is conformance-tested; what Phase 1 builds is the **writer side** — the scanner, the multi-file publication path, the planner — and the recovery story around it.

## Two pushes

Phase 1 lands in two deliberate pushes (decision recorded at planning):

- **Push 1 — core**: everything a user needs for real backups. Waves G (decisions on paper), S (scanner), T (multi-file publication + incremental), C (CLI + local state + recovery kit), R (restore planning), V (proof).
- **Push 2 — services**: OpenTelemetry instrumentation, the Agent service, scheduling, and the user-level status model. Push 2 starts with its own ADR pinning the shapes the architecture survey found unspecified: schedule semantics and missed-run behaviour, the job-state store (durable local state vs rebuildable), and the OpenTelemetry instrument names, units, and privacy-bounded attribute sets (architecture 10 §2, NFR-PRIV-002).

Cross-platform posture (decision recorded at planning): the metadata matrix is implemented **in full now**, behind platform-specific code paths; Linux paths are tested in the development environment, Windows and macOS paths are proven by the three-platform CI matrix. Windows/macOS defects surfacing in CI are expected and fixed forward.

## What already exists

| Prerequisite | State |
|---|---|
| Engine: segmentation (both profiles), records, blobs, spool resume, index plane, journal, catalogue, forensic rebuild, verify/restore engines | Phase 0, all exit criteria traced to tests |
| Format capacity for everything Phase 1 writes | `EntryMetadata` keys 1–10, file-version keys 1–13, tree + chain sharding, error manifest, policy rules — codecs conformance-tested, **no production writer yet** |
| Rule dialect | rules-v1 ([ADR-0024](adr/0024-include-exclude-rule-dialect.md)), `PathRuleSet` in Domain, dual-language vectors |
| Licence | dual AGPL-3.0-only + commercial; `specifications/` Apache-2.0 ([ADR-0001](adr/0001-licence-and-contribution-model.md)) |
| Recovery-kit decision | [ADR-0013](adr/0013-recovery-kit.md) pins contents and representations; **wire format unspecified** — wave G item G2 |

---

## Waves

```text
G · Decisions on paper ──▶ S · Scanner ──▶ T · Publication ──▶ C · CLI + recovery ──▶ R · Restore planning ──▶ V · Proof
```

### Wave G — Decisions before bytes

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| G1 | ADR-0026: phase-1 format-surface pins — `hardlink_group` derivation, `capture_diagnostics` vocabulary (incl. captured-inconsistent), `capture_status` triggers, special-file representation, alternate-stream `object_id` meaning, directory tree-entry semantics, `source_filesystem` capability keys, catalogue casefold key, verification-outcome durability posture | Survey gaps 4–10, 14, 17 | Every shape the scanner will write has normative text + erratum before wave S starts |
| G2 | Recovery-kit specification: `specifications/recovery-kit/` — CBOR body, transcribable text form, checksums, QR parameters; `recovery-kit.json` vectors; fixture kit for `fixture-repository-v1` | Survey gap 1; FR-KIT-001..003 | An independent implementation can parse and produce kits from the spec + vectors alone |
| G3 | Restore receipt and exportable-plan schemas (versioned JSON, client-domain) | Survey gaps 2–3; FR-RST-004 | Receipt accounts for every planned file; schema versioned from day one |

### Wave S — Scanner

| # | Item | Spec / Arch | Acceptance |
|---|------|-------------|-----------|
| S1 | `FallbackPlan.Filesystem` contracts: `IFileSystemSource`, `ScanEntry`, capability probe, `ScanOptions` | arch 11 §1, §6 | Contracts platform-free; architecture-test rule added |
| S2 | `FallbackPlan.Filesystem.Local`: cross-platform enumeration — identity (device+inode / FileId), metadata matrix capture, xattrs/ADS/security descriptors per platform, sparse extents, symlink no-follow, mount boundaries, hardlink detection | arch 06 §1–§3 | Temp-tree round trips on Linux; Windows/macOS paths CI-proven |
| S3 | Rules + revalidation: `PathRuleSet` wiring (validate before capture), pre/post stat revalidation with one re-queue, error collection | 06 §7.1; arch 06 §1, §6 | Excluded ≠ failed; mid-scan mutation lands in error manifest or diagnostics per ADR-0026 |

### Wave T — Multi-file publication and incremental backup

| # | Item | Spec / Arch | Acceptance |
|---|------|-------------|-----------|
| T1 | `SnapshotJob` + orchestrator generalization: per-file archive loop, bottom-up byte-sorted tree writer with 16 MiB chain sharding, per-directory metadata, symlink/hardlink/sparse/special emission, populated policy rules, error manifest, probed `source_filesystem`, `capture_status` logic, `parent_snapshots` | 06 §4–§9; arch 04 §5 | A directory tree publishes through the nine steps; every manifest field the scanner captured survives a restore round trip |
| T2 | Catalogue v2 + incremental: path tables (casefold key), snapshot/file queries, live writes from the orchestrator, unchanged-file short-circuit (identity+size+mtime), changed-file segment reuse via stored content ids; rebuild parity | arch 06 §4; NFR-PERF-003; FR-MAN-005/006 | Second backup of an unchanged tree reads ≤ 1 % of logical bytes; `ls` works after catalogue rebuild |

### Wave C — CLI, local state, recovery kit

| # | Item | Spec / Arch | Acceptance |
|---|------|-------------|-----------|
| C1 | CLI v2: `backup`, `snapshots`, `ls`, `restore`, `check`; configuration store (schema-versioned, validated, named-field rejection, secret-free export); three-way local state separation; inspector growth | arch 11 §3; NFR-OPS-003 | `LocalStateSeparationTests`: deleting the catalogue never touches device identity or configuration |
| C2 | `key export` + recovery kit implementation + `FallbackPlan.Recovery` standalone tool (format/crypto/packing/index/storage deps only) | ADR-0013; arch 08 §4–§5; FR-KIT-001..006 | Clean-machine drill: restore using only store + kit; transcription errors detected by checksum |

### Wave R — Restore planning

| # | Item | Spec / Arch | Acceptance |
|---|------|-------------|-----------|
| R1 | `FallbackPlan.Restore`: plan before transfer (conflicts incl. case collisions, metadata degradation per target, space, privileges), quarantine default, metadata-after-content, machine-readable receipt, never-partial-success | arch 08 §2–§3; FR-RST-001..006 | 9 999 of 10 000 files is a failed restore, and the receipt says which one |
| R2 | Partial-rebuild restore: targeted forensic scan wired to `restore` | 07 §10; FR-MAN-010; NFR-PERF-015 | Restore of a named file begins without a full-repository scan when every index object is deleted |

### Wave V — Proof

| # | Item | Requirements | Acceptance |
|---|------|--------------|-----------|
| V1 | Multi-file interruption matrix over `SnapshotJob` | NFR-REL-001 | Every 04 §5.1 row holds for tree publication; committed snapshots stay restorable |
| V2 | NFR-PERF-004 path-lookup measurement (reduced scale, honestly labelled) | NFR-PERF-004 | Numbers published beside the phase-0 benchmarks with the same caveat discipline |
| V3 | Fixture coverage for exit criterion 7; multi-file fixture only if needed (v1 stays frozen) | NFR-COMP-004 | The criterion's object list is demonstrably covered by committed fixtures |
| V4 | Clean-machine recovery end to end; three-OS CI green | NFR-OPS-005 | The drill runs with no catalogue and no durable local state |

---

## Exit-criteria coverage (push 1 unless noted)

Push 1 is complete; each criterion now names the test that proves it.

| # | Exit criterion | Proven by |
|---|----------------|-----------|
| 1 | Cross-platform backup and point-in-time restore | `LocalTreeBackupTests`, `SnapshotPublicationTests`, `RestorePlanTests`, CLI `backup`/`restore` — cross-platform via the CI matrix |
| 2 | Path and version lookups meet NFR-PERF-004 | `IncrementalBackupTests` + the `pathlookup` measurement ([phase-1-benchmarks.md](phase-1-benchmarks.md): ~29 µs lookup, ~0.28 ms listing at 100k files) |
| 3 | Interruption testing at every publication boundary | `TreeSnapshotInterruptionTests` (five kill points over `SnapshotJob`) + the phase-0 `PublicationInterruptionTests` matrix |
| 4 | Complete rebuild without the local database | `IncrementalBackupTests.A_rebuilt_catalogue_answers_the_same_queries…` (E1 + `CatalogueProjector`), `ForensicRebuildTests` path tables |
| 5 | Restore begins during partial rebuild | `RestorePlanTests.A_targeted_forensic_rebuild_is_enough_to_restore_that_snapshot` |
| 6 | Clean-machine recovery using only repository plus kit | `KitDrillTests` (store + kit + passphrase only; text round trip; per-line transcription check) + the live CLI→`fallbackplan-recover` drill |
| 7 | Public conformance fixtures cover blobs, records, manifests, indexes, and snapshots | `fixture-repository-v1` (blobs, records, manifests, index delta, journal, snapshot, key object, descriptor) + the committed kit fixtures — asserted by the conformance fixture tests; the v1 bytes stayed frozen through the whole phase |

Push 2 owes no exit criterion; it owes the roadmap features "OpenTelemetry instrumentation · Agent service and basic scheduling" and the status model of architecture 10 §1.

---

## Push 2 — waves

Shapes pinned by [ADR-0027](adr/0027-services-scheduling-status-telemetry.md) before any service byte: schedule semantics with missed-run coalescing, the job-state store's place in the 11 §3 split, the instrument surface with its NFR-PRIV-002 attribute allowlist, and the phase-1 status derivation.

| # | Item | Decision / Arch | Acceptance |
|---|------|-----------------|-----------|
| P2-A | `FallbackPlan.Application`: client configuration and durable local state move from the CLI (CLI behaviour unchanged); `Schedule` (pure next-run arithmetic, missed runs coalesce to one), `JobStateStore` (10 §3 machine, sacrificial by design), status model (10 §1.1 vocabulary, never-merge rules, failure-domain check for `protected`) | ADR-0027 §1, §2, §4 | Schedule arithmetic tested without a clock; deleting `jobs.json` costs history, never identity or correctness; same-device store is never `protected` |
| P2-O | Engine instrumentation: `FallbackPlan.Engine` meter + activity source, the ADR-0027 §3 instrument table, wired through archive/publication/catalogue/store paths | ADR-0027 §3; NFR-OPS-001 | An automated listener asserts every emitted attribute against the allowlist — no path, name, or identifier can ride telemetry (NFR-PRIV-002) |
| P2-H | `FallbackPlan.Agent` host: config-driven loop over backup sets, one run per set, catch-up on start, job-state journal, status output; CLI `status` command reading the same derivation | ADR-0027 §1–§4 | An in-process agent pass runs a due set end to end and skips a not-due set; status output distinguishes `captured` from `protected` |

## Standing constraints

Phase-0 rules carry forward unchanged: implement against the specification; every spec gap is a flagged decision (ADR/erratum/open question), never a silent choice; the spool checkpoint stores sealed bytes; every blob is intent-covered before upload; warnings are errors; vectors and fixtures regenerate byte-identically; *segment* and *blob*, never *chunk*. New for Phase 1: **capture is lossless** (degradation happens at restore, recorded in the receipt, never at backup — arch 06 §3), and **excluded ≠ failed** (policy manifest vs error manifest — 06 §8).

# Phase 0 — Execution plan

**Status:** implemented — all waves A–F complete; every item below is delivered and its acceptance criterion backed by a named test (benchmarks published at reduced scale, [`phase-0-benchmarks.md`](phase-0-benchmarks.md)) · **Scope:** [Phase 0](roadmap.md#phase-0--archive-engine-vertical-slice) — archive engine vertical slice

---

## What Phase 0 is for

Prove that files can be segmented, compared, encrypted, packed, indexed, rebuilt, and restored — with no filesystem scanner, no peer protocol, no cloud provider, and no UI above it.

If the engine is wrong, every later phase inherits the defect and the format cannot be changed once real data exists in it. Everything else waits.

## What already exists

| Prerequisite | State |
|---|---|
| Architecture, reviewed twice | [`docs/architecture/`](architecture/) |
| Requirements with acceptance criteria | 142 IDs, fully traced |
| Decisions | 20 ADRs; 10 accepted, the rest proposed *(at the time; 30 records now, 25 accepted)* |
| **Normative format specification** | [`specifications/repository-format/`](../specifications/repository-format/README.md) |
| **Conformance vectors** | Identifiers, keys, record and footer AAD, segmentation, compression, AES-GCM and Argon2id known-answer tests |
| **Solution scaffold** | 12 src + 8 test projects, building clean with warnings-as-errors |
| **CI** | Build and test on three platforms; vector reproducibility; documentation integrity |

Work items below reference specification sections. **Implement against the specification, not against the architecture documents** — the architecture explains *why*, the specification says *what bytes*.

---

## Waves

Ordered by real dependency rather than by backlog position. Items within a wave can proceed in parallel.

```text
A · Foundations ──▶ B · Record path ──▶ C · Container ──▶ D · Metadata ──▶ E · Recovery ──▶ F · Proof
```

### Wave A — Foundations

| # | Item | Spec | Requirements | Acceptance |
|---|------|------|--------------|-----------|
| A1 | Domain primitives: identifiers, profiles, generations, sizes | [02](../specifications/repository-format/02-identifiers.md), [00 §3](../specifications/repository-format/00-conventions.md#3-profiles) | — | Types are immutable; an invalid profile cannot be constructed |
| A2 | Canonical CBOR encode/decode with **strict** determinism enforcement | [00 §4](../specifications/repository-format/00-conventions.md#4-cbor-encoding) | NFR-PORT-003, NFR-COMP-004 | Round-trips byte-identically; **rejects** non-canonical input — indefinite lengths, non-shortest integers, unsorted or duplicate keys |
| A3 | Content and object identifiers | [02](../specifications/repository-format/02-identifiers.md) | FR-ARCH-003, NFR-SEC-004 | Matches `identifiers.json` exactly |
| A4 | Key hierarchy: Argon2id, wrapping, HKDF derivation | [03](../specifications/repository-format/03-keys.md) | FR-ARCH-008, NFR-SEC-008 | Matches `keys.json` and `argon2id.json`; two writers with an identical CSPRNG stream derive distinct blob keys; **an empty passphrase is rejected at repository creation** ([03 §2.1](../specifications/repository-format/03-keys.md#21-the-passphrase-is-constrained-too-and-the-primitive-will-not-do-it-for-you) — the primitive accepts one, so refusing is the engine's job and untestable until the engine exists) |
| A5 | Configuration schemas, validated before use | [06 §7](../specifications/repository-format/06-manifests.md#7-policy-manifest) | FR-ARCH-007, NFR-OPS-003 | A profile exceeding a provider limit is rejected **at configuration time** with a named reason |
| A6 | `IObjectStore` and the local filesystem provider | [`05-storage-providers.md` §2](architecture/05-storage-providers.md#2-the-store-interface) | FR-REP-002, NFR-PORT-004 | Content supplied as a re-openable factory; expected outcomes are results, not exceptions |

> **A2 is the one to get right.** Object identifiers derive from encoded bytes, so a lenient decoder silently permits two encodings of the same object — and deduplication, verification, and the entire conformance suite quietly stop meaning anything. Write the rejection tests before the encoder.

### Wave B — The record path

| # | Item | Spec | Requirements | Acceptance |
|---|------|------|--------------|-----------|
| B1 | Streaming `fixed-v1` segmenter with sparse extents | [09 §2](../specifications/repository-format/09-segmentation.md#2-fixed-v1), [§4](../specifications/repository-format/09-segmentation.md#4-sparse-extents) | FR-ARCH-001, FR-ARCH-013 | Matches `segmentation.json`; a 2 TiB file stays within the NFR-PERF-001 bound |
| B2 | Segment hashing pipelined with reading | [02 §2](../specifications/repository-format/02-identifiers.md#2-content-identifier) | FR-ARCH-003 | Hardware-accelerated where available |
| B3 | Prior-version positional comparison and reuse | [09 §6](../specifications/repository-format/09-segmentation.md#6-capture-algorithm) | FR-ARCH-004 | Changing one segment of an *n*-segment file writes exactly one record |
| B4 | Compression with the storage threshold | [10](../specifications/repository-format/10-compression.md) | FR-ARCH-005 | Matches `compression.json`; the choice is recorded per record |
| B5 | AEAD record framing, nonce and AAD | [04](../specifications/repository-format/04-record.md) | FR-ARCH-009, NFR-SEC-003 | AAD matches `records.json` and is exactly 55 bytes; a record moved between ordinals or repositories fails authentication |
| B6 | `cdc-v1`: **pin the Rabin polynomial and table**, then implement | [09 §3](../specifications/repository-format/09-segmentation.md#3-cdc-v1) | FR-ARCH-014 | Parameters committed to `segmentation.json`; boundaries reproducible across implementations |

> **B6's prerequisite is met.** The rolling-hash polynomial and per-byte tables are pinned by [ADR-0023](adr/0023-cdc-v1-rabin-parameters.md) and committed to `segmentation.json` — polynomial, derivation rule, tables, and computed boundary cases, including the insertion-resynchronisation property asserted rather than assumed.

### Wave C — The container

| # | Item | Spec | Requirements | Acceptance |
|---|------|------|--------------|-----------|
| C1 | Blob spool with a **sealed-bytes** checkpoint | [05 §6](../specifications/repository-format/05-blob.md#6-the-spool) | FR-ARCH-011 | Resume re-emits stored bytes; **a codec version change forces restart, never recompression** |
| C2 | Blob writer, sealing, authenticated recovery footer, locator | [05 §2–5](../specifications/repository-format/05-blob.md#2-cleartext-envelope) | FR-ARCH-012, FR-MAN-007 | Every record locatable and verifiable from the blob and keys alone |
| C3 | Blob reader with ranged access | [05 §4](../specifications/repository-format/05-blob.md#4-footer-locator) | FR-ARCH-006 | Footer reachable in two range reads |
| C4 | Writer-allocated blob identifiers and store-key derivation | [02 §4](../specifications/repository-format/02-identifiers.md#4-blob-identifier) | FR-SNP-006 | Allocatable before the blob is sealed; store key reveals no writer identity |

> **C1 is the highest-risk item in Phase 0.** Build the checkpoint to store sealed bytes in the *first* version. Retrofitting it means shipping a window in which crash-plus-upgrade produces nonce reuse — the exact failure the construction exists to prevent ([PT-1](review/2026-08-fix-pressure-test.md#pt-1--c2s-resume-guarantee-silently-assumes-bit-reproducible-compression)).

### Wave D — Metadata

| # | Item | Spec | Requirements | Acceptance |
|---|------|------|--------------|-----------|
| D1 | File-version manifests with **logical-only** segment references | [06 §3–4](../specifications/repository-format/06-manifests.md#3-segment-references) | FR-ARCH-010, FR-MAN-003 | A manifest containing a blob ID or physical offset **fails format validation** |
| D2 | SQLite catalogue with the required indexes | [`02-repository-format.md` §8](architecture/02-repository-format.md#8-catalogue-rebuild) | FR-MAN-002, FR-MAN-005, FR-MAN-006 | Meets NFR-PERF-004 and NFR-PERF-010; disposable |
| D3 | Index deltas with per-writer chains and void deltas | [07 §2, §4](../specifications/repository-format/07-index.md#2-index-delta) | FR-MAN-008, FR-MAN-016 | A missing sequence is detected, not silently tolerated |
| D4 | Checkpoints, precedence, supersession | [07 §3, §5–7](../specifications/repository-format/07-index.md#3-precedence) | FR-MAN-013, FR-MAN-015, FR-MAN-017 | Applying entries in any order converges; a post-compaction reader never resolves to a tombstoned blob |
| D5 | Write-intent journal records | [08](../specifications/repository-format/08-journal.md) | FR-SNP-004, FR-SNP-005 | No blob uploaded without covering intent; expiry requires **both** generation and duration |
| D6 | Publication ordering | [08 §10](../specifications/repository-format/08-journal.md#10-publication-order) | FR-SNP-001 | Every object a published object references is already durable |

> **D5 before C-wave upload code goes live.** If the blob writer ships without intents and they arrive later, the interruption harness passes for the wrong reason — nothing was concurrently collecting, so nothing was at risk ([PT-3](review/2026-08-fix-pressure-test.md#pt-3--compaction-output-blobs-are-unprotected-between-creation-and-index-publication)).

### Wave E — Recovery

| # | Item | Spec | Requirements | Acceptance |
|---|------|------|--------------|-----------|
| E1 | Normal catalogue rebuild from checkpoint plus deltas | [07 §5](../specifications/repository-format/07-index.md#5-checkpoint) | FR-MAN-009 | Delete the catalogue, rebuild, restore successfully |
| E2 | Forensic rebuild from blob footers, **targetable** | [07 §10](../specifications/repository-format/07-index.md#10-rebuilding-without-the-index) | FR-MAN-009, NFR-PERF-015 | Succeeds with every index object deleted; a single named file recovers without a whole-repository scan |
| E3 | Rebuild verification and damage reporting | [`02-repository-format.md` §8.3](architecture/02-repository-format.md#83-rebuild-never-repairs) | FR-MAN-011, FR-MAN-012, FR-MAN-014 | Each fault class distinctly named; **rebuild leaves every object byte-identical** |
| E4 | File restore with per-segment and whole-file verification | [04 §6](../specifications/repository-format/04-record.md#6-reading-a-record), [06 §4.2](../specifications/repository-format/06-manifests.md#42-the-whole-file-hash) | FR-RST-002, FR-RST-005 | Verifies the plaintext hash after decryption, not merely the AEAD tag; never reports partial success |

### Wave F — Proof

| # | Item | Requirements | Acceptance |
|---|------|--------------|-----------|
| F1 | Interruption harness at every persistence boundary | NFR-REL-001, NFR-REL-005 | Kill at each step in [08 §10](../specifications/repository-format/08-journal.md#10-publication-order); every published snapshot stays readable |
| F2 | Corruption harness | NFR-REL-004, FR-MAN-011 | Bit flips, truncation, missing blobs, forged identifiers, replayed snapshots — each detected and scoped |
| F3 | Fuzzing of every binary parser | NFR-PORT-003 | No crash, no unbounded allocation, on any input |
| F4 | Benchmarks against the stated targets | NFR-PERF-001..015 | Published numbers; **targets revised with the revision recorded**, per [Q7](open-questions.md#q7--performance-targets). First round published at reduced scale in [`phase-0-benchmarks.md`](phase-0-benchmarks.md) — the scale caveats are part of the publication |
| F5 | Low-level CLI | — | `archive`, `inspect-blob`, `inspect-manifest`, `rebuild-index`, `verify`, `restore-file` |
| F6 | Synthetic legacy source adapter | FR-CP-002 | An arbitrary byte stream plus a provenance record traverses the same pipeline |
| F7 | Fixture repositories | NFR-COMP-004 | Committed under `conformance/fixtures/`; synthetic only |

---

## Exit-criteria coverage

Every one of the 11 criteria in [Phase 0](roadmap.md#phase-0--archive-engine-vertical-slice), mapped to what delivers it. A criterion with nothing behind it is visible at a glance.

| # | Exit criterion | Delivered by | Proof (named test) |
|---|----------------|--------------|--------------------|
| 1 | Multi-terabyte file within the memory bound, spanning many blobs | B1, C2, F4 | `MemoryBoundProof` — live set flat from 1 GiB to 3 GiB; the multi-TB run itself is future work, recorded in [`phase-0-benchmarks.md`](phase-0-benchmarks.md) §1 |
| 2 | Changing one segment writes exactly one record plus a manifest | B3, D1 | `SecondBackupTests`, `CdcSecondBackupTests` |
| 3 | Blob targets respected without splitting records | C2 | `BlobWriterTests` (rotation at target, refusal to split) |
| 4 | Every segment and restored file cryptographically verified | B5, E4 | `VerifyEngine`/`RestoreEngine` paths in `ForensicRebuildTests` — incl. the swapped-segment forgery only the whole-file hash catches |
| 5 | Interruption cannot expose an incomplete committed file version | C1, D6, F1 | `PublicationInterruptionTests` — every 04 §5.1 row, committed snapshots stay restorable |
| 6 | Resume byte-identical; restart draws a different salt | C1, F1 | `SpoolCheckpointTests` |
| 7 | Catalogue deletable and rebuildable from checkpoint plus deltas | D2, E1 | `CatalogueRebuildTests` |
| 8 | Forensic rebuild succeeds with all index objects removed | C2, E2 | `ForensicRebuildTests` |
| 9 | Compaction relocates records without modifying any manifest | D1, D4 | `ManifestCodecTests` (nothing physical decodes), `IndexPrecedenceTests` (supersession converges) |
| 10 | GC concurrent with a backup deletes no intent-covered blob | D5, F1 | `ConcurrentCollectionTests` |
| 11 | Synthetic legacy adapter traverses the same pipeline | F6 | `LegacyImportTests` — stripped of provenance, the imported manifest is byte-identical to a native one |

---

## Standing constraints

Four rules that apply to every item, each closing a failure already documented in [`docs/review/`](review/). They look like details and are not.

1. **The spool checkpoint stores sealed bytes.** Never a plaintext offset to recompute from. First version, not retrofitted. → [PT-1](review/2026-08-fix-pressure-test.md#pt-1--c2s-resume-guarantee-silently-assumes-bit-reproducible-compression)
2. **Any component that creates a blob publishes an intent first** — including the collector. No exception for maintenance. → [PT-3](review/2026-08-fix-pressure-test.md#pt-3--compaction-output-blobs-are-unprotected-between-creation-and-index-publication)
3. **Manifests never carry physical location.** A reviewer seeing a blob ID in a manifest should reject the change without further discussion. → [ADR-0007](adr/0007-logical-object-identifiers-in-manifests.md)
4. **Index entries carry generations and are not commutative.** Compaction remaps identifiers; order decides. → [ADR-0017](adr/0017-index-entry-supersession.md)

## Known unmet prerequisites

| Item | Blocked on | Effect |
|------|-----------|--------|
| ~~B6 `cdc-v1`~~ | **Resolved** — polynomial and tables pinned by [ADR-0023](adr/0023-cdc-v1-rabin-parameters.md), boundary vectors committed | The freeze-gate benchmark is unblocked |
| AEAD conformance vectors | No independent generator available | Framing is covered by `records.json` plus the independent reader. The AES-GCM known-answer tests are correctness-verified but the CAVP case's NIST provenance is **not** — see [conformance README](../specifications/repository-format/conformance/README.md) |
| ~~Ed25519 signature vectors~~ | **Resolved** — `ed25519.json` carries RFC 8032 §7.1 vectors and format-real cases, computed by a pure-Python RFC 8032 implementation in the generator ([ADR-0022](adr/0022-standalone-metadata-records-and-index-identifiers.md) Decision 8) | Snapshot signature verification is vector-tested |
| ~~XChaCha20-Poly1305 cross-verification~~ | **Resolved** — no second implementation existed, so the profile was withdrawn rather than shipped unchecked ([Q12](open-questions.md#closed)) | Format v1 has one record AEAD |
| — | [ADR-0001](adr/0001-licence-and-contribution-model.md) licence | Blocks external contributions and the freeze gate. **Does not block any item above.** |

## What Phase 0 deliberately excludes

Filesystem scanner and include/exclude rules (Phase 1) · tree and snapshot manifests beyond what the engine needs (Phase 1) · peer protocol and replication (Phase 2) · cloud providers (Phase 3) · retention and garbage collection *execution* — the write-intent side is Phase 0 because the engine cannot be correct without it (Phase 4) · the CrashPlan reader (Phase 5).

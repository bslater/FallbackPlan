# Open questions

**Status:** live document

Decisions that are deliberately unresolved, who owns them, and what they block. An ADR in `Proposed` state carries the analysis; this page tracks the ones still needing a human answer.

---

## Q1 — Project licence

**Owner:** project maintainer · **Blocks:** first public release, any CrashPlan code reuse, external contributions · **ADR:** [0001](adr/0001-licence-and-contribution-model.md)

The document describes an open-source project and never names a licence. The choice has a consequence beyond the usual, because it determines whether GPL-licensed prior art — potentially including CrashPlan reader implementations — could ever be reused.

| Option | Consequence |
|--------|-------------|
| Apache-2.0 | Permissive, explicit patent grant. Best for third-party readers and distro packaging. **Cannot absorb GPL code** — the importer would need clean-room implementation. |
| MPL-2.0 | File-level copyleft. Combinable with proprietary code. Still cannot absorb GPL code. |
| GPL-3.0 / AGPL-3.0 | Strong copyleft. **Could reuse GPL reader code directly.** Deters some commercial adoption and embedding. |

**Recommendation:** Apache-2.0, with the CrashPlan importer clean-roomed. The format's value depends on independent implementations existing, and a patent grant plus permissive terms maximises that. The importer is isolated in its own package precisely so this choice does not have to be made to suit it ([`architecture/11-solution-structure.md` §4](architecture/11-solution-structure.md#4-import-isolation)).

**Not decided.** Recorded as open at the maintainer's direction.

---

## Q2 — Plan C licence and reuse posture

**Owner:** project maintainer, with legal review · **Blocks:** Phase 5 · **ADR:** [0015](adr/0015-crashplan-importer-isolation.md)

The original proposal cites Plan C as evidence that CrashPlan archives can be read, and instructs that its licence be reviewed before any reuse. **That licence has not been verified** — it could not be checked from the environment this review was produced in, and it is not asserted anywhere in this document set.

Required before any parser work:

1. Verify Plan C's licence and its compatibility with Q1's answer.
2. Decide reuse posture: direct reuse (if compatible), documentation-only reference, or full clean-room with an independent implementer who has not read the source.
3. Confirm the interoperability position for reverse engineering in the target jurisdictions.

**Constraint regardless of the answer:** no parser work begins before this gate passes. Reading source under an incompatible licence contaminates the clean-room option permanently, so the sequence matters more than the speed.

---

## Q3 — Product name and trademark

**Owner:** project maintainer, with legal review · **Blocks:** first public release · **Review finding:** [M7](review/2026-08-architecture-review.md#m7--naming-proximity-to-crashplan-carries-trademark-risk)

"FallbackPlan" shares a structure, a domain, and a rhyme with "CrashPlan", and the project's most visible advertised capability is reading CrashPlan archives. That combination is what makes a confusion argument easy to state.

This is a flag rather than a legal opinion. It should be assessed alongside Q2 — the same review, the same lawyer — while renaming is still cheap. It becomes expensive the moment a repository format, a wire protocol, and a package name carry it.

---

## Q4 — Canonical metadata encoding

**Owner:** engineering · **Blocks:** format v1 freeze · **ADR:** [0003](adr/0003-canonical-metadata-encoding.md)

Canonical CBOR is the candidate. Confirm with cross-language determinism tests and an encoding-size benchmark on realistic manifests before the format freezes. The requirement it has to satisfy is that an independent implementer, in another language, produces byte-identical output from the same logical input.

---

## Q5 — Segmentation default

**Owner:** engineering · **Blocks:** format v1 freeze · **ADR:** [0002](adr/0002-segmentation-strategy.md)

`fixed-v1` is the v1 default and `cdc-v1` is specified alongside it. The corpus benchmark decides whether the default changes. Deferring this past the freeze — as originally planned — would have meant discovering the answer after users had committed data ([H1](review/2026-08-architecture-review.md#h1--fixed-size-segmentation-is-under-argued-and-its-review-is-scheduled-after-the-point-of-no-return)).

Needed: a corpus that actually represents the target workloads — documents, photographs, VM images, source trees, mailboxes — rather than synthetic data, which flatters CDC and fixed-size in different and misleading ways.

---

## Q6 — Segment hash function

**Owner:** engineering · **Blocks:** format v1 freeze · **ADR:** [0004](adr/0004-segment-hash-function.md)

SHA-256 in-box versus a BLAKE3 native binding. The trade is throughput against NFR-PORT-001 — a native binding adds a platform-specific dependency to the one component that must run everywhere, including the standalone recovery tool. Recommendation is SHA-256 as the default with the profile field allowing another later; confirm against benchmark.

---

## Q7 — Performance targets

**Owner:** engineering · **Blocks:** nothing; revised continuously

The targets in [`requirements/non-functional.md`](requirements/non-functional.md) are initial and some will be wrong. Revise after Phase 0 benchmarks, and **record each revision** rather than silently editing — a target that quietly moves to meet the measurement is not a target.

---

## Q8 — Reference-machine definition

**Owner:** engineering · **Blocks:** benchmark comparability

The reference machine in [`requirements/non-functional.md`](requirements/non-functional.md#reference-machine) is a starting point. It needs to be pinned to something reproducible — a specific CI runner class or documented hardware — before published numbers mean anything across time.

---

## Q9 — Repository-server scope

**Owner:** product · **Blocks:** Phase 2 design detail

Repository-server mode is described as an ownership model but its administrative surface is undefined: multi-user households, quota delegation, policy locks, and per-device grants. Enough is specified for the format to be correct; the product surface is not.

---

## Q10 — Padding policy

**Owner:** engineering, with security review · **Blocks:** nothing in v1

[`threat-model.md` T-11](threat-model.md#t-11-metadata-side-channels) proposes optional record padding for high-sensitivity backup sets. Bucket granularity, storage cost, and whether it is per-set or per-repository are undecided. It is optional in v1, so this can wait — but the format should be checked to confirm it can express padding without a version bump.

---

## Q11 — Physical hints in segment references

**Owner:** project maintainer · **Blocks:** format v1 freeze · **ADR:** [0007](adr/0007-logical-object-identifiers-in-manifests.md) · **Finding:** [PT-10](review/2026-08-fix-pressure-test.md#pt-10--emergency-single-file-restore-regressed-from-one-fetch-to-a-full-scan)

Manifests carry logical object identifiers only, and that decision is confirmed. What is open is whether a segment reference should *also* carry a non-authoritative `last_known_blob` hint.

**Why it matters.** With no index, recovering a single file means scanning blob footers — hours at scale **M** for one document, against roughly one fetch if a hint were present. That is the emergency-recovery path, so it is the worst place to be slow.

**Why the original rejection does not hold.** ADR-0007 dismissed hints as "a correctness question dressed up as an optimisation". Record headers are independently authenticated and carry the object identifier, so a reader following a stale hint **detects** it and falls back to the index. Detectably stale is not silently wrong.

**What still argues against it.** It partially re-couples manifests to physical layout, and invites implementations that trust the hint without validating. The mitigation is a mandatory stale-hint conformance fixture.

Either answer preserves the core decision: because the hint may go stale, compaction still touches no manifest.

| Option | Trade |
|--------|-------|
| **Add the hint** | O(1) first-byte recovery with no index; a few bytes per segment reference; a stale-hint fixture becomes mandatory |
| **No hint** | Manifests stay purely logical; emergency single-file recovery relies on prioritised footer scanning (NFR-PERF-015) |

**Recommendation:** add it, with mandatory validation and a conformance fixture. The cost is small and bounded; the benefit lands exactly when the user is in the worst position.

---

## Closed

| Question | Resolution |
|----------|-----------|
| Do manifests carry physical locations? | No — logical object identifiers only ([ADR-0007](adr/0007-logical-object-identifiers-in-manifests.md)) |
| How is nonce uniqueness guaranteed? | Per-blob key derivation, record ordinal as nonce ([ADR-0005](adr/0005-aead-suite-and-nonce-construction.md)) |
| Is cross-device dedup safe by default? | Yes — `repository` is the default and verifies on reuse; `device` is the hardened opt-in ([ADR-0006](adr/0006-object-identifiers-and-dedup-trust-domains.md)) |
| How does GC avoid deleting in-flight blobs? | Write-intent journal records; leases are advisory ([ADR-0009](adr/0009-garbage-collection-safety.md)) |
| Does an offline destination block snapshots? | No — commit is per-replica ([ADR-0011](adr/0011-commit-versus-replication-semantics.md)) |
| How is a checkpoint conflict resolved? | Both retained, both applied, under explicit generation precedence ([ADR-0008](adr/0008-index-generations-and-checkpoints.md), [ADR-0017](adr/0017-index-entry-supersession.md)) |
| What is in the recovery kit? | Specified ([ADR-0013](adr/0013-recovery-kit.md)) |
| How are blob identifiers formed? | Writer-allocated and opaque, not content-derived ([ADR-0016](adr/0016-blob-identifier-formation.md)) |
| What happens when two index entries map one object? | Highest generation wins; relocations typed as supersessions ([ADR-0017](adr/0017-index-entry-supersession.md)) |
| Does `protected` require an offsite copy? | Yes — a replica outside the source's failure domain ([ADR-0018](adr/0018-replica-failure-domains.md)) |
| Is the local database disposable? | The catalogue is; device identity and pairings are not ([ADR-0010](adr/0010-local-store-separation.md)) |

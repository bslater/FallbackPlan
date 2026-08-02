# ADR-0015 — CrashPlan importer isolation and licensing gate

**Status:** Proposed
**Date:** 2026-08
**Requirements:** FR-CP-001..006, NFR-PORT-002
**Related:** [ADR-0001](0001-licence-and-contribution-model.md), [`../open-questions.md#q2`](../open-questions.md#q2--plan-c-licence-and-reuse-posture)

---

## Context

CrashPlan migration is a major differentiator: users with an existing archive and their key material should be able to bring their history across without first restoring every version to a plaintext directory tree.

It is also the project's largest source of legal and technical uncertainty. The archive format is proprietary and varies by product line, client version, and destination type. Community work such as Plan C demonstrates that some archives can be read, but coverage varies — and **Plan C's licence has not been verified**. It could not be checked from the environment this review was produced in, and it is asserted nowhere in this document set.

The proposal's instincts here were right: isolate the importer, review licences before reuse, prefer clean-room where provenance is uncertain, never mutate a source archive. What it lacked was a **sequencing rule**, and sequencing is what actually matters. Reading incompatibly licensed source *before* deciding the reuse posture forecloses the clean-room option permanently — a developer who has read GPL reader source cannot later be the clean-room implementer of a permissively licensed one.

## Decision

### Isolation

`FallbackPlan.Import.CrashPlan` is a **separately packaged optional component**:

- the core never references it — enforced by `ArchitectureTests`, not by convention;
- it depends on `Import.Abstractions`, which defines a neutral legacy model independent of any specific legacy format;
- its dependencies and licence obligations stay contained within it;
- it opens sources **read-only** and never mutates an archive, verified by digest before and after.

The neutral model exists so the same import pipeline later serves restic, Kopia, and Duplicati importers without any of them reaching into the core.

### Gate

> **No CrashPlan parser work begins before this gate passes.**

1. **Verify Plan C's licence** and its compatibility with the answer to [ADR-0001](0001-licence-and-contribution-model.md).
2. **Decide the reuse posture:**
   - *direct reuse* — only if licences are compatible and provenance is clear;
   - *documentation-only reference* — read for format understanding, write independently;
   - *full clean-room* — an implementer who has **not** read the source works from an independently written format description.
3. **Confirm the interoperability position** for reverse engineering in the target jurisdictions.
4. **Confirm fixture redistribution rights.** Test archives must be synthetic or explicitly redistributable; user-supplied archives are never committed.

The order is the decision. Steps 1–3 are cheap and reversible; reading source under an unresolved licence is neither.

### Correctness rules

Sources opened read-only, never repaired by default · every imported file version records source provenance and importer version · content hashed after CrashPlan decryption and before FallbackPlan encryption · comparison mode restores both source and destination streams and compares · malformed records isolated rather than aborting the import · missing blocks produce explicit incomplete-version records rather than silent truncation · path traversal and hostile metadata contained · every binary parser fuzzed · import resumable without republishing completed snapshots · never silently substitute a different version.

## Consequences

**Positive**

- The core's licence is decided on the core's merits, not to suit an importer.
- A licence problem in the importer cannot contaminate the core or block a release.
- The clean-room option stays open, because nobody has read anything yet.
- Fuzzing an isolated parser bounds the blast radius of a hostile archive ([T-15](../threat-model.md#t-15-parser-attacks-through-legacy-archives)).

**Negative**

- Clean-room implementation, if required, is materially slower than reuse.
- The gate delays Phase 5, possibly by months if legal review is slow.

**Neutral**

- CrashPlan import is **experimental** until validated against diverse real archives, and is never broadly claimed before then. A narrow, honest compatibility matrix is worth more than a broad, unreliable promise — and a migration that silently drops history is worse than one that refuses to start.

## Alternatives considered

**Importer in the core.** Rejected. Couples the project's licence to the importer's dependencies and puts a parser for hostile input inside the trust boundary.

**Begin parser work in parallel with legal review.** Rejected — this is precisely the sequencing error the gate exists to prevent.

**Skip CrashPlan import entirely.** Considered seriously: it is the highest-risk, highest-uncertainty feature in the plan. Rejected because it is a genuine differentiator and the isolation makes the risk containable. If the gate cannot be passed, dropping it costs the project nothing that was already built.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Gate must pass before any Phase 5 parser work |

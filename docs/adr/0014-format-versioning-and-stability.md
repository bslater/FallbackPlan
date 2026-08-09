# ADR-0014 — Format versioning and pre-1.0 stability posture

**Status:** Proposed · Implemented — see [implementation status](../implementation-status.md#by-decision)
**Date:** 2026-08
**Requirements:** NFR-COMP-001..004, NFR-COMP-006, NFR-COMP-007, NFR-REL-008
**Review finding:** [M6](../review/2026-08-architecture-review.md#m6--no-stability-posture-for-pre-10-repositories)

---

## Context

The proposal set out good rules for format evolution — feature advertisement rather than a single integer version, safe refusal on unknown required features, append-only upgrades where possible, resumable migration that never destroys the last readable generation.

It never said **when v1 freezes**, or what guarantee applies before it.

That gap has a concrete consequence. Early adopters will point real backups at pre-1.0 builds, and for some of them it will be the only copy. Without a stated posture the project ends up either shipping a breaking change that destroys those repositories, or frozen on a format it wanted to revise — and the choice arrives as a crisis rather than a decision.

## Decision

### Independent version axes

Repository format · blob format · record format · manifest schema · index schema · encryption profile · peer protocol · recovery-kit format · configuration schema · importer compatibility.

Each versions independently. A reader advertises a **feature set**, not a single integer, so partial capability is expressible.

### Rules

- Unknown **required** features cause safe refusal with a named reason. A reader never guesses and never partially reads.
- Unknown **optional** fields follow documented preserve-or-ignore rules.
- Upgrades are append-only where possible.
- A repository may contain multiple object generations simultaneously.
- Format migration is resumable and never destroys the last readable generation.
- Recovery tooling for every supported major format remains downloadable and buildable from published source.
- Deprecation requires a published migration path and a support window.

### Pre-1.0 posture

> **Repositories created by pre-1.0 builds carry no forward-compatibility guarantee.**

- Builds **warn at repository creation** that the format is unstable and the repository may not be readable by a later build.
- The format version is always recorded, so a build **refuses** a repository it cannot read rather than misreading it. This is non-negotiable even pre-1.0: refusing is recoverable, misreading is not.
- Each pre-1.0 breaking change ships **either** a migration tool **or** an explicit statement that re-seeding is required.
- Pre-1.0 builds carry a prominent statement that they must not hold the only copy of anything.

### Freeze gate

Format v1 freezes only when all of the following pass ([`../roadmap.md`](../roadmap.md#format-v1-freeze-gate)):

1. Segmentation benchmark published — `fixed-v1` versus `cdc-v1` ([ADR-0002](0002-segmentation-strategy.md)).
2. **Independent reader** written from the published specification alone, by an author who did not write the format, in a different language, passing the conformance fixtures. This is the real test of NFR-COMP-004.
3. Specification and conformance fixtures public.
4. External format review complete.
5. Threat model reviewed against the frozen format.
6. Licence decided ([ADR-0001](0001-licence-and-contribution-model.md)).

## Consequences

**Positive**

- Early adopters can make an informed decision instead of an implicit bet.
- The project can revise the format pre-1.0 without betraying anyone.
- Refuse-rather-than-misread means a version mismatch is an inconvenience, never data loss.
- Criterion 2 makes the independence claim testable rather than rhetorical — and it is where the original proposal's Phase 0 criterion belonged ([H2](../review/2026-08-architecture-review.md#h2--two-phase-0-exit-criteria-cannot-be-met-at-phase-0)).

**Negative**

- Warning at repository creation will deter some early adopters. That is the correct trade for a backup product: a user who would have been deterred by the warning is a user who would have been harmed by its absence.
- The freeze gate is demanding and will delay v1, particularly criterion 2.

## Alternatives considered

**Guarantee forward compatibility from the first public build.** Rejected. It would freeze the format before the segmentation benchmark and before any independent implementation has stress-tested the specification — which is to say, before we know whether it is right.

**No posture; handle breakage case by case.** Rejected. This is the original position, and it converts a decision into a crisis.

**Version by a single integer.** Rejected. Cannot express partial capability, so a reader supporting most of a version has no way to say so and must refuse everything.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |

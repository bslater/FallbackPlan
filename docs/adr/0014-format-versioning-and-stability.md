# ADR-0014 — Format versioning and pre-1.0 stability posture

**Status:** Proposed (amended 2026-09) · Implemented — see [implementation status](../implementation-status.md#by-decision)
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

> **Amended 2026-09.** Format 1 was withdrawn before this gate was reached ([Amendment 1](#amendment-1-2026-09--format-1-withdrawn-before-freeze)). The gate below reads against **format 2**, which is what the product creates by default and what the one live installation holds; its items are unchanged.
>
> **Amended again 2026-09.** Readers now accept format 2 **and format 3** ([ADR-0052](0052-relocatable-records-format-v3.md), built for the record and blob planes), and a repository's version is fixed at creation — `init --format-version 3` is the only way to ask for one, and the service still creates format 2. A pre-format-3 reader refuses such a repository by name on the required feature `0x0003`, which is this record's versioning rule working rather than an exception to it: *refuse, never misread*. The gate's items are still unchanged and still read against format 2, because that is the format an independent reader would be written for; nothing freezes while a second version is a week old.

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

## Amendment 1 (2026-09) — format 1 withdrawn before freeze

**What changed.** Repository format 1 — a random master key wrapped under a passphrase-derived key-encryption key at `/keys/<key-id>`, with a symmetric data-key family beneath it — is withdrawn. Format 2 ([ADR-0042](0042-write-only-repositories.md)), which had been an opt-in beside it, is the only format the product writes or reads. Nothing that opened a format-1 repository remains in the code, and nothing that could hold a service passphrase for one remains either: the service opens every archive with the write credential first-run setup stores ([ADR-0044](0044-first-run-setup.md)), and the passphrase is present only where a person is.

**Why this is allowed.** The pre-1.0 posture above says a breaking change ships either a migration tool or an explicit statement that re-seeding is required. This is the second kind, and the honest reason it costs nothing is that there is no installed base: the product is pre-release, and the one live installation went through setup and has only ever written format 2. A migration would have moved nobody's data.

**What the number does.** Format 1's number is **not reused**. The descriptor's `format_version` and every sealed data blob's envelope and associated data carry `2`; a reader that meets `1` refuses it by name — *refuse, never misread*, exactly as this record requires — and names re-seeding from a live installation as the remedy. One consequence is recorded because it would otherwise look like an error: format 2's **symmetric** containers — metadata blobs and standalone records — still stamp `1` in their envelopes and associated data, because the symmetric construction is the one format 1 defined and format 2 kept byte for byte, and the stamp is authenticated data over bytes already on disk ([04 §4](../../specifications/repository-format/04-record.md#4-associated-data)).

**What the gate means now.** The freeze gate's six items are unchanged and read against format 2 — see the second amendment note at the gate for what format 3's arrival does and does not change. The conformance vectors and the committed fixture are format 2's ([conformance](../../specifications/repository-format/conformance/README.md)); the independent-reader criterion is a reader of format 2.

**What was given up.** The choice at creation between two formats, and with it the service's passphrase mode, its platform keystore and the `unlock`/`lock` verbs ([ADR-0028 §9](0028-service-boundary-and-deployment-topologies.md), [ADR-0033](0033-hosting-under-an-os-service-manager.md), both amended). A format-1 recovery kit still parses — its wire shape is pinned until the kit itself goes — but cannot be opened.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-09 | Amended | Format 1 withdrawn before freeze; the gate reads against format 2 ([Amendment 1](#amendment-1-2026-09--format-1-withdrawn-before-freeze)). |
| 2026-09 | Amended | Format 3 admitted beside format 2: readers accept both, a repository's version is fixed at creation and declared by a required descriptor feature, and a build without that feature refuses by name ([ADR-0052](0052-relocatable-records-format-v3.md)). `Domain/FormatVersions` and `Domain/FormatLimits` carry the two versions and the container stamp each implies; `Repository.Format/Descriptor/RepositoryDescriptorCodec` holds the feature and its consistency rule. The freeze gate is unchanged and still reads against format 2 |

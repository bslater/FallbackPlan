# ADR-0002 — Segmentation strategy

**Status:** Proposed · Implemented — see [implementation status](../implementation-status.md#by-decision)
**Date:** 2026-08
**Requirements:** FR-ARCH-001, FR-ARCH-014, NFR-PERF-007
**Review finding:** [H1](../review/2026-08-architecture-review.md#h1--fixed-size-segmentation-is-under-argued-and-its-review-is-scheduled-after-the-point-of-no-return)

---

## Context

The proposal chose fixed-size segmentation for v1, acknowledged in a single sentence that it handles byte insertion poorly, and scheduled the comparison against content-defined chunking (CDC) as "a later comparative design spike" — that is, after the format would have been frozen and real repositories existed.

Fixed-size is a defensible v1 choice. It is genuinely *better* than CDC for in-place rewrites (VM disks, database files, mailbox stores), which is where the large absolute byte savings are. It gives deterministic fixtures, exact positional version comparison, and O(1) random access to a byte offset.

Its weakness is specific and common: inserting or removing bytes shifts every subsequent boundary. Prepended logs, recompressed containers (`.docx`, `.xlsx`, `.zip` — where a one-character edit changes nearly every byte), and SQLite files after `VACUUM` all defeat it.

For a product whose promise is efficient long version history, that trade determines the storage bill and the bandwidth bill. Discovering the answer after the format is frozen means discovering it after users have committed data.

## Decision

1. **`fixed-v1` is the v1 default.** 1 MiB segments, range 64 KiB – 64 MiB.
2. **`cdc-v1` is specified in format v1**, not deferred. Rolling-hash boundaries with configured min/target/max.
3. **The segmentation profile is selected per backup set**, not per repository, and recorded per file version.
4. **Format v1 shall not be frozen** until both profiles are benchmarked against a representative corpus and the results published ([`../roadmap.md`](../roadmap.md#format-v1-freeze-gate)).

Specifying `cdc-v1` now costs little — the profile field already exists — and forces us to prove the field is expressive enough to describe a content-defined scheme. That is the part that is expensive to discover late.

## Consequences

**Positive**

- Deterministic fixtures and exact positional comparison for v1.
- The default can change at the freeze gate at zero cost, because both profiles are already specified and fixtured.
- Per-set selection means a repository holding both VM images and a documents folder is not forced into one answer.

**Negative**

- Two profiles to specify, fixture, and test in v1 rather than one.
- The `fixed-v1` insertion weakness is real and will be visible to users on affected workloads until the gate resolves.

**Neutral**

- The dedup index key includes the segmentation profile, so segments from different profiles never collide and never falsely deduplicate.

## Alternatives considered

**CDC as the v1 default.** Rejected for v1: it complicates deterministic fixtures and cross-language conformance at exactly the moment we are trying to establish both, and its advantage on in-place-rewrite workloads is negative rather than merely smaller. Revisit at the gate with data.

**Fixed-size only, CDC deferred entirely.** Rejected — this is the original plan, and it schedules an irreversible decision after the point of no return.

**Per-file heuristic selection.** Rejected for v1. Choosing a profile from file type or observed churn is attractive but makes reproducibility and fixture determinism much harder, and there is no evidence yet about which heuristic would be right. Possible later, as a new profile.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Default confirmed at the format v1 freeze gate |

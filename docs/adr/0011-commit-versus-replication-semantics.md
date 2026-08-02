# ADR-0011 — Commit versus replication semantics

**Status:** Proposed
**Date:** 2026-08
**Requirements:** FR-SNP-001, FR-SNP-003, FR-REP-001, NFR-OPS-002
**Review finding:** [C5](../review/2026-08-architecture-review.md#c5--snapshot-commit-is-defined-so-that-one-offline-destination-stalls-all-protection)

---

## Context

FR-SNP-001 required snapshots to be published "only after all required blobs and index deltas are durable". §8.4 separately described a multi-destination durability policy: "complete when local repository durable **and** at least one trusted peer durable".

Read together, "required" makes a snapshot hostage to the least available destination. A peer switched off for a fortnight's holiday means no snapshot is published for a fortnight. Local protection that is working perfectly is withheld because a remote destination is unavailable.

It compounds: with no recent snapshot to compare against, there is nothing for version comparison to reuse, so the eventual catch-up is far more expensive than a series of incrementals would have been. And the status display has nothing truthful to say — it would show no recent backup, when in fact the data is safely captured locally.

The proposal was caught between two ideas that are each correct in isolation: §7.10's within-a-replica ordering rule, and §8.4's cross-replica health policy. Collapsing them into one requirement broke both.

## Decision

Separate the two.

**Commit is per-replica.** A snapshot is committed to a replica once every object it references is durable *in that replica*, following the publication order in [`../architecture/04-concurrency-and-publication.md` §5](../architecture/04-concurrency-and-publication.md#5-publication-order). This preserves the ordering invariant exactly as written, is always achievable locally, and is what makes a replica independently restorable.

**Replication is separate state,** per `(snapshot, destination)`:

| Status | Meaning |
|--------|---------|
| `pending` | Not yet started for this destination |
| `replicating` | Transfer in progress |
| `durable` | All referenced objects durable there |
| `verified` | Independently confirmed by challenge |
| `degraded` | Previously durable, now failing verification or partially missing |

**Policy is evaluated over replication state**, producing `protected` / `policy-compliant` / `healthy` rather than a single boolean.

## Consequences

**Positive**

- Local protection is never withheld because a remote destination is offline.
- Incremental comparison keeps working during a destination outage, so catch-up stays cheap.
- The status model can say "protected locally, waiting on the offsite copy" — a true statement the original could not express.
- Each replica remains independently restorable, which is the property that made the invariant worth having.

**Negative**

- More state to track and display: one status per `(snapshot, destination)` rather than one per snapshot.
- The UI must resist collapsing it back into a single indicator, which is the natural pull. [`../architecture/10-observability.md` §1.2](../architecture/10-observability.md#12-honest-degradation) makes that a rule.

**Neutral**

- `SnapshotCommitResult` reports against the local replica; destination progress is observed through the replication service.

## Alternatives considered

**Keep the coupled definition; require a destination before backing up.** Rejected. It fails exactly when it matters — during an outage — and it makes a laptop that is away from its home peer unprotectable.

**Publish optimistically, retract if replication fails.** Rejected. Retraction contradicts immutability and would mean a snapshot a user saw could later vanish.

**Per-destination snapshot objects.** Rejected. Duplicates metadata proportional to destination count and makes the snapshot's identity ambiguous — which of them is *the* snapshot?

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |

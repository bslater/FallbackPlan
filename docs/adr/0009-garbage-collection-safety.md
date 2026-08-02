# ADR-0009 — Garbage collection safety

**Status:** Proposed
**Date:** 2026-08
**Requirements:** FR-SNP-004, FR-GC-002, FR-GC-003, FR-GC-006, NFR-TIME-001
**Review finding:** [C4](../review/2026-08-architecture-review.md#c4--garbage-collection-can-delete-blobs-belonging-to-an-in-flight-snapshot)

---

## Context

Publication order — blobs, then index deltas, then snapshot — is correct and is the most valuable rule inherited from restic. It also creates a window: between the first blob upload and the delta publication, potentially hours on an initial backup, a writer's blobs are durable and referenced by nothing. A mark-and-sweep collector cannot distinguish them from garbage.

The proposal closed this with "account for active writer leases and grace periods". A lease cannot carry that weight:

- **Clock skew.** A lease is a timed record and there is no trusted time source. Skew between writer and collector translates directly into blobs swept while in use.
- **Eventual consistency.** The store may not show the collector a lease written seconds ago — and the format explicitly permits stores that behave this way.
- **Suspension.** A closed laptop lid, a suspended VM, or a scheduler hiccup loses a lease while its blobs remain legitimate.
- **No binding.** Nothing ties a lease to *which* blobs it protects, so a collector cannot act on one except by declining to collect at all.

The consequence is data loss inside a snapshot the user was told completed successfully, discovered at restore.

## Decision

### Write-intent records

Before uploading its first blob, a writer publishes to `/journal/<writer-id>/<sequence>`:

```text
write_intent {
  writer_id, sequence, issued_at
  backup_set_id
  intended_blob_ids[]      // extended by further intent records as the job grows
  expiry_generation
}
```

- The collector treats every blob covered by an **unretired** intent as reachable. No exceptions, no heuristics.
- The writer retires the intent when its snapshot is published.
- An abandoned job's intent expires only after a grace period exceeding the longest permitted job duration.

The only ordering obligation is that the intent covering a blob is durable **before** that blob is uploaded.

### Safety rests on four mechanisms, none of them a clock

| Mechanism | Protects against |
|-----------|------------------|
| Generation cut-off | Racing with concurrent publication |
| Unretired write intents | Sweeping in-flight work |
| Tombstone grace period | Acting on a stale or incomplete view |
| Pre-delete revalidation | Anything the first three missed |

### Leases are demoted

Leases remain, advisory, for one purpose: stopping two collectors doing the same work. Losing one costs efficiency and nothing else. **No correctness property may depend on a lease.**

## Consequences

**Positive**

- GC concurrent with an in-flight backup is safe by construction, not by timing.
- Safety survives clock skew, store latency, and writer suspension — the three things that actually happen.
- A collector knows exactly which blobs are protected and why, so its dry-run report can say so.

**Negative**

- One extra journal write before the first blob upload, plus extensions as the job grows. Negligible against the payload.
- An abandoned job's blobs occupy space until its intent expires. Bounded by the grace period, and reported as reclaimable-pending.
- Writers must retire intents. A writer that never does delays collection of its blobs until expiry — wasteful, never unsafe.

**Neutral**

- Retention still uses wall-clock time, because "keep daily snapshots for 30 days" is inherently a wall-clock policy. It is applied to recorded capture times, and an implausible timestamp is flagged rather than silently acted on.

## Alternatives considered

**Leases with generous timeouts.** Rejected. Makes the race less likely without eliminating it, and lengthening the timeout trades one failure (data loss) for another (collection never runs).

**Refuse to collect while any writer is active.** Rejected. On a repository with several devices, some writer is nearly always active, so collection would effectively never happen.

**Reference-count blobs at upload.** Rejected. Mutable counters on an eventually consistent store need atomic increment, which is not uniformly available, and a crashed writer leaks counts permanently.

**Collect only blobs older than a long fixed age.** Rejected. An age threshold long enough to be safe for a slow initial backup is long enough to make collection useless, and it is still a clock.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |

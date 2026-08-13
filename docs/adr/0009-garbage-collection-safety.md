# ADR-0009 — Garbage collection safety

**Status:** Accepted (amended 2026-08 after [pressure test](../review/2026-08-fix-pressure-test.md))
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
  declared_max_duration
  expiry_generation
}
```

- The collector treats every blob covered by an **unretired** intent as reachable. No exceptions, no heuristics.
- The writer retires the intent when its snapshot is published.
- An abandoned job's intent expires only when **both** the generation and duration conditions below are met.

The only ordering obligation is that the intent covering a blob is durable **before** that blob is uploaded.

### Amendment 1 — the collector is a writer

The original algorithm applied intent protection to backup writers and not to the collector, which also creates blobs during compaction. Between writing a replacement blob and publishing its index entries, that blob is unreferenced — precisely the window intents exist to cover — so a second concurrent collector could sweep it, after which the first publishes index entries into a deleted blob and tombstones the originals. Both copies of every record in the batch are lost ([PT-3](../review/2026-08-fix-pressure-test.md#pt-3--compaction-output-blobs-are-unprotected-between-creation-and-index-publication)).

The rule is therefore stated generally: **any component that creates a blob publishes an intent first, with no exception for maintenance.** The GC algorithm gains explicit publish and retire steps around compaction.

### Amendment 2 — blob identifiers must be writer-allocated

An intent names blobs before they exist, which is impossible for a content-derived identifier. The format never said how blob identifiers are formed, leaving this mechanism unimplementable ([PT-4](../review/2026-08-fix-pressure-test.md#pt-4--blob-identifier-formation-is-unspecified-and-c4-cannot-be-implemented-without-it)). Resolved in [ADR-0016](0016-blob-identifier-formation.md): blob identifiers are writer-allocated and opaque, unlike record identifiers, which remain content-derived and keyed.

### Amendment 3 — expiry needs two conditions

An intent expires only when the repository has advanced past `expiry_generation` **and** the writer's `declared_max_duration` has elapsed with a configured skew margin.

Generation alone couples one writer's liveness to other writers' activity — generations advance when *others* publish, so a laptop running a three-week initial backup can be expired in two days by siblings backing up hourly, and have its blobs collected mid-job. Wall-clock alone reintroduces the clock dependency this ADR exists to remove. The duration is declared by the writer rather than fixed globally, because a 4 TB first backup and a 20 MB incremental have no single safe constant between them ([PT-5](../review/2026-08-fix-pressure-test.md#pt-5--intent-expiry-mixes-generation-and-wall-clock-and-couples-slow-writers-to-busy-repositories)). An audited administrative force-expire covers genuinely abandoned jobs.

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

## Amendment 4 (2026-08) — where the collector runs under hub-and-spoke

[ADR-0034](0034-hub-and-spoke-destinations.md) splits one archive into a
staging archive per set plus whole-archive replicas at destinations, and the
collector's world divides the same way. **Marking happens where the keys are:
the hub computes the keep-set and its object closure against the set's staging
archive**, under every safety mechanism above, unchanged — intents, generation
cut-off, grace, revalidation. Destinations are then *converged, not collected*:
a local-path destination has the hub's plan executed against it directly, and a
peer is instructed which objects to delete and deletes exactly those, bounded
by its own granted floor — a spoke holds ciphertext it cannot mark, so it never
runs this algorithm and never decides what is garbage.

Two rules join the four mechanisms for the fan-out world. **Deletion may not
outrun replication**: an object leaves staging only when every configured
destination of the set holds it, or the deferral bound of
[ADR-0011 Amendment 2](0011-commit-versus-replication-semantics.md) has been
raised as a warning — the same gate that makes staging trimmable at all.
And **a destination's deletions are keyed to the hub's plan**, never inferred
from local reachability, because local reachability at a replica is exactly the
partial view this ADR exists to distrust.

## Amendment 5 (2026-08) — the grace generation, realised

Building the collector surfaced a conflation the design had survived on
paper: the number every code path called "the generation" is the **key
generation**, which advances on key rotation — not on publication. A grace
period counted in it would never run, and a replication gate compared
against it would hold nothing, because rotation is rare and publication is
the event both actually wait for.

A per-set staging archive is **single-writer by construction**
([ADR-0034](0034-hub-and-spoke-destinations.md)), and a single writer has
exactly one per-publication monotonic every participant can see: its
**journal sequence**, carried in cleartext as each standalone snapshot
record's counter ([ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md)
§Decision 7). The staging collector therefore counts its grace in that
sequence — a tombstone becomes eligible only after the writer has visibly
published past the decision — and the replication gate compares each
snapshot's publication sequence to the highest sequence a destination's
sync had when it began. Sealing and signing keep using the key generation,
which is what derives keys; only the ordering arithmetic moved. When
multi-writer archives exist, this returns to the index generation
[specification 11 §3.1](../../specifications/repository-format/11-lifecycle-objects.md#31-the-grace-period-is-counted-in-generations-not-in-time)
speaks of; the property preserved is the same — no clock, only visible
advancement.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-08 | Accepted (amended) | Intent mechanism unchanged. Extended to the collector itself (PT-3, critical); blob identifier formation resolved via ADR-0016 (PT-4); expiry now requires both generation and declared-duration conditions (PT-5). |
| 2026-08 | Accepted (amended) | Amendment 4: the hub marks against staging, destinations are converged on instruction, and deletion never outruns replication ([ADR-0034](0034-hub-and-spoke-destinations.md)). |

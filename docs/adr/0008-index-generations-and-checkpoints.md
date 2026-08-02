# ADR-0008 — Index generations, deltas, and checkpoints

**Status:** Accepted (amended 2026-08 after [pressure test](../review/2026-08-fix-pressure-test.md))
**Date:** 2026-08
**Requirements:** FR-MAN-008, FR-MAN-013, NFR-SEC-005, NFR-PERF-005
**Review finding:** [C6](../review/2026-08-architecture-review.md#c6--checkpoint-compaction-requires-a-complete-listing-the-design-forbids-relying-on)

---

## Context

The index maps object identifiers to physical locations. It must grow incrementally, support many concurrent writers, and be reconstructable — and §7.9 required it to have "no correctness dependency on object-listing freshness or eventual-consistency timing".

Checkpoint compaction contradicted that. Compacting "prior deltas" means knowing what they are, and the only discovery mechanism offered was listing `/index/delta/`. On an eventually consistent store, a listing can omit a delta written moments ago; the compactor then produces a checkpoint that silently drops it, and every reader that trusts the checkpoint and retires the superseded deltas loses the index entries for a set of blobs. Those blobs are intact and readable — the repository just no longer knows what is in them, and the next collection sees them as unreachable.

The design was also silent on two writers publishing a checkpoint at the same generation: no election rule, no tie-break, no guidance for a reader that finds both.

## Decision

### Deltas form per-writer chains

```text
index_delta {
  writer_id
  sequence               // strictly increasing per writer, no gaps
  predecessor_delta_id
  generation, shard
  covered_blob_ids
  entries[]              // object_identifier → (blob, offset, stored_length, profiles)
}
```

A reader holding any delta from a writer can walk backwards, and can **detect** a missing sequence number rather than assuming it has seen everything. Absence becomes observable.

### Gaps are closed explicitly

Detection alone is not enough, and the original ADR stopped there. A writer that prepares delta *N*, crashes, and resumes at *N+1* leaves a gap that will never be filled: readers that block on it lose that writer's contributions forever, and readers that ignore it throw away the truncation defence the chain was built for ([PT-6](../review/2026-08-fix-pressure-test.md#pt-6--a-crashed-writer-can-permanently-block-readers-through-a-sequence-gap)).

A writer that discovers it has skipped a sequence publishes a signed **void delta** at that number, declaring it intentionally empty. Readers treat a gap as unresolved until either the delta or its void record appears, and after a bounded number of generations with neither, surface it as a damage finding rather than blocking indefinitely. **Silence is never interpreted as "empty".**

### Checkpoints enumerate what they subsume

```text
index_checkpoint {
  generation
  subsumed_delta_ids[]          // exactly which deltas
  writer_watermarks[]           // per writer: highest sequence covered
  shard_set[], shard_hashes[]
  predecessor_checkpoint_id
}
```

A reader applies any delta whose sequence exceeds the checkpoint's watermark for that writer, whether or not a listing revealed it.

### Retirement requires uncontested coverage

The original rule — retire a delta once "a checkpoint explicitly naming it" has been durable for the safety window — is ambiguous when two checkpoints exist at the same generation naming different delta sets. If `CP-a` names `{D1, D2, D3}` and concurrent `CP-b` names `{D1, D2}`, retiring `D3` on `CP-a`'s authority strands its entries for any reader holding only `CP-b` ([PT-7](../review/2026-08-fix-pressure-test.md#pt-7--delta-retirement-is-ambiguous-under-concurrent-checkpoints)).

A delta may therefore be retired only when it is named by a checkpoint that **no live checkpoint at or above its generation contradicts** — in practice, when every retained checkpoint at that generation names it, or a later checkpoint supersedes them all. Retirement is a deletion and takes the same tombstone-and-grace treatment as any other.

### Conflicting checkpoints are merged under explicit precedence

Two checkpoints at the same generation are **both retained and both applied**.

The original justification — that deltas are "immutable, idempotent, and commutative" — was **false**, and had to be withdrawn. [ADR-0007](0007-logical-object-identifiers-in-manifests.md) made the index the sole authority on physical location, so blob compaction republishes an object identifier at a new location. Two deltas mapping one identifier to different blobs are not commutative: order decides, and the losing order resolves to a blob that has been tombstoned and deleted ([PT-2](../review/2026-08-fix-pressure-test.md#pt-2--c6s-commutativity-claim-is-false-once-c1-is-in-place)).

Precedence is therefore explicit — see [ADR-0017](0017-index-entry-supersession.md):

- every entry carries the **generation** at which it was published;
- for a given object identifier, the **highest generation wins**, with a documented deterministic tie-break;
- relocation entries are typed as **supersessions**, distinguishing "this object moved" from "two writers independently recorded the same new object".

Merging is then safe for the reason that actually holds: the winner is a property of the entries, not of arrival order, so any application order converges. Still no election, no lock, no leader — but now for a stated reason rather than an incorrect one.

## Consequences

**Positive**

- Correctness no longer depends on listing freshness. Listing remains a useful accelerator for finding a recent checkpoint.
- Missing deltas are detected rather than silently tolerated.
- Concurrent compaction needs no coordination at all — the property falls out of immutability rather than being engineered on top of it.
- Truncation and rollback are detectable through sequence gaps (NFR-SEC-005).

**Negative**

- Checkpoints are larger, carrying the subsumed delta set and per-writer watermarks. Bounded by writer count and compaction interval, both small.
- A reader must track per-writer watermarks rather than a single global generation.
- Prior checkpoints are retained for a safety window, costing some space.

## Alternatives considered

**Single global index rewritten periodically.** Rejected — violates NFR-PERF-005 and reproduces the monolithic-manifest failure the project exists to avoid.

**Listing-based compaction with a settle delay.** Rejected. A delay makes the race less likely without eliminating it, and "less likely" is the wrong property for a silent data-loss path.

**Leader election for compaction.** Rejected. Requires a coordination primitive object stores do not uniformly provide, and merging is strictly simpler and needs nothing.

**Last-writer-wins on conflicting checkpoints.** Rejected. Needs a trusted ordering — that is, a trusted clock — and would discard the losing checkpoint's coverage.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-08 | Accepted (amended) | Commutativity justification withdrawn as false and replaced by explicit generation precedence (PT-2, critical). Void deltas added for gap closure (PT-6); retirement now requires uncontested coverage (PT-7). Chain mechanism itself unchanged. |

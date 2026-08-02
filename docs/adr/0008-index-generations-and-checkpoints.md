# ADR-0008 — Index generations, deltas, and checkpoints

**Status:** Proposed
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

A reader applies any delta whose sequence exceeds the checkpoint's watermark for that writer, whether or not a listing revealed it. A delta is retired only once a checkpoint explicitly naming it has been durable for the safety window.

### Conflicting checkpoints are merged, not elected

Two checkpoints at the same generation are **both retained and both applied**.

This is safe because index deltas are immutable, idempotent, and commutative: the union of two overlapping checkpoints yields the same catalogue state as either alone plus the difference. No election, no lock, no tie-break, no leader.

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

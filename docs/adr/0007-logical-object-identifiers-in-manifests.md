# ADR-0007 — Manifests reference logical object identifiers only

**Status:** Proposed
**Date:** 2026-08
**Requirements:** FR-ARCH-010, FR-MAN-003, FR-MAN-007, FR-GC-004
**Review finding:** [C1](../review/2026-08-architecture-review.md#c1--immutable-manifests-embed-physical-locations-that-compaction-changes)

---

## Context

The proposal specified a segment reference — inside the immutable file-version manifest — as carrying `plaintext hash, logical offset, logical length, blob ID, physical record offset, stored length, compression profile, encryption profile, key generation`.

Blob compaction (§11.2 step 6) reads still-live records out of mostly-dead blobs and writes them into new ones. That changes the blob ID and the record offset of every record it moves.

So the design required manifests to be immutable — stated in five places, and the foundation of the entire durability argument — while embedding in them two fields that a routine maintenance operation changes. The first compaction pass would either rewrite immutable objects or strand every manifest referencing a moved record. The symptom is unreadable historical snapshots, on the first run of ordinary maintenance.

The proposal already contained the answer without noticing: FR-MAN-007 puts physical layout in the blob's recovery footer, and §7.9 puts it in the index.

## Decision

**A segment reference in a manifest contains logical facts only:**

```text
segment_reference {
  logical_offset      // where in the file
  logical_length      // how many plaintext bytes
  object_identifier   // which segment
}
```

**No blob identifier. No physical offset. No stored length. No encoding profiles.**

Physical resolution — `object_identifier → (blob, record offset, stored length, compression profile, encryption profile, key generation)` — belongs to:

1. the **index** (deltas and checkpoints), as the fast path;
2. the **blob recovery footer**, as the recovery path when the index is gone.

## Consequences

**Positive**

- Compaction republishes index entries and touches no manifest, tree, or snapshot. Immutability is real rather than aspirational.
- Data-key rotation by background rewrite becomes possible for the same reason.
- Manifests get smaller, which helps NFR-PERF-011.
- One authority for physical location instead of three, so they cannot disagree.

**Negative**

- Restoring a segment needs an index lookup that the original design would have avoided. Bounded, local, indexed, and measured against NFR-PERF-004 and NFR-PERF-010 — and the alternative was a maintenance operation that could not be performed.
- Forensic rebuild must reconstruct the mapping from footers before restore can begin. It already had to, for every object not in a surviving manifest.

**Neutral**

- Encoding profiles move from manifest to index and footer, alongside the physical location they describe. This is where they belong: they are properties of how the record was *stored*, not of what the file *is*.

## Alternatives considered

**Keep physical location in manifests; forbid compaction.** Rejected. Compaction is how space is reclaimed after retention expires snapshots; without it the repository grows monotonically and the retention policy becomes advisory.

**Keep physical location as a *hint*, with index fallback on miss.** Rejected. A stale hint that silently falls back is a correctness question dressed up as an optimisation — and it invites implementations that trust the hint. The saving is one indexed local lookup.

**Rewrite manifests on compaction.** Rejected. This is the contradiction, made explicit rather than resolved: it abandons immutability, invalidates snapshot signatures, and turns a bounded maintenance operation into one proportional to history.

**Indirection object between manifest and blob.** Rejected as redundant — the index already is that indirection.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |

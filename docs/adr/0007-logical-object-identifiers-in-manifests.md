# ADR-0007 — Manifests reference logical object identifiers only

**Status:** Proposed — *core decision confirmed; one open question, see [Q11](../open-questions.md#q11--physical-hints-in-segment-references)*
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
- **When the index is lost, single-file recovery regresses from one blob fetch to a footer scan.** This was understated as "one indexed local lookup". Before this change a manifest plus a blob was sufficient to recover a file; now the manifest names object identifiers only, so recovering one 4 MiB document with no index means scanning blob footers — hours at scale **M** under NFR-PERF-012 — which is not what FR-MAN-010 promises ([PT-10](../review/2026-08-fix-pressure-test.md#pt-10--emergency-single-file-restore-regressed-from-one-fetch-to-a-full-scan)).
- **Repository-side index growth is strictly larger.** Physical location moved out of manifests, which shard naturally through the tree graph, and into the index, which must hold an entry per distinct segment object permanently. NFR-PERF-011 covers *catalogue* size; nothing covered the repository-side index until NFR-PERF-014 was added ([PT-14](../review/2026-08-fix-pressure-test.md#pt-14--repository-side-index-growth-is-now-strictly-larger-with-no-requirement-covering-it)).

## Open question — physical hints

This ADR rejected physical hints as "a correctness question dressed up as an optimisation". That rejection does not survive scrutiny and is reopened.

Record headers are independently authenticated and carry the object identifier ([`../architecture/02-repository-format.md` §5.2](../architecture/02-repository-format.md#52-layout)), so a reader that follows a hint and finds the wrong object **detects it** and falls back to the index. A stale hint is detectably stale, not silently wrong — which is precisely the distinction the rejection assumed away.

A non-authoritative `last_known_blob` on the segment reference would restore O(1) first-byte latency in the index-lost case for a few bytes per reference, and — importantly — because it is allowed to go stale, compaction still touches no manifest and the core decision above is preserved intact.

The counter-argument that does survive: it partially re-couples manifests to physical layout, and invites implementations that trust the hint without validating. The mitigation is that conformance fixtures must include a stale-hint case which any correct reader passes.

This is a maintainer decision, tracked as [Q11](../open-questions.md#q11--physical-hints-in-segment-references). **The core decision — that manifests are not authoritative for physical location — is confirmed either way.** Only the presence of an advisory hint alongside it is open.

**Neutral**

- Encoding profiles move from manifest to index and footer, alongside the physical location they describe. This is where they belong: they are properties of how the record was *stored*, not of what the file *is*.

## Alternatives considered

**Keep physical location in manifests; forbid compaction.** Rejected. Compaction is how space is reclaimed after retention expires snapshots; without it the repository grows monotonically and the retention policy becomes advisory.

**Keep physical location as a *hint*, with index fallback on miss.** Originally rejected on the grounds that "a stale hint that silently falls back is a correctness question dressed up as an optimisation". **Reopened** — see the open question above. The saving is not one indexed local lookup; it is the difference between one fetch and a footer scan when the index is gone.

**Rewrite manifests on compaction.** Rejected. This is the contradiction, made explicit rather than resolved: it abandons immutability, invalidates snapshot signatures, and turns a bounded maintenance operation into one proportional to history.

**Indirection object between manifest and blob.** Rejected as redundant — the index already is that indirection.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-08 | Proposed (core confirmed) | Pressure test confirmed the core decision and found two understated costs: index-lost restore latency (PT-10) and repository-side index growth (PT-14). The hint rejection is reopened as [Q11](../open-questions.md#q11--physical-hints-in-segment-references) — the only reason this ADR is not Accepted. |

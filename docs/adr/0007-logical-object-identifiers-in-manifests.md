# ADR-0007 — Manifests reference logical object identifiers only

**Status:** Accepted (amended 2026-08 — Q11 resolved) · Implemented — see [implementation status](../implementation-status.md#by-decision)
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

## Amendment (2026-08) — Q11 resolved, and not where it was asked

**A hint exists. It is not in the manifest.**

The reopening below was right that the original rejection did not survive: a
stale hint is *detectably* stale, because record headers are independently
authenticated and carry the object identifier. What neither this record nor Q11
noticed is what putting the hint **inside a manifest** would have cost.

A manifest's object identifier is derived from its own bytes
([specification 02 §3](../../specifications/repository-format/02-identifiers.md#3-object-identifier)),
and the specification states plainly that deriving the same identifier from the
same content on two devices "is what makes cross-device deduplication possible".
A physical hint is device-specific by definition. Adding one to a segment
reference would therefore have made the same file version encode differently on
every device that captured it — buying faster emergency recovery by quietly
disabling a property the design is built on, and doing it in a way no test would
have caught, because each device's repository would still have been internally
consistent.

So the hint became a separate object:
[specification 06 §10](../../specifications/repository-format/06-manifests.md),
one optional record per snapshot at `/hints/placement/<snapshot-id>`, mapping
object identifiers to the blobs they were written into. Manifests stay
byte-identical across devices. The hint is advisory by construction rather than
by promise — a reader must already handle its absence, so no implementation can
come to depend on it — and compaction still touches no manifest, so the core
decision is preserved exactly as it was.

**The core decision is unchanged: manifests are not authoritative for physical
location.** This amendment only says where the non-authoritative part lives.

## Amendment 2 (2026-08) — the same rule, applied to source identity

The amendment above settled physical location. A second device-specific fact
turned out to want the same treatment, and it is recorded here because the
reasoning is identical and should not have to be rediscovered a third time.

Finding a renamed file's prior version needs its **source identity** — the
inode or `FileId` it was captured from. That is device-specific, so putting it
on a file version would have made the same version encode differently on every
device, for exactly the reason set out above. It became a second optional
object per snapshot instead:
[specification 06 §11](../../specifications/repository-format/06-manifests.md#11-source-identity),
at `/hints/identity/<snapshot-id>`, keyed under the content-ID key so the store
learns nothing about the source's inode space.

What made this worth doing rather than leaving to the local catalogue: the
catalogue is a disposable cache, and a rename captured while it was cold would
write a file version with no `parent_version` — severing that file's history
**permanently**, in an immutable object, because of a transient local state.
Speed degrading with a cold cache is acceptable; correctness degrading is not.

`hardlink_group` remains the one device-specific value inside a manifest. That
exception is narrow and accepted — it is present only for files with multiple
links, and there is nowhere else it can live if hardlinks are to be
reconstructed at all. Generalising it to every file would have extended the
exception from a small minority to all of them.

## Superseded — the open question as it stood

This ADR rejected physical hints as "a correctness question dressed up as an optimisation". That rejection does not survive scrutiny and is reopened.

Record headers are independently authenticated and carry the object identifier ([`../architecture/02-repository-format.md` §5.2](../architecture/02-repository-format.md#52-layout)), so a reader that follows a hint and finds the wrong object **detects it** and falls back to the index. A stale hint is detectably stale, not silently wrong — which is precisely the distinction the rejection assumed away.

A non-authoritative `last_known_blob` on the segment reference would restore O(1) first-byte latency in the index-lost case for a few bytes per reference, and — importantly — because it is allowed to go stale, compaction still touches no manifest and the core decision above is preserved intact.

The counter-argument that does survive: it partially re-couples manifests to physical layout, and invites implementations that trust the hint without validating. The mitigation is that conformance fixtures must include a stale-hint case which any correct reader passes.

This was a maintainer decision, tracked as Q11 and **now closed** — see the amendment above. **The core decision — that manifests are not authoritative for physical location — is confirmed either way.**

**Neutral**

- Encoding profiles move from manifest to index and footer, alongside the physical location they describe. This is where they belong: they are properties of how the record was *stored*, not of what the file *is*.

## Alternatives considered

**Keep physical location in manifests; forbid compaction.** Rejected. Compaction is how space is reclaimed after retention expires snapshots; without it the repository grows monotonically and the retention policy becomes advisory.

**Keep physical location as a *hint* inside the segment reference.** Reopened, then rejected on a ground neither the original nor the reopening had: it would make a manifest device-specific, and manifests are identified by their bytes. The hint moved to its own object instead (amendment above).

**Rewrite manifests on compaction.** Rejected. This is the contradiction, made explicit rather than resolved: it abandons immutability, invalidates snapshot signatures, and turns a bounded maintenance operation into one proportional to history.

**Indirection object between manifest and blob.** Rejected as redundant — the index already is that indirection.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-08 | Proposed (core confirmed) | Pressure test confirmed the core decision and found two understated costs: index-lost restore latency (PT-10) and repository-side index growth (PT-14). The hint rejection is reopened as [Q11](../open-questions.md#q11--physical-hints-in-segment-references) — the only reason this ADR is not Accepted. |

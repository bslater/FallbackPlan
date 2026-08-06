# ADR-0017 — Index entry supersession and precedence

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-MAN-008, FR-MAN-013, FR-MAN-015, FR-GC-004
**Pressure-test finding:** [PT-2](../review/2026-08-fix-pressure-test.md#pt-2--c6s-commutativity-claim-is-false-once-c1-is-in-place)

---

## Context

[ADR-0008](0008-index-generations-and-checkpoints.md) permits two writers to publish a checkpoint at the same generation, and resolves the conflict by retaining and applying both. Its justification was that index deltas are "immutable, idempotent, and commutative", so the union of two overlapping checkpoints yields the same state as either alone plus the difference.

That argument holds for an index that only ever adds mappings for new object identifiers. It stopped holding the moment [ADR-0007](0007-logical-object-identifiers-in-manifests.md) made the index the sole authority on physical location, because blob compaction then **remaps an object identifier that already has a mapping**.

Concretely: object `O` is mapped to `(B1, 4096)` by delta `D1`. Compaction moves it, and `D2` maps `O → (B2, 128)`. `B1` is tombstoned and deleted. A reader that ends up applying `D1` after `D2` resolves `O` to a blob that no longer exists. The bytes are intact in `B2`; the repository has simply lost track of them.

Two deltas mapping one identifier to different locations are not commutative, and the merge rule that makes multi-writer indexing work had nothing under it. This fires on the first compaction pass in any repository with more than one writer.

## Decision

### Entries carry their generation, and the highest generation wins

Every index entry records the generation at which it was published. For a given object identifier, the entry with the **highest generation** is authoritative.

Below that, a documented deterministic tie-break applies — entries at the same generation for the same identifier are ordered by `(writer_id, sequence)`. Ties should not arise for a *relocation*, since compaction is the only producer of relocations and two collectors relocating the same object concurrently is already bounded by advisory leases; the tie-break exists so that behaviour is defined rather than accidental.

### Relocations are typed

An entry declares whether it is an **insertion** (first mapping for this identifier) or a **supersession** (this object moved). The distinction matters because the two have different orderings:

| Entry type | Produced by | Ordering |
|-----------|-------------|----------|
| Insertion | A writer storing a new record | Order-independent — two writers recording the same new object genuinely commute |
| Supersession | Compaction relocating a record | Ordered — the later publication is correct and the earlier points at a doomed blob |

A reader that encounters two insertions for one identifier may take either; they describe the same bytes in different places, and both are valid until one is superseded. A reader that encounters a supersession must honour generation order.

### Merging is safe for a stated reason

With precedence explicit, "both retained and both applied" is sound: the winner is a property of the entries themselves rather than of arrival order, so any application order converges on the same state. No election, no lock, no leader — but now because of a rule, rather than because of an incorrect claim about commutativity.

## Consequences

**Positive**

- Compaction and concurrent checkpoint publication coexist without coordination.
- Readers converge regardless of the order in which they discover deltas and checkpoints — the property ADR-0008 needed and did not have.
- A reader can tell the difference between benign duplication and a relocation, which also makes the damage report more precise.

**Negative**

- Every index entry carries a generation field. A few bytes per entry, on the largest structure in the repository.
- Catalogue application must compare generations rather than blindly overwriting, and a rebuild that applies deltas out of order must not let an older entry win.
- Superseded entries cannot be discarded immediately: a reader may still be resolving against the older generation. They are retired with the tombstone-and-grace treatment that applies to the blob they point at.

## Alternatives considered

**Keep commutativity and forbid compaction.** Rejected — compaction is how space is reclaimed after retention expires snapshots, and without it the retention policy is advisory.

**Make compaction rewrite manifests instead of the index.** Rejected. This is exactly the contradiction [ADR-0007](0007-logical-object-identifiers-in-manifests.md) was written to remove; it would abandon immutability and invalidate snapshot signatures.

**Elect a single checkpoint per generation.** Rejected — needs a coordination primitive object stores do not uniformly provide, and it discards the losing checkpoint's coverage. Precedence achieves the same determinism with no coordination at all.

**Last-writer-wins by timestamp.** Rejected. Requires a trusted clock, which [ADR-0009](0009-garbage-collection-safety.md) establishes we do not have.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Forced into the open by PT-2; ADR-0008's merge rule is unsound without it |

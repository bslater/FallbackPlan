# ADR-0017 — Index entry supersession and precedence

**Status:** Accepted (amended 2026-10) · Partly built — see [implementation status](../implementation-status.md#0008-0017--the-deltas-are-built-the-checkpoints-are-not-and-two-writers-are-ranked-by-id)
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

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--precedence-as-built-and-the-writer-it-favours)).**
> No entry carries a generation. Each one ranks at the generation, writer and
> sequence of the delta that published it, and the generation never advances,
> so every relocation ties and the tie-break decides it. With one writer the
> sequence decides, and a compaction stands. Between two writers the writer
> id's bytes decide: a compaction published under the lower id is undone by
> the next retention pass, which is churn and not loss. A fix is owed.

> **Amended 2026-10 ([Amendment 2](#amendment-2-2026-10--the-compactor-declines-a-relocation-that-would-not-stand)).**
> The compactor no longer publishes a relocation that would lose. It declines
> the blob before it writes anything and leaves it whole.

### Relocations are typed

An entry declares whether it is an **insertion** (first mapping for this identifier) or a **supersession** (this object moved). The distinction matters because the two have different orderings:

| Entry type | Produced by | Ordering |
|-----------|-------------|----------|
| Insertion | A writer storing a new record | Order-independent — two writers recording the same new object genuinely commute |
| Supersession | Compaction relocating a record | Ordered — the later publication is correct and the earlier points at a doomed blob |

A reader that encounters two insertions for one identifier may take either; they describe the same bytes in different places, and both are valid until one is superseded. A reader that encounters a supersession must honour generation order.

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--precedence-as-built-and-the-writer-it-favours)).**
> The type is published and stored, and no reader orders by it. Only the
> generation, writer and sequence of the publishing object decide, so a
> supersession is honoured only where that triple puts it above what it
> relocates.

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

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--precedence-as-built-and-the-writer-it-favours)).**
> The per-entry generation field was never added; the generation is the
> delta's. Superseded entries are never retired, because no delta is.

## Alternatives considered

**Keep commutativity and forbid compaction.** Rejected — compaction is how space is reclaimed after retention expires snapshots, and without it the retention policy is advisory.

**Make compaction rewrite manifests instead of the index.** Rejected. This is exactly the contradiction [ADR-0007](0007-logical-object-identifiers-in-manifests.md) was written to remove; it would abandon immutability and invalidate snapshot signatures.

**Elect a single checkpoint per generation.** Rejected — needs a coordination primitive object stores do not uniformly provide, and it discards the losing checkpoint's coverage. Precedence achieves the same determinism with no coordination at all.

**Last-writer-wins by timestamp.** Rejected. Requires a trusted clock, which [ADR-0009](0009-garbage-collection-safety.md) establishes we do not have.

## Amendment 1 (2026-10) — precedence as built, and the writer it favours

Reading the index plane against this record, before designing checkpoints
([ADR-0008 Amendment 1](0008-index-generations-and-checkpoints.md#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first)),
found four statements above that describe the design rather than the code.
The first three decide, between them, who wins a relocation in an archive
with two writers.

1. **No entry carries a generation.** The entry
   ([specification 07 §2.1](../../specifications/repository-format/07-index.md#21-index-entry))
   has six fields, and a generation is not one of them. The generation
   belongs to the delta, and every entry in a delta ranks at that delta's
   generation, writer and sequence. The catalogue stores that triple beside
   each location it records. The cost named under Negative, a generation
   field on every entry, was never paid.
2. **The generation never advances.** Every delta is published at the
   repository's key generation. Only a key rotation would move it, and
   nothing rotates a key yet
   ([ADR-0009 Amendment 5](0009-garbage-collection-safety.md#amendment-5-2026-08--the-grace-generation-realised)).
   So rule 1 of 07 §3 decides nothing in any repository the product writes.
   Every pair of entries for one object ties on generation, and the
   tie-break decides all of them. "Ties should not arise for a relocation"
   is false as built: every relocation ties.
3. **No reader orders by the entry type.** A compaction publishes
   supersessions and a capture publishes insertions. Both orderings in the
   code, `IndexPrecedence` and the catalogue's location query, compare the
   triple and nothing else. The type records what a compaction meant;
   precedence is what carries it out, or fails to.
4. **Superseded entries are never retired.** The last cost under Negative,
   retiring superseded entries under tombstone and grace, is not built. No
   delta is retired, so a superseded entry stays in the delta that published
   it, and precedence picks the winner every time an index is read. Retiring
   entries is a checkpoint's work, and nothing publishes a checkpoint yet.

What that means:

- **With one writer, a compaction stands.** A compaction's delta comes after
  the deltas whose entries it relocates, so it carries a higher sequence
  from the same writer and wins. A staging archive is single-writer by
  construction ([ADR-0034](0034-hub-and-spoke-destinations.md)), and this is
  the case the product runs.
- **With two writers, the writer id decides.** An archive holds two writers
  when a machine adopts it and keeps its own identity. [ADR-0061](0061-adopt-a-destinations-archives.md)
  §4 keeps it when the archive already has more than one writer, or when the
  machine has already published. Suppose one writer compacts records the
  other inserted, and the compacting writer's id sorts lower. Its
  supersessions then lose to the insertions they relocate, whatever order
  the two were published in. The next retention plan resolves the records
  to the drained blob again and condemns the blob the compaction produced.
  It leaves the drained blob part-dead, so a later compaction chooses it
  again.

That is **churn, not loss**. Every reader resolves the same winner, and the
winner is the blob that still holds the records. `Retention.Tests/CompactionCollectionTests`
pins both directions:
- Under the higher id, the compaction stands.
- Under the lower id, the produced blob is swept and the drained blob is
  kept. No finding is raised, and every reachable object still reads.

The cost is a compaction that copies records and reclaims nothing, every time
it chooses that blob, in an adopted archive whose ids fall the wrong way.

**The reuse shortcut follows the same winner.** A writer reuses a segment
without a read only when the location that wins is its own
([ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md)). Where both
writers hold a segment, the higher id's location wins. So under a lower id
a writer is never credited with its own copy, and under the device domain it
stores a changed file's unchanged segments again every time the file
changes. `Repository.Tests/DedupTrustDomainTests` pins both directions:
- Under the higher id, a file changed twice after its first backup stores
  only the segment that changed.
- Under the lower id, it stores its unchanged segment again on each backup.

An unchanged file is not affected, because it reuses its prior version
without asking the shortcut. Nothing is lost here either. The cost is
storage and upload.

> **Amended 2026-10 ([ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md#amendment-2026-10--a-devices-own-copy-is-one-it-holds-not-only-one-that-wins)).**
> No longer under `device`. A writer's own copy is any live entry it
> published, so under the lower id it stores a changed file's unchanged
> segments once. A restore still reads the winning copy.

**A fix is owed, and this amendment does not choose it.** Whatever it is
must:
- make a compaction's supersessions outrank every entry they relocate,
  whichever writer published either;
- leave two insertions order-independent, as this record decided;
- account for what advancing a generation sets off, if the fix advances
  one. The gap patience of [ADR-0008](0008-index-generations-and-checkpoints.md)
  is counted in generations, and no load the product runs passes sequence
  accounting. Once generations move, the numbers a writer that has stopped
  publishing spent on journal records and blobs would read as missing deltas
  ([ADR-0008 Amendment 1](0008-index-generations-and-checkpoints.md#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first)).

FR-MAN-015 carries the fix.

> **Amendment 2** stops the wasted compaction without the fix: a writer
> declines a relocation that would not stand. The fix is still owed.

## Amendment 2 (2026-10) — the compactor declines a relocation that would not stand

The fix Amendment 1 owes changes the rank rule, and the rank rule is a format
question. A delta would carry an index generation, apart from the key
generation, and a relocation published after the entries it relocates would
outrank them whichever writer published either. That is format 4's to
design. This amendment builds what format 3 can do until then: **a writer
does not publish a relocation that would lose.**

1. **One rule says whether an entry would win.** `IndexPrecedence.WouldOutrank`
   says whether an entry a writer has yet to publish, at a given generation,
   would outrank an incumbent. The generation decides first. At one
   generation the writers' bytes decide, and a writer's own entries lose to
   its next one, because its next sequence exceeds every one it has
   published. This is the order `IndexPrecedence.Compare` already defines,
   asked before publication. `Repository.Tests/IndexPrecedenceTests` holds the
   two to the same answer on random pairs.
2. **The pass asks it before it writes anything.** `CompactionPass.RelocationStands`
   asks it of the entry that holds each live record. That entry is
   `Catalogue.WinningLocation`, the winner `ResolveLocation` returns, without
   the rule-3 finding the plan has already recorded. The pass declines a
   candidate if any of its records would not stand, and it decides before its
   intent. A pass that declines every candidate writes nothing at all, and
   its outcome names what it declined.
3. **The whole blob, or none of it.** The collector condemns a blob only when
   none of its records is live. A pass that moved the records that would
   stand and left the rest would copy bytes and reclaim nothing from the blob
   it drained.
4. **The plan asks the same question.** `CompactionPolicy` leaves such a blob
   out of the candidates and says why ("left whole: another writer's entries
   would outrank the rewrite"). It does not count the blob under a threshold
   the blob cleared. The service hands the plan the pass's rule, so the dry
   run offers only what the apply would rewrite (FR-GC-005).

What that means:

- **A compaction that is published stands, whichever writer published it.**
  That is FR-MAN-015's acceptance. It is met by not publishing the
  relocations that would lose.
- **The lower id leaves the other writer's blobs whole.** In an adopted
  archive whose ids fall the wrong way, the writer whose id sorts lower
  declines every blob whose live records the other writer's entries hold.
  Their dead bytes stay until those records die, or until the writer whose id
  sorts higher compacts the blob, and its compaction stands. Nothing is lost,
  and no pass copies records only to reclaim nothing.
- **An object the catalogue does not locate stands,** because the
  supersession would be its only entry. So the pass must ask the catalogue
  its plan resolved through. In the service that catalogue holds every
  writer's deltas.

What it does not change:

- **The rank rule.** Two writers are still ranked by id at one generation.
  The compactor now publishes only the supersessions the triple puts above
  what they relocate.
- **The reuse shortcut.** Under the device domain, the writer whose id sorts
  lower still stores a changed file's unchanged segments again
  (`Repository.Tests/DedupTrustDomainTests`).

  > **Amended 2026-10.** Since fixed in format 3: under `device` a writer's
  > own copy is any live entry it published
  > ([ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md#amendment-2026-10--a-devices-own-copy-is-one-it-holds-not-only-one-that-wins)).
- **The format and the wire.** Nothing new is written, and nothing a reader
  reads changes.

The fix Amendment 1 owes is still owed. Format 4's index generation is to
make a compaction's supersessions outrank every entry they relocate,
whichever writer published either, so the writer whose id sorts lower can
compact too.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted (amended); partly built | [Amendment 2](#amendment-2-2026-10--the-compactor-declines-a-relocation-that-would-not-stand): the compactor declines a relocation that would not stand. `IndexPrecedence.WouldOutrank`, asked by `CompactionPass` of the entry that holds each live record, declines a candidate before its intent, and `CompactionPolicy` leaves the blob whole and says why. A published compaction now stands whichever writer published it (FR-MAN-015, `Retention.Tests/CompactionCollectionTests`). The rank rule and the reuse shortcut are unchanged, and the fix Amendment 1 owes is format 4's |
| 2026-10 | Accepted (amended); partly built | [Amendment 1](#amendment-1-2026-10--precedence-as-built-and-the-writer-it-favours): no entry carries a generation and the generation never advances, so the writer-id tie-break decides every relocation. A compaction under the lower of two writer ids is undone, which is churn and not loss (`Retention.Tests/CompactionCollectionTests`). Superseded entries are never retired. The fix is owed under FR-MAN-015 |
| 2026-08 | Accepted | Forced into the open by PT-2; ADR-0008's merge rule is unsound without it |

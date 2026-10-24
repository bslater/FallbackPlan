# ADR-0008 — Index generations, deltas, and checkpoints

**Status:** Accepted (amended 2026-08 after [pressure test](../review/2026-08-fix-pressure-test.md); amended 2026-10) · Partly built — see [implementation status](../implementation-status.md#0008-0017--the-deltas-are-built-format-5-checkpoints-them-and-two-writers-are-ranked-by-id)
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

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first)).** Void deltas and
> detection are built. The patience is counted in generations, which never
> advance, so a gap a service rebuild finds stays unresolved and is never
> reported as damage. No load the product runs accounts for the journal and
> blob numbers in the same sequence space, so those count as gaps too.

> **Amended 2026-10 ([ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md) §Decision 7, amended).**
> The product's two rebuilds now pass that accounting, so a number spent on
> a journal record, or on a blob an intent named, is not a gap. The patience
> still never runs out.

> **Amended 2026-10 ([ADR-0096](0096-format-4-an-index-generation-per-delta.md) §4).**
> From format 4 it does. Every delta carries an index generation, and a gap
> ages per gap: the highest any loaded delta carries, less that of the first
> delta its writer published above the gap. A production reader judges it at
> 8 (`Repository.Index/IndexLoader`'s `GapPatienceGenerations`). A gap in
> format-3 history, whose next delta carries none, is not aged by them.

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

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first)).** Nothing in the
> product publishes a checkpoint. The loader applies what a listing returns:
> a delta the listing omits is detected as a gap once a later number is
> seen, never fetched. It drops every delta at or below a watermark without
> reading `subsumed_delta_ids`, and it ignores `shard_set`, so a watermark is
> only as safe as the accounting behind it.

> **Amended 2026-10 ([Amendment 2](#amendment-2-2026-10--format-5-meets-five-of-the-six); [ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md)).**
> From format 5 a checkpoint carries the deltas it subsumes, verbatim, beside
> their identifiers, and nothing of its own. A loader reads each carried delta
> from the checkpoint and fetches only the listed deltas no verified copy
> carries. No watermark is acted on, so a delta a builder's listing missed is
> applied when it is listed, and gap accounting starts from 1.

### Retirement requires uncontested coverage

The original rule — retire a delta once "a checkpoint explicitly naming it" has been durable for the safety window — is ambiguous when two checkpoints exist at the same generation naming different delta sets. If `CP-a` names `{D1, D2, D3}` and concurrent `CP-b` names `{D1, D2}`, retiring `D3` on `CP-a`'s authority strands its entries for any reader holding only `CP-b` ([PT-7](../review/2026-08-fix-pressure-test.md#pt-7--delta-retirement-is-ambiguous-under-concurrent-checkpoints)).

A delta may therefore be retired only when it is named by a checkpoint that **no live checkpoint at or above its generation contradicts** — in practice, when every retained checkpoint at that generation names it, or a later checkpoint supersedes them all. Retirement is a deletion and takes the same tombstone-and-grace treatment as any other.

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first)).** No delta is retired,
> at staging or at a destination. Constraint 6 of the amendment says what
> retirement would have to survive first.

### Conflicting checkpoints are merged under explicit precedence

Two checkpoints at the same generation are **both retained and both applied**.

The original justification — that deltas are "immutable, idempotent, and commutative" — was **false**, and had to be withdrawn. [ADR-0007](0007-logical-object-identifiers-in-manifests.md) made the index the sole authority on physical location, so blob compaction republishes an object identifier at a new location. Two deltas mapping one identifier to different blobs are not commutative: order decides, and the losing order resolves to a blob that has been tombstoned and deleted ([PT-2](../review/2026-08-fix-pressure-test.md#pt-2--c6s-commutativity-claim-is-false-once-c1-is-in-place)).

Precedence is therefore explicit — see [ADR-0017](0017-index-entry-supersession.md):

- every entry carries the **generation** at which it was published;
- for a given object identifier, the **highest generation wins**, with a documented deterministic tie-break;
- relocation entries are typed as **supersessions**, distinguishing "this object moved" from "two writers independently recorded the same new object".

Merging is then safe for the reason that actually holds: the winner is a property of the entries, not of arrival order, so any application order converges. Still no election, no lock, no leader — but now for a stated reason rather than an incorrect one.

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first); [ADR-0017 Amendment 1](0017-index-entry-supersession.md#amendment-1-2026-10--precedence-as-built-and-the-writer-it-favours)).**
> No entry carries a generation, and the generation never advances, so the
> writer id decides between writers. A checkpoint's entries all rank at its
> own writer and watermark, so a merge is safe only once constraint 1 of the
> amendment is met.

> **Amended 2026-10 ([Amendment 2](#amendment-2-2026-10--format-5-meets-five-of-the-six)).** Met at format 5 by carrying
> deltas rather than entries: every carried entry ranks as its own delta does,
> and a checkpoint's own entries are ranked nowhere. Two checkpoints carrying
> one delta apply it once.

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

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first)).** None of these costs is
> paid, because no checkpoint is written. The cost paid instead is that every
> full load reads every delta.

> **Amended 2026-10 ([Amendment 2](#amendment-2-2026-10--format-5-meets-five-of-the-six)).** A format-5 set writes
> checkpoints, and pays different costs from these: no watermark is tracked,
> and every carried delta is stored twice, at its key and in its checkpoint,
> until a delta can be retired.

## Alternatives considered

**Single global index rewritten periodically.** Rejected — violates NFR-PERF-005 and reproduces the monolithic-manifest failure the project exists to avoid.

**Listing-based compaction with a settle delay.** Rejected. A delay makes the race less likely without eliminating it, and "less likely" is the wrong property for a silent data-loss path.

**Leader election for compaction.** Rejected. Requires a coordination primitive object stores do not uniformly provide, and merging is strictly simpler and needs nothing.

**Last-writer-wins on conflicting checkpoints.** Rejected. Needs a trusted ordering — that is, a trusted clock — and would discard the losing checkpoint's coverage.

## Amendment 1 (2026-10) — what is built, and what a checkpoint must satisfy first

The status history called this record built. The delta half is. The
checkpoint half is a codec and a publisher with no caller in the product:
nothing publishes a checkpoint, and nothing retires a delta. This amendment
says which half is which. It then states what any checkpoint must satisfy
before the product publishes one. It does not decide how checkpoints are
built; that design comes next, under this record.

### What is built

- **Deltas and their chains.** Each delta carries its writer, sequence and
  predecessor, and is published only after the blobs it covers are durable
  (`Repository.Index/IndexPublisher`). A number a writer skips is closed by
  a void delta.
- **One loader.** `Repository.Index/IndexLoader` is the only decoder of index
  objects. It lists every checkpoint and every delta, opens each, and checks
  its signature. Then it applies them.
- **Gap detection, without the patience running out.** A missing number is
  detected and never read as empty. The patience is counted in generations,
  and the generation never advances
  ([ADR-0017 Amendment 1](0017-index-entry-supersession.md#amendment-1-2026-10--precedence-as-built-and-the-writer-it-favours)).
  So a gap a service rebuild finds stays unresolved for good and is never
  reported as damage. The numbers a writer spends on journal records and
  blobs share the deltas' sequence space
  ([ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md)
  §Decision 7). No load the product runs passes the accounting that would
  tell them apart, so those numbers count as gaps too.

  > **Amended 2026-10 ([ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md) §Decision 7, amended).**
  > The service's catalogue rebuild and the CLI's `rebuild-index` now pass
  > it (`Repository.Index/SequenceAccounting`), so only a number nothing
  > accounts for is a gap. `ArchitectureTests/IndexLoadAccountingTests` holds
  > that in the compiled code, because no rebuild's output can show it while
  > the patience never runs out.

  > **Amended 2026-10 ([ADR-0096](0096-format-4-an-index-generation-per-delta.md) §4).**
  > At format 4 the patience runs out. A gap ages in index generations from
  > the first later delta of its writer, and the rebuilds judge it at 8.
- **The checkpoint codec and publisher.** `Repository.Index/CheckpointCodec`
  and `IndexPublisher.PublishCheckpointAsync` round-trip a checkpoint, and
  the loader applies any checkpoint it finds. Only tests publish one.
- **The rollback witness.** It reads the deltas' sequences and the journal
  keys. It would read checkpoint watermarks too, but none is ever written.

### What is not built

- **No checkpoint is published.** Every repository's index is its deltas.
  A normal rebuild applies every delta, and "the latest checkpoint" that
  [architecture 02 §8.1](../architecture/02-repository-format.md#81-normal-rebuild)
  starts from does not exist.

  > **Amended 2026-10 ([Amendment 2](#amendment-2-2026-10--format-5-meets-five-of-the-six)).** A format-5 set publishes
  > them after its backups, carrying its deltas.
- **No delta is retired**, at staging or at a destination. A destination
  never deletes an index key: convergence keeps every key outside `blobs/`
  and `snapshots/`.
- **"Whether or not a listing revealed it" promises less than it sounds.**
  The loader applies what a listing returns. A delta's key carries a random
  identifier, so a delta the listing omits cannot be fetched. It can only be
  detected, as a gap, once a later number of its writer is seen. What does
  hold is that a checkpoint never hides a delta above its watermark. That is
  tested only with a checkpoint a test published.

### What a checkpoint would buy

A full index load happens at these points:
- archive open;
- a catalogue rebuild;
- a rollback heal;
- adoption;
- a restore source opened from a destination, which every recovery drill
  opens.

It never happens per backup, per retention pass or per sync: the live
catalogue takes each delta as it is published. The loader fetches every
delta it lists, checkpoint or not. So a checkpoint saves work only once the
deltas it covers are retired, or once the loader learns to skip them. Every
load at a destination gains nothing until a destination retires deltas too.

### What a checkpoint must satisfy before one is published

A checkpoint published over today's code, as the codec stands, would be wrong
in six ways. Each one below constrains the design. None of them is a design.

1. **Its entries must rank where the entries they stand for ranked.** The
   loader ranks every entry of a checkpoint at the checkpoint's own
   generation, its writer, and that writer's watermark. The catalogue ranks
   them the same way. The generation never advances, so the checkpoint
   writer's id decides against every delta and every other checkpoint.

   > **Amended 2026-10 ([ADR-0096](0096-format-4-an-index-generation-per-delta.md)).**
   > From format 4 a delta's entries rank at its index generation, which
   > advances, and a checkpoint's still rank at the checkpoint's generation.
   > So a checkpoint published today would rank below every format-4 delta.
   > The constraint stands: a checkpoint must keep each entry's rank.
   - A checkpoint by a writer whose id sorts high would outrank a later
     relocation by a writer whose id sorts low. That is
     [PT-2](../review/2026-08-fix-pressure-test.md#pt-2--c6s-commutativity-claim-is-false-once-c1-is-in-place)
     again, at the checkpoint level.
   - Two checkpoints would be ranked by their writers' ids.
   - Two entries for one object inside one checkpoint would tie, and arrival
     order would decide them.

   [Specification 07](../../specifications/repository-format/07-index.md)
   says how entries rank (§3) and that concurrent checkpoints are both
   applied (§6). It says nothing about how a checkpoint's entries rank
   against a delta's. That rule has to be written first.
2. **Every entry must keep its writer and sequence.** A catalogue credits a
   location to the writer of the row that wins it, and two things read that
   credit:
   - **The reuse shortcut.** A segment credited to this writer is reused
     with no read ([ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md)).
   - **The files-delivered status.** It compares each location's sequence
     with a destination's watermark.

   A checkpoint row credits every location it holds to the checkpoint's
   writer, at its watermark. One that won another writer's segment would let
   its writer reuse that segment unread. That includes the `device` domain,
   where another writer's segment is refused outright.
3. **A watermark must stand only where every number below it is accounted
   for.** The loader drops the entries of every delta at or below the
   highest watermark any checkpoint gives its writer. It also starts gap
   detection above that watermark. It reads `subsumed_delta_ids` for
   neither.

   Suppose a builder places a watermark past a delta its listing missed.
   That delta is then hidden from every loader: its entries are dropped
   when it does appear, and its absence is never detected. That is
   [C6](../review/2026-08-architecture-review.md#c6--checkpoint-compaction-requires-a-complete-listing-the-design-forbids-relying-on)
   again, the failure this record was written to close.

   The numbers below a watermark are not all deltas. Journal records and
   blob counters take numbers from the same space, and nothing the product
   runs can tell them from a missed delta. So one of two things must hold:
   - the loader skips only the deltas a checkpoint names; or
   - a builder accounts for every number before it places a watermark.
4. **It must be readable at the size it is.** A standalone record over
   16 MiB is refused at read (`Repository.Format/Records/StandaloneRecordFraming`),
   so one checkpoint holds about 210 000 objects at most. A larger index
   needs checkpoints split by shard, and the loader ignores `shard_set`. A
   checkpoint covering some shards would, through its per-writer watermark,
   drop the entries of the shards it does not cover. Watermarks then have to
   apply per shard.
5. **The blob digests and Merkle roots must survive it.** Only deltas carry
   each covered blob's signed digest and root
   ([07 §2.2](../../specifications/repository-format/07-index.md#22-covered-blob-digests),
   [§2.3](../../specifications/repository-format/07-index.md#23-covered-blob-merkle-roots)),
   and a catalogue rebuilt from the index takes both from them. They are what
   a destination's verification proves a sealed blob against.

   A checkpoint has no field for either, and the codec refuses a key it does
   not know. Carrying them is a format change, gated on the format version
   the way §2.3's roots were. Until then, no delta that carries them may be
   retired.
6. **Retiring a delta must survive the copies.** Nothing in the product
   deletes an index object today.
   - **No identity.** A tombstone names a 32-byte object identifier. A delta
     is found by the 16-byte random identifier in its key, and nothing maps
     one to the other without opening every delta. The collector acts on
     blob and snapshot tombstones only.
     [Specification 11 §3](../../specifications/repository-format/11-lifecycle-objects.md#3-tombstone)
     already records *retired delta* as unreachable.
   - **The copies bring it back.** Destinations never delete index keys.
     The rollback heal and adoption copy every index key from a destination
     back into the set's own plane, and migration copies every one from
     staging into the set's metadata store. So a delta retired in one place
     comes back from another.
   - **Readers mid-flight.** Staging retirement refuses while staging holds
     an index object the set's metadata store lacks. A delta retired from
     the store and not from staging would therefore hold it back for ever.
     A peer push lists everything before it reads anything, so a delta
     retired during the push fails it.
   - **A delete through the sink is partial.** For a direct-ship set it
     reaches local-path destinations before the metadata store, and never
     reaches a peer or an object store. Interrupted, it leaves the delta at
     some destinations and not others:
     - the next copy re-sends it silently;
     - a verification on a pass with nothing to copy can sample it and fail
       the destination;
     - a copy a destination still holds after the metadata store's is gone
       is never listed again.

Until the design meets these six, the index stays deltas only. That is safe,
because nothing the product does depends on a checkpoint. The cost is that a
full load reads every delta, and that cost grows with the number of backups
and compaction passes.

> **Amended 2026-10 ([Amendment 2](#amendment-2-2026-10--format-5-meets-five-of-the-six)).** Format 5 meets constraints 1
> to 5. Constraint 6 stands: nothing retires a delta.

## Amendment 2 (2026-10) — format 5 meets five of the six

[ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md) is the design
Amendment 1 said would come next. A format-5 checkpoint carries the deltas it
subsumes as their stored objects, verbatim, at key 10 beside their identifiers
in key 2, and carries nothing of its own. A loader opens each copy exactly as
it would the object at the delta's key and fetches only the listed deltas no
verified copy carries.

Against the six constraints:
1. **Rank.** A carried delta's entries rank at its own index generation,
   writer and sequence. A checkpoint's own entries, the earlier form, are
   ranked nowhere, by the loader or the catalogue.
2. **Writer and sequence.** Kept, because the copy is the delta. A catalogue
   rebuilt through a checkpoint credits each location as one rebuilt from the
   deltas does.
3. **Watermarks.** No watermark is acted on. The loader skips only what a
   verified copy carries, so nothing a builder's listing missed is hidden,
   and gap accounting starts from 1.
4. **Size.** Each checkpoint is held under the metadata limit, and a delta too
   large for one is never carried. Nothing depends on `shard_set`.
5. **Digests and roots.** Kept: they are signed inside each delta, and the
   delta is carried whole.
6. **Retirement.** Not met. Nothing retires a delta, so every carried delta is
   stored twice. Retirement is owed under FR-MAN-017.

The service checkpoints a format-5 set after a committed backup, inside the
run, once 64 of its deltas sit outside every checkpoint
(`Repository.Index/IndexCheckpointer`). A set below format 5 publishes none,
because a reader that predates key 10 would report the checkpoint as damage.

What a checkpoint buys, as Amendment 1 put it, is now bought at the loader:
it no longer fetches a delta a checkpoint carries, so every full load
gains, at staging and at every destination, without a delta being retired.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Partly built (amended) | [Amendment 2](#amendment-2-2026-10--format-5-meets-five-of-the-six): format 5's checkpoint carries the deltas it subsumes, verbatim, and a load reads each carried delta from it and fetches only the rest ([ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md); `Repository.Index/CheckpointCodec`, `Repository.Index/IndexLoader`, `Repository.Index/IndexCheckpointer`). No watermark and no checkpoint entry is acted on. Constraints 1 to 5 are met; retirement, constraint 6, is not built |
| 2026-10 | Partly built (amended) | Every delta a writer publishes is one a reader can open. A backup put every record it wrote into one delta, so one past about 245 000 records published a delta over the 16 MiB metadata bound that every reader refused. A publication now cuts its entries into deltas at blob boundaries under that bound, the cut compaction already made (`Repository/IndexDeltaBudget`), and the publisher refuses any index object over it (`Repository.Tests/DeltaSplitPublicationTests`, `Repository.Tests/Index/OversizedIndexObjectTests`; 07 §2) |
| 2026-10 | Partly built (amended) | The gap patience runs out at format 4: every delta carries an index generation, a gap ages per gap from the first later delta of its writer, and the product's rebuilds judge it at 8 (`Repository.Index/IndexLoader`, `Repository.Tests/Index/IndexGenerationTests`, [ADR-0096](0096-format-4-an-index-generation-per-delta.md) §4). Checkpoints are still unpublished |
| 2026-10 | Partly built (amended) | The product's index loads pass sequence accounting ([ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md) §Decision 7, amended), so a journal record or a named blob is not a gap (`Repository.Index/SequenceAccounting`, `ArchitectureTests/IndexLoadAccountingTests`). The patience still never runs out |
| 2026-10 | Partly built (amended) | [Amendment 1](#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first): the deltas, their chains, void deltas and the loader are built. Nothing publishes a checkpoint, nothing retires a delta, and the gap patience never runs out because the generation never advances. The 2026-09 row's "Built" read the codec as the decision. The amendment states six constraints a checkpoint must meet before one is published: rank, writer attribution, watermark accounting, a readable size, digests and roots, and retirement across copies |
| 2026-09 | Built (witness extended) | The rollback witness read at archive open reaches only the plane the writer publishes into, and for a direct-ship set that plane is the state directory itself; [ADR-0062](0062-the-destination-is-the-rollback-witness.md) reads the same journal keys at the destination on every fan-out pass (`Repository.Index/ObservedHead`'s `JournalHeadAsync`), which is the one copy a state directory rolled back whole does not take with it |
| 2026-08 | Proposed | |
| 2026-08 | Accepted (amended) | Commutativity justification withdrawn as false and replaced by explicit generation precedence (PT-2, critical). Void deltas added for gap closure (PT-6); retirement now requires uncontested coverage (PT-7). Chain mechanism itself unchanged. |

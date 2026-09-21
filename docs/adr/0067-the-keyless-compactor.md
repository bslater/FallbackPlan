# ADR-0067 — The keyless compactor

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-GC-011, FR-GC-003, FR-GC-004, FR-GC-005, FR-MAN-015, FR-MAN-019, FR-WOR-003, NFR-SEC-003
**Related:** [ADR-0025](0025-compaction-reseals-records.md), [ADR-0052](0052-relocatable-records-format-v3.md), [ADR-0066](0066-the-format-upgrade-record.md), [ADR-0009](0009-garbage-collection-safety.md), [ADR-0017](0017-index-entry-supersession.md), [ADR-0007](0007-logical-object-identifiers-in-manifests.md), [ADR-0042](0042-write-only-repositories.md), [ADR-0046](0046-direct-to-destination-publication.md), [repository-format 05 §5](../../specifications/repository-format/05-blob.md), [repository-format 07 §3](../../specifications/repository-format/07-index.md)

**Built:** `Retention/CompactionPolicy` (`CompactableBlob`, the dead-fraction and reclaim floor, the byte budget, and `Select` — one decision the dry run and the act share), `Retention/CollectionPlanner` (the backlog named rather than counted, and the record whose location the index has moved counted dead where its bytes still are), `Retention/RetentionRunner` (the selection on the report, before the apply early return, under the effective-format gate), `Repository.Packing/BlobReader` (`ReadSealedRecordAsync` — the one read with no key path at all, sharing the header/table cross-check), `Repository.Packing/BlobWriter` (`AppendSealedRecordAsync`, which re-frames the header and copies the sealed bytes verbatim), `Repository/BlobCompactor` (the rewrite, holding a structure key per source generation and no content key), `Repository/CompactionPublication` (the supersessions, the covered commitments, and the split that keeps a delta readable), `Repository/CompactionPass` (intent, seal, upload under extensions, publish, retire last), `Repository.Catalogue/Forensic/ForensicRebuilder` (one delta per blob, so a rebuilt index holds every record a blob carries), `Agent/ServiceCommandHandler` (the compaction phase of `retention --apply`, and the catalogue resolver the collector plans with); `Retention.Tests/CompactionPolicyTests`, `Repository.Tests/Packing/BlobCompactionTests`, `Repository.Tests/Index/CompactionIndexTests`, `Repository.Tests/EndToEnd/CompactedRestoreTests`, `InterruptionTests/CompactionInterruptionTests`, `Retention.Tests/CompactionCollectionTests`, `Hosts.Tests/CompactionRetentionTests`. The tombstone's reason is derived at condemnation by `Retention/CollectionPlanner` and written by `Retention/StagingSweep` (2026-09 amendment).

---

## Context

[ADR-0025](0025-compaction-reseals-records.md) has been *Specified only*
since it was written, and `Retention/CollectionPlanner` has been printing
*"kept whole for a live minority: N blob(s) … (compaction is a later phase)"*
for as long. A blob holding one live record out of fifty is retained whole,
for ever, because the only thing the product could do with it was nothing.

[ADR-0052](0052-relocatable-records-format-v3.md) and the three slices that
built format 3 exist so that it need not stay that way. This record is what
they were for.

## The finding that makes it possible at all

At format 2 compaction is decrypt-and-reseal, and on the shape every set now
has, **a service cannot do it at all**. It holds the structure key and not the
content key ([FR-WOR-003](../requirements/functional.md),
[ADR-0042](0042-write-only-repositories.md)), so re-sealing a record would
need a passphrase nobody is present to type. ADR-0025's cost accounting reads
as though compaction there were merely expensive — bounded by AEAD speed
rather than copy speed — and its Negative consequence concedes only that "a
compactor cannot run keyless". On a write-only set the truth is stronger:
there is no compactor at all, keyless or otherwise.

At format 3 a record's key derives from its own object identifier, its nonce
rides its own prefix, and the 51-byte associated data names no position. The
sealed bytes move verbatim: `BlobWriter.AppendSealedRecordAsync` copies
prefix, ciphertext and tag and re-frames only the 54-byte header with the
destination ordinal. The compactor never opens a record and holds no key that
could.

## Decision

**1. The rewrite holds no key that could open a record.** `BlobCompactor` is
constructed with the repository's structure keys — one per source blob's key
generation, because a rotated repository's older blobs are exactly the ones
worth compacting — and never with a content key. `BlobReader` gained
`ReadSealedRecordAsync` for it: the one read in the product with no key path
at all, sharing `ReadRecordAsync`'s header/table cross-check so that a
compactor cannot faithfully relocate corruption. A test constructs the
compactor holding nothing that could decrypt and watches it complete, which
is the claim stated as an assertion rather than as a paragraph.

**2. The order is the interruption discipline.** A pass publishes a write
intent, seals its blobs, uploads them under per-blob intent extensions,
publishes the supersessions, and retires the intent **last**. Cut anywhere in
that sequence and the produced blobs are covered by an unretired intent,
which [ADR-0009](0009-garbage-collection-safety.md) step 4 already treats as
reachability — so the collector that would otherwise delete a half-written
compaction blob is the very thing that protects it, with no new mechanism.
Retire before publishing and there is a window in which the produced blobs
are covered by nothing and named by nothing; that window is why the order is
the decision rather than an implementation detail.

**3. The pass deletes nothing.** A drained blob is not tombstoned by the
compactor. It is condemned by the collector on its own terms, on a later
pass, because every record it holds now resolves elsewhere — which is a fact
about the index, arrived at by the planner rather than asserted by the thing
that moved the bytes. The tombstone, its grace and the sweep's pre-delete
revalidation (FR-GC-006) apply unchanged, which is what makes an interrupted
compaction safe without the compactor knowing anything about deletion.

**4. A supersession condemns only against a blob that is present.** The
inverse of ADR-0025's exit criterion 12, and the one that loses data if it is
got wrong. `CollectionPlanner` counts a record dead where its bytes still are
only when the blob the index names is one the reader actually opened; an
index entry pointing into a blob nobody holds condemns nothing. A partial
listing, a destination mid-seed or a delta that ran ahead of its blob can
each produce that state, and in every one of them the safe answer is to keep
the bytes.

**5. Format 3 or nothing, refused by name, and quiet about it when it should
be.** A set below format 3 selects no candidates and is told why: re-sealing
there needs a content key this service does not hold, and the remedy is
`upgrade_set_format`. The line is printed **only when a backlog would
otherwise have produced candidates** —
[ADR-0066](0066-the-format-upgrade-record.md) decision 6 settled that both
formats are supported, that nothing is pushed, and that acknowledging the
`format-upgradable` notice silences it for good; a retention line repeating
the offer every pass would re-open what that acknowledgement closed.

**6. Peers are excluded by the ship sink, not by the compactor.** Outside a
run `DestinationShipSink.ReadOrder()` resolves fresh from the configuration
and takes local paths only, because a peer shipment is a live session rather
than a directory. Retention runs outside a run, so a direct-ship set reads
its candidates back from local-path destinations in priority order and never
from a peer, with no guard written for the purpose. Two consequences follow
and are stated rather than coded: a set whose destinations are all peers
lists no blobs through the sink, so its plan vetoes and there is nothing to
compact; and a set with every local path away is the same case — the pass
completes, reports, and rewrites nothing.

**7. The selection happens once.** `CompactionPolicy.Select` decides and
describes in the same call, before the runner's `apply` early return, so the
mandatory dry run (FR-GC-005) and the act cannot disagree about what a pass
would rewrite. A plan the collector vetoed selects nothing: a pass that
cannot say what is garbage cannot say what is worth rewriting either.

## Consequences

**Positive**

- A blob holding a live minority stops being a permanent tax. The backlog
  the planner has been counting since it was written now shrinks.
- The rewrite is a copy, not a re-encryption, so throughput is bounded by
  storage rather than by AEAD — the cost ADR-0025 §4 budgeted for is not
  paid at all at format 3.
- A produced blob is an ordinary sealed blob: it gets its own digest and its
  own Merkle root from the ordinary sealer, so a peer can be challenged for
  one leaf of it ([ADR-0065](0065-merkle-commitment-and-chunk-possession.md))
  the day it lands.
- The compactor is the first producer of an index supersession
  ([ADR-0017](0017-index-entry-supersession.md)). The precedence code that
  had only ever been resolved against is now exercised against something that
  writes.

**Negative**

- **The space comes back two passes after the rewrite, not one.** Compaction
  runs at the end of a pass, after the sweep; the drained blobs are condemned
  by the *next* pass's plan and swept by the one after. The alternative — a
  compaction callback inside `RetentionRunner` between the plan and the
  tombstone — buys one pass and costs a second blob-footer walk, a second
  journal read (the grace clock must be recomputed past the compaction's own
  records, or the tombstone is born already eligible and the grace is no
  grace at all), and a sweep revalidating against a world the same pass just
  wrote. On a maintenance operation that runs on a schedule, a day is worth
  less than that property.
- ~~**A drained blob is tombstoned with reason *unreferenced*** although
  specification [11 §3](../../specifications/repository-format/11-lifecycle-objects.md#3-tombstone)
  defines a *compacted* reason for exactly this. The collector condemns by
  plan and does not know provenance; carrying it would mean the compactor
  telling the collector what it did, which is the coupling decision 3 exists
  to avoid. A code change, and not this record's.~~

  > **Withdrawn (2026-09).** The premise was wrong, not merely overtaken. The
  > collector does know: `Retention/CollectionPlanner` was already
  > distinguishing the two ways a record can be dead, because that
  > distinction is what decides condemnation at all. No coupling was needed
  > and nothing is carried from the compactor — see the amendment below.
- **A peer's replica is never compacted**, so a peer-only set's backlog is a
  backlog for ever. Compacting one means pulling a whole blob over a domestic
  uplink and pushing a new one back to reclaim space on someone else's disk.
- **Nothing compacts a format-2 set**, and nothing will. The remedy is the
  upgrade, which is append-only and does not rewrite a byte.

## Alternatives considered

**Compact inside `RetentionRunner`, between the plan and the tombstone.**
Reclaims in one pass instead of two. Rejected on the three costs above; the
tombstone's grace already imposes a pass, so the saving is one scheduled run.

**Let the compactor tombstone what it drained.** Simpler to read and one pass
faster. It makes the thing that moved the bytes also the thing that condemns
them, so a compactor bug becomes a deletion bug; and it would have to
reproduce the planner's reachability, the grace clock and the revalidation
that FR-GC-006 requires. Rejected: the collector reaching its own conclusion
is the safety property, not a formality.

**Decrypt and re-seal at format 2 under a restore grant.** The passphrase
already reaches the service for a granted restore, so a compaction grant is
imaginable. It would put the content key in the service for a maintenance
operation that runs unattended on a schedule — the opposite of what
[ADR-0055](0055-reclaim-authority.md) and the write-only shape are for — and
it would cost AEAD throughput on every byte. Rejected.

**Compact at the destination.** The destination holds the bytes and the link
cost is zero. It holds no repository keys and may not allocate a sequence
number ([ADR-0034](0034-hub-and-spoke-destinations.md)); a destination that
could compact would have the keys, which is the property the whole design
refuses. Rejected, as ADR-0025 Amendment 1 rejected it.

## What this does not do

- It does not choose a candidate by anything but dead bytes: age, access
  pattern and locality play no part. A blob half dead is a candidate whether
  it was written yesterday or last year.
- It does not reclaim space at a peer, and it does not compact a staging
  archive's blobs on behalf of a destination that holds its own copy.
- It does not touch a manifest, a tree or a snapshot (FR-GC-004). The only
  thing it publishes is index entries, which is what
  [ADR-0007](0007-logical-object-identifiers-in-manifests.md) bought by
  keeping physical location out of manifests in the first place.

### Amendment (2026-09): the reason is derived, and the limit above rested on a premise the planner contradicts

This record named a limit it did not have. It said the collector "condemns by
plan and does not know provenance", and that carrying the provenance would
require the compactor to tell the collector what it had done — the coupling
decision 3 exists to avoid. Both halves were wrong.

`Retention/CollectionPlanner` already computed the distinction. A record here
is dead in one of exactly two ways, and the planner separates them to decide
condemnation at all: either nothing reaches the object, or it is still reached
and the index resolves it into a **different blob that is present**. The
second is a relocation, and a relocation is what *compacted* names. So the
reason is derived where the planner already stands — no durable state, nothing
threaded across the two passes that separate a rewrite from its reclaim, and
no message from the compactor. Decision 3 is untouched: the collector still
reaches its own conclusion on its own terms, and now says which one.

Finding it turned up something larger. The reason field had never carried
information at all: both of `Retention/StagingSweep`'s call sites hard-coded
*unreferenced*, so reasons 2, 3 and 4 were declared, encoded, decoded,
validated on read and round-tripped by tests, and written by nothing. That
matters because the reason is inside the tombstone's signed bytes
(11 §3, keys 1–7) — the repository plane's half of FR-GC-008's promise of
signed audit records. A constant is not a claim.

What is produced and what is not, stated so the closed vocabulary does not
read as a gap: *unreferenced* and *compacted* are written; *retired delta* is
unreachable, because nothing tombstones an index delta; *superseded* describes
a newer object replacing an older, which an expiring snapshot manifest is not.

A mixed blob is *compacted*, and that is what happened rather than a rounding:
a compactor carries the live records and leaves the rest, so whatever it did
not carry was already unreachable. A blob is drained by the rewrite or it is
not.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | The keyless compactor over `Repository.Packing/BlobWriter`'s `AppendSealedRecordAsync`, built in five commits; [ADR-0025](0025-compaction-reseals-records.md)'s twelve exit criteria answered one by one, and its *Specified only* row retired |
| 2026-09 | Amended | The first named follow-up withdrawn rather than deferred: the reason a drained blob is tombstoned with is derived by `Retention/CollectionPlanner` from the distinction it already computes, so the limit rested on a premise its own planner contradicts. Found in passing that the reason field had never carried information at all — both of `Retention/StagingSweep`'s call sites hard-coded *unreferenced* |

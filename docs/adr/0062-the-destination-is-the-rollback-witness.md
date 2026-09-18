# ADR-0062 — The destination is the rollback witness

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-DEST-018, NFR-SEC-005
**Related:** [ADR-0046](0046-direct-to-destination-publication.md), [ADR-0008](0008-index-generations-and-checkpoints.md), [ADR-0056](0056-incremental-reconciliation.md), [ADR-0058](0058-peer-write-adapter.md), [ADR-0061](0061-adopt-a-destinations-archives.md), [architecture 03 §6](../architecture/03-crypto.md#6-authentication-of-repository-state), [architecture 09 §4](../architecture/09-replication-and-peers.md#4-durability-policy)

**Built:** `Agent/FanOut` (the detector, the protection and the heal's trigger in the local-path pass and, since Amendment 1, in the peer push), `Agent/ReplicationInitiator` (the inventory hook a push consults before it filters or drops anything), `Agent/ServiceRuntime` (`HealFromDestinationAsync`, over a local replica or a `PeerRetrievalObjectStore`), `Repository.Index/ObservedHead` (`JournalHeadAsync`, and `JournalHeadOf` over keys already in hand), `Agent/CatalogueRebuild` (the rebuild in place); `Hosts.Tests/DirectoryRollbackTests`, `Hosts.Tests/PeerRollbackTests`, `Repository.Tests/ObservedHeadTests`.

---

## Context

The proof-obligation table has carried this row since it was written:

> **A whole state directory rolled back together is detected** — *falsified
> by:* restore state directory *and* metadata store from one older copy,
> leaving the destinations ahead — **Unproved** (NFR-SEC-005): the observed
> head is read from the repository the writer publishes into, and for a
> direct-ship set that plane is local, so a consistent rollback of the whole
> directory rolls back the witness with it. The destinations hold the newer
> index and nothing consults them.

Slice 3.2 built the rollback witness [ADR-0008](0008-index-generations-and-checkpoints.md)
implied: at archive open the service asks the repository how far this
writer had got — signed checkpoint watermarks, signed delta sequences,
journal keys — and moves the local sequence past it when local allocation
state turns out to be behind its own published history
(`Hosts.Tests/ObservedHeadAdoptionTests`). That closed the case of a lost
or rolled-back **sequence file**. It could not close the case of a whole
state directory restored from an older copy, because for a direct-ship set
([ADR-0046](0046-direct-to-destination-publication.md)) the archive's
metadata plane *is* the state directory: the catalogue, the sequence file,
the sync ledger and the metadata store roll back together, and the archive
then attests exactly what the rolled-back sequence file says. Every local
witness agrees with the rollback because every local witness was rolled
back with it.

The harm is concrete, and worse than a collision. A collision — the next
backup handing out sequence numbers the destination already holds — is
caught by the store's refusal of a colliding put, partway through a run,
as an I/O error. Before that run happens, though, the next **fan-out pass**
runs, and under a per-destination retention policy it computes the
destination's keep-set from the metadata it has (FR-GC-010). Rolled-back
metadata knows only the older history, so the keep-set is the older
history, and the pass converges the destination down to it — deleting the
newest backup from the only place that holds it, and reporting a clean
pass. The one copy that survived the rollback is destroyed by the machinery
that exists to keep destinations current.

One thing did not roll back: the destination. It holds every object the
writer published, and its journal keys carry the writer's sequence in the
clear (`journal/<writer>/<sequence>`, [repository format 08 §1](../../specifications/repository-format/08-journal.md)).
This record makes the destination the witness.

## Decision

### 1. The allocator is the detector

On every fan-out pass to a local-path destination, once the replica store
exists, the pass reads the highest journal sequence the destination attests
**for this writer** — `ObservedHead.JournalHeadAsync`, one prefix listing,
no reads, no credential — and offers it to the writer sequence's
`AdoptObservedHead`. That method only ever raises, and it answers `Adopted`
only when the attested head reaches the next number the writer would hand
out. Numbers are allocated before anything is written, so a destination
attesting a number this writer has not yet allocated is a rollback of the
allocation state and nothing else: not a destination ahead of a slow
sibling, not a run in flight, not a second device. The sequence moves past
the attested head, durably, before the pass does anything else.

Per writer, deliberately. Two devices writing one repository is a supported
shape ([ADR-0008](0008-index-generations-and-checkpoints.md)), and a
survey of *all* sequences at the destination — snapshot counters, other
writers' journals — would trip on the second device's ordinary progress.
The question is only ever "how far had **I** got, as far as this replica
knows", and the journal key answers it per writer by construction.

The detector runs before the reconciliation gate
([ADR-0056](0056-incremental-reconciliation.md)). The gate reads the sync
ledger, and the ledger rolled back with everything else; left to itself it
would answer "nothing to look at" from the rollback's own opinion of itself.

### 2. The detecting pass never deletes

A pass that has detected a rollback keeps everything at the destination:
no keep-set is computed, so no convergence runs and no spares are needed
— `StoreToStoreCopier.CopyAsync` never deletes; only `ConvergeAsync` does
— and the scope is forced to read through. The retention policy is simply
not applied on that pass. It is applied on the next one, over metadata the
heal below has made current, which is the order that makes it safe.

The pass raises a notice, `destination-ahead:<set>:<destination>`, that is
never auto-resolved — the posture of the destination-shortfall notice: the
next pass is quiet because the writer moved, not because the rollback did
not happen, and an operator whose machine was restored from an older image
should learn it from the product rather than from a collision. The notice
names the sequence the destination attests and the one local state said,
and says what was done. Log event 3773 carries the same fact.

### 3. A direct-ship set is healed in place, from the destination

For a direct-ship set the pass then makes the local state current again,
under the set gate, inside the same pass, in three steps that are each
idempotent and each safe to repeat after a failure:

1. **The destination's metadata is copied back** into the set's metadata
   store — the same if-absent copy archive adoption
   ([ADR-0061](0061-adopt-a-destinations-archives.md)) and the staging
   migration use. Every metadata key is append-only (`index/delta/<gen>/<id>`,
   `index/checkpoint/<gen>/<id>`, `journal/<writer>/<seq>`,
   `snapshots/<device>/<set>/<snapshot>`), so nothing newer can be
   overwritten by something older, and a copy interrupted halfway resumes.
2. **The catalogue is rebuilt in place** over the runtime's live handle,
   reading the index plane and the metadata blobs from the replica. Every
   write the rebuild makes is an upsert or an insert-or-ignore, so it adds
   what is missing and disturbs nothing that is there — which is what lets
   it run without evicting the archive under a read path.
3. **The writer is moved past whatever the healed archive now attests**,
   through the same adoption archive open performs, so the index plane's
   watermarks count as well as the journal's.

**What triggers the heal is the metadata plane, not the allocator.** The
pass heals whenever the destination's journal head for this writer exceeds
the *local metadata store's*. The distinction matters for one case: a heal
that fails halfway. The sequence moved durably on the first pass and would
never detect again, so a retry keyed on detection would never happen;
keyed on the metadata still being behind, it happens on every pass until
the copy succeeds. A failed heal changes nothing at the destination,
records the pair as failed with the reason rather than as synced, and the
notice says what could not be copied back and that the next pass retries.

**The sequence moves before the copy.** If the machine dies between the
two, the next backup still cannot collide, and the next pass finds the
metadata still behind and copies again. The other order would leave a
window in which a healed catalogue sat beside an allocation state that was
about to hand out a number the destination holds.

### 4. What this does not do, stated

- **A staging set is protected and told, not healed.** Its archive lives
  outside the state directory, so a rollback of the state directory alone
  is the case slice 3.2 already handles at open. A machine restored whole
  — state directory and archives root together — is witnessed here the
  same way, and the detecting pass deletes nothing; but what the staging
  archive lacks is *content*, not metadata, and copying snapshot manifests
  back without the blobs they reference would produce an archive that
  lists history it cannot restore and a replication gate (FR-GC-009) that
  believes it holds that history. The notice therefore says the one thing
  that is true and unwelcome: the newer history is only at the
  destination, and the next converging pass will trim the destination to
  the staging archive's keep-set — restore or copy it aside first if it
  matters. A content copy-back is the same seam reversed and is the named
  follow-up if a staging set ever wants it.
- **Peers are not asked.** The read is a journal listing over the replica,
  which a peer serves through the retrieval session
  ([peer-protocol 07](../../specifications/peer-protocol/07-retrieval.md)); the
  same head read over a `PeerRetrievalObjectStore` opened once per pass is
  the follow-up, and until it lands a set whose only destination is a peer
  is witnessed by nothing but the colliding-put refusal.

  > **Amended 2026-09.** Built, and not the way this bullet expected: a peer
  > is asked nothing, because it already answers. See
  > [Amendment 1](#amendment-1--the-peer-is-a-witness-too-from-the-inventory-it-already-declares-2026-09).
- **A rollback that reaches the only destination too is undetectable.** If
  the state directory and every destination are restored from one older
  image, nothing is ahead of anything, and there is no witness. That is
  not a limit of this design but of the question: a system restored
  consistently to an earlier point has not been rolled back from
  anything it still knows about.
- **Blob counters are not consulted**, for the reason
  [ADR-0008](0008-index-generations-and-checkpoints.md)'s witness already
  states: they sit inside sealed payloads, and a run interrupted after its
  blobs and before its delta is an unaccounted obligation the index reports
  as a gap. The colliding-put refusal remains the last line behind this
  witness as it was behind the first.

## Consequences

**Positive**

- Proof-obligation row 86 is proved, for local-path destinations: a
  direct-ship set whose state directory was restored from an image taken
  between two backups notices on the next pass, keeps the newest backup at
  the destination, and is current again before the next backup.
- The mechanism is a listing per pass and no cryptography: the journal key
  already carries the fact in the clear, which is why the witness costs
  nothing at the destination and nothing on the wire.
- The heal reuses three things that existed — the adoption copy, the
  restore-source rebuild, the open-time adoption — rather than adding a
  fourth way to make a catalogue.

**Negative**

- One more listing per fan-out pass per local-path destination, on the
  journal prefix alone. Bounded by the writer's history, not the repository.
- A rollback under a per-destination policy leaves the destination holding
  its keep-set *plus* whatever the rollback's older metadata still lists;
  the next pass converges it. One pass of slack, by design.
- A staging set gets a warning where a direct-ship set gets a repair. The
  asymmetry is the archive's, not the witness's, and is written above
  rather than smoothed over.

## Alternatives considered

**A survey of every sequence at the destination** — the snapshot counter,
every writer's journal — compared with the local publication counter. It
would have fired on a second device's ordinary progress into a shared
repository, and it would have compared a number the rollback rolled back
with one it did not, which is a weaker test than "has this writer allocated
this number yet".

**Evict the archive and rebuild the catalogue at next open.** Simpler than
rebuilding in place, and wrong on timing: the pass holds the archive under
the set gate, a read path may hold a read catalogue, and the window between
eviction and reopen is a window in which the next backup could run against
a stale catalogue. The in-place rebuild is what the catalogue's own write
semantics already permit, and it is kept in one place with its two existing
callers so it cannot drift from them. Evict-and-reopen remains the fallback
if the projector ever stops being idempotent.

**A witness file at the destination** — a small record the writer stamps
with its head on every pass, read back before the next. It would have been
a new artefact, per destination, with its own integrity question (who
signs it, against what key a destination cannot hold), and it would have
told the pass nothing the journal keys do not already say. The keys are
the record; they were simply never read from this side.

**Refuse the pass rather than protect it.** Honest, and worse: the pass
that refuses leaves the destination one scheduled pass away from the same
convergence, with nothing healed. Protecting the pass and healing inside it
turns a refusal into a repair and leaves a notice where the refusal would
have left a log line.

## Amendment 1 — the peer is a witness too, from the inventory it already declares (2026-09)

§4 left a peer destination out and named a retrieval-session read as the
follow-up. Building it found a cheaper witness already on the wire: every
push opens by reading the peer's **complete** inventory — every key it holds
for the repository, journal keys included
([peer-protocol 03 §3.2](../../specifications/peer-protocol/03-replication.md)).
The journal head for this writer is a fold over that list
(`ObservedHead.JournalHeadOf`), so the peer is witnessed on every sync pass
with no second session and no read at all.

What made it more than a listing was the order of events inside a push. The
retention instruction is decided **before** the inventory arrives: the
keep-set is computed from local — possibly rolled-back — metadata, filters
the push, and drives the drop half in the same session. A detector that ran
after the push would have watched the rolled-back keep-set act. So
`ReplicationInitiator.PushAndConvergeAsync` takes a hook invoked once, after
the inventory and before anything is filtered or dropped: `FanOut` offers
the attested head to the allocator exactly as decision 1 does for a local
path, and when it is adopted the hook withholds convergence for the whole
session — the push goes unfiltered, no instruction is sent, and the outcome
says so (`ConvergenceWithheld`). The granted collection run
([ADR-0055](0055-reclaim-authority.md) §6) reaches the peer through the same
push, so one hook protects both paths.

The harm on the peer path is not the local path's, and the drill that pins
it is shaped accordingly. A peer keeps what the source no longer lists
([ADR-0034 §6](0034-hub-and-spoke-destinations.md)), so a rolled-back
keep-set cannot condemn the newer history's *metadata* — only keys the
source lists can be dropped. What it can reach is a blob the older history
owns and the newer history shares: a direct-ship set lists its blobs
through the destinations themselves, so under a keep-newest policy the
granted convergence run would drop that blob at the peer while keeping the
manifests that point at it. And short of any deletion, the next backup
hands out sequences the peer already holds under different bytes: the push
skips a key the inventory declares, and the replica's journal silently
stops matching the source's. `Hosts.Tests/PeerRollbackTests` holds both —
three backups reverting to the first, so the third dedups against a blob
the rolled-back policy no longer keeps; and a third backup after the heal
whose every journal record is at the peer byte for byte.

The heal is decision 3's, over the retrieval session: keyed on the metadata
plane being behind the peer's attested head, `FanOut` dials
`PeerRetrievalClient`, wraps a `PeerRetrievalObjectStore`, and hands it to
`HealFromDestinationAsync` unchanged. A dial or protocol failure is a heal
failure — the pair recorded as failed with the reason, no success stamp, the
next pass retrying — and never a finding against the peer. A staging set is
protected and told, as at a local path; its notice says the peer keeps what
the staging archive no longer lists.

What stays out: `PeerShipStore` reads the same inventory at run open and is
left alone — detection belongs to the sync pass on both kinds of
destination, and a run is not the place to start healing. The last stated
limit stands: a rollback that reaches every destination too has no witness
anywhere.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Amended | [Amendment 1](#amendment-1--the-peer-is-a-witness-too-from-the-inventory-it-already-declares-2026-09): a peer destination is witnessed from the inventory every push already reads, before the push filters or drops anything. `Agent/ReplicationInitiator` takes the inventory hook and reports a withheld convergence; `Agent/FanOut` adopts, protects both the sync pass and the granted collection run, and heals a direct-ship set over the retrieval session; `Repository.Index/ObservedHead` folds the head from keys already in hand. `Hosts.Tests/PeerRollbackTests` is the drill, including the mixed-set convergence that would have dropped a shared blob |
| 2026-09 | Accepted | Built over four commits: the journal head on its own and the catalogue rebuild in one place (`Repository.Index/ObservedHead`, `Agent/CatalogueRebuild`); the detector, the protection and the notice in `Agent/FanOut`; the heal in `Agent/ServiceRuntime`; `Hosts.Tests/DirectoryRollbackTests` is the drill — a direct-ship set's state directory restored from a copy taken between two backups, the next pass noticing, deleting nothing, healing and running a third backup, plus the cry-wolf guard, the staging variant and a failed copy-back retried — with `Repository.Tests/ObservedHeadTests` on the primitive. `eng/recovery-drill.sh` green on the Release binaries |

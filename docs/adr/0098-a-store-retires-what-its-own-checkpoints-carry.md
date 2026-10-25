# ADR-0098 — A store retires what its own checkpoints carry

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-MAN-017 (amended: a delta is retired from a store only when a checkpoint there carries a sound, identical copy of it and its writer has published there since; concurrent checkpoints need no tie-break); FR-MAN-008 (a load racing a retirement reads the retired delta from its carrier); FR-MAN-013 (a delta carried by two checkpoints is retired once and applied once)
**Related:** [ADR-0008](0008-index-generations-and-checkpoints.md) Amendment 1 constraint 6, and Amendment 3, which records this; [ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md) (format 5, whose checkpoint carries the deltas this retires); [ADR-0029](0029-pipeline-and-service-concurrency.md) Amendment 2 (the set gate); [ADR-0046](0046-direct-to-destination-publication.md) (direct-ship and the staging migration); [ADR-0034](0034-hub-and-spoke-destinations.md) §6 (a peer is told to drop only what staging lists); [specification 07 §5.2, §7](../../specifications/repository-format/07-index.md#7-delta-retirement)

**Built:**
- The reader: `Repository.Index/IndexLoader` lists the deltas before the checkpoints, and looks for a listed delta that is gone at its key in a second listing of the checkpoints. Only a delta still uncarried after that is reported.
- The rule: `Repository.Index/DeltaRetirer` deletes a delta's key from a store when a checkpoint at that store carries a copy that opens and equals the object at the key, and the checkpoint's writer has a journal key there above the checkpoint's own number. It touches no store whose listing may lag, and retires only what its caller admits. `Repository.Index/CheckpointCoverage` remembers what each checkpoint carries, so a pass opens only those carrying something still present.
- The set's plane: `Agent/BackupRunner` retires at the staging archive, or a direct-ship set's metadata store (`Agent/ArchiveHandle`'s plane), after the run's checkpoint, only while nothing else holds the set.
- The destinations: `Agent/FanOut` retires at a local path or object store after a sync's copy and proof, only what the plane no longer lists. A direct-ship run retires at the destinations it wrote (`Agent/DestinationShipSink`).
- The copies: `Replication/StoreToStoreCopier` and `Agent/ServiceRuntime`'s copy-back skip a delta retired under them and still carry its checkpoint. `Agent/ServiceCommandHandler.Configuration.cs` lets staging go when the metadata store retired a delta only staging still holds.
- The tests:
  - `Repository.Tests/Index/RetiredDeltaLoadTests`: a delta retired after the checkpoint listing is read from the checkpoint a second listing shows, once; one retired after the delta listing is read from the checkpoint listed after it; a listed delta that vanishes uncarried is reported by key; and with nothing gone, the checkpoints are listed once
  - `Repository.Tests/Index/DeltaRetirerTests`: a settled carried delta is retired and a load is unchanged; an unsettled, uncarried, unopenable or mismatched one is kept; concurrent checkpoints from two writers carrying different sets retire every delta and lose no entry; only admitted deltas go; a store whose listing may lag is not even listed; and a known checkpoint is not reopened
  - `Hosts.Tests/DeltaRetirementServiceTests`: a staging set and a direct-ship set retire at their own plane after a later run; a busy set retires nothing and the next run does; a local-path destination retires in its sync and restores the retired delta's snapshot alone; a destination retires nothing the plane still holds; a direct-ship run retires at its destinations; retire_staging takes a retired delta for migrated and the history it held restores from the destination; a copy-back skips a retired delta and copies the checkpoint published since its listing
  - `Hosts.Tests/S3DestinationTests`, `Hosts.Tests/AzureBlobDestinationTests` (through `Hosts.Tests/ObjectStoreDestinationTests`): the store loses the carried deltas to deletes in the pass after the plane did, and alone restores the first snapshot
  - `Replication.Tests/CopierRetiredDeltaTests`: a copy and a converge skip a delta retired under them and copy its carrier; any other object that vanishes still fails the copy

---

## Context

A format-5 checkpoint carries the deltas it subsumes, verbatim ([ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md)). A load reads a carried delta from the checkpoint and stops fetching it from its key, but the key stays. Every carried delta is stored twice, at staging and at every destination, and a load still lists them all.

[ADR-0008 Amendment 1](0008-index-generations-and-checkpoints.md#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first) set its sixth constraint for retirement: it must survive the copies. It found five ways it would not:
- **No identity.** A tombstone names an object identifier, and a delta is found by the random identifier in its key.
- **The copies bring it back.** A heal, an adoption or a migration copies every index key back into a plane that retired one.
- **Readers mid-flight.** A loader or a push that listed a delta and reads it a moment later.
- **retire_staging would refuse for ever.** It holds the staging archive while staging has an index object the metadata store lacks.
- **A delete through the sink is partial.** It reaches some destinations and not others, and a delta left at one is never listed again.

The rule the record began with ([ADR-0008](0008-index-generations-and-checkpoints.md) §Retirement, from [PT-7](../review/2026-08-fix-pressure-test.md#pt-7--delta-retirement-is-ambiguous-under-concurrent-checkpoints)) was written for checkpoints that restated their deltas' entries. A delta could be retired only when no live checkpoint at or above its generation *contradicted* it, under tombstone and grace. At format 5 a checkpoint restates nothing, so omitting a delta contradicts nothing. The hazard PT-7 named is a reader that holds only the checkpoint that does not carry the delta. That hazard needs a different answer.

## Decision

### 1 The decision is each store's, from what that store holds

A delta may be deleted from a store when, at that store and at that moment:
- **a checkpoint carries it**, in a copy that opens as the delta's own object would (07 §5.2);
- **the copy is the object at the delta's key**, byte for byte;
- **the checkpoint is settled**: its writer has a journal key there above the checkpoint's own sequence number;
- **the store's listing is strong**: it shows every put that completed before it began.

Nothing is taken on another store's word. The facts a staging archive retires on are read from the staging archive, and the facts a destination retires on are read from the destination. Each copy of the repository decides for itself, so a delete never has to reach every copy to be safe, and a partial one leaves nothing inconsistent.

**Checkpoints are never deleted.** That is what makes concurrent checkpoints harmless. Two writers' checkpoints may carry different sets: one 0 to 3, the other 1 to 5. A delta may go on either one's authority, because both stay at the store, and every reader of the store lists both and applies every delta either carries. PT-7's stranded reader would have to hold a store without the checkpoint the delta went on. The first condition rules that store out.

**The copy must equal the object at the key.** A copy that opens proves *a* delta, not *that* delta. Deleting a key on the word of a copy named for the wrong delta would lose the one really there. The comparison costs one read of a small object, once per delta retired.

**The grace is one publication, not a time.** A backup's first act is a write intent, a journal record, so the writer's next backup settles its last checkpoint. A retirement in the same run as its checkpoint therefore leaves that checkpoint's deltas for the next run. The journal key carries its sequence in the clear (08 §1), so this costs a prefix listing and no read. What the grace buys is the margin for a reader built before §2. Such a reader lists checkpoints first, and misses a delta only if its load spans both the checkpoint's put and the deletion. The deletion now comes a whole publication later.

**A store whose listing may lag retires nothing.** §2's reader is sound only where a listing shows every completed put. On such a store retirement is declined outright, and nothing is even listed.

There is no tombstone. A tombstone protects a reader that resolved an object by identifier and will fetch it later. A delta is not resolved by identifier, and the reader that might fetch it later has its own rule (§2). [Specification 11 §3](../../specifications/repository-format/11-lifecycle-objects.md#3-tombstone)'s *retired delta* reason stays defined and unused.

### 2 A reader lists the deltas before the checkpoints, and looks again for one gone

A reader lists the deltas, then the checkpoints, and only then fetches the listed deltas no opened copy carries ([07 §5.2](../../specifications/repository-format/07-index.md#52-reading-a-checkpoint)). A delta is deleted only after its carrier is durable at the same store, so:
- **A delta the listing misses was retired before the listing began.** Its carrier was durable before that, so the checkpoint listing that follows shows it.
- **A delta the listing returns is either still at its key when fetched, or found gone.** If it is gone, its carrier was durable before the delete, and the delete came before the failed fetch. A second listing of the checkpoints shows it.

The second listing runs only when a listed delta was gone, and opens only the checkpoints the first did not show. A delta still uncarried after it is the damage it always was: a missing index object, by key. A delta read from its key whose carrier appears only in the second listing is applied once.

### 3 The set's plane retires after the run's checkpoint, while nothing else holds the set

After a format-5 backup commits and checkpoints, the run retires at the set's plane. The plane is the staging archive, or a direct-ship set's metadata store, never through the sink: a delete through the sink would reach every destination without each one deciding.

The run takes the set gate only if it is free. A sync copying the plane, a sweep reading it and retention trimming it all hold the gate, and a delta deleted under a copy would leave it proving what it no longer reads. A backup has never taken the gate, and waiting on it would hold the run's settlement, and the set's next run, behind a sync of hours. A busy run therefore retires nothing, says so at Debug, and leaves it to the next run. Both this and a failure are best effort: a delta left at its key is only stored twice for one more run.

### 4 A destination retires only what the plane has let go

A local path or an object store retires by the same rule (§1), from what it holds itself, and admits only the deltas the plane no longer lists. A destination that ran ahead of the plane would have the next sync copy the delta straight back, and so would a heal running the other way.

- **In a sync**, after the copy and its proof, before the pass is recorded in sync. Never in a pass that found the state directory rolled back, which keeps everything (ADR-0062), and never in a pass that skips because nothing was published, so a skip still costs only its sequence read.
- **In a direct-ship run**, right after its plane retires anything, at each destination the run wrote. The run shipped its own journal record there, so the checkpoint it just settled at home is settled there too. The run also left each such pair level, so the sync after it skips and would never get to retire anything.

A destination's failure is logged and the pair is still in sync. The checkpoint coverage is shared as a hint, since a checkpoint's key names one immutable object at every replica.

### 5 A copy survives a retirement it races

Under the service's own gate nothing retires under a copy. A copy can still read a store some other installation retires at, as an adoption from a bucket another machine still syncs to does. So:
- **The copier skips a delta found gone** between its listing and its read. The checkpoint phase lists after the delta phase, so the carrier is copied.
- **A copy-back lists once and buckets**, so a delta gone under it makes it list the checkpoints again before anything that follows the index.
- **Any other object gone is still a failure.** A store losing a blob it listed is damage, not retirement.

A copy-back that brings a retired delta back, from a destination that still holds it, is harmless. The plane's next run retires it again.

### 6 A staging archive may go when the metadata store retired what only staging still holds

A migrating direct-ship set keeps its staging archive until retire_staging proves nothing would be lost. A delta the metadata store has retired is still at staging and perhaps at no destination. Counted as unmigrated, it would hold staging for ever. retire_staging therefore asks the metadata store which of those deltas a checkpoint there carries in a copy that opens (`DeltaRetirer.CarriedAsync`). Those are not counted. What staging alone held is in the metadata store's checkpoint, and from there in every destination's.

## Constraint 6, part by part

| Part | How this meets it |
|---|---|
| No identity | No tombstone: a delta goes on the proof of a carrier at the same store (§1), and a reader's own rule (§2) covers the read a tombstone would have |
| The copies bring it back | A destination retires only what the plane no longer lists, so a sync does not copy it back. A heal that does, from a destination that still holds it, is undone by the plane's next run (§4, §5) |
| Readers mid-flight | A loader lists deltas first and looks again for one gone (§2). A copy skips one and carries its checkpoint (§5). A sync, sweep or retention holds the set, and the plane does not retire under it (§3) |
| retire_staging | A delta the metadata store retired is carried there, and is not counted as unmigrated (§6) |
| A delete through the sink | Nothing is deleted through it. Each store retires for itself, so an interruption leaves one store holding a delta it may still let go (§1, §4) |

## Consequences

**Positive**
- **A carried delta is stored once,** in its checkpoint, at staging, at a metadata store, and at every local-path and object-store destination.
- **A load lists what the checkpoints do not carry,** not the whole history.
- **No store's safety depends on another's.** A delete is only ever as wide as one store, and every fact behind it is read there.
- **Concurrent checkpoints need no coordination,** because none is ever deleted.

**Negative**
- **Retirement lags a checkpoint by one publication.** A delta lives twice from its checkpoint to its writer's next backup, and longer if that run finds the set busy.
- **A writer that never publishes again settles nothing.** Its last checkpoint's deltas stay at their keys. They are stored twice, never lost.
- **A destination retires a pass behind the plane,** and one that was away catches up only when a sync reaches it.
- **A reader built before §2 can race a retirement,** if its load spans a whole publication. It would then miss a delta's entries for that load, never for good.

## Alternatives considered

- **Tombstone and grace, as specified.** A tombstone would need a delta identity the format does not have, and a collector pass to act on it. It protects a resolution the index never makes, and does nothing for the reader that did list the delta.
- **Retire everywhere on the plane's decision.** One decision, one set of deletes. It needs every copy to hold what the plane held when it decided, which a destination away for a week does not, and a delete reaching some copies and not others is ADR-0008's fifth part.
- **Wait for the gate.** A run that waited could always retire. It would wait behind syncs of hours, and a settled run is what lets the set's next run begin.
- **Retire checkpoints too, into a newer one.** That would bound the checkpoints, but make which checkpoint carries a delta a moving answer, and bring PT-7 back.
- **Count the grace in time.** A day is a guess about readers. A publication is a fact the store attests.

## What this record does not do

- **Retire at a peer.** A peer keeps every delta. The hub reaches a peer only through its exchange, and deletes there only by a drop instruction signed under the reclaim grant, which only a person's retention apply or deletion carries. A drop also names only keys staging still lists (ADR-0034 §6), and a retired delta is one it no longer does. Owed.
- **Stop listing every delta's key.** A load lists the delta prefix, which now holds only what no checkpoint carries yet.
- **Retire a delta a checkpoint does not carry.** One too large for any checkpoint, or one that did not open when a checkpoint was built, stays at its key.

## Status history

| Date | Status | Note |
|---|---|---|
| 2026-10 | Accepted; built | A store retires the deltas its own settled checkpoints carry, at staging, a metadata store, a local path and an object store, and a reader lists deltas first and looks again for one gone (`Repository.Index/DeltaRetirer`, `Repository.Index/IndexLoader`, `Agent/BackupRunner`, `Agent/FanOut`). Peers keep every delta. Recorded in [ADR-0008 Amendment 3](0008-index-generations-and-checkpoints.md#amendment-3-2026-10--a-store-retires-what-its-own-checkpoints-carry) |

# ADR-0198 — A sync copies only from a source that holds the newest backup whole

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-DEST-014 (amended: catch-up seeds a direct-ship set's destination only from a source that holds the set's newest backup whole, a peer's replica read over the retrieval session where no destination this machine reads outside a run holds it; and a destination none can seed is left behind, naming where the backup is held, with nothing copied that it could not complete)
**Related:** [ADR-0046](0046-direct-to-destination-publication.md) §2 (the union across destinations, and the invariant that made it sufficient), [ADR-0058](0058-peer-write-adapter.md) §5 (outside a run the sink does not read a peer), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the retrieval session), [ADR-0047](0047-backup-pool-and-priorities.md) (the sync ledger), [ADR-0074](0074-background-byte-rate-limits.md) (a destination's transfer limit), [ADR-0196](0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md) decision 2 (beside a peer, a store is filled after the run)

**Built:**
- The source: `Agent/SyncSource` decides, before a sync copies, what it reads: the set's archive where that holds the newest backup whole, the archive with a whole peer's replica behind it for blobs where only a peer does, or nothing, with the reason. `Agent/PeerFilledObjectStore` is the archive with the peer behind it. `Agent/DestinationShipSink` names the destinations a read through it can be answered by now (`ReadableHolders`).
- The copy: `Agent/FanOut` reads that source for the copy to a local path or a store and for the push to a peer, samples and verifies from it, and records a destination it could not seed as behind. A sync that reads from a peer logs event 3702.
- The tests: `Hosts.Tests/SyncSourceTests`:
  - beside a peer, a store is filled with every blob the peer holds, and is in sync;
  - a local path that was away for the first backup is seeded from the peer;
  - a peer that refused the first backup is seeded from the one that took it;
  - with the one whole peer unreachable, the store is left behind naming it, and holds no snapshot;
  - with the one whole peer away, a second peer is left behind naming it, and is pushed no snapshot;
  - with no peer at all, a local path whose sibling holds the only whole copy and is unplugged is left behind naming the sibling, and is handed no snapshot it lacks.

---

## Context

A sync copies blobs before the snapshots that name them (`Replication/StoreToStoreCopier`'s phases). A snapshot it copies is complete at the destination only where its source lists every blob of the snapshot's closure.

A staging set's source is its staging archive, which holds everything. A direct-ship set's source is the ship sink ([ADR-0046](0046-direct-to-destination-publication.md) §2): metadata from the machine's copy, and a blob from whichever destination holds it. ADR-0046 rested this on an invariant: a capture refuses to run with no reachable destination, so every committed snapshot's closure exists at some destination, and a sibling can always seed one that missed a run.

The invariant holds. What it does not give is a source. Outside a run, the sink reads the set's local paths that are plugged in, and its stores where they are all it has. It never reads a peer ([ADR-0058](0058-peer-write-adapter.md) §5). So a closure held whole only at a peer, or only at a local path that is unplugged, is held somewhere the sync cannot read.

The sync then copies what it can read: the snapshots and the index from the machine's copy, and none of the blobs. It records the destination in sync. A restore from it lists the backups and finds their data missing. Three ordinary shapes did this:

- **A store beside a peer.** The run ships to the peer and leaves the store to the sync after it ([ADR-0196](0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md) decision 2). Every sync filled the store with snapshots and no blobs.
- **A local path that missed a backup beside a peer.** The run skips it as behind, and the sync meant to seed it had nothing to seed it from.
- **Two local paths, the whole one unplugged.** The second path took a backup the first missed. When the first came back with the second away, it was handed that backup's snapshot without its blobs.

## Decision

### 1 A sync reads a source that holds the newest backup whole

Before it copies, a sync of a direct-ship set decides what it reads. The evidence is the ledger. A run records each destination it shipped to as holding everything up to the backup it published, so a destination holds the newest backup whole when its row's synced sequence reaches the sequence the sync will claim.

- **The archive serves** when the destination being synced holds the newest backup whole already, or when a destination the sink can read now does.
- **The archive serves** when the ledger names no destination as holding it, as for a set adopted from a store. The union is all there is, and the sync does what it did before.
- **A migrating set's archive serves** while its staging archive remains, which holds the history its runs did not ship (ADR-0046 §3).

### 2 Where only a peer holds it, the sync reads the peer

Where the only whole destinations the ledger names are peers, the sync dials them in priority order over the retrieval session ([ADR-0041](0041-guided-restore-and-peer-retrieval.md)). It reads from the first that answers, behind the archive:

- A key is asked of the archive first. A blob the archive does not hold is read from the peer. Only blobs: a snapshot or an index the peer still holds and the set has let go is not the sync's to carry back.
- A listing is the archive's, with the peer's blobs added. A blob both list is listed once.
- A background sync reads the peer through the peer's own transfer limit ([ADR-0074](0074-background-byte-rate-limits.md)); a person's does not.
- A failure of the peer part-way is said as the peer's, naming it. The destination being synced did nothing wrong, and its row should not say it did.

The peer read is never the destination being synced. So a verification that reads the source to compare against the destination compares two copies, never a replica against itself (ADR-0058 §8's concern).

### 3 Where no whole destination can be read, nothing is copied

The destination is recorded **behind**, with a reason that names each destination holding the newest backup whole and says why it could not be read. Behind, not failed, so the next pass tries again at once. A peer that answers then fills it; a path plugged back in serves through the archive.

Nothing is copied first. A partial copy would carry snapshots whose blobs are missing, which is the false claim this record removes.

## Consequences

- **A store beside a peer holds whole backups.** The sync after each run reads the run's blobs back from the peer and sends them on. Each blob crosses the peer's link twice, once to the peer in the run and once back to this machine in the sync.
- **A peer offline leaves the store behind until it answers.** The store's row names the peer. The run is not held, and the peer itself is still written as the run goes.
- **Stores already filled without blobs heal.** The next backup makes the store incomplete by the ledger, and the sync then reads every blob it lacks from the peer, including those of earlier backups.
- **A sync dials a peer only when nothing nearer holds the backup whole.** A set with a whole local path plugged in reads from it, as before.
- **Two peers seed each other.** A peer that missed a run is pushed the blobs read from one that did not.

## What this does not do

- **It does not read a peer inside a run.** The write-behind of [ADR-0196 Amendment 6](0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md#amendment-6-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-local-path) sends blobs on from a local path only. A store beside a peer alone is filled by the sync after each run, now from the peer.
- **It does not check the closure itself.** The ledger's word that a destination holds the newest backup whole is what the replication gate already rests on (FR-GC-009). Walking every snapshot's closure before each copy would cost a full read of the metadata on every pass.
- **It does not fill a destination from a peer for a staging set.** A staging set's archive holds everything, so it never needs to.

## Alternatives considered

**Let the sink read peers outside a run.** ADR-0058 rejected this: a presence probe would cost a TLS handshake, and a peer's challenge would read the peer's replica as its own witness. Here the peer is dialled once per sync, only when nothing nearer serves, and never for the destination it seeds.

**Copy what can be copied and record the destination behind.** The destination would hold snapshots whose blobs are missing, and a restore from it would list backups it cannot restore. Behind with nothing copied keeps every destination's snapshots restorable.

**Stage a direct-ship set beside a peer.** That is the copy of the backup on the machine that direct-ship exists to avoid ([ADR-0046](0046-direct-to-destination-publication.md)).

## Status history

| Date | Status | Note |
|---|---|---|
| 2026-11 | Accepted | Built in one slice, tests first. Before it, every sync of a store beside a peer recorded the store in sync holding snapshots and no blobs, and a local path that missed a backup beside a peer was seeded the same way. `Agent/SyncSource`; `Agent/PeerFilledObjectStore`; `Agent/DestinationShipSink`; `Agent/FanOut`; `Hosts.Tests/SyncSourceTests` |

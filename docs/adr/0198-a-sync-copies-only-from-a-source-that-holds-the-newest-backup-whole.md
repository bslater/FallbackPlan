# ADR-0198 — A sync copies only from a source that holds the newest backup whole

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-DEST-014 (amended: catch-up seeds a direct-ship set's destination only from a source that holds the set's newest backup whole, a peer's replica read over the retrieval session where no destination this machine reads outside a run holds it; and a destination none can seed is left behind, naming where the backup is held, with nothing copied that it could not complete); FR-DEST-014 (amended by [Amendment 1](#amendment-1-2026-11--a-destination-that-holds-every-blob-is-brought-current-without-the-peer): a destination whose own listing shows every blob the set's index locates is brought current from this machine's metadata when no whole destination can be read, and no verification is claimed for that pass); FR-DEST-004 (amended by [Amendment 2](#amendment-2-2026-11--a-destination-is-in-sync-only-once-it-holds-the-newest-backup-whole): a pass records a destination in sync only once it holds every blob a restore of the newest backup the pass carried reads; one the pass leaves short is failed, naming the first blob it lacks, and keeps its watermark); FR-DEST-004 (amended by [Amendment 3](#amendment-3-2026-11--a-copy-that-leaves-a-destination-short-sends-it-no-record-of-that-backup): a pass that leaves a destination short of the newest backup does not send it that backup's record)
**Related:** [ADR-0046](0046-direct-to-destination-publication.md) §2 (the union across destinations, and the invariant that made it sufficient), [ADR-0058](0058-peer-write-adapter.md) §5 (outside a run the sink does not read a peer), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the retrieval session), [ADR-0047](0047-backup-pool-and-priorities.md) (the sync ledger), [ADR-0074](0074-background-byte-rate-limits.md) (a destination's transfer limit), [ADR-0196](0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md) decision 2 (beside a peer, a store is filled after the run)

**Built:**
- The source: `Agent/SyncSource` decides, before a sync copies, what it reads: the set's archive where that holds the newest backup whole, the archive with a whole peer's replica behind it for blobs where only a peer does, or nothing, with the reason. `Agent/PeerFilledObjectStore` is the archive with the peer behind it. `Agent/DestinationShipSink` names the destinations a read through it can be answered by now (`ReadableHolders`).
- The copy: `Agent/FanOut` reads that source for the copy to a local path or a store and for the push to a peer, samples and verifies from it, and records a destination it could not seed as behind. A sync that reads from a peer logs event 3702.
- The destination's own blobs: where no whole destination can be read, `Agent/SyncSource` lists the destination's blobs and, if it holds every blob the set's index locates, serves the archive for the metadata it lacks, and `Agent/FanOut` samples nothing for that pass; the sync logs event 3703 (Amendment 1).
- The tests: `Hosts.Tests/SyncSourceTests`:
  - beside a peer, a store is filled with every blob the peer holds, and is in sync;
  - a local path that was away for the first backup is seeded from the peer;
  - a peer that refused the first backup is seeded from the one that took it;
  - with the one whole peer unreachable, the store is left behind naming it, and holds no snapshot;
  - with the one whole peer away, a second peer is left behind naming it, and is pushed no snapshot;
  - with no peer at all, a local path whose sibling holds the only whole copy and is unplugged is left behind naming the sibling, and is handed no snapshot it lacks.
  - `Hosts.Tests/StoreBesideAPeerTests` (Amendment 1): with the peer away, a store the run sent every blob is brought current, after the first backup and after a second, and claims no verification; a store missing a blob, or one that took nothing from the run before, is left behind naming the peer and holds no snapshot.
- The closure before in sync (Amendment 2): `Repository.Catalogue/Catalogue` names every blob a restore of a snapshot reads (`ClosureOf`). `Agent/SnapshotClosure` crosses them off from the keys the sync's file count already sees, and `Agent/FanOut` records a destination still lacking any as failed, logging event 3704.
  - `Hosts.Tests/InSyncClosureTests`: a staging set's local path, short of a data blob and of a metadata blob, is failed naming it, its watermark and last success unmoved; it is in sync once the blob is back where the sync reads it; a direct-ship set's path copied from a whole copy short of a blob is failed naming it; a staging set's peer pushed from an archive short of a blob is failed naming it, its watermark unmoved; a path that missed the newest backup, which a person then deleted, is in sync holding none of it.
- A short copy sends no record (Amendment 3): `Replication/StoreToStoreCopier` asks, before each snapshot record the destination lacks, whether to hold it back, as does the push in `Agent/ReplicationInitiator`, which now sends records after everything else; `Agent/FileHoldingCounter` holds back the carried snapshot's record while its closure is lacking, and the copier logs event 3009.
  - `Replication.Tests/CopierWithheldRecordTests`: a copy and a converge send everything but the record they are told to hold back, never hold back anything but a record, and leave a record the destination holds where it is.
  - `Hosts.Tests/InSyncClosureTests`: a staging set's path copied to, the same path under a retention policy, and a staging set's peer pushed to, each left short, hold no record of that backup and the ledger says so; the record follows once the blob is back.
  - `Repository.Tests/CatalogueTests`: the closure names the blob of a snapshot's record, its structure, its folders, a file's version and its content, each filed apart; and takes an object from its live winning location only, not one a later generation superseded nor one in a deleted blob.

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

> **Amended 2026-11 ([Amendment 1](#amendment-1-2026-11--a-destination-that-holds-every-blob-is-brought-current-without-the-peer)).** Unless the destination holds every blob already: its own listing is asked, and one that holds every blob the set's index locates is brought current from the metadata here.

Nothing is copied first. A partial copy would carry snapshots whose blobs are missing, which is the false claim this record removes.

## Consequences

- **A store beside a peer holds whole backups.** The sync after each run reads the run's blobs back from the peer and sends them on. Each blob crosses the peer's link twice, once to the peer in the run and once back to this machine in the sync.

  > **2026-11 ([ADR-0196 Amendment 8](0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md#amendment-8-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-peer-from-a-copy-kept-here)).** The run now sends each blob on to the store as it goes, from a copy kept in a bounded room on this machine. The sync reads back from the peer only what found the room full, or went unsent when the run ended.
- **A peer offline leaves the store behind until it answers.** The store's row names the peer. The run is not held, and the peer itself is still written as the run goes.
- **Stores already filled without blobs heal.** The next backup makes the store incomplete by the ledger, and the sync then reads every blob it lacks from the peer, including those of earlier backups.
- **A sync dials a peer only when nothing nearer holds the backup whole.** A set with a whole local path plugged in reads from it, as before.
- **Two peers seed each other.** A peer that missed a run is pushed the blobs read from one that did not.

## What this does not do

- **It does not read a peer inside a run.** The write-behind of [ADR-0196 Amendment 6](0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md#amendment-6-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-local-path) sends blobs on from a local path only. A store beside a peer alone is filled by the sync after each run, now from the peer.

  > **2026-11 ([ADR-0196 Amendment 8](0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md#amendment-8-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-peer-from-a-copy-kept-here)).** It still does not read the peer. Beside a peer alone the run keeps its own copy of each blob and sends the store that.
- **It does not check the closure itself.** The ledger's word that a destination holds the newest backup whole is what the replication gate already rests on (FR-GC-009). Walking every snapshot's closure before each copy would cost a full read of the metadata on every pass.

  > **Amended 2026-11 ([Amendment 2](#amendment-2-2026-11--a-destination-is-in-sync-only-once-it-holds-the-newest-backup-whole)).** The newest backup's closure is now checked after the copy, from the keys the pass already sees, before the destination is recorded in sync. The source is still chosen on the ledger's word.
- **It does not fill a destination from a peer for a staging set.** A staging set's archive holds everything, so it never needs to.

## Alternatives considered

**Let the sink read peers outside a run.** ADR-0058 rejected this: a presence probe would cost a TLS handshake, and a peer's challenge would read the peer's replica as its own witness. Here the peer is dialled once per sync, only when nothing nearer serves, and never for the destination it seeds.

**Copy what can be copied and record the destination behind.** The destination would hold snapshots whose blobs are missing, and a restore from it would list backups it cannot restore. Behind with nothing copied keeps every destination's snapshots restorable.

**Stage a direct-ship set beside a peer.** That is the copy of the backup on the machine that direct-ship exists to avoid ([ADR-0046](0046-direct-to-destination-publication.md)).

## Amendment 1 (2026-11) — a destination that holds every blob is brought current without the peer

**Requirements:** FR-DEST-014 (amended: where no whole destination can be read, a destination whose own listing shows every blob the set's index locates is brought current from this machine's metadata, and that pass claims no verification)

### Context

Beside a peer alone, a run now sends each blob it writes on to the set's store as it goes ([ADR-0196 Amendment 8](0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md#amendment-8-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-peer-from-a-copy-kept-here)). A store that took every blob lacks only the snapshot and the index, and both are on this machine. Decision 3 still left it behind while the peer did not answer, because the ledger named the peer as the only whole destination.

The owner asked that a store the run sent every blob be brought current without the peer.

### Decision

1. **The destination's own listing is the evidence.** Where decision 3 would refuse, the sync lists the destination's blobs and asks whether it holds every blob the set's index locates. The catalogue names each blob some object is located in, and the repository's keys give the store key it is filed under.
2. **If it holds them all, the archive serves.** The copy carries the snapshots and the index from the metadata here. Every blob they name is at the destination already, so nothing it copies lacks its closure.
3. **That pass proves nothing.** Nothing the archive reads is a copy of the destination's blobs. So the pass draws no verification samples and records the destination in sync with no proof, rather than a proof drawn from the metadata plane (ADR-0058 §8's concern). The next pass that reads a whole peer samples again.
4. **Only where nothing whole answers.** A readable whole destination, or a whole peer that answers, is still the source, as decisions 1 and 2 say. The listing is paid only when the sync would otherwise refuse.
5. **A local path or a store only.** A peer's replica is listed over the session the push holds, which this does not reach.

### Consequences

- **A store beside a peer becomes current with the peer away,** once the run has sent it every blob. A peer that is often away no longer leaves the off-site copy behind between its visits.
- **A store that missed a blob stays behind,** whatever the run sent it: the check reads what the store holds, not what was sent.
- **The fallback costs a listing of the destination's blobs,** one request per page at a store.

### What this does not do

- **Count the store whole on the run's word.** That was the option first asked for: record in the ledger that the run sent the store every blob it wrote, when the store held the backup before whole. It is not enough. A run does not write a blob the index already locates, nor one a backup resumed after a crash shipped before the crash, and the store need not have been sent either. The listing asks the store what it holds, which covers both.
- **Verify the destination's blobs.** They are proved by the next pass that reads a whole copy, or by the replica sweep.

## Amendment 2 (2026-11) — a destination is in sync only once it holds the newest backup whole

**Requirements:** FR-DEST-004 (amended: a pass records a destination in sync only once it holds every blob a restore of the newest backup the pass carried reads; one the pass leaves short is failed, naming the first blob it lacks, and keeps its watermark)

### Context

Decisions 1 to 3 choose a source that holds the newest backup whole by the ledger, and the copy carries what that source lists. Nothing then checked what the copy left behind.

A source can be short where the ledger says it is whole. A staging archive can lose a blob. A destination the ledger names whole can lose one after the run that filled it. In each case the copy carried everything else and the pass recorded the destination in sync. Its watermark moved past a backup it cannot restore. For a staging set, that watermark is what the replication gate lets staging drop the backup on (FR-GC-009).

The owner asked that a sync check the destination holds the closure before it records the destination in sync.

### Decision

1. **The closure of the backup the pass carries.** The snapshot checked is the one the pass's publication survey names newest, read before the copy as the watermark is. Its closure is every blob a restore of it reads: those its record, its structure, its folders and its files' versions are located in, and those its files' content is. Each object is taken at its winning location (07 §3). The catalogue answers (`Catalogue.ClosureOf`).
2. **What the destination holds comes from what the pass already reads.** Before the copy, the sync lists a store's or a local path's blobs to count the newest backup's files ([ADR-0088](0088-a-backups-percentage-is-what-it-has-backed-up.md) Amendment 1). A peer declares its inventory at the start of a push. Each key the copy then finds held, or delivers, is reported to that count. The closure is crossed off from the same keys, so no second listing is made.
3. **A blob is matched by its store name.** That is the last segment of its key, and the class segment before it is ignored. A catalogue rebuilt from the index, after a heal or an adoption, does not always know a blob's class, and the name is the blob's own either way.
4. **A pass that leaves the destination short is failed.** It records the destination failed, saying how many blobs are lacking and naming the first, and logs event 3704. The watermark and last success stay where the last whole pass left them. It is failed rather than behind because the copy carried everything its source lists, so the next pass from the same source cannot finish it. What was lost has to come back where the sync reads it, or the backup has to be written again.
5. **Where nothing can be said, nothing is judged.**
   - A pass the reconciliation gate skips reads nothing and checks nothing; the pass before it checked.
   - A catalogue that does not hold the carried snapshot yet, or cannot answer, checks nothing, as the file count goes without. The catalogue is never authoritative.
   - A keep filter that drops the newest backup, as a person's deletion of it does, leaves the destination nothing of it to hold, so nothing is checked.

### Consequences

- **The watermark means what the gate reads it as.** A destination short of the newest backup's closure no longer counts toward letting staging release that backup.
- **The status names the blob.** A destination left short reads failed with the first blob it lacks, not in sync.
- **The cost is one catalogue query per pass and destination,** about the size of the one the file count already makes.

### What this does not do

- **Hold the copy back.** The destination still holds what the source does, the snapshot record included, which the copy sends last. A restore from it lists the backup and cannot read the blobs it lacks. Holding the record back would need the copier to check between its phases.

  > **Amended 2026-11 ([Amendment 3](#amendment-3-2026-11--a-copy-that-leaves-a-destination-short-sends-it-no-record-of-that-backup)).** The copier and the push now ask before each record, and a destination left short is sent no record of that backup.
- **Check older backups.** Each was checked by the pass that carried it as the newest. A pass that carries several new backups at once checks the newest of them only.
- **Read the destination again after the copy.** A put the destination acknowledged is taken as held, as the file count takes it. Whether the bytes are still there is verification's question.

## Amendment 3 (2026-11) — a copy that leaves a destination short sends it no record of that backup

**Requirements:** FR-DEST-004 (amended: a pass that leaves a destination short of the newest backup does not send it that backup's record)

### Context

Amendment 2 stopped a short destination being recorded in sync, but left it holding the backup's snapshot record, which the copy sends last. A record's presence is what says a backup is at a destination. A restore from a destination left short listed a backup it could not read. The copier's own promise, that a record is there only once everything it references is ([ADR-0011](0011-commit-versus-replication-semantics.md)'s per-replica commit rule, kept by copy order), held only while the source was whole.

The owner asked that the record be held back.

### Decision

1. **The copier asks before each record.** `StoreToStoreCopier.CopyAsync` and `ConvergeAsync` take a question to ask of each snapshot record the destination lacks, once every other phase is done. A record it names is not sent, and is logged (event 3009). Nothing but a record is ever held back: the blobs and the index are what a later pass completes the destination from. A record the destination already holds is left where it is.
2. **The push asks the same, and sends records last.** The push to a peer listed its source in one pass and sent each key as it came. Its sources include unions of planes, the ship sink and the archive with a peer's blobs behind it, and those list a record before blobs they add from elsewhere. So the push could send a record before the blobs it names, and an interrupted push could leave a peer holding a record without its blobs. The push now holds every record until everything else has been offered, then asks of each the same question.
3. **The sync holds back the carried record while its closure is lacking.** The question is answered by the sync's count (Amendment 2): the record of the snapshot the pass carries is held back while any blob it needs is lacking at the destination. The ledger's words for the failed pass then say the record was not sent.

### Consequences

- **A destination left short lists no backup it cannot restore** from this pass. Its earlier backups' records stay.
- **The record follows its blobs.** The next pass that completes the destination sends it.
- **A peer is never pushed a record ahead of the blobs it names,** whatever its source lists first.

### What this does not do

- **Hold back an older backup's record.** Only the newest's closure is known to the sync; a pass carrying several new backups holds back only the newest's record.
- **Put the union listings in key order.** The ship sink and the peer-filled archive still list one plane after another, not in the ordinal order the store contract states. The copier walks one phase at a time and the push now sends records last, so neither depends on it here; a reader that does, such as a resumable listing, is not yet looked at.

## Status history

| Date | Status | Note |
|---|---|---|
| 2026-11 | Accepted | Built in one slice, tests first. Before it, every sync of a store beside a peer recorded the store in sync holding snapshots and no blobs, and a local path that missed a backup beside a peer was seeded the same way. `Agent/SyncSource`; `Agent/PeerFilledObjectStore`; `Agent/DestinationShipSink`; `Agent/FanOut`; `Hosts.Tests/SyncSourceTests` |
| 2026-11 | Amended (related) | [ADR-0196 Amendment 8](0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md#amendment-8-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-peer-from-a-copy-kept-here): the double crossing this record left is mostly gone. Beside a peer alone, the run sends each blob on to the store as it goes, from a copy kept in a bounded room on this machine; the sync reads from the peer only what found no room or went unsent. |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--a-destination-that-holds-every-blob-is-brought-current-without-the-peer): where no whole destination can be read, a destination whose own listing shows every blob the set's index locates is brought current from the metadata here, with no verification claimed for that pass; decision 3 noted. A ledger claim on the run's word was considered and set aside, since a run does not write a blob it reuses. `Agent/SyncSource`; `Agent/FanOut`; `Hosts.Tests/StoreBesideAPeerTests`; `Hosts.Tests/SyncSourceTests` |
| 2026-11 | Amended | [Amendment 2](#amendment-2-2026-11--a-destination-is-in-sync-only-once-it-holds-the-newest-backup-whole): a pass records a destination in sync only once it holds every blob a restore of the newest backup the pass carried reads, matched by store name; one left short is failed, naming the first blob, its watermark unmoved; "what this does not do" noted. `Repository.Catalogue/Catalogue`; `Agent/SnapshotClosure`; `Agent/FileHoldingCounter`; `Agent/FanOut`; `Hosts.Tests/InSyncClosureTests` |
| 2026-11 | Amended | [Amendment 3](#amendment-3-2026-11--a-copy-that-leaves-a-destination-short-sends-it-no-record-of-that-backup): a pass that leaves a destination short sends it no record of the newest backup: the copier and the push ask before each record, and the push sends records after everything else, since a union source may list a record before the blobs it names. `Replication/StoreToStoreCopier`; `Agent/ReplicationInitiator`; `Agent/FileHoldingCounter`; `Agent/FanOut`; `Replication.Tests/CopierWithheldRecordTests`; `Hosts.Tests/InSyncClosureTests` |

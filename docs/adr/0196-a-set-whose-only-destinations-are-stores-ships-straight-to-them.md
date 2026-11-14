# ADR-0196 — A set whose only destinations are stores ships straight to them

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-DEST-021 (new: a direct-ship set whose destinations include no local path and no peer writes its backup through its object stores as the run goes, keeps no data on the machine, restores from them, and a run that cannot reach them fails naming them); FR-DEST-015 (amended: the refusal of a capture with nowhere to write names each destination it tried and why); FR-DEST-021 (amended by [Amendment 1](#amendment-1-2026-11--a-new-sets-shape-follows-its-destinations): a new set that names no shape is born direct-ship where every destination it names is a store, and the console's new set follows its destinations as the service does); FR-DR-009 (amended by [Amendment 2](#amendment-2-2026-11--a-set-adopted-from-a-store-ships-straight-to-it): a set adopted from a store is adopted direct-ship, with only the archive's metadata copied to the machine); FR-DEST-021 (amended by [Amendment 3](#amendment-3-2026-11--a-store-only-set-keeps-its-folder-trees-on-the-machine): a set whose stores are all it has keeps a copy of each metadata blob it ships on the machine, so a backup reads no folder tree from the store); FR-DEST-021 (amended by [Amendment 4](#amendment-4-2026-11--a-staging-set-of-stores-alone-is-told-it-could-ship-straight): a staging set whose destinations are all stores is named in a notice saying it could ship straight to them, and once it does it keeps the staging archive's folder trees); FR-DEST-021 (amended by [Amendment 5](#amendment-5-2026-11--a-tree-read-from-the-store-is-kept-once-it-verifies): a tree such a set has no copy of is kept the first time it is read from a store, once it verifies); FR-DEST-021 (amended by [Amendment 6](#amendment-6-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-local-path): beside a local path, a direct-ship run sends each blob it lands there on to the set's stores as it goes, never waiting on them, and leaves the snapshot and the index to the sync after the run)
**Related:** [ADR-0046](0046-direct-to-destination-publication.md) (direct-to-destination publication: the ship sink, the metadata store, and the capture with nowhere to ship), [ADR-0058](0058-peer-write-adapter.md) §8 (why a peer-only set still stages by default), [ADR-0091](0091-an-s3-compatible-destination.md) decision 5 and [ADR-0093](0093-an-azure-blob-destination.md) (a run never writes through a store), [ADR-0012](0012-storage-provider-contract.md) Amendment 5 (a put lands whole or not at all), [ADR-0061](0061-adopt-a-destinations-archives.md) §3 (adoption, which makes the set direct-ship)

**Built:**
- The sink: `Agent/DestinationShipSink` writes through a set's object stores when nothing the set names is a local path or a peer (`WritesThroughStores`), opening each through `Agent/StoreComposition`, and reads from them outside a run. A run with nowhere to write names each destination it tried.
- The kept trees: `Agent/DestinationShipSink` keeps a copy of each metadata blob a set that writes through its stores ships, answers a read of one from that copy first, deletes the copy before the stores' object, and never lists it (Amendment 3).
- The verified copy: `Agent/DestinationShipSink` keeps a tree it has no copy of the first time it reads it from a store, once the sweep's own check in `Repository/VerifyEngine`, which `Agent/ServiceRuntime` hands it, finds it sound (Amendment 5).
- The notice: `Agent/StagingCouldShipWatch`, observed on every pass by `Agent/Scheduler`, names each staging set whose destinations are all stores; the first open after such a set is made direct-ship copies its staging archive's folder trees with its metadata, in `Agent/ServiceRuntime` (Amendment 4).
- The write-behind: for each store beside a local path, `Agent/DestinationShipSink` starts an `Agent/StoreWriteBehind` that sends each blob the run lands on a local path on to the store, read back from there, and ends it with the run (Amendment 6).
- The set check: `Agent/ServiceCommandHandler` accepts a direct-ship set that names only stores, and gives a new set that names no shape the one its destinations call for (Amendment 1).
- Adoption: `Agent/ServiceCommandHandler.Adoption.cs` adopts a set from a store as direct-ship, copying only the archive's metadata to the machine (Amendment 2).
- The console: `Web/wwwroot/app.js` gives a new set the shape its destinations call for until the person sets it, and its note says a set of stores alone is written through as it runs (Amendment 1).
- The tests:
  - `Hosts.Tests/ObjectStoreDestinationTests`, run for S3 and for Azure alike, with the store the set's only destination:
    - a backup ships the data to the store and keeps no data blob on the machine;
    - a backup after another puts the new blob and nothing the store held;
    - the set's own archive restores from the store, every record from its own archive;
    - a retention report walks whole, reading the folder trees from the store, and since Amendment 3 from the trees kept here;
    - a backup after another reads no folder tree from the store, and the metadata blobs here are those at the store (Amendment 3);
    - a retention trim deletes the trees it drops here as at the store (Amendment 3);
    - a metadata blob put through the set's archive outside a run, as compaction puts what it packs, is kept here too (Amendment 3);
    - a store that does not answer fails the run, naming it.
    - A set with a local path beside the store: the run sends its blobs on to the store as it goes, and nothing else, and leaves it behind for the sync that follows; a store that holds every upload never holds the run; a store that refuses an upload is sent nothing more, and the run goes on; a store whose stored credential no longer reads is sent nothing and does not fail the run (Amendment 6).
    - A tree with no copy here is read from the store once and kept, and the backup after reads none; a damaged tree read from the store is not kept, nor a sound tree at the store under another tree's key (Amendment 5).
    - A staging set with the store its only destination is named in a notice, and one beside a local path is not; once such a set is made direct-ship, the backup after reads no tree from the store, and the notice goes (Amendment 4).
  - `Hosts.Tests/DirectShipTests` — a direct-ship set naming only a bucket, or only a container, is accepted; one naming only a reserved kind is still refused. A new set naming only stores is born direct-ship, and one naming a store and a peer is still born staging (Amendment 1).
  - `Hosts.Tests/ObjectStoreAdoptionTests`, run for S3 and for Azure alike — a set adopted from a store is direct-ship, and the preview and the answer say it ships straight to the store; only the archive's metadata is copied here, with no staging archive and no data blob; the next backup writes through the store and keeps no data here; an older snapshot restores from the store (Amendment 2). The archive's folder trees are copied here too, and a backup of the adopted set reads none from the store (Amendment 3).
  - `Web.DomTests/NewSetWizardDomTests` — a new set naming only a local path or only a store is sent direct-ship, and one naming only a peer staging; the Other settings step shows the shape the destinations give and says a set of stores alone is written through (Amendment 1).

---

## Context

The owner asked for the local and remote split another well-known backup tool uses. That tool sends a backup's data straight to the remote store, packed into volumes, and keeps on the machine only a database that indexes it, which can be rebuilt from the remote.

This product already has that split, under other names. Data is packed into blobs. The index is deltas and checkpoints, and the snapshots list the files. The catalogue is a local database, disposable and rebuilt from the repository. A direct-ship set (ADR-0046) writes its blobs straight to its destinations through the ship sink and keeps only metadata on the machine. That is the default for a set with a local-path destination, and a choice for a set whose only destinations are peers.

One case differed: a set whose only destinations are S3 or Azure stores. A run never wrote through a store (ADR-0091 decision 5, ADR-0093). It left the store behind for the sync after the run to fill. So a store-only set had to stage, keeping a local archive. Once trimmed that archive still holds the metadata and the newest backup's data, which on a large set is most of a copy. The set check refused direct-ship for such a set, as having nowhere to ship.

ADR-0058 §8 gave three reasons a remote-only set keeps a local copy:

- a capture that does not wait on the link;
- a transfer that resumes after the link dies inside an object;
- an independent copy to check the replica against.

For a store, the second does not arise: a put lands whole or not at all (ADR-0012 Amendment 5), and an object cut off is put again. The third is answered for every direct-ship set: a sampled blob is proved by opening it at the replica, which needs a key the store never held (ADR-0046's verification). The first is the trade itself, and the owner chose it.

## Decision

1. **A set's stores are written through when they are all it has.** Where nothing a set names is a local path or a peer, a run writes its blobs through its object stores as it goes, as it does through a local path. The snapshot and the index go to the metadata store on the machine and to the stores, as for any direct-ship set.
2. **Beside a local path or a peer, a store is filled after the run, as before.** Those keep up with a capture as it goes, so a run that can land on one never waits on a network store. The store stays behind for the sync after the run.

   > **Amended 2026-11 ([Amendment 6](#amendment-6-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-local-path)).** Beside a local path the run also sends each blob it lands there on to the store as it goes, still never waiting on it. The store stays behind until the sync after the run, which sends the snapshot.
3. **A store is held to every check the other destinations meet.** A store with a defective address, one with no full backup yet, and one that missed a run are left out of the run as a local path would be. A store that cannot be opened before any request, with no credential stored or a lapsed signature, is dropped and named.
4. **A read outside a run asks the stores** of a set that writes through them, since they hold its data. Each read is a request the provider may charge for.
5. **A run with nowhere to write names where it tried.** The refusal ADR-0046 §4 accepted now names each destination and why it could not be written, so it says which to reconnect.
6. **The set check accepts a direct-ship set naming only stores.** A set naming only a reserved kind, which nothing serves, is still refused.

## Consequences

- **A store-only set can keep no data on the machine.** Its metadata store holds the snapshots and the index. Every blob is at the store.
- **A capture waits on the network.** A store that does not answer fails the run, as ADR-0046 §4 accepted for every direct-ship set, and the next pass tries again.
- **Each backup reads the folder trees it builds on from the store.** The metadata store keeps snapshots and the index, not the metadata blobs, so a run reads the trees of the snapshot before it back from the store. At the test store a second backup made sixteen requests: one data put, one metadata put, the snapshot and index objects, and five reads of trees.

  > **2026-11: no longer ([Amendment 3](#amendment-3-2026-11--a-store-only-set-keeps-its-folder-trees-on-the-machine)).** The set keeps a copy of each tree it ships, and a backup reads its trees there.
- **A restore, a retention pass, a preview and a catch-up copy read from the store,** each read a request.

## What this does not do

- **Make a store-only set direct-ship by default.** A new set naming only stores still stages unless it is made direct-ship. That default, and saying it in the console, is the next step.

  > **2026-11: done ([Amendment 1](#amendment-1-2026-11--a-new-sets-shape-follows-its-destinations)).** A new set that names no shape and names only stores is born direct-ship, and the console's new set follows its destinations as the service does.
- **Adopt a set from a store as direct-ship.** A set adopted from a store is still staged (ADR-0091 Amendment 1).

  > **2026-11: done ([Amendment 2](#amendment-2-2026-11--a-set-adopted-from-a-store-ships-straight-to-it)).** A set adopted from a store is adopted direct-ship, and only the archive's metadata is copied to the machine.
- **Move existing store-only sets.** A set that stages can be made direct-ship by changing its shape, and its staging archive retired, through the machinery ADR-0046 built; nothing does it for a person.

  > **2026-11: said, not done ([Amendment 4](#amendment-4-2026-11--a-staging-set-of-stores-alone-is-told-it-could-ship-straight)).** A notice names each such set and says where to change it. Changing it is still a person's.
- **Keep the metadata blobs on the machine.** Doing so would spare each backup its reads of the trees, at the cost of the metadata's size on disk.

  > **2026-11: done ([Amendment 3](#amendment-3-2026-11--a-store-only-set-keeps-its-folder-trees-on-the-machine)).** A set whose stores are all it has keeps a copy of each metadata blob it ships.
- **Write through a store beside a local path or a peer.** The store is filled after the run, as before.

  > **2026-11: in part ([Amendment 6](#amendment-6-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-local-path)).** Beside a local path the run sends its blobs on to the store as it goes, without waiting on it; the snapshot is still the sync's. Beside a peer alone, nothing is sent during the run.

## Alternatives considered

- **Adopt the other tool's on-disk format.** Rejected. Its volumes are another repository format. Adopting it would replace formats 2 to 6 and restart the freeze gate (roadmap), for a split this product already has.
- **Write through every store in every direct-ship set.** Rejected. A set with a local path would wait on the network for nothing: the store is filled from what the run shipped, a moment after it.

  > **2026-11: still rejected ([Amendment 6](#amendment-6-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-local-path)).** The run now sends its blobs on to the store behind the local path instead, which spares the sync most of its upload without the run ever waiting on the store.
- **Keep staging, and only trim harder.** Rejected. A trimmed staging archive still holds the newest backup's data, which is the copy the owner asked to be rid of.

## Amendment 1 (2026-11) — a new set's shape follows its destinations

**Requirements:** FR-DEST-021 (amended: a new set that names no shape is born direct-ship where every destination it names is a store, and the console's new set follows its destinations as the service does)

### Context

A new set that names no shape is born direct-ship where it names a local path (ADR-0046), and staging otherwise, so a new set naming only stores still staged. ADR-0058 §8 gives three reasons a remote-only set keeps a local copy, and Context above answers each for a set of stores alone. That default was left as the next step.

The console did not follow the service's default at all. Its new-set wizard sent direct-ship for every new set, so a set naming only a peer was made direct-ship on the person's behalf, which ADR-0058 §8 declines to do. And its Storage shape note still said a store is filled by the sync after each run, which since this record holds only beside a local path or a peer.

### Decision

**A new set's shape follows its destinations.** Where a set names no shape, it is born direct-ship where one of its destinations is a local path, or where every one is a store. It is born staging where a peer is all a run could ship to. Beside a peer, a store is filled after the run and the run ships to the peer alone, so the peer's reasons hold.

**The console gives a new set the same shape** until the person sets it in the set's Other settings. The summary, the wizard's account of what Create leaves, the checkbox and the upsert all read that one shape.

**The note says what the shapes do:** a local path or a peer is written as the backup runs; a store beside one is filled by the sync after each run; a set whose only destinations are stores writes through them as each backup runs, each write a request.

### Consequences

- A new set naming only stores keeps no copy of its data on the machine unless the person asks for one.
- A new set naming only a peer, made in the console, now stages as the service's default does. Made before, it stays as it was made.

### What this does not do

- **Change an existing set's shape.** An edit that names no shape keeps the set's own, as before.
- **Move existing store-only sets.** A person changes the shape and retires the staging archive, as before.

## Amendment 2 (2026-11) — a set adopted from a store ships straight to it

**Requirements:** FR-DR-009 (amended: a set adopted from a store is adopted direct-ship, with only the archive's metadata copied to the machine, and the preview and the answer say it ships straight to the store)

### Context

ADR-0061 adopts every set as direct-ship, since the destination holds the whole repository. ADR-0091 Amendment 1 made a set adopted from a store the exception, because a run never wrote through a store, and ADR-0093 followed it for a container. Such a set was staged: its staging archive was seeded with the archive's metadata and the newest backup's data, and each backup was synced back to the store after it ran.

The reason no longer holds. A set adopted from a store names that store alone, and this record has a run write through the stores of a set that has nothing else. A staged adoption also kept on the machine the copy of the data the owner asked to be rid of.

### Decision

**A set adopted from a store is adopted direct-ship,** as from a local path or a peer. Only the archive's metadata is copied, into the set's metadata store. No staging archive is made, and no data is copied to the machine.

> **Amended 2026-11 ([Amendment 3](#amendment-3-2026-11--a-store-only-set-keeps-its-folder-trees-on-the-machine)).** The archive's metadata blobs, its folder trees, are copied too, since the set keeps the trees it ships.

**The preview and the answer say so.** Each says the store is all the adopted set has, that the set ships straight to it, that only the archive's metadata is copied to the machine, and that each backup writes through the store as it runs.

### Consequences

- **A rebuilt machine keeps no data for a set adopted from a store.** The next backup is incremental against the store and writes through it, and every snapshot, the newest included, restores from the store.
- **Adoption reads less from the store:** the metadata, not the newest backup's data too.
- **The next backup reads the folder trees it builds on from the store,** as any backup of a store-only set does (Consequences above).

  > **2026-11: no longer ([Amendment 3](#amendment-3-2026-11--a-store-only-set-keeps-its-folder-trees-on-the-machine)).** The trees are copied at adoption and read here.

### What this does not do

- **Move a set adopted from a store before this.** It stays a staging set, and a person changes its shape and retires its staging archive, as for any existing store-only set.
- **Let adoption choose the shape.** Adoption names no shape, and every set it adopts is direct-ship.

## Amendment 3 (2026-11) — a store-only set keeps its folder trees on the machine

**Requirements:** FR-DEST-021 (amended: a set whose stores are all it has keeps a copy of each metadata blob it ships on the machine, reads its folder trees there, and trims the copies with the store)

### Context

A set whose stores are all it has kept only its snapshots and its index on the machine. Its folder trees are metadata blobs, and every blob was at the store alone. So each backup read the trees of the snapshot before it back from the store, and so did a retention walk outside a run. At the test store a second backup read its root tree five times, each read a request the provider may charge for.

The owner asked for the split another backup tool uses: the data at the remote store, and on the machine the database that indexes it. The folder trees are part of that index. The record left keeping them as not done, at the cost of their size on disk.

### Decision

**A set that writes through its stores keeps a copy of each metadata blob it ships,** in its metadata store, once the blob has landed at a store. A data blob is never kept.

**A read of a metadata blob is answered from the copy first.** Only a blob with no copy here is asked of the stores.

**Whether a blob is held is still the stores' to say.** A probe of whether a blob is held asks the stores, never the copy, so a copy here never stands for one at a store.

**The copies are trimmed with the store.** A delete of a metadata blob removes the copy first, then the stores' object. The sweep asks the stores whether a condemned blob is still there, so a delete cut short after the copy leaves the stores' object for the next pass, and no copy outlives the object it copies.

**A copy is never listed.** A listing says what the destinations hold, and a catch-up that copied from it would put back at a store what the store had dropped.

**A set adopted from a store copies the archive's metadata blobs** with its metadata (Amendment 2).

### Consequences

- **A backup reads no folder tree from the store.** At the test store the second backup's five reads of its root tree are gone. Its three probes of whether the store holds that tree remain, since that is the store's to answer.
- **A retention walk reads the trees here.** A restore still reads the data from the store.
- **The machine holds the trees' size on disk.** The data stays at the store alone.
- **A damaged copy here is read as a damaged copy at a local path would be.** The reader checks every blob it opens, and a restore reads around what fails.

### What this does not do

- **Keep the trees of a set made direct-ship before this, or from staging, or healed from a replica.** Each keeps the trees it ships from now on. A tree it built on before, unchanged since, is still read from the store.

  > **2026-11: from staging, done ([Amendment 4](#amendment-4-2026-11--a-staging-set-of-stores-alone-is-told-it-could-ship-straight)).** A set made direct-ship from staging copies its staging archive's trees with its metadata. A set made direct-ship before this amendment, and one healed from a replica, are as said.

  > **2026-11: the rest, done ([Amendment 5](#amendment-5-2026-11--a-tree-read-from-the-store-is-kept-once-it-verifies)).** Each such set keeps a tree it built on the first time it reads it from the store, once it verifies.
- **Keep the trees beside a local path or a peer.** There a backup reads them from the destination.

## Amendment 4 (2026-11) — a staging set of stores alone is told it could ship straight

**Requirements:** FR-DEST-021 (amended: a staging set whose destinations are all stores is named in a notice saying it could ship straight to them and where to change it; once it does, its staging archive's folder trees are copied here with its metadata)

### Context

Since Amendment 1 a new set naming only stores is born direct-ship. A set made before it still stages: it keeps a staging archive of its data on this machine, the copy the owner asked to be rid of. This record left moving such a set to a person, and nothing told the person a set could move.

A set made direct-ship from staging migrates its metadata at its first open (ADR-0046). That copy left the folder trees behind, so the set's backups read them from the store, as Amendment 3 left for any set made direct-ship from staging. Its staging archive holds every tree it wrote.

### Decision

**A notice names each staging set whose destinations are all stores.** It says the set keeps a staging archive of its data here though a backup writes through its stores, and where to change that: "ship straight to destinations" in the set's Other settings. It says the staging archive then stays as a read-only seed until it is retired from the notice that follows. It changes nothing: a person decides the shape. It reads the same on every pass, so an acknowledgement holds, and it is resolved once the set ships straight or names a local path or a peer.

**A set made direct-ship from staging copies its staging archive's folder trees** with its metadata, where it writes through its stores. Its backups read their trees here from the first.

### Consequences

- **A person learns which sets could keep no data here,** and where to change each.
- **A moved set reads no tree from the store.** Once its staging archive is retired, it keeps no data here.
- **The sync after each run still reads the store back** while the staging archive remains, as for any set mid-migration (ADR-0046). That is the store's to answer.

### What this does not do

- **Move a set for the person.** The shape is theirs to choose.
- **Keep the trees of a set made direct-ship before Amendment 3, or healed from a replica.** Each keeps the trees it ships from now on.

  > **2026-11: done ([Amendment 5](#amendment-5-2026-11--a-tree-read-from-the-store-is-kept-once-it-verifies)).** Each keeps a tree it built on the first time it reads it from the store, once it verifies.

## Amendment 5 (2026-11) — a tree read from the store is kept once it verifies

**Requirements:** FR-DEST-021 (amended: a tree a set that keeps its trees has no copy of is kept the first time it is read from a store, once it verifies, and one that does not verify is not kept)

### Context

Amendments 3 and 4 have a set whose stores are all it has keep the trees it ships, and the trees its staging archive held. A set made direct-ship before Amendment 3, or healed from a replica, still has none of the trees it built on here. A tree it never ships again, a folder unchanged since, is read from the store by every backup.

Keeping each tree as it is read would end that, but Amendment 3 answers a read from the copy before any store. A damaged copy kept here would be read for ever, even once the store's object was repaired. That is why keeping trees read from a store was left undone.

### Decision

**A tree a set has no copy of is kept the first time it is read from a store,** where the set keeps its trees. The sink reads the whole blob once from the first store that holds it, into a scratch store under the state directory, and verifies it there.

**Only a tree that verifies is kept.** The check is the sweep's own: the blob is whole and authentic, and it is the blob its key names. A tree that verifies is copied into the metadata store, and the read is answered from the copy. One that does not verify is not kept, and the read is answered from the store as before. An unverified copy is never where a read would find it.

### Consequences

- **A set made direct-ship before Amendment 3, or healed from a replica, reads each tree from the store once.** The backup after reads none.
- **The first read of a tree is the whole blob,** where a reader may have asked for a range. It is one request where the reader might have made several.
- **A damaged tree at the store is read from the store until it is repaired there,** as before, and never kept.

### What this does not do

- **Check a copy after it is kept.** A copy damaged on this machine is read as a damaged copy at a local path would be: the reader checks every blob it opens, and a restore reads around what fails.
- **Keep data.** A data blob is still read from the store each time.

## Amendment 6 (2026-11) — a run sends its blobs on to a store beside a local path

**Requirements:** FR-DEST-021 (amended: beside a local path, a direct-ship run sends each blob it lands there on to the set's stores as it goes, never waiting on them; a store that does not take an upload does not hold the run, one that refuses an upload is sent nothing more, and the snapshot and the index are left to the sync after the run)

### Context

Decision 2 leaves a store beside a local path to the sync after the run, so the run never waits on the network. That sync then uploads the whole run's new data in one go, after the run, and the store is behind for as long as that takes. On a large first backup or a slow link that is hours in which the off-site copy lags behind a run that has already ended.

The owner asked for the store to be fed while the run goes on, with the rule that the run never waits on it.

### Decision

1. **Each blob the run lands on a local path is sent on to the store.** One upload at a time per store, in the order the blobs landed, read back from the local path. This is the run's **write-behind** to that store. A blob the local path already held, and so the run did not write, is left to the sync.
2. **Blobs only.** The snapshot, the index and the descriptor are not sent during the run. The sync after the run sends a snapshot only once its blobs are at the store, so a store fed by both still never holds a backup whose blobs it lacks.
3. **The run never waits on it.** When the run ends, the write-behind ends with it, an upload in flight included; a put that is cut off lands whole or not at all (ADR-0012 Amendment 5). What has not reached the store is the sync's to send, as before.
4. **The first failure ends it, and never the run.** The store is sent nothing more for the rest of the run. The failure is kept as the reason in the store's behind detail; the sync after the run meets the store as it would have, and records what it finds.
5. **The store stays behind until the sync,** with a detail saying how many of the run's blobs went up as it went, or why the run stopped sending them. A background run's uploads are paced at the store's transfer limit, as the sync's are.
6. **Only beside a local path.** A blob is read back from the local path the run wrote it to. Beside a peer alone there is nothing to read it back from without a retrieval session beside the run's own, so nothing is sent during the run, and a blob that lands only at a peer, its local path having left the run, ends the write-behind there. A store that cannot be opened before any request, for a defective address, no credential stored, a lapsed signature or a stored credential that no longer reads, is also left to the sync, which says why; none of these fails the run, as none could before the run sent a store anything.

### Consequences

- **The sync after a run sends what is left:** the blobs the write-behind did not reach, the snapshot and the index. On a link that keeps up, that is a few objects.
- **A store can hold blobs no snapshot names yet,** between the run and its sync, or for good where the run's snapshot never commits. That is the state an interrupted sync leaves, and it is met as one.
- **The requests are counted as the store's,** in the store's request count (ADR-0111), whether the run or the sync made them. An upload cut off at the run's end is a request the store may have received and the count does not hold.

### What this does not do

- **Send the snapshot or the index during the run.** They stay the sync's, for the reason in decision 2.
- **Make the store current at the run's end.** It is behind until the sync, as before. The console's note that a store beside a local path is filled by the sync after each run still holds.
- **Send blobs to a store beside a peer alone.**

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first, against the in-process S3 and Azure stores. |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--a-new-sets-shape-follows-its-destinations): a new set that names no shape is born direct-ship where it names a local path or every destination it names is a store, and staging where a peer is all a run could ship to; the console's new set follows the same rule until the person sets it (before, it was always direct-ship), and its Storage shape note says a set of stores alone is written through as it runs. `Agent/ServiceCommandHandler`; `Hosts.Tests/DirectShipTests`; `Web.DomTests/NewSetWizardDomTests` |
| 2026-11 | Amended | [Amendment 2](#amendment-2-2026-11--a-set-adopted-from-a-store-ships-straight-to-it): a set adopted from a store is adopted direct-ship, as from a local path or a peer; only the archive's metadata is copied to the machine, with no staging archive and no data, and the preview and the answer say the set ships straight to the store. ADR-0091 Amendment 1 decision 6, ADR-0093 decision 5 and ADR-0061 §3 amended (related). `Agent/ServiceCommandHandler.Adoption.cs`; `Hosts.Tests/ObjectStoreAdoptionTests` |
| 2026-11 | Amended | [Amendment 3](#amendment-3-2026-11--a-store-only-set-keeps-its-folder-trees-on-the-machine): a set whose stores are all it has keeps a copy of each metadata blob it ships in its metadata store, reads its folder trees there, deletes a copy before the stores' object and never lists it; whether a blob is held is still asked of the stores; a set adopted from a store copies the archive's metadata blobs too. `Agent/DestinationShipSink`; `Agent/ServiceCommandHandler.Adoption.cs`; `Hosts.Tests/ObjectStoreDestinationTests`; `Hosts.Tests/ObjectStoreAdoptionTests` |
| 2026-11 | Amended | [Amendment 4](#amendment-4-2026-11--a-staging-set-of-stores-alone-is-told-it-could-ship-straight): a notice names each staging set whose destinations are all stores, says it could ship straight to them and where to change that, and is resolved once the set ships straight or names a local path or a peer; a set made direct-ship from staging copies its staging archive's folder trees with its metadata. ADR-0046 amended (related). `Agent/StagingCouldShipWatch`; `Agent/Scheduler`; `Agent/ServiceRuntime`; `Hosts.Tests/ObjectStoreDestinationTests` |
| 2026-11 | Amended | [Amendment 5](#amendment-5-2026-11--a-tree-read-from-the-store-is-kept-once-it-verifies): a tree a set that keeps its trees has no copy of is read whole from the store once, verified by the sweep's own check in a scratch store, and kept only if it verifies; one that does not is not kept, and the read is answered from the store. `Agent/DestinationShipSink`; `Agent/ServiceRuntime`; `Repository/VerifyEngine`; `Hosts.Tests/ObjectStoreDestinationTests` |
| 2026-11 | Amended | [Amendment 6](#amendment-6-2026-11--a-run-sends-its-blobs-on-to-a-store-beside-a-local-path): beside a local path, a direct-ship run sends each blob it lands there on to the set's stores as it goes, one at a time and in order, read back from the local path, never waiting on them; the snapshot and the index are left to the sync after the run; the first failure ends it and never the run; the store is behind until that sync, saying what went up or why it stopped. ADR-0091 decision 5 and ADR-0093 decision 5 amended (related). `Agent/StoreWriteBehind`; `Agent/DestinationShipSink`; `Hosts.Tests/ObjectStoreDestinationTests` |

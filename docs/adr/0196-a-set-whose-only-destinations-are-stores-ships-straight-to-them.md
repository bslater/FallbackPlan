# ADR-0196 — A set whose only destinations are stores ships straight to them

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-DEST-021 (new: a direct-ship set whose destinations include no local path and no peer writes its backup through its object stores as the run goes, keeps no data on the machine, restores from them, and a run that cannot reach them fails naming them); FR-DEST-015 (amended: the refusal of a capture with nowhere to write names each destination it tried and why); FR-DEST-021 (amended by [Amendment 1](#amendment-1-2026-11--a-new-sets-shape-follows-its-destinations): a new set that names no shape is born direct-ship where every destination it names is a store, and the console's new set follows its destinations as the service does); FR-DR-009 (amended by [Amendment 2](#amendment-2-2026-11--a-set-adopted-from-a-store-ships-straight-to-it): a set adopted from a store is adopted direct-ship, with only the archive's metadata copied to the machine); FR-DEST-021 (amended by [Amendment 3](#amendment-3-2026-11--a-store-only-set-keeps-its-folder-trees-on-the-machine): a set whose stores are all it has keeps a copy of each metadata blob it ships on the machine, so a backup reads no folder tree from the store)
**Related:** [ADR-0046](0046-direct-to-destination-publication.md) (direct-to-destination publication: the ship sink, the metadata store, and the capture with nowhere to ship), [ADR-0058](0058-peer-write-adapter.md) §8 (why a peer-only set still stages by default), [ADR-0091](0091-an-s3-compatible-destination.md) decision 5 and [ADR-0093](0093-an-azure-blob-destination.md) (a run never writes through a store), [ADR-0012](0012-storage-provider-contract.md) Amendment 5 (a put lands whole or not at all), [ADR-0061](0061-adopt-a-destinations-archives.md) §3 (adoption, which makes the set direct-ship)

**Built:**
- The sink: `Agent/DestinationShipSink` writes through a set's object stores when nothing the set names is a local path or a peer (`WritesThroughStores`), opening each through `Agent/StoreComposition`, and reads from them outside a run. A run with nowhere to write names each destination it tried.
- The kept trees: `Agent/DestinationShipSink` keeps a copy of each metadata blob a set that writes through its stores ships, answers a read of one from that copy first, deletes the copy before the stores' object, and never lists it (Amendment 3).
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
    - a store that does not answer fails the run, naming it.
    - A set with a local path beside the store: the run itself writes nothing to the store, and leaves it behind for the sync that follows.
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
- **Keep the metadata blobs on the machine.** Doing so would spare each backup its reads of the trees, at the cost of the metadata's size on disk.

  > **2026-11: done ([Amendment 3](#amendment-3-2026-11--a-store-only-set-keeps-its-folder-trees-on-the-machine)).** A set whose stores are all it has keeps a copy of each metadata blob it ships.
- **Write through a store beside a local path or a peer.** The store is filled after the run, as before.

## Alternatives considered

- **Adopt the other tool's on-disk format.** Rejected. Its volumes are another repository format. Adopting it would replace formats 2 to 6 and restart the freeze gate (roadmap), for a split this product already has.
- **Write through every store in every direct-ship set.** Rejected. A set with a local path would wait on the network for nothing: the store is filled from what the run shipped, a moment after it.
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

- **A backup reads no folder tree from the store.** At the test store the second backup's five reads of its root tree are gone. Whether the store holds each new blob is still asked, once.
- **A retention walk reads the trees here.** A restore still reads the data from the store.
- **The machine holds the trees' size on disk.** The data stays at the store alone.
- **A damaged copy here is read as a damaged copy at a local path would be.** The reader checks every blob it opens, and a restore reads around what fails.

### What this does not do

- **Keep the trees of a set made direct-ship before this, or from staging, or healed from a replica.** Each keeps the trees it ships from now on. A tree it built on before, unchanged since, is still read from the store.
- **Keep the trees beside a local path or a peer.** There a backup reads them from the destination.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first, against the in-process S3 and Azure stores. |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--a-new-sets-shape-follows-its-destinations): a new set that names no shape is born direct-ship where it names a local path or every destination it names is a store, and staging where a peer is all a run could ship to; the console's new set follows the same rule until the person sets it (before, it was always direct-ship), and its Storage shape note says a set of stores alone is written through as it runs. `Agent/ServiceCommandHandler`; `Hosts.Tests/DirectShipTests`; `Web.DomTests/NewSetWizardDomTests` |
| 2026-11 | Amended | [Amendment 2](#amendment-2-2026-11--a-set-adopted-from-a-store-ships-straight-to-it): a set adopted from a store is adopted direct-ship, as from a local path or a peer; only the archive's metadata is copied to the machine, with no staging archive and no data, and the preview and the answer say the set ships straight to the store. ADR-0091 Amendment 1 decision 6, ADR-0093 decision 5 and ADR-0061 §3 amended (related). `Agent/ServiceCommandHandler.Adoption.cs`; `Hosts.Tests/ObjectStoreAdoptionTests` |
| 2026-11 | Amended | [Amendment 3](#amendment-3-2026-11--a-store-only-set-keeps-its-folder-trees-on-the-machine): a set whose stores are all it has keeps a copy of each metadata blob it ships in its metadata store, reads its folder trees there, deletes a copy before the stores' object and never lists it; whether a blob is held is still asked of the stores; a set adopted from a store copies the archive's metadata blobs too. `Agent/DestinationShipSink`; `Agent/ServiceCommandHandler.Adoption.cs`; `Hosts.Tests/ObjectStoreDestinationTests`; `Hosts.Tests/ObjectStoreAdoptionTests` |

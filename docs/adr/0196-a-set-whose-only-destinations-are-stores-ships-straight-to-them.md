# ADR-0196 — A set whose only destinations are stores ships straight to them

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-DEST-021 (new: a direct-ship set whose destinations include no local path and no peer writes its backup through its object stores as the run goes, keeps no data on the machine, restores from them, and a run that cannot reach them fails naming them); FR-DEST-015 (amended: the refusal of a capture with nowhere to write names each destination it tried and why)
**Related:** [ADR-0046](0046-direct-to-destination-publication.md) (direct-to-destination publication: the ship sink, the metadata store, and the capture with nowhere to ship), [ADR-0058](0058-peer-write-adapter.md) §8 (why a peer-only set still stages by default), [ADR-0091](0091-an-s3-compatible-destination.md) decision 5 and [ADR-0093](0093-an-azure-blob-destination.md) (a run never writes through a store), [ADR-0012](0012-storage-provider-contract.md) Amendment 5 (a put lands whole or not at all)

**Built:**
- The sink: `Agent/DestinationShipSink` writes through a set's object stores when nothing the set names is a local path or a peer (`WritesThroughStores`), opening each through `Agent/StoreComposition`, and reads from them outside a run. A run with nowhere to write names each destination it tried.
- The set check: `Agent/ServiceCommandHandler` accepts a direct-ship set that names only stores.
- The tests:
  - `Hosts.Tests/ObjectStoreDestinationTests`, run for S3 and for Azure alike, with the store the set's only destination:
    - a backup ships the data to the store and keeps no data blob on the machine;
    - a backup after another puts the new blob and nothing the store held;
    - the set's own archive restores from the store, every record from its own archive;
    - a retention report reads the folder trees from the store, and walks whole;
    - a store that does not answer fails the run, naming it.
    - A set with a local path beside the store: the run itself writes nothing to the store, and leaves it behind for the sync that follows.
  - `Hosts.Tests/DirectShipTests` — a direct-ship set naming only a bucket, or only a container, is accepted; one naming only a reserved kind is still refused.

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
- **A restore, a retention pass, a preview and a catch-up copy read from the store,** each read a request.

## What this does not do

- **Make a store-only set direct-ship by default.** A new set naming only stores still stages unless it is made direct-ship. That default, and saying it in the console, is the next step.
- **Adopt a set from a store as direct-ship.** A set adopted from a store is still staged (ADR-0091 Amendment 1).
- **Move existing store-only sets.** A set that stages can be made direct-ship by changing its shape, and its staging archive retired, through the machinery ADR-0046 built; nothing does it for a person.
- **Keep the metadata blobs on the machine.** Doing so would spare each backup its reads of the trees, at the cost of the metadata's size on disk.
- **Write through a store beside a local path or a peer.** The store is filled after the run, as before.

## Alternatives considered

- **Adopt the other tool's on-disk format.** Rejected. Its volumes are another repository format. Adopting it would replace formats 2 to 6 and restart the freeze gate (roadmap), for a split this product already has.
- **Write through every store in every direct-ship set.** Rejected. A set with a local path would wait on the network for nothing: the store is filled from what the run shipped, a moment after it.
- **Keep staging, and only trim harder.** Rejected. A trimmed staging archive still holds the newest backup's data, which is the copy the owner asked to be rid of.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first, against the in-process S3 and Azure stores. |

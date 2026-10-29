# ADR-0153 — A folder's access time goes with the last snapshot that reaches its tree

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-011, NFR-PERF-003, FR-GC-013
**Related:** [ADR-0151](0151-the-catalogue-keeps-a-folders-entries-once-under-its-tree.md) (which recorded this gap, and whose walk this reuses), [ADR-0099](0099-a-folder-that-was-only-read-keeps-its-tree.md) (why the catalogue keeps a folder's access time at all), [ADR-0010](0010-local-store-separation.md) (the catalogue is a cache), [architecture 02 §7.3](../architecture/02-repository-format.md)

**Built:**
- The catalogue: `Repository.Catalogue/Catalogue` lets go of every folder access time no remaining snapshot reaches, in the transaction that forgets snapshots and lets go of their folders. It records a snapshot's access times together, in one transaction.
- The writers: `Repository/SnapshotPublication`, `Repository/CatalogueProjector` and `Repository.Catalogue/Forensic/ForensicRebuilder` record a snapshot's access times after its row and its folders.
- The tests:
  - `Repository.Tests/Catalogue/CatalogueTests` — forgetting a snapshot lets go of the access time of each folder none of the rest reaches and keeps the others', an empty folder's included; a tree met again keeps the time it states
  - `Repository.Tests/EndToEnd/FolderAccessTimeTests` — once the snapshot before is forgotten, a folder that was only read still keeps its tree

---

## Context

A folder's tree states the folder's access time, and the tree's identity derives from its bytes. A backup reads every folder it walks, which moves the access time. So a capture gives a folder whose only change is that time the time its prior tree stated, and with it the prior tree ([ADR-0099](0099-a-folder-that-was-only-read-keeps-its-tree.md)). It reads that prior time from the catalogue's `directory_access_times`, keyed by tree.

[ADR-0151](0151-the-catalogue-keeps-a-folders-entries-once-under-its-tree.md) keyed a folder's entries by its tree, and made forgetting snapshots let go of every folder none of the rest reaches. It recorded that access times were still never let go of. A set that keeps a bounded number of snapshots therefore kept a row for every folder tree any of its snapshots ever had.

The capture recorded each access time in a transaction of its own. The projection from a store and the forensic rebuild recorded them while they walked, before the folders that reach them.

## Decision

### 1. Forgetting lets go of the access times no snapshot reaches

When the catalogue forgets snapshots, it first lets go of every folder no remaining root reaches, as before. In the same transaction it then lets go of every access time whose tree is not a remaining root or a folder that a remaining folder holds.

After the first step, every folder entry left belongs to a folder some snapshot reaches. So the reached folders are exactly the roots and the folders those entries name. The second step needs no walk of its own.

An empty folder holds no entries, so it has no rows of its own. It is still named by its parent's entry, and its time stays while that parent is held.

### 2. A snapshot's access times are recorded together, after its folders

The catalogue records a snapshot's access times in one transaction. Every writer gives them after the snapshot's row and its folders, as it gives the folders after the row.

Recorded before them, a time could be let go of by a snapshot forgotten in between, since nothing would yet reach its tree. The next capture would then not find the prior time, and would write that folder, and every folder above it, again.

A tree states one access time, so recording one already held keeps the time it has.

## Consequences

- A set's catalogue keeps an access time for each folder tree its kept snapshots hold, not for each one it ever held.
- A folder that was only read keeps its tree across a forgotten snapshot, as across a rebuild.
- The capture writes its access times in one commit, where it made one for each folder.

## What this does not do

- **Measure the plane.** The catalogue's size measurement seeds no access times, so the figures [NFR-PERF-011](../requirements/non-functional.md) records leave this table out. It holds one row for each folder tree a kept snapshot holds, which a folder only read does not add to.
- **Share what a snapshot is made of.** `snapshot_structure` still names every tree of every snapshot ([ADR-0151](0151-the-catalogue-keeps-a-folders-entries-once-under-its-tree.md)).

## Alternatives considered

- **Keep the access time in `folder_entries`, on the folder's entry in its parent.** It would then go with the parent's rows. Rejected: a root has no parent, and one folder held under two parents would state its one time twice.
- **Walk the remaining roots again for the access times.** Rejected: once the folders no root reaches are gone, the entries left already say which folders are reached.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Forgetting snapshots lets go of every folder access time no remaining snapshot reaches, in the transaction that lets go of their folders, and a snapshot's access times are recorded together after its row and folders. |

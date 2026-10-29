# ADR-0151 — The catalogue keeps a folder's entries once, under its tree

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-011 (amended: a snapshot pays for the folders it changed), NFR-PERF-004, FR-GC-013
**Related:** [ADR-0148](0148-a-tree-entry-is-keyed-by-its-folder-and-name.md) (the key this replaces), [ADR-0099](0099-a-folder-that-was-only-read-keeps-its-tree.md) (a folder only read keeps its tree, so it is shared too), [ADR-0146](0146-the-catalogue-folds-a-paths-case-when-asked-not-when-stored.md) (the case-folding walk), [ADR-0010](0010-local-store-separation.md) (the catalogue is a cache), [architecture 02 §7.3](../architecture/02-repository-format.md), [phase-0 benchmarks §5](../phase-0-benchmarks.md#5-catalogue-size-per-file-version-nfr-perf-011)

**Built:**
- The schema: `Repository.Catalogue/CatalogueSchema` at version 17, where `folder_entries` keeps an entry by the folder's head tree and its name, and no table keeps an entry by snapshot.
- The catalogue: `Repository.Catalogue/Catalogue` records a snapshot's folders in one transaction and passes over a tree it already holds. It finds a path from the snapshot's root tree a name at a time, and walks a snapshot's folders for its leaves, its counts, its content and the damage traced to it. When it forgets snapshots, it lets go of every folder none of the rest reaches.
- The writers: `Repository/SnapshotPublication`, `Repository/CatalogueProjector` and `Repository.Catalogue/Forensic/ForensicRebuilder` give each folder's entries under its head tree, a sharded folder's chain included, after the row of the snapshot that reaches them. `Agent/ServiceCommandHandler` forgets a pass's deleted snapshots together.
- The tests:
  - `Repository.Tests/Catalogue/CatalogueTests` — two snapshots holding one folder keep its entries once and each finds them at its own path; forgetting a snapshot keeps the folders another reaches and lets the rest go; a lookup's resolved folder does not outlive a snapshot recorded again or forgotten, nor a folder recorded after it missed
  - `Repository.Tests/Catalogue/CatalogueReachTests` — damage to a folder two snapshots share reaches both, at the one path
  - `Repository.Tests/Catalogue/CatalogueSizeTests` — a snapshot that changes nothing costs its files nothing, and one that changes a file pays for that file's folder whole and each folder above it
  - `TestSupport/CatalogueTrees`, `TestSupport/CatalogueSeeding` — tests and the measurement record folders as publication does, each tree once for what it holds

---

## Context

Each snapshot holds every file of its set, changed or not. Schema 16 kept an entry for each file of each snapshot, keyed by snapshot, folder and name ([ADR-0148](0148-a-tree-entry-is-keyed-by-its-folder-and-name.md)). So the catalogue grew with the snapshots a set kept as well as with its versions. Each file a snapshot left alone cost it a row of about 131 B.

The NFR-PERF-011 headline names each version in one snapshot, which is a set whose every file changes in every snapshot. At scale M's ten versions per file, a set where a tenth of the files change per snapshot keeps 91 snapshots and cost 2 005 B/version. A hundredth keeps 901 and cost 12 590, about 126 GB at scale M. The decision put to the person was what a kept snapshot should cost, and they chose to share a folder's entries between snapshots whose folders did not change.

A folder's tree is an object whose identity derives from what it holds: its entries, its name and its own metadata. A folder a snapshot left as it was is the tree an earlier snapshot published. That includes a folder whose only change is that it was read ([ADR-0099](0099-a-folder-that-was-only-read-keeps-its-tree.md)). Its entries are already in the catalogue.

## Decision

### 1. A folder's entries are kept under its tree

Schema 17 replaces `tree_entries` with `folder_entries`. It keeps an entry's name, kind and object, keyed by the head tree of the folder it is in and its name there. A sharded folder's entries span its chain and are all its head's. No row names a snapshot.

A snapshot reaches its own entries from its row's root tree, a folder at a time, through the entries that name a folder's tree. Two snapshots holding a folder unchanged reach the same rows, and the second pays nothing for them.

### 2. A path is followed from the root, a name at a time

The readers keep their path API:
- A lookup follows the path's folders from the snapshot's root tree, one key seek a name, in one statement, and then seeks the name in the folder it reached.
- A folder's listing reads its tree's rows in name order.
- A snapshot's leaves, its file count, whether its files carry identities, its content, what a destination holds of it, and the paths damage reaches are found by walking its folders from the root.

A path is names joined by a separator. One that begins with a separator names an empty one first, which no folder holds, so it is found nowhere. A name that holds a separator is listed as it is and is never what a path reaches.

The catalogue remembers the last folder a lookup resolved. A capture asks a folder's files in turn, so the folder is resolved once for all of them. A snapshot's trees never change. This catalogue forgets what it resolved whenever it records or forgets a snapshot, or records folders.

### 3. A snapshot's folders are recorded together, after its row

A writer gives a snapshot's folders to the catalogue together, once its walk is done and after the snapshot's own row. This covers the capture, the projection from a store and the forensic rebuild. The catalogue records them in one transaction and passes over a tree whose entries it already holds. A tree's entries are recorded whole or not at all, so any row means all of them.

### 4. Forgetting a snapshot lets go of what nothing else reaches

Forgetting snapshots deletes their rows and the records they were made of. It then lets go of every folder that no remaining snapshot's root reaches, all in one transaction. A capture recording its folders on another connection therefore sees all of it or none of it.

A folder it let go of before that capture's transaction is not held when the capture asks, so the capture records it again. A folder recorded after its snapshot's row is reached from that row, and is never let go of in between.

The walk that finds what is reached is a pass over the folders kept. So a retention pass forgets all the snapshots it deleted together, and the walk runs once.

## Consequences

Measured with `catalogue-size 20000`:

| | Before (schema 16) | Now (schema 17) |
|---|---:|---:|
| The tree plane | 138 B/version | 115 |
| Scale M's shape, all planes | 942 | 919 |
| The floor | 395 | 372 |
| A tenth of the files changed per snapshot, 91 snapshots | 2 005 | **930** |
| A hundredth changed per snapshot, 901 snapshots | 12 590 | **1 428** |

A snapshot that changed nothing costs its files nothing. One that changed a file pays for that file's folder, whole, and for each folder above it.

Lookups stay inside NFR-PERF-004. `pathlookup` at 100 000 files, against schema 16 on the same machine:
- a path asked in a folder's turn costs 37 µs/op, against 45;
- a path in a folder the last lookup did not ask costs 116 µs/op;
- a folder's listing costs 353 µs/op, against 303;
- a snapshot's leaves in path order cost 525 ms, against 533.

A snapshot's folders are written in one transaction, where every entry was a commit of its own.

## What this does not do

- **Keep a changed folder's unchanged entries.** A folder that changed is a new tree and restates every entry, siblings included, and so does every folder above it. Where a hundredth of the files change per snapshot, that is still 1 428 B/version against the budget's 400. A folder of many files changes whole for one of them.
- **Share what a snapshot is made of.** `snapshot_structure` still names every tree of every snapshot, so damage to a tree is traced to each snapshot holding it. That grows with the snapshots too, by folder rather than by file. The measurement does not seed it.
- **Let go of folder access times.** `directory_access_times` is keyed by tree and is still never let go of. It has a row for each folder tree a capture recorded, not for each file.
- **Read fewer trees.** A projection from a store still reads every tree of every snapshot, because it lists each snapshot's trees for damage tracing. It writes only those it does not hold.
- **Find a version by identity without a walk.** A lookup by a file's identity is answered from the identity index when no version has it, which is every new file. Otherwise it walks the snapshot's folders, as schema 16 scanned the snapshot's rows.

## Alternatives considered

- **One entry for each run of snapshots in which a path kept its version.** Entries would then grow with versions rather than with snapshots, a changed folder would restate nothing, and every lookup would need a range of snapshots. Not chosen: it was the other option put to the person, and keying by tree makes the trees the repository already has the unit of sharing.
- **Count the snapshots holding each tree, and let a tree go when its count reaches nought.** Forgetting a snapshot would then not need a walk. Not chosen: every recording would have to keep the counts exact for every tree it passes over, and one miscount would leave a folder no snapshot reaches, or let go of one a snapshot does. The walk can only keep too much for a moment.
- **Index `folder_entries` by object, to find a version's paths bottom-up.** Not chosen: the index would restate every row to speed up damage tracing and identity lookups. Both are paid only when damage is outstanding or a file moved.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Catalogue schema 17 keeps a folder's entries under its head tree and its name, so snapshots that hold a folder unchanged share its rows. A snapshot's folders are recorded together after its row, and forgetting snapshots lets go of every folder none of the rest reaches. At 20 000 versions a set where a tenth of the files change per snapshot costs 930 B/version against 2 005, and one where a hundredth do costs 1 428 against 12 590. |

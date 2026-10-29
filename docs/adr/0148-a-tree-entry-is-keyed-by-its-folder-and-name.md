# ADR-0148 — A tree entry is keyed by its folder and name

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-011 (amended: re-measured at 942 B/version, with the floor inside the budget; still not met), NFR-PERF-004 (re-measured, unchanged)
**Related:** [ADR-0147](0147-a-file-versions-catalogue-row-keeps-only-what-is-asked-of-it.md) (the record before this one), [ADR-0146](0146-the-catalogue-folds-a-paths-case-when-asked-not-when-stored.md) (its folder-by-folder walk now reads this key), [ADR-0010](0010-local-store-separation.md) (the catalogue is a cache, rebuilt on a schema change)

**Built:**
- The schema: `Repository.Catalogue/CatalogueSchema` at version 16, where `tree_entries` is keyed by snapshot, folder and name, and keeps no path and no index.
- The queries: `Repository.Catalogue/Catalogue` splits a path into its folder and name to look it up, lists a folder through the key, rebuilds a path where a query returns one, and sorts a snapshot's leaves into path order.
- The tests:
  - `Repository.Tests/Catalogue/CatalogueTests` — a path whose only separator leads it stays apart from the same name without one; a snapshot's leaves are in path order across folders, a folder's listing in name order, and a folder is found by its path; two folders, or two files, of one name in different folders are found, listed and counted apart
  - `Repository.Tests/Catalogue/CatalogueSizeTests` — the tree plane costs what its row costs, and a version with no segments costs about the whole budget

---

## Context

`tree_entries` kept each entry of a snapshot's tree by its snapshot and path, with its folder beside them. Schema 3 added `ix_tree_entries_parent`, a covering index on snapshot, folder, path, kind and object. Without it a folder's listing scanned the whole snapshot, at 15 ms/op against 0.28 ([phase-1 benchmarks](../phase-1-benchmarks.md)).

So each entry was stated twice, once in the row and once in the index. Each statement carried its folder twice, once as the folder and again as the start of the path.

At scale M's shape `tree_entries` cost 378 B/version of the catalogue's 1 183 ([ADR-0147](0147-a-file-versions-catalogue-row-keeps-only-what-is-asked-of-it.md)).

## Decision

### 1. An entry is keyed by its folder and name

Schema 16's `tree_entries` keeps an entry's snapshot, folder, name, kind and object, keyed by snapshot, folder and name, with no other index. A folder's listing is a range of that key, in name order, and within one folder that is path order.

> **2026-10 ([ADR-0151](0151-the-catalogue-keeps-a-folders-entries-once-under-its-tree.md)).** Schema 17 keys an entry by its folder's head tree and its name instead, and no row names a snapshot. Snapshots that hold a folder unchanged share its rows, and a snapshot reaches its own from its root tree. A listing is still a range of the key in name order.

### 2. A path is split and rebuilt one way

A path's folder is everything before its last separator, and its name everything after. A path whose only separator is its first character names no folder. It is kept whole as a name at the root, apart from the same name without the separator. No codec refuses a separator inside a tree's names.

Where a query returns a path, it rebuilds it: a root entry's name, or else the folder, a separator and the name.

> **2026-10 ([ADR-0151](0151-the-catalogue-keeps-a-folders-entries-once-under-its-tree.md)).** A path is now followed from the root a name at a time, so one that begins with a separator names an empty one first and is found nowhere, and a name that holds a separator is listed but is never what a path reaches.

### 3. A snapshot's leaves are sorted into path order

Path order is not folder order followed by name order. Some siblings' names have a byte lower than the separator, and some a byte higher, and a folder's files sort between the two. The key gave path order before and no longer does, so the listing of a snapshot's leaves sorts by the rebuilt path.

## Consequences

- At scale M's shape `tree_entries` falls from 378 B/version to 138, and the catalogue from 1 183 to 942, about 9 GB at scale M.
- The floor falls from 636 to 395, inside the budget for the first time. What is over the budget now is the segment references: `object_locations` costs 661 B/version, about 110 for each of the six locations a version has at that shape.
- Lookups cost what they did. At 100 000 files, three runs each before and after (the ranges overlap, so the change is within what this machine varies by between runs):

  | Operation | Before | After |
  |---|---:|---:|
  | `LookupPath` (µs/op) | 35–41 | 37–47 |
  | `ListDirectory` (µs/op) | 345–417 | 309–426 |
  | `EnumerateLeaves` (ms per snapshot) | 683–732 | 670–773 |

  The leaves are now sorted on each call, rather than read in an index's order.

## What this does not do

- **Meet NFR-PERF-011.** The catalogue is still more than twice its budget.
- **Change a path, a listing or an order any reader sees.**
- **Name a folder or a snapshot by a number.** Each entry still states its folder's whole path and its snapshot's sixteen bytes.

## Alternatives considered

- **Key by snapshot, folder and path.** Rejected. Each entry would still state its folder twice.
- **Keep an index on the folder alone, without the covering columns.** Rejected. A listing would read the table once per child, the cost the covering index was added to stop.
- **Key by folder and name, but keep the path for the queries that return one.** Rejected. The path is the folder again.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Catalogue schema 16 keys a tree entry by snapshot, folder and name, with no path and no covering index, and a snapshot's leaves are sorted into path order. At scale M's shape tree_entries falls from 378 B/version to 138 and the catalogue from 1 183 to 942, and the floor, at 395, is inside the budget. NFR-PERF-011 is still not met. |
| 2026-10 | Accepted (amended) | [ADR-0151](0151-the-catalogue-keeps-a-folders-entries-once-under-its-tree.md): an entry is keyed by its folder's head tree and its name rather than by snapshot, folder and name, so snapshots that hold a folder unchanged share its rows; a path is followed from the root a name at a time. |

# ADR-0146 — The catalogue folds a path's case when asked, not when stored

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-011 (amended: re-measured at 1 373 B/version; still not met)
**Related:** [ADR-0026](0026-phase-1-capture-shapes.md) (§8 chose the fold as the catalogue's case-insensitive key, and this record amends where it is kept), [ADR-0145](0145-the-catalogue-keeps-no-map-from-content-to-object.md) (the record before this one), [ADR-0010](0010-local-store-separation.md) (the catalogue is a cache, rebuilt on a schema change)

**Built:**
- The schema: `Repository.Catalogue/CatalogueSchema` at version 14, where `tree_entries` keeps no fold of its path and no index on one.
- The lookup: `Repository.Catalogue/Catalogue` takes a case-insensitive lookup a folder at a time through the parent index, folding each child's name as it is compared.
- The tests:
  - `Repository.Tests/Catalogue/CatalogueTests` — every folder whose name folds alike is followed, and only the one holding the rest finds it; the exact path wins over one that folds alike, and otherwise the first in ordinal order does; a decomposed name is found by its composed form; a lookup not asked to fold does not
  - `Repository.Tests/Catalogue/CatalogueSizeTests` — the tree plane costs what its row and parent index cost
  - `Repository.Tests/EndToEnd/IncrementalBackupTests` — a published path is still found by another case

---

## Context

[ADR-0026](0026-phase-1-capture-shapes.md) §8 made the catalogue's path index key the fold of each path: the invariant lowercase of its NFC form, the case regime rules-v1 matching uses. Each tree entry stored that fold in its row, as `path_casefold`, and again in `ix_tree_entries_casefold`, whose every entry also carries the row's key, the path itself.

The only reader was a lookup asked to fold case, and no product path asks it. Publication, the restore planner and the command line each look a path up as it is. The restore plan's check for paths a case-insensitive target would fold together folds the plan's paths in memory, and never read the column. At scale M's shape `tree_entries` was the largest plane, at 613 B/version of 1 607 ([ADR-0145](0145-the-catalogue-keeps-no-map-from-content-to-object.md)).

## Decision

### 1. The fold is not stored

Schema 14's `tree_entries` keeps no fold of its path and no index on one. A path is held in the row's key and in the parent index, which a folder's listing reads.

### 2. A lookup that folds walks the path

A lookup asked to fold case takes the asked path a folder at a time. At each level it lists the children of every folder matched so far through the parent index, and keeps each child whose name folds as the asked component does. Two folders that fold alike are both followed, since only one may hold the rest. Of the paths it ends on, it takes the asked path itself where the snapshot holds it, and otherwise the first in ordinal order, as before.

The fold is ADR-0026 §8's, applied to a component rather than a path. A separator neither composes nor changes case, so the two agree.

## Consequences

- At scale M's shape `tree_entries` falls from 613 B/version to 379, and the catalogue from 1 607 to 1 373, about 14 GB at scale M. The floor falls from 1 052 to 818.
- A lookup that folds costs one listing of each matching folder on the way down, instead of one index read. Nothing in the product makes one today.

## What this does not do

- **Meet NFR-PERF-011.** The catalogue is still more than three times its budget, and its floor twice.
- **Change what folds alike.** The fold is ADR-0026 §8's, unchanged.
- **Change the restore plan's collision check.** It folded in memory before, and still does.

## Alternatives considered

- **Keep the fold only where it differs from the path.** Rejected. A path with a capital anywhere in it, as most home folders have, would keep the whole fold, and a lookup would have to read two columns.
- **Register the fold as an SQL function and scan for it.** Rejected. A lookup would read every entry of the snapshot.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Catalogue schema 14 keeps no fold of a tree entry's path, and a lookup that folds case walks the path a folder at a time through the parent index. At scale M's shape tree_entries falls from 613 B/version to 379 and the catalogue from 1 607 to 1 373. NFR-PERF-011 is still not met. |
| 2026-10 | Accepted | [ADR-0147](0147-a-file-versions-catalogue-row-keeps-only-what-is-asked-of-it.md): a file version's row keeps only what is asked of it, and at scale M's shape the catalogue costs 1 183 B/version, not 1 373. |

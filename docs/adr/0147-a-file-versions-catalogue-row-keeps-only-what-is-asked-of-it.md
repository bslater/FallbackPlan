# ADR-0147 — A file version's catalogue row keeps only what is asked of it

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-011 (amended: re-measured at 1 183 B/version; still not met)
**Related:** [ADR-0146](0146-the-catalogue-folds-a-paths-case-when-asked-not-when-stored.md) (the record before this one), [ADR-0010](0010-local-store-separation.md) (the catalogue is a cache, rebuilt on a schema change)

**Built:**
- The schema: `Repository.Catalogue/CatalogueSchema` at version 15, where `file_versions` keeps a version's length, modification time, identity, alternate-streams flag and metadata digest, and no index on a hash.
- The writer: `Repository.Catalogue/Catalogue` records only those. Publication, the projection and the forensic rebuild pass only those.
- The tests: `Repository.Tests/Catalogue/CatalogueSizeTests` — the file-version plane costs what its row and identity index cost

---

## Context

Every route that fills a catalogue records each file version it meets: publication, the projection from the index and the forensic rebuild. Each wrote twelve columns of the version's manifest into `file_versions`, and indexed one of them, the whole-file hash, in `ix_file_versions_hash`.

Seven are read:
- the object's identifier, as the row's key;
- the length, the modification time, the identity and the metadata digest, which the incremental short-circuit compares (NFR-PERF-003);
- the identity again, through its own index, to find a renamed file (architecture 06 §1);
- the alternate-streams flag, which the restore planner declares a degradation from (RR-6).

The other five are not:
- the version's name;
- its kind, which the tree entry naming it already keeps and is what a lookup reads;
- its whole-file hash, and the index on it;
- its parent version;
- its segment count.

Nothing reads any of them, and nothing looks a version up by its hash.

The parent version shows it plainest. Publication wrote it as absent for every version it captured, whatever the manifest said, while the projection and the forensic rebuild wrote the manifest's. A warm catalogue and a rebuilt one disagreed about every version that had a parent, and nothing noticed, because nothing asked.

At scale M's shape `file_versions` cost 345 B/version of the catalogue's 1 373 ([ADR-0146](0146-the-catalogue-folds-a-paths-case-when-asked-not-when-stored.md)).

## Decision

### 1. The row keeps what is asked of it

Schema 15's `file_versions` keeps a version's identifier, length, modification time, identity, alternate-streams flag and metadata digest, and the index on its identity. It keeps no name, kind, whole-file hash, parent version or segment count, and no index on a hash.

`RecordFileVersion` takes only what the row keeps, and every route that fills a catalogue passes only that.

### 2. What else a version states is read from its manifest

The manifest states all of it, unchanged. Anything that comes to need a column the row does not keep adds it in a new schema version, which costs a rebuild and no migration. It must then be written alike by every route that fills a catalogue.

## Consequences

- At scale M's shape `file_versions` falls from 345 B/version to 155, and the catalogue from 1 373 to 1 183, about 12 GB at scale M. The floor falls from 818 to 636.
- A catalogue at schema 14 is deleted and rebuilt on its first open, as any schema change does.

## What this does not do

- **Meet NFR-PERF-011.** The catalogue is still nearly three times its budget, and its floor more than half as much again.
- **Change what a manifest states.** The name, kind, hash, parent and segment count are all still in it.
- **Change how reuse is judged.** The short-circuit compares the same facts, read from the same row.

## Alternatives considered

- **Keep the whole-file hash and its index for a lookup by content.** Rejected. Nothing makes such a lookup, and a reader that wants a version's hash has it in the manifest.
- **Keep the parent version for a history view.** Rejected. It was wrong on every warm catalogue, so a view reading it would have shown a history publication had not recorded. A reader that needs it can add it once publication writes it as the other routes do.
- **Keep the columns and drop only the hash's index.** Rejected. The index was 80 B/version of the 190 saved, and the columns the other 110, for no reader either way.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Catalogue schema 15 keeps no name, kind, whole-file hash, parent version or segment count for a file version, and no index on its hash. At scale M's shape file_versions falls from 345 B/version to 155 and the catalogue from 1 373 to 1 183. NFR-PERF-011 is still not met. |
| 2026-10 | Accepted | [ADR-0148](0148-a-tree-entry-is-keyed-by-its-folder-and-name.md): a tree entry is keyed by its folder and name, and at scale M's shape the catalogue costs 942 B/version, not 1 183. |

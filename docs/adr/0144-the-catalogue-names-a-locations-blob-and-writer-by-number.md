# ADR-0144 — The catalogue names a location's blob and writer by number

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-011 (amended: re-measured at 2 001 B/version, the locations plane at 662; still not met)
**Related:** [ADR-0010](0010-local-store-separation.md) (the catalogue is a cache, rebuilt on a schema change and never migrated), [ADR-0017](0017-index-entry-supersession.md) (an entry ranks by its generation, then its writer's id, then its sequence), [ADR-0096](0096-format-4-an-index-generation-per-delta.md) (the generation a location ranks by)

**Built:**
- The schema: `Repository.Catalogue/CatalogueSchema` at version 12, where `object_locations` names its blob and its writer by numbers of the catalogue's own, from `location_blobs` and `location_writers`, and the `located` view reads each location by their ids again.
- The catalogue: `Repository.Catalogue/Catalogue` gives a blob or a writer its number as it records a location. Its lookups of one object rank the object's locations themselves, and every other reading goes through `located`.
- The tests:
  - `Repository.Tests/Catalogue/CatalogueSizeTests` — the locations plane costs what its rows cost now, and the whole catalogue with it
  - `Repository.Tests/Catalogue/CatalogueTests` — two writers at one generation rank by their ids, not by which the catalogue met first; the reuse candidate of an object two writers hold names the writer a read resolves to; an object whose only location is in a deleted blob has none; the SQL resolver still agrees with the in-memory one on every randomised input

---

## Context

NFR-PERF-011 asks at most 400 B of catalogue per file version, and the catalogue costs 2 477 at the shape scale M states, five segment references per version ([phase-0 benchmarks §5](../phase-0-benchmarks.md#5-catalogue-size-per-file-version-nfr-perf-011)). The requirement row keeps the target and says replacing it needs a schema decision. The largest plane is `object_locations` and its blob index, at 1 137 B/version.

A location row named its blob and its writer by their ids, sixteen bytes each. The row's key holds the writer, and the blob index holds the blob and, as every index of a table without row ids does, the row's whole key again. So each location stated each id twice. A catalogue names few blobs, since each holds thousands of records, and fewer writers.

## Decision

### 1. A location names its blob and its writer by number

`location_blobs` and `location_writers` give each blob and each writer a number the first time the catalogue records a location naming it. A location holds those numbers where it held the ids, in its row, its key and its blob index.

### 2. Every reader reads the ids again

Precedence ranks by the writer's id, as 07 §3 has it, and never by the number, which is only the order the catalogue met the writers.
- **A lookup of one object** reads each of its locations with its blob's and its writer's ids, and ranks them itself, by generation, then the writer's id, then sequence. These are the lookups a backup and a restore make once per object. Sorting by the writer's id in the query would sort on every lookup, and each lookup is prepared afresh.
- **Every other reading** goes through the `located` view, which joins each location to both ids, so it reads what it read before.

### 3. The cache is rebuilt, not migrated

The schema version moves to 12. A catalogue of an older schema is replaced and rebuilt from the repository, as every change to it is (ADR-0010).

## Consequences

- At scale M's shape the locations plane falls from 1 137 B/version to 662, and the catalogue from 2 477 to 2 001. The floor, a version with no segments, falls from 1 129 to 1 052.
- Scale M is about 20 GB of catalogue, not 25.
- A lookup of one object costs what it did: `ResolveLocation` measured 61 µs against 65 before, at 1/500 of scale M on one machine. Read through the view and sorted in the query, it measured 105.
- The first open of an existing catalogue after this change rebuilds it.

## What this does not do

- **Meet NFR-PERF-011.** The catalogue is still five times its budget, and its floor more than twice. A 32-byte object id is still stated in every location, every dedup row, every file version and every tree entry.
- **Shrink the other planes.** `tree_entries` holds each path three times over, in its row and its two indexes, and is the next largest.
- **Change the format or the wire.** The catalogue is local, and nothing it names leaves it.

## Alternatives considered

- **Give every object id a number too.** Rejected for now. It would reach every table and every query that joins on an object. A blob or a writer is named by thousands of locations, so its number pays for itself at once. An object is named by a handful of rows, one or two in each table that holds it.
- **Order writers by number, as given.** Rejected. Precedence is the writer's id, and the order a catalogue meets writers differs from catalogue to catalogue, so two would resolve the same location differently.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Catalogue schema 12 names a location's blob and writer by number through location_blobs and location_writers, and reads them back through the located view. At scale M's shape the locations plane falls from 1 137 B/version to 662 and the catalogue from 2 477 to 2 001. NFR-PERF-011 is still not met. |
| 2026-10 | Accepted | [ADR-0145](0145-the-catalogue-keeps-no-map-from-content-to-object.md): the catalogue keeps no map from content to object, which nothing read, and at scale M's shape costs 1 607 B/version, not 2 001. |

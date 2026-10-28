# ADR-0145 — The catalogue keeps no map from content to object

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-011 (amended: re-measured at 1 607 B/version; still not met); NFR-PERF-010 (the dedup lookup measured is the reuse candidate's)
**Related:** [ADR-0144](0144-the-catalogue-names-a-locations-blob-and-writer-by-number.md) (the catalogue names a location's blob and writer by number, the record before this one), [ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md) (a segment's object identifier is keyed from its content identifier, and reuse is judged in the dedup trust domains), [ADR-0010](0010-local-store-separation.md) (the catalogue is a cache, rebuilt on a schema change)

**Built:**
- The schema: `Repository.Catalogue/CatalogueSchema` at version 13, with no `segment_dedup`.
- The publication: `Repository/SnapshotPublication` records no segment's content identifier in the catalogue, and `Repository.Catalogue/Catalogue` keeps no call that would.
- The tests:
  - `Repository.Tests/EndToEnd/StaleCatalogueTests` — after a publication, no table the catalogue keeps has a content identifier column
  - `Repository.Tests/Catalogue/CatalogueSizeTests` — the catalogue costs what its three planes cost

---

## Context

Publication wrote a row to `segment_dedup` for every segment it archived, mapping the segment's content identifier to its object. Nothing in the product read it. Reuse asks whether a segment is already held by the object identifier its content identifier is keyed into ([ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md)), and finds the answer in `object_locations`. Within one run, reuse rides the run's own memory. The 2026-08 [pipeline integrity review](../review/2026-08-pipeline-integrity-review.md) found the table dead on the publication path, and pinned it as inert so it could not silently become load-bearing.

It was a fifth of the catalogue: 399 B/version of 2 001 at scale M's shape ([ADR-0144](0144-the-catalogue-names-a-locations-blob-and-writer-by-number.md)). NFR-PERF-011 asks 400 in all.

## Decision

### 1. The table goes

Schema 13 has no `segment_dedup`. Publication records no segment's content identifier in the catalogue, and the catalogue keeps neither the call that wrote the map nor the lookup that read it. A catalogue of an older schema is replaced and rebuilt from the repository (ADR-0010).

### 2. The pin becomes the absence

In place of the case that poisoned the table and showed publication unaffected, a case holds that no table the catalogue keeps has a content identifier column. A second answer to whether a segment is held cannot come back unnoticed.

### 3. The dedup lookup measured is the one the product makes

NFR-PERF-010's figure is the reuse candidate's lookup by object identifier, which a backup makes once per segment, not the lookup of the map nothing called.

## Consequences

- At scale M's shape the catalogue falls from 2 001 B/version to 1 607, about 16 GB at scale M. The floor, a version with no segments, is unchanged at 1 052, since it recorded no segment.
- A publication writes one row fewer for each segment it archives.

## What this does not do

- **Meet NFR-PERF-011.** The catalogue is still four times its budget, and its floor more than twice.
- **Change how reuse is judged.** It was judged by object identifier before, and still is.
- **Change the format or the wire.** The map was local, and never left the catalogue.

## Alternatives considered

- **Keep the table, pinned as inert.** Rejected. It cost a fifth of the catalogue to keep a map whose only use was proving nothing used it.
- **Keep a shorter map, by a prefix of the content identifier.** Rejected. A shorter map is still a map nothing reads.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Catalogue schema 13 has no segment_dedup, publication records no content identifier in the catalogue, and the dedup lookup benchmark measures the reuse candidate's lookup. At scale M's shape the catalogue falls from 2 001 B/version to 1 607. NFR-PERF-011 is still not met. |

# Phase-1 benchmarks — path lookup (NFR-PERF-004)

Reduced-scale measurement of the catalogue v2 lookup paths, run with
`dotnet run --project tests/FallbackPlan.PerformanceTests -c Release -- pathlookup`.
The same caveat discipline as [phase 0](phase-0-benchmarks.md): these are
single-machine numbers at reduced scale, honestly labelled — evidence of
shape, not a guarantee of production latency.

## Setup

100 000 tree entries and file versions in one snapshot — 1/10 of
NFR-PERF-004's stated scale — in a realistic shape (50 files per
directory, 40 subdirectories per parent), seeded through the same
`RecordTreeEntry`/`RecordFileVersion` path live publication uses.
Measured on the CI-class Linux container this repository builds on.

## Results (2026-08, schema v3)

| Operation | Latency | What it is |
|-----------|---------|------------|
| `LookupPath` | ~29 µs/op | One path resolved with its file-version columns — the query the NFR-PERF-003 incremental short-circuit issues per file |
| `ListDirectory` | ~0.28 ms/op (50 entries) | One directory's immediate children — the `ls` query |

At these latencies a 100 000-file incremental backup spends ~3 s on
prior-snapshot lookups; NFR-PERF-004's interactive-browse bound is met
with two orders of magnitude of headroom at this scale.

## What the measurement caught

The first run showed `ListDirectory` at **15 ms/op** — a full scan of
every path in the snapshot per listing. SQLite's planner preferred the
`(snapshot_id, path)` primary key (whose order satisfies the query's
`ORDER BY` for free) over the parent index, because using the parent
index required a per-row table fetch for the selected columns. Schema v3
makes `ix_tree_entries_parent` a covering index
(`snapshot_id, parent, path, entry_kind, object_id`); the planner now
searches it directly — a 54× improvement, and the reason this wave
measures instead of assuming. The schema version bump forces existing
caches to rebuild; a cache is never migrated.

Segmentation-profile dedup numbers live in
[segmentation-benchmark.md](segmentation-benchmark.md); phase-0 engine
numbers in [phase-0-benchmarks.md](phase-0-benchmarks.md).

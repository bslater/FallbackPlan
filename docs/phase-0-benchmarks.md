# Phase 0 benchmarks — reduced scale, honestly labelled

**Status:** measured · **Wave:** F4 · **Requirements:** NFR-PERF-001, -002, -004, -010, -011, -012 (shape); the full NFR-PERF-001..015 set remains open at scale
**Harness:** [`tests/FallbackPlan.PerformanceTests/`](../tests/FallbackPlan.PerformanceTests/)

---

## What this document is — and is not

These are the first published numbers for the phase-0 engine, produced by
BenchmarkDotNet 0.15.8's **ShortRun** job (3 warmup + 3 measurement
iterations) and a non-BenchmarkDotNet memory proof, on a machine well below
the reference machine, at scales far below scale M. They
exist so that wave F4 publishes *measurements* instead of promises, and so
that later revisions of the targets have a recorded starting point
([Q7](open-questions.md#q7--performance-targets): revise targets with the
revision recorded, never silently).

They are **not** verification of the NFR targets:

- **The scales are reduced.** NFR-PERF-004/010 state p99 targets at scale M
  (10 M file versions, 50 M segment references) and scale L. The catalogue
  here holds 100 000 locations and 100 000 dedup rows — **1/500 of scale M**.
  A B-tree lookup degrades roughly logarithmically, so the reduced-scale
  numbers are informative, but *the targets at scale M and L are not verified
  by this document*.
- **ShortRun is a coarse job.** Three warmup and three measurement iterations
  per benchmark; error bars are wide. The numbers below are order-of-magnitude
  evidence, not publication-grade statistics.
- **The machine is below reference.** The [reference
  machine](requirements/non-functional.md#reference-machine) is 8 physical
  cores with AES-NI, 32 GB RAM, NVMe at ≥ 2 GB/s. These runs used a
  4-core Intel Xeon @ 2.80 GHz cloud container, 15 GB RAM, with
  container-grade storage. Absolute throughput here is expected to be well
  under reference-machine throughput.
- **The pipeline is single-streamed.** Phase 0 archives one stream with no
  concurrency; NFR-PERF-002's saturation target assumes the concurrency that
  later phases add.

## How to reproduce

```bash
cd tests/FallbackPlan.PerformanceTests
dotnet run -c Release -- --filter '*' --job short   # BenchmarkDotNet suite
dotnet run -c Release -- membound 3                 # NFR-PERF-001 proof, 3 GiB
dotnet run -c Release -- catalogue-size 100000      # NFR-PERF-011, by plane
dotnet run -c Release -- rebuild-rate 4000          # NFR-PERF-012, timed
```

`BenchmarkDotNet.Artifacts/` output is gitignored; the numbers in this file
are the durable record, per the Q7 rule.

---

## 1. Memory bound (NFR-PERF-001 shape, exit criterion 1)

`MemoryBoundProof` streams a synthetic pseudorandom input — generated on the
fly, never materialised — through the full `FileArchiver` pipeline (cdc-v1
segmentation, SHA-256 content ids, zstd attempt, AES-256-GCM, blob packing
with spool checkpoints) into a store that discards every byte. Managed-heap
live-set samples are taken with forced collections every 5 s; the raw heap
and process working set are sampled every 250 ms.

| Input | Elapsed | Throughput | Segments | Blobs | Peak live set | Peak raw heap | Peak working set | Verdict |
|---|---|---|---|---|---|---|---|---|
| 1 GiB | 37.6 s | 27 MiB/s | 813 | 16 | **89.1 MiB** | 228.7 MiB | 270.0 MiB | PASS |
| 3 GiB | 111.3 s | 28 MiB/s | 2 437 | 48 | **103.3 MiB** | 253.9 MiB | 297.7 MiB | PASS |

The claim this proves is the *shape* of NFR-PERF-001: tripling the input
moved the peak live set by ~14 MiB (sampling noise and GC timing), not by
gigabytes — memory is a function of segment size and blob buffers, not input
length. The raw-heap number is an allocation-rate artefact (garbage awaiting
collection), which is why the bound is asserted on the live set. The proof
fails its process exit code if the live set exceeds 256 MiB — the figure
NFR-PERF-001 allows a 2 TiB single file to add over idle.

**Not verified here:** the 2 TiB single-file case itself, scale-L RSS (≤ 1 GB
with default profiles), and behaviour under concurrency. The 3 GiB run is the
largest this environment affords in reasonable wall-clock; the streaming
architecture gives no reason to expect a different curve at 2 TiB, but that
is an expectation, not a measurement.

## 2. Pipeline throughput (`PipelineBenchmarks`)

The full record path — segment, hash, compress-or-skip, encrypt, pack, seal —
through `FileArchiver` to the discarding store, over 32 MiB of incompressible
pseudorandom input. Incompressible input is the *worst* case for the
compression threshold (the zstd attempt is paid and then discarded).

| Method | Input | Mean | StdDev | Throughput (mean) | Allocated/op |
|---|---|---:|---:|---:|---:|
| `ArchiveFixedV1` | 32 MiB | 629.6 ms | 38.7 ms | ≈ 51 MiB/s | 32.84 MB |
| `ArchiveCdcV1` | 32 MiB | 863.2 ms | 37.1 ms | ≈ 37 MiB/s | 40.83 MB |

cdc-v1 costs ~1.37× fixed-v1 end to end on this input — the rolling window
plus smaller average segments. The allocated-per-op figure is dominated by
the input copy the benchmark itself makes and the pipeline's transient
buffers; the *retention* story is §1's live-set numbers, not this column.
These throughputs are single-streamed on a 4-core container without
reference-machine storage; they say nothing yet about NFR-PERF-002's
saturation target on reference hardware.

## 3. Raw segmentation (`SegmentationBenchmarks`)

fixed-v1 against cdc-v1 over the same 64 MiB, measuring exactly the price of
the rolling Rabin window (ADR-0023). CPU cost only — the *deduplication*
comparison the freeze gate turns on is published separately in
[`segmentation-benchmark.md`](segmentation-benchmark.md).

| Method | Input | Mean | StdDev | Throughput (mean) | Allocated/op |
|---|---|---:|---:|---:|---:|
| `FixedV1` | 64 MiB | 9.82 ms | 0.36 ms | ≈ 6.4 GiB/s | 256 B |
| `CdcV1` | 64 MiB | 434.1 ms | 19.3 ms | ≈ 148 MiB/s | 8.0 MB |

fixed-v1 is a memcpy-rate cursor; cdc-v1's ~148 MiB/s is the table-driven
Rabin roll, single-threaded, and is the segmentation profile's intrinsic
price (its 8 MB/op allocation is the max-size carry buffer). At ~148 MiB/s
the roller is not the pipeline bottleneck on this machine (§2's whole
pipeline runs at ~37 MiB/s), but it would become one near reference-machine
storage rates — worth re-measuring there before drawing conclusions.

## 4. Catalogue lookups (`CatalogueBenchmarks` — NFR-PERF-004 / -010 at 1/500 scale)

A catalogue seeded through the engine's own `ApplyDelta` path with 100 000
object locations (100 deltas of 1 000 entries) and 100 000 dedup rows,
probed with the exact SQL the engine runs (`ResolveLocation` — the
precedence-honouring path-resolution query; `LookupByContent` — the dedup
lookup).

| Method | Rows | Mean | StdDev | Allocated/op |
|---|---|---:|---:|---:|
| `ResolveLocation` | 100 000 | 62.2 µs | 3.6 µs | 4.36 KB |
| `DedupLookup` | 100 000 | 13.9 µs | 1.0 µs | 1.10 KB |

Both sit two orders of magnitude under the scale-M p99 targets — but these
are *means at 1/500 scale*, not p99s at scale M. SQLite B-tree depth grows
logarithmically, so the margin is encouraging rather than conclusive: a
500× larger tree adds a few levels, and p99 (cold pages, checkpoint
interference) is the number the target actually names.

Targets for context, **not** verified at this scale: NFR-PERF-004 p99
≤ 10 ms at scale M; NFR-PERF-010 p99 ≤ 1 ms at scale M.


## 5. Catalogue size per file version (NFR-PERF-011)

`CatalogueSizeBenchmark` seeds through the catalogue's own
`RecordFileVersion`, `RecordTreeEntry`, `ApplyDelta` and
`RecordSegmentDedup` calls — so what is measured is the schema's cost, not a
model's guess at it — at the shape the [reference
scales](requirements/non-functional.md#reference-scales) themselves state:
10 M file versions against 50 M segment references is **five references per
version**, each version named by one path in one snapshot.

| Planes held | B/version |
|---|---:|
| `file_versions` and its two indexes | 375 |
| `tree_entries` and its two indexes | 642 |
| `object_locations` and `ix_locations_blob` | 1 174 |
| `segment_dedup` | 427 |
| **all four, at scale M's shape** | **2 520** |
| **the floor — no segments at all** | **1 174** |

**The 400 B/version budget is missed by 6.3×**, and the shortfall is
structural rather than a corpus's bad luck: the *floor* — one file version,
the one path that makes it reachable, and its own manifest's location, with
no segment anywhere — is already 1 174 B/version. A version no snapshot
names is not restorable and is not one of scale M's ten million, so nothing
below that floor is a catalogue anybody could restore from.

At this ratio scale **M** is ~25 GB and scale **L** ~250 GB, against the
4 GB and 40 GB the requirement's own sentence works out to — and that
sentence ends *"a number that must fit on a consumer laptop"*.

**The ratio is flat**, which is what lets a small measurement speak for a
large one: 2 537 B/version at 2 000 versions, 2 486 at 40 000 — within 1.2%
over a 20× range, and *falling* as the B-trees fill, so the cheap
measurement is the conservative one. That is asserted and not merely
observed, by `Repository.Tests/CatalogueSizeTests`.

**Where the bytes are** decides what could be done about it, which is why
the total is broken out. The two segment planes are 62% of the cost, at
~256 B per segment reference — exactly the term
[the segmentation benchmark](segmentation-benchmark.md) named in passing as
*"the real cost of smaller segments … which quadruples between 1 MiB and
256 KiB targets"*. That was written as a caveat on a segmentation decision;
it turns out to be the main term.

Every segment reference here is a **distinct** object, which is the no-dedup
upper bound on `object_locations`; a corpus with cross-version dedup holds
fewer rows there for the same reference count. The floor is unaffected by
that and is the honest lower bound.

---

## 6. Forensic rebuild rate (NFR-PERF-012)

`RebuildRateBenchmark` publishes a tree, deletes its **whole index plane** —
the premise a forensic rebuild runs under — and times the rebuild from
recovery footers alone.

| Files | Blobs | Records | Elapsed | records/s | blobs/s | reads/record |
|---:|---:|---:|---:|---:|---:|---:|
| 2 000 | 2 | 4 035 | 1.30 s | 3 104 | 1.5 | 0.51 |
| 4 000 | 2 | 8 066 | 2.37 s | 3 402 | 0.8 | 0.51 |

**The blob is the wrong unit, and the table shows why.** A blob holds up to
65 536 records, so blobs per second varies with how full the blobs are —
4 033 records per blob here. Reported in blobs/s this pass looks like a
catastrophic 0.8 against a ≥ 500 target while doing 3 402 records/s. A
rebuild's work is per record.

**And the requirement's two clauses disagree by about two orders of
magnitude.** Scale **M** is roughly 24 000 blobs at the default 128 MiB blob
size; at ≥ 500 blobs/s that is under a minute, not the ≤ 2 hours the same
row states. One of the two numbers was written about a different blob size,
and the row cannot be marked met until that is settled.

**The measurement found a defect on the way.** The targeted walk opened a
fresh `BlobReader` per record — three ranged reads, a footer decrypt and a
record-table decode each time, then a linear scan of that table — so an
added file cost 4.4 ranged reads where it now costs 1.1. Holding one blob
across the walk fixed it; `Repository.Tests/ForensicRebuildCostTests` is
what keeps it fixed, and it asserts the **work** rather than the rate,
because a rate target is unreachable on any disk if the scan re-reads.

---

## Q7 ledger — target revisions arising from these measurements

**Two targets are now contradicted, and neither is revised here** — which is
the honest answer rather than a dodge. A revision needs a replacement number,
and in both cases the replacement follows from a decision this round does not
make. What the round does is stop them being unmeasured, and record what
forced the question.

| Date | Target | Old | New | Evidence |
|------|--------|-----|-----|----------|
| 2026-09 | NFR-PERF-011 | ≤ 400 B/file version at scale M | **contradicted, not yet replaced** — measured 2 520 B, with a structural floor of ~1 175 B | §5. The floor is reached with no segment at all, so no corpus brings the schema inside 400 B. A replacement number is a schema or segmentation-target decision, not a measurement. |
| 2026-09 | NFR-PERF-012 | ≥ 500 blobs/s **and** scale M in ≤ 2 hours | **internally inconsistent, and in the wrong unit** — the two clauses differ by ~100×, and a rebuild's work is per record | §6. Scale M is ~24 000 blobs at the default blob size, so ≥ 500 blobs/s is under a minute. Settling the unit is what the row needs before a number can be set. |

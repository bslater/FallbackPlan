# Phase 0 benchmarks — reduced scale, honestly labelled

**Status:** measured · **Wave:** F4 · **Requirements:** NFR-PERF-001, -002, -004, -010 (shape); the full NFR-PERF-001..015 set remains open at scale
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
the rolling Rabin window (ADR-0023).

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

---

## Q7 ledger — target revisions arising from these measurements

No target is revised by this round. The measurements are consistent with the
targets being *plausible* at scale on reference hardware, and nothing
measured here contradicts one. When a later, full-scale run revises a target,
the revision belongs in this section with the number that forced it.

| Date | Target | Old | New | Evidence |
|------|--------|-----|-----|----------|
| — | — | — | — | none yet |

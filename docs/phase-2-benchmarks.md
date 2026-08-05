# Phase 2 benchmarks — throughput, attributed

**Status:** first measurement · **Requirement:** NFR-PERF-007 · **Decision:** [ADR-0029 §6](adr/0029-pipeline-and-service-concurrency.md)

---

ADR-0029 §6 makes measurement step 1 and says why: the pipeline is doing about
128 `fsync`s and 128 checkpoint sidecar rewrites per blob, and *"parallelising an
fsync-bound pipeline mostly multiplies fsyncs"*. So this is what the throughput
work is aimed at, taken before any of it.

```
dotnet run --project tests/FallbackPlan.PerformanceTests -c Release -- throughput 64
```

## First run

Container, 4 logical cores, 32 MiB per configuration. **Not the reference
machine** — NFR-PERF-007's ≥400 MB/s target is stated against that, and this is
not it. These numbers are for comparing configurations against each other, which
is what attribution needs.

| Configuration | MiB/s |
|---|---|
| fixed-v1, concurrency 1 | 60.6 |
| fixed-v1, concurrency 2 | 61.3 |
| fixed-v1, concurrency 4 | 73.8 |
| fixed-v1, concurrency 8 | 82.1 |
| cdc-v1, concurrency 1 | 56.8 |
| cdc-v1, concurrency 4 | 59.1 |
| fixed-v1, no compression | 87.2 |

## What this does and does not say

**The concurrency rows mean nothing yet, and the table is left in so that stays
visible.** `Concurrency` exists as a validated setting (ADR-0029 §3) and the
staged pipeline that would consume it does not, so every row runs the same
sequential code. The spread across those rows — 60.6 to 82.1 — is therefore
*measurement noise plus warm cache*, not a speed-up, and it is a useful
calibration: any future concurrency result inside that spread has proven nothing.

**Compression is worth about 30%** on this data (87.2 against 60.6). That is a
real attribution and it bounds what removing the compressor could ever buy — it
is not where the order of magnitude went.

**cdc-v1 costs about 6%** against fixed-v1, which matches the phase-0
segmentation benchmark's finding that the rolling Rabin window is a modest tax.
Segmentation is not the constraint: raw `fixed-v1` segmentation runs at
**6.4 GiB/s** in `SegmentationBenchmarks`, two orders of magnitude above the
whole pipeline.

So the gap is not in any stage this sweep can vary. It is in the serial costs
ADR-0029 §6 step 2 names — the per-record `fsync` and checkpoint rewrite, and the
blob upload awaited inline in the archive loop — and that is where the next work
goes. One of them is already gone: `RecordCipher` now takes a cached `AesGcm`
per blob rather than constructing one per record, which removes an AES key
schedule per record. It does not show above the noise here, which is itself the
result: it was worth doing and it was not the problem.

## Re-run this when

- the spool checkpoint stops being rewritten per record;
- upload leaves the archive loop;
- the staged pipeline lands and the concurrency rows start to mean something.

Each of those should move a number in this table, and if it does not, that is the
finding.

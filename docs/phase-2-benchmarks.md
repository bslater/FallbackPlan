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

## Second run — after upload left the archive loop

Same machine and inputs, after ADR-0029 §2 landed.

| Configuration | before | after |
|---|---|---|
| fixed-v1, concurrency 1 | 60.6 | 60.1 |
| fixed-v1, concurrency 2 | 61.3 | 68.3 |
| fixed-v1, no compression | 87.2 | 71.6 |

**Against `NullObjectStore` the change is a wash, and that is the expected
result.** That store consumes and discards, so a PUT costs nothing and cannot
stall a loop; removing a stall that was not there buys nothing.

### The rows that can see it

`SlowObjectStore` imposes a fixed latency per PUT, and the `stalled` column
reports the total it imposed. When that exceeds what the run could have absorbed
serially, the uploads provably overlapped the archive loop — they could not
otherwise have fitted.

| Configuration | seconds | MiB/s | stalled |
|---|---|---|---|
| fixed-v1, concurrency 1 (free store) | 0.63 | 50.5 | — |
| slow store 200 ms, concurrency 1 | 0.67 | 48.0 | **0.40** |
| slow store 200 ms, concurrency 2 | 0.69 | 46.5 | **0.40** |
| slow store 200 ms, concurrency 4 | 0.65 | 49.3 | **0.40** |

0.40 s of store latency lands inside a 0.67 s run. Inline — the behaviour this
replaced — the same work would have taken about 0.63 + 0.40 = **1.03 s**, and
throughput would have fallen from 50.5 to roughly 31 MiB/s. It did not fall: it
went to 48.0, within the run-to-run noise of the free store. The stall is gone,
and the arithmetic rather than the assertion says so.

The effect scales with how slow the destination is and how many blobs a job
seals, which is why the claim was always about remote destinations and never
about local NVMe.

What the run *did* catch is worth more than a number: the first version sized
the hand-off channel at exactly `Concurrency`, so at 1 the archive loop and the
single upload worker ran in lock-step and throughput fell to **36.2 MiB/s** —
materially *worse* than the inline upload it replaced. Capacity is now
`Concurrency + 1`, which is what makes a hand-off a hand-off rather than a
rendezvous. The memory bound stays statable; it is bounded by a number the
setting still names.

## What this does and does not say

**The concurrency rows still mean very little, and the table is left in so that
stays visible.** `Concurrency` now sizes the upload workers (ADR-0029 §2), but
the staged pipeline of §1 — the part that would let read, hash and compress run
concurrently — does not exist, so the stages that dominate this measurement are
still sequential at every setting. The spread across those rows is therefore
mostly *measurement noise plus warm cache*, and it is a useful calibration: any
future concurrency result inside that spread has proven nothing.

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
- the staged pipeline lands and the concurrency rows start to mean something.

Each of those should move a number in this table, and if it does not, that is the
finding.

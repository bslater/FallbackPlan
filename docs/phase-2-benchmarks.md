# Phase 2 benchmarks — throughput, attributed

**Status:** F1 and G2 measured on a container; the reference-machine run is still owed · **Requirement:** NFR-PERF-007 · **Decision:** [ADR-0029 §6](adr/0029-pipeline-and-service-concurrency.md)

---

ADR-0029 §6 makes measurement step 1 and says why: the pipeline is doing about
128 `fsync`s and 128 checkpoint sidecar rewrites per blob, and *"parallelising an
fsync-bound pipeline mostly multiplies fsyncs"*. So this is what the throughput
work is aimed at, taken before any of it.

```
dotnet run --project tests/FallbackPlan.PerformanceTests -c Release -- throughput 32
```

The argument is MiB per configuration. Every table on this page was taken at 32;
the command here said 64 until the third run noticed the tables could not have
been produced by it.

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

## Third run — after F1 removed the per-record `fsync` and sidecar rewrite

**A different container from the two runs above, so this section stands alone.**
Its absolute numbers are roughly twice theirs — 127 MiB/s where the first run saw
60.6 — because the machine is faster, not because anything got twice as quick.
Read the before and after columns below against each other and against nothing
else on this page.

Method, because the result depends on it. Both sides were built from the same
tree: `786324f` (the commit before F1) against the branch tip, with
`git diff` confirming the benchmark harness byte-identical between them, so the
comparison measures the product rather than the instrument. **Ten sweeps per
side** — five run before-then-after, five run after-then-before. The second
ordering is the point: alternating sides guards against drift over time but not
against position within a round, and running the baseline first every time would
let a warm page cache read as an improvement. Medians of all ten, with the full
range in brackets.

| Configuration | before | after | change |
|---|---|---|---|
| fixed-v1, concurrency 1 | 127.0 (111–135) | 136.2 (99–141) | **+7%** |
| fixed-v1, concurrency 2 | 132.9 (88–142) | 140.1 (131–144) | **+5%** |
| fixed-v1, concurrency 4 | 159.2 (111–174) | 175.7 (166–190) | **+10%** |
| fixed-v1, concurrency 8 | 165.2 (113–176) | 192.1 (178–195) | **+16%** |
| cdc-v1, concurrency 1 | 95.6 (76–102) | 102.3 (97–107) | **+7%** |
| cdc-v1, concurrency 4 | 100.7 (89–103) | 105.0 (96–107) | **+4%** |
| fixed-v1, no compression | 202.9 (158–215) | 225.6 (213–246) | **+11%** |
| slow store 200 ms, concurrency 1 | 64.5 (59–65) | 65.6 (64–66) | +2% |
| slow store 200 ms, concurrency 2 | 83.4 (71–85) | 85.8 (73–88) | +3% |
| slow store 200 ms, concurrency 4 | 78.3 (71–86) | 85.5 (69–89) | +9% |

**The result is the unanimity, not any single row.** Look at the brackets: the
before column swings as much as 40% of its own median, which is wider than most
of the changes beside it, so no individual percentage here is safe on its own.
What is not noise is that **all ten configurations improved, in both run
orders** — twenty comparisons, twenty the same direction. Noise does not do that.

The largest gains sit at high concurrency (+16% at 8) and with compression off
(+11%), which is the shape the change predicts: the more the pipeline could
otherwise be doing per second, the more a synchronous `fsync` and a whole-file
sidecar rewrite per record were in the way.

**The three slow-store rows say almost nothing, as expected**, and are kept
because a row that cannot show an effect is worth seeing. At 200 ms per PUT the
store dominates, so removing microseconds of spool work moves +2% and +3%; the
+9% at concurrency 4 is inside that row's own spread.

### The quieter finding, which may be the better one

The before side is not just slower, it is **far less predictable**. Spread across
ten runs, as a percentage of the median:

| Configuration | before | after |
|---|---|---|
| fixed-v1, concurrency 2 | 40.6% | **9.4%** |
| fixed-v1, concurrency 4 | 40.2% | **14.2%** |
| fixed-v1, concurrency 8 | 38.1% | **9.2%** |
| cdc-v1, concurrency 1 | 27.3% | **10.1%** |
| fixed-v1, no compression | 28.2% | **14.3%** |

Seven of the ten configurations tightened. That is what removing an `fsync` from
a hot loop should do and is hard to explain any other way: `fsync` latency is a
function of disk scheduling and journal commits rather than of the work being
done, so a pipeline issuing thousands of them inherits the disk's variance.
A backup whose throughput is predictable is worth something on its own, and this
was not the property the change was aimed at.

One caveat kept in view: the sweep pins `TargetSizeBytes` to 8 MiB, so a blob
holds far fewer records than the "~128 `fsync`s per 128 MiB blob" figure quoted
at the top of this page. The rate per mebibyte is what is unchanged, and what
these rows see.

**Still a container, still not the reference machine.** NFR-PERF-007's ≥400 MB/s
target is stated against that machine and remains untested.

## Fourth run — after G2 staged the pipeline

Same container as the third run, same method: both sides built from one tree with
`ThroughputBenchmarks` confirmed byte-identical between them, ten sweeps per side,
five run before-then-after and five after-then-before. Medians of all ten, full
range in brackets.

| Configuration | before | after | change |
|---|---|---|---|
| fixed-v1, concurrency 1 | 140.1 (117–142) | 320.9 (298–357) | **+129%** |
| fixed-v1, concurrency 2 | 139.4 (121–144) | 360.8 (341–415) | **+159%** |
| fixed-v1, concurrency 4 | 166.9 (156–186) | 356.9 (310–419) | **+114%** |
| fixed-v1, concurrency 8 | 186.1 (168–200) | 459.8 (321–535) | **+147%** |
| cdc-v1, concurrency 1 | 102.2 (96–104) | 137.7 (133–144) | **+35%** |
| cdc-v1, concurrency 4 | 104.2 (98–106) | 142.5 (127–150) | **+37%** |
| fixed-v1, no compression | 232.9 (145–242) | 343.6 (328–377) | **+48%** |
| slow store 200 ms, concurrency 1 | 65.5 (64–66) | 72.1 (69–73) | +10% |
| slow store 200 ms, concurrency 2 | 87.1 (84–88) | 116.6 (98–120) | **+34%** |
| slow store 200 ms, concurrency 4 | 87.3 (82–90) | 114.8 (110–121) | **+32%** |

**Unlike the F1 measurement, this one does not need the unanimity argument.**
There the per-row change sat inside the run-to-run spread and only the direction
across all rows was safe to report. Here the before and after ranges do not
overlap on any row — fixed-v1 at concurrency 2 ranges 121–144 before and 341–415
after — so each figure stands on its own. Ten of ten configurations improved, in
both run orders, as before.

**fixed-v1 throughput roughly doubled.** Two things did that: compression left
the serial path, and so did the per-segment SHA-256. That second one is the
"second hash" ADR-0029 §6 step 2 named and F1 did not reach — the pipeline was
running two SHA-256 passes over every byte on one thread, the whole-file hash and
the content id, and only the whole-file hash has to be there.

**cdc-v1 gains much less (+35% against fixed-v1's +129%)**, and the reason is
structural rather than incidental: `CdcSegmentReader`'s rolling window chains
across segment boundaries, so the reader cannot be parallelised at all and stays
the whole serial floor. Segmentation was measured at 6.4 GiB/s in isolation, so
this is not the Rabin scan being slow — it is that under cdc-v1 more of the
remaining work is on the one thread that cannot move.

**Raising the setting past 2 buys little on this machine**, which is the honest
reading of 360.8 at 2 against 356.9 at 4. Four logical cores, with a
single-threaded reader and a single-threaded barrier both on them, is not many
spare threads to give the concurrent stage. The default of 2 looks well chosen
rather than lucky.

### One thing the number hides

**Concurrency 1 improved by 129%, and it is not a serial configuration any
more.** The channel holds `Concurrency + 1` items, so even at 1 one segment is
hashed and compressed while the previous is appended. The ordering barrier is
still one thread and everything it protects still holds — but "1" no longer means
"one thing at a time", and NFR-PERF-013's CPU cap is stated against a 4-core
laptop. That should be measured rather than inferred from the setting's name.

**Still a container, still not the reference machine.** NFR-PERF-007's ≥400 MB/s
is stated against that machine. The fixed-v1 rows here now pass 400 *MiB/s* at
concurrency 8 on some sweeps, which is a different unit on a different machine
and is not the same claim.

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

- ~~the spool checkpoint stops being rewritten per record~~ — **done, and
  measured: see the third run above.** Note that the first and second runs'
  tables were taken against the per-record `fsync` and sidecar rewrite, which no
  longer exist; they are kept as the record of what was measured when, not as a
  current description of the engine.
- ~~the staged pipeline lands and the concurrency rows start to mean something~~ —
  **done, and measured: see the fourth run.** The rows mean something now, and
  what they mean is that the setting matters much less than staging the pipeline
  did: doubling throughput came from moving work off the serial thread, not from
  raising the number.
- **the reference machine is available.** Everything on this page is container
  measurement, useful for comparing configurations and versions against each
  other and useless against NFR-PERF-007's ≥400 MB/s, which has still never been
  tested.

Each of those should move a number in this table, and if it does not, that is the
finding.

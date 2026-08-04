# Segmentation benchmark — fixed-v1 vs cdc-v1 (freeze-gate item 1)

**Status:** first published round (synthetic corpus) · **Gate:** [format v1 freeze, item 1](roadmap.md#format-v1-freeze-gate) · **Decides:** the default segmentation profile, while it is still free to change ([architecture 02 §3.3](architecture/02-repository-format.md#33-the-freeze-gate))

---

## Question under test

`fixed-v1` (positional 1 MiB segments) versus `cdc-v1` (ADR-0023 Rabin
content-defined boundaries): deduplication ratio, storage growth per backup,
metadata overhead, and CPU cost, across the edit patterns backup workloads
actually produce. The phase-0 numbers in
[`phase-0-benchmarks.md`](phase-0-benchmarks.md) measured CPU only; this run
measures what the freeze-gate decision actually turns on.

## Method

`DedupCorpusBenchmark` (`tests/FallbackPlan.PerformanceTests`, run with
`dotnet run -c Release -- dedup`) generates five deterministic version
series — eleven versions each, fixed seeds, numbers reproduce exactly — and
segments every version under three profiles, counting a byte as *stored*
only if no prior segment of the series already carries its content id
(SHA-256 of plaintext — the same identity the engine's `segment_dedup`
table uses). Compression and encryption sit downstream of that identity and
are excluded; the run isolates segmentation.

| Scenario | Models | Shape |
|---|---|---|
| `log-append` | logs, mail spools | 32 MiB + 2 MiB appended per version |
| `doc-insert` | office documents, mail folders | 3 × 64 KiB insertions + 16 KiB deletion + 3 in-place edits per version — content shifts |
| `db-inplace` | databases, VM images | 200 random 4 KiB pages overwritten in place per version — no shift |
| `rewrite-shift` | pure-shift saves | 5 × 4 KiB insertions per version, nothing else changes |
| `media-unique` | photos/video (control) | fresh 32 MiB every version — no dedup exists to find |

Profiles: `fixed-v1` at the default 1 MiB; `cdc-v1` at the default
(target 1 MiB, min 256 KiB, max 8 MiB); `cdc-v1` at target 256 KiB
(min 64 KiB, max 2 MiB) as the size-sensitivity probe.

## Results

Machine: 4-core Intel Xeon @ 2.80 GHz container (sub-reference), .NET 10,
single-threaded. *Dedup ratio* = stored ⁄ logical over all eleven versions
(lower is better); *incremental* = the same over versions 2–11, i.e. the
steady-state cost of each backup after the first. *Overhead est.* =
segments × ~240 B (record header + tag + record-table entry + index entry +
manifest reference).

| Scenario | Profile | Logical | Stored | Dedup ratio | Incremental | Segments | Overhead est. | MiB/s |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| log-append | fixed-v1 1MiB | 462.0 MiB | 52.0 MiB | 11.3 % | 4.7 % | 462 | 0.1 MiB | 347 |
| log-append | cdc-v1 1MiB | 462.0 MiB | 59.9 MiB | 13.0 % | 6.5 % | 428 | 0.1 MiB | 119 |
| log-append | cdc-v1 256KiB | 462.0 MiB | 54.1 MiB | 11.7 % | 5.1 % | 1659 | 0.4 MiB | 128 |
| doc-insert | fixed-v1 1MiB | 713.5 MiB | 607.5 MiB | 85.1 % | 83.7 % | 719 | 0.2 MiB | 363 |
| doc-insert | cdc-v1 1MiB | 713.5 MiB | 201.5 MiB | 28.2 % | 21.2 % | 531 | 0.1 MiB | 107 |
| doc-insert | cdc-v1 256KiB | 713.5 MiB | 101.3 MiB | 14.2 % | 5.7 % | 2288 | 0.5 MiB | 127 |
| db-inplace | fixed-v1 1MiB | 1408.0 MiB | 1154.0 MiB | 82.0 % | 80.2 % | 1408 | 0.3 MiB | 347 |
| db-inplace | cdc-v1 1MiB | 1408.0 MiB | 1229.1 MiB | 87.3 % | 86.0 % | 1129 | 0.3 MiB | 104 |
| db-inplace | cdc-v1 256KiB | 1408.0 MiB | 753.1 MiB | 53.5 % | 48.8 % | 4811 | 1.1 MiB | 131 |
| rewrite-shift | fixed-v1 1MiB | 705.1 MiB | 530.1 MiB | 75.2 % | 72.7 % | 714 | 0.2 MiB | 364 |
| rewrite-shift | cdc-v1 1MiB | 705.1 MiB | 139.1 MiB | 19.7 % | 11.7 % | 594 | 0.1 MiB | 94 |
| rewrite-shift | cdc-v1 256KiB | 705.1 MiB | 93.1 MiB | 13.2 % | 4.5 % | 2167 | 0.5 MiB | 124 |
| media-unique | fixed-v1 1MiB | 352.0 MiB | 352.0 MiB | 100.0 % | 100.0 % | 352 | 0.1 MiB | 353 |
| media-unique | cdc-v1 1MiB | 352.0 MiB | 352.0 MiB | 100.0 % | 100.0 % | 292 | 0.1 MiB | 81 |
| media-unique | cdc-v1 256KiB | 352.0 MiB | 352.0 MiB | 100.0 % | 100.0 % | 1154 | 0.3 MiB | 121 |

## What the numbers say

1. **Shifted content is the decisive case, and cdc-v1 wins it by 4–6×.**
   One inserted byte re-keys every downstream fixed segment; content-defined
   boundaries resynchronise within one segment (the ADR-0023 §3.2 property).
   `doc-insert` steady state: 83.7 % of logical bytes re-stored per backup
   under fixed, 5.7 % under cdc-256KiB — a **15× reduction** on the workload
   that describes most user documents.
2. **Appends are a wash.** Fixed handles append-only files perfectly by
   construction; cdc is within two points of it. Nothing is lost by choosing
   cdc for this class.
3. **In-place page writes favour small segments, not fixed boundaries.**
   `db-inplace` is fixed's best case, yet cdc-256KiB still halves its
   stored bytes (53.5 % vs 82.0 %): the win comes from granularity, and cdc
   at the *default* 1 MiB target loses here (87.3 %) because its average
   segment — inflated by the 8 MiB max — makes each dirty 4 KiB page drag
   more collateral. Target size, not boundary style, is the lever for this
   class.
4. **Metadata overhead does not change the decision.** The worst case in the
   table (cdc-256KiB on `db-inplace`, 4 811 segments) costs ~1.1 MiB of
   format overhead against ~400 MiB of storage saved: a 1:350 exchange.
   The real cost of smaller segments is catalogue/index row count
   (NFR-PERF-011's 400 B/version budget), which quadruples between 1 MiB
   and 256 KiB targets — that is the trade a real-corpus run must price.
5. **CPU cost is the price of admission.** The Rabin roll runs ~100–130 MiB/s
   here against fixed's ~350 MiB/s (both single-threaded, sub-reference
   hardware). Since the whole encrypt-and-pack pipeline currently runs at
   ~37 MiB/s on this machine, segmentation is not the bottleneck today — but
   on reference hardware with fast storage it could be, and cdc's cost
   scales with *read* bytes while its saving scales with *written* bytes: a
   favourable exchange for any workload with dedup to find, a pure loss for
   `media-unique`.

## Recommendation (not yet a decision)

**cdc-v1 should become the default segmentation profile at the freeze
gate.** It is decisively better on shifted-content workloads, no worse on
appends, and its one weak class (`db-inplace` at the default target) is a
target-size problem, not a boundary-style problem. Two things must be
settled by a follow-up run before the gate passes, on reference hardware
with a real (non-synthetic) corpus:

1. **The default cdc target size.** 1 MiB vs 512 KiB vs 256 KiB — trading
   catalogue growth (NFR-PERF-011) against the in-place-write class. The
   synthetic numbers suggest the sweet spot is below 1 MiB.
2. **Whether per-file-class profile selection is worth its complexity** —
   e.g. media files (no dedup available) could skip the Rabin cost entirely
   under fixed-v1. The format already carries the profile per manifest, so
   this is policy machinery, not format change.

The default in code remains `fixed-v1` until the gate passes — the roadmap
places the flip *at the gate*, and this document is the evidence half, not
the decision.

## Honest limits of this round

- **Synthetic corpus.** Edit patterns are modelled, not sampled from real
  archives. Pseudorandom content is incompressible and gives the Rabin
  fingerprint uniform statistics; real text may shift boundary densities
  somewhat, though the mask-based cut rule is designed to be
  content-distribution-tolerant.
- **Single-file series.** Cross-file dedup (copies, renames) is not
  measured; it can only favour cdc further, since fixed boundaries also
  break on any leading-offset difference between copies.
- **No whole-file short-circuit.** The engine skips unchanged files by
  identity/mtime before segmenting; this run measures changed files only,
  which is the honest basis for comparing segmenters.
- **Sub-reference machine, single thread** — CPU numbers are indicative,
  not gate-quality; the ratios (which do not depend on hardware) are the
  load-bearing result.

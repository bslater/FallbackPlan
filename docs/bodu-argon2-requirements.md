# Requirements: a faster Argon2id in Bodu

**Status: Answered upstream, and adopted** — raised against
`Bodu.Security.Cryptography` **1.0.0**, the package FallbackPlan makes every
passphrase- and password-based derivation through. **1.1.0** answered it, and
FallbackPlan consumes it since 2026-09-28, ahead of the other five Bodu
packages ([ADR-0021](adr/0021-consume-bodu-via-committed-package-feed.md)
Amendment 3). §2's figures are 1.0.0's, and
[§8](#110-as-published) has the published package measured against them.
**Audience:** the Bodu maintainer and FallbackPlan contributors ·
**Raised:** 2026-09-27

FallbackPlan derives keys from what a person types with Argon2id at 64 MiB,
three iterations and four lanes. Those are the minimums its format sets for a
new repository ([repository-format 03 §2](../specifications/repository-format/03-keys.md#2-the-root)),
and RFC 9106's second recommended option. It derives in three places, each
through `Argon2id.DeriveKey`:

- `Repository.Crypto/KekDerivation`, on every repository open;
- `Repository.Crypto/WriteOnlyDerivation`, for a write-only repository's root,
  behind every restore and reclaim grant;
- `Repository.Crypto/PasswordHash`, for every console account and every login,
  including the fixed hash an unknown name is checked against.

A person waits through each one. The cost is the point of the function: the
memory is what makes each guess expensive, and FallbackPlan will not lower
it. What is not the point is cost the implementation adds on top. Bodu 1.0.0
fills the four lanes one after another, so a derivation that RFC 9106 lets
four cores share takes one core the whole time. Its compression function is
scalar. And every call allocates the 64 MiB matrix afresh, for the runtime to
zero and the collector to reclaim.

This document states what FallbackPlan needs from a faster implementation,
measured against 1.0.0. It is a requirements statement against the
*library*, written from the consumer side; how Bodu implements it is Bodu's
decision.

Requirement IDs here are `ARG-F-*` (functional) and `ARG-N-*`
(non-functional). They are scoped to this document and deliberately do not
use FallbackPlan's `FR-*`/`NFR-*` namespace.

---

## 1. Consumers and their shapes

| Consumer | What it needs |
|----------|---------------|
| **FallbackPlan's unlock paths** (`KekDerivation`, `WriteOnlyDerivation`) | One derivation per repository open or grant, which the person waits through. They run on desktops and laptops, many without AVX-512, and on ARM64: Apple silicon and small boards. |
| **FallbackPlan's console logins** (`PasswordHash`) | One derivation per attempt, on a service that may be verifying several at once while it runs backups. |
| **FallbackPlan's test suites and CI** | About 1,500 derivations per full run, many at once, on three- and four-core runners that are bound by CPU ([§2.3](#23-what-fallbackplans-suites-pay)). |
| **Other consumers of password hashing** | Interactive tools want latency. Servers verifying logins at volume want throughput per core and a bounded memory footprint. |

## 2. Current state, measured against 1.0.0

### 2.1 What one derivation costs

Measured with the published packages on a 4-vCPU Intel Xeon at 2.1 GHz with
AVX2 and AVX-512, on .NET 10.0.12, at m = 64 MiB, t = 3, p = 4 and a 32-byte
tag. The harness is in the [appendix](#appendix-how-the-numbers-were-measured).
Konscious 1.3.1 is the independent implementation FallbackPlan cross-verifies
Bodu against on every CI run, and both produced the same tag.

| | Wall per call | CPU per call | Cores used | Allocated per call | Gen2 collections per call |
|---|---|---|---|---|---|
| Bodu 1.0.0, p = 4 | 209–213 ms | 208–225 ms | 1.0 | 64.0 MiB | 0.5 |
| Bodu 1.0.0, p = 1 | 205–207 ms | 207–220 ms | 1.0 | 64.0 MiB | 0.5 |
| Konscious 1.3.1, p = 4 | 94–102 ms | 297–300 ms | 2.9–3.2 | 64.5 MiB | 0.6–0.7 |
| Konscious 1.3.1, p = 1 | 265–287 ms | 272–289 ms | 1.0 | 64.3 MiB | 0.7 |

Each range spans two rounds run in opposite orders. With four derivations at
once, each on its own thread, Bodu completed sixteen in 1.77 s, 111 ms each on
average against 234 ms each one at a time. So each derivation took about
twice as long as it does alone.

Read together:

- **Bodu's per-core code is the faster of the two.** At p = 1 it needs a
  fifth to a quarter less CPU than Konscious.
- **Konscious is faster for the person only because it fills the lanes on
  several threads**, and it spends between a third and a half more CPU per
  derivation than Bodu to do it.
- **p buys Bodu nothing.** Four lanes cost what one does, and wall time
  equals CPU time.
- **The collector costs little per call**, 4 to 5 ms of pause. But a gen2
  collection every second derivation lands on everything else a long-running
  service is doing.
- **Four at once is limited by the memory system, not by threads.** Argon2 is
  memory-hard by design, and four 64 MiB matrices share one memory bus.

### 2.2 Where the time goes

From the source at
[`40068ba`](https://github.com/bslater/bodu/tree/40068ba265b56cf488079e6f3bc8d26a7f8e5fd7):

- **Lanes are filled one after another.**
  [`Argon2Core.DeriveTag`](https://github.com/bslater/bodu/blob/40068ba265b56cf488079e6f3bc8d26a7f8e5fd7/Bodu.Security.Cryptography/src/Security.Cryptography/Argon2Core.cs#L66-L73)
  loops over pass, slice and lane in turn, and
  [`Argon2`'s remarks](https://github.com/bslater/bodu/blob/40068ba265b56cf488079e6f3bc8d26a7f8e5fd7/Bodu.Security.Cryptography/src/Security.Cryptography/Argon2.cs#L26)
  say so. RFC 9106 §3.4 permits otherwise: "Segments of the same slice can be
  computed in parallel and do not reference blocks from each other."
- **Every call allocates the matrix.**
  [`new ulong[memoryBlocks * WordsPerBlock]`](https://github.com/bslater/bodu/blob/40068ba265b56cf488079e6f3bc8d26a7f8e5fd7/Bodu.Security.Cryptography/src/Security.Cryptography/Argon2Core.cs#L58)
  is 64 MiB that the runtime zeroes, that lands on the large-object heap, and
  that a gen2 collection reclaims. The matrix is cleared after use
  ([L79](https://github.com/bslater/bodu/blob/40068ba265b56cf488079e6f3bc8d26a7f8e5fd7/Bodu.Security.Cryptography/src/Security.Cryptography/Argon2Core.cs#L79)),
  which is right and must stay ([ARG-N-006](#security)).
- **The compression function is scalar.**
  [`FillBlock`](https://github.com/bslater/bodu/blob/40068ba265b56cf488079e6f3bc8d26a7f8e5fd7/Bodu.Security.Cryptography/src/Security.Cryptography/Argon2Core.cs#L335-L354)
  and
  [`Permute`](https://github.com/bslater/bodu/blob/40068ba265b56cf488079e6f3bc8d26a7f8e5fd7/Bodu.Security.Cryptography/src/Security.Cryptography/Argon2Core.cs#L361-L406)
  apply one `GB` at a time, and the column step copies sixteen words into a
  stack buffer and back. The package already dispatches BLAKE2, BLAKE3,
  Threefish and CubeHash to AVX-512 kernels through `SimdCapabilities`, but
  Argon2's permutation has no accelerated path.
- **Stack scratch is zeroed for every block.** The project does not set
  `SkipLocalsInit`, so `FillBlock`'s two 1 KiB buffers and `Permute`'s
  128-byte one are cleared on every call, about 197,000 times per derivation
  at these parameters. What that costs is for a benchmark to say.

### 2.3 What FallbackPlan's suites pay

A local run of the six FallbackPlan suites that derive keys made about 1,500
derivations at these parameters, and the time spent inside them came to
about 360 s. That is roughly half of those suites' test time.

| Suite | Derivations | Time inside them |
|---|---|---|
| Hosts.Tests | 1,078 | 253 s |
| Retention.Tests | 195 | 44 s |
| Repository.Tests | 75 | 35 s |
| Cli.Tests | 113 | 25 s |
| Web.Tests | 24 | 5 s |
| InterruptionTests | 6 | 1.5 s |

None of it is waste in the product. The repeated derivations were traced in
the three suites that make most of them, Hosts.Tests, Retention.Tests and
Cli.Tests. Each repeat is a separate operation on a repository the test had
already created, which a real user would pay once per operation too. FallbackPlan's CI runs
every suite at once on three- and four-core runners and is bound by CPU there,
so any CPU a derivation sheds comes off every run, up to the point where the
shared memory bus of §2.1 becomes the limit.

## 3. Functional requirements

- **ARG-F-001 — Output unchanged.** Every tag is bit-for-bit what 1.0.0
  produces for the same inputs and parameters, for all three variants and
  both versions. *Acceptance:* RFC 9106's test vectors, Bodu's own Argon2
  tests, FallbackPlan's committed
  [`argon2id.json`](../specifications/repository-format/conformance/vectors/argon2id.json)
  vectors, and FallbackPlan's cross-verification against Konscious all pass
  unchanged.

- **ARG-F-002 — API unchanged.** `Argon2id.DeriveKey` and `Argon2Parameters`
  keep their signatures and meaning, so FallbackPlan can adopt the new
  version with a version bump alone. Anything new is additive.

- **ARG-F-003 — The caller can bound a derivation's threads.** It can go down
  to one thread, without changing the output: p stays the RFC's lane count
  and is part of the result, and the thread bound is not. A service
  verifying many logins at once, or a test host running many derivations,
  gains nothing when each spreads across every core, and can lose to the
  contention.

## 4. Non-functional requirements

### Latency

- **ARG-N-001 — Lanes on cores.** With p = 4 and four idle cores, one
  derivation's wall time falls to at most 40 % of 1.0.0's on the same
  machine. Konscious gets under half (§2.1), but at more CPU than 1.0.0
  uses. ARG-N-002 asks that Bodu not give up its per-core lead to get there.

### CPU

- **ARG-N-002 — Vector compression where people run it.** CPU per derivation
  falls to at most 60 % of 1.0.0's on x64 with AVX2 and on ARM64 with
  AdvSimd. An AVX-512 path alone would miss many desktops and laptops, and
  every Apple-silicon machine.

- **ARG-N-003 — No regression without the hardware.** The scalar path, taken
  when no vector set is available or when `SimdCapabilities`' opt-out is set,
  is no slower than 1.0.0.

### Memory and concurrency

- **ARG-N-004 — No matrix per call on the collected heap.** A steady loop of
  derivations triggers no gen2 collections of its own, and allocates less
  than 1 MiB of managed memory per call. How the matrix is held instead is
  open question 2.

- **ARG-N-005 — Concurrency no worse.** N derivations at once on N cores
  reach at least 1.0.0's throughput: 2.1 times one-at-a-time for four
  (§2.1). The benchmark of ARG-N-009 reports it.

### Security

- **ARG-N-006 — Everything password-derived is cleared.** The matrix, H0, and
  any scratch that holds password-derived words, whether per-thread, pooled
  or on the stack, is cleared before it is released or reused, as 1.0.0
  clears the matrix and H0 today.

- **ARG-N-007 — The data-independent half stays data-independent.** In
  Argon2id's first half-pass, memory is addressed independently of the
  password. No vector or threaded code may introduce password-dependent
  branches, table lookups or memory access there.

### Proof and packaging

- **ARG-N-008 — Every path held to the vectors.** Paths are chosen at run
  time. The accelerated ones are held to the known-answer vectors in the main
  test assembly, and the scalar one in `Bodu.Security.Cryptography.Simd.Test`
  with the opt-out set, as the package already does for BLAKE2, BLAKE3 and
  Threefish. An ARM64 path needs an ARM64 run.

- **ARG-N-009 — A benchmark that measures this.**
  `Bodu.Security.Cryptography.Benchmarks` gains an Argon2id benchmark at
  m = 64 MiB, t = 3, p = 4 and at a small memory size, one derivation at a
  time and four at once, reporting time and allocation.

- **ARG-N-010 — Nothing new to depend on.** The package keeps its current
  dependencies, target frameworks and AOT compatibility.

## 5. What FallbackPlan would do with it

- **Take the version.** ARG-F-002 means no source change, and the
  cross-verification test and committed vectors prove the output is
  unchanged.
- **Measure again**, with the harness in the appendix and the same count of
  derivations across the suites, and record the result in §8.
- **Nothing else.** FallbackPlan's parameters stay the specification's
  minimums, and nothing in FallbackPlan caches a derivation.

## 6. Out of scope

- **Lower parameters anywhere in FallbackPlan.** The cost is the protection.
  This asks only that the implementation add as little as it can on top of
  it.
- **A native implementation or a platform call.** .NET has no Argon2id of its
  own, which is why Bodu's is the one FallbackPlan uses
  ([ADR-0019](adr/0019-third-party-dependency-policy.md)).
- **Caching derivations.** A cache would keep derived keys alive past their
  use for a saving only tests would see (§2.3).

## 7. Open questions for the maintainer

> **Answered 2026-09-27** by the maintainer's implementation plan for 1.1.0.
> §8 records each answer.

1. **Threads by default, or on request?** Filling lanes in parallel is the
   latency win of ARG-N-001, but a host running many derivations at once
   gains nothing from it (§2.1). FallbackPlan's preference is parallel by
   default, bounded by p and the core count, with ARG-F-003's bound for
   callers that know better.
2. **How to hold the matrix?** Native memory freed deterministically avoids
   both the zeroing and the collector, but needs unsafe code. A buffer
   reused per thread avoids them too, but keeps 64 MiB resident per thread
   that ever derived. An uninitialised managed array avoids only the zeroing.
   ARG-N-004 and ARG-N-006 hold whichever is chosen.
3. **Version.** ARG-F-002 and ARG-F-003 are additive, which suggests a minor
   version, and the Bodu packages move in lock-step.

## 8. Disposition

> **Released 2026-09-28 as 1.1.0, and adopted the same day.** Upstream
> published `Bodu.Security.Cryptography` 1.1.0 alone, through the
> out-of-band route of decision 3. It is built from Bodu's master at
> [`b5cc004`](https://github.com/bslater/bodu/tree/b5cc004452bb805007bec1cb13d4832ccf418673).
> The plan's Argon2id reached master in
> [#710](https://github.com/bslater/bodu/pull/710), together with a
> successor plan that carried the same techniques through the package's
> other primitives. FallbackPlan took the release with the version bump alone
> (§5), under
> [ADR-0021](adr/0021-consume-bodu-via-committed-package-feed.md)
> Amendment 3, and §5's "measure again" ran against it.
> [1.1.0 as published](#110-as-published) has the figures. Everything between
> this note and that section is kept as it was written before the release,
> so its figures are the first implementation's.

Raised 2026-09-27, from the test-cost investigation whose numbers are §2.3.

Answered the same day by the maintainer's plan,
[`plans/argon2-performance.md`](https://github.com/bslater/bodu/blob/182b257/plans/argon2-performance.md),
and implemented against it for **1.1.0** on the bodu branch
`claude/argon2-prototype-co27tu`, at `182b257`. Nothing is merged or
released yet. FallbackPlan still consumes 1.0.0, and adopts the change with
the version bump once 1.1.0 is on NuGet (§5). The decisions are below the
tables.

A first implementation on the bodu branch `claude/new-session-ujlem9`
(seven commits on `40068ba`, ending at `9a3949e`) came before the plan. It
answered question 2 differently, with a pooled managed array, and put the
bound in `Argon2Parameters` rather than on the instance. The plan's
implementation supersedes it. It is recorded here because the tables below
were measured with it.

- The figures below were measured as §2.1's were, on the same machine and
  against the same parameters.
- Each is set against §2.1's figures for 1.0.0: 209 to 213 ms wall and
  208 to 225 ms CPU per derivation, and 111 ms each with four at once.

| ID | Requirement | Disposition |
|----|-------------|-------------|
| ARG-F-001 | Output unchanged | Met. RFC 9106's vectors, the reference implementation's 21, and 25 regression vectors captured from the published 1.0.0 pass through every kernel at one thread, two, and one per lane. With the first implementation's assembly in place of 1.0.0's, FallbackPlan's committed `argon2id.json` vectors and its cross-verification against Konscious pass. |
| ARG-F-002 | API unchanged | Met. One property is added, and nothing changes signature or meaning. |
| ARG-F-003 | A caller-set thread bound | Met by the first implementation for every entry point that takes `Argon2Parameters`, but not for `Verify`, which reads its parameters from the PHC string. 1.1.0 puts the bound on the instance and adds a bounded `Verify` (decision 4). |
| ARG-N-001 | Lanes on cores | Met: 32 to 38 ms, which is 15 to 18 % of 1.0.0. |
| ARG-N-002 | Vector compression | Met on x64 with AVX2: 71 to 75 ms of CPU with the lanes on one thread, and 82 to 87 ms with them spread, which is 32 to 42 %. The 128-bit kernel, used on x64 without AVX2, took 119 to 123 ms before the matrix was pooled. The same kernel serves Arm64, where its speed is not measured. |
| ARG-N-003 | No regression without the hardware | Met: the scalar kernel took 156 to 162 ms of CPU before the matrix was pooled. |
| ARG-N-004 | No matrix per call | Met: 2 to 28 KB allocated per derivation, and no gen2 collections. The first implementation rented the matrix from the runtime's shared array pool. 1.1.0 holds it in native memory instead (decision 2). |
| ARG-N-005 | Concurrency no worse | Met: four at once took 24 to 26 ms each, 4.3 to 4.6 times 1.0.0's throughput. |
| ARG-N-006 | Password-derived state cleared | Met: the matrix before it goes back to the pool. Also the stack scratch, H0's expansion and the final block, which 1.0.0 left on the stack. |
| ARG-N-007 | The data-independent half stays so | Met by construction. Every kernel is additions, rotations, XORs and a fixed-latency multiplication, with no branch or table lookup on the data. Threads divide the lanes, which the parameters fix. |
| ARG-N-008 | Every path held to the vectors | Met on x64, in the main test assembly and in the opt-out one. The Arm64 kernel was held to all 49 vectors under emulation, on .NET 8 and 10. That is not a CI run: Bodu's CI is x64 only. |
| ARG-N-009 | A benchmark | Met: `Argon2Benchmarks`. |
| ARG-N-010 | Nothing new to depend on | Met. |

The plan records its own implementation's figures in its §10. They come
from a different machine, a four-vCPU Xeon, with 1.0.0 measured in the same
session. One derivation took 30.7 ms of wall time with AVX2 and threads,
12 % of 1.0.0's 247.5 ms, and 37 % of its CPU. It allocated 26 KiB, with no
gen2 collections.

§5's "measure again" was run against the first implementation without
waiting for a release, and runs again against 1.1.0 when FallbackPlan
takes it.
- The first implementation's `Bodu.Security.Cryptography.dll` replaced
  1.0.0's in the test outputs. The assembly identity is the same, and
  between the 1.0.0 release and its base the package's sources differ only
  in doc comments.
- Each suite ran alone, and the two largest twice. Every test passed on both
  assemblies.

| Suite | 1.0.0 | The first implementation |
|---|---|---|
| Hosts.Tests | 2m35s, 2m37s | 1m49s, 1m47s |
| Cli.Tests | 16 s, 16 s | 5 s, 5 s |
| Retention.Tests | 41 s | 36 s |
| Repository.Tests | 28 s | 25 s |
| Repository.ConformanceTests | 3 s | 2 s |

### The maintainer's decisions

These are the plan's §3, confirmed in the code of `claude/argon2-prototype-co27tu` at `182b257`.

1. **Threads by default** (question 1), as this document preferred. A
   derivation fills its lanes on up to p threads, bounded by the processor
   count, the calling thread included. It divides a slice only once a
   segment is at least 256 blocks, a lane of 1 MiB. Below that it stays on
   the calling thread, because dispatching costs more than it spreads.
   `MerkleTree` still defaults to one thread, and the difference is
   deliberate. There, parallelism is an opt-in trade. Here, p is the
   caller's own statement of how many lanes may run at once.
2. **The matrix lives in native memory, reused through a small bounded pool**
   (question 2). Buffers are 64-byte aligned and cleared every time they are
   returned. The pool keeps at most one per processor, each of at most
   256 MiB, and frees any left idle for 30 seconds. The `AppContext` switch
   `Bodu.Security.Cryptography.Argon2.DisableMatrixReuse` turns retention
   off. At FallbackPlan's parameters that is at most processor count × 64 MiB
   of zeroed memory, for 30 seconds after the last derivation. The plan
   preferred native memory to a pooled array for three reasons:
   - A trimmed buffer goes back at once, where a dropped array waits for a
     gen2 collection.
   - It is 64-byte aligned, so no vector load straddles a cache line.
   - It never moves.

   The price is `AllowUnsafeBlocks` for the project, with the pointer code
   confined to one type.
3. **Version 1.1.0** (question 3), a minor version, since the API change is
   additive. It ships with the next lock-step wave, or alone through Bodu's
   out-of-band route if FallbackPlan cannot wait for the wave.
4. **The bound lives on the instance, and `Verify` takes one too.** The
   additions are `new Argon2id(parameters, maxDegreeOfParallelism)` (and the
   same for Argon2i and Argon2d), a `MaxDegreeOfParallelism` property, and
   `Argon2.Verify(encoded, password, secret, maxDegreeOfParallelism)`.
   `Argon2Parameters` is unchanged. A PHC string cannot carry the bound, and
   the bound is not part of the result. -1, the default, lets the library
   choose. 1 confines a derivation to the calling thread, as 1.0.0 ran.

For FallbackPlan this changes nothing in §5. Every derivation it makes goes
through the one-shot `Argon2id.DeriveKey` (`KekDerivation` and
`PasswordHash`; `WriteOnlyDerivation` goes through `KekDerivation`). That
method keeps its signature and takes the default bound, so adopting 1.1.0
is the version bump alone. The committed vectors and the cross-verification
against Konscious prove the output.

### 1.1.0 as published

§5's "measure again", run against the published package on 2026-09-28 with
the appendix's harness.

**The machine is not §2.1's.** It is a 4-vCPU Intel Xeon at 2.8 GHz with
AVX2 and AVX-512, on .NET 10.0.12. 1.0.0 takes more than twice as long here
as it did there: 438 to 465 ms a derivation, against 209 to 213 ms. So each
figure below is set against 1.0.0 measured in the same session, on the same
machine and with the same harness, and the ratios are what carry over.

The harness was built twice, once against each version, and the two builds
ran in alternating order. Each range spans every run of its case, two to
eight of them. Every case produced the same tag as Konscious at p = 4 and
p = 1, and a bounded derivation produced the same tag as an unbounded one.

| | Wall per call | CPU per call | Cores used | Allocated per call | Gen2 collections per call |
|---|---|---|---|---|---|
| 1.0.0, p = 4 | 438–465 ms | 443–469 ms | 1.0 | 64.0 MiB | 0.5 |
| 1.0.0, p = 1 | 455–487 ms | 443–478 ms | 1.0 | 64.0 MiB | 0.5 |
| 1.1.0, p = 4 | 65–74 ms | 159–178 ms | 2.4–2.6 | 26 KiB | 0 |
| 1.1.0, p = 4, bound 1 | 134–162 ms | 134–162 ms | 1.0 | 0.3 KiB | 0 |
| 1.1.0, p = 4, 128-bit kernel | 92–99 ms | 247–255 ms | 2.6–2.7 | 26 KiB | 0 |
| 1.1.0, p = 4, scalar | 111–119 ms | 301–305 ms | 2.5–2.7 | 26 KiB | 0 |
| 1.1.0, p = 4, no matrix reuse | 80–90 ms | 203–220 ms | 2.4–2.5 | 26 KiB | 0 |
| Konscious 1.3.1, p = 4 | 144–177 ms | 439–487 ms | 2.8–3.1 | 64.5 MiB | 0.7–0.8 |

How each variant was selected:
- **The 128-bit kernel** is the one x64 takes without AVX2, and was selected
  with `DOTNET_EnableAVX2=0`. Arm64 runs the same kernel over AdvSimd, and
  its speed there is still not measured.
- **Scalar** is `SimdCapabilities`' opt-out switch.
- **No matrix reuse** is the `Bodu.Security.Cryptography.Argon2.DisableMatrixReuse`
  switch.

1.1.0 at p = 1 costs what the bound of 1 does, 132 to 158 ms.

With four derivations at once, each on its own thread:
- 1.0.0 took 125 to 127 ms each on average;
- 1.1.0 took 41 to 49 ms each, and 38 to 41 ms each with the bound at 1;
- Konscious took 116 to 121 ms each.

| ID | Requirement | 1.1.0 as published |
|----|-------------|--------------------|
| ARG-F-001 | Output unchanged | Met at FallbackPlan's boundary. The committed `argon2id.json` vectors, both frozen fixtures and the cross-verification against Konscious pass with each kernel an x64 machine can select: AVX2, the 128-bit kernel under `DOTNET_EnableAVX2=0`, and the scalar one under `DOTNET_EnableHWIntrinsic=0`. |
| ARG-F-002 | API unchanged | Met. FallbackPlan builds against 1.1.0 with no source change and warnings as errors. A reflection diff of the two public surfaces finds eighteen members added, and none removed or changed. |
| ARG-F-003 | A caller-set thread bound | Met. A bound of 1 keeps a derivation on the calling thread, at 1.0 cores used, with the same tag. |
| ARG-N-001 | Lanes on cores | Met: 14 to 17 % of 1.0.0's wall time. |
| ARG-N-002 | Vector compression | Met on x64 with AVX2: 34 to 40 % of 1.0.0's CPU with the lanes spread, and 29 to 36 % on one thread. The 128-bit kernel takes 53 to 58 % spread and 46 to 50 % on one thread. |
| ARG-N-003 | No regression without the hardware | Met: the scalar path takes 64 to 69 % of 1.0.0's CPU spread, and 59 to 64 % on one thread. |
| ARG-N-004 | No matrix per call | Met: 0.3 KiB allocated on one thread and 26 KiB with the lanes spread, and no gen2 collections. |
| ARG-N-005 | Concurrency no worse | Met: four at once reach 2.5 to 3.1 times 1.0.0's throughput, and 3.1 to 3.4 times with the bound at 1. |
| ARG-N-008 | Every path held to the vectors | Met on x64 as the first table says. Bodu's CI now runs the cryptography suites on an Arm64 runner as well, and that job passed at `b5cc004`. The x64 job there failed on one test, on net8.0 only: a `MerkleTree` memory probe that measured no growth where it expects some. No output assertion failed, and every Argon2, X25519 and Ed25519 test passed on both frameworks. |
| ARG-N-010 | Nothing new to depend on | Met. The package declares the same one dependency, `Bodu.Core` 1.0.0, for the same two target frameworks, and the assembly still declares itself trimmable and AOT-compatible. |

ARG-N-006, ARG-N-007 and ARG-N-009 are properties of the library's code,
which a consumer cannot observe from outside. They were read in the published
source at `b5cc004`:
- **ARG-N-006.** The matrix is cleared before it goes back to the pool. So
  are H0, the input and output blocks of H′, the final block, each segment's
  scratch and the BLAKE2b state. The Argon2 cores are `[SkipLocalsInit]`, so
  the scratch is no longer zeroed for every block as §2.2 found.
- **ARG-N-007.** The kernels branch only on whether a pass XORs into the
  block it overwrites, which the pass number and the version decide, and
  their loops run a fixed number of times.
- **ARG-N-009.** `Argon2Benchmarks` and `Argon2FirstCallBenchmarks`.

1.1.0's Argon2id is the plan's implementation at `182b257` with two changes.
Each moves a part of it into code that the package's other primitives now
share:
- its matrix pool became `NativeBufferPool`, under decision 2's limits and
  switch;
- its BLAKE2b compression, which H0 and H′ run through, became the package's
  `Blake2bCore`.

FallbackPlan's suites, on the same machine:
- The same Release build ran with each version's assembly in every output,
  so nothing else differed between the runs.
- Each suite ran alone, in two rounds of opposite order.
- Every test passed on both assemblies.

| Suite | 1.0.0 | 1.1.0 |
|---|---|---|
| Hosts.Tests | 270 s, 272 s | 141 s, 142 s |
| Cli.Tests | 33 s, 36 s | 10 s, 10 s |
| Retention.Tests | 51 s, 54 s | 41 s, 42 s |
| Repository.Tests | 52 s, 54 s | 48 s, 49 s |
| Repository.ConformanceTests | 5.6 s, 5.9 s | 3.9 s, 3.9 s |
| Web.Tests | 9.7 s, 10.3 s | 6.9 s, 7.1 s |
| InterruptionTests | 16.6 s, 17.4 s | 13.2 s, 13.0 s |
| **All seven** | **438 s, 450 s** | **264 s, 267 s** |

The seven suites finish in 40 % less time. Hosts.Tests, which makes most of
the derivations, takes 48 % less, and Cli.Tests takes less than a third of
its time. The suites whose time is mostly not Argon2id gain least:
Repository.Tests takes 8 to 10 % less.

## Appendix: how the numbers were measured

A console application on .NET 10.0.12 referencing the published
`Bodu.Security.Cryptography` 1.0.0 and `Konscious.Security.Cryptography.Argon2`
1.3.1, run in Release on the machine described in §2.1, with nothing else
running. Both produced the same tag for the same inputs. Each case was warmed
up six times and then measured over ten calls. The four-at-once figure ran
four threads, each deriving four times.

```csharp
void Measure(string name, Func<byte[]> derive, int runs = 10)
{
    for (int i = 0; i < 6; i++) derive();
    var pauseBefore = GC.GetTotalPauseDuration();
    var process = Process.GetCurrentProcess();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long allocBefore = GC.GetTotalAllocatedBytes(true);
    int gen2Before = GC.CollectionCount(2);
    process.Refresh(); var cpuBefore = process.TotalProcessorTime;
    var walls = new List<double>();
    for (int i = 0; i < runs; i++)
    {
        var sw = Stopwatch.StartNew(); derive(); walls.Add(sw.Elapsed.TotalMilliseconds);
    }
    process.Refresh();
    double cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / runs;
    // Report: median wall, CPU per call, CPU / wall ("cores used"), allocation,
    // gen2 collections and GC pause per call.
}

Measure("Bodu 1.0.0 p=4", () => Bodu.Security.Cryptography.Argon2id.DeriveKey(password, salt,
    new() { MemoryKiB = 65536, Iterations = 3, Parallelism = 4, TagLength = 32 }));
Measure("Konscious 1.3.1 p=4", () => new Konscious.Security.Cryptography.Argon2id(password)
    { Salt = salt, DegreeOfParallelism = 4, Iterations = 3, MemorySize = 65536 }.GetBytes(32));
```

The counts in §2.3 came from a temporary probe around FallbackPlan's two call
sites, recording each derivation's parameters, a digest of its inputs, and
its duration. The probe was removed afterwards and was never committed.

[1.1.0 as published](#110-as-published) used the same harness, built once
against each package, with the builds run in alternating order. It adds three
things:
- the bounded case, `new Argon2id(parameters, 1).GetBytes(password, salt)`;
- each variant's switch, set before the first derivation;
- a check that the bounded tag equals the unbounded one.

Its suite timings used one Release build of FallbackPlan. Each suite ran
alone with `dotnet test --no-build`, once with 1.0.0's
`Bodu.Security.Cryptography.dll` in every output and once with 1.1.0's. The
assembly version rises from 1.0.0.0 to 1.1.0.0, which the runtime binds in
place of the lower version it was compiled against.

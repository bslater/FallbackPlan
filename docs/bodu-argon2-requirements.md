# Requirements: a faster Argon2id in Bodu

**Status: Implemented upstream on a branch, not yet released** — against `Bodu.Security.Cryptography` **1.0.0**, the
package FallbackPlan makes every passphrase- and password-based derivation
through.
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

Raised 2026-09-27, from the test-cost investigation whose numbers are §2.3.

Implemented the same day on the bodu branch
[`claude/new-session-ujlem9`](https://github.com/bslater/bodu/tree/claude/new-session-ujlem9):
seven commits on `40068ba`, ending at
[`9a3949e`](https://github.com/bslater/bodu/commit/9a3949e).

- Nothing is merged or released yet. FallbackPlan still consumes 1.0.0, and
  adopting the change waits on a release (question 3).
- The figures below were measured as §2.1's were, on the same machine and
  against the same parameters.
- Each is set against §2.1's figures for 1.0.0: 209 to 213 ms wall and
  208 to 225 ms CPU per derivation, and 111 ms each with four at once.

| ID | Requirement | Disposition |
|----|-------------|-------------|
| ARG-F-001 | Output unchanged | Met. RFC 9106's vectors, the reference implementation's 21, and 25 regression vectors captured from the published 1.0.0 pass through every kernel at one thread, two, and one per lane. With the branch's assembly in place of 1.0.0's, FallbackPlan's committed `argon2id.json` vectors and its cross-verification against Konscious pass. |
| ARG-F-002 | API unchanged | Met. One property is added, and nothing changes signature or meaning. |
| ARG-F-003 | A caller-set thread bound | Met for every entry point that takes `Argon2Parameters`: `MaxDegreeOfParallelism`, with `MerkleTree`'s contract. `Verify` reads its parameters from the PHC string and uses the default; see question 4 below. |
| ARG-N-001 | Lanes on cores | Met: 32 to 38 ms, which is 15 to 18 % of 1.0.0. |
| ARG-N-002 | Vector compression | Met on x64 with AVX2: 71 to 75 ms of CPU with the lanes on one thread, and 82 to 87 ms with them spread, which is 32 to 42 %. The 128-bit kernel, used on x64 without AVX2, took 119 to 123 ms before the matrix was pooled. The same kernel serves Arm64, where its speed is not measured. |
| ARG-N-003 | No regression without the hardware | Met: the scalar kernel took 156 to 162 ms of CPU before the matrix was pooled. |
| ARG-N-004 | No matrix per call | Met: 2 to 28 KB allocated per derivation, and no gen2 collections. The matrix is rented from the runtime's shared array pool (question 2). |
| ARG-N-005 | Concurrency no worse | Met: four at once took 24 to 26 ms each, 4.3 to 4.6 times 1.0.0's throughput. |
| ARG-N-006 | Password-derived state cleared | Met: the matrix before it goes back to the pool. Also the stack scratch, H0's expansion and the final block, which 1.0.0 left on the stack. |
| ARG-N-007 | The data-independent half stays so | Met by construction. Every kernel is additions, rotations, XORs and a fixed-latency multiplication, with no branch or table lookup on the data. Threads divide the lanes, which the parameters fix. |
| ARG-N-008 | Every path held to the vectors | Met on x64, in the main test assembly and in the opt-out one. The Arm64 kernel was held to all 49 vectors under emulation, on .NET 8 and 10. That is not a CI run: Bodu's CI is x64 only. |
| ARG-N-009 | A benchmark | Met: `Argon2Benchmarks`. |
| ARG-N-010 | Nothing new to depend on | Met. |

§5's "measure again" was run against the branch without waiting for a
release.
- The branch's `Bodu.Security.Cryptography.dll` replaced 1.0.0's in the test
  outputs. The assembly identity is the same, and between the 1.0.0 release
  and the branch's base the package's sources differ only in doc comments.
- Each suite ran alone, and the two largest twice. Every test passed on both
  assemblies.

| Suite | 1.0.0 | The branch |
|---|---|---|
| Hosts.Tests | 2m35s, 2m37s | 1m49s, 1m47s |
| Cli.Tests | 16 s, 16 s | 5 s, 5 s |
| Retention.Tests | 41 s | 36 s |
| Repository.Tests | 28 s | 25 s |
| Repository.ConformanceTests | 3 s | 2 s |

The branch settles questions 1 and 2 provisionally, and they remain the
maintainer's:

1. **The default bound is -1**, FallbackPlan's preference. `MerkleTree`
   defaults to 1. Either way, a derivation with less than 1 MiB of memory per
   lane fills its lanes on one thread, because below that the handoff cost more
   than it saved.
2. **The matrix is rented from `ArrayPool<ulong>.Shared`** and cleared before it
   goes back. That avoids the zeroing and the collector without unsafe code.
   The cost is that a buffer stays resident per thread that derived, until the
   runtime trims idle buffers.
3. **The version is unchanged** on the branch. The added property suggests a
   minor version.
4. **`Verify` has no thread bound.** A server verifying many logins at once has
   no way to ask for one thread there.

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

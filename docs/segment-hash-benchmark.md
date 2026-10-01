# Segment hash throughput — SHA-256 against BLAKE3, and against what shares its core

**Status:** published · **Settles:** [Q6](open-questions.md#q6--segment-hash-function) · **Informs:** [ADR-0004 Amendment 1](adr/0004-segment-hash-function.md#amendment-1-2026-09--sha-256-is-kept-and-there-is-no-profile-field-to-change-it-through), where the decision is recorded

---

## Question under test

ADR-0004 chose SHA-256 for the content identifier and asked that the choice be confirmed against a throughput benchmark. It deferred BLAKE3 until two things held:

- **hashing proves the binding constraint** on NFR-PERF-007, the single-stream capture target of 400 MB/s on incompressible data; and
- **a managed implementation removes the portability objection**, since the only .NET bindings it had seen wrapped a native library, and the recovery tool must run with no native code (NFR-PORT-001).

This page measures both.

## Method

`HashThroughputBenchmark` (`tests/FallbackPlan.PerformanceTests`) times each function on one thread. Run it with `dotnet run -c Release -- hash-throughput`. Each figure is the median of five 0.3-second trials after a 0.4-second warm-up, taken at three input sizes:

- 64 KiB;
- 1 MiB, the default segment;
- 16 MiB, standing in for a whole large file, which is the shape of the whole-file hash.

What it measures:

- **SHA-256** through `ContentHasher.Hash`, the product's own content-id path. The implementation is the platform's, which on Linux is OpenSSL. OpenSSL uses the processor's SHA extensions where they exist.
- **SHA-512.** SHA-512/256, the alternative ADR-0004 turned down, runs SHA-512's compression function, so this stands in for it.
- **BLAKE3** from `Bodu.Security.Cryptography` 1.1.0. This is managed code, with kernels for AVX-512, AVX2, SSSE3 and AdvSimd. It already reaches the project through Repository.Crypto, so measuring it adds no package. It is measured here and used nowhere else.
- **Context:** the stages that share a core with the hash.
  - AES-256-GCM, from the platform.
  - The product's `ZstdSegmentCodec` at the default level 3 and threshold, run on two inputs. One is text-like input that it packs about three to one. The other is incompressible input, which it tries to compress and then stores as `none`.

What it does not measure: a non-cryptographic hash, such as the CityHash or XXH3 families. They are faster again, but the second-preimage requirement rules them out whatever they cost. Deduplication decides on this identifier, so a collision is a data-corruption path ([ADR-0004](adr/0004-segment-hash-function.md#alternatives-considered), [ADR-0006](adr/0006-object-identifiers-and-dedup-trust-domains.md)).

## Results

Two containers, each with four logical processors of an Intel Xeon at 2.80 GHz, running Linux and .NET 10. They differ in the one thing that matters here: **the first exposed the processor's SHA extensions and the second does not.**

- **The "without" column** is this harness, run twice on the second container. The ranges show the spread between the two runs.
- **The "with" column** was measured on the first container by this harness's predecessor, which timed the same calls. That container is gone, so this column cannot be re-run from here. It is kept because it is the only measurement of the case the SHA extensions decide.

MiB/s on one thread, 1 MiB inputs:

| Function | With SHA extensions | Without |
|----------|--------------------:|--------:|
| **SHA-256**, the content id | **1 487–1 520** | **359–376** |
| SHA-512, SHA-512/256's function | 640–641 | 554–575 |
| BLAKE3, managed | 2 225–4 389 | 2 619–2 762 |
| AES-256-GCM seal | 10 510–11 948 | 3 240–3 256 |
| zstd-v1 level 3, text-like | 155–164 | 121–139 |
| zstd-v1 level 3, incompressible | — | 2 625–2 842 |

At 64 KiB the order is the same. SHA-256 measured 363–395 MiB/s without the extensions and 1 433–1 447 with them.

On the first container, a third run hid the extensions from OpenSSL (`OPENSSL_ia32cap=:~0x20000000`). SHA-256 then measured 416–435 MiB/s at 1 MiB. So the second container's figure is the function without its instructions, and not a slower machine.

In the first column, BLAKE3's range is its own kernel choice: 4 389 with AVX-512 enabled, and 2 225 with only AVX2.

One 16 MiB input, MiB/s:

| Function | With SHA extensions | Without |
|----------|--------------------:|--------:|
| SHA-256 | 1 353–1 425 | 386–391 |
| BLAKE3, one thread | 1 791–3 701 | 2 211–2 230 |
| BLAKE3, every processor | 6 373–13 329 | 7 640–7 716 |

## What the numbers say

**A captured byte is hashed with SHA-256 four times.** Two passes run over the plaintext: the content id and the whole-file hash. Two run over the sealed bytes: the blob's flat digest and its Merkle leaves ([05 §5](../specifications/repository-format/05-blob.md#5-sealing), [ADR-0065](adr/0065-merkle-commitment-and-chunk-possession.md)). Only the first two are the content hash. The other two are SHA-256 by their own specification, and changing the content hash would leave them as they are.

**With the SHA extensions, hashing is not what limits a capture.**

- SHA-256 runs at about 1.5 GiB/s on one thread, nearly four times NFR-PERF-007's 400 MB/s.
- The whole-file hash has to walk a file in order on one thread, whatever the pipeline around it does. It costs about 0.7 ms per MiB.
- On compressible data, zstd at level 3 is about ten times slower than that hash, and it is zstd that sets the pace.

**Without the SHA extensions, on incompressible data, hashing is what limits a capture.**

- SHA-256 falls to 359–435 MiB/s. That is about the target itself, since 400 MB/s is 381 MiB/s.
- Zstd gives up on incompressible input quickly and runs at about 2.6 GiB/s. AES-GCM runs at about 3.2 GiB/s.
- On such a machine, the whole-file hash alone holds one large incompressible file to about the target. The four passes together cost more processor time than every other stage combined.
- BLAKE3 would speed the two content-hash passes up by about seven times. It would do nothing for the other two.

**BLAKE3 is portable now.** Bodu's implementation is managed code, so it runs everywhere the recovery tool does, and it is already in the dependency closure. The portability half of ADR-0004's condition for revisiting has lapsed. The throughput half holds only on hardware without the SHA extensions, and only on incompressible data.

**Where that hardware sits.** The reference machine requires AES-NI and says nothing about the SHA extensions. The machines without them are older Intel generations. AMD's processors have had them since 2017, and Intel's desktop and server lines since 2021. ARM64 cores, Apple's among them, carry SHA-256 instructions in their cryptographic extension.

## What this does and does not say

- **Containers, not the reference machine.** The ratios between functions are what carry over. NFR-PERF-007's number is still untested, as the [phase-2 benchmarks](phase-2-benchmarks.md) already say.
- **Functions, not the pipeline.** How much of each pass lands on the critical path depends on where it runs. The phase-2 fourth run moved the content id off the capture's serial thread. The whole-file hash is still on it.
- **Nothing here reopens the choice on its own.** The owner kept SHA-256 with these numbers in hand. ADR-0004 Amendment 1 records why, and states what a change would cost: there is no profile field to switch through, so it would be a format change.

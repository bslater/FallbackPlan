# What else Bodu 1.0.0 offers that this repository hand-rolls

**Status:** recorded · **Taken:** 2026-09-26, against all six packages at 1.0.0 ·
**Audience:** FallbackPlan contributors, and whoever next proposes taking
something from upstream

Not an adoption. A list, taken from the public type surface of all six packages
at 1.0.0 — `Bodu.Core`, `Bodu.Security.Cryptography`, `Bodu.Text.Encoding`,
`Bodu.Globalization.Recurrence`, `Bodu.Collections` and
`Bodu.Collections.Concurrent` — against what `src/` actually contains. Each row
says what a consumer would be taking on, because "upstream has one" is not by
itself a reason.

The surface was read from the XML documentation the packages ship, and every
claim below was then checked against the packages and the code rather than
left as read. Where reading was not enough it was settled another way, and the
row says how: the accumulator by a test that failed, `FillBlock` by
decompiling.

## Taken by this slice

| Bodu | Replaces |
|---|---|
| `Security.Cryptography.MerkleTree` — the tree, the length-bound root, the authentication path, the verifier and the block arithmetic — with `MerkleBlockComputation` for the leaf hashes of a buffer | The hand-rolled arithmetic of `Repository.Packing/BlobMerkle`, which is now `Repository.Crypto/BlobMerkle`: the format's leaf size, preimage and bound on a peer's path, bound over the library's tree ([ADR-0065](adr/0065-merkle-commitment-and-chunk-possession.md) Amendment 1, [ADR-0019](adr/0019-third-party-dependency-policy.md) Amendment 4) |

**Not taken: `MerkleBlockAccumulator`.** It re-blocks its input by copying it
into a buffer of one whole block — `new byte[blockSize + 1]`, unpooled, per
instance — and hashes each block in one call. At the format's leaf size that is
a fresh mebibyte on the large-object heap for every blob written, every spool
resumed and every possession challenge answered. The first attempt at this
slice used it, and `SpoolCheckpointTests.SpoolResume_ALargeSpool_IsWalkedWithoutBufferingIt`
refused it — 1,267,600 bytes allocated against a ceiling of 1,049,718, because
a resume is held to one record plus the reader's buffers (NFR-PERF-001). So
`BlobMerkleAccumulator` still streams each leaf into an incremental hash under
RFC 6962's leaf prefix, and hands the leaf hashes to the library for everything
above them. The library has no incremental leaf hash, which is the one request
this survey sends upstream
([Merkle requirements §12](bodu-merkle-requirements.md#12-open-questions-for-the-maintainer)).

## Real overlaps, not taken

| Bodu | What this repository has | Why not, for now |
|---|---|---|
| `Threading.AsyncLock`, `AsyncSemaphore`, `AsyncReaderWriterLock`, `AsyncManualResetEvent`, `AsyncCountdownEvent` | Thirteen files build their own coordination on `SemaphoreSlim` and `TaskCompletionSource` — `Agent/JobScheduler`, `Agent/PauseGate`, `Agent/Scheduler`, `Agent/ServiceRuntime` and `Api/Transport/LocalServiceClient` among them | The primitives are not the hard part; the *policies* are — the pool's priority, a park that carries its reason and ends at the max-pause cap, the standing background hold. Swapping the primitive under working, heavily tested scheduling buys nothing and risks a behaviour nobody asked to change. |
| `Threading.RateGate`, `AsyncDebouncer` | Nothing | Of genuine interest if NFR-PERF-013's **network** or **disk** limit is ever built; [ADR-0069](adr/0069-the-background-window.md) built the first of its four, the time window. Noted here so that slice does not start from scratch. |
| `Security.Cryptography.SecretBytes`, `Salt`, `Nonce`, `AuthenticationTag`, `SignatureValue` | `Repository.Crypto`'s own key and salt handling | These are `Bodu.Security.Cryptography` types, so taking them widens the surface the containment rule bounds ([ADR-0019](adr/0019-third-party-dependency-policy.md) §3) for ergonomics rather than for a primitive. Not worth it. |
| `Security.Cryptography.Hkdf`, `Text.Encoding.Base16` | The platform's `System.Security.Cryptography.HKDF` and `Convert.ToHexString` | Already served by the platform. A second implementation of an in-box primitive is exactly what [ADR-0019](adr/0019-third-party-dependency-policy.md) §2's first gate keeps out, and §4 already keeps hex on the platform. |

## Not overlaps, listed so they are not mistaken for gaps

- `Collections.Generic.Concurrent.ConcurrentLruCache` — nothing here is an LRU
  wearing a different name. The two candidates the names suggest are not
  caches: `Cli/SessionCache` is the CLI's persisted session token, one
  owner-only file, and `Restore/RestoreBlobSet` is a plan's list of the blobs it
  needs. Since [ADR-0068](adr/0068-the-catalogue-directed-restore-read.md) a
  restore opens no blob at all, and what it reads ahead is bounded in bytes by
  `Repository/PrefetchPolicy` — what a coalesced read may fetch, waste and hold —
  rather than by a count.
- `Extensions.StreamExtensions` — read-to-end and write-all helpers
  (`ReadAllBytes`, `WriteAllBytes` and their asynchronous forms), not fills. The
  draft of this survey paired it with `MerkleTree.FillBlock` against three
  "read-until-full" loops. `FillBlock` is private to the library, and only one
  of the three loops is a fill: `Repository/ArchiveSession` reading a segment
  until its buffer is full or the source ends. `Agent/RetrievalResponder`'s loop
  is a streaming pass over a replica, and `Filesystem.Local/SparseProbe` walks
  holes with `lseek` and reads nothing. The one fill is the platform's to serve
  — `Stream.ReadAtLeastAsync` with `throwOnEndOfStream: false` — not Bodu's.
- `Functional.Result`, `Option`, `Either` — expected outcomes here are typed
  results at the call site and faults are exceptions, by decision
  ([ADR-0012](adr/0012-storage-provider-contract.md) §2, which rejected result
  types for everything), not a general monad.
- `Collections.Probabilistic.BloomFilter`, `HyperLogLog`, `CountMinSketch` — a
  probabilistic answer is wrong for deduplication, where a false positive is a
  lost file.
- `Globalization.Recurrence.CronExpression` — already consumed:
  `Application/Schedule` is built on it and on `AnchoredInterval` from the same
  package ([Bodu recurrence requirements](bodu-recurrence-requirements.md)).
- The large cipher and hash catalogue — Serpent, Threefish, Skein, ML-KEM,
  ML-DSA, HPKE and the rest. This repository takes from the library only what
  .NET does not provide — Argon2id, X25519 and Ed25519 — and, as of this slice,
  the Merkle construction over the platform's own SHA-256. ADR-0019 §1 is about
  not acquiring more.

## The conclusion to record

One adoption, and it is the tree, not the accumulator — which produced one
request upstream: an incremental leaf hash, so a consumer can stream leaves
without holding one. One thing parked against a named future slice: the rate
gate, if NFR-PERF-013's network limit is built. And one small duplication worth
a look, which turned out to be the platform's rather than Bodu's:
`ArchiveSession`'s segment fill, which `Stream.ReadAtLeastAsync` already does.
Everything else upstream ships is either already consumed, already served by
the platform, or deliberately not wanted.

# ADR-0004 — Segment hash function

**Status:** Accepted (amended 2026-09) · Implemented — see [implementation status](../implementation-status.md#by-decision)
**Date:** 2026-08
**Requirements:** FR-ARCH-003, NFR-PERF-007, NFR-PORT-001

---

## Context

Every segment's plaintext content identifier is a cryptographic hash. It runs over **every byte the system ever reads**, so it sits directly on the throughput target (NFR-PERF-007: ≥ 400 MB/s single-stream including compression and encryption).

It also has to run everywhere. The standalone recovery tool must build and run on a clean machine on all three platforms with minimal dependencies — that is the entire point of it ([`../architecture/08-restore-and-recovery.md` §5](../architecture/08-restore-and-recovery.md#5-emergency-recovery)).

The identifier must be second-preimage resistant: an attacker who can produce a different plaintext with the same content identifier can substitute content into a backup that deduplicates against it.

## Decision

**SHA-256 as the v1 default**, selected through a profile field so another function can be added without a format break.

The profile is recorded per segment record and participates in the dedup index key, so segments hashed under different functions never falsely deduplicate against each other.

> **Corrected 2026-09 ([Amendment 1](#amendment-1-2026-09--sha-256-is-kept-and-there-is-no-profile-field-to-change-it-through)).** Neither of the two
> sentences above was ever true of the format. No durable object records a
> content-hash profile: not the record header, not the index, not a
> manifest and not the repository descriptor. So there is no field that
> selects another function, and a reader could not tell which function to
> check a segment against. SHA-256 is fixed by the format version, and
> adding another function is a format change.

## Rationale

The trade is throughput against portability, and portability wins for the default.

- **SHA-256 is in-box** in .NET on every platform, with SHA-NI hardware acceleration on modern x86-64 and equivalent on ARM64. No native binding, no platform-specific package, nothing extra for the recovery tool to carry.
- **BLAKE3 is substantially faster**, particularly on multi-core, and would help the throughput target. But the available .NET bindings wrap a native library, which adds a per-platform native dependency to the one component that must run everywhere — working directly against NFR-PORT-001 and against the recovery tool's minimal-dependency requirement.

The profile field means this is not a permanent commitment. If the Phase 0 benchmark shows SHA-256 is the binding constraint on NFR-PERF-007 — plausible on machines without SHA-NI — a BLAKE3 profile can be added and made the default for new writes, with existing repositories unaffected.

> **Amended 2026-09 ([Amendment 1](#amendment-1-2026-09--sha-256-is-kept-and-there-is-no-profile-field-to-change-it-through)).** Both premises of this
> section have moved. A managed BLAKE3 now exists, and it is already in the
> dependency closure, so the native-dependency objection no longer holds.
> And there is no profile field, so a BLAKE3 content hash would be a format
> change, not a profile added beside the default. The benchmark this
> paragraph waited on is [published](../segment-hash-benchmark.md), and the
> reasons SHA-256 is kept are now the amendment's.

## Consequences

**Positive**

- No native dependency in the core or the recovery tool.
- Hardware-accelerated on most current hardware.
- Universally available in every language an independent implementer might use, which matters for NFR-COMP-004.

**Negative**

- Slower than BLAKE3, especially on hardware without SHA-NI. If it becomes the throughput bottleneck, the answer is a new profile rather than a format change — but that is still work.

  > **Corrected 2026-09 ([Amendment 1](#amendment-1-2026-09--sha-256-is-kept-and-there-is-no-profile-field-to-change-it-through)).** It would be a format
  > change, because there is no profile field. The amendment also says when
  > SHA-256 is the bottleneck: on processors without the SHA extensions, on
  > incompressible data.

**Neutral**

- Truncation is not used: full 256-bit identifiers. Truncating to save catalogue space would weaken second-preimage resistance for a saving that NFR-PERF-011 does not require.

## Alternatives considered

**BLAKE3 as the default.** Deferred rather than rejected. Revisit if the Phase 0 benchmark shows hashing is the binding constraint, and if a managed or trimmable implementation removes the portability objection.

> **Declined 2026-09 ([Amendment 1](#amendment-1-2026-09--sha-256-is-kept-and-there-is-no-profile-field-to-change-it-through)).** Both conditions were
> measured. The second holds. The first holds only without the SHA
> extensions, and only on incompressible data. The owner kept SHA-256.

**SHA-512/256.** Faster than SHA-256 on 64-bit hardware *without* SHA-NI, slower with it. Rejected as a default because it is less universally available in other languages, and the machines it would help are the ones least likely to be the reference case.

**A non-cryptographic hash with cryptographic verification elsewhere.** Rejected. Deduplication decisions are made on this identifier, so a collision is a data-corruption path — exactly the failure mode [ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md) exists to close.

## Amendment 1 (2026-09) — SHA-256 is kept, and there is no profile field to change it through

**The decision stands, and it is accepted: SHA-256 is the content hash,** the
function behind both the content id and the whole-file hash. The owner took the decision with
the [measurement](../segment-hash-benchmark.md) in hand. Two things this record
said were wrong, and one of its reasons has lapsed. This section corrects them,
and the sections above carry notes scoped to each.

### 1. What was measured

The measurement is `PerformanceTests/HashThroughputBenchmark`, run with
`dotnet run -c Release -- hash-throughput`. Each figure is one thread over 1 MiB
inputs, on four-processor Xeon containers. The first container exposed the
processor's SHA extensions and the second did not.

| MiB/s, one thread | With SHA extensions | Without |
|-------------------|--------------------:|--------:|
| SHA-256 | 1 487–1 520 | 359–435 |
| BLAKE3, managed | 2 225–4 389 | 2 619–2 762 |
| zstd level 3, text-like | 155–164 | 121–139 |

- **With the SHA extensions, hashing is not the binding constraint.** SHA-256
  runs at nearly four times NFR-PERF-007's 400 MB/s. On compressible data, zstd
  is about ten times slower than it.
- **Without them, on incompressible data, it is.**
  - The whole-file hash has to walk a file in order on one thread.
  - At 359–435 MiB/s, that hash alone holds one large incompressible file to
    about the target (400 MB/s is 381 MiB/s), before anything else on that
    thread is counted.
- **The reference machine does not rule this out.** It requires AES-NI and
  says nothing about the SHA extensions. The processors without them are older
  Intel generations.

### 2. Why SHA-256 is kept anyway

- **The gain is bounded.** The content hash is two of the four SHA-256 passes
  a captured byte receives. The blob's flat digest and its Merkle leaves are
  SHA-256 by their own specification
  ([05 §5](../../specifications/repository-format/05-blob.md#5-sealing),
  [ADR-0065](0065-merkle-commitment-and-chunk-possession.md)), and they would
  stay.
- **Changing it is a format change.** There is no field to switch through
  (§3), so a second function needs a format version and every reader taught
  to recognise it.
- **The faster candidate would be an exception to the dependency policy.** The
  portability objection that deferred BLAKE3 is gone: `Bodu.Security.Cryptography`
  carries a managed implementation, and Repository.Crypto already references
  that package. But the platform has no BLAKE3, so it would enter as an
  exception under [ADR-0019 §3](0019-third-party-dependency-policy.md#3-where-the-platform-provides-nothing-the-gap-is-named).
  - That means it is contained, cross-verified against a second implementation
    and covered by the external review.
  - All of that would be for the function that decides what is stored once
    and what restore verifies against. A defect there is in the user's data
    before any test sees it.
  - SHA-256 is the platform's own, and needs none of it.
- **SHA-256 is everywhere.** It is in every language an independent reader
  might use (NFR-COMP-004), and in the platform on every system the recovery
  tool runs on.
- **The case that pays is narrow.**
  - The data must be incompressible, on an older Intel machine.
  - Where it falls short, it falls short of the target by a few percent, not
    by a multiple.
  - The hardware it applies to is shrinking.

### 3. Corrections

1. **There is no content-hash profile field.** The Decision said the profile
   "is recorded per segment record and participates in the dedup index key".
   Neither was ever specified or built.
   - The fields that are recorded:
     - the record header's compression and encryption profiles
       ([04 §2](../../specifications/repository-format/04-record.md#2-framing));
     - the policy manifest's segmentation, compression, encryption and
       blob-write profiles
       ([06 §7](../../specifications/repository-format/06-manifests.md#7-policy-manifest));
     - the descriptor's KDF profile
       ([01 §3](../../specifications/repository-format/01-object-layout.md#3-the-repository-descriptor)).
   - No object anywhere carries a content-hash profile.
   - `Domain/Profiles/ContentHashProfile` names the one value, and nothing
     reads it.
   - The specification said the same thing at 02 §2.1 and 00 §3. Each now
     carries an erratum.
2. **So "without a format break" was never available.** A second function
   needs something that says which function a repository uses, a field or a
   version, and a reader that refuses what it does not know. That is a format
   change.
3. **The native-dependency objection has lapsed,** as §2 says. The decision
   no longer rests on it.

### 4. What would reopen it

- **The trigger is a reference-case measurement.** It would have to show
  NFR-PERF-007 failing on incompressible data, with the whole-file hash as the
  measured bound.
- **The moment to act is a format version cut for another reason.** That is
  the cheapest time, because the new field would ride that version.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Confirm against Phase 0 throughput benchmark |
| 2026-09 | Accepted (amended) | Amendment 1: confirmed against the [hash benchmark](../segment-hash-benchmark.md) and kept by the owner's decision. SHA-256 is not the binding constraint where the SHA extensions exist. Without them, on incompressible data, it is, and the owner accepted that. Corrects the record's claim of a profile field: none exists, so another function is a format change. Records that the managed BLAKE3 now available removes the portability objection without changing the choice. |

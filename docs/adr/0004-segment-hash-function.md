# ADR-0004 — Segment hash function

**Status:** Proposed
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

## Rationale

The trade is throughput against portability, and portability wins for the default.

- **SHA-256 is in-box** in .NET on every platform, with SHA-NI hardware acceleration on modern x86-64 and equivalent on ARM64. No native binding, no platform-specific package, nothing extra for the recovery tool to carry.
- **BLAKE3 is substantially faster**, particularly on multi-core, and would help the throughput target. But the available .NET bindings wrap a native library, which adds a per-platform native dependency to the one component that must run everywhere — working directly against NFR-PORT-001 and against the recovery tool's minimal-dependency requirement.

The profile field means this is not a permanent commitment. If the Phase 0 benchmark shows SHA-256 is the binding constraint on NFR-PERF-007 — plausible on machines without SHA-NI — a BLAKE3 profile can be added and made the default for new writes, with existing repositories unaffected.

## Consequences

**Positive**

- No native dependency in the core or the recovery tool.
- Hardware-accelerated on most current hardware.
- Universally available in every language an independent implementer might use, which matters for NFR-COMP-004.

**Negative**

- Slower than BLAKE3, especially on hardware without SHA-NI. If it becomes the throughput bottleneck, the answer is a new profile rather than a format change — but that is still work.

**Neutral**

- Truncation is not used: full 256-bit identifiers. Truncating to save catalogue space would weaken second-preimage resistance for a saving that NFR-PERF-011 does not require.

## Alternatives considered

**BLAKE3 as the default.** Deferred rather than rejected. Revisit if the Phase 0 benchmark shows hashing is the binding constraint, and if a managed or trimmable implementation removes the portability objection.

**SHA-512/256.** Faster than SHA-256 on 64-bit hardware *without* SHA-NI, slower with it. Rejected as a default because it is less universally available in other languages, and the machines it would help are the ones least likely to be the reference case.

**A non-cryptographic hash with cryptographic verification elsewhere.** Rejected. Deduplication decisions are made on this identifier, so a collision is a data-corruption path — exactly the failure mode [ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md) exists to close.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Confirm against Phase 0 throughput benchmark |

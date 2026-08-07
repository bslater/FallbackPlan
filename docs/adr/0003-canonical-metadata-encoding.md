# ADR-0003 — Canonical metadata encoding

**Status:** Proposed · Implemented — see [implementation status](../implementation-status.md#by-decision)
**Date:** 2026-08
**Requirements:** NFR-COMP-004, NFR-PORT-003, FR-MAN-004

---

## Context

Every repository metadata object — segment references, file-version manifests, trees, snapshots, policies, index deltas — needs a **canonical** encoding: the same logical input must produce byte-identical output in every implementation, on every platform, forever.

Canonicality is not a nicety here. Object identifiers are derived from encoded bytes, so two implementations that encode the same manifest differently produce different identifiers for the same object, and deduplication, verification, and conformance testing all break. This is the property NFR-COMP-004 depends on: an independent reader in another language must agree with us byte for byte.

The proposal named canonical CBOR as the leading candidate, subject to benchmarks and conformance review.

## Decision

**Canonical CBOR** (deterministic encoding per RFC 8949 §4.2), pending confirmation by:

1. **Cross-language determinism tests** — at minimum a second implementation in a different language producing byte-identical output for the fixture corpus.
2. **Size benchmark** on realistic manifests at reference scale **M**, since metadata size directly drives NFR-PERF-011 (catalogue bytes per file version).

Wire protocols are versioned independently and may use a different encoding — Protocol Buffers for gRPC control operations is expected. The repository format does not inherit that choice.

## Rationale

CBOR gives us a self-describing binary encoding with a *specified* deterministic profile, mature independent implementations across languages, compact integers and byte strings, and no schema-compiler dependency in the recovery tool — which matters, because the recovery tool must build and run with the fewest possible moving parts.

`System.Formats.Cbor` is available for .NET and provides explicit conformance-mode control, which is what makes the deterministic profile enforceable rather than aspirational.

## Consequences

**Positive**

- Deterministic encoding is specified rather than invented, so an independent implementer has a normative reference and not just our code.
- No IDL or code generation in the recovery path.
- Fixtures are byte-comparable, which makes conformance testing meaningful.

**Negative**

- CBOR's flexibility means the deterministic profile must be *enforced* — a decoder that silently accepts non-canonical input undermines the whole property. Encoder and decoder both need conformance-mode assertions, and the fuzz suite must include non-canonical inputs that must be rejected.
- Slightly larger than a schema-driven format like Protocol Buffers for the same data, since keys are encoded.

## Alternatives considered

**Protocol Buffers.** Rejected for repository objects: proto3 serialisation is explicitly *not* canonical — field ordering and default handling vary across implementations and versions — so identical logical input can produce different bytes. Fine for wire protocols where identifiers are not derived from bytes; unusable where they are.

**A bespoke binary format.** Rejected. Full control over canonicality, but every independent implementer must reimplement it from our prose with no existing library, which works directly against NFR-COMP-004.

**JSON / JCS.** Rejected. Canonicalisation is specified, but number handling is a persistent source of cross-language disagreement and the size cost at scale **L** is unacceptable for metadata.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Confirm via cross-language determinism tests and size benchmark before format v1 freeze |

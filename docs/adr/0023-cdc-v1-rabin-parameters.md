# ADR-0023 — cdc-v1 Rabin fingerprint parameters

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-ARCH-014, FR-DED-001, NFR-COMP-004
**Related:** [ADR-0002](0002-segmentation-strategy.md), [ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md), [specification 09 §3](../../specifications/repository-format/09-segmentation.md#3-cdc-v1)

---

## Context

Specification 09 §3.1 defines `cdc-v1` completely except for the rolling hash itself: "The polynomial and the per-byte table are **not yet pinned**", and until they are, "a writer MUST NOT use `cdc-v1` in a repository intended to be portable". Two implementations with different tables produce different boundaries and deduplicate against nothing, so the pin is a portability prerequisite, named as phase-0 item B6 and enforced today by the engine refusing `cdc-v1` policies outright.

Everything else is already normative: 64-bit rolling hash, 64-byte window, boundary at `(hash & mask) == 0 && current_length ≥ min_size`, forced boundary at `max_size`, `mask = target_size − 1` with power-of-two target, defaults 1 MiB / 256 KiB / 8 MiB.

This ADR pins the remaining free choices. The choices are **arbitrary within their constraints** — any irreducible polynomial and any consistent conventions would deduplicate equally well; what matters is that every implementation uses the *same* ones, that they are reproducible from a short written rule, and that the conformance vectors freeze them.

## Decision

### 1 The fingerprint

`cdc-v1`'s rolling hash is a **Rabin polynomial fingerprint over GF(2) with a 64-bit state**. The modulus is:

```text
P(x) = x⁶⁴ + x⁴ + x³ + x + 1
```

recorded in the vectors as `"polynomial": "0x000000000000001b"` — the low 64 coefficient bits, with the x⁶⁴ term implicit. This is the standard low-weight irreducible polynomial defining GF(2⁶⁴) (the same modulus family used by GCM's GHASH field, reversed convention aside), chosen for exactly two properties: it is irreducible over GF(2) (so the fingerprint is a proper field reduction with maximal period, not a degenerate ring), and it is universally documented (so an independent implementer can verify the choice from the literature rather than trusting this repository). The conformance generator asserts irreducibility at run time with a pure-Python Rabin irreducibility test, so the pinned constant can never silently drift from the property that justifies it.

### 2 Definition of the hash

The fingerprint at position *p* (the zero-based offset of the most recently consumed byte) is defined over the **64 bytes ending at and including *p***:

```text
H(p) = ( b[p−63]·x^(8·63) + b[p−62]·x^(8·62) + … + b[p−1]·x^8 + b[p] ) mod P(x)
```

— the window's bytes interpreted most-significant-first as coefficients of a GF(2) polynomial, reduced mod P. Conventions this fixes:

- **Warm-up:** `H(p)` is undefined for `p < 63`; no boundary may be declared there. This can never matter in a conforming configuration, because `min_size ≥ target_size / 8 ≥ 8 KiB` (09 §3.1) is far beyond the 64-byte window — the constraint is stated so that an implementation with an incremental state need not special-case the file head: initialise the state to zero and push bytes; boundary tests only begin once `current_length ≥ min_size`.
- **The window never resets.** It rolls continuously across declared segment boundaries — including forced `max_size` boundaries — because a boundary must be a pure function of the local 64 bytes, not of where previous boundaries fell. That locality is the entire §3.2 resynchronisation property: an insertion perturbs only the boundaries whose windows overlap it.
- **Inclusive evaluation:** a segment **ends at byte *p*** (i.e. `length = p − segment_start + 1`) when `(H(p) & mask) == 0` and `length ≥ min_size`, or unconditionally when `length == max_size`. The final segment of a file is emitted as-is, however short — the same rule as `fixed-v1` (09 §2.1).

### 3 The tables

An implementation maintains the rolling state with two 256-entry u64 tables, both **derived from P alone** by GF(2) arithmetic:

```text
push_table[b] = ( b · x⁶⁴ )      mod P(x)     — reduces the byte shifted out of the state's top
pop_table[b]  = ( b · x^(8·64) ) mod P(x)     — removes the byte leaving the 64-byte window
```

One rolling step, consuming `incoming` and evicting `outgoing` (the byte 64 positions back):

```text
H ← ( (H << 8) ⊕ push_table[H >> 56] ) ⊕ incoming ⊕ pop_table[outgoing]
```

The tables are not independent constants: they are a deterministic function of P, and the conformance vectors commit **the polynomial, this derivation rule, and both computed tables**, so an implementation may either regenerate the tables from the rule or embed the committed values — the vectors prove both routes agree, and `segmentation.json` remains honestly `independently_derived: true` because everything in it is computed from the written rule with no reference implementation involved.

### 4 Vectors

`segmentation.json`'s `cdc_v1` placeholder ("parameters not yet pinned") is replaced by computed cases using reduced parameters (target 64 KiB, min 8 KiB, max 512 KiB — small enough to exercise every rule in kilobytes instead of megabytes):

| Case | What it pins |
|------|--------------|
| `empty` | Zero-length input produces zero segments |
| `all_zeros_1mib` | The zero window gives `H = 0` everywhere, so every boundary test passes and every segment is exactly `min_size` — the sharp edge of the `min_size` rule |
| `deterministic_stream` | `SHA-256(u64(0)) ‖ SHA-256(u64(1)) ‖ …` — statistically random, pure-stdlib-reproducible; pins ordinary boundary placement |
| `insertion_resync` | The same stream with one byte prepended; the builder asserts boundaries realign after a bounded prefix — the §3.2 property, checked, not assumed |
| `max_size_forcing` | A low-entropy repeat crafted to never satisfy the mask, forcing `max_size` cuts |

## Consequences

**Positive**

- `cdc-v1` becomes portable: the engine's refusal (`segmentation_cdc_parameters_not_pinned`) is lifted, and two independent implementations of the written rule produce identical boundaries, checked by committed vectors.
- Everything is derivable from one constant and three short rules — nothing in the pin requires trusting this repository's binaries.

**Negative**

- The choice is now frozen: changing polynomial or conventions after repositories exist is a new segmentation profile (`cdc-v2`), never a revision — the same discipline as every other profile (00 §3).
- Rabin fingerprinting is the classical choice, not the fastest known (gear/buzhash variants trade table structure for speed). Accepted: 09 §3.1 names a Rabin-style fingerprint, and profile versioning leaves the door open for a faster profile later without disturbing this one.

## Alternatives considered

**A gear-hash / FastCDC-style function.** Faster in most published measurements, but it is a different algorithm family from the "Rabin-style polynomial fingerprint" the specification names — adopting it would be a spec change, not a parameter pin, and its 256-entry random table is a bag of arbitrary constants with no verifiable property, where the Rabin table is derived from one checkable polynomial.

**A random irreducible polynomial generated for this format.** Rabin's original scheme draws the polynomial at random per deployment; a format needs one fixed value, and a bespoke random constant would be unverifiable-by-literature. A standard published polynomial gives the same field with provenance.

**Zero-padding the window at file start (defining `H(p)` for `p < 63`).** Functionally indistinguishable in conforming configurations (min_size makes early boundaries impossible) but forces every implementation to document the padding; leaving the warm-up undefined states the truth — those positions can never be boundaries — with no extra convention.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Polynomial, tables, and evaluation conventions pinned; vectors added; phase-0 item B6 |

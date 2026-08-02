# ADR-0005 — AEAD suite and nonce construction

**Status:** Proposed
**Date:** 2026-08
**Requirements:** NFR-SEC-002, NFR-SEC-003, FR-ARCH-008, FR-ARCH-009, FR-ARCH-011
**Review finding:** [C2](../review/2026-08-architecture-review.md#c2--nonce-uniqueness-is-asserted-but-never-constructed)

---

## Context

NFR-SEC-003 required nonce uniqueness "guaranteed by construction … across concurrent writers and resumed operations". The proposal restated that goal in §7.6 and gave no construction.

The requirement is right, and it is the one place in the system where a mistake is unrecoverable rather than expensive. Under AES-256-GCM, a single repeated `(key, nonce)` pair leaks the XOR of two plaintexts and permits recovery of the GHASH authentication subkey — after which an attacker can forge arbitrary authenticated records.

The architecture creates two independent routes to that:

1. **Concurrent writers.** Direct-store mode permits many writers sharing a data-key generation with no coordination channel. A counter needs partitioning; random 96-bit nonces need a birthday-bound budget nobody will track.
2. **Resumed spools.** FR-ARCH-011 explicitly resumes interrupted blob construction from a checkpoint. Any nonce sequence derived from something that resets — a session counter, a timestamp, a per-job counter — re-emits a nonce under the same key on replay. This is a *designed-for* path, not an accident, which makes it the likelier failure in practice.

## Decision

### Suite

**AES-256-GCM** where hardware AES is available; **XChaCha20-Poly1305** otherwise. Both are approved profiles, recorded per record. Unapproved suites are rejected at configuration time, not at write time.

### Construction

```text
blob_salt  ← 256 bits from a CSPRNG, drawn once per blob,
             stored in the blob's cleartext envelope

blob_key   ← HKDF-Expand(
                 PRK  = data_key[generation]   (or metadata_key[generation]),
                 info = "fbp/blob/v1" ‖ blob_salt,
                 L    = 32)

nonce(i)   ← 96-bit big-endian ordinal of record i within the blob (0, 1, 2, …)

AAD(i)     ← repository_id ‖ format_version ‖ object_type ‖ object_id ‖ i
```

Every blob has its own key. Nonce uniqueness therefore only has to hold **within a single blob**, where exactly one writer owns a strictly increasing ordinal.

## Why this works

**Concurrent writers cannot collide** — they hold different keys. Two writers draw independent 256-bit salts, so key collision is negligible and no coordination is needed because there is nothing to coordinate.

**Resumption is idempotent, not catastrophic.** The salt lives in the durable spool checkpoint. A *resumed* blob replays the same `(blob_salt, ordinal)` pairs under the same key, producing byte-identical output — bit-for-bit what the interrupted blob would have been. A *restarted* blob draws a fresh salt and is therefore a different key, so ordinals beginning again at zero reuse nothing.

That single distinction — resume reads the salt, restart draws one — is what converts the most dangerous path in the system into a safe one.

**Relocation fails authentication.** Binding repository, format version, object type, object identifier, and ordinal as AAD means a record cannot be moved between blobs, positions, object types, or repositories.

## Consequences

**Positive**

- No cross-writer coordination, no nonce budget to track, no counter partitioning.
- Resumption is safe by construction rather than by careful implementation.
- Compromise of one blob key exposes one blob.

**Negative**

- One HKDF expansion per blob. Negligible against 128 MiB of payload.
- 32 bytes of salt per blob in the cleartext envelope.
- The spool checkpoint must persist the salt. If it does not, resume is impossible and the blob must be restarted — which is *safe*, merely wasteful. The failure mode is the harmless one.

## Test obligations

These are requirements on the suite, not aspirations:

- property test: no `(key, nonce)` pair repeats across any generated write sequence;
- interruption test: resume-after-kill at every record boundary produces byte-identical blobs;
- interruption test: restart-after-kill always yields a different blob salt;
- concurrency test: *N* writers produce pairwise-distinct blob salts;
- negative test: a record moved between blobs, ordinals, or repositories fails authentication.

## Alternatives considered

**Random 96-bit nonces under a shared key.** Rejected. Requires a birthday-bound budget tracked across all writers and all time; a repository is long-lived and nobody will track it.

**Counter partitioned by writer ID.** Rejected. Needs durable per-writer counter state that survives crashes and clones, and a cloned device reuses its partition — reintroducing the exact failure.

**XChaCha20-Poly1305 with random 192-bit nonces throughout.** Viable — the extended nonce makes random selection safe. Rejected as the sole scheme because AES-GCM is significantly faster on hardware with AES-NI, and the per-blob construction makes both suites safe uniformly rather than making safety a property of the suite choice.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Requires external cryptographic review before format v1 freeze |

# ADR-0005 — AEAD suite and nonce construction

**Status:** Accepted (amended 2026-08 after [pressure test](../review/2026-08-fix-pressure-test.md))
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
                 info = "fbp/blob/v1" ‖ blob_salt ‖ writer_id ‖ blob_counter,
                 L    = 32)

nonce(i)   ← 96-bit big-endian ordinal of record i within the blob (0, 1, 2, …)

AAD(i)     ← repository_id ‖ format_version ‖ object_type ‖ object_id ‖ i
```

Every blob has its own key. Nonce uniqueness therefore only has to hold **within a single blob**, where exactly one writer owns a strictly increasing ordinal.

### Amendment 1 — writer identity is bound into the derivation

`writer_id` and `blob_counter` were added after the pressure test. The original construction rested key separation entirely on CSPRNG quality, and a cloned VM or early-boot embedded device can replay RNG state and draw the same salt twice. Binding writer identity and a monotonic per-writer blob counter means a collision would additionally require the same writer at the same counter value; the counter comes from the journal sequence, which is gapless, monotonic, and protected against cloning by [T-18](../threat-model.md#t-18-writer-identity-cloning). It costs nothing and removes a dependency on hardware we do not control ([PT-13](../review/2026-08-fix-pressure-test.md#pt-13--blob-salt-uniqueness-rests-entirely-on-csprng-quality-and-vm-cloning-defeats-that)).

### Amendment 2 — the spool checkpoint stores sealed bytes, not a plaintext offset

**This amendment is load-bearing. Without it the ADR ships the failure it was written to prevent.**

The original text argued that a resumed spool "replays the same plaintext at the same ordinal under the same key" and therefore produces byte-identical output. The input to the AEAD is not the segment's plaintext, however — it is the plaintext *after compression*, and recompression is not guaranteed reproducible. Zstandard is deterministic for a given library version and parameter set and offers no guarantee across versions.

An agent that crashed, was upgraded, and resumed would therefore recompress into different bytes and encrypt them under the same `(blob_key, ordinal)` — nonce reuse, with XOR leakage and GHASH subkey recovery, requiring nothing more exotic than a crash and an unattended update ([PT-1](../review/2026-08-fix-pressure-test.md#pt-1--c2s-resume-guarantee-silently-assumes-bit-reproducible-compression)).

The checkpoint therefore persists **the sealed record bytes**, and resume re-emits them rather than recomputing. Byte-identity becomes a property of the checkpoint rather than an assumption about a third-party codec. Everything else that could vary between crash and resume — blob salt, writer ID and counter, segmentation profile and parameters, compression codec **and version**, encryption profile — is pinned in the checkpoint, and any mismatch forces a **restart** instead of a resume.

## Why this works

**Concurrent writers cannot collide** — they hold different keys. Two writers draw independent 256-bit salts and are additionally separated by writer ID and counter, so no coordination is needed because there is nothing to coordinate.

**Resumption is idempotent, not catastrophic.** A *resumed* blob re-emits the sealed bytes recorded in the checkpoint — bit-for-bit what the interrupted blob would have been. A *restarted* blob draws a fresh salt and is therefore a different key, so ordinals beginning again at zero reuse nothing.

That single distinction — resume replays stored bytes, restart draws a new salt — is what converts the most dangerous path in the system into a safe one. **Restart is always the safe failure, and the engine prefers it whenever anything is in doubt.**

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
- interruption test: **resume with a changed compression codec version re-emits checkpointed bytes or refuses to resume** — it never recompresses under an already-used ordinal;
- interruption test: restart-after-kill always yields a different blob salt;
- concurrency test: *N* writers produce pairwise-distinct blob salts;
- concurrency test: two writers seeded with an **identical CSPRNG stream** still derive distinct blob keys;
- negative test: a record moved between blobs, ordinals, or repositories fails authentication.

## Alternatives considered

**Random 96-bit nonces under a shared key.** Rejected. Requires a birthday-bound budget tracked across all writers and all time; a repository is long-lived and nobody will track it.

**Counter partitioned by writer ID.** Rejected. Needs durable per-writer counter state that survives crashes and clones, and a cloned device reuses its partition — reintroducing the exact failure.

**XChaCha20-Poly1305 with random 192-bit nonces throughout.** Viable — the extended nonce makes random selection safe. Rejected as the sole scheme because AES-GCM is significantly faster on hardware with AES-NI, and the per-blob construction makes both suites safe uniformly rather than making safety a property of the suite choice.

## Amendment 3 — key wrapping is fixed to AES-256-GCM

A specification review found that the key object's fixed 12-byte `wrap_nonce` field cannot represent `xchacha20-poly1305-v1`, whose nonce is 24 bytes — the layout admitted a profile it could not encode. Format v1 therefore fixes `kek_profile` to `aes-256-gcm-v1`.

The wrap happens once per repository open, so the hardware-acceleration argument for offering a second suite does not apply here.

*Rejected alternative:* a variable-length nonce field sized by `kek_profile`. It makes every subsequent offset in a fixed-layout header depend on a profile lookup, for the benefit of a profile that did not survive the freeze (Amendment 4).

## Amendment 4 — the extended-nonce profile is withdrawn

**Format version 1 admits one record AEAD, `aes-256-gcm-v1`.** Value `0x0002` is reserved and MUST NOT be assigned to another suite.

Amendment 3 found that the key object could not *wrap* under the extended-nonce profile. That was the smaller half. The deciding one is that no second independent implementation of XChaCha20-Poly1305 was available to cross-verify against, and cross-verification is the condition on which this project admits a third-party cryptographic primitive at all — it is how the Argon2id empty-passphrase gap was found.

An unverified KDF makes keys weaker. An unverified AEAD can make ciphertext forgeable or, with a nonce-handling error, plaintext recoverable — and the discovery happens inside bytes the user already stored. Shipping it flagged was therefore the option that aged worst: a format version can add a profile later, but it cannot un-admit one that written repositories depend on.

The cost is accepted and named: on hardware without AES acceleration, a ChaCha-family suite would be faster. Nothing had written the profile, so withdrawing costs that and nothing else. A future version MAY admit such a suite under a new value, with a second implementation to check it against as the condition of entry.

The value stays reserved rather than freed. Draft repositories and draft readers understood `0x0002` as XChaCha20-Poly1305, and a value meaning one thing in a draft and another in the frozen format is precisely what a version number cannot repair. → [Q12](../open-questions.md#closed), [specification 03 §6.1](../../specifications/repository-format/03-keys.md#61-where-each-primitive-comes-from)

*Rejected alternative:* take a third-party XChaCha20-Poly1305 and ship it unverified with a warning. Rejected on the asymmetry above — the warning does not travel with the bytes.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Requires external cryptographic review before format v1 freeze |
| 2026-08 | Accepted (amended) | Construction unchanged. Amendment 1 binds writer identity into derivation (PT-13); Amendment 2 makes the spool checkpoint store sealed bytes (PT-1, critical). External cryptographic review still required before freeze, and must cover AES-GCM key commitment (PT-15). |
| 2026-08 | Accepted (amended) | Amendment 3 fixes KEK wrapping to `aes-256-gcm-v1` — the 12-byte `wrap_nonce` field cannot carry the extended-nonce profile. |
| 2026-08 | Accepted (amended) | Amendment 4 withdraws `xchacha20-poly1305-v1` before the freeze: no second implementation existed to cross-verify it, and an unverified AEAD cannot be un-admitted once repositories depend on it. Format v1 has one record AEAD; `0x0002` is reserved. Closes [Q12](../open-questions.md#closed). |

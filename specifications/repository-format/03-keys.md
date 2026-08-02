# 03 — Keys

**Normative.** Derived from [`03-crypto.md` §2–3](../../docs/architecture/03-crypto.md#2-key-hierarchy) and [ADR-0005](../../docs/adr/0005-aead-suite-and-nonce-construction.md).

---

## 1 Hierarchy

```text
passphrase ──Argon2id──▶ key-encryption key (KEK)
                              │
                              │ wraps
                              ▼
                      repository master key (32 bytes, random at creation)
                              │
                              │ HKDF-Expand, domain-separated
              ┌───────────────┼───────────────┬───────────────┬──────────────┐
              ▼               ▼               ▼               ▼              ▼
        content-ID key   data key[gen]  metadata key[gen]  signing key   key-ID key
              │               │               │
              │               └───────┬───────┘
              ▼                       ▼
      object identifiers        per-blob keys
```

The master key is never used to encrypt anything directly. Every key that touches data is derived from it with explicit domain separation, so a compromise of one derived key does not extend to the others.

## 2 Key-encryption key

```text
KEK = Argon2id(
          password    = passphrase (UTF-8, NFC-normalised, no trailing newline),
          salt        = kdf_parameters.salt,
          memory      = kdf_parameters.memory_kib,
          iterations  = kdf_parameters.iterations,
          parallelism = kdf_parameters.parallelism,
          tag_length  = 32)
```

Parameters come from the repository descriptor ([01 §3.3](01-object-layout.md#33-kdf-parameters)) and are public.

Minimum acceptable parameters for a new repository: **64 MiB memory, 3 iterations, parallelism 4**. A writer MUST NOT create a repository below these. A reader MUST accept lower values in an existing repository — refusing would make an old repository unrecoverable, which is a worse outcome than a weaker KEK — but SHOULD warn.

### 2.1 The passphrase is constrained too, and the primitive will not do it for you

Argon2id accepts a **zero-length password**. RFC 9106 permits it, and an implementation will happily derive a key from nothing.

A writer MUST reject an empty passphrase and SHOULD enforce a minimum length, refusing rather than warning.

This is stated explicitly because the cross-implementation testing behind §6.2 found that two Argon2id implementations disagree on precisely this boundary — one refuses an empty password, the other accepts it. Relying on either behaviour would be relying on an accident of which library was linked, and the parameter minimums above say nothing about the input those parameters are applied to.

Passphrase normalisation matters: the same passphrase typed on macOS and Linux can differ in Unicode composition, and an un-normalised comparison would make a repository unopenable on the other platform. NFC is applied before UTF-8 encoding.

## 3 The key object

`/keys/<key-id>` holds the wrapped master key.

```text
offset  size   field
------  -----  -------------------------------------------------------------
     0      8  magic         = 0x46 42 50 4B 4B 45 59 53   ("FBPKKEYS")
     8      2  format_version u16
    10      2  kek_profile    u16  (AEAD suite used for wrapping, §5)
    12     12  wrap_nonce     bytes[12]
    24     16  key_id         bytes[16]
    40      4  cbor_length    u32, max 4 096
    44      N  wrapped        AEAD ciphertext of the CBOR key bundle
  44+N     16  wrap_tag       AEAD authentication tag
```

Unwrapping uses:

```text
AAD = magic ‖ format_version ‖ kek_profile ‖ key_id
```

so the wrapped bundle cannot be moved to a different key object or a different repository without authentication failing.

A failed unwrap means the passphrase is wrong **or** the object has been tampered with, and a reader MUST NOT try to distinguish the two in a message to the user. Distinguishing them would confirm a correct passphrase to an attacker holding a modified key object.

### 3.1 Key bundle

| Key | Type | Value |
|-----|------|-------|
| 1 | bytes[32] | `master_key` |
| 2 | u32 | `current_data_generation` |
| 3 | u32 | `current_metadata_generation` |
| 4 | u64 | `created_at` |

## 4 Derived keys

All derivation uses **HKDF-Expand** ([RFC 5869](https://www.rfc-editor.org/rfc/rfc5869) §2.3) with HMAC-SHA256, taking the master key directly as the pseudorandom key. The extract step is omitted because the master key is already 32 uniformly random bytes; extracting again would add nothing.

```text
derive(info) = HKDF-Expand(PRK = master_key, info = info, L = 32)
```

| Key | `info` |
|-----|--------|
| Content-ID key | `"fbp/content-id/v1"` |
| Key-ID key | `"fbp/key-id/v1"` |
| Data key, generation *g* | `"fbp/data/v1" ‖ u32(g)` |
| Metadata key, generation *g* | `"fbp/metadata/v1" ‖ u32(g)` |
| Signing key, generation *g* | `"fbp/signing/v1" ‖ u32(g)` |

Info strings are ASCII, without a terminating NUL. Domain separation is by the string, not by chance.

### 4.1 Generations

Data and metadata keys are generational. Introducing a new generation lets a repository migrate to a new key without rewriting existing objects: old objects remain readable under the old generation, and new writes use the new one.

A blob records the generation it used in its cleartext envelope ([05 §2](05-blob.md#2-cleartext-envelope)), so a reader always knows which to derive.

## 5 Per-blob keys

**This is the construction the format's confidentiality rests on.** It is given in full.

```text
blob_salt = 32 bytes from a CSPRNG, drawn once per blob

blob_key  = HKDF-Expand(
                PRK  = data_key[generation]          (or metadata_key[generation]),
                info = "fbp/blob/v1" ‖ blob_salt ‖ writer_id ‖ u64(blob_counter),
                L    = 32)
```

`blob_salt`, `writer_id` and `blob_counter` are all stored in the blob's cleartext envelope, so a reader can reproduce the derivation.

### 5.1 Why per-blob keys

Every blob having its own key means **nonce uniqueness only has to hold within a single blob**, where exactly one writer owns a strictly increasing record ordinal.

The alternative — one key for many blobs — requires either partitioning a counter across writers who have no coordination channel, or drawing random nonces and tracking a birthday-bound budget across the repository's whole lifetime. Neither survives contact with a system where any number of devices may write concurrently and unattended.

Under this construction, two concurrent writers cannot collide because they hold different keys. There is nothing to coordinate. → [ADR-0005](../../docs/adr/0005-aead-suite-and-nonce-construction.md)

### 5.2 Why writer identity is in the derivation

A 32-byte CSPRNG salt makes collision negligible — provided the CSPRNG is sound. That proviso does not hold universally: a cloned virtual machine, a restored VM snapshot, or an embedded device early in boot can replay RNG state and draw the same salt twice.

Binding `writer_id` and `blob_counter` means a collision would additionally require the same writer at the same counter value. The counter comes from the journal sequence, which is gapless, monotonic, and protected against cloning by the writer-identity conflict alert. This costs one concatenation and removes a dependency on hardware the format cannot inspect. → [PT-13](../../docs/review/2026-08-fix-pressure-test.md#pt-13--blob-salt-uniqueness-rests-entirely-on-csprng-quality-and-vm-cloning-defeats-that)

### 5.3 Blob keys are ephemeral

A blob key MUST NOT be stored. It is derived when the blob is written and re-derived when it is read. A compromise of one blob key exposes exactly one blob.

## 6 AEAD suites

| Profile | Value | Suite | Key | Nonce | Tag | Implementation |
|---------|-------|-------|-----|-------|-----|----------------|
| `aes-256-gcm-v1` | `0x0001` | AES-256-GCM | 32 | 12 | 16 | Platform (`System.Security.Cryptography.AesGcm`) |
| `xchacha20-poly1305-v1` | `0x0002` | XChaCha20-Poly1305 | 32 | 24 | 16 | **Third-party** — see §6.2 |

A writer SHOULD select `aes-256-gcm-v1` where hardware AES is available and `xchacha20-poly1305-v1` otherwise. Both are permitted; the profile is recorded per record.

### 6.2 Where each primitive comes from

Rule 1 in §1 says to use audited platform primitives and write none ourselves. That rule is satisfiable for most of what this format needs and **not** for all of it, so the position is stated plainly rather than left to be discovered.

| Primitive | Source | Status |
|-----------|--------|--------|
| SHA-256 | Platform | Audited, in-box |
| HMAC-SHA256 | Platform | Audited, in-box |
| HKDF-Expand | Platform (`HKDF`) | Audited, in-box |
| AES-256-GCM | Platform (`AesGcm`) | Audited, in-box |
| **Argon2id** | **Third-party** | No platform implementation exists |
| **XChaCha20-Poly1305** | **Third-party** | No platform implementation exists |

.NET provides `ChaCha20Poly1305` — RFC 8439, with a **12-byte** nonce. It does **not** provide the extended-nonce XChaCha20 variant, which takes 24 bytes. The two are not interchangeable, and an implementer who substitutes one for the other will produce a repository nothing else can read.

An earlier revision of this document listed `xchacha20-poly1305-v1` as an approved profile without noting that the platform cannot supply it, which made the profile unimplementable as specified. That was a defect in this specification, found while evaluating candidate libraries.

**Consequences an implementer must accept:**

- The two third-party primitives are **not** covered by the platform's audit posture. They carry the same risk any third-party cryptographic implementation carries, and the external cryptographic review required before the first beta MUST cover them specifically.
- Where a second independent implementation exists, the conformance suite cross-verifies against it. Argon2id is checked against a second implementation on every CI run; XChaCha20-Poly1305 currently is not, because no second implementation is available — that gap is recorded in [`conformance/README.md`](conformance/README.md).
- An implementer targeting a platform where a vetted XChaCha20-Poly1305 is unavailable MAY omit the profile entirely and use `aes-256-gcm-v1` only. Nothing in the format requires both.

The reference implementation takes Argon2id from `Bodu.Security.Cryptography` and confines it, and every other third-party primitive, to a single project. The policy governing what may enter the format-critical path is [ADR-0019](../../docs/adr/0019-third-party-dependency-policy.md); whether the XChaCha20-Poly1305 profile should ship at all while unverified is [Q12](../../docs/open-questions.md#q12--xchacha20-poly1305-has-no-second-implementation-to-check-against).

No other suite is permitted. A writer MUST reject an unapproved suite at configuration time, not at write time — discovering an unusable configuration during a backup is a failure mode the user cannot act on. Insecure selection MUST NOT be available as a compatibility switch.

### 6.1 A note for the security review

AES-GCM is **not key-committing**: a ciphertext can be constructed that authenticates under two different keys. Exploitability here is low, because keys derive from the master key and an attacker without it cannot choose them. It is recorded because the `repository-unverified` deduplication domain accepts records from other writers without verification, which is the closest this design comes to an adversary influencing what gets decrypted under a key the victim holds. → [PT-15](../../docs/review/2026-08-fix-pressure-test.md#pt-15--aes-gcm-is-not-key-committing)

## 7 Rotation

| Operation | Rewrites | Cost |
|-----------|----------|------|
| Change passphrase | `/keys/<key-id>` only | Trivial |
| New data-key generation | Nothing; new writes use it | Trivial |
| Full data-key rotation | Every blob, in the background | Proportional to repository size |

Changing the passphrase does **not** re-encrypt data. A user interface MUST say so plainly, because users routinely believe otherwise, and a user who thinks a password change has protected them from an attacker holding old blobs is worse off than one who knows it has not.

## 8 What is never written down

The passphrase, the KEK, the master key in unwrapped form, any derived key, and any blob key MUST NOT appear in any durable object, log, telemetry payload, crash dump, or configuration export.

Redaction MUST be by declared type rather than by string matching, so that a newly added secret-bearing field is protected by construction rather than by someone remembering to add a pattern. → NFR-SEC-006

---

**Previous:** [02 — Identifiers](02-identifiers.md) · **Next:** [04 — Records](04-record.md)

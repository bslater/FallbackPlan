# 03 — Keys

**Normative.** Derived from [`03-crypto.md` §2–3](../../docs/architecture/03-crypto.md#2-key-hierarchy) and [ADR-0005](../../docs/adr/0005-aead-suite-and-nonce-construction.md).

---

## 1 Hierarchy

```text
passphrase + kdf_salt + kdf_parameters ──Argon2id──▶ root (32 bytes; never stored)
                                                        │
                                                        │ HKDF-Expand, domain-separated
        ┌──────────────┬──────────────┬─────────────────┼──────────────┬──────────────┬──────────────┐
        ▼              ▼              ▼                 ▼              ▼              ▼              ▼
  sealing scalar  structure root  signing root   content-ID key   key-ID key   reclaim root   claim root
        │              │              │
        ▼              ▼              ▼
  X25519 public   metadata key[g]  signing seed[g]
  (descriptor)         │
                       ▼
                 per-blob keys (structure) · data-blob content keys are random and sealed to the public key (05 §2.1)
```

Every repository derives its whole key material from one passphrase, and nothing is stored that could reproduce any of it without the passphrase. The root is never used to encrypt anything directly; every key that touches data is an independent one-way HKDF output of it, so a compromise of one derived key does not extend to the others.

The **write credential** — the structure root, signing root, content-ID key and key-ID key, plus the three public keys — is what a writing service holds ([ADR-0042](../../docs/adr/0042-write-only-repositories.md)). It publishes and cannot read content back: file contents seal to the sealing **public** key, and the private scalar exists only where the passphrase is present. The reclaim and claim roots are likewise withheld from the credential ([ADR-0055](../../docs/adr/0055-reclaim-authority.md), [ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)); only their public halves travel with it.

> **Format 1 is withdrawn.** An earlier format wrapped a random master key under a passphrase-derived key-encryption key and stored it at `/keys/<key-id>`, with a symmetric data-key family beneath it. It was withdrawn before any freeze, with no installed base, in favour of the derivation above ([ADR-0014 amendment](../../docs/adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)). The *symmetric* construction it defined — the per-blob key of §5 and the record framing of [04](04-record.md) — is the one format 2 kept byte for byte for its structure plane, which is why those containers still stamp `format_version = 1` ([04 §4](04-record.md#4-associated-data)).

## 2 The root

```text
root = Argon2id(
          password    = passphrase (UTF-8, NFC-normalised, no trailing newline),
          salt        = kdf_parameters.salt,
          memory      = kdf_parameters.memory_kib,
          iterations  = kdf_parameters.iterations,
          parallelism = kdf_parameters.parallelism,
          tag_length  = 32)
```

Parameters come from the repository descriptor ([01 §3.3](01-object-layout.md#33-kdf-parameters)) and are public. The 32-byte output is the **root** of §1 — used only as HKDF input, never stored, never wrapped.

Minimum acceptable parameters for a new repository: **64 MiB memory, 3 iterations, parallelism 4**. A writer MUST NOT create a repository below these. A reader MUST accept lower values in an existing repository — refusing would make an old repository unrecoverable, which is a worse outcome than a weaker root — but SHOULD warn.

The descriptor's copy of the sealing public key ([01 §3.2](01-object-layout.md#32-body) key 9) is the **wrong-passphrase verifier**: derive the root, expand the sealing scalar, compute its public key, compare. No decryption is involved and nothing is unwrapped; equality is the whole test. A reader MUST report a mismatch as a wrong passphrase *or* an altered descriptor without distinguishing the two — confirming a correct passphrase to an attacker holding a modified descriptor is the same leak the old unwrap rule guarded against.

### 2.1 The passphrase is constrained too, and the primitive will not do it for you

Argon2id accepts a **zero-length password**. RFC 9106 permits it, and an implementation will happily derive a key from nothing.

A writer MUST reject an empty passphrase and SHOULD enforce a minimum length, refusing rather than warning.

This is stated explicitly because the cross-implementation testing behind §6.1 found that two Argon2id implementations disagree on precisely this boundary — one refuses an empty password, the other accepts it. Relying on either behaviour would be relying on an accident of which library was linked, and the parameter minimums above say nothing about the input those parameters are applied to.

Passphrase normalisation matters: the same passphrase typed on macOS and Linux can differ in Unicode composition, and an un-normalised comparison would make a repository unopenable on the other platform. NFC is applied before UTF-8 encoding.

## 3 The key object

There is none. A repository stores no key material: the descriptor's salt, parameters and public key plus the passphrase reproduce everything (§2), and the `/keys/` namespace of format 1 does not exist in format 2 ([01 §2](01-object-layout.md#2-namespace)). A reader MUST NOT look for one, and a store that carries objects under `keys/` is carrying something this format did not write.

This section keeps its number so that references written against format 1 still resolve to the sentence that says what became of it.

## 4 Derived keys

All derivation uses **HKDF-Expand** ([RFC 5869](https://www.rfc-editor.org/rfc/rfc5869) §2.3) with HMAC-SHA256, taking the root — or a sub-root — directly as the pseudorandom key. The extract step is omitted because the root is already 32 uniformly random bytes; extracting again would add nothing.

```text
root             = Argon2id(passphrase, kdf_salt, kdf_parameters)      (§2)

sealing_scalar   = HKDF-Expand(root, "fbp/seal/v2",        32)   → X25519 keypair; the public half is descriptor key 9
structure_root   = HKDF-Expand(root, "fbp/metadata/v2",    32)
content_id_key   = HKDF-Expand(root, "fbp/content-id/v2",  32)
key_id_key       = HKDF-Expand(root, "fbp/key-id/v2",      32)
signing_root     = HKDF-Expand(root, "fbp/signing/v2",     32)
reclaim_root     = HKDF-Expand(root, "fbp/reclaim/v2",     32)
claim_seed       = HKDF-Expand(root, "fbp/claim/v2",       32)   → Ed25519; not generational (ADR-0053 §1)

metadata_key[g]  = HKDF-Expand(structure_root, "fbp/metadata-generation/v2" ‖ u32(g), 32)
signing_seed[g]  = HKDF-Expand(signing_root,   "fbp/signing-generation/v2"  ‖ u32(g), 32)
reclaim_seed[g]  = HKDF-Expand(reclaim_root,   "fbp/reclaim-generation/v2"  ‖ u32(g), 32)
```

Info strings are ASCII, without a terminating NUL. Domain separation is by the string, not by chance. The per-generation keys expand from sub-roots rather than from `root` precisely so a holder of a sub-root can derive every generation without carrying anything that walks back up.

The **write credential** is `structure_root`, `content_id_key`, `key_id_key`, `signing_root` and the public halves of the sealing, reclaim and claim keys. Every member is an independent one-way output: possession of the whole credential yields neither the root, nor the passphrase, nor the sealing scalar, nor either private authority. There is **no data-key family**: data-class record content encrypts under per-blob random content keys sealed to the public key ([05 §2.1](05-blob.md#21-format-v2-data-blobs-the-sealed-content-key)), and data-blob *footers* and everything metadata-class use `metadata_key[g]` through the [§5](#5-per-blob-keys) construction.

The signing, reclaim and claim seeds are **Ed25519 private-key seeds** in the sense of [RFC 8032](https://www.rfc-editor.org/rfc/rfc8032) §5.1.5 — the input to the seed-expansion step, not a pre-clamped scalar. Every mainstream Ed25519 API takes exactly this. The signing public key is computed from the seed by any holder of the signing root, which is why the format stores no signing public key anywhere: signatures are repository-scoped, not device-scoped. → [ADR-0020](../../docs/adr/0020-ed25519-signing-key-semantics.md). The reclaim and claim public keys are carried in the write credential and published to peers, because a keyless destination has to check them and cannot derive them ([ADR-0055 §5](../../docs/adr/0055-reclaim-authority.md), [ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)).

The conformance vectors for this tree are [`write-only.json`](conformance/vectors/write-only.json).

### 4.1 Generations

The metadata, signing and reclaim keys are generational. Introducing a new generation lets a repository migrate to a new key without rewriting existing objects: old objects remain readable under the old generation, and new writes use the new one.

A blob records the generation it used in its cleartext envelope ([05 §2](05-blob.md#2-cleartext-envelope)), so a reader always knows which to derive.

## 5 Per-blob keys

**This is the construction the format's confidentiality rests on.** It is given in full.

```text
blob_salt = 32 bytes from a CSPRNG, drawn once per blob

blob_key  = HKDF-Expand(
                PRK  = metadata_key[generation]      (the structure plane: metadata blobs and every blob's footer),
                info = "fbp/blob/v1" ‖ blob_salt ‖ writer_id ‖ u64(blob_counter),
                L    = 32)
```

`blob_salt`, `writer_id` and `blob_counter` are all stored in the blob's cleartext envelope ([05 §2](05-blob.md#2-cleartext-envelope)), so a reader can reproduce the derivation from the blob and the repository keys alone. A data blob's *records* do not use this key: they encrypt under the blob's random content key, sealed to the public key ([05 §2.1](05-blob.md#21-format-v2-data-blobs-the-sealed-content-key)), and only its footer derives as above.

### 5.1 Why per-blob keys

Every blob having its own key means **nonce uniqueness only has to hold within a single blob**, where exactly one writer owns a strictly increasing record ordinal.

The alternative — one key for many blobs — requires either partitioning a counter across writers who have no coordination channel, or drawing random nonces and tracking a birthday-bound budget across the repository's whole lifetime. Neither survives contact with a system where any number of devices may write concurrently and unattended.

Under this construction, two concurrent writers cannot collide because they hold different keys. There is nothing to coordinate. → [ADR-0005](../../docs/adr/0005-aead-suite-and-nonce-construction.md)

### 5.2 Why writer identity is in the derivation

A 32-byte CSPRNG salt makes collision negligible — provided the CSPRNG is sound. That proviso does not hold universally: a cloned virtual machine, a restored VM snapshot, or an embedded device early in boot can replay RNG state and draw the same salt twice.

Binding `writer_id` and `blob_counter` means a collision would additionally require the same writer at the same counter value. The counter comes from the journal sequence, which is gapless, monotonic, and protected against cloning by the writer-identity conflict alert. This costs one concatenation and removes a dependency on hardware the format cannot inspect. → [PT-13](../../docs/review/2026-08-fix-pressure-test.md#pt-13--blob-salt-uniqueness-rests-entirely-on-csprng-quality-and-vm-cloning-defeats-that)

### 5.3 Blob keys are ephemeral

A blob key MUST NOT be stored. It is derived when the blob is written and re-derived when it is read. A compromise of one blob key exposes exactly one blob.

### 5.4 Format v3: the key is the record's

In a format-3 repository ([ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md)) the blob key of §5 opens the **footer only**, in every blob class. A record's key is scoped to the record, so that its sealed bytes can be copied into another blob and still open there:

```text
metadata record, and a data record where no sealed data plane applies:

record_key = HKDF-Expand(
                 PRK  = metadata_key[generation]     (the blob's envelope generation)
                 info = "fbp/record/v3" ‖ u8(object_type) ‖ object_id,
                 L    = 32)

data record in the sealed data plane:

record_key = 32 random bytes, drawn per record and sealed to the sealing
             public key with associated data repository_id ‖ object_id,
             carried in the record's prefix (05 §2.2)
```

The derivation inputs of the first case are the record header's own fields ([04 §2](04-record.md#2-framing)), so a reader holding the class key derives the key from the record alone; nothing about the container enters it. The second case is §9's sealed content key with `blob_id` replaced by `object_id`, and for the same reason: a derived key is derivable by whoever holds the class key, and in a write-only repository the service holds it and must not read content (FR-WOR-001).

**Nonce uniqueness moves with the key.** §5.1's argument — one writer, one blob, one increasing ordinal — no longer applies to a record; the record carries a random 12-byte nonce ([04 §3](04-record.md#3-nonce)), and under one object's derived key there are at most a handful of messages ever (the same object sealed by different writers, or stored differently by one), so the birthday budget is never approached. The reason the nonce is random rather than zero is stated in [04 §3](04-record.md#3-nonce) and MUST be understood by an implementer before deviating from it.

**The seed a writer resumes from.** A writer MUST NOT store per-record content keys. For a data blob it draws one random 32-byte **record-key seed** per blob, keeps it only in the spool checkpoint ([05 §6.2](05-blob.md#62-everything-that-could-vary-is-pinned)), derives each record's content key as `HKDF-Expand(seed, "fbp/record-seed/v3" ‖ object_id, 32)`, seals that key into the record, and destroys the seed at seal. A reader never sees the seed and needs nothing but the sealed share; the derivation exists so that an interrupted spool can be authenticated on resume without a key per record on disk.

**Generations.** The derived key takes the generation the blob's envelope records ([05 §2](05-blob.md#2-cleartext-envelope)). A record moved between blobs MUST be placed in a blob of the same key generation; rotation (§7) rewrites, as it always has.

## 6 AEAD suites

| Profile | Value | Suite | Key | Nonce | Tag | Implementation |
|---------|-------|-------|-----|-------|-----|----------------|
| `aes-256-gcm-v1` | `0x0001` | AES-256-GCM | 32 | 12 | 16 | Platform (`System.Security.Cryptography.AesGcm`) |
| *Reserved* | `0x0002` | — | — | — | — | Withdrawn before freeze (§6.1). MUST NOT be assigned to another suite |

**The format admits exactly one record AEAD.** A writer MUST use `aes-256-gcm-v1`; a reader MUST refuse any other profile value, including `0x0002`. This table governs **records**; the sealed content key of [05 §2.1](05-blob.md#21-format-v2-data-blobs-the-sealed-content-key) is fixed to the same suite over an X25519 agreement.

`0x0002` stays reserved rather than being freed for reuse. Draft repositories and draft readers exist that understood it as XChaCha20-Poly1305, and a value that means one thing in a draft and another in the frozen format is the kind of ambiguity a version number cannot repair.

### 6.1 Where each primitive comes from

Rule 1 in §1 says to use audited platform primitives and write none ourselves. That rule is satisfiable for most of what this format needs and **not** for all of it, so the position is stated plainly rather than left to be discovered.

| Primitive | Source | Status |
|-----------|--------|--------|
| SHA-256 | Platform | Audited, in-box |
| HMAC-SHA256 | Platform | Audited, in-box |
| HKDF-Expand | Platform (`HKDF`) | Audited, in-box |
| AES-256-GCM | Platform (`AesGcm`) | Audited, in-box |
| **Argon2id** | **Third-party** | No platform implementation exists |

**Why `xchacha20-poly1305-v1` was withdrawn.** .NET provides `ChaCha20Poly1305` — RFC 8439, with a **12-byte** nonce. It does **not** provide the extended-nonce XChaCha20 variant, which takes 24 bytes. The two are not interchangeable, and an implementer who substitutes one for the other produces a repository nothing else can read. An earlier revision of this document listed the profile as approved without noting that, which made it unimplementable as specified.

That could have been repaired by taking a third-party XChaCha20-Poly1305. It was not, and the reason is the one thing this format cannot fix later: no second independent implementation was available to cross-verify against, and an unverified AEAD is a different order of risk from an unverified KDF. A KDF defect makes keys weaker; an AEAD defect can make ciphertext forgeable or, with a nonce-handling error, make plaintext recoverable — and it would be discovered inside bytes the user already stored. A format version can add a profile; it cannot un-admit one that written repositories depend on. → [Q12](../../docs/open-questions.md#closed), [ADR-0005](../../docs/adr/0005-aead-suite-and-nonce-construction.md)

The cost is accepted and named: on hardware without AES acceleration, AES-256-GCM is slower than a ChaCha-family suite would be. A future format version MAY admit one under a new profile value, with a second implementation to check it against as the condition of entry.

**Consequences an implementer must accept:**

- **Argon2id** is the one third-party primitive left in the format-critical path, and it is **not** covered by the platform's audit posture. The external cryptographic review required before the first beta MUST cover it specifically.
- It is cross-verified against a second independent implementation on every CI run, which is how the empty-passphrase gap in §2.1 was found. That check is the condition on which a third-party primitive is admitted at all.

The reference implementation takes Argon2id from `Bodu.Security.Cryptography` and confines it, and every other third-party primitive, to a single project. The policy governing what may enter the format-critical path is [ADR-0019](../../docs/adr/0019-third-party-dependency-policy.md).

No other suite is permitted. A writer MUST reject an unapproved suite at configuration time, not at write time — discovering an unusable configuration during a backup is a failure mode the user cannot act on. Insecure selection MUST NOT be available as a compatibility switch.

### 6.2 A note for the security review

AES-GCM is **not key-committing**: a ciphertext can be constructed that authenticates under two different keys. Exploitability here is low, because keys derive from the root or are drawn by the writer, and an attacker without either cannot choose them. It is recorded because the `repository-unverified` deduplication domain accepts records from other writers without verification, which is the closest this design comes to an adversary influencing what gets decrypted under a key the victim holds. → [PT-15](../../docs/review/2026-08-fix-pressure-test.md#pt-15--aes-gcm-is-not-key-committing)

## 7 Rotation

| Operation | Rewrites | Cost |
|-----------|----------|------|
| Change passphrase | **Not possible** — see below | — |
| New metadata-key generation | Nothing; new writes use it | Trivial |
| Full rotation | Every blob, in the background | Proportional to repository size |

**There is no passphrase change.** Every key derives directly from the passphrase (§4); changing it would change every derived key and orphan every sealed blob. A repository's passphrase is fixed for its life — the remedy for a passphrase the user wishes to retire is a new repository ([ADR-0042 §11](../../docs/adr/0042-write-only-repositories.md)). A user interface MUST say so at creation, because users routinely believe otherwise, and a user who thinks a password can be changed later has not been told what they are agreeing to.

## 8 What is never written down

The passphrase, the root, the sealing scalar, the reclaim and claim seeds, any member of the write credential, any blob key and any blob content key MUST NOT appear in any durable repository object, log, telemetry payload, crash dump, or configuration export. The one deliberate exception is the spool checkpoint's in-flight content key ([05 §6.2](05-blob.md#62-everything-that-could-vary-is-pinned)) — writer-local state, never a repository object, destroyed at seal.

Redaction MUST be by declared type rather than by string matching, so that a newly added secret-bearing field is protected by construction rather than by someone remembering to add a pattern. → NFR-SEC-006

## 9 Write-only repositories (format v2)

Every repository is write-only, and format 2 is the only format: the term names the shape §1–§4 describe, in which the machine that writes backups holds nothing that opens them ([ADR-0042](../../docs/adr/0042-write-only-repositories.md)). This section keeps its heading because the decision records and the older documents cite it; its content is now the body of this document.

What a repository does **not** have, said once:

- **No `/keys/` namespace** (§3) — no wrapped key object, no key-encryption key.
- **No passphrase change** (§7).
- **No data-key family** (§4) — content keys are random and sealed; the structure plane derives.
- **No stored private authority** — the sealing scalar, the reclaim seed and the claim seed exist only where the passphrase is present, and reach a service, if at all, as a grant sealed to its recipient key for one run ([ADR-0042 §5](../../docs/adr/0042-write-only-repositories.md), [ADR-0055 §6](../../docs/adr/0055-reclaim-authority.md)).

---

**Previous:** [02 — Identifiers](02-identifiers.md) · **Next:** [04 — Records](04-record.md)

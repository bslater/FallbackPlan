# 05 — Blobs

**Normative.** Derived from [`02-repository-format.md` §5](../../docs/architecture/02-repository-format.md#5-blobs).

---

## 1 Layout

```text
+---------------------------------------------------------------+
| Cleartext envelope            88 bytes, fixed                  |
+---------------------------------------------------------------+
| Record 0                      header + ciphertext + tag        |
| Record 1                                                       |
| …                                                              |
| Record n-1                                                     |
+---------------------------------------------------------------+
| Recovery footer               authenticated                    |
+---------------------------------------------------------------+
| Footer locator                16 bytes, fixed, at the very end |
+---------------------------------------------------------------+
```

A blob is immutable once sealed. Maximum size: 512 MiB ([00 §8](00-conventions.md#8-lengths-and-limits)).

## 2 Cleartext envelope

```text
offset  size   field
------  -----  -------------------------------------------------------------
     0      8  magic          = 0x46 42 50 4B 42 4C 4F 42   ("FBPKBLOB")
     8      2  format_version u16
    10      2  blob_class     u16   0x0001 = data, 0x0002 = metadata
    12      4  key_generation u32
    16     16  blob_id        bytes[16]
    32     32  blob_salt      bytes[32]
    64      8  blob_counter   u64
    72     16  writer_id      bytes[16]
```

Total: **88 bytes**.

`writer_id` is carried explicitly because it is an input to the blob-key derivation ([03 §5](03-keys.md#5-per-blob-keys)) and is **not** reliably recoverable from `blob_id`: the structured formation embeds only the first 8 of its 16 bytes, and [02 §4](02-identifiers.md#4-blob-identifier) equally permits a `blob_id` of 16 random bytes, which embeds none. A reader holding only the blob and the repository keys must be able to reproduce the derivation — that is the whole recovery property — so every derivation input lives in the envelope.

The envelope is cleartext because a reader must derive the blob key before it can read anything, and the derivation inputs cannot themselves be encrypted under the key they produce. It carries only key-derivation selectors: no content, no path, no count of records, no timestamp.

`blob_class` selects which key family to derive from — `data_key[generation]` or `metadata_key[generation]` ([03 §4](03-keys.md#4-derived-keys)).

## 3 Recovery footer

The footer is what makes a blob self-describing. Given the blob and the repository keys and **nothing else** — no index, no catalogue, no other object — every record in it can be located, decrypted, and verified.

That property is the reason the footer exists. It bounds the blast radius of losing every index object in the repository, and it is what forensic rebuild is built on. → [`02-repository-format.md` §8.2](../../docs/architecture/02-repository-format.md#82-forensic-rebuild)

```text
offset  size   field
------  -----  -------------------------------------------------------------
     0      8  magic          = 0x46 42 50 4B 46 4F 4F 54   ("FBPKFOOT")
     8      4  record_count   u32, max 65 536
    12      4  cbor_length    u32, max 16 MiB
    16      N  ciphertext     AEAD-encrypted CBOR record table (§3.1)
  16+N     16  tag            AEAD authentication tag
```

The footer is encrypted under the blob key with:

```text
nonce = 12-byte big-endian 0xFFFFFFFF_FFFFFFFF_FFFFFFFF
AAD   = repository_id ‖ u16(format_version) ‖ blob_id ‖ u32(record_count)
```

The all-ones nonce is reserved for the footer and MUST NOT be used by any record. Since a blob may hold at most 65 536 records, no record ordinal can reach it.

The footer's record table duplicates information also held in the index. That duplication is deliberate and is the only redundancy in this format: it is metadata, not payload, so it costs a fraction of a percent of repository size, and it is what allows complete recovery when every index object is gone. Payload is never duplicated for recovery. → NFR-REL-006

### 3.1 Record table

CBOR array of `record_count` maps, in ascending ordinal order:

| Key | Type | Value |
|-----|------|-------|
| 1 | bytes[32] | `object_id` |
| 2 | u32 | `ordinal` |
| 3 | u64 | `physical_offset` — from the start of the blob |
| 4 | u32 | `stored_length` |
| 5 | u64 | `logical_length` |
| 6 | u16 | `compression_profile` |
| 7 | u16 | `encryption_profile` |
| 8 | u8 | `object_type` |

A reader MUST verify that every `physical_offset` plus its record's total size falls within the blob, and that offsets are strictly increasing. A footer failing either check is a damage finding.

## 4 Footer locator

The last 16 bytes of a blob:

```text
offset  size   field
------  -----  -------------------------------------------------------------
     0      8  footer_offset  u64 — byte offset of the footer's magic
     8      8  digest_prefix  bytes[8] — first 8 bytes of the blob digest
```

A reader fetches the final 16 bytes, seeks to `footer_offset`, and reads the footer. On a store supporting range reads this costs two requests and no wasted transfer.

`digest_prefix` is a cheap corruption check for a reader that has the whole blob. It is **not** an integrity guarantee: it is unkeyed and truncated. Authenticity comes from the footer's AEAD tag and each record's own tag.

### 4.1 Stores where reading the tail is expensive

Where a store makes tail reads costly or unreliable, a writer MAY additionally publish a **sidecar** object at `/blobs/<class>/<shard>/<store-blob-key>.footer` containing a byte-identical copy of the footer and locator. The sidecar's name uses the same HMAC-rendered store blob key as the blob itself ([02 §4.3](02-identifiers.md#43-not-leaking-writer-identity)) — a raw `blob_id` in a store key would leak the writer identity the rendering exists to hide.

A sidecar is an optimisation. A reader MUST be able to operate without one, and where both exist and disagree, the in-blob footer wins — the sidecar is a separate object and may be stale or absent.

## 5 Sealing

A blob is sealed when any of the following is first true:

| Condition | Local / peer default | Object-store default |
|-----------|---------------------|---------------------|
| Target size reached | 64 MiB | 128 MiB |
| Next record would exceed the maximum | 256 MiB | 512 MiB |
| Open-blob age | 15 min | 15 min |
| Record count | 65 536 | 65 536 |
| Job completes | — | — |

The age limit exists so that a low-churn backup set still commits within a bounded time rather than waiting indefinitely for a blob to fill.

Sealing computes the footer, appends it and the locator, and computes the blob digest — SHA-256 over the complete sealed representation, recorded in the index and used for end-to-end verification during replication.

After sealing, the blob is immutable. Appending to a sealed blob is not permitted, and a reader encountering data beyond the locator MUST report a damage finding.

> **Erratum (phase 0).** Two defects in the digest sentence above. First, "the complete sealed representation" is circular: the locator carries `digest_prefix`, so the locator cannot be inside its own digest's preimage. The digest is computed over bytes `[0, blob_length − 16)` — everything up to but excluding the 16-byte locator. Second, "recorded in the index" names a field that does not exist — no index delta or checkpoint key carries a blob digest ([07](07-index.md)). Phase 0 records the digest in the device-local catalogue; format-level carriage is an open format change ([open questions Q16](../../docs/open-questions.md#q16--the-blob-digest-has-no-home-in-the-index)).

## 6 The spool

Blobs are assembled in a durable local spool before upload. A blob becomes visible in the repository only after it is sealed, validated, uploaded under its final identifier, and acknowledged.

### 6.1 The checkpoint stores sealed bytes

**This rule is load-bearing. An implementation that gets it wrong will produce a catastrophic cryptographic failure under ordinary operating conditions.**

The spool checkpoint MUST store the **sealed record bytes** — header, ciphertext, and tag as they will appear in the blob. It MUST NOT store only a plaintext offset to be recomputed from.

The reason: the input to the AEAD is not the segment's plaintext, it is the plaintext **after compression** ([04 §5](04-record.md#5-producing-a-record)). Recompression is not guaranteed reproducible — Zstandard is deterministic for a given library version and parameter set, and offers no guarantee across versions.

So an agent that crashes, is upgraded, and resumes would recompress the same segment into different bytes and encrypt **those** under the same `(blob_key, ordinal)`. Two different plaintexts under one key and nonce leaks their XOR and permits recovery of the GHASH authentication subkey, after which an attacker can forge arbitrary authenticated records in that blob.

No attacker and no unusual configuration is required: a crash, an unattended update, and a resume. → [PT-1](../../docs/review/2026-08-fix-pressure-test.md#pt-1--c2s-resume-guarantee-silently-assumes-bit-reproducible-compression)

### 6.2 Everything that could vary is pinned

The checkpoint MUST record: `blob_salt`, `blob_id`, `blob_counter`, `key_generation`, segmentation profile and parameters, compression profile **and codec version**, and encryption profile.

On resume, a writer MUST compare each against its current configuration. **Any mismatch forces a restart**, not a resume. A restarted blob draws a fresh `blob_salt` and is therefore a different key, so ordinals beginning again at zero reuse nothing.

Restart is always the safe failure. A writer in any doubt MUST restart.

### 6.3 Resume and restart

| Path | Behaviour | Safety |
|------|-----------|--------|
| **Resume** | Re-emit checkpointed sealed bytes, continue from the next ordinal | Byte-identical to the interrupted blob |
| **Restart** | Discard the spool, draw a new `blob_salt`, begin at ordinal 0 | Different key; no nonce reuse |

A partial spool MUST NOT be uploaded under any circumstances.

## 7 Publication

A sealed blob is uploaded under `/blobs/<class>/<shard>/<store-blob-key>`.

Uploading a blob is **not** publication. The blob is not referenced by anything until its index delta is published ([07](07-index.md)), and it is not reachable from a snapshot until the snapshot manifest is published. During that window it is protected from garbage collection by the write-intent journal record that named it before it was created ([08](08-journal.md)).

Where the store offers conditional create, a writer SHOULD use it. Where it does not, uniqueness of the writer-allocated blob identifier prevents collision — which is why the format does not require conditional create for correctness.

## 8 Verification

| Level | Cost | Establishes |
|-------|------|-------------|
| Locator + footer | 2 range reads | The blob is structurally intact and its record table authentic |
| Footer + digest | Whole blob | Nothing has been altered since sealing |
| Every record | Whole blob + decryption | Every record is authentic and its content identifier is truthful |

Routine verification uses the first. Replication receipts use the second. A full check uses the third.

---

**Previous:** [04 — Records](04-record.md) · **Next:** [06 — Manifests](06-manifests.md)

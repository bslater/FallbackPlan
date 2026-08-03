# 04 — Records

**Normative.** Derived from [`03-crypto.md` §3](../../docs/architecture/03-crypto.md#3-nonce-and-key-construction) and [`02-repository-format.md` §5.2](../../docs/architecture/02-repository-format.md#52-layout).

---

## 1 What a record is

A **record** is one independently encrypted, independently authenticated unit of stored data: a file segment, or a metadata object. Records are the granularity at which the repository deduplicates, verifies, and localises corruption.

A record lives inside a blob ([05](05-blob.md)) but does not depend on it. Given a blob's cleartext envelope and the repository keys, any single record can be located, decrypted, and verified without reading or trusting the rest of the blob.

## 2 Framing

Each record occupies a contiguous byte range within its blob:

```text
offset  size   field
------  -----  -------------------------------------------------------------
     0      1  record_marker      = 0x52  ("R")
     1      1  object_type        u8   (see 02 §3.1)
     2      2  compression_profile u16
     4      2  encryption_profile u16
     6      4  ordinal            u32  — position of this record in the blob
    10      8  logical_length     u64  — plaintext length before compression
    18      4  stored_length      u32  — ciphertext length, max 64 MiB
    22     32  object_id          bytes[32]
    54      N  ciphertext         bytes[stored_length]
  54+N     16  tag                AEAD authentication tag
```

Fixed header size: **54 bytes**. Total record size: `54 + stored_length + 16`.

The header is cleartext. It has to be: a reader scanning a blob for recovery must be able to walk records without first knowing which key to use, and must be able to find a specific `object_id` without decrypting everything before it. The header contains no plaintext content, no path, and no unkeyed content hash — `object_id` is already keyed ([02 §3](02-identifiers.md#3-object-identifier)).

The header is **not** unauthenticated, however. It is bound into the AEAD associated data (§4), so any modification to it causes decryption of that record to fail.

### 2.1 Field constraints

| Field | Constraint |
|-------|-----------|
| `record_marker` | MUST be `0x52`. A reader scanning for records uses it as a cheap first filter, never as proof. |
| `ordinal` | MUST equal the record's zero-based position in the blob. MUST be strictly increasing. MUST NOT exceed 65 535. |
| `logical_length` | For a segment, the plaintext byte count. MUST be ≥ 1 — a zero-length file produces no segments and no records at all ([09 §2](09-segmentation.md#2-fixed-v1)), so a record with `logical_length` 0 cannot exist and a reader MUST treat one as a damage finding. |
| `stored_length` | MUST be ≤ 64 MiB ([00 §8](00-conventions.md#8-lengths-and-limits)). |
| `compression_profile` | `0x0000` = none. When none, `stored_length` equals `logical_length`. |

A reader MUST validate `stored_length` against the limit **before allocating**, and MUST verify that `54 + stored_length + 16` does not extend past the end of the blob.

## 3 Nonce

```text
nonce = 12-byte big-endian encoding of `ordinal`
```

For `xchacha20-poly1305-v1`, whose nonce is 24 bytes, the ordinal is encoded in the **last 12 bytes** and the first 12 are zero. The extra space is not used to add randomness: uniqueness already holds by construction, and adding entropy would only make the derivation harder to reproduce.

Because every blob has its own key ([03 §5](03-keys.md#5-per-blob-keys)), and exactly one writer owns a blob's ordinal sequence, `(blob_key, nonce)` is unique by construction with no coordination between writers and no probabilistic budget to track.

## 4 Associated data

```text
AAD = repository_id ‖ u16(format_version) ‖ u8(object_type) ‖ object_id ‖ u32(ordinal)
```

Total: 16 + 2 + 1 + 32 + 4 = **55 bytes**.

This binds each record to its exact context. A record cannot be moved to a different ordinal, a different object type, a different repository, or replayed under a different format version without authentication failing. It is what defends against the substitution and splicing attacks in [T-3](../../docs/threat-model.md#t-3-object-substitution-and-splicing).

Note that AAD does **not** include the blob identifier. A record is intentionally relocatable between blobs by compaction, which republishes its index entry without re-encrypting it ([07](07-index.md)). Binding the blob would make compaction require decryption and re-encryption of every moved record.

> **Erratum (phase 0).** The relocation claim above is in tension with §2.1: the AAD *does* include `ordinal`, and §2.1 requires the ordinal to equal the record's position in its blob — a byte-identical relocation generally cannot satisfy both. Nothing in phase 0 relocates records, so the contradiction is recorded rather than resolved: see [open questions Q15](../../docs/open-questions.md#q15--record-ordinal-in-the-aad-versus-byte-identical-relocation). Resolve it before implementing compaction.
>
> Records also exist **outside** blobs — index deltas, checkpoints, journal records, and the standalone snapshot object are metadata records stored as standalone store objects. This document defines their encryption inputs (`blob_salt`, `writer_id`, counter, `ordinal`) only via the blob envelope, which a standalone object does not have. Pending a normative edit, [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md) §Decision 1 defines the `FBPKSREC` standalone framing: a 72-byte cleartext prefix carries the same selectors, the record header carries `ordinal = 0`, and this document's key derivation, nonce, and AAD apply byte-for-byte.

## 5 Producing a record

1. Compute `content_id = H(plaintext)` ([02 §2](02-identifiers.md#2-content-identifier)).
2. Compute `object_id = HMAC-SHA256(content_id_key, object_type ‖ content_id)`.
3. Compress the plaintext per the compression profile ([10](10-compression.md)). If the saving is below the threshold, use profile `none` and the plaintext unchanged.
4. Assign `ordinal` = the next position in the open blob.
5. Encrypt the compressed bytes under `blob_key` with the nonce from §3 and the AAD from §4.
6. Write header, ciphertext, tag.

Steps 3 → 5 are ordered: **compression happens before encryption**. Reversing them would produce incompressible input and defeat the purpose. It also creates a length side channel — stored lengths reveal compressed sizes, which fingerprint file types — a trade recorded rather than hidden. → [T-11](../../docs/threat-model.md#t-11-metadata-side-channels)

## 6 Reading a record

1. Read and validate the 54-byte header.
2. Check `stored_length` against limits and blob bounds.
3. Derive `blob_key` from the blob envelope ([03 §5](03-keys.md#5-per-blob-keys)).
4. Reconstruct nonce and AAD.
5. Decrypt and verify the tag. **If verification fails, stop.** Do not use the plaintext.
6. Decompress per `compression_profile`.
7. Verify `H(plaintext)` matches the `content_id` implied by `object_id`.

Step 7 is not redundant with step 5. The AEAD tag proves the record was written by someone holding the blob key and has not been altered since. It does **not** prove that the writer's claimed `object_id` matches what the plaintext actually hashes to — a writer with a bug, or a malicious member, can produce a perfectly authentic record whose content identifier is a lie. Step 7 is what catches that, and it is the reason verify-on-reuse exists at all. → [T-10](../../docs/threat-model.md#t-10-malicious-repository-member-poisons-deduplication)

A reader that skips step 7 will restore corrupt data and report success.

## 7 Corruption is local

A record whose tag fails verification affects **only that record**. Every other record in the same blob remains independently readable, because each has its own nonce, its own AAD, and its own tag.

A reader encountering a failed record MUST:

- report which `object_id` failed and in which blob;
- continue processing the remaining records if it is scanning;
- refuse to emit a file version that depends on the failed record;
- report the affected file versions by name, so the damage has a scope a user can act on.

It MUST NOT substitute zeroes, skip the segment silently, or emit a partial file. → NFR-REL-004, FR-RST-005

## 8 Records are never split

A record MUST be wholly contained in one blob in format version 1. When the open blob cannot accommodate a complete record within its maximum size, the writer seals it and starts the record in a new blob ([05 §5](05-blob.md#5-sealing)).

This costs some blob-size variance and buys a great deal: recovery scanning never has to reassemble a record across objects, and a single fetched blob is always self-sufficient for the records it contains.

## 9 Test vectors

Record framing vectors, including header layouts and AAD construction, are in [`conformance/vectors/records.json`](conformance/vectors/records.json).

**There are no AEAD ciphertext vectors.** Producing them independently requires an AES-GCM implementation the vector generator deliberately does not depend on, and generating them from the reference implementation would prove only that a future build matches today's build. The gap is stated rather than filled with self-certifying values — see [`conformance/README.md`](conformance/README.md).

AES-256-GCM known-answer tests in [`conformance/vectors/aes-gcm.json`](conformance/vectors/aes-gcm.json) demonstrate the primitive is used correctly, including AAD absorption over this section's real 55-byte AAD. Their correctness is verified against the platform implementation; their provenance is declared per case — one believed-CAVP case recorded as **unverified** because the archive was unreachable, one platform-derived regression case labelled as such. What ultimately validates the framing is the freeze-gate independent reader, not this file.

---

**Previous:** [03 — Keys](03-keys.md) · **Next:** [05 — Blobs](05-blob.md)

# 01 — Object layout

**Normative.** Derived from [`02-repository-format.md` §2](../../docs/architecture/02-repository-format.md#2-object-classes).

---

## 1 Store model

A repository lives in a **store**: a flat key–value namespace of immutable objects. The format assumes only that a store can put an object under a key, get it (whole or by byte range), list keys under a prefix, and delete a key.

It does **not** assume: atomic rename, strong listing consistency, provider-computed checksums, mutable objects, or that listing reflects a write that has just completed. Each of those is absent from at least one store the project intends to support, and correctness here never depends on any of them.

## 2 Namespace

```text
/repository-format
/keys/<key-id>
/blobs/data/<shard>/<store-blob-key>
/blobs/meta/<shard>/<store-blob-key>
/index/delta/<generation>/<delta-id>
/index/checkpoint/<generation>/<checkpoint-id>
/snapshots/<device-id>/<backup-set-id>/<snapshot-id>
/journal/<writer-id>/<sequence>
/leases/<scope>/<lease-id>
/tombstones/<object-type>/<object-id>
/audit/<period>/<record-id>
/hints/placement/<snapshot-id>
```

`<store-blob-key>` is the HMAC-rendered store blob key of [02 §4.3](02-identifiers.md#43-not-leaking-writer-identity) — **never** the raw `blob_id`, whose structured formation embeds writer identity. `<shard>` is the **first four characters** of the base32-rendered store blob key. Sharding keeps any single listing prefix bounded, which matters on stores that paginate listings and on filesystems that degrade with very large directories; deriving the shard from the keyed rendering means it, too, reveals nothing (§2.1).

`<generation>` is rendered as a zero-padded 16-digit decimal `u64`, so lexicographic key order matches numeric order. `<sequence>` follows the same rule.

> **Erratum (phase 0).** This specification never defines how `<delta-id>`, `<checkpoint-id>`, or `<key-id>` are allocated or rendered. Pending a normative edit, [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md) resolves them: delta and checkpoint identifiers are 16 CSPRNG bytes allocated at publication and rendered as 26 lowercase base32 characters (§00 §6); the key identifier is likewise 16 opaque bytes, and readers discover it by listing `/keys/` (see the erratum at §6).

### 2.1 What keys must not reveal

Every identifier appearing in a key MUST be keyed or opaque. Store keys MUST NOT contain, or allow derivation of: file paths or names, user or device names, plaintext content hashes, backup-set names, or timestamps.

A store operator who can see the whole namespace learns the approximate size of the repository, its rate of growth, and when it is active. That residual is recorded in the [threat model](../../docs/threat-model.md#t-11-metadata-side-channels). They MUST NOT be able to learn anything else, and in particular MUST NOT be able to test whether the repository contains a file they already possess.

## 3 The repository descriptor

`/repository-format` is the only object a reader can locate without prior knowledge, and the only object with a fixed key. Everything else is reached from it.

It is **not encrypted** — a reader must be able to determine whether it can read a repository, and derive keys from a passphrase, before it holds any key. It therefore contains no user data and no secret.

### 3.1 Framing

```text
offset  size   field
------  -----  -------------------------------------------------------------
     0      8  magic          = 0x46 42 50 4B 52 45 50 4F   ("FBPKREPO")
     8      2  format_version u16
    10      2  reserved       u16, MUST be zero
    12      4  cbor_length    u32, length of the CBOR body, max 65 536
    16      N  cbor_body      deterministic CBOR map (§3.2)
  16+N     32  digest         SHA-256 over bytes [0, 16+N)
```

The magic string is checked first. An object that does not begin with it is not a FallbackPlan repository descriptor, and a reader MUST say so rather than reporting a parse error.

`digest` covers the header and body but not itself. A reader MUST verify it before interpreting the body. It provides integrity against accidental corruption only — it is unkeyed, so it provides **no** protection against deliberate modification. Authenticity of repository *state* comes from signed snapshots and authenticated index objects, not from this field.

### 3.2 Body

| Key | Type | Value |
|-----|------|-------|
| 1 | bytes[16] | `repository_id` — random at creation, never reused |
| 2 | u16 | `format_version`, repeated inside the digest-covered body — a corruption check against the framing copy, **not** a defence against deliberate downgrade, because the digest is unkeyed (§3.1) |
| 3 | array | `required_features` — array of u16 feature identifiers |
| 4 | array | `optional_features` — array of u16 feature identifiers |
| 5 | map | `kdf_parameters` (§3.3) |
| 6 | u64 | `created_at` — informational only |
| 7 | text | `created_by` — implementation name and version, informational |
| 8 | bool | `unstable_format` — `true` while the format is unfrozen |

A reader MUST refuse the repository if `required_features` contains any identifier it does not implement, naming the unimplemented identifier. It MUST NOT proceed on the assumption that an unknown feature is unimportant.

A reader MUST surface a prominent warning when `unstable_format` is `true`. Pre-1.0 repositories carry no forward-compatibility guarantee, and a user pointing their only copy of something at one deserves to know. → [ADR-0014](../../docs/adr/0014-format-versioning-and-stability.md)

### 3.3 KDF parameters

| Key | Type | Value |
|-----|------|-------|
| 1 | u16 | `kdf_profile` — `0x0001` = Argon2id |
| 2 | bytes[16] | `salt` |
| 3 | u32 | `memory_kib` |
| 4 | u32 | `iterations` |
| 5 | u8 | `parallelism` |

These are public. The salt is not a secret; publishing the parameters is what allows a recovery tool, years later, to reproduce the key-encryption key from the user's passphrase. Withholding them would make the repository unrecoverable without also making it more secure.

## 4 Object immutability

Every object in the namespace except `/leases/…` is **immutable once written**. A writer MUST NOT overwrite an existing key with different content.

Where a store offers conditional create, a writer SHOULD use it. Where it does not, uniqueness of the final identifier is what prevents collision — which is why the format is designed not to require conditional create for correctness.

Re-writing an object with byte-identical content is permitted, and is the expected outcome of an idempotent retry.

## 5 Deletion

Only two processes delete objects: garbage collection ([`07-retention-and-gc.md`](../../docs/architecture/07-retention-and-gc.md)) and index-delta retirement ([07](07-index.md)).

Both proceed by tombstone, grace period, and revalidation before the delete. A reader that encounters a missing object referenced by a live object MUST report it as a damage finding, and MUST NOT infer that the reference was invalid.

## 6 Discovery order

A reader bootstraps in this order:

1. Fetch `/repository-format`. Verify magic and digest. Check `format_version` and `required_features`.
2. Derive the key-encryption key from the passphrase using `kdf_parameters`.
3. Fetch and unwrap `/keys/<key-id>` ([03](03-keys.md)).
4. Enumerate `/snapshots/…` to establish a stable snapshot set.
5. Load the index generation needed to resolve that set ([07](07-index.md)).

Step 4 precedes step 5 deliberately. A snapshot is published only after every object it references is durable, so a reader that fixes the snapshot set first and then loads the index can never observe a snapshot whose objects are unresolvable. Doing it the other way round exposes the reader to a partially published view. → [`04-concurrency-and-publication.md` §5](../../docs/architecture/04-concurrency-and-publication.md#5-publication-order)

> **Erratum (phase 0).** Step 3 names `/keys/<key-id>` but nothing tells the reader the key identifier: the descriptor body (§3.2) has no key-id field. Pending a normative fix, [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md) §Decision 3 applies: the reader lists `/keys/` and attempts to unwrap what it finds; creation writes the key object before the descriptor, so a visible descriptor implies the key object is durable, and a lagging listing is a transient open failure to retry — not a damage finding.

---

**Previous:** [00 — Conventions](00-conventions.md) · **Next:** [02 — Identifiers](02-identifiers.md)

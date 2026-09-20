# 01 — Object layout

**Normative.** Derived from [`02-repository-format.md` §2](../../docs/architecture/02-repository-format.md#2-object-classes).

---

## 1 Store model

A repository lives in a **store**: a flat key–value namespace of immutable objects. The format assumes only that a store can put an object under a key, get it (whole or by byte range), list keys under a prefix, and delete a key.

It does **not** assume: atomic rename, strong listing consistency, provider-computed checksums, mutable objects, or that listing reflects a write that has just completed. Each of those is absent from at least one store the project intends to support, and correctness here never depends on any of them.

## 2 Namespace

```text
/repository-format
/blobs/data/<shard>/<store-blob-key>
/blobs/meta/<shard>/<store-blob-key>
/index/delta/<generation>/<delta-id>
/index/checkpoint/<generation>/<checkpoint-id>
/snapshots/<device-id>/<backup-set-id>/<snapshot-id>
/journal/<writer-id>/<sequence>
/leases/<scope>/<lease-id>
/tombstones/<object-type>/<object-id>
/audit/<period>/<record-id>
/format-upgrade/<to-version>
/hints/placement/<snapshot-id>
/hints/identity/<shard>/<source-key>/<captured-at>/<snapshot-id>
```

`<store-blob-key>` is the HMAC-rendered store blob key of [02 §4.3](02-identifiers.md#43-not-leaking-writer-identity) — **never** the raw `blob_id`, whose structured formation embeds writer identity. `<shard>` is the **first four characters** of the base32-rendered store blob key. Sharding keeps any single listing prefix bounded, which matters on stores that paginate listings and on filesystems that degrade with very large directories; deriving the shard from the keyed rendering means it, too, reveals nothing (§2.1).

`<to-version>` under `/format-upgrade/` is a format version as four lowercase hexadecimal digits ([11 §5](11-lifecycle-objects.md#5-format-upgrade-record)).

`<generation>` is rendered as a zero-padded 16-digit decimal `u64`, so lexicographic key order matches numeric order. `<sequence>` and a source-identity hint's `<captured-at>` ([06 §11](06-manifests.md#11-source-identity)) follow the same rule; that hint's `<shard>` is the first four base32 characters of its `<source-key>`, sharded for the reason blobs are — one child per file in the repository is exactly the listing prefix this rule exists to bound.

> **Erratum (phase 0).** This specification never defines how `<delta-id>` or `<checkpoint-id>` are allocated or rendered. Pending a normative edit, [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md) resolves them: delta and checkpoint identifiers are 16 CSPRNG bytes allocated at publication and rendered as 26 lowercase base32 characters (§00 §6). The `/keys/<key-id>` entry that used to sit beside them belonged to format 1 and went with it ([03 §3](03-keys.md#3-the-key-object)).

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
| 9 | bytes[32] | `sealing_public_key` ([03 §2](03-keys.md#2-the-root)): the X25519 public key data-blob content keys seal to, and the derive-and-compare wrong-passphrase verifier. Not a secret, exactly like the salt. Mandatory: a descriptor without it has lost its verifier and is refused |

A descriptor MUST list feature `0x0001` (`sealed-data-plane`) in `required_features`, so a reader that predates the sealed data plane refuses through the rule below with the identifier named rather than half-reading sealed blobs.

The feature identifiers this specification defines:

| Identifier | Name | Listed by | Means |
|---|---|---|---|
| `0x0001` | `sealed-data-plane` | every descriptor | Data-blob content is sealed to the sealing public key ([03 §9](03-keys.md#9-write-only-repositories-format-v2)) |
| `0x0002` | `reclaim-authority` | every descriptor | Tombstones and retention instructions are signed under the reclaim key ([11 §3](11-lifecycle-objects.md); [ADR-0055](../../docs/adr/0055-reclaim-authority.md)) |
| `0x0003` | `relocatable-records` | a format-3 descriptor, and never a format-2 one | Records are keyed to the object, carry their nonce and omit the ordinal from their associated data ([04 §3–§4](04-record.md#3-nonce)); a reader that does not implement format 3 refuses by this name ([ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md)) |

A format-3 descriptor MUST list `0x0003` and a format-2 descriptor MUST NOT; a reader MUST treat either mismatch as a format violation, because the version and the feature name one fact and a descriptor in which they disagree was not written by a conforming writer.

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

Every object in the namespace except `/leases/…` ([11 §2](11-lifecycle-objects.md#2-lease)) is **immutable once written**. A writer MUST NOT overwrite an existing key with different content.

Where a store offers conditional create, a writer SHOULD use it. Where it does not, uniqueness of the final identifier is what prevents collision — which is why the format is designed not to require conditional create for correctness.

Re-writing an object with byte-identical content is permitted, and is the expected outcome of an idempotent retry.

For blobs the rule is stated as a named invariant, [INV-BLOB-001](05-blob.md#51-blob-immutability--inv-blob-001), because the operations that would break it — compaction, key rotation, upload retry — are the ones written last and the ones whose violation nothing detects.

## 5 Deletion

Only two processes delete objects: garbage collection ([`07-retention-and-gc.md`](../../docs/architecture/07-retention-and-gc.md)) and index-delta retirement ([07](07-index.md)).

Both proceed by tombstone, grace period, and revalidation before the delete — the tombstone object and the rules a collector must satisfy before deleting are [11 §3](11-lifecycle-objects.md#3-tombstone). A reader that encounters a missing object referenced by a live object MUST report it as a damage finding, and MUST NOT infer that the reference was invalid.

## 6 Discovery order

A reader bootstraps in this order:

1. Fetch `/repository-format`. Verify magic and digest. Check `format_version` — it MUST be 2 or 3; a reader that meets 1 refuses by name, naming re-seeding as the remedy — and `required_features`.
2. Derive the root from the passphrase using `kdf_parameters`, expand the sealing scalar, and compare its public key with key 9 ([03 §2](03-keys.md#2-the-root)). A holder of the write credential instead compares the credential's public key with key 9; either way nothing is fetched and nothing is unwrapped.
3. There is no third fetch: the derivation is the whole of the key material ([03 §3](03-keys.md#3-the-key-object)).
4. Enumerate `/snapshots/…` to establish a stable snapshot set.
5. Load the index generation needed to resolve that set ([07](07-index.md)).

Step 4 precedes step 5 deliberately. A snapshot is published only after every object it references is durable, so a reader that fixes the snapshot set first and then loads the index can never observe a snapshot whose objects are unresolvable. Doing it the other way round exposes the reader to a partially published view. → [`04-concurrency-and-publication.md` §5](../../docs/architecture/04-concurrency-and-publication.md#5-publication-order)


---

**Previous:** [00 — Conventions](00-conventions.md) · **Next:** [02 — Identifiers](02-identifiers.md)

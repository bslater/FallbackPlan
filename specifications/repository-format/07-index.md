# 07 — Index

**Normative.** Derived from [`02-repository-format.md` §7](../../docs/architecture/02-repository-format.md#7-index-architecture), [ADR-0008](../../docs/adr/0008-index-generations-and-checkpoints.md), [ADR-0017](../../docs/adr/0017-index-entry-supersession.md).

---

## 1 What the index is

The index maps **object identifiers to physical locations**. It is the only authority on where a record lives, because manifests deliberately carry no physical information ([06 §3.1](06-manifests.md#31-no-physical-location--and-why)).

It is never a single object. It consists of:

- **deltas** — immutable, writer-authored, published after the blobs they describe are durable;
- **checkpoints** — immutable compactions that subsume an explicitly enumerated set of deltas.

Read path: local catalogue → checkpoint + deltas → blob recovery footers. Each fallback is independently sufficient; the last requires no index at all.

## 2 Index delta

Stored at `/index/delta/<generation>/<delta-id>`, encrypted as a metadata record under the metadata key.

| Key | Type | Value |
|-----|------|-------|
| 1 | bytes[16] | `writer_id` |
| 2 | u64 | `sequence` — strictly increasing per writer, no gaps |
| 3 | bytes[16] | `predecessor_delta_id` — absent for a writer's first delta |
| 4 | u64 | `generation` |
| 5 | u16 | `shard` |
| 6 | array | `covered_blob_ids` — array of bytes[16] |
| 7 | array | `entries` (§2.1) |
| 8 | bool | `is_void` — present and true only for a void delta (§4) |
| 9 | bytes[64] | `signature` — Ed25519 over the canonical encoding of **every other key present**; semantics as [06 §6.1](06-manifests.md#61-signature): repository-scoped, verified against the derived signing key for `generation` |
| 10 | array | `covered_blob_digests` — array of bytes[32], parallel to `covered_blob_ids` (§2.2) |

The signature covers every key except itself, stated that way rather than as a numeric range so that a key added later is signed by construction. Key 10 is the first such key; nothing sorts a signed key after the signature by accident.

> **Erratum (phase 0).** Three resolutions pending normative edits, all per [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md): (1) "encrypted as a metadata record" is under-specified for an object outside a blob — the `FBPKSREC` standalone framing (Decision 1) supplies the encryption context, with object type `0x08`; (2) `<delta-id>` is 16 CSPRNG bytes rendered base32 (Decision 2); (3) key 5 `shard` is **optional** — present only when every entry falls in that one shard, absent otherwise (Decision 4), since §8 permits multi-shard deltas that a scalar cannot describe.

### 2.1 Index entry

CBOR array of six elements:

```text
entry = [ object_id, blob_id, physical_offset, stored_length, profiles, entry_type ]
```

| Position | Type | Value |
|----------|------|-------|
| 0 | bytes[32] | `object_id` |
| 1 | bytes[16] | `blob_id` |
| 2 | u64 | `physical_offset` |
| 3 | u32 | `stored_length` |
| 4 | u32 | `profiles` — compression in the high 16 bits, encryption in the low 16 |
| 5 | u8 | `entry_type` — 1 insertion, 2 supersession |

### 2.2 Covered blob digests

`covered_blob_digests` is OPTIONAL. When present it MUST have exactly the length of `covered_blob_ids`, and element *i* MUST be the SHA-256 digest of the blob named by `covered_blob_ids[i]`, computed as [05 §5](05-blob.md#5-sealing) defines it — over bytes `[0, blob_length − 16)`, everything up to but excluding the locator.

A reader that holds a blob and a delta carrying its digest can establish that the bytes it has are the bytes the writer sealed, **without trusting the store and without decrypting anything**. That is what a replication receipt needs: a participant receiving a blob checks it against a digest that is signed, published, and discoverable in the same object that declares the blob covered. A device-local record cannot serve that purpose, because the participant checking it is not the device that wrote it.

It is optional because a writer that publishes no digest is not wrong — the format's integrity rests on per-record AEAD tags, and the digest adds a cheaper whole-blob check rather than a necessary one. A reader MUST NOT treat absence as damage. A reader MUST treat a **present** digest that does not match the blob as a damage finding, and MUST NOT fall back to the record tags to decide the blob is fine: the tags authenticate records, and a blob whose sealed bytes differ from what was signed is a different question.

A length mismatch between the two arrays is a malformed object, and a reader MUST refuse the delta rather than pair the elements it can.

> **Erratum resolved (phase 1).** [05 §5](05-blob.md#5-sealing) said the digest was "recorded in the index" when no index field carried it, and [Q16](../../docs/open-questions.md#closed) tracked the gap. This section is the field. The device-local catalogue keeps its copy as a cache; this is the durable one.

## 3 Precedence

**Index entries are not commutative, and a reader MUST NOT assume they are.**

Blob compaction relocates a record: it writes the same `object_id` into a new blob and publishes a new entry pointing there. The same identifier therefore legitimately appears in two deltas with different locations, and applying them in the wrong order resolves to a blob that has since been tombstoned and deleted.

An earlier draft of the design justified merging concurrent checkpoints on the grounds that deltas were "immutable, idempotent, and commutative". That was false as soon as compaction existed, and the merge rule had nothing under it. → [PT-2](../../docs/review/2026-08-fix-pressure-test.md#pt-2--c6s-commutativity-claim-is-false-once-c1-is-in-place)

Precedence is therefore explicit:

1. For a given `object_id`, the entry published at the **highest generation** wins.
2. At equal generation, the entry from the higher `(writer_id, sequence)` wins, comparing `writer_id` as unsigned bytes then `sequence` numerically. This tie-break exists so behaviour is defined rather than accidental; it should not arise for a supersession, since compaction is its only producer.
3. An entry whose `blob_id` names a blob known to be deleted MUST be treated as superseded even if it wins on generation, and reported as a damage finding.

Two **insertions** for one `object_id` are benign: two writers independently stored the same content in different blobs, and either location serves. A **supersession** is ordered and must be honoured.

With precedence stated, applying entries in any order converges on the same state — because the winner is a property of the entries, not of arrival sequence. That is what makes §6 safe.

## 4 Sequence gaps and void deltas

A writer's `sequence` is gapless. A reader holding any delta from a writer can walk backwards via `predecessor_delta_id` and **detect** that it is missing a sequence number rather than assuming it has seen everything. That detection is what defends against truncation and rollback.

Detection alone is insufficient. A writer that prepares delta *N*, crashes before publishing, and resumes at *N+1* leaves a gap that will never be filled. A reader that blocks on it loses that writer's contributions permanently; a reader that ignores it throws away the defence.

**A writer that discovers it has skipped a sequence MUST publish a void delta** at that number: a well-formed, signed delta with `is_void = true`, empty `entries`, and empty `covered_blob_ids`.

A reader:

- MUST treat a gap as unresolved until either the delta or its void record appears;
- MUST NOT interpret silence as an empty delta;
- MUST, after a bounded number of generations with neither, surface the gap as a damage finding rather than blocking indefinitely.

→ [PT-6](../../docs/review/2026-08-fix-pressure-test.md#pt-6--a-crashed-writer-can-permanently-block-readers-through-a-sequence-gap)

## 5 Checkpoint

Stored at `/index/checkpoint/<generation>/<checkpoint-id>`.

| Key | Type | Value |
|-----|------|-------|
| 1 | u64 | `generation` |
| 2 | array | `subsumed_delta_ids` — the exact deltas this checkpoint covers |
| 3 | array | `writer_watermarks` — array of `[writer_id, highest_sequence]` |
| 4 | array | `shard_set` — every shard this checkpoint covers |
| 5 | array | `shard_hashes` — SHA-256 per shard, parallel to `shard_set` |
| 6 | bytes[16] | `predecessor_checkpoint_id` |
| 7 | array | `entries` — as §2.1 |
| 8 | bytes[16] | `writer_id` |
| 9 | bytes[64] | `signature` — Ed25519 over the canonical encoding of keys 1–8; semantics as [06 §6.1](06-manifests.md#61-signature) |

> **Erratum (phase 0).** Pending normative edits, per [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md): `<checkpoint-id>` is 16 CSPRNG bytes rendered base32 (Decision 2); the object is sealed under the `FBPKSREC` standalone framing with object type `0x09` (Decision 1); key 3's elements are `[bytes[16], u64]` pairs, key 4's are u16 shard values; and key 5's preimage — unstated above — is pinned by Decision 6: `shard_hashes[i]` = SHA-256 over the deterministic CBOR array of shard `shard_set[i]`'s post-precedence entries in §2.1 form, sorted by `object_id` bytes ascending.

A reader applies a checkpoint and then **any delta whose `sequence` exceeds that checkpoint's watermark for its writer** — whether or not a store listing revealed it. This is what removes the dependency on listing freshness: a delta written moments ago and not yet visible in a listing is still applied when found, and its absence is detectable via the sequence chain.

## 6 Concurrent checkpoints

Two writers may publish a checkpoint at the same generation. **Both are retained and both applied.**

This is safe because of §3, not because entries commute. There is no election, no lock, and no leader — the outcome is determined by the entries themselves.

## 7 Delta retirement

A delta may be retired only when it is named by a checkpoint that **no live checkpoint at or above its generation contradicts** — in practice, when every retained checkpoint at that generation names it, or a later checkpoint supersedes them all.

Retiring a delta on one checkpoint's authority while another live checkpoint omits it would strand its entries for any reader holding only the second. → [PT-7](../../docs/review/2026-08-fix-pressure-test.md#pt-7--delta-retirement-is-ambiguous-under-concurrent-checkpoints)

Retirement is a deletion and follows the same discipline as any other: tombstone, grace period, revalidate, delete.

## 8 Sharding

`shard` is the **top 16 bits of the object identifier**, giving 65 536 shards. A reader resolving one file fetches only the shards covering its segments — the index never has to be loaded whole.

A writer MAY publish a single delta spanning multiple shards for a small job. A checkpoint MUST enumerate every shard it covers in `shard_set`, so a reader can tell the difference between "this shard has no entries" and "this checkpoint does not cover this shard".

## 9 Filters

A writer MAY publish Bloom or XOR filters alongside a checkpoint to accelerate negative lookups.

A filter is **never authoritative evidence of absence**. A negative filter result MAY skip a fetch; a positive result MUST be confirmed against the actual entries. An implementation that treats a filter as proof will eventually report a segment as missing when it is present.

## 10 Rebuilding without the index

If every delta and checkpoint is lost, the index is reconstructable by scanning sealed blobs and reading their recovery footers ([05 §3](05-blob.md#3-recovery-footer)). Every field in an index entry is present in the footer.

A scan MUST accept a **target** — a snapshot, a path, or a set of object identifiers — and order blobs so the target's records are located first. Without targeting, recovering one document from a damaged repository costs a whole-repository scan, which is the wrong behaviour at exactly the moment a user is in the worst position. → [PT-10](../../docs/review/2026-08-fix-pressure-test.md#pt-10--emergency-single-file-restore-regressed-from-one-fetch-to-a-full-scan)

Rebuild reports; it never repairs. Conflicting or duplicate mappings are retained as findings, not silently resolved. → [`02-repository-format.md` §8.3](../../docs/architecture/02-repository-format.md#83-rebuild-never-repairs)

---

**Previous:** [06 — Manifests](06-manifests.md) · **Next:** [08 — Journal](08-journal.md)

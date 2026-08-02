# 02 — Identifiers

**Normative.** Derived from [`03-crypto.md` §4](../../docs/architecture/03-crypto.md#4-object-identifiers) and [ADR-0006](../../docs/adr/0006-object-identifiers-and-dedup-trust-domains.md), [ADR-0016](../../docs/adr/0016-blob-identifier-formation.md).

---

## 1 Three kinds of identifier

This format uses three, and confusing them is the most likely way to build something that appears to work and is subtly wrong.

| Identifier | Derived from | Size | Appears in | Leaves the trust boundary |
|-----------|--------------|------|-----------|---------------------------|
| **Content identifier** | Plaintext content | 32 bytes | Nothing durable | **Never** |
| **Object identifier** | Content identifier, keyed | 32 bytes | Manifests, index, footers, store keys | Yes |
| **Blob identifier** | Writer allocation | 16 bytes | Index, journal, store keys | Yes |

## 2 Content identifier

```text
content_id = H(plaintext)
```

where `H` is the hash named by the repository's **content-hash profile**.

| Profile | Value | Function | Output |
|---------|-------|----------|--------|
| `sha-256-v1` | `0x0001` | SHA-256 | 32 bytes |

The content identifier is computed over the segment's **plaintext**, before compression and before encryption. It is what deduplication compares and what restore verifies against.

It MUST NOT be written to any durable object, and MUST NOT appear in any store key. A raw content hash in the store would let the operator hash a file they already possess and test whether the repository contains it — which is precisely the confirmation attack keyed identifiers exist to prevent. → [`threat-model.md` T-1](../../docs/threat-model.md#t-1-untrusted-store-reads-repository-content)

The content identifier MAY be held in the local catalogue, which is inside the trust boundary.

### 2.1 Profile choice

SHA-256 is the v1 profile. It is in-box on every platform the recovery tool must run on, hardware-accelerated on current hardware, and universally available to an independent implementer — which outranks throughput here, since the recovery tool must build with no native dependency. The profile field exists so a faster function can be added later without a format break. → [ADR-0004](../../docs/adr/0004-segment-hash-function.md), [Q6](../../docs/open-questions.md#q6--segment-hash-function)

The full 32 bytes are used. Truncation would save catalogue space at the cost of second-preimage resistance, and deduplication decisions are made on this value — a collision is a data-corruption path, not a performance detail.

## 3 Object identifier

```text
object_id = HMAC-SHA256(
                key     = content_id_key,
                message = object_type ‖ content_id
            )
```

- `content_id_key` is derived per [03 §4](03-keys.md#4-derived-keys) and is the same for every writer in the repository.
- `object_type` is a single byte (§3.1).
- The result is 32 bytes, used in full.

Because the key is repository-scoped, two writers in the same repository derive the same object identifier for the same content — which is what makes cross-device deduplication possible — while a party without the key cannot derive it at all.

### 3.1 Object types

| Type | Value | Object |
|------|-------|--------|
| Segment record | `0x01` | A segment of file content |
| File-version manifest | `0x02` | One version of one file |
| Tree manifest | `0x03` | A directory |
| Snapshot manifest | `0x04` | A snapshot root |
| Policy manifest | `0x05` | Effective capture configuration |
| Error manifest | `0x06` | Uncapturable paths |

Binding the type into the derivation means a record cannot be reinterpreted as a manifest, or the reverse, even if their plaintexts were somehow identical.

### 3.2 What this does and does not protect

Keying defends against the **store**. It does not defend against a **repository member**, because members hold the key.

A member can therefore publish a record whose claimed object identifier does not match its actual plaintext, and a second device that deduplicates against it silently stores corrupt data. Detection at restore time is too late — the source file is gone by then.

This is why deduplication is governed by a **trust domain** rather than being unconditional, and why the default requires verifying another writer's segment before referencing it. The mechanism is in [06 §3](06-manifests.md#3-segment-references); the reasoning is [ADR-0006](../../docs/adr/0006-object-identifiers-and-dedup-trust-domains.md) and [T-10](../../docs/threat-model.md#t-10-malicious-repository-member-poisons-deduplication).

## 4 Blob identifier

A blob identifier is **16 bytes, allocated by the writer, and not derived from content**.

```text
blob_id = writer_id[0..8] ‖ u64(blob_counter)
```

`blob_counter` is a per-writer monotonic counter drawn from the writer's journal sequence space. An implementation MAY instead use 16 random bytes from a CSPRNG; both satisfy the requirement, and a writer MAY combine them by keying the concatenation, provided the result is unique and allocatable in advance.

### 4.1 Why this is not content-derived

Every *record* identifier in this format is content-derived, so a reader will reasonably expect blob identifiers to be too. They are not, and the asymmetry is required rather than incidental.

A writer must name the blobs it is about to create in a **write-intent journal record before creating them**, so that a concurrent garbage collector treats them as reachable during the window when they are durable but referenced by nothing. An identifier derived from content that does not yet exist cannot be named in advance, and the intent mechanism — and with it the guarantee that GC does not delete a running job's data — would be unimplementable. → [ADR-0016](../../docs/adr/0016-blob-identifier-formation.md), [08](08-journal.md)

A blob is a container, not a content-addressed object. Nothing deduplicates blobs, nothing verifies a blob by recomputing its identity, and two blobs holding the same records in a different order are not interchangeable.

### 4.2 Integrity

A blob identifier carries **no integrity property**. It is a name, not a checksum. Integrity comes from the authenticated recovery footer and the blob digest ([05](05-blob.md)), and a reader MUST NOT treat a matching blob identifier as evidence that a blob's contents are correct.

### 4.3 Not leaking writer identity

`blob_id` as constructed above embeds part of the writer identity, which would let a store operator partition objects by device. Before use as a store key, a writer MUST render the identifier as:

```text
store_blob_key = HMAC-SHA256(key = key_id_key, message = 0x07 ‖ blob_id)[0..16]
```

The mapping is deterministic and reversible only by a holder of `key_id_key`, so writers and readers agree on the key while the store learns nothing from it.

## 5 Rendering

Identifiers appearing in store keys are rendered as lowercase unpadded base32 ([00 §6](00-conventions.md#6-object-identifiers-in-paths)):

| Identifier | Bytes | Base32 characters |
|-----------|-------|-------------------|
| Object identifier | 32 | 52 |
| Blob store key | 16 | 26 |
| Repository ID | 16 | 26 |

Identifiers inside CBOR objects are encoded as CBOR byte strings, **not** as text. Rendering is a store-key concern only.

## 6 Test vectors

Vectors for content identifiers, object identifiers, and store-key derivation are in [`conformance/vectors/identifiers.json`](conformance/vectors/identifiers.json), generated by [`conformance/generate.py`](conformance/generate.py).

These vectors are computed independently of the reference implementation, using only SHA-256 and HMAC-SHA256 from a standard library. An implementer can reproduce them in any language without trusting our code.

---

**Previous:** [01 — Object layout](01-object-layout.md) · **Next:** [03 — Keys](03-keys.md)

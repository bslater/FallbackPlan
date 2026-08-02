# 09 — Segmentation

**Normative.** Derived from [`02-repository-format.md` §3](../../docs/architecture/02-repository-format.md#3-segmentation) and [ADR-0002](../../docs/adr/0002-segmentation-strategy.md).

---

## 1 Profiles

Segmentation is selected **per backup set** and recorded **per file version** ([06 §4](06-manifests.md#4-file-version-manifest) key 8), so a repository may contain both and a reader never has to infer which was used.

| Profile | Value | Status |
|---------|-------|--------|
| `fixed-v1` | `0x0001` | v1 default |
| `cdc-v1` | `0x0002` | Specified in v1, not the default |

Both are specified now. Deferring the second would leave the profile field unexercised, and an unexercised extension point is one you discover is insufficient at the worst moment. Whether the default changes is decided by a corpus benchmark before the format freezes. → [Q5](../../docs/open-questions.md#q5--segmentation-default)

## 2 `fixed-v1`

### 2.1 Definition

Given segment size *S* and file length *L*, the file is divided at fixed offsets from byte 0:

```text
segment i  covers  [ i·S , min((i+1)·S, L) )        for i in 0 .. ceil(L/S)-1
```

Every segment has length exactly *S* except the last, which has length `L - (n-1)·S` and MAY be shorter. Only the final segment may be short.

A file of length 0 produces **no segments**. Its manifest has an empty `segment_references` array, `logical_length` 0, and a `whole_file_hash` over the empty byte string. It is not a special case elsewhere in the format.

### 2.2 Parameters

| Parameter | Default | Range |
|-----------|---------|-------|
| Segment size *S* | 1 MiB | 64 KiB – 64 MiB |

*S* MUST be a power of two. This is not required by the arithmetic — it makes the offset-to-segment mapping a shift rather than a division, and eliminates a class of rounding disagreement between implementations.

*S* is recorded in the policy manifest ([06 §7](06-manifests.md#7-policy-manifest)) and MUST NOT vary within a single file version.

### 2.3 Properties

Fixed segmentation is genuinely the better choice for a large share of the bytes that actually churn:

- **In-place rewrites** — virtual-machine disks, database files, mailbox stores. A modified region maps to a bounded set of segments, and positional comparison against the prior version is exact and cheap.
- **Deterministic fixtures.** Every conformance vector is reproducible from a byte string and a segment size, with no rolling-hash parameters to agree on.
- **Random access.** Byte offset *N* lives in segment `N >> log2(S)`, with no index walk.

### 2.4 The weakness

**Inserting or removing bytes shifts every subsequent boundary**, so a one-byte insertion at the front of a file makes the entire file appear new.

This is not a corner case. It is the normal behaviour of:

- files that grow at the front, such as prepended logs;
- recompressed containers — `.docx`, `.xlsx`, `.zip`, `.odt` — where a one-character edit changes nearly every byte;
- SQLite files after `VACUUM`, which shifts page contents;
- editors that write a new file and rename over the original with different leading bytes.

An implementer should understand this clearly rather than discovering it from a storage bill. It is the entire reason `cdc-v1` is specified alongside.

## 3 `cdc-v1`

### 3.1 Definition

Content-defined chunking using a 64-bit rolling hash over a sliding window. A boundary is declared at position *p* when:

```text
(rolling_hash(p) & mask) == 0        and    current_length >= min_size
```

or unconditionally when `current_length == max_size`.

| Parameter | Default | Range |
|-----------|---------|-------|
| Target size | 1 MiB | 64 KiB – 16 MiB |
| Minimum size | 256 KiB | ≥ target / 8 |
| Maximum size | 8 MiB | ≤ target × 8 |
| Window size | 64 bytes | fixed in v1 |
| Mask | `target_size − 1` | derived; target MUST be a power of two |

The rolling hash is a Rabin-style polynomial fingerprint. The polynomial and the per-byte table are fixed in v1 and reproduced in [`conformance/vectors/segmentation.json`](conformance/vectors/segmentation.json) — an implementation MUST use those exact values, because two implementations with different tables would produce different boundaries and deduplicate against nothing.

### 3.2 Properties

An insertion changes only the segments near it; boundaries re-synchronise within roughly one average segment. That is the case `fixed-v1` handles badly.

The cost is that positional comparison no longer works — comparison is by content identifier across the whole prior version — and that fixtures depend on getting the rolling-hash parameters exactly right.

## 4 Sparse extents

A region of a file known to be sparse is recorded in `sparse_extents` ([06 §4](06-manifests.md#4-file-version-manifest) key 6) rather than segmented and stored.

```text
sparse_extent = [ offset, length ]
```

Extents MUST be ordered by ascending offset, MUST NOT overlap each other or any segment reference, and together with segment references MUST cover `[0, logical_length)` exactly.

A restore materialises them as zeroes where the target filesystem does not support sparseness, and as holes where it does. The `whole_file_hash` is computed over the materialised form — zeroes included — so a file restored to a filesystem without sparse support still verifies.

## 5 Deduplication domain

A segment is reusable only when **all** of the following match: content identifier, logical length, segmentation profile, and segmentation parameters.

Profile and parameters are part of the key because a 1 MiB `fixed-v1` segment and a 1 MiB `cdc-v1` segment with identical bytes are not interchangeable — reusing one for the other would produce a manifest whose declared profile does not describe how its segments were actually produced.

Reuse is further constrained by the **dedup trust domain** ([`03-crypto.md` §5](../../docs/architecture/03-crypto.md#5-deduplication-trust-domains)):

| Domain | Reuse permitted from |
|--------|---------------------|
| `device` | Segments this device wrote |
| `repository` (default) | Any writer, **after** fetching, decrypting, and confirming the content identifier |
| `repository-unverified` | Any writer, unverified |

Under `repository`, a writer MUST perform the verification before emitting a segment reference to another writer's record. Skipping it means trusting a claim it cannot check, and the failure — silently storing corrupt data, discovered at restore when the source is gone — is exactly what the domain exists to prevent. → [ADR-0006](../../docs/adr/0006-object-identifiers-and-dedup-trust-domains.md)

Writer attribution is recoverable from index delta `writer_id`, so `device` mode survives a catalogue rebuild.

## 6 Capture algorithm

For each file version:

1. Read the file as a bounded stream.
2. Divide into segments per the profile.
3. Compute each segment's content identifier.
4. Compare against the corresponding segment of the prior version — positionally under `fixed-v1`, by content under `cdc-v1` — then against the reusable-segment index within the trust domain.
5. Reuse the existing object identifier where content identifier, logical length, profile and parameters all match.
6. Otherwise compress, encrypt, authenticate, and append to the open blob.
7. Record `[logical_offset, logical_length, object_id]` in the manifest.

Memory is bounded by pipeline concurrency and segment size, never by file size, file count, or repository size. A 2 TiB file costs no more resident memory than a 2 MiB one. → NFR-PERF-001

---

**Previous:** [08 — Journal](08-journal.md) · **Next:** [10 — Compression](10-compression.md)

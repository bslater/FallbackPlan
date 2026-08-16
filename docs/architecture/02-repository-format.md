# 02 — Repository format

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §7.1–7.5, §7.7–7.9 · **Resolves:** [C1](../review/2026-08-architecture-review.md#c1--immutable-manifests-embed-physical-locations-that-compaction-changes), [C6](../review/2026-08-architecture-review.md#c6--checkpoint-compaction-requires-a-complete-listing-the-design-forbids-relying-on), [H1](../review/2026-08-architecture-review.md#h1--fixed-size-segmentation-is-under-argued-and-its-review-is-scheduled-after-the-point-of-no-return)

**Built:** Yes — implemented and covered by conformance vectors — see [implementation status](../implementation-status.md).

---

## 1. Format goals

The repository format must be documented · append-oriented · content-addressed · encrypted with no plaintext mode · resilient to partial writes · efficient on local disks and object storage · safe for concurrent readers and multiple snapshot writers · reconstructable without a local database · self-describing by version · independently implementable · testable against public conformance vectors · migratable without rewriting all content for every minor change.

## 2. Object classes

```text
/repository-format                                  format profile, repository ID, feature set
/keys/<key-id>                                      wrapped key material
/blobs/data/<shard>/<store-blob-key>                segment records
/blobs/meta/<shard>/<store-blob-key>                manifest and tree records
/index/delta/<generation>/<delta-id>                immutable writer index deltas
/index/checkpoint/<generation>/<checkpoint-id>      compacted index generations
/snapshots/<device-id>/<backup-set-id>/<snapshot-id>
/journal/<writer-id>/<sequence>                     write intents, publications, audit
/leases/<scope>/<lease-id>                          advisory only
/tombstones/<object-type>/<object-id>
/audit/<period>/<record-id>
```

Physical naming must not leak source names, user names, paths, timestamps, or raw content hashes. Every identifier exposed to a store is a **keyed** identifier — see [`03-crypto.md` §4](03-crypto.md#4-object-identifiers). (An earlier revision of this diagram spelled the delta path segment `<index-id>`; the specification's name is `<delta-id>`, and its allocation is pinned by [ADR-0022](../adr/0022-standalone-metadata-records-and-index-identifiers.md) §Decision 2.)

`/repository-format` is the only object a reader can locate without prior knowledge. It is small, unencrypted in its structural fields, and states the repository ID, format version, required feature set, and the key-derivation parameters needed to get everything else. A reader that does not understand a **required** feature refuses the repository rather than guessing (NFR-COMP-003).

## 3. Segmentation

### 3.1 Profiles

Segmentation is described by a **profile** recorded per file version, so a repository may contain more than one and readers never have to infer which was used.

| Profile | Description | Status |
|---------|-------------|--------|
| `fixed-v1` | Equal-length segments at fixed offsets from byte 0. Only the final segment may be shorter. | v1 default |
| `cdc-v1` | Content-defined boundaries from a rolling hash, with min/target/max sizes. | **Specified in v1**, not the default |

`cdc-v1` is specified now rather than deferred. The original proposal left it to "a later comparative design spike" scheduled *after* the format would be frozen ([H1](../review/2026-08-architecture-review.md#h1--fixed-size-segmentation-is-under-argued-and-its-review-is-scheduled-after-the-point-of-no-return)). Writing it into the specification today costs little — the profile field already exists — and it forces us to prove the field is actually expressive enough to describe a content-defined scheme, which is the expensive thing to discover late.

The profile is selected **per backup set**, not per repository. A set holding VM images and a set holding a documents folder want different answers and there is no reason to force one choice on a repository containing both.

### 3.2 Why fixed-size is the v1 default

Fixed-size segmentation is genuinely the better choice for a large share of the bytes that actually churn:

- **In-place rewrites** — VM disk images, database files, mailbox stores, disk images. Modified regions map to a bounded set of fixed segments, and positional comparison against the prior version is exact and cheap.
- **Deterministic fixtures.** Every conformance vector is reproducible from a byte string and a segment size, with no rolling-hash parameters to agree on.
- **Random-access restore.** Byte offset *N* is in segment `N / segment_size`, with no index walk.
- **Simple version comparison.** Segment *i* of version *n* compares directly against segment *i* of version *n−1*.

Its weakness is equally specific: **inserting or removing bytes shifts every subsequent boundary**, so a one-byte insertion at the front of a file rewrites the whole file. That is the common case for prepended logs, some container formats, and files rewritten wholesale on save (`.docx`, `.xlsx`, `.zip` — recompressed containers where a one-character edit changes nearly every byte), and for SQLite files after a `VACUUM`.

### 3.3 The freeze gate

Because the product promise is efficient long version history, this trade-off is not something to discover after users have committed data. Therefore:

> **Gate.** Format v1 shall not be frozen until `fixed-v1` and `cdc-v1` have been benchmarked against a representative corpus, and the measured deduplication ratio, storage growth, and CPU cost of each are published.

If `cdc-v1` wins decisively on that corpus, the default changes while changing it is still free. See [`../roadmap.md`](../roadmap.md#format-v1-freeze-gate).

### 3.4 Capture algorithm

For each file version:

1. read the file as a bounded stream;
2. divide it into segments per the backup set's segmentation profile;
3. compute the plaintext content identifier of each segment;
4. compare against the corresponding segment of the prior version, then against the reusable-segment index within the applicable [dedup trust domain](03-crypto.md#5-deduplication-trust-domains);
5. reuse the existing segment's **object identifier** where the content identifier, logical length, and segmentation profile all match;
6. otherwise compress if beneficial, encrypt and authenticate independently, and append the record to the open blob;
7. record `(logical offset, logical length, object identifier)` in the file-version manifest.

A file version therefore writes only new or changed segments. A file may span any number of blobs; a blob may hold segments from many files and versions.

### 3.5 Configuration envelope

| Setting | Default | Range |
|---------|---------|-------|
| `fixed-v1` segment size | 1 MiB | 64 KiB – 64 MiB |
| `cdc-v1` target / min / max | 1 MiB / 256 KiB / 8 MiB | target 64 KiB – 16 MiB |

Segment size is a validated profile value, never an arbitrary per-file value. Sparse extents are represented as logical zero extents, not materialised payload. The pipeline never loads a whole file into memory (NFR-PERF-001). Hashing is pipelined with reading and uses hardware acceleration where available.

## 4. Compression

Compression happens **before** encryption, using a bounded-memory codec (Zstandard is the v1 codec). The codec and level are recorded per record.

A segment is stored uncompressed when compression saves less than a configured fraction of its length (default: 5%), so incompressible data is not paid for twice. The choice is recorded per record so a reader never has to guess and a benchmark can measure how often it fires.

Compressing before encrypting is correct for efficiency and it is also what creates a length side channel: stored record lengths reveal compressed sizes, which fingerprint file types and sometimes individual files. That trade is stated deliberately, and the optional mitigation is in [`../threat-model.md`](../threat-model.md#t-11-metadata-side-channels).

## 5. Blobs

### 5.1 Purpose and sizing

Blobs amortise per-request cost and latency on object stores while keeping every segment record independently encrypted, authenticated, and retrievable by range.

Sizing comes from a versioned write profile:

| Setting | Local / peer default | Object-store default |
|---------|---------------------|----------------------|
| Target blob size | 64 MiB | 128 MiB |
| Hard maximum | 256 MiB | 512 MiB |
| Maximum open-blob age | 15 min | 15 min |
| Maximum records per blob | 65 536 | 65 536 |

Supported range is 8 MiB to the provider-safe limit reported by the store's capability record. Metadata blobs use smaller independent targets. Maximum open-blob age exists so a low-churn backup set still commits within a bounded time rather than waiting indefinitely to fill a blob.

A segment record is **never split across blobs** in format v1. When the open blob cannot hold the next complete record within its maximum, it is sealed and the record starts a new blob.

### 5.2 Layout

```text
+-------------------------------------------------------------+
| Cleartext envelope  (88 bytes, fixed)                        |
|   magic, format version, blob class, key generation,         |
|   blob identifier, blob salt, blob counter, writer identity  |
+-------------------------------------------------------------+
| Record 0   authenticated header + AEAD ciphertext            |
| Record 1   authenticated header + AEAD ciphertext            |
| …                                                            |
+-------------------------------------------------------------+
| Recovery footer  (authenticated)                             |
|   per record: object identifier, ordinal, physical offset,   |
|               stored length, logical length, object type,    |
|               compression profile, encryption profile        |
+-------------------------------------------------------------+
| Footer locator  (16 bytes: footer offset, digest prefix)     |
+-------------------------------------------------------------+
```

The cleartext envelope carries only non-sensitive selectors — everything needed to derive the blob key and pick a parser, nothing about content. The blob salt, writer identity and blob counter are the inputs to per-blob key derivation, which is why all three are carried explicitly. The blob digest is computed at sealing and recorded in the **index**, not appended to the blob; the trailing element on disk is the 16-byte footer locator. The normative byte layout is [specification 05](../../specifications/repository-format/05-blob.md); the derivation itself is [`03-crypto.md` §3](03-crypto.md#3-nonce-and-key-construction).

The **recovery footer is the point of the whole structure**. It makes a blob self-describing: given the repository key material and the blob alone, every record in it can be located, decrypted, and verified with no index and no catalogue. That is what makes forensic rebuild (§8.2) possible and what bounds the blast radius of losing every index object.

For providers where reading the tail is expensive or unreliable, the profile may enable redundant footer copies or a small authenticated sidecar object.

### 5.3 Spooling and sealing

Blobs are assembled in a durable local spool. A blob becomes visible only after it is sealed, validated, uploaded under its final immutable identifier, and acknowledged.

**Blob identifiers are writer-allocated and opaque** — random, or derived from `(writer_id, sequence)`. They are **not** content-derived. This is a deliberate asymmetry with record identifiers, which *are* content-derived and then keyed ([`03-crypto.md` §4](03-crypto.md#4-object-identifiers)), and independent implementers should note it: a blob is a container, not a content-addressed object, and it is the records inside it that deduplication and verification address.

The asymmetry is required rather than incidental. A writer must name the blobs it is about to create in its write-intent record *before* creating them ([`04-concurrency-and-publication.md` §4](04-concurrency-and-publication.md#4-write-intent)), which is impossible for an identifier derived from content that does not yet exist. Writers therefore pre-allocate identifiers, declare them, and then fill them. See [ADR-0016](../adr/0016-blob-identifier-formation.md).

Interrupted construction either **resumes** from a verified spool checkpoint or is **abandoned**, in which case the partial spool is discarded and never uploaded.

Resume re-emits the **sealed record bytes held in the checkpoint**. It does not recompute them from plaintext: recompression is not guaranteed reproducible across codec versions, and recomputing would risk encrypting different bytes under an already-used `(key, ordinal)` pair. The checkpoint therefore stores the sealed bytes plus every parameter that could vary — blob salt, writer ID and blob counter, segmentation profile, compression codec **and version**, encryption profile — and any mismatch on resume forces a restart instead. Full reasoning in [`03-crypto.md` §3.3](03-crypto.md#33-why-resumption-is-safe).

On providers without atomic rename, publication relies on creating a unique final object, followed by index-delta and snapshot publication in the order given in [`04-concurrency-and-publication.md` §5](04-concurrency-and-publication.md#5-publication-order).

## 6. Manifests

### 6.1 Immutable metadata objects

| Object | Contents |
|--------|----------|
| **Segment reference** | Logical file offset, logical length, **object identifier**. Nothing physical. |
| **File-version manifest** | Path identity, logical length, ordered segment references, sparse extents, whole-file verification hash, timestamps, permissions, attributes, alternate-stream/resource-fork references, hard-link identity, parent file-version reference, capture diagnostics |
| **Tree manifest** | Sorted directory entries referencing child trees and file-version manifests |
| **Snapshot manifest** | Source, backup set, capture details, root tree, parent snapshot, policy version, errors, publication generation, signature |
| **Policy manifest** | The exact effective configuration used for capture: segmentation, compression, encryption, and blob profiles |
| **Error manifest** | Paths and metadata that could not be captured, with reasons |

### 6.2 Manifests hold logical facts only

This is the correction from [C1](../review/2026-08-architecture-review.md#c1--immutable-manifests-embed-physical-locations-that-compaction-changes) and it is worth stating as a rule rather than a detail:

> A segment reference inside a manifest contains **no blob identifier and no physical offset**. It names the segment by object identifier. Resolving that to a physical location is the index's job, and the blob recovery footer's job when the index is gone.

The original design put `blob ID` and `record offset` in the manifest. Blob compaction — which exists precisely to move live records out of mostly-dead blobs — changes both. Manifests are declared immutable in five places, so compaction would have had to either rewrite immutable objects or strand every manifest that referenced a moved record.

With the physical layer behind an indirection, compaction republishes index entries and touches no manifest, no tree, and no snapshot. Immutability survives, and so does maintenance.

The cost is one index lookup per segment on the restore path. That is bounded, local, indexed, and measured against NFR-PERF-004 — a good trade for making the maintenance story correct.

That accounting holds while the index is healthy. When it is not, the cost is larger and should be stated plainly: before this change, a manifest plus a blob was enough to recover a file, because the manifest said where the bytes were. Now recovering a single file with no index means scanning blob recovery footers until its segments are located. A user who has lost their machine, holds the recovery kit, and wants one 4 MiB document could wait through a scale-**M** footer scan — hours — before that document can be produced, which is not what FR-MAN-010 promises ([PT-10](../review/2026-08-fix-pressure-test.md#pt-10--emergency-single-file-restore-regressed-from-one-fetch-to-a-full-scan)).

Two mitigations, neither of which reverses the decision:

- **Prioritised footer scanning.** Forensic rebuild accepts a target snapshot or path and orders its scan by the target's segment identifiers, so single-file recovery does not wait for a whole-repository rebuild (§8.2).
- **An optional physical hint** on the segment reference — a non-authoritative `last_known_blob` that a reader may try first and must validate against the record's authenticated object identifier, falling back to the index on mismatch. This restores O(1) first-byte latency in the index-lost case. It is **not** currently part of the format: it partially re-couples manifests to physical layout, and the decision is the maintainer's ([Q11](../open-questions.md#q11--physical-hints-in-segment-references), [ADR-0007](../adr/0007-logical-object-identifiers-in-manifests.md)).

### 6.3 Sharding and encoding

Manifests shard naturally through the tree and file-version graph. **No manifest's size grows with the repository or with total snapshot history** — the failure mode the consumer prior art documents for its own large manifests, and principle 6 in [`00-overview.md`](00-overview.md#3-core-principles). Small metadata objects are packed into metadata blobs but remain independently addressed and authenticated.

The encoding is deterministic, canonical, versioned, and independently implementable. Canonical CBOR is the candidate, pending the benchmark and cross-language determinism tests in [ADR-0003](../adr/0003-canonical-metadata-encoding.md). Wire protocols may use a different encoding; the two are versioned independently.

## 7. Index architecture

### 7.1 Structure

The index maps object identifiers to physical locations. It is never a single growing object:

- **Deltas** are immutable and writer-authored, published only after the blobs they cover are durable.
- **Sharding** is by keyed object-ID prefix and object class.
- **Checkpoints** are periodic authenticated generations that subsume prior deltas.
- **Filters** (Bloom or XOR) are optional lookup accelerators and are *never* authoritative evidence of absence.
- **Blob recovery footers** are the final physical recovery source.

Read path: catalogue → checkpoint + deltas → blob recovery footer. Fast in the common case, and each fallback is independently sufficient.

### 7.2 Deltas and checkpoints without a global listing

The original design required checkpoint compaction to know the complete set of prior deltas, whose only discovery mechanism was listing — while simultaneously forbidding correctness dependence on listing freshness ([C6](../review/2026-08-architecture-review.md#c6--checkpoint-compaction-requires-a-complete-listing-the-design-forbids-relying-on)). On an eventually consistent store, a compactor could silently omit a recent delta and every reader trusting the resulting checkpoint would lose the index entries for a set of blobs.

Five rules close it:

**1 — Deltas form per-writer chains.** Each delta carries `(writer_id, sequence, predecessor_delta_id)`, with `sequence` strictly increasing per writer and no gaps. A reader holding any delta from a writer can walk backwards, and — critically — can *detect* that it is missing a sequence number rather than silently assuming it has seen everything.

**2 — Gaps are closed explicitly, never by silence.** A writer that skips a sequence — it prepared delta *N*, crashed, and resumed at *N+1* — publishes a signed **void delta** at *N* declaring it intentionally empty. A reader treats a gap as unresolved until either the delta or its void record appears; after a bounded number of generations with neither, it surfaces the gap as a damage finding rather than blocking on it indefinitely. Silence is never read as "empty", so a crashed writer cannot render its own index contributions permanently unusable.

**3 — Checkpoints enumerate what they subsume.** A checkpoint lists the exact delta IDs it covers and the per-writer high-water sequence. A reader keeps applying any delta whose sequence exceeds the checkpoint's watermark for that writer, whether or not a listing revealed it.

**4 — Retirement requires uncontested coverage.** A delta may be retired only when it is named by a checkpoint that no live checkpoint at or above its generation contradicts — in practice, when every retained checkpoint at that generation names it, or a later checkpoint supersedes them all. Retirement is a deletion and takes the same tombstone-and-grace treatment as any other. Retiring a delta on one checkpoint's authority while another live checkpoint omits it would strand its entries for any reader holding only the second.

**5 — Conflicting checkpoints are merged under an explicit precedence rule.** Two writers may publish a checkpoint at the same generation. Both are retained and both applied.

Merging is **not** justified by commutativity. Index entries are not commutative: blob compaction republishes an object identifier at a new physical location ([§6.2](#62-manifests-hold-logical-facts-only), [`07-retention-and-gc.md` §3](07-retention-and-gc.md#3-garbage-collection)), so the same identifier legitimately appears in two deltas with different locations — and applying them in the wrong order resolves to a blob that has since been tombstoned and deleted. An earlier draft of this section claimed commutativity; it was false as soon as compaction existed ([PT-2](../review/2026-08-fix-pressure-test.md#pt-2--c6s-commutativity-claim-is-false-once-c1-is-in-place)).

Precedence is therefore explicit rather than emergent:

- every index entry carries the **generation** at which it was published;
- for a given object identifier, the entry with the **highest generation wins**, with a documented deterministic tie-break below that;
- relocation entries are **typed as supersessions**, so a reader can distinguish "two writers independently recorded the same new object" — genuinely order-independent — from "this object moved", which is not.

With precedence stated, merging is safe for the reason that actually holds: entries applied in any order converge on the same state, because the winner is a property of the entries themselves rather than of arrival sequence. See [ADR-0017](../adr/0017-index-entry-supersession.md).

Listing remains a useful accelerator for finding a recent checkpoint quickly. It is no longer load-bearing for correctness.

### 7.3 Compaction

Checkpoint compaction is bounded, resumable, and cancellable. Prior checkpoints are retained for a configurable safety window. Compaction never invalidates a prior generation — a reader mid-operation against generation *n* continues to work while *n+1* is published.

## 8. Catalogue rebuild

The catalogue is a cache. Two rebuild paths exist, and both must work.

### 8.1 Normal rebuild

Load the latest authenticated checkpoint, apply subsequent deltas per §7.2, validate referenced snapshot and blob objects. This is the fast path.

### 8.2 Forensic rebuild

Used when index objects are lost, damaged, or distrusted. Enumerate sealed blobs, read and authenticate each recovery footer, reconstruct object-to-blob mappings, enumerate snapshot roots and metadata manifests, rebuild all lookup tables — **without relying on any global index**.

For this to be practical:

- each blob recovery footer must be self-contained for physical record discovery;
- snapshot manifests must be discoverable from bounded prefixes;
- every delta must identify its writer, sequence, generation, shard, predecessor, and covered blobs;
- checkpoints must list their complete shard set and hashes;
- scanning must be parallel, bounded, resumable, and locally checkpointed;
- restore of a selected snapshot must become possible as soon as *its* dependency graph is known, without waiting for the full repository (FR-MAN-010);
- scanning must accept a **target** — a snapshot, a path, or a set of object identifiers — and order blobs so the target's segments are located first. Without this, recovering one document from a damaged repository costs a whole-repository scan, which is the regression PT-10 identifies;
- conflicts and duplicate mappings are retained as forensic findings, never silently resolved.

### 8.3 Rebuild never repairs

Rebuild produces a verified catalogue and a damage report. It does not rewrite or repair repository objects. Repair and replica healing are separate, explicitly invoked operations — see [`07-retention-and-gc.md` §6](07-retention-and-gc.md#6-healing-from-replicas).

Damage is reported by kind, because the kinds have different consequences and different remedies: catalogue corruption, missing index objects, missing blobs, corrupt records, and unreachable orphan data. Each report names the affected snapshots and file versions (FR-MAN-012).

---

**Previous:** [01 — Domain model](01-domain-model.md) · **Next:** [03 — Cryptography](03-crypto.md)

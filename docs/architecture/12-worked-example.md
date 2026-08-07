# 12 — Worked example: backing up one file, end to end

**Status:** draft · **Explanatory, not normative** — see the note below

**Built:** Yes — every step here is code you can run — see [implementation status](../implementation-status.md).

---

> **This document defines nothing.** Every rule it mentions is stated authoritatively in one of the numbered documents, and this one links there rather than restating it. Where the two disagree, **the numbered document wins** and this one is wrong.
>
> That constraint is deliberate. Principle 6 in [`00-overview.md`](00-overview.md#3-core-principles) rejects monolithic sources of truth, and a walkthrough that redefined rules would quietly become a second one. If you are tempted to copy a normative statement into this file, link to it instead.

Everything else in this set describes one concern at a time. This describes one *file* at a time, which is the view you need before any of the rest is legible.

## The example

`~/Documents/accounts.sqlite` — 3,670,016 bytes (3.5 MiB), on its **second** backup. Since the last snapshot the user recorded a transaction, which rewrote one page in the middle of the file without changing its length.

Defaults throughout: `fixed-v1` at 1 MiB, 128 MiB object-store blobs, Zstandard, AES-256-GCM, `repository` dedup trust domain.

Two choices in that setup are deliberate:

- **Second backup, not first.** The first backup is the trivial case where everything is new. The second is where the engine does the thing it exists to do — write 1 MiB for a 3.5 MiB file.
- **SQLite, not a document.** A `.docx` or `.odt` is a recompressed zip container, so a one-character edit changes nearly every byte. That would illustrate the `fixed-v1` weakness ([§3.2](02-repository-format.md#32-why-fixed-size-is-the-v1-default)) without illustrating the reuse mechanism. An in-place page rewrite illustrates both, because it is the case fixed-size segmentation handles *well*.

The stages below are numbered to match the publication order in [`04-concurrency-and-publication.md` §5](04-concurrency-and-publication.md#5-publication-order) exactly. Steps 2 and 3 expand into the capture algorithm from [`02-repository-format.md` §3.4](02-repository-format.md#34-capture-algorithm).

---

## 1 · Publish the write intent

Before anything else touches a byte, the agent publishes a record to `/journal/<writer-id>/<sequence>`:

```text
write_intent {
  writer_id, sequence, issued_at,
  backup_set_id,
  intended_blob_ids: [ blob-7f3a…, blob-9c21… ],
  declared_max_duration,
  expiry_generation
}
```

This comes first because everything downstream produces blobs that are, for a while, referenced by nothing. Between upload and index publication — potentially hours on a large job — a garbage collector walking reachability cannot distinguish them from garbage. The intent is the durable statement that they are in flight.

It works only because **blob identifiers are writer-allocated rather than content-derived** ([ADR-0016](../adr/0016-blob-identifier-formation.md)). You cannot name a blob after its contents before its contents exist. Note the asymmetry with *record* identifiers, which are content-derived and keyed — see stage 6.

→ [`04-concurrency-and-publication.md` §4](04-concurrency-and-publication.md#4-write-intent)

## 2 · Scan, and find the previous version

The scanner streams the directory tree, capturing stable file identity — `FileId` on Windows, `(device, inode)` on Unix — alongside size and timestamps, and looks up the prior file-version manifest in the local catalogue.

Identity rather than path is what makes a renamed file recognisable as the same file rather than a delete plus a create.

Here size and mtime have both changed, so the file will be read. **Had they matched the prior version, the payload would never be read at all** — that fast path is what keeps an incremental scan proportional to what changed (NFR-PERF-003).

→ [`06-filesystem-capture.md` §1](06-filesystem-capture.md#1-scanner)

## 3 · Segment, hash, compare, compress, encrypt, pack

The canonical publication step 3 is a single line. It expands into six things.

### 3a · Split into segments

Read as a bounded stream and divided at fixed 1 MiB offsets:

| Segment | Byte range | Length |
|---------|-----------|--------|
| 0 | 0 – 1,048,576 | 1,048,576 |
| 1 | 1,048,576 – 2,097,152 | 1,048,576 |
| 2 | 2,097,152 – 3,145,728 | 1,048,576 |
| 3 | 3,145,728 – 3,670,016 | 524,288 |

Only the final segment may be short. Nothing here depends on the file fitting in memory — segments flow through a bounded pipeline, so a 2 TiB file costs no more resident memory than this one.

> **The known weakness.** Boundaries are anchored at byte 0, so *inserting* bytes shifts every subsequent boundary and the whole file looks new. An in-place page rewrite — this example — is the good case. A prepended log line, or a recompressed container, is the bad one.
>
> This is why `cdc-v1` is specified alongside `fixed-v1` rather than deferred, and why a corpus benchmark gates the format freeze. → [ADR-0002](../adr/0002-segmentation-strategy.md), [`02-repository-format.md` §3.3](02-repository-format.md#33-the-freeze-gate)

### 3b · Hash each segment

SHA-256 over each segment's plaintext yields its **content identifier**. Hashing is pipelined with reading and uses hardware acceleration where available.

SHA-256 rather than the faster BLAKE3 because the standalone recovery tool must build and run on every platform with no native dependency — a portability constraint that outranks throughput for this particular choice. The function is profile-selected, so it can change later without a format break. → [ADR-0004](../adr/0004-segment-hash-function.md)

### 3c · Decide what actually needs storing

Positional comparison against the prior version first:

```text
seg 0   content-id 9a4f…   ==  prior seg 0   → reuse
seg 1   content-id c17b…   ==  prior seg 1   → reuse
seg 2   content-id 55e8…   !=  prior seg 2   → CHANGED
seg 3   content-id 2d90…   ==  prior seg 3   → reuse
```

Segment 2 then gets a repository-wide lookup keyed on `(content identifier, segmentation profile, dedup trust domain, writer attribution)`. Assume it misses — genuinely new bytes.

**Only segment 2 continues through the pipeline.** 1 MiB written for a 3.5 MiB file. Reuse costs nothing at all: the new manifest simply names the same object identifiers for segments 0, 1 and 3.

Had the lookup *hit* on a segment written by another device, the default `repository` domain would fetch it, decrypt it, and confirm its content identifier before referencing it. Skipping that check is how a member with a faulty hashing path silently corrupts another device's backup — detectable only at restore, when the source is gone.

→ [`03-crypto.md` §5](03-crypto.md#5-deduplication-trust-domains), [ADR-0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md)

### 3d · Compress

Zstandard over segment 2's plaintext, **before** encryption:

```text
1,048,576 bytes  →  611,204 bytes   (41.7% saved)
```

Well above the 5% threshold below which a segment is stored uncompressed, so the compressed form is kept and the record is marked accordingly. Incompressible input would be stored raw rather than paying CPU for nothing and storing a slightly larger result.

Compress-then-encrypt is correct for efficiency, and it is also exactly what creates a length side channel: the store learns compressed sizes, which fingerprint file types. A deliberate trade, recorded rather than hidden. → [`02-repository-format.md` §4](02-repository-format.md#4-compression), [`../threat-model.md` T-11](../threat-model.md#t-11-metadata-side-channels)

### 3e · Encrypt, independently

Segment 2 becomes **record 47** in the currently open blob — blobs hold records from many files and many versions, so its ordinal has nothing to do with its position in this file.

The blob drew a 256-bit CSPRNG salt when it was opened, and its key derives from that plus writer identity:

```text
blob_key  = HKDF-Expand(
                PRK  = data_key[generation],
                info = "fbp/blob/v1" ‖ blob_salt ‖ writer_id ‖ u64(blob_counter),
                L    = 32)

nonce(47) = 96-bit big-endian 47

AAD(47)   = repository_id ‖ format_version ‖ object_type ‖ object_id ‖ 47
```

Three things are load-bearing here:

- **Every blob has its own key.** Nonce uniqueness therefore only has to hold *within one blob*, where a single writer owns a monotonic ordinal. Concurrent writers cannot collide because they hold different keys — no coordination channel, no birthday-bound budget anyone has to track.
- **`writer_id` and `blob_counter` are in the derivation** so key separation does not rest on CSPRNG quality alone. A cloned VM replaying RNG state would otherwise be able to draw the same salt twice.
- **The AAD binds the record to its exact context**, so it cannot be relocated to another blob, ordinal, object type, or repository without authentication failing.

The **object identifier** — what every manifest and index entry references it by — is a keyed function of the content identifier and object type. Keyed, so a storage provider cannot hash a file it already has and check whether you are storing it.

→ [`03-crypto.md` §3](03-crypto.md#3-nonce-and-key-construction), [`§4`](03-crypto.md#4-object-identifiers), [ADR-0005](../adr/0005-aead-suite-and-nonce-construction.md)

### 3f · Append to the open blob

The sealed record is appended in the local spool. A record is **never split across blobs**: if the next one will not fit under the maximum, the current blob seals and a new one opens.

The spool checkpoint stores the **sealed record bytes** — not a plaintext offset. This is the subtlest rule in the whole pipeline. Resume *re-emits* stored bytes rather than recomputing them, because recompression is not reproducible across Zstd versions, and recomputing after an upgrade would encrypt different plaintext under an already-used `(key, nonce)`. If any pinned parameter has changed since the crash, the engine restarts the blob instead — drawing a fresh salt, which is always the safe failure.

→ [`02-repository-format.md` §5.3](02-repository-format.md#53-spooling-and-sealing), [`03-crypto.md` §3.3](03-crypto.md#33-why-resumption-is-safe)

## 4 · Seal and upload

Sealing appends the authenticated **recovery footer**, listing for every record in the blob: object identifier, ordinal, physical offset, stored length, logical length, object type, compression profile, encryption profile. Then the 16-byte footer locator, and a digest over the complete sealed representation — recorded in the index, not appended to the blob.

That footer is the point of the entire structure. Given the blob and the repository keys and *nothing else* — no index, no catalogue, no other object — every record in it can be located, decrypted, and verified. It is what bounds the blast radius of losing every index object, and what makes forensic rebuild possible at all.

The blob then uploads under the **store blob key** derived from its pre-allocated identifier — an HMAC rendering ([spec 02 §4.3](../../specifications/repository-format/02-identifiers.md#43-not-leaking-writer-identity)), because the raw identifier embeds writer identity and a store key must not.

→ [`02-repository-format.md` §5.2](02-repository-format.md#52-layout)

## 5 · Verify acknowledgements

The store's acknowledgement is checked, and uploaded ranges may be sample-read back. Nothing downstream may reference an object that is not confirmed durable *in this replica*.

## 6 · Publish the index delta

Only now — after the blob is durable:

```text
index_delta {
  writer_id, sequence, predecessor_delta_id,
  generation: 5, shard,
  covered_blob_ids: [ blob-7f3a… ],
  entries: [
    3e91…  →  (blob-7f3a…, offset 24_117_248, stored_length 611_204,
               zstd / aes-256-gcm, type: insertion)
  ]
}
```

The index is the **sole authority on physical location**. Entries carry the generation at which they were published and declare whether they are an insertion or a supersession, so when compaction later relocates this record the newer entry wins deterministically — regardless of the order in which any reader discovers the two.

Deltas form gapless per-writer chains, which is what lets a reader *detect* a delta it has not seen rather than silently assuming it has everything.

→ [`02-repository-format.md` §7.2](02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing), [ADR-0017](../adr/0017-index-entry-supersession.md)

## 7 · Publish the manifests and snapshot

The file-version manifest:

```text
file_version {
  path identity, logical length 3_670_016,
  whole-file verification hash,
  parent file-version reference  →  previous version's manifest,
  segment references: [
    (offset         0, length 1_048_576, object-id 8b02…),   ← reused
    (offset 1_048_576, length 1_048_576, object-id d4c7…),   ← reused
    (offset 2_097_152, length 1_048_576, object-id 3e91…),   ← NEW
    (offset 3_145_728, length   524_288, object-id 6f15…)    ← reused
  ],
  timestamps, permissions, attributes,
  sparse extents, hard-link identity, capture diagnostics
}
```

> **Note what is absent: no blob identifier, no physical offset, no stored length.** A segment reference carries logical facts and an object identifier, and nothing else.
>
> That absence is what lets compaction relocate records later without rewriting a single immutable object. Physical location in the manifest was the original design's most serious internal contradiction — compaction would have had to either rewrite immutable objects or strand every manifest referencing a moved record. → [ADR-0007](../adr/0007-logical-object-identifiers-in-manifests.md)

The manifest is packed into a metadata blob, referenced by its enclosing tree, up through parent trees to the root tree — and then the signed snapshot manifest is published, referencing a root tree that already exists.

Ordering is the whole game: **every object a published object references is already durable.**

## 8 · Retire the intent

The write intent is retired and an audit record written. The blobs it covered are now reachable through the index and the snapshot, so they no longer need its protection.

## 9 · Mark the job complete

---

## What "done" actually means

The snapshot is now **committed to the local replica** — every object it references is durable *there*. Commit is a per-replica property, always achievable, and it is what makes a replica independently restorable.

Replication to a peer or a cloud store is **separate state**, tracked per `(snapshot, destination)`. A peer that has been switched off for a fortnight delays policy compliance; it does not block the backup, and it does not stop the next incremental from having a recent version to compare against.

And the status will **not** say `protected` if the local repository sits on the same disk as `~/Documents`. That is `captured`. `protected` requires a replica in a different failure domain — because a copy that dies with the original was never a backup.

→ [`04-concurrency-and-publication.md` §6](04-concurrency-and-publication.md#6-commit-versus-replication), [`§6.4`](04-concurrency-and-publication.md#64-protected-requires-an-independent-failure-domain), [ADR-0018](../adr/0018-replica-failure-domains.md)

## Restore runs the same path backwards

```text
snapshot manifest
  → root tree → … → file-version manifest
    → ordered segment references (logical offset, length, object identifier)
      → INDEX LOOKUP per object identifier → (blob, offset, stored length, profiles)
        → range-read the blob
          → authenticate → decrypt → decompress
            → verify each segment's content identifier
              → assemble in logical order
                → verify the whole-file hash
                  → apply metadata last
```

Two rules worth restating because they are where restores go wrong elsewhere:

- **Metadata is applied after content**, so a failure mid-file never leaves a file with correct permissions and wrong bytes.
- **A restore that recovered 9,999 of 10,000 files is a failed restore that recovered 9,999 files**, and is reported that way. Partial success is never reported as success.

If the index has been lost, the index lookup is served instead by scanning blob recovery footers — targeted at the requested file rather than rebuilding the whole repository first.

→ [`08-restore-and-recovery.md` §3](08-restore-and-recovery.md#3-restore-verification), [`02-repository-format.md` §8.2](02-repository-format.md#82-forensic-rebuild)

## What each stage is defending against

The ordering above is not arbitrary. Nearly every step exists because a specific failure was found and closed — this table is also a map into the two review documents.

| Stage | Defends against | Finding |
|-------|----------------|---------|
| 1 · Intent before blobs | GC deleting a running job's blobs | [C4](../review/2026-08-architecture-review.md#c4--garbage-collection-can-delete-blobs-belonging-to-an-in-flight-snapshot) |
| 1 · Writer-allocated blob IDs | Intents unable to name blobs that do not exist yet | [PT-4](../review/2026-08-fix-pressure-test.md#pt-4--blob-identifier-formation-is-unspecified-and-c4-cannot-be-implemented-without-it) |
| 3c · Verify-on-reuse | A faulty member corrupting other devices' backups | [C3](../review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard) |
| 3e · Per-blob keys | Nonce reuse across concurrent writers | [C2](../review/2026-08-architecture-review.md#c2--nonce-uniqueness-is-asserted-but-never-constructed) |
| 3e · `writer_id` in derivation | Cloned VMs replaying RNG state | [PT-13](../review/2026-08-fix-pressure-test.md#pt-13--blob-salt-uniqueness-rests-entirely-on-csprng-quality-and-vm-cloning-defeats-that) |
| 3f · Checkpoint stores sealed bytes | Nonce reuse after a crash-and-upgrade | [PT-1](../review/2026-08-fix-pressure-test.md#pt-1--c2s-resume-guarantee-silently-assumes-bit-reproducible-compression) |
| 4 · Recovery footer | Total loss of the index | [C1](../review/2026-08-architecture-review.md#c1--immutable-manifests-embed-physical-locations-that-compaction-changes) |
| 6 · Generation precedence | Compaction and concurrent checkpoints disagreeing | [PT-2](../review/2026-08-fix-pressure-test.md#pt-2--c6s-commutativity-claim-is-false-once-c1-is-in-place) |
| 6 · Per-writer delta chains | Silent index loss on eventually consistent stores | [C6](../review/2026-08-architecture-review.md#c6--checkpoint-compaction-requires-a-complete-listing-the-design-forbids-relying-on) |
| 7 · Logical-only references | Compaction stranding immutable manifests | [C1](../review/2026-08-architecture-review.md#c1--immutable-manifests-embed-physical-locations-that-compaction-changes) |
| Done · Commit ≠ replication | One offline destination stalling all protection | [C5](../review/2026-08-architecture-review.md#c5--snapshot-commit-is-defined-so-that-one-offline-destination-stalls-all-protection) |
| Done · Failure domains | `protected` meaning "on the same disk as the original" | [PT-8](../review/2026-08-fix-pressure-test.md#pt-8--protected-does-not-require-a-replica-outside-the-sources-failure-domain) |

---

**Previous:** [11 — Solution structure](11-solution-structure.md) · **Back to:** [docs index](../README.md)

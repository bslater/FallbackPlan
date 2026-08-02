# 06 — Manifests

**Normative.** Derived from [`02-repository-format.md` §6](../../docs/architecture/02-repository-format.md#6-manifests) and [ADR-0007](../../docs/adr/0007-logical-object-identifiers-in-manifests.md).

---

## 1 What manifests are

Manifests are the metadata graph: what files exist, what versions they have, what segments they are made of, and what a snapshot contained.

They are CBOR objects ([00 §4](00-conventions.md#4-cbor-encoding)), stored as records in **metadata blobs** (`blob_class = 0x0002`), and addressed by object identifier like any other record. Being packed into blobs does not make them less individually addressable — each is a separate record with its own encryption, its own tag, and its own entry in the footer and index.

Every manifest is immutable. Correction happens by publishing new objects, never by modifying old ones.

## 2 The graph

```text
snapshot manifest
  ├── policy manifest        (effective configuration used for this capture)
  ├── error manifest         (paths that could not be captured)
  └── root tree manifest
        ├── tree manifest    (subdirectory) ─── recursively
        └── file-version manifest
              └── segment references ──▶ object identifiers ──▶ [ index ] ──▶ blob
```

The bracketed step is the important one. A manifest states *what* a file is made of; the index states *where* those parts currently live.

## 3 Segment references

```text
segment_reference = [ logical_offset, logical_length, object_id ]
```

Encoded as a CBOR array of exactly three elements — an array rather than a map, because there are millions of these and the key overhead of a map is not worth paying for three well-known positions.

| Position | Type | Value |
|----------|------|-------|
| 0 | u64 | Byte offset of this segment within the file |
| 1 | u64 | Plaintext length of this segment |
| 2 | bytes[32] | Object identifier of the segment record |

### 3.1 No physical location — and why

A segment reference contains **no blob identifier, no physical offset, and no stored length**. A writer MUST NOT emit them; a reader MUST reject a manifest that contains them.

Blob compaction reclaims space by reading still-live records out of mostly-dead blobs and writing them into new ones, which changes both the blob and the offset of every record it moves. If those values lived in the manifest, the first compaction pass would have to either rewrite objects the format declares immutable, or leave every affected manifest pointing at a blob that no longer exists. The symptom would be unreadable historical snapshots on the first run of routine maintenance.

Keeping manifests purely logical means compaction republishes index entries and touches no manifest, no tree, and no snapshot. → [ADR-0007](../../docs/adr/0007-logical-object-identifiers-in-manifests.md), [C1](../../docs/review/2026-08-architecture-review.md#c1--immutable-manifests-embed-physical-locations-that-compaction-changes)

The cost is one index lookup per segment on the restore path, and a slower path to the first byte when the index has been lost entirely. Both are recorded in [PT-10](../../docs/review/2026-08-fix-pressure-test.md#pt-10--emergency-single-file-restore-regressed-from-one-fetch-to-a-full-scan); whether to add an advisory non-authoritative location hint is open as [Q11](../../docs/open-questions.md#q11--physical-hints-in-segment-references).

### 3.2 Ordering and coverage

Segment references MUST be ordered by ascending `logical_offset`, MUST NOT overlap, and together with sparse extents MUST cover `[0, logical_length)` exactly. A reader MUST verify this and reject a manifest that fails.

Maximum references per manifest: 1 048 576. A file needing more requires a larger segment size.

## 4 File-version manifest

Object type `0x02`.

| Key | Type | Value |
|-----|------|-------|
| 1 | u16 | `entry_kind` — 1 file, 2 symlink, 3 directory-placeholder, 4 special |
| 2 | bytes | `name` — the entry name as the source filesystem reported it, raw bytes |
| 3 | u8 | `name_normalisation` — 0 unknown, 1 NFC, 2 NFD |
| 4 | u64 | `logical_length` |
| 5 | array | `segment_references` (§3) |
| 6 | array | `sparse_extents` — array of `[offset, length]` pairs |
| 7 | bytes[32] | `whole_file_hash` |
| 8 | u16 | `segmentation_profile` |
| 9 | bytes[32] | `parent_version` — object id of the previous version, absent if none |
| 10 | map | `metadata` (§4.1) |
| 11 | bytes | `link_target` — for symlinks, raw bytes |
| 12 | bytes[16] | `hardlink_group` — present when the file is one of several links to one inode |
| 13 | array | `capture_diagnostics` — array of text, present only when non-empty |

### 4.1 Metadata map

| Key | Type | Value |
|-----|------|-------|
| 1 | u64 | `modified_at` |
| 2 | u64 | `created_at` |
| 3 | u64 | `accessed_at` |
| 4 | u32 | `posix_mode` |
| 5 | text | `owner_name` |
| 6 | text | `group_name` |
| 7 | bytes | `windows_security_descriptor` — self-relative binary form |
| 8 | array | `extended_attributes` — array of `[name, value]` byte-string pairs |
| 9 | array | `alternate_streams` — array of `[name, object_id, length]` |
| 10 | u32 | `file_attributes` — platform attribute bits |

Names rather than numeric IDs for owner and group: a UID means nothing on the machine a file is restored to, whereas a name has a chance of resolving. Where it does not, restore degrades and reports rather than silently assigning ownership to whoever happens to hold that number.

Absent keys mean the source did not provide the value. They do not mean zero.

### 4.2 The whole-file hash

`whole_file_hash` is computed over the **reconstructed plaintext file**, including materialised sparse extents as zeroes, using the repository's content-hash profile.

It is verified after reassembly during restore. Per-segment verification already proves each part is authentic and truthfully identified; the whole-file hash proves they were *assembled correctly* — right order, no gaps, no duplication. The two check different things and both are required. → FR-RST-002

## 5 Tree manifest

Object type `0x03`.

| Key | Type | Value |
|-----|------|-------|
| 1 | array | `entries` — array of `[name, object_id, entry_kind]` |
| 2 | map | `metadata` — as §4.1, for the directory itself |
| 3 | bytes | `name` |
| 4 | u8 | `name_normalisation` |

Entries MUST be sorted by the **raw bytes** of `name`, ascending. Byte order rather than a collation order, because collation is locale-dependent and would make the encoding non-deterministic across machines — which would break object identifiers ([00 §4](00-conventions.md#4-cbor-encoding)).

A tree MUST NOT contain two entries with the same `name` bytes. It MAY contain entries differing only by case or Unicode normalisation — that is a legitimate state on a case-sensitive source, and it becomes a restore-plan conflict rather than a capture error. → [`06-filesystem-capture.md` §2](../../docs/architecture/06-filesystem-capture.md#2-path-handling)

## 6 Snapshot manifest

Object type `0x04`. Unlike other manifests, a snapshot is stored **both** as a metadata record and as a standalone object at `/snapshots/<device-id>/<backup-set-id>/<snapshot-id>`, so a reader can enumerate snapshots from a bounded prefix without an index.

| Key | Type | Value |
|-----|------|-------|
| 1 | bytes[16] | `snapshot_id` |
| 2 | bytes[16] | `device_id` |
| 3 | bytes[16] | `backup_set_id` |
| 4 | u64 | `capture_started_at` |
| 5 | u64 | `capture_completed_at` |
| 6 | bytes[32] | `root_tree` |
| 7 | array | `parent_snapshots` — array of bytes[16] |
| 8 | bytes[32] | `policy_manifest` |
| 9 | bytes[32] | `error_manifest` — absent when nothing failed |
| 10 | u8 | `consistency_method` — 1 live, 2 VSS, 3 filesystem snapshot, 4 application-quiesced |
| 11 | u8 | `capture_status` — 1 complete, 2 partial, 3 aborted |
| 12 | map | `source_filesystem` — capabilities and case sensitivity observed |
| 13 | u64 | `publication_generation` |
| 14 | i64 | `observed_clock_skew_ms` — absent if no reference was available |
| 15 | text | `client_version` |
| 16 | array | `tags` |
| 17 | bytes[64] | `signature` — over the canonical encoding of keys 1–16 |

`consistency_method` is recorded and surfaced because "best-effort live capture" and "application-consistent" are materially different promises, and a user restoring a database needs to know which one they have.

### 6.1 Signature

Ed25519 over the deterministic CBOR encoding of the map containing keys 1–16, using the device signing key. A reader MUST verify it against the device's known public key before trusting the snapshot, and MUST report a failure as a **security finding** rather than a corruption finding — a bad signature means substitution or forgery, not a bad disk.

## 7 Policy manifest

Object type `0x05`. Records the effective configuration a snapshot was captured under.

| Key | Type | Value |
|-----|------|-------|
| 1 | u16 | `segmentation_profile` |
| 2 | map | `segmentation_parameters` |
| 3 | u16 | `compression_profile` |
| 4 | u16 | `compression_threshold_permille` |
| 5 | u16 | `encryption_profile` |
| 6 | map | `blob_write_profile` |
| 7 | u8 | `dedup_trust_domain` — 1 device, 2 repository, 3 repository-unverified |
| 8 | array | `include_rules` |
| 9 | array | `exclude_rules` |

This exists so that a snapshot can always answer "what settings produced this?" years later, without those settings having to still exist in anyone's configuration file. It is also what makes a benchmark comparing two profiles interpretable.

## 8 Error manifest

Object type `0x06`. Present only when something could not be captured.

| Key | Type | Value |
|-----|------|-------|
| 1 | array | `failures` — array of maps (§8.1) |

### 8.1 Failure entry

| Key | Type | Value |
|-----|------|-------|
| 1 | array | `path_components` — array of raw byte strings |
| 2 | u16 | `reason` — 1 permission, 2 not found, 3 I/O error, 4 changed during read, 5 unsupported type, 6 too large, 7 excluded by limit |
| 3 | text | `detail` |

A path **excluded by policy** is not a failure and MUST NOT appear here — it belongs in the policy manifest's exclude rules. Conflating the two is how a user comes to believe they have a backup of something they excluded two years ago. → [`06-filesystem-capture.md` §6](../../docs/architecture/06-filesystem-capture.md#6-backup-set-selection)

## 9 Sharding

No manifest's size grows with the repository or with total snapshot history. Trees shard the graph naturally: a directory with a million entries produces a large tree manifest, but a repository with a million directories produces a million small ones.

Where a single directory's tree manifest would exceed the 16 MiB metadata limit, a writer MUST split it into an ordered chain of tree manifests, each referencing the next via a continuation entry. This is the only case in the format where a logical object spans multiple physical objects.

---

**Previous:** [05 — Blobs](05-blob.md) · **Next:** [07 — Index](07-index.md)

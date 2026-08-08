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

The cost is one index lookup per segment on the restore path, and a slower path to the first byte when the index has been lost entirely. Both are recorded in [PT-10](../../docs/review/2026-08-fix-pressure-test.md#pt-10--emergency-single-file-restore-regressed-from-one-fetch-to-a-full-scan).

**[Q11](../../docs/open-questions.md#closed) is closed, and not by adding a field here.** A hint inside a manifest would have made the same file version encode differently on two devices, because the manifest's own object identifier is derived from its bytes ([02 §3](02-identifiers.md#3-object-identifier)) — and that identity across devices is what makes cross-device deduplication work. Neither the question nor ADR-0007 recorded that cost. The hint lives in a separate object instead — §10.

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

> **Erratum (phase 1).** Several shapes in §4 and §4.1 were unassigned
> until the first production writer needed them. Pending a normative edit,
> [ADR-0026](../../docs/adr/0026-phase-1-capture-shapes.md) pins them:
> `hardlink_group` (key 12) = the first 16 bytes of
> HMAC-SHA-256(content_id_key, `"fbp/hardlink/v1"` ‖ device_id ‖
> u64(file_identity)) — deterministic, keyed, equal within one snapshot for
> links to one inode (§Decision 1); `capture_diagnostics` (key 13) uses the
> documented `key: value` vocabulary incl. `captured-inconsistent:`
> (§Decision 2); an alternate stream's `object_id` (§4.1 key 9) names a
> segment record (`0x01`) holding the whole stream, one segment maximum in
> v1 (§Decision 5); special files (`entry_kind` 4) are zero-content file
> versions with `special-kind:`/`device:` diagnostics (§Decision 4).

### 4.2 The whole-file hash

`whole_file_hash` is computed over the **reconstructed plaintext file**, including materialised sparse extents as zeroes, using the repository's content-hash profile.

It is verified after reassembly during restore. Per-segment verification already proves each part is authentic and truthfully identified; the whole-file hash proves they were *assembled correctly* — right order, no gaps, no duplication. The two check different things and both are required. → FR-RST-002

### 4.3 What `name` must contain

`name` is the entry name **as the source filesystem reported it**, and this section says what that means on each host so that "raw bytes" is a rule an implementer can follow rather than an aspiration.

**POSIX.** A filename is a byte sequence containing neither NUL nor `/`. It carries no encoding guarantee. A conforming implementation MUST obtain it from the directory-reading syscall (`readdir` and its relatives) and store those bytes unchanged. It MUST NOT obtain the name through a host string type that decodes it, because that decoding is lossy for any name that is not valid UTF-8, and re-encoding the decoded string produces different bytes from the ones on disk.

**Windows.** A filename is a UTF-16 code-unit sequence. The repository encoding is **UTF-8 of that sequence**. A name containing an unpaired surrogate has no UTF-8 encoding; a conforming implementation MUST refuse such an entry with error-manifest reason 8 rather than substituting a replacement character. Substitution would store a name that is not the file's name, under a format field that promises it is.

**Both.** Where the host cannot hand the implementation the true bytes, the entry MUST be recorded in the error manifest with reason 8. It MUST NOT be captured under a substituted name.

That last rule exists because the failure it prevents is silent. An entry stored under a replacement-character name looks captured, appears in listings, and restores as a file the user did not have — and where the substitution also breaks the implementation's ability to open the source, the content behind that plausible-looking entry was never read at all.

> **Implementation status (2026-08).** The POSIX rule is enforced: names come from `readdir` and non-representable entries are refused with reason 8. What is **not** yet built is capturing those entries anyway, which needs the byte-native open path that [architecture 06 §4.1](../../docs/architecture/06-filesystem-capture.md#41-links-are-classified-before-they-are-traversed) also owes for its own reasons. Until then a non-UTF-8 name is reported and skipped rather than backed up.

## 5 Tree manifest

Object type `0x03`.

| Key | Type | Value |
|-----|------|-------|
| 1 | array | `entries` — array of `[name, object_id, entry_kind]` |
| 2 | map | `metadata` — as §4.1, for the directory itself |
| 3 | bytes | `name` |
| 4 | u8 | `name_normalisation` |
| 5 | bytes[32] | `continuation` — object identifier of the next tree manifest in a sharded chain (§9). Absent on the last manifest of a chain and on any unsharded tree. |

Entries MUST be sorted by the **raw bytes** of `name`, ascending. Byte order rather than a collation order, because collation is locale-dependent and would make the encoding non-deterministic across machines — which would break object identifiers ([00 §4](00-conventions.md#4-cbor-encoding)).

A tree MUST NOT contain two entries with the same `name` bytes. It MAY contain entries differing only by case or Unicode normalisation — that is a legitimate state on a case-sensitive source, and it becomes a restore-plan conflict rather than a capture error. → [`06-filesystem-capture.md` §2](../../docs/architecture/06-filesystem-capture.md#2-path-handling)

> **Erratum (phase 1).** The relationship between a tree entry and a child
> directory was unstated. Pending a normative edit,
> [ADR-0026](../../docs/adr/0026-phase-1-capture-shapes.md) §Decision 6
> pins it: a subdirectory entry carries `entry_kind` 3 and its `object_id`
> names the child directory's **tree manifest** (`0x03`) — the first
> manifest of the chain when sharded; resolution to any other object type
> is a damage finding. An empty directory is a tree manifest with zero
> entries.

## 6 Snapshot manifest

Object type `0x04`. Unlike other manifests, a snapshot is stored **both** as a metadata record and as a standalone object at `/snapshots/<device-id>/<backup-set-id>/<snapshot-id>`, so a reader can enumerate snapshots from a bounded prefix without an index.

> **Erratum (phase 0).** The standalone copy has no blob envelope, so this specification gives it no encryption inputs. Pending a normative edit, it is sealed under the `FBPKSREC` standalone record framing of [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md) §Decision 1, keeping object type `0x04` and the same manifest bytes — so its object identifier is identical to the in-blob record's.

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

Ed25519 over the deterministic CBOR encoding of the map containing keys 1–16, using the signing key for the generation recorded in `publication_generation` ([03 §4](03-keys.md#4-derived-keys)). A reader MUST verify it against the **repository signing public key for that generation** — derived from the master key, so the reader computes it itself and no key distribution is required — and MUST report a failure as a **security finding** rather than a corruption finding: a bad signature means substitution or forgery, not a bad disk.

In format version 1 a signature is **repository-scoped**: it proves the snapshot was produced by a holder of the master key at that generation, and no more. It does not attribute the snapshot to a particular device — `device_id` and `writer_id` fields are attribution **by claim**. Per-device signing keys are a considered and deferred extension. → [ADR-0020](../../docs/adr/0020-ed25519-signing-key-semantics.md), [Q13](../../docs/open-questions.md#q13--device-level-signature-attribution)

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
| 8 | array | `include_rules` — array of text strings, `rules-v1` (§7.1) |
| 9 | array | `exclude_rules` — array of text strings, `rules-v1` (§7.1) |

This exists so that a snapshot can always answer "what settings produced this?" years later, without those settings having to still exist in anyone's configuration file. It is also what makes a benchmark comparing two profiles interpretable.

> **Erratum (phase 0).** The inner shapes of key 2 `segmentation_parameters`, key 6 `blob_write_profile`, and the snapshot manifest's key 12 `source_filesystem` are not assigned here. Pending a normative edit, [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md) §Decision 6 pins them. Phase 1 extends `source_filesystem` with optional keys 4 `max_path_bytes` (u32), 5 `max_component_bytes` (u32), and 6 `reserved_names` (bool) — the filesystem capability record of [ADR-0026](../../docs/adr/0026-phase-1-capture-shapes.md) §Decision 7; absence means "limits unknown". The snapshot manifest's `capture_status` triggers are pinned by the same ADR §Decision 3: 2 (partial) iff key 9 references a non-empty error manifest; 3 (aborted) is never published by this implementation.

### 7.1 Rule dialect (rules-v1)

In format v1, every string in `include_rules` and `exclude_rules` is a
**rules-v1** rule ([ADR-0024](../../docs/adr/0024-include-exclude-rule-dialect.md)).
No dialect field exists; a future dialect requires a new policy-manifest key
assigned by a future format revision. Rules are evaluated **at capture** —
a reader treats stored rules as informational and MUST NOT fail to decode a
manifest because of their content.

**Matching subject.** A rule is matched against the entry's relative path
within the backup-set root: components joined by `/`, NFC-normalised, no
leading `/`, and no trailing `/` for directories. The empty path (the root
itself) matches no rule.

**Rule forms.** A rule whose first three characters are `re:` is a *regex
rule*; every other rule is a *glob rule*. There is no glob escape
mechanism: a pattern that must match a literal `*` or `?`, or a literal
path beginning `re:`, is written as a regex rule.

**Glob rules.**

| Token | Meaning |
|-------|---------|
| `*` | zero or more characters within one component — never matches `/` |
| `?` | exactly one character, never `/` |
| `**` | zero or more whole components; valid **only** as a complete component |
| any other character | itself |

A `**` adjacent to anything else within a component (`a**b`, `**.log`)
makes the rule **invalid**. A glob rule containing `/` is anchored at the
backup-set root; a rule containing no `/` is shorthand for `**/<rule>` and
therefore matches its pattern against the final component of any path. A
trailing `/**` matches every strict descendant of the prefix and not the
prefix itself.

**Regex rules.** The characters after `re:` are a pattern in the following
subset, implicitly anchored at both ends — the whole relative path must
match, and explicit `^` or `$` make the rule invalid:

- literals; `.` (any character, including `/`)
- character classes `[...]` and `[^...]`, with ranges
- alternation `|`; grouping `(...)`
- quantifiers `*`, `+`, `?`, `{m}`, `{m,n}`
- `\` escaping a metacharacter (`\.`, `\*`, `\[`, `\\`, `\:`, …)

Backreferences, lookaround, shorthand classes (`\d`, `\w`, `\s`), inline
flags, and named groups are **not** in the subset; a rule using them is
invalid. Implementations MUST validate rules against this subset rather
than passing them to a host regex engine unchecked, and SHOULD match with a
linear-time engine. Rule strings are bounded at 4 096 UTF-8 bytes.

**Evaluation.** Exclude wins; rules are an unordered set and there is no
negation.

1. A path is **excluded** iff the path itself or any of its ancestors
   matches any exclude rule. Excluding a directory prunes its whole
   subtree.
2. A path is **captured** iff it is not excluded, and either
   `include_rules` is empty, or the path or any of its ancestors matches an
   include rule.
3. A scanner MAY descend a non-captured, non-excluded directory when some
   include rule could match beneath it; descending never captures the
   directory itself.

**Case sensitivity.** Matching follows the source filesystem as the
snapshot records it (§6 key 12 `case_sensitive`). Case-insensitive
matching applies Unicode simple case folding to both pattern literals and
path before comparison; `*`, `?`, `**`, and regex metacharacters are
unaffected.

**Invalid rules.** The empty rule, and any rule with an empty component
(leading `/`, trailing `/`, or `//`), is invalid, in both forms. A writer
MUST NOT publish a policy manifest containing an invalid rule — validation
happens before capture starts, and each defect is reported by rule and
reason. Conformance vectors for the whole
dialect, including invalid-rule cases, live in
[`conformance/vectors/path-rules.json`](conformance/vectors/path-rules.json).

## 8 Error manifest

Object type `0x06`. Present only when something could not be captured.

| Key | Type | Value |
|-----|------|-------|
| 1 | array | `failures` — array of maps (§8.1) |

### 8.1 Failure entry

| Key | Type | Value |
|-----|------|-------|
| 1 | array | `path_components` — array of raw byte strings |
| 2 | u16 | `reason` — 1 permission, 2 not found, 3 I/O error, 4 changed during read, 5 unsupported type, 6 too large, 7 excluded by limit, 8 name not representable (§4.2) |
| 3 | text | `detail` |

A path **excluded by policy** is not a failure and MUST NOT appear here — it belongs in the policy manifest's exclude rules (§7.1). Conflating the two is how a user comes to believe they have a backup of something they excluded two years ago. → [`06-filesystem-capture.md` §6](../../docs/architecture/06-filesystem-capture.md#6-backup-set-selection)

## 9 Sharding

No manifest's size grows with the repository or with total snapshot history. Trees shard the graph naturally: a directory with a million entries produces a large tree manifest, but a repository with a million directories produces a million small ones.

Where a single directory's tree manifest would exceed the 16 MiB metadata limit, a writer MUST split it into an ordered chain of tree manifests, each referencing the next via `continuation` (key 5 in §5). This is the only case in the format where a logical object spans multiple physical objects.

Chain rules:

- Each manifest in the chain except the last carries `continuation`; the last omits it.
- The `entries` of the whole chain, concatenated in chain order, form one logical entry list; §5's sorting and duplicate rules apply to that **logical** list, not to each shard independently — every entry in a manifest MUST sort strictly after every entry in its predecessor.
- `metadata`, `name`, and `name_normalisation` are carried by the **first** manifest of the chain and MUST be absent from continuations.
- A reader MUST follow the chain to its end before treating the directory as read, and MUST treat a cycle or a missing continuation target as a damage finding, not an empty remainder — a truncated directory that reads as complete is silent data loss.

## 10 Placement hint

**Optional, advisory, and authoritative for nothing.** A writer MAY publish one placement hint per snapshot at `/hints/placement/<snapshot-id>`, recording which blob each object it newly created was written into.

It exists for one scenario: single-file recovery when the index is gone. Without it, finding one segment means fetching blob footers until the object identifier turns up — hours at scale **M** for one document, and that is the emergency path, so it is the worst place to be slow ([PT-10](../../docs/review/2026-08-fix-pressure-test.md#pt-10--emergency-single-file-restore-regressed-from-one-fetch-to-a-full-scan)).

```text
placement_hint = {
    1: u16       schema version, 1
    2: bytes[16] snapshot_id
    3: array     placements
}

placement = [ object_id[32], blob_id[16] ]
```

Placements MUST be sorted by `object_id` ascending. The object is a standalone metadata record ([04 §7](04-record.md)) like any other, and is sealed and encrypted the same way — it names object and blob identifiers, which [01 §2.1](01-object-layout.md#21-what-keys-must-not-reveal) keeps out of store keys and this keeps out of plaintext.

### 10.1 What a reader may do with it

A reader MAY consult the hint to choose which blob to fetch first. It MUST then verify that the record it finds carries the object identifier it wanted, exactly as it would have without the hint, and MUST fall back to the index or to a footer scan when the hint is absent, unreadable, or wrong.

**A hint is never evidence.** It is not consulted to decide whether an object exists, is not repaired when it goes stale, and is not part of any reachability or liveness calculation. Compaction moves records and does not update it — that is expected, and is why "detectably stale" is the design rather than a defect. → [ADR-0007](../../docs/adr/0007-logical-object-identifiers-in-manifests.md)

### 10.2 Why it is a separate object

The obvious design puts a `last_known_blob` beside each segment reference. It was rejected: a manifest's object identifier is derived from its bytes ([02 §3](02-identifiers.md#3-object-identifier)), so a physical hint inside one makes the same file version encode differently on two devices — and identical encoding across devices is precisely what makes cross-device deduplication work. The hint would have bought faster emergency recovery by quietly disabling a core property.

A separate object has neither problem, and gains one: absence is the normal case a reader must already handle, so there is no path on which an implementation can come to depend on the hint being there.

---

**Previous:** [05 — Blobs](05-blob.md) · **Next:** [07 — Index](07-index.md)

# ADR-0026 — Phase-1 capture shapes: hardlinks, diagnostics, special files, capabilities

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-MAN-003, FR-RST-004, FR-DED-003, NFR-COMP-004
**Related:** [ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md), [ADR-0024](0024-include-exclude-rule-dialect.md), [specification 06](../../specifications/repository-format/06-manifests.md), [architecture 06](../architecture/06-filesystem-capture.md), [phase-1 plan wave G](../phase-1-execution-plan.md#wave-g--decisions-before-bytes)

---

## Context

Phase 1's scanner is the first production writer of most of the manifest
surface, and the planning survey found the format's *capacity* complete but
several shapes it must write **unspecified**: no derivation rule for
`hardlink_group`, no vocabulary for `capture_diagnostics`, no trigger
definition for `capture_status`, no representation for special files, an
ambiguous `object_id` in alternate-stream entries, an unstated relationship
between directory tree entries and child trees, a filesystem capability
record (architecture 06 §2) with nowhere to live, an unpinned casefold for
the catalogue's path index, and an unresolved durability posture for
verification outcomes (FR-DED-003 / PT-259). Under the standing rule —
every gap is a flagged decision, never a silent choice — each is pinned
here before the first scanner-written byte exists. Errata in specification
06 point back at the decisions below until the normative text is folded in.

## Decisions

### 1 `hardlink_group` derivation

File-version key 12 is **16 bytes = HMAC-SHA-256(content_id_key,
`"fbp/hardlink/v1"` ‖ device_id ‖ u64(file_identity))[0..16]**, where
`file_identity` is the source filesystem's stable file identifier (inode on
POSIX, FileId on Windows) and `device_id` is the snapshot's key 2. Keyed
with the content-id key so the group leaks nothing about the inode space;
deterministic so the *same* inode captured twice in one snapshot — and in
later snapshots of the same device — yields the same group. Restore
reconstructs hardlinks by equal `hardlink_group` **within one snapshot**;
equality across snapshots is informational. The field remains present only
when link count > 1, per specification 06 §4.

### 2 `capture_diagnostics` vocabulary

Key 13 strings are machine-parseable `key: value` pairs, one fact per
string — the convention import provenance already established
(`imported-from:`, `legacy-id:`). Phase 1 pins:

| String | Meaning |
|---|---|
| `captured-inconsistent: <attempts>` | The file changed during read; content is the **last complete read** after `<attempts>` total attempts (architecture 06 §1's captured-inconsistent state). A file with **no** complete read is instead an error-manifest entry, reason 4 |
| `captured-identity-changed` | The name no longer refers to the object that was classified: revalidation observed a different device and file identifier. This is a substitution, not an edit, so the read is **not** retried — re-reading the name would read the substitute again. It appears only where the source could not take a handle on the content; where it could, the read came from the handle and the object cannot have changed |
| `special-kind: fifo\|socket\|chardev\|blockdev` | See decision 4 |
| `device: <major>,<minor>` | Device numbers for `chardev`/`blockdev` |
| `mount-boundary: <fstype>` | Recorded on a directory entry where traversal stopped at a mount point |

Unknown diagnostic keys are permitted (readers treat diagnostics as
informational text); *writers* in this repository emit only documented keys.

### 3 `capture_status` triggers

`capture_status = 2` (partial) **iff the snapshot references a non-empty
error manifest** (key 9 present). `1` (complete) otherwise. `3` (aborted)
is **never published by this implementation**: an aborted job publishes no
snapshot at all (the write intent expires or is retired unused, 08 §5–§7);
the value exists for readers because the format cannot forbid other
writers from using it.

### 4 Special files (`entry_kind = 4`)

FIFOs, sockets, and device nodes are captured as **zero-content file
versions**: `logical_length = 0`, no segment references, no whole-file
read, metadata captured normally, `special-kind:` (and for devices
`device:`) diagnostics carrying the identity. Capturing them is never an
error — reason 5 (`unsupported type`) is reserved for kinds the scanner
cannot even classify. Restore recreates them only where the target
platform can, else records a degradation in the receipt.

### 5 Alternate streams reference segment records

Metadata key 9's `[name, object_id, length]`: the `object_id` names a
**segment record (object type `0x01`)** holding the stream's entire
content, deduplicated and verified like any segment. In format v1 a
stream longer than the policy's maximum segment size is **refused**:
error-manifest reason 6 naming `<path>:<stream>`. (Multi-segment streams
would need a manifest-per-stream indirection — deferred until a real
archive demands it, recorded here rather than half-built.)

### 6 Directory entries name child tree manifests

In a tree manifest's `entries`, a subdirectory is `entry_kind = 3`
(directory-placeholder) and its `object_id` names the child directory's
**tree manifest** (object type `0x03`) — the first manifest of the chain
when the child is sharded. `entry_kind = 3` with an object id resolving to
any other object type is a damage finding. (An *empty* directory is still
a tree manifest with zero entries — the placeholder kind refers to the
entry's role in the parent, not to emptiness.)

### 7 Filesystem capability record: `source_filesystem` keys 4–6

Architecture 06 §2 requires recording the source's path limits; ADR-0022
§Decision 6 pinned `source_filesystem` to three keys and the codec rejects
unknowns. The record gains three **optional** keys, emitted when the
scanner can determine them:

| Key | Type | Value |
|-----|------|-------|
| 4 | u32 | `max_path_bytes` |
| 5 | u32 | `max_component_bytes` |
| 6 | bool | `reserved_names` — the source enforces Windows-style reserved names |

Pre-freeze this is an ordinary format addition (ADR-0014 applies; readers
built before it reject snapshots that carry the new keys — acceptable
while `unstable_format = true`). The codec change lands **with the first
writer that emits the keys** (wave T1), never before, so the frozen
`fixture-repository-v1` bytes are untouched. Restore-plan conflict
detection (08 §2) consumes these values; their absence means "limits
unknown", never "no limits".

### 8 The catalogue casefold key

The catalogue's path index key is the **Unicode simple case folding of the
NFC form** of each path component — exactly the fold rules-v1 pinned for
rule matching (ADR-0024), so a path that a rule matches case-insensitively
and a path the catalogue folds are never in different case regimes. The
fold is applied for indexing only; stored names remain the original bytes
(architecture 06 §2's never-normalise-destructively rule).

### 9 Verification outcomes are not durable in v1

FR-DED-003 wants verification outcomes to survive a catalogue rebuild;
PT-259 demanded the cost be either paid (a repository object) or accepted
explicitly. **v1 accepts the re-verification cost explicitly**: outcomes
live in the catalogue, a rebuild discards them, and `check` after a
rebuild starts from unverified. A durable receipt object is a format
addition deferred to the replication phase, where remote receipts make it
pay for its complexity. The requirement's traceability row records this
disposition.

### 10 Restore receipt and exportable plan are versioned JSON

Client-domain documents, **not** repository format surface: JSON with a
`schema_version` field, UTF-8, stable lower-snake-case keys.
The **receipt** accounts for every planned file — path, per-item outcome,
bytes, whole-file hash verified — and reports what the restore **as a
whole** achieved. The **plan** carries the selection, resolved file set,
conflict list with per-file resolutions, size estimates, and a resume
cursor. Full field lists live with the wave-R implementation and its
tests; this decision pins the medium, the versioning, and the
receipt-accounts-for-everything obligation (FR-RST-004).

#### Amendment (2026-08): what the receipt actually says

Two details drifted from what shipped, and one was a defect rather than a
naming slip.

The version field is `schema_version`, not `schema`. The per-item
vocabulary is `restored | skipped | failed` — there is no `degraded`
outcome, because a degradation is a property of an *attribute* on a file
that was restored, not a third fate for the file. The 06 §3 matrix
detail the original wording asked for is carried by the plan's
degradation list, which is declared before any byte moves.

The defect: the receipt carried a `complete` boolean computed as "nothing
failed", so a restore that skipped every symlink and special file
reported itself complete — against architecture 08 §3's absolute rule
that a restore of 9 999 of 10 000 files is a failed restore. Schema 2
replaces it with an `outcome` of `complete | partial | failed |
cancelled`; a skipped required item makes the restore partial. The
boolean is not carried alongside, because a reader that understood it
would still read `true` for a partial restore.

The quarantine ledger is now `displaced`, and names what it holds: files
that were already at a destination and were moved aside. It is not
architecture 08 §3.1's quarantine control, which is about where restored
content lands — see that section for why the two are kept apart.

## Consequences

**Positive** — the scanner can be written without inventing a single byte;
restore-plan conflict detection gets real capability data; hardlink groups
are stable and privacy-preserving; nothing here changes any committed
vector or fixture byte today.

**Negative** — decision 5 caps alternate streams at one segment (real NTFS
streams are near-universally tiny, but the cap is a v1 limitation);
decision 9 makes re-verification after rebuild a real, accepted cost;
decision 7 commits to a reader-visible format addition mid-phase, which is
only cheap because the format is pre-freeze.

## Alternatives considered

**Let the scanner invent each shape as it meets it.** The default, and the reason this record exists. Rejected: ten shapes decided under implementation pressure, one at a time, is ten chances to write a byte into a pre-freeze format because it was convenient that afternoon. Deciding them together made the interactions visible — decision 5's alternate-stream cap and decision 9's re-verification cost were only obviously acceptable once both were on the same page.

**Derive `hardlink_group` from the source inode number.** The obvious construction, and it leaks: an inode number is a stable identifier for a file on a specific filesystem, visible to a destination that holds only ciphertext everywhere else. Rejected for the keyed derivation of decision 1, which groups the same links without naming them.

**Capture special files (`entry_kind = 4`) by content.** A FIFO or a socket has no content to capture, a device node's "content" is the device, and a backup tool that opens one can block forever or read something it must not. Rejected in favour of recording the kind and metadata and restoring the node, which is what a restore actually needs.

**Store alternate streams as a manifest of their own.** More general than decision 5's one-segment cap, and unnecessary: real NTFS streams are near-universally tiny, and a second manifest type is format surface that must then be specified, versioned and read by every implementation. Rejected as a v1 limitation worth taking, revisitable as a format addition rather than a format correction.

**A free-text note instead of decision 2's `key: value` diagnostics.** Easier to write and impossible to consume: a restore planner deciding whether a file was captured inconsistently, or a UI explaining a degradation, would be pattern-matching English. Rejected in favour of one machine-parseable fact per string, following the convention import provenance had already set.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Ten shapes pinned ahead of the first scanner-written manifest (phase-1 wave G1) |

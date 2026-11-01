# ADR-0180 — Capture names the file flags it does not keep

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-MAN-003 (amended: a file's flags that no format key keeps are named among its capture diagnostics); FR-RST-003 and FR-RST-004 (amended by [Amendment 1](#amendment-1-2026-10--a-restore-names-the-file-flags-it-does-not-give-back): the plan and the receipt name the flags a restore does not set; FR-RST-004 amended again by [Amendment 2](#amendment-2-2026-10--the-recovery-tool-and-the-single-file-restore-name-them-too): so do the recovery tool and `restore-file`)
**Related:** [ADR-0026](0026-phase-1-capture-shapes.md) §2 (the `capture_diagnostics` vocabulary, which gains `file-flags:`), [ADR-0175](0175-a-restore-writes-back-the-windows-attribute-bits-it-may.md) (Windows' own attribute bits, which a format key keeps), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (immutable and system flags, and compression and encryption attributes, degraded and reported on Linux and macOS), [specification 06 §4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map) (key 13)

**Built:**
- The flags: `Domain/FileFlagBits` names them, and `Domain/MetadataRules` maps Linux's and macOS's bits to them, names them in a diagnostic and reads that back.
- The read: `Filesystem.Local/StatResult` carries the flags each platform's stat gives, and `Filesystem.Local/LocalFileSystemSource` names them among a file's capture diagnostics, which publication writes into its manifest.
- The change: `Repository.Format/FileVersionManifestCodec` counts the flags in the metadata digest an incremental compares, and publication, the catalogue projection, the forensic rebuild and the source comparer all count them alike; a manifest restated for unchanged bytes names the flags the file carries now.
- The tests:
  - `Domain.Tests/FileFlagsTests` — on every platform: Linux's bits are read only where the volume says it reports each, and no other; macOS's user and system flags map alike, and no other; the diagnostic names each flag in one order and none where there are none, and reads back as the flags it named, among other diagnostics, passing over a name no build knows.
  - `Filesystem.Tests/LocalScanTests` — on Linux a file carrying the no-dump flag is scanned with it named, a plain one with none, and a folder carrying it with none; on macOS a file carrying no-dump and hidden is scanned with both named.
  - `Repository.Tests/FileFlagsCaptureTests` — on Linux a real file's no-dump flag is named in its published manifest, and a plain file's manifest names none.
  - `Repository.Tests/MetadataOnlyChangeTests` — a file whose flags alone changed gets a new manifest naming them, with its content not read again; one whose flags went away names none; one whose flags stayed is short-circuited; the projected and the forensically rebuilt catalogue record the digest capture computed, flags and all; the comparer counts a flagged file unchanged and a change of flags a metadata change.

---

## Context

Architecture 06's metadata matrix has a file's immutable and system flags, and its compression and encryption attributes, degraded and reported on Linux and macOS. Capture read neither, so neither was reported. A file Linux kept immutable or append-only, or one macOS kept hidden or locked, was backed up as an ordinary file and restored as one, and no record said it had been anything else.

Both stat calls capture already makes return them. Linux's `statx` reports a file's immutable, append-only, no-dump, compressed and encrypted flags in `stx_attributes`, and says in `stx_attributes_mask` which of them the volume reports at all. macOS's `stat` carries `st_flags`, which hold the user's and the system's immutable and append-only flags, no-dump, compressed and hidden.

No key of the manifest's metadata map keeps them. Key 10 holds Windows' attribute bits, which a restore elsewhere would read as Windows' own. A new key is a format change: a reader of today refuses a manifest with a key it does not know.

## Decision

### 1. Capture reads the flags each platform's stat gives

On Linux a flag is read only where `stx_attributes_mask` says the volume reports it, so a volume that reports none is never taken to have none set. On macOS the user's and the system's immutable flags are one flag, and so are their append-only flags. Linux's other attributes, such as a mount's root, and macOS's other flags, such as the archived one, are not file flags in this sense and are not read.

### 2. Capture names them in a diagnostic

The flags a file carries are named in its capture diagnostics (specification 06 §4.1 key 13), `file-flags: immutable, no-dump`, each by one name and in one order: immutable, append-only, no-dump, compressed, encrypted, hidden. Diagnostics are informational text every reader accepts, so this changes no format. A link and a special file are named alike. A folder is kept as a tree, which carries no diagnostics, so a folder's flags are not named.

### 3. A change of flags alone is a change

An incremental passes over a file whose identity, size, modification time and metadata digest are unchanged, and re-emits its earlier manifest. Setting a file's flags moves its change time, not its modification time, so the digest must count them, or the earlier manifest would go on naming flags the file no longer carries, and a file backed up before this record would never have its flags named. The digest counts the flags a file's diagnostics name, and is the metadata's alone where it names none, so no file without flags is restated. A file whose flags changed gets a new manifest without its content being read again, keeping what the earlier one said of its read and naming the flags it carries now. The catalogue rebuilt from manifests, by projection or forensically, records the same digest, and the source comparer judges by it.

### 4. What is named can be read back

`MetadataRules.FileFlagsOf` reads a manifest's diagnostics back to the flags they name, passing over a name no build knows, so a restore can say what it does not give back.

## Consequences

- `inspect` shows a file's flags with its other diagnostics.
- A file whose flags change between backups gets a new manifest for the change, with its content not read again. The first backup by this build restates once each file that already carries flags.
- A volume that keeps many files compressed, as macOS keeps much of its own software, names `compressed` on each of them.

## What this does not do

- **Keep the flags.** A restore gives none back. Keeping them is a format key, with the format version and refusal ADR-0014 asks of one.

  > **2026-11: the key is format 6's metadata key 11 ([ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md)).** Nothing writes it yet: capture still names the flags among a file's diagnostics, and no restore gives one back.
- **Say so on restore.** The plan and the receipt do not yet name a file whose flags were not given back. That is the next record's.

  > **2026-10: taken up by [Amendment 1](#amendment-1-2026-10--a-restore-names-the-file-flags-it-does-not-give-back)** for the engine's restore: its plan and its receipt name a file's flags. The recovery tool and `restore-file` do not yet.
  >
  > **2026-10: and by [Amendment 2](#amendment-2-2026-10--the-recovery-tool-and-the-single-file-restore-name-them-too)** for the recovery tool and `restore-file`.
- **A folder's flags.** A tree carries no diagnostics.

  > **2026-11 ([ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md)).** Format 6's key 11 is in the metadata map a tree carries too, so a folder's flags can be kept there. Nothing writes it yet.

## Alternatives considered

- **Keep the flags in key 10.** Rejected. Key 10 holds Windows' attribute bits, and a restore on Windows would read Linux's or macOS's as its own.
- **Add a format key now.** Deferred. It is a format change, and naming what is lost comes first, with no change to what any reader accepts.

  > **2026-11: taken up by [ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md),** format 6, whose metadata key 11 keeps the flags.

## Amendment 1 (2026-10) — a restore names the file flags it does not give back

The record left the restore silent ("What this does not do"): a file captured with flags came back without them, and neither the plan nor the receipt said so. Architecture 06's matrix has such flags degraded and reported, and only capture's half of that was built.

**Decision.** No restore sets a file's flags, so the restore says so wherever a file carried them:

- **the plan declares them on every target.** Each item's facts carry the flags its manifest's diagnostics name (`MetadataRules.FileFlagsOf`). The plan declares the files and links that carried any on a line of its own, `file-flags`, naming each flag among them in the diagnostic's order. No target sets one, so the line is the same wherever they land;
- **the receipt names them beside each item.** A file's item, or a link's, says `file flag(s) not applied: immutable, no-dump (not set by a restore)` (`MetadataRules.FileFlagsNotApplied`). A later name of a hard-link group says what its first name says, since the flags are its file's. The item is still restored: a flag changes nothing of what the content achieved, as a Windows attribute bit no restore sets changes nothing ([ADR-0175 Amendment 1](0175-a-restore-writes-back-the-windows-attribute-bits-it-may.md));
- **the receipt's not-applied list does not name them.** That list holds the metadata map's keys in the format's words, and no key holds a flag.

The recovery tool and `restore-file` still say nothing of a file's flags. A folder's are named neither at capture nor on restore, since a tree carries no diagnostics.

> **2026-10: the recovery tool and `restore-file` are taken up by [Amendment 2](#amendment-2-2026-10--the-recovery-tool-and-the-single-file-restore-name-them-too).**

**Built:** `Domain/MetadataRules`; `Restore/RestoreMetadata`; `Restore/RestoreBlobSet`; `Restore/RestoreExecutor`:

- `Domain.Tests/FileFlagsTests`: the flags' names are the diagnostic's, in its order; what a restore says names each flag, and nothing where there are none;
- `Repository.Tests/RestoreMetadataHonestyTests`: each file's facts carry the flags its manifest names; the plan declares the two flagged files of three on its own line, naming each flag, on a POSIX target and on one that writes Windows' attribute bits and streams; a file's receipt item names its flags and is still restored, its not-applied list empty; on a POSIX host a link's item names its flags too;
- `Repository.Tests/RestoreHardLinkTests`: a later name of a hard-link group names the flags its first name names.

## Amendment 2 (2026-10) — the recovery tool and the single-file restore name them too

Amendment 1 left the recovery tool and `restore-file` silent: a file recovered with either came back without its flags, and nothing said so. They write a file's metadata by the executor's rule (ADR-0149, ADR-0150), and should say what they did not give back in the same words.

**Decision.** Both name a file's flags as the executor's receipt does, with `MetadataRules.FileFlagsNotApplied`:

- **the recovery tool notes each item that carried them.** A file, a link, or a later name of a hard-link group that the tool restores gets a note of its own kind, `FlagsNotApplied`, whose reason is the receipt's words, and still counts as restored. The report's count of attributes not applied does not name them, as the receipt's list does not;
- **`restore-file` says so on a line of its own** after what it says of the rest of the file's metadata.

**Built:** `Recovery/RecoveryNote`; `Recovery/RecoverySession`; `Cli/CliApplication`:

- `Repository.Tests/RecoveryMetadataTests`: a file's flags are noted in the receipt's words, the file restores, and no attribute is counted not applied;
- `Repository.Tests/RecoveryLinkTests`: on a POSIX host, a link's flags are noted and the link restores;
- `Repository.Tests/RecoveryHardLinkTests`: a later name of a group is noted as its first name is;
- `Cli.Tests/InspectionCommandTests`: on Linux, `restore-file` of a file backed up with the no-dump flag says it was not given back.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Capture reads a file's flags from each platform's stat, Linux's only where the volume reports each, and names them in the file's capture diagnostics, which publication writes into its manifest; they read back as the flags they name, and the digest an incremental compares counts them. `Domain/FileFlagBits`; `Domain/MetadataRules`; `Filesystem.Local/LocalFileSystemSource`; `Repository.Format/FileVersionManifestCodec`; `Domain.Tests/FileFlagsTests`; `Filesystem.Tests/LocalScanTests`; `Repository.Tests/FileFlagsCaptureTests`; `Repository.Tests/MetadataOnlyChangeTests` |
| 2026-10 | Accepted (Amendment 1) | A restore names the file flags it does not give back: the plan on a line of its own on every target, naming each flag, and the receipt beside each file and link that carried them, a later hard-link name as its first; the item is still restored. `Domain/MetadataRules`; `Restore/RestoreMetadata`; `Restore/RestoreBlobSet`; `Restore/RestoreExecutor`; `Domain.Tests/FileFlagsTests`; `Repository.Tests/RestoreMetadataHonestyTests`; `Repository.Tests/RestoreHardLinkTests` |
| 2026-10 | Accepted (Amendment 2) | The recovery tool notes the flags a file, a link or a later hard-link name carried, in the receipt's words, and still counts it restored; `restore-file` says so on a line of its own. `Recovery/RecoveryNote`; `Recovery/RecoverySession`; `Cli/CliApplication`; `Repository.Tests/RecoveryMetadataTests`; `Repository.Tests/RecoveryLinkTests`; `Repository.Tests/RecoveryHardLinkTests`; `Cli.Tests/InspectionCommandTests` |
| 2026-11 | Accepted (related) | [ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md) gives the flags a key, format 6's metadata key 11, which a tree's metadata carries too. Nothing writes it yet, so capture still names them among a file's diagnostics and no restore gives one back |

# ADR-0180 — Capture names the file flags it does not keep

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-MAN-003 (amended: a file's flags that no format key keeps are named among its capture diagnostics); FR-RST-003 and FR-RST-004 (amended by [Amendment 1](#amendment-1-2026-10--a-restore-names-the-file-flags-it-does-not-give-back): the plan and the receipt name the flags a restore does not set; FR-RST-004 amended again by [Amendment 2](#amendment-2-2026-10--the-recovery-tool-and-the-single-file-restore-name-them-too): so do the recovery tool and `restore-file`); FR-MAN-003 amended again by [Amendment 3](#amendment-3-2026-11--format-6-keeps-the-flags-a-folders-too): from format 6 a file's and a folder's flags are kept in metadata key 11; FR-RST-003 and FR-RST-004 amended again by [Amendment 4](#amendment-4-2026-11--a-restore-gives-the-flags-back-where-it-may): the engine's restore gives a file's, a folder's and a link's flags back where the target sets them, and names only those it does not; FR-RST-004 amended again by [Amendment 5](#amendment-5-2026-11--the-recovery-tool-and-the-single-file-restore-give-them-back-too): so do the recovery tool and `restore-file`
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

> **2026-11 ([Amendment 3](#amendment-3-2026-11--format-6-keeps-the-flags-a-folders-too)).** From format 6 publication keeps the flags in metadata key 11 rather than naming them, a folder's as well as a file's, and below format 6 names them as here.

### 3. A change of flags alone is a change

An incremental passes over a file whose identity, size, modification time and metadata digest are unchanged, and re-emits its earlier manifest. Setting a file's flags moves its change time, not its modification time, so the digest must count them, or the earlier manifest would go on naming flags the file no longer carries, and a file backed up before this record would never have its flags named. The digest counts the flags a file's diagnostics name, and is the metadata's alone where it names none, so no file without flags is restated. A file whose flags changed gets a new manifest without its content being read again, keeping what the earlier one said of its read and naming the flags it carries now. The catalogue rebuilt from manifests, by projection or forensically, records the same digest, and the source comparer judges by it.

> **2026-11 ([Amendment 3](#amendment-3-2026-11--format-6-keeps-the-flags-a-folders-too)).** The digest counts the flags key 11 keeps as it counts those a diagnostic names, so a manifest of either form digests alike.

### 4. What is named can be read back

`MetadataRules.FileFlagsOf` reads a manifest's diagnostics back to the flags they name, passing over a name no build knows, so a restore can say what it does not give back.

## Consequences

- `inspect` shows a file's flags with its other diagnostics.
- A file whose flags change between backups gets a new manifest for the change, with its content not read again. The first backup by this build restates once each file that already carries flags.
- A volume that keeps many files compressed, as macOS keeps much of its own software, names `compressed` on each of them.

## What this does not do

- **Keep the flags.** A restore gives none back. Keeping them is a format key, with the format version and refusal ADR-0014 asks of one.

  > **2026-11: the key is format 6's metadata key 11 ([ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md)).** Nothing writes it yet: capture still names the flags among a file's diagnostics, and no restore gives one back.
  >
  > **2026-11: capture keeps them there from format 6 ([Amendment 3](#amendment-3-2026-11--format-6-keeps-the-flags-a-folders-too)).** No restore gives one back yet.
  >
  > **2026-11: the engine's restore gives them back where the platform and the account let it ([Amendment 4](#amendment-4-2026-11--a-restore-gives-the-flags-back-where-it-may)).** The recovery tool and `restore-file` do not yet.
  >
  > **2026-11: and so do the recovery tool and `restore-file` ([Amendment 5](#amendment-5-2026-11--the-recovery-tool-and-the-single-file-restore-give-them-back-too)).**
- **Say so on restore.** The plan and the receipt do not yet name a file whose flags were not given back. That is the next record's.

  > **2026-10: taken up by [Amendment 1](#amendment-1-2026-10--a-restore-names-the-file-flags-it-does-not-give-back)** for the engine's restore: its plan and its receipt name a file's flags. The recovery tool and `restore-file` do not yet.
  >
  > **2026-10: and by [Amendment 2](#amendment-2-2026-10--the-recovery-tool-and-the-single-file-restore-name-them-too)** for the recovery tool and `restore-file`.
- **A folder's flags.** A tree carries no diagnostics.

  > **2026-11 ([ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md)).** Format 6's key 11 is in the metadata map a tree carries too, so a folder's flags can be kept there. Nothing writes it yet.
  >
  > **2026-11: taken up by [Amendment 3](#amendment-3-2026-11--format-6-keeps-the-flags-a-folders-too)** from format 6. The plan and the receipt do not yet name a folder's flags.
  >
  > **2026-11: and by [Amendment 4](#amendment-4-2026-11--a-restore-gives-the-flags-back-where-it-may),** whose plan and receipt name a folder's flags that do not land, and whose restore gives the rest back.

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

> **2026-11 ([Amendment 4](#amendment-4-2026-11--a-restore-gives-the-flags-back-where-it-may)).** A target now sets some flags. The plan names only those the target will not set, and the receipt only those that did not land, in words that no longer say no restore sets them: `file flag(s) not applied: immutable`.

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

> **2026-11 ([Amendment 5](#amendment-5-2026-11--the-recovery-tool-and-the-single-file-restore-give-them-back-too)).** Both now give the flags back by Amendment 4's rule, and name only those that did not land, in words that no longer say no restore sets them.

## Amendment 3 (2026-11) — format 6 keeps the flags, a folder's too

[ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md) gave a file's flags a key, metadata key 11, and nothing wrote it. Capture still named them in a diagnostic, which a restore can read but which no format guarantees. A folder's were not named at all, since a tree carries no diagnostics.

**Decision.**

- **From format 6, publication keeps the flags in key 11 and names them no more.**
  - The scanner names them as before, a file's, a link's and a special file's.
  - Publication moves them into the entry's metadata where the repository's effective version is 6 or later (`MetadataRules.KeepFileFlags`), so one rule decides for every entry.
  - Below format 6 they stay named, since a reader of that format refuses key 11.
- **A folder's flags are kept too.**
  - The scanner names a folder's flags as it names a file's.
  - From format 6 publication keeps them in the folder's tree, whose metadata is the same map.
  - Below format 6 a folder's are written nowhere, as before.
- **The flags count alike in the digest an incremental compares, wherever a manifest keeps them.**
  - `FileVersionManifestCodec.MetadataDigest` counts the flags key 11 keeps as it counts the flags a diagnostic names. So a file captured before format 6 and the same file captured at 6 digest alike.
  - An upgrade therefore restates no file for its flags. A flagged file's manifest goes on naming them until something about the file changes, and the manifest written then keeps them in key 11.
  - The source comparer, the projected catalogue and the forensically rebuilt one need no word of the format.
- **Every reader takes either form.**
  - The plan, the receipt, the recovery tool and `restore-file` read the flags key 11 keeps and those a diagnostic names (`MetadataRules.FileFlagsOf`), and say what they did not give back in Amendments 1 and 2's words.
  - The receipt's not-applied list still names none of them: they are named once, beside the item.
- **`inspect-manifest` shows the flags key 11 keeps**, a file's and a folder's, on a line of their own.

**Built:** `Domain/MetadataRules`; `Repository.Format/Manifests/FileVersionManifestCodec`; `Repository/SnapshotPublication`; `Filesystem.Local/LocalFileSystemSource`; `Restore/RestoreBlobSet`; `Restore/RestoreExecutor`; `Recovery/RecoverySession`; `Cli/CliApplication`:

- `Domain.Tests/FileFlagsTests`:
  - at format 6 the flags a diagnostic names move into key 11 and the other diagnostics stay;
  - below format 6 nothing moves;
  - an entry without flags is kept as it was;
  - a reader takes the key's flags and a diagnostic's alike.
- `Repository.Tests/ManifestCodecTests`: the flags digest alike whether key 11 keeps them or a diagnostic names them.
- `Filesystem.Tests/LocalScanTests`: on Linux a folder carrying the no-dump flag names it, and a plain folder names none.
- `Repository.Tests/FileFlagsCaptureTests`, on Linux:
  - at format 6 a real file's and a real folder's no-dump flag are kept in key 11, and named nowhere;
  - at format 5 the file's is named, and neither is kept.
- `Repository.Tests/MetadataOnlyChangeTests`:
  - at format 6 a file whose flags alone changed gets a new manifest keeping them, its content not read again, and one whose flags stayed is short-circuited;
  - upgraded to format 6, a file whose flags stayed is not restated, and one whose flags changed keeps them in key 11 and names them no more;
  - at format 6 the projected and the forensically rebuilt catalogue record the digest capture computed, and the comparer counts a flagged file unchanged and a change of flags a metadata change.
- `Repository.Tests/RestoreMetadataHonestyTests`, `Repository.Tests/RecoveryMetadataTests`: at format 6 the plan declares the flags key 11 keeps, and the receipt and the recovery tool name them as before.
- `Cli.Tests/InspectionCommandTests`: on Linux, `inspect-manifest` shows the no-dump flag a backed-up file's and folder's manifests keep, and no diagnostic names it.

**Not done.**

- **A restore gives no flag back.** That is the next record's.

  > **2026-11: taken up by [Amendment 4](#amendment-4-2026-11--a-restore-gives-the-flags-back-where-it-may)** for the engine's restore.
- **A folder's flags are neither declared in the plan nor named in the receipt.** The plan's facts are a file's and a link's, and a folder is given its metadata by ADR-0086's rule, which names nothing of its flags. That is the next record's too.

  > **2026-11: taken up by [Amendment 4](#amendment-4-2026-11--a-restore-gives-the-flags-back-where-it-may).**

## Amendment 4 (2026-11) — a restore gives the flags back where it may

Amendments 1 to 3 named the flags a restore did not give back, and it gave none back. Since Amendment 3 format 6 keeps them, a folder's too, and both platforms that carry them have a call that sets them. Architecture 06's matrix has them degraded only where the target cannot carry them.

**Decision.** The engine's restore gives a file's, a folder's and a link's flags back where the platform and the account let it, and names only those that did not land.

- **What a target sets** (`FileFlags.Settable`, which `RestoreTargetProfile` carries as the flags it sets on a file or a folder and on a link):
  - Linux sets no-dump for the file's owner. It sets immutable and append-only only for a process holding `CAP_LINUX_IMMUTABLE`, read from the process's effective set, since a container's root need not hold it. It sets nothing on a link, which cannot be opened without following it.
  - macOS sets its owner's immutable, append-only, no-dump and hidden flags, a link's own through `lchflags`.
  - Neither sets compressed or encrypted: the volume gives the one, and only a key the other. Windows keeps none of them.
- **When they go on.**
  - A file's and a link's once every item has landed, before any folder settles. An immutable or append-only file takes no further name, so each later name of a hard-link group is made first, and an immutable one refuses every write after.
  - A hard-link group's file is set once, under its first name, and every name of it says what the file got.
  - A folder's last in its own settle, after its times, deepest folder first: an immutable folder refuses its own times and any name made in it.
  - What stands where an item landed is held to what landed there, reached without leaving the root through a link, as a folder is before it settles, because the run has been long.
  - A stopped run sets none. A resume writes into the folders the run made and onto the files it landed, which an immutable flag would refuse, so the flags wait for the run that finishes.
- **Each flag is tried so that one refused leaves the rest.** On Linux the owner's flag and the privileged ones are set apart. On macOS one call sets them all, and where it is refused each is tried alone. What the entry carries afterwards is read back, and that is what the receipt says (`IFileFlags`, `MetadataWriter.WriteFlags`).
- **The plan names only the flags the target will not set**, on the `file-flags` line, counting files, folders and links. A folder's facts now carry its tree's flags. Where immutable or append-only is why, the line says that setting them needs root or `CAP_LINUX_IMMUTABLE`. A target that sets every flag an item carries says nothing of it.
- **The receipt names only those that did not land:** `file flag(s) not applied: immutable` (`MetadataRules.FileFlagsNotApplied`). A flag given back is not named, and a folder's item names its own. The item is still restored, as before.

**Consequences.**

- **A file given back immutable cannot be replaced by a later restore**, nor can a folder given back immutable take a new name. A later run into the same destination fails that item with the platform's refusal, which the receipt says. Clearing the flag is the person's to do.
- **A run cut short after its flags went on**, while its folders settled, leaves files a resume cannot write onto again: their metadata is refused, and the resume's receipt says so of each.

**Not done.**

- **The recovery tool and `restore-file` give no flag back.** They still say so in Amendment 2's words. That is the next record's.

  > **2026-11: taken up by [Amendment 5](#amendment-5-2026-11--the-recovery-tool-and-the-single-file-restore-give-them-back-too).**
- **A folder that already stands keeps its own flags**, as it keeps the rest of its metadata ([ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md)).
- **An immutable file at the destination is not cleared to replace it**, whether a person set it or an earlier restore did.

**Built:** `Domain/FileFlags`; `Domain/LinuxCapabilities`, which `Domain/FileOwnership` reads CAP_CHOWN through too; `Domain/MetadataWriter`; `Domain/MetadataTarget`; `Domain/MetadataRules`; `Restore/RestorePlan`; `Restore/RestoreExecutor`; `Restore/RestoreMetadata`; `Restore/RestoreBlobSet`:

- `Domain.Tests/FileFlagWriteTests`:
  - on every platform, the rule: Linux sets no-dump, and immutable and append-only only with the capability, and nothing on a link; macOS sets its owner's four, on a link too; this platform's is its own, and never compressed or encrypted;
  - the receipt's words name each flag that did not land, and nothing where every one did;
  - the writer asks only for the flags the target sets on that kind, and answers which landed; the local target sets what this platform may;
  - on Linux, a real file's and folder's flags are set as the account may, each flag apart, and none through a link; on macOS a file's, a folder's and a link's own;
  - where nothing stands, no flag is said to land; on Linux a device, which keeps no inode flags, is said to get none; on macOS a file on the read-only system volume, which refuses each flag together and alone, is said to get only what it already carried, and keeps it.
- `Repository.Tests/RestoreFileFlagsTests`:
  - only the flags the target sets are asked for, and only those that did not land are named;
  - a file's flags go on once every item has landed, so a hard-link group's later name is made first, and the group is set once and named alike;
  - a folder's go on last in its settle, after its times, deepest first;
  - on a POSIX host, a link's own are set where the target sets a link's, and the rest named;
  - a stopped run sets none, a folder's either, and names them;
  - on a POSIX host, none is set through a link put where a file landed;
  - the local target sets what this platform may, and its writer is told;
  - on Linux and macOS, a real restore gives back what the account may set to a file, its hard-link group and a folder, the group still one file.
- `Repository.Tests/RestoreMetadataHonestyTests`: the plan names only the flags the target will not set, counting only the files that carry them, says where the privilege is why, and names none where the target sets them all; at format 6 a folder's facts carry its tree's flags, which the plan counts; on a target that sets none, the receipt names each flag as before.
- `Repository.Tests/RestoreHardLinkTests`: a later name of a group names what its first name names.

## Amendment 5 (2026-11) — the recovery tool and the single-file restore give them back too

Amendment 4 had the engine's restore give the flags back, and left the recovery tool and `restore-file` naming every flag as not applied, in Amendment 2's words that no restore sets them. Both write a file's metadata by the executor's rule (ADR-0149, ADR-0150), so they should give back what it gives back.

**Decision.** Both set a file's, a folder's and a link's flags by Amendment 4's rule, through `MetadataWriter.WriteFlags`, onto this platform as the account running them, and name only those that did not land, in the receipt's words (`MetadataRules.FileFlagsNotApplied`).

- **The recovery tool sets them as the executor does.**
  - It notes each file and link it lands with flags, and sets them in one pass once the whole tree has landed, before any folder settles. A hard-link group's file is set once, under its first name, and every name notes what the file got.
  - A folder's flags go on last in its settle, after its times, and only onto a folder the tool made that still stands where it made it.
  - Each is set only onto what still stands where it landed, reached through folders the tool made. A link put in a file's place gets nothing, and the file's flags are noted.
  - A run that stops part way sets none.
  - The note, `FlagsNotApplied`, names only what did not land. The report's count of attributes not applied still names no flag.
- **`restore-file` sets the file's flags last**, after the rest of its metadata, and prints a line only for those that did not land.
- **The one-argument wording goes.** Nothing says any longer that no restore sets a flag.

**Not done.** The consequences Amendment 4 records hold here too: a file given back immutable cannot be replaced by a later recovery or `restore-file` into the same place.

**Built:** `Recovery/RecoverySession`; `Cli/CliApplication`; `Domain/MetadataRules`:

- `Repository.Tests/RecoveryFileFlagsTests` (on Linux and macOS):
  - a file, its hard-link group and a folder get back what this platform and the account may set, and each notes the rest; the group is still one file, and the folder has its times;
  - none is set through a link put where a file landed, nor through one put where a folder above it landed, which the platform's call would follow, and the file's flags are noted;
  - a run that stops part way sets no file's flag and no folder's.
- `Repository.Tests/RecoveryMetadataTests`: a file's no-dump and hidden flags are given back where this platform sets them and noted where it does not, below format 6 and at it.
- `Repository.Tests/RecoveryLinkTests`: on a POSIX host a link's hidden flag is set on macOS and noted on Linux.
- `Repository.Tests/RecoveryHardLinkTests`: a later name of a group notes what its first name notes, which is nothing where this platform sets no-dump.
- `Cli.Tests/InspectionCommandTests`: on Linux `restore-file` gives back the no-dump flag a file was backed up with, and says nothing of it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Capture reads a file's flags from each platform's stat, Linux's only where the volume reports each, and names them in the file's capture diagnostics, which publication writes into its manifest; they read back as the flags they name, and the digest an incremental compares counts them. `Domain/FileFlagBits`; `Domain/MetadataRules`; `Filesystem.Local/LocalFileSystemSource`; `Repository.Format/FileVersionManifestCodec`; `Domain.Tests/FileFlagsTests`; `Filesystem.Tests/LocalScanTests`; `Repository.Tests/FileFlagsCaptureTests`; `Repository.Tests/MetadataOnlyChangeTests` |
| 2026-10 | Accepted (Amendment 1) | A restore names the file flags it does not give back: the plan on a line of its own on every target, naming each flag, and the receipt beside each file and link that carried them, a later hard-link name as its first; the item is still restored. `Domain/MetadataRules`; `Restore/RestoreMetadata`; `Restore/RestoreBlobSet`; `Restore/RestoreExecutor`; `Domain.Tests/FileFlagsTests`; `Repository.Tests/RestoreMetadataHonestyTests`; `Repository.Tests/RestoreHardLinkTests` |
| 2026-10 | Accepted (Amendment 2) | The recovery tool notes the flags a file, a link or a later hard-link name carried, in the receipt's words, and still counts it restored; `restore-file` says so on a line of its own. `Recovery/RecoveryNote`; `Recovery/RecoverySession`; `Cli/CliApplication`; `Repository.Tests/RecoveryMetadataTests`; `Repository.Tests/RecoveryLinkTests`; `Repository.Tests/RecoveryHardLinkTests`; `Cli.Tests/InspectionCommandTests` |
| 2026-11 | Accepted (related) | [ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md) gives the flags a key, format 6's metadata key 11, which a tree's metadata carries too. Nothing writes it yet, so capture still names them among a file's diagnostics and no restore gives one back |
| 2026-11 | Accepted (Amendment 3) | From format 6 publication keeps a file's and a folder's flags in metadata key 11 and names them no more, the scanner naming a folder's too; the digest counts them alike wherever kept, so an upgrade restates nothing; every reader takes either form, and `inspect-manifest` shows them. `Domain/MetadataRules`; `Repository.Format/Manifests/FileVersionManifestCodec`; `Repository/SnapshotPublication`; `Filesystem.Local/LocalFileSystemSource`; `Cli/CliApplication`; `Domain.Tests/FileFlagsTests`; `Repository.Tests/FileFlagsCaptureTests`; `Repository.Tests/MetadataOnlyChangeTests`; `Cli.Tests/InspectionCommandTests` |
| 2026-11 | Accepted (Amendment 4) | The engine's restore gives a file's, a folder's and a link's flags back where the platform and the account let it: Linux's no-dump, and immutable and append-only with `CAP_LINUX_IMMUTABLE`, none on a link; macOS's owner's four, a link's too; a file's and a link's once every item has landed, a folder's last in its settle, none by a stopped run; the plan names only the flags the target will not set, counting folders, and the receipt only those that did not land. `Domain/FileFlags`; `Domain/LinuxCapabilities`; `Domain/MetadataWriter`; `Domain/MetadataTarget`; `Restore/RestorePlan`; `Restore/RestoreExecutor`; `Restore/RestoreMetadata`; `Restore/RestoreBlobSet`; `Domain.Tests/FileFlagWriteTests`; `Repository.Tests/RestoreFileFlagsTests`; `Repository.Tests/RestoreMetadataHonestyTests` |
| 2026-11 | Accepted (Amendment 5) | The recovery tool and `restore-file` give a file's, a folder's and a link's flags back by Amendment 4's rule: the tool sets a file's and a link's once the tree has landed, a group's once, a folder's last in its settle, none through a link put in a file's place, and none in a run that stops; each notes only what did not land, and `restore-file` prints only that. `Recovery/RecoverySession`; `Cli/CliApplication`; `Domain/MetadataRules`; `Repository.Tests/RecoveryFileFlagsTests`; `Repository.Tests/RecoveryMetadataTests`; `Repository.Tests/RecoveryLinkTests`; `Repository.Tests/RecoveryHardLinkTests`; `Cli.Tests/InspectionCommandTests` |

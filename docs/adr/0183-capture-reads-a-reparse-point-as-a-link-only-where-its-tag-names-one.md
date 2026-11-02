# ADR-0183 — Capture reads a reparse point as a link only where its tag names one

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-MAN-003 (amended: on Windows a reparse point is captured as a link only where its tag names another path, and any other is captured as the file or folder it is, with its content; amended again by [Amendment 1](#amendment-1-2026-11--capture-keeps-a-junctions-form): from format 6 a junction's form is kept in manifest key 14); FR-RST-004 and FR-RST-003 (amended by [Amendment 2](#amendment-2-2026-11--a-restore-on-windows-makes-a-junction-again): a restore on Windows makes a junction a junction, and a target that makes none says so)
**Related:** [architecture 06 §4.1](../architecture/06-filesystem-capture.md#41-links-are-classified-before-they-are-traversed) (links are classified before they are traversed), [ADR-0181](0181-a-restore-on-windows-makes-a-symlink-where-the-account-may.md) (a restore on Windows makes links), [ADR-0182](0182-capture-reads-a-links-own-security-descriptor-on-windows.md) (a link's own descriptor, read through a handle on the reparse point)

**Built:**
- The rule: `Filesystem.Local/ReparsePoints` says what an entry is from its attribute bits and its reparse tag.
- The read: `Filesystem.Local/WindowsInterop` reads a reparse point's tag through a handle opened on the reparse point itself, and `Filesystem.Local/LocalFileSystemSource` asks for it only where an entry carries the reparse bit.
- The tests:
  - `Filesystem.Tests/ReparsePointTests` — on every platform: a symbolic link to a file or a folder and a mount point are links; a compressed file and a deduplicated sparse file are files, and a cloud-kept folder a folder; a reparse point whose tag was not read is a link; an entry without the reparse bit is what its directory bit says, whatever tag is given.
  - `Filesystem.Tests/LocalScanTests` — on Windows a file carrying a reparse point whose tag names no other path, and which no filter on the machine keeps, is scanned as a file with no link target and its full length; its content, which no filter gives, does not open, where a link would have kept nothing.

---

## Context

Architecture 06 §4.1 has a link classified before it is traversed: an entry carrying both a directory marker and a link marker is a link, never descended into. On Windows, capture took the reparse-point bit for the link marker, and every entry carrying it for a link.

The bit says only that the entry has a reparse point. Its tag says what kind:

- **A symbolic link or a mount point names another path.** A junction is a mount point. These are what §4.1 is about, and .NET reads their targets.
- **Every other tag is a filter's.** Windows Server's data deduplication keeps a file's chunks in a store and leaves a reparse point, which capture sees. A cloud provider's placeholders can show one. Read through the filter, each is the file or the folder it always was. The overlay filter that `compact /exe` compresses through leaves one too, but hides it: a file it compresses shows no reparse bit, so capture always took it for the file it is.

Taken for a link, such a file was captured with no target and none of its content. A restore then skipped it as a link it could not make. On a deduplicated volume that is most of the files, and nothing said so. A folder kept by such a filter was not descended into.

## Decision

### 1. The tag decides

Where an entry carries the reparse bit, its tag is read through a handle opened on the reparse point itself, which reads it without going through the filter or the link.

- A symbolic link (`IO_REPARSE_TAG_SYMLINK`) or a mount point (`IO_REPARSE_TAG_MOUNT_POINT`) is a link, never followed, as before.
- Any other tag is the file or the folder its directory bit says. Its content is read as any file's is, through the filter, which gives it whole.

### 2. A tag that cannot be read is a link

A reparse point whose tag cannot be read is a link, never followed. What it is cannot be said, and a folder's link taken for a folder would be descended into, out of the approved root, which §4.1 forbids.

### 3. Only a reparse point is asked

The tag is read only where the bit is set, so an entry without a reparse point costs nothing more.

## Consequences

- A deduplicated file is backed up with its content, and restores as an ordinary file. The reparse point, the filter's, is not restored. The file's attribute bits treat it as one of the bits that say what an entry is, which a restore gives only by what it makes ([ADR-0175](0175-a-restore-writes-back-the-windows-attribute-bits-it-may.md)).
- A folder a filter keeps is descended into, and what it holds is backed up.
- A file whose tag no filter on the machine keeps does not open. Its read fails and is reported as any unreadable file's is, where it was kept as a link with nothing in it.
- Reading a file a cloud provider keeps only as a placeholder may make the provider fetch it, as any program reading it would.
- A backup taken before this record holds such a file as a link with no target. The next backup after it captures the file's content, as a file it had not held.

## What this does not do

- **Junctions as junctions.** A mount point is still captured as a link, and comes back as a folder's symbolic link ([ADR-0181](0181-a-restore-on-windows-makes-a-symlink-where-the-account-may.md)).

  > **2026-11 ([ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md)).** Format 6's manifest key 14 says that a link is a mount point. Nothing writes it yet, so a junction is still captured as a link with no form.
  >
  > **2026-11: capture keeps it ([Amendment 1](#amendment-1-2026-11--capture-keeps-a-junctions-form)).** From format 6 a junction's manifest says it is a mount point, and below 6 its diagnostics name it. No restore reads it yet, so it still comes back as a folder's symbolic link.
  >
  > **2026-11: and a restore on Windows makes it a junction again ([Amendment 2](#amendment-2-2026-11--a-restore-on-windows-makes-a-junction-again)).**
- **A placeholder left unfetched.** A file a cloud provider has not brought down is read like any other, which fetches it. Leaving it as a placeholder would mean capturing it as one, which no format key keeps.

## Alternatives considered

- **Leave every reparse point a link.** Rejected. It loses the content of every file a filter keeps, without a word.
- **Read every entry's tag.** Rejected. It costs an open for every entry, and only an entry with the bit can have one.
- **Take an unreadable tag for a file.** Rejected. A folder's link whose tag could not be read would be walked through.

## Amendment 1 (2026-11) — capture keeps a junction's form

[ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md) gave a link's form a key: format 6's manifest key 14, whose one value says a link is a mount point. Capture read a mount point's tag to call it a link, then kept nothing of it, so a junction was captured as a link like any other. This amendment writes the key, under ADR-0184 §4's rule that nothing below format 6 holds it.

**Decision.** Capture names a junction a mount point, and publication keeps that from format 6.

- **A junction is a mount point that names a folder by its drive's path.** It carries the mount-point tag, and .NET reads its target as a path such as `C:\Users\Shared`. `Filesystem.Local/ReparsePoints` says which form a link has from its tag and its target.
  - A volume mounted on a folder carries the same tag, and names the volume (`Volume{…}\`), which belongs to the machine it was mounted on. It keeps no form, and is captured as before, a link. So is a mount point whose target cannot be read.
  - The tag is read once, where the reparse bit is set, as before, and the stat carries it to where the target is read.
- **Capture names the form as it names a file's flags,** in a capture diagnostic: `link-form: mount-point` (`MetadataRules.LinkFormDiagnostic`).
- **Publication keeps it from format 6.** A link's manifest carries the form in key 14 and names it no more (`MetadataRules.KeepLinkForm`).
  - Below format 6 no key keeps it, and a reader of that format refuses key 14, so it stays named among the diagnostics.
  - Only a link carries a form. A symbolic link's manifest is what it was at every format.
  - A link is never taken from its previous version unread, so the backup after an upgrade to 6 writes the key for every junction.
- **`inspect-manifest` shows it,** on a line of its own: `link form      mount point`.

**Not done.**

- **No restore reads the form yet.** A junction still comes back as a folder's symbolic link where the account may make one ([ADR-0181](0181-a-restore-on-windows-makes-a-symlink-where-the-account-may.md)). On Linux or macOS it comes back as a symbolic link to a Windows path. Making a junction again is the next amendment's.

  > **2026-11: taken up for the engine's restore by [Amendment 2](#amendment-2-2026-11--a-restore-on-windows-makes-a-junction-again).** The recovery tool is the next.
- **A volume mounted on a folder keeps no form.** Its target names a volume of the machine it was captured on, and no restore elsewhere could mount it.

**Built:** `Filesystem.Local/ReparsePoints`; `Filesystem.Local/LocalFileSystemSource`; `Filesystem.Local/PosixInterop` (the stat); `Domain/MetadataRules`; `Repository/SnapshotPublication`; `Cli/CliApplication`:

- `Filesystem.Tests/ReparsePointTests`, on every platform:
  - a mount point naming a folder by its drive's path is a mount point;
  - one naming a volume, nothing, a relative path, a share, or a drive with no root keeps no form;
  - so do a symbolic link, a tag not read, and a filter's tag.
- `Domain.Tests/LinkFormTests`:
  - the diagnostic names a mount point and nothing for a symbolic link;
  - from format 6 it moves into key 14 and the other diagnostics stay, and below 6 it stays named;
  - a link named no form, or one no build knows, is a symbolic link.
- `Repository.Tests/LinkFormCaptureTests`:
  - a link captured as a mount point is kept in key 14 at format 6 and named nowhere, and is named and not kept at format 5;
  - a symbolic link beside it keeps no form at either, and an entry of another kind named one is given none;
  - on Windows, a real junction is kept as a mount point naming its folder at format 6, and named one at format 5.
- `Filesystem.Tests/LocalScanTests`: on Windows a junction is named a mount point naming its folder by its drive's path, and a folder's symbolic link is not.
- `Cli.Tests/InspectionCommandTests`: on Windows `inspect-manifest` shows the junction's form, and no other manifest shows one.
- `TestSupport/Junctions` makes a junction for those tests.

## Amendment 2 (2026-11) — a restore on Windows makes a junction again

[Amendment 1](#amendment-1-2026-11--capture-keeps-a-junctions-form) had capture keep a junction's form: from format 6 in manifest key 14, and below it among its diagnostics. No restore read it, so a junction came back as a folder's symbolic link where the account may make one ([ADR-0181](0181-a-restore-on-windows-makes-a-symlink-where-the-account-may.md)), and on Linux or macOS as a symbolic link to a Windows path, with nothing said of it.

The two are not alike. A junction is resolved on the machine that holds it, even when its folder is reached across the network, where a folder's symbolic link is resolved by whoever follows it. A program that tells them apart, as a backup tool or an installer may, sees a different thing.

**Decision.** The engine's restore makes a junction a junction where the target makes one, and says where it does not.

- **A junction is a link captured as a mount point that names a folder by its drive's path** (`Domain/MountPoints`).
  - The form is read where either kind of manifest keeps it (`MetadataRules.LinkFormOf`): key 14 from format 6, or the capture diagnostic before it.
  - A mount point whose target names no drive's folder is made what that target can be, a symbolic link, and nothing names it a junction. Capture never names one so.
- **Windows makes one** as a folder carrying a mount-point reparse point, set through `FSCTL_SET_REPARSE_POINT` on a handle opened on the folder itself.
  - The reparse point names the folder twice: under `\??\`, as the kernel resolves it, and as it is shown.
  - A folder that is not there is named still, as a link to nothing is.
  - Where Windows refuses one, nothing is left at its name. That item fails alone, in the refusal's words, as a link that cannot be made does.
- **Its own metadata goes on as a link's does,** onto the junction and never through it ([ADR-0181 Amendments 2 and 3](0181-a-restore-on-windows-makes-a-symlink-where-the-account-may.md#amendment-2-2026-10--a-links-own-times-on-windows); [ADR-0182 Amendment 1](0182-capture-reads-a-links-own-security-descriptor-on-windows.md#amendment-1-2026-10--a-restore-writes-a-links-own-descriptor-back)). The existing-destination policy holds as for any link.
- **A target that makes none makes a symbolic link to the same path,** as before, and now says so.
  - The plan counts the links captured as junctions on a line of their own, `junctions`.
  - The receipt says of each, `made as a symbolic link: this target makes no junction`.
- **The profile carries the rule:** `RestoreTargetProfile.MakesJunctions`, set on Windows. It also hands the executor an `IMountPoints` seam, as flags and compression have, so the executor's cases run on every platform with a recorder in its place.

**Not done.**

- **The recovery tool.** It still makes a junction a folder's symbolic link. That is the next amendment's.
- **An account that may make no symbolic link** skips a junction still, as it skips every link, though a junction needs no privilege. The planner judges links from the catalogue, which holds no form, so its line saying links will be skipped cannot tell a junction apart. Telling it is a change of its own.
- **A volume mounted on a folder** keeps no form ([Amendment 1](#amendment-1-2026-11--capture-keeps-a-junctions-form)), so it comes back as a symbolic link.

**Built:** `Domain/MountPoints`; `Domain/MetadataRules`; `Restore/RestorePlan` (the profile); `Restore/RestoreExecutor`; `Restore/RestoreMetadata` and `Restore/RestoreBlobSet` (the plan's facts and line):

- `Domain.Tests/MountPointsTests`:
  - a folder on a drive is named by its drive's path, and a volume, nothing, a relative path, a share, a drive with no root and a POSIX path are not;
  - the form is what key 14 keeps or the diagnostics name, and a junction is a mount point naming a drive's folder however its form was kept;
  - only Windows makes one, and off Windows the call says so and makes nothing;
  - on Windows:
    - a junction is made naming its folder, a mount point and no symbolic link, and what the folder holds is seen through it;
    - one is made to a folder not there;
    - none is made where something stands, which is left;
    - a target too long for a reparse point is refused, and nothing is left at its name.
- `Repository.Tests/RestoreJunctionTests`:
  - a junction is made by the junction maker where the target makes them, named in key 14 or, below format 6, in its diagnostics, and a symbolic link beside it is made a symbolic link; the plan says nothing of junctions;
  - one the maker refuses fails alone in the maker's words;
  - a target that makes none makes it a symbolic link to the same path, and the plan's line and the receipt say so;
  - a mount point naming no drive's folder is made a symbolic link, and nothing names it a junction;
  - on Windows, a junction is restored a junction naming its folder, and a folder's symbolic link a symbolic link.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. On Windows a reparse point is captured as a link only where its tag is a symbolic link's or a mount point's; any other is the file or folder it is, its content read through the filter; one whose tag cannot be read is a link, never followed. `Filesystem.Local/ReparsePoints`; `Filesystem.Local/WindowsInterop`; `Filesystem.Local/LocalFileSystemSource`; `Filesystem.Tests/ReparsePointTests`; `Filesystem.Tests/LocalScanTests` |
| 2026-11 | Accepted (related) | [ADR-0184](0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md) gives a link's form a key, format 6's manifest key 14, whose one value names a mount point. Nothing writes it yet, so a junction is still captured as a link with no form |
| 2026-11 | Accepted (Amendment 1) | Capture names a junction a mount point: a link whose tag is a mount point's and whose target names a folder by its drive's path. From format 6 publication keeps the form in manifest key 14 and names it no more; below 6 it stays named among the diagnostics. A symbolic link, and a volume mounted on a folder, keep no form; inspect-manifest shows it; no restore reads it yet. `Filesystem.Local/ReparsePoints`; `Filesystem.Local/LocalFileSystemSource`; `Filesystem.Local/PosixInterop`; `Domain/MetadataRules`; `Repository/SnapshotPublication`; `Cli/CliApplication`; `Filesystem.Tests/ReparsePointTests`; `Filesystem.Tests/LocalScanTests`; `Domain.Tests/LinkFormTests`; `Repository.Tests/LinkFormCaptureTests`; `Cli.Tests/InspectionCommandTests` |
| 2026-11 | Accepted (Amendment 2) | The engine's restore makes a junction a junction where the target makes one: a link captured as a mount point, in key 14 or its diagnostics, that names a drive's folder. Windows makes it through a mount-point reparse point set on the folder; one refused fails alone, in the refusal's words, and leaves nothing. A target that makes none makes a symbolic link to the same path, and its plan and receipt say so. `Domain/MountPoints`; `Domain/MetadataRules`; `Restore/RestorePlan`; `Restore/RestoreExecutor`; `Restore/RestoreMetadata`; `Restore/RestoreBlobSet`; `Domain.Tests/MountPointsTests`; `Repository.Tests/RestoreJunctionTests` |

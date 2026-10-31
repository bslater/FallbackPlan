# ADR-0181 — A restore on Windows makes a symlink where the account may

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a symlink comes back as a link on Windows where the account may make one); FR-RST-003 (amended: the plan says links will be skipped only where they will be); FR-RST-005 (amended by [Amendment 1](#amendment-1-2026-10--the-recovery-tool-makes-a-link-on-windows-too): so does the recovery tool); FR-RST-004 (amended again by [Amendment 2](#amendment-2-2026-10--a-links-own-times-on-windows): a link made on Windows gets its own times back, and by [Amendment 3](#amendment-3-2026-10--a-links-own-attribute-bits-on-windows) its own attribute bits)
**Related:** [ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md) (a link's own metadata, which Windows did not arise for), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) §4 and [ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md) (every path a restore writes is placed through no link out of it), [ADR-0155](0155-the-recovery-tool-makes-a-link-a-link-and-fails-only-the-entry-the-output-refuses.md) (the recovery tool makes links where they need no privilege), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (symbolic links: "preserve, privilege permitting" on Windows)

**Built:**
- The rule: `Domain/SymbolicLinks` says whether this process may make a link, writes a target with Windows' separators, and says whether a link is a folder's.
- The restore: `Restore/RestorePlan` takes the local profile's links from that answer, and `Restore/RestoreExecutor` makes each link a folder's or a file's.
- The tests:
  - `Domain.Tests/SymbolicLinksTests` — on every platform: a target is written with Windows' separators; a link Windows captured is a folder's by its bits whatever it names, and one captured elsewhere by what it names. Off Windows a link can always be made, and on Windows the answer is what making one answers.
  - `Repository.Tests/RestoreWindowsLinkTests` — the local profile makes links where the account may. On Windows a link captured elsewhere is made a link that resolves, with Windows' separators, and the plan no longer says links will be skipped; a link to a folder the restore makes is a folder's link, though the folder is made after it, and a link Windows captured is a folder's or a file's as its bits say.

---

## Context

Architecture 06's matrix has symbolic links preserved on Windows, privilege permitting. The restore's local profile on Windows said it could make no link, so the plan declared every link skipped and the executor skipped each, whatever the account could do. An account that holds the privilege to make links, or any account on a machine in developer mode, may make one, and an administrator usually holds the privilege.

Windows differs from Linux and macOS in two ways a restore meets:

- **A link is a folder's or a file's.** Windows makes a link to a folder apart from a link to a file, and a file's link to a folder does not open as a folder. Linux and macOS make one kind.
- **A link resolves only Windows' separators.** A link captured on Linux or macOS names its target with forward slashes, and Windows does not resolve a relative link written with them.

A link captured on Windows carries its attribute bits (specification 06 §4.1 key 10), which say whether it was made as a folder's. One captured elsewhere carries none.

## Decision

### 1. A link is made where the account may make one

Whether this process may make a link is asked once, by making one in the temporary folder. Linux and macOS always may. The local profile says it supports links where the answer is yes. The plan then declares links skipped only where they will be, and the executor makes each as it does on Linux and macOS, under the same existing-file policy.

### 2. A folder's link or a file's

On Windows a link is made as a folder's:

- where Windows captured it, as its captured attribute bits say, since it was one or the other whatever it names now;
- otherwise where what it names is a folder: one the plan makes, though it may not be made yet, or one that stands.

Anything else is made as a file's link.

### 3. Windows' separators

A target is written with each forward slash made Windows' separator. No Windows name may hold a forward slash, so each was a separator.

### 4. What a link is given

Nothing more than before. No call on Windows sets a link's own times, and its attribute bits and descriptor are written only onto files and folders, so a link's captured metadata is declared and listed as not applied, as ADR-0101 already said of a link it could not write onto.

> **2026-10: amended by [Amendment 2](#amendment-2-2026-10--a-links-own-times-on-windows).** A link's own modification, access and creation times are now written, through a handle opened on the link itself. Its attribute bits and descriptor still are not, and are declared and listed as before.

> **2026-10: amended by [Amendment 3](#amendment-3-2026-10--a-links-own-attribute-bits-on-windows).** A link's attribute bits are now written too, through the same handle. Its descriptor still is not.

## Consequences

- A tree with links restores on Windows as it does on Linux and macOS where the account may make links, and as before where it may not.
- A link made before what it names still points at it. A link may still point anywhere; every path the restore writes is still placed through no link out of it (ADR-0086 §4, ADR-0152), so a link carries no write out of the restore.
- A volume that keeps no links, as FAT does, refuses each at the write, and the item fails and says why.

## What this does not do

- **The recovery tool.** It still makes no link on Windows (ADR-0155). That is the next record's.

  > **2026-10: taken up by [Amendment 1](#amendment-1-2026-10--the-recovery-tool-makes-a-link-on-windows-too).**
- **A link's own times on Windows.** They would need a call opened on the link itself, which nothing here makes.

  > **2026-10: taken up by [Amendment 2](#amendment-2-2026-10--a-links-own-times-on-windows).**
- **A junction.** A directory junction captured on Windows comes back as a folder's symbolic link to the same target, not as a junction.

## Alternatives considered

- **Probe each destination's volume.** Rejected for now. The account decides whether a link may be made, and a volume that keeps none refuses at the write, which the item says.
- **Leave a POSIX target's separators as they are.** Rejected. The link would be made and resolve to nothing.
- **Decide a folder's link by what stands at the write alone.** Rejected. Links sort before what they name as often as after, so a link made before its folder would be a file's.

## Amendment 1 (2026-10) — the recovery tool makes a link on Windows too

The record left the recovery tool as ADR-0155 had it: it made a link wherever links need no privilege, every platform but Windows, and skipped one there with a note. A tree recovered on Windows by an account that may make links lost every link the engine's restore of it would have made.

**Decision.** The tool makes a link where the account may, by the record's rule:

- **where the account may.** It asks `SymbolicLinks.CanCreate`, as the local profile does, and skips a link with its note, as before, where the answer is no;
- **a folder's link or a file's,** as the record says, by the link's captured bits where Windows captured it, and otherwise by what it names: a folder of the same tree, which it may make after the link, or one that stands. The tool walks one tree at a time, so a folder elsewhere in the snapshot not yet made is not known, and a link naming one is a file's;
- **with Windows' separators.**

What a link names is its target from the folder it is in (`SymbolicLinks.PathNamed`), which the executor now judges by too.

**Built:** `Domain/SymbolicLinks`; `Recovery/RecoverySession`; `Restore/RestoreExecutor`:

- `Domain.Tests/SymbolicLinksTests`: what a link names is its target from the link's folder, or where a target from the root says;
- `Repository.Tests/RecoveryLinkTests`: on Windows a link is made where the account may and skipped with its note where it may not; a link naming a folder of its tree is a folder's link that resolves, one naming a file has Windows' separators and resolves, and one Windows captured as a folder's is made one.

## Amendment 2 (2026-10) — a link's own times on Windows

The record made a link on Windows and wrote nothing onto it, because no call it made set a link's own times. So every link a restore made there listed its captured times as not applied, and the plan declared them, though Windows keeps a link's own times as it keeps a file's.

**Decision.** A link made on Windows gets its own modification, access and creation times back, onto the link and never through it, by the executor and the recovery tool alike:

- **through a handle opened on the link itself.** Windows opens a reparse point rather than what it points at when asked to (`FILE_FLAG_OPEN_REPARSE_POINT`), and a folder's link when asked for a folder's semantics. Asked for nothing but the right to write attributes, that handle takes .NET's setters for a handle, which write onto what the handle names. The no-follow is the open's own flag, as it is `utimensat`'s on Linux and macOS, so ADR-0101's reason not to trust a path-taking setter is met;
- **in the writer's order,** the modification and access times, then the creation time, which `SymlinkMetadata` now sets on macOS, as before, and on Windows;
- **as the rule says.** The plan and the receipt hold a link on Windows to the times, and a creation time where the target keeps one. Its attribute bits and descriptor are still written only onto files and folders, so a link Windows captured still lists them.

A write the link refuses is listed, as any refused write is. A link to nothing, or to a folder, takes its times as one to a file does.

**Built:** `Domain/SymlinkMetadata`; `Domain/MetadataWriter`; `Restore/RestoreMetadata`:

- `Domain.Tests/SymlinkMetadataTests`: a link's times are set on Windows, and on Linux and macOS in a 64-bit process; its creation time on macOS and Windows, and on Linux not at all;
- `Domain.Tests/SymlinkMetadataOnWindowsTests`: on Windows a file's link, a folder's link and a link to nothing take their own times, either alone, and their creation time, and what each points at keeps its own;
- `Repository.Tests/RestoreWindowsLinkTests`: on Windows a link to a file, a folder and nothing comes back with all three times, the plan declares nothing of it, its item lists nothing, and the file it names keeps its own;
- `Repository.Tests/RecoveryLinkTests`: on Windows the recovery tool gives a link leading out of its output its own times, and the file outside keeps its own.

> **2026-10: amended by [Amendment 3](#amendment-3-2026-10--a-links-own-attribute-bits-on-windows).** A link's attribute bits now go on through the same handle; its descriptor is still written only onto files and folders.

## Amendment 3 (2026-10) — a link's own attribute bits on Windows

Amendment 2 opened a handle on a link to give it its times and left its attribute bits to files and folders. A link Windows captured hidden or system came back plain, and every plan and receipt over one said so.

**Decision.** A link made on Windows gets its attribute bits back by [ADR-0175](0175-a-restore-writes-back-the-windows-attribute-bits-it-may.md)'s rule, through the same handle:

- **the handle reads and writes the link's attributes.** It is opened to read them too, and the link's own bits are read through it, never what it points at;
- **ADR-0175's five bits** (read-only, hidden, system, archive and not-content-indexed) are set as captured and cleared where not, last, as a file's are;
- **the bits that make it a link stay as making it left them.** Windows sets the reparse-point bit, and a folder's link's folder bit, when it makes the link, and takes neither as a written bit, so neither is written;
- **a bit no restore sets is named,** as a file's is. Nothing compresses a link, so a link's compressed bit is named among them whatever the target keeps, by the plan and the writer alike.

A link's security descriptor is still written only onto files and folders. The recovery tool writes through the same writer, so a link it makes gets its bits too.

**Built:** `Domain/SymlinkMetadata`; `Domain/MetadataWriter`; `Domain/MetadataRules`; `Restore/RestoreMetadata`:

- `Domain.Tests/SymlinkMetadataTests`: only Windows sets a link's bits; off Windows nothing is written;
- `Domain.Tests/SymlinkMetadataOnWindowsTests`: on Windows a file's link, a folder's link and a link to nothing take the bits captured and lose those not, each is still a link and a folder's link a folder's, and what each points at keeps its own;
- `Repository.Tests/RestoreWindowsLinkTests`: on Windows a hidden link and a hidden folder's link come back so with nothing listed, an encrypted bit on a link is named in its item and on the plan's line, and the file and folder they name keep their own; on every platform the rule grants a link's bits exactly where the platform sets them;
- `Repository.Tests/RecoveryLinkTests`: on Windows the recovery tool gives a link its bits, and the file it names keeps its own.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A restore on Windows makes a link where the account may, a folder's or a file's as its captured bits or what it names say, with Windows' separators; the plan says links will be skipped only where they will be. `Domain/SymbolicLinks`; `Restore/RestorePlan`; `Restore/RestoreExecutor`; `Domain.Tests/SymbolicLinksTests`; `Repository.Tests/RestoreWindowsLinkTests` |
| 2026-10 | Accepted (Amendment 1) | The recovery tool makes a link on Windows where the account may, a folder's or a file's by its captured bits or by what it names in its tree or on disk, with Windows' separators. `Domain/SymbolicLinks`; `Recovery/RecoverySession`; `Restore/RestoreExecutor`; `Domain.Tests/SymbolicLinksTests`; `Repository.Tests/RecoveryLinkTests` |
| 2026-10 | Accepted (Amendment 2) | A link made on Windows gets its own modification, access and creation times back through a handle opened on the link itself, by the executor and the recovery tool alike; its attribute bits and descriptor are still not written. `Domain/SymlinkMetadata`; `Domain/MetadataWriter`; `Restore/RestoreMetadata`; `Domain.Tests/SymlinkMetadataTests`; `Domain.Tests/SymlinkMetadataOnWindowsTests`; `Repository.Tests/RestoreWindowsLinkTests`; `Repository.Tests/RecoveryLinkTests` |
| 2026-10 | Accepted (Amendment 3) | A link made on Windows gets its own attribute bits back by ADR-0175's rule, through the same handle on the link; the bits that make it a link stay as making it left them, and a bit no restore sets is named. `Domain/SymlinkMetadata`; `Domain/MetadataWriter`; `Domain/MetadataRules`; `Restore/RestoreMetadata`; `Domain.Tests/SymlinkMetadataTests`; `Domain.Tests/SymlinkMetadataOnWindowsTests`; `Repository.Tests/RestoreWindowsLinkTests`; `Repository.Tests/RecoveryLinkTests` |

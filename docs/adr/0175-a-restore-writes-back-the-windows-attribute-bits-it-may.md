# ADR-0175 — A restore writes back the Windows attribute bits it may

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a file's and a folder's Windows attribute bits come back where the target keeps them, and a bit no restore sets is named), FR-RST-003
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (what a restore says it does not write back), [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) (each attribute on its own, and a receipt that records what each write did), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) (a folder's metadata, which goes on last), [ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md) and [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) (the one writer the executor, the command line and the recovery tool share), [ADR-0174](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md) (a file's streams, which go on before), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (compression and encryption attributes are degraded and reported), [specification 06 §4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map) (key 10, the bits as captured)

**Built:**
- The rule: `Domain/MetadataRules` names the bits a restore sets as captured and clears where not, the bits that say what an entry is, and each other bit captured, which no restore sets, and works out the value to set from the bits an entry stands with and those captured.
- The write: `Domain/MetadataWriter` writes them as the last write for a file or a folder, on Windows alone; `Domain/MetadataTarget` and `Restore/RestorePlan` say a local target keeps them on Windows, and `Restore/RestoreMetadata` counts them applied there. `Domain/RestorePaths` moves a verified file over a read-only one it replaces.
- The tests:
  - `Domain.Tests/FileAttributesWriteBackTests` — the local target writes bits only on Windows; bits a restore sets or an entry is leave nothing unset, and every other bit is named, an undefined one by its value; the value set keeps each standing bit a restore does not write, and is the plain-file bit alone where none is left; on Windows a file's bits are set and cleared as captured with its time kept, a file captured with none comes back with none, a folder's are written, and a compressed bit leaves the attributes not applied and is named while the rest land; on POSIX a target said to keep bits writes none; a verified file replaces one there, and on Windows a read-only one
  - `Repository.Tests/RestoreFileAttributesTests` — the profile writes bits only on Windows, passes them to the writer, and the rule counts them applied for a file and a folder where it does; on Windows a real file and its folder captured read-only and hidden come back so through the executor, with the file's time, nothing listed, and a second restore with Replace over the read-only file lands
  - `Repository.Tests/RestoreMetadataHonestyTests`, `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` — the cases that listed the bits as not applied on Windows now say none is, and the one that holds one list on every platform says its target writes none

---

## Context

Capture records each entry's Windows attribute bits as the platform gives them (specification 06 §4.1 key 10). No restore wrote any back. The executor, the recovery tool and `restore-file` all listed `file_attributes` as not applied for every item on Windows, and the plan declared that a target would not apply them. So a read-only or hidden file came back writable and visible, a system file came back as an ordinary one, and every restore on Windows listed the bits.

The bits are not all of one kind. Some say how a file is kept and can simply be set: read-only, hidden, system, archive and not-content-indexed. Some say what an entry is: a directory, a plain file, a sparse file, a reparse point. A restore gives an entry those by what it makes, or does not make a link at all on Windows. The rest, as compressed and encrypted, are kept by the volume through calls of their own, and architecture 06 §3 has them degraded and reported.

## Decision

### 1. Five bits are written back as captured

The writer sets read-only, hidden, system, archive and not-content-indexed as captured, and clears each that was not, keeping every other bit the entry has. Windows takes the plain-file bit only alone, and alone it clears the rest, so an entry left with no bit is given that one. It does so for a file and for a folder, by the writer every way of restoring shares, so the executor, `restore-file` and the recovery tool all write them.

### 2. A bit no restore sets is named, and the attributes are not applied

A bit that is neither written back nor what an entry is, as compressed, encrypted, temporary or offline, is one no restore sets. The writer names it in the item's detail, and `file_attributes` is listed as not applied, though the five it does set still land. An undefined bit is named by its value.

### 3. The bits go on last, and only on Windows

A read-only file refuses a stream written onto it, so the bits are the last write for an entry, after its streams, times and creation time. Off Windows .NET maps the same calls onto permissions and file flags of its own, so the writer writes none there whatever a target says, and a target keeps them only on Windows.

### 4. A file a restore replaces is replaced though it is read-only

Windows refuses to move a file over a read-only one. A file an earlier restore gave its captured bits may be one, so a later restore with Replace, `restore-file` over its own earlier output, and the recovery tool over an output it wrote before would each fail at it. The three move their verified spool through one call, which clears the read-only bit of the file it replaces first. The file that lands takes its own bits back with the rest of its metadata.

## Consequences

- A file restored on Windows comes back read-only, hidden or system as it was captured, and a folder hidden as it was.
- A restore on Windows lists the bits only where a compressed or encrypted file, or another bit no restore sets, was captured.
- A plan on Windows declares no file-attributes line. The plan does not yet say beforehand which files carry a bit no restore sets.
- A restored read-only file is read-only to the person too, as it was before the backup.

## What this does not do

- **Compress or encrypt a file as captured.** NTFS compression and EFS encryption are set through calls of their own, and EFS needs the key of the account that encrypted. They are named, not set.
- **Predict a bit no restore sets in the plan.** The plan's facts carry which attributes an item has, not which bits, so a compressed file's line is the receipt's.
- **A link's bits.** No restore on Windows makes a symlink.

## Alternatives considered

- **Write every captured bit and let the platform refuse.** Rejected. `SetFileAttributes` ignores compressed, encrypted and the bits that say what an entry is, so the call would succeed and the receipt would say they landed.
- **Set only the bits captured, leaving the rest as they are.** Rejected. A file captured without the archive bit would keep the one the restore's own write gave it, and the attributes would read applied while differing.
- **Clear the read-only bit only under Replace.** Rejected. `restore-file` and the recovery tool replace what stands at their output whole too, and each would fail at a read-only file it wrote before.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The writer every way of restoring shares writes a file's and a folder's read-only, hidden, system, archive and not-content-indexed bits back on Windows, as the last write; a bit no restore sets is named and leaves the attributes not applied; and a read-only file a restore replaces is replaced. `Domain/MetadataWriter`; `Domain/MetadataRules`; `Domain/RestorePaths`; `Domain.Tests/FileAttributesWriteBackTests`; `Repository.Tests/RestoreFileAttributesTests` |

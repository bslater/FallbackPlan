# ADR-0175 — A restore writes back the Windows attribute bits it may

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a file's and a folder's Windows attribute bits come back where the target keeps them, and a bit no restore sets is named; amended by [Amendment 2](#amendment-2-2026-10--a-restore-gives-a-file-and-a-folder-their-compression-back): a file's and a folder's compression comes back where the target keeps it), FR-RST-003 (amended by [Amendment 1](#amendment-1-2026-10--the-plan-names-a-bit-no-restore-sets): the plan names a bit no restore sets; amended by [Amendment 2](#amendment-2-2026-10--a-restore-gives-a-file-and-a-folder-their-compression-back): where the target keeps compression, the plan does not name the compressed bit)
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (what a restore says it does not write back), [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) (each attribute on its own, and a receipt that records what each write did), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) (a folder's metadata, which goes on last), [ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md) and [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) (the one writer the executor, the command line and the recovery tool share), [ADR-0174](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md) (a file's streams, which go on before), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (compression and encryption attributes are degraded and reported), [specification 06 §4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map) (key 10, the bits as captured)

**Built:**
- The rule: `Domain/MetadataRules` names the bits a restore sets as captured and clears where not, the bits that say what an entry is, and each other bit captured, which no restore sets, and works out the value to set from the bits an entry stands with and those captured.
- The write: `Domain/MetadataWriter` writes them as the last write for a file or a folder, on Windows alone; `Domain/MetadataTarget` and `Restore/RestorePlan` say a local target keeps them on Windows, and `Restore/RestoreMetadata` counts them applied there. `Domain/RestorePaths` moves a verified file over a read-only one it replaces.
- Compression ([Amendment 2](#amendment-2-2026-10--a-restore-gives-a-file-and-a-folder-their-compression-back)): `Domain/FileCompression` sets a file's and a folder's NTFS compression through the platform's call, and `Domain/MetadataWriter` gives each entry captured with bits its compression as captured, before the times, through the calls `Domain/MetadataTarget` names; `Domain/MetadataRules` no longer counts the compressed bit among those no restore sets where the target compresses, for the writer and the plan alike.
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

> **2026-10 ([Amendment 2](#amendment-2-2026-10--a-restore-gives-a-file-and-a-folder-their-compression-back)).** Compressed is no longer one: where the target keeps compression, the writer gives an entry the compression it was captured with by a call of its own. Encrypted, temporary and offline still are.

### 3. The bits go on last, and only on Windows

A read-only file refuses a stream written onto it, so the bits are the last write for an entry, after its streams, times and creation time. Off Windows .NET maps the same calls onto permissions and file flags of its own, so the writer writes none there whatever a target says, and a target keeps them only on Windows.

> **2026-10 ([ADR-0176](0176-a-restore-writes-back-a-windows-security-descriptor-whose-accounts-the-machine-knows.md)).** The security descriptor now goes on just before the bits, after the streams and times, since its DACL may take from the restoring account the access those need. The bits stay the last write: writing a descriptor sets the archive bit.

### 4. A file a restore replaces is replaced though it is read-only

Windows refuses to move a file over a read-only one. A file an earlier restore gave its captured bits may be one, so a later restore with Replace, `restore-file` over its own earlier output, and the recovery tool over an output it wrote before would each fail at it. The three move their verified spool through one call, which clears the read-only bit of the file it replaces first. The file that lands takes its own bits back with the rest of its metadata.

## Consequences

- A file restored on Windows comes back read-only, hidden or system as it was captured, and a folder hidden as it was.
- A restore on Windows lists the bits only where a compressed or encrypted file, or another bit no restore sets, was captured.

  > **2026-10 ([Amendment 2](#amendment-2-2026-10--a-restore-gives-a-file-and-a-folder-their-compression-back)).** A compressed file no longer lists them where the target keeps compression; one whose volume refuses it does, with the reason.

- A plan on Windows declares no file-attributes line. The plan does not yet say beforehand which files carry a bit no restore sets.

  > **Built 2026-10 ([Amendment 1](#amendment-1-2026-10--the-plan-names-a-bit-no-restore-sets)).** It does now, on a line of its own that names the bits.
- A restored read-only file is read-only to the person too, as it was before the backup.

## What this does not do

- **Compress or encrypt a file as captured.** NTFS compression and EFS encryption are set through calls of their own, and EFS needs the key of the account that encrypted. They are named, not set.

  > **Compression built 2026-10 ([Amendment 2](#amendment-2-2026-10--a-restore-gives-a-file-and-a-folder-their-compression-back)).** A file and a folder get their compression back through its own call. Encryption is still named, not set.

- **Predict a bit no restore sets in the plan.** The plan's facts carry which attributes an item has, not which bits, so a compressed file's line is the receipt's.

  > **Built 2026-10 ([Amendment 1](#amendment-1-2026-10--the-plan-names-a-bit-no-restore-sets)).** The facts now carry the bits, and the plan names those no restore sets.
- **A link's bits.** No restore on Windows makes a symlink.

## Alternatives considered

- **Write every captured bit and let the platform refuse.** Rejected. `SetFileAttributes` ignores compressed, encrypted and the bits that say what an entry is, so the call would succeed and the receipt would say they landed.
- **Set only the bits captured, leaving the rest as they are.** Rejected. A file captured without the archive bit would keep the one the restore's own write gave it, and the attributes would read applied while differing.
- **Clear the read-only bit only under Replace.** Rejected. `restore-file` and the recovery tool replace what stands at their output whole too, and each would fail at a read-only file it wrote before.

## Amendment 1 (2026-10) — the plan names a bit no restore sets

**Gap.** A plan on Windows declared no file-attributes line, so a file captured compressed or encrypted was said only by the receipt: its attributes were listed not applied, with the bits named in its detail, after the run. ADR-0083 holds the plan and the receipt to one rule, and for these files they disagreed. The plan's facts carried which kinds of metadata an item had, not its bits, so the plan could not tell.

**Decision.** Each item's facts now carry its captured attribute bits (`Restore/RestoreBlobSet`), as they carry its descriptor's accounts (ADR-0176). On a target that writes bits, the rule judges them by `MetadataRules.UnsetFileAttributes`, as the writer does. An item captured with a bit no restore sets is declared on a line of its own, `unset-file-attributes`, which counts the files and folders and names every such bit the tree carries, in the bits' order. That line says the five bits a restore sets still land. Such an item is not also counted on the file-attributes line. Where the target writes no bits, every item that carries some is declared on that line, as before, and no bit is singled out.

**Tests.** `Repository.Tests/RestoreMetadataHonestyTests` holds the plan to it on every platform, against manifests captured with archive, archive and compressed, and encrypted bits. On a target that writes bits, the plan declares two files on the new line, naming Compressed and Encrypted, and no file-attributes line. On one that writes none, it declares all three on the file-attributes line and nothing on the new one.

## Amendment 2 (2026-10) — a restore gives a file and a folder their compression back

**Gap.** §2 named the compressed bit as one no restore sets, because `SetFileAttributes` ignores it. So every file a person had compressed on Windows came back uncompressed, and the plan and the receipt listed its attribute bits as not applied. NTFS sets compression through `FSCTL_SET_COMPRESSION`, on a handle open to read and write the file's data, or a folder's with backup semantics. That is the call of its own this record said compression needs.

**Decision.**
- **The call.** `Domain/FileCompression` makes it, behind calls a case can stand in for, as `Domain/AlternateStreams` does. It acts only where an entry stands otherwise than asked. The target says whether it keeps compression: a local one does on Windows alone.
- **What is set.** The writer every way of restoring shares gives each file and folder captured with bits its compression as captured. It compresses where the entry was compressed, and expands where it was not, since a file made in a compressed folder starts compressed. A folder's compression is what each file made in it later starts with, and changes none it already holds. A link is given none.
- **The order.** It goes on before the times, since compressing or expanding rewrites a file's clusters. It also comes before the descriptor and the bits, because a DACL may take the access the call needs, and a read-only file refuses it.
- **A refusal.** One the target refuses, as a FAT or ReFS volume does, is named with its reason, and leaves the attribute bits not applied.
- **The rule.** `MetadataRules.UnsetFileAttributes` takes whether the target compresses. On such a target the compressed bit is not one no restore sets, in the writer or in the plan, whose `unset-file-attributes` line then also says compression will land.

Encryption is still named and not set, for the reason this record gives.

**What it does not do.** The plan does not ask whether the volume keeps compression. A target that is said to compress but whose volume refuses it is found at the write, and named in the receipt.

**Tests.**
- `Domain.Tests/FileCompressionTests`, against calls a case holds, on every platform:
  - a file is asked compressed or not as captured, before its times;
  - a folder is asked as captured;
  - a refusal is named and leaves the bits not applied;
  - an entry captured with no bits, a link and a target that keeps no compression ask for none;
  - the rule names encryption alone where the target compresses.
- `Domain.Tests/FileCompressionTests` on Windows: a real file comes back compressed with its time and bits, a folder comes back compressed, a file captured uncompressed in a compressed folder comes back uncompressed, and a compression refused leaves the bits not applied while the bits a restore sets still land.
- `Repository.Tests/RestoreFileAttributesTests` restores a compressed folder, its compressed file and a loose uncompressed file through the executor on Windows, and holds the profile's mapping everywhere.
- `Repository.Tests/RestoreMetadataHonestyTests` holds the plan naming only encryption.
- `Domain.Tests/FileAttributesWriteBackTests`' Windows case of a bit no restore sets now uses encryption.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The writer every way of restoring shares writes a file's and a folder's read-only, hidden, system, archive and not-content-indexed bits back on Windows, as the last write; a bit no restore sets is named and leaves the attributes not applied; and a read-only file a restore replaces is replaced. `Domain/MetadataWriter`; `Domain/MetadataRules`; `Domain/RestorePaths`; `Domain.Tests/FileAttributesWriteBackTests`; `Repository.Tests/RestoreFileAttributesTests` |
| 2026-10 | Accepted (related) | [ADR-0176](0176-a-restore-writes-back-a-windows-security-descriptor-whose-accounts-the-machine-knows.md): the security descriptor now goes on just before the bits, which stay the last write. |
| 2026-10 | Amended | [Amendment 1](#amendment-1-2026-10--the-plan-names-a-bit-no-restore-sets): the plan names a bit no restore sets, on a line of its own, from the bits each item's facts now carry. `Restore/RestoreMetadata`; `Restore/RestoreBlobSet`; `Repository.Tests/RestoreMetadataHonestyTests` |
| 2026-10 | Amended | [Amendment 2](#amendment-2-2026-10--a-restore-gives-a-file-and-a-folder-their-compression-back): a file and a folder get their NTFS compression back through its own call, before the times, as captured; a refusal is named; encryption is still named and not set. `Domain/FileCompression`; `Domain/MetadataWriter`; `Domain/MetadataRules`; `Domain/MetadataTarget`; `Restore/RestoreMetadata`; `Restore/RestorePlan`; `Domain.Tests/FileCompressionTests`; `Repository.Tests/RestoreFileAttributesTests`; `Repository.Tests/RestoreMetadataHonestyTests` |

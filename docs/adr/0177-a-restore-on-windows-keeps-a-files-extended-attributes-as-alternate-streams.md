# ADR-0177 — A restore on Windows keeps a file's extended attributes as alternate streams

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: on a target that keeps alternate streams and writes no extended attributes of its own, a file's extended attributes come back as streams of the same names, and each that does not is named), FR-RST-003 (amended: the plan names the files whose attributes only Linux's kernel reads); amended by [Amendment 2](#amendment-2-2026-10--a-folders-attributes-are-kept-as-its-streams): a folder's attributes are kept as its streams by the same rule
**Related:** [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) (the attributes a restore writes back where the target writes them, which named Windows as not done), [ADR-0174](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md) (a file's own streams, the rule a stream's name is held to, and the order: after the content, before the times), [ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md) and [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) (the one writer the executor, the command line and the recovery tool share), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (extended attributes on Windows are preserved as alternate streams)

**Built:**
- The rule: `Domain/MetadataRules` says when a target keeps an entry's attributes as streams, and which attribute it does not keep as one. `Restore/RestoreMetadata` counts a file's attributes applied on such a target, and declares on a line of its own the files whose attributes only Linux's kernel reads.
- The write: `Domain/MetadataWriter` writes each of a file's attributes as the stream of its name, judged by `Domain/AlternateStreams`, through the calls `Domain/MetadataTarget` names. `Domain/MetadataTarget` and `Restore/RestorePlan` say a local target keeps streams on Windows, and `Restore/RestoreExecutor` hands the writer the calls it was given.
- The plan's names ([Amendment 1](#amendment-1-2026-10--the-plan-names-a-file-whose-attribute-no-stream-is-kept-under)): `Domain/MetadataRules` judges the stream each attribute is kept as, or why not, for the writer and the plan alike, and `Restore/RestoreBlobSet` counts the attributes it refuses for their names into each item's facts.
- A folder's ([Amendment 2](#amendment-2-2026-10--a-folders-attributes-are-kept-as-its-streams)): `Domain/MetadataRules` keeps a folder's attributes as streams by the rule a file's are, so `Domain/MetadataWriter` writes them onto the folder it makes and `Restore/RestoreMetadata` counts them applied; `Restore/RestoreBlobSet` counts a folder's refused names too. `Domain.Tests/ExtendedAttributeStreamsTests`, `Repository.Tests/RestoreAlternateStreamsTests` and `Repository.Tests/RestoreMetadataHonestyTests` hold it, a real folder's on Windows among them.
- The tests:
  - `Domain.Tests/ExtendedAttributeStreamsTests` — on every platform, against calls that hold streams in memory, a file's attributes land as streams of their names and count applied; one in the security or trusted namespace, or a POSIX ACL, is not kept and is named while the rest land; a name no stream may have, one the file's own captured stream has in any case, the second of two that differ only in case and a stream the target refuses are each named; a folder's are not kept as streams; on Linux and macOS a target that writes attributes of its own writes none as streams; on Windows a real file gets its attribute as a stream, with its time kept
  - `Repository.Tests/RestoreAlternateStreamsTests` — the profile passes whether it keeps streams to the writer, and the rule counts a file's attributes applied on a target that keeps them as streams and not a folder's; through the executor, a file's attribute lands as its stream and nothing is declared or listed
  - `Repository.Tests/RestoreMetadataHonestyTests` — on such a target the plan declares, on a line of its own, the files whose attributes only Linux's kernel reads, two files of three, and none of the lines a Linux target's attributes are declared on; on a target that keeps no streams either, every one is declared as before

---

## Context

Capture records a file's extended attributes on Linux and macOS (specification 06 §4.1 key 8). A restore writes them back where the target writes attributes of its own ([ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md)). Windows writes none. On Windows, every file captured with an attribute came back listing `extended_attributes` as not applied, and the plan declared every such file. A macOS file's Finder information and where it came from, and a Linux file's user tags, were lost there.

Architecture 06 §3 has extended attributes on Windows "preserve as alternate streams". Since [ADR-0174](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md) a restore writes a file's own captured streams back on Windows. It judges each name before joining it to the file's path, and writes them after the content and before the times.

Not every attribute is the file's data. Linux's security namespace holds a file's capabilities and its security label, its trusted namespace holds what only root's tools read, and `system.posix_acl_access` and `system.posix_acl_default` are its access control lists. Linux's kernel reads each of them; on Windows, as bytes beside the file, they would mean nothing and do nothing.

## Decision

### 1. A file's attributes are kept as streams of their names

Where a target keeps a file's alternate streams and writes no extended attributes of its own, as Windows does, the writer writes each of a file's captured attributes as the stream of the same name, holding the attribute's value. It writes them before the times, since a stream write moves a file's modification time, and they land for the executor, `restore-file` and the recovery tool alike, through the writer all three share. The attributes count applied only where every one landed, as ADR-0087 has it.

### 2. What only Linux's kernel reads is not kept

An attribute in Linux's security or trusted namespace, or a POSIX ACL, is not written as a stream, and the item's detail names it. The file's other attributes are still written. A restore to Linux writes these where the account may (ADR-0087); on Windows they are listed, not mistaken for data.

### 3. Each stream's name is judged as a stream's

An attribute's name is judged by the rule a captured stream's is ([ADR-0174 §2](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md)), before it is joined to the file's path. A name no stream may have, as one holding a colon, is not written and is named with the reason. So is a name the file was also captured with a stream of, which is not written over that stream, and the second of two attributes whose names differ only in case. Windows matches a stream's name without regard to case, so either would land on a stream already written, and both would be called applied. A stream the target refuses is named with its reason.

### 4. A folder's attributes are not kept

A restore writes no folder's streams (ADR-0174), so a folder's attributes are listed as not applied as before.

> **Amended 2026-10 ([Amendment 2](#amendment-2-2026-10--a-folders-attributes-are-kept-as-its-streams)).** A folder's attributes are kept as its streams, by the rule a file's are.

### 5. The plan says so per file

On such a target the plan counts a file's attributes applied. A file that carries one only Linux's kernel reads is declared on a line of its own, `kernel-attributes`, rather than on the lines that assume a Linux target writes them. The rule is judged by what the target states, so a plan for a Windows target can be made, and tested, on any host.

## Consequences

- A Linux or macOS file restored on Windows keeps its attributes beside it, readable as `file:name`, and a later backup of that file captures them as its streams.
- A file whose attributes are only its security label or its ACL is declared, and listed, as before.
- A macOS attribute whose name holds a colon is named in the receipt, not written.

## What this does not do

- **Judge an attribute's name in the plan.** The plan's facts carry each item's attribute kinds, not their names, so a name no stream may have is found at the write and named in the receipt, not in the plan.

  > **Built 2026-10 ([Amendment 1](#amendment-1-2026-10--the-plan-names-a-file-whose-attribute-no-stream-is-kept-under)).** The writer's judgement of each name moved to `Domain/MetadataRules`, each item's facts count the attributes it refuses for their names, and the plan names those files.

- **Give the attributes back as attributes.** A stream captured from Windows is a stream: restored to Linux or macOS, it is not turned back into the attribute it came from.

  > **Built 2026-10 ([ADR-0179](0179-a-restore-on-linux-and-macos-keeps-a-files-alternate-streams-as-extended-attributes.md)).** A file's stream is now kept as the attribute of its name on Linux and macOS, so an attribute kept as a stream here comes back as itself. Linux takes only those in its user namespace, which is where every one this record keeps came from.
- **Keep a folder's attributes,** for the reason in §4.

  > **Built 2026-10 ([Amendment 2](#amendment-2-2026-10--a-folders-attributes-are-kept-as-its-streams)).** A folder's attributes are kept as its streams.
- **Keep a symlink's.** No restore on Windows makes a symlink.

## Alternatives considered

- **Keep every attribute as a stream, the kernel's too.** Rejected. A capability or a security label beside a file on Windows does nothing, and kept as a stream it would read as the file's data; on a later restore it would come back as a stream, never as the label.
- **Keep them as NTFS extended attributes.** Rejected. Windows' own extended attributes are written through `NtSetEaFile`, are limited to 64 KiB for a file in all, and are read by no tool a person uses. Streams are what the architecture names, and the restore already writes them.
- **One stream holding every attribute, in a format of our own.** Rejected. It would be readable only by this product, and one attribute that would not write would lose them all.

## Amendment 1 (2026-10) — the plan names a file whose attribute no stream is kept under

**Gap.** §3 judges each attribute's name at the write. A name no stream may have, a name the file's own stream has, and the second of two names that differ only in case are each named in the receipt and not written. The plan's facts carried each item's attribute kinds, not their names, so the plan counted such a file's attributes applied, and the receipt then listed them as not applied.

**Decision.** The judgement of each name moves from the writer into `Domain/MetadataRules`: for each of a file's attributes, in the order captured, the stream it is kept as or why it is not. The writer writes what that says and nothing else, so the plan and the write judge every name by one rule. Each item's facts count the attributes it refuses for their names, leaving out those only Linux's kernel reads, which §5's line already names. The plan's probe counts them from the manifest it already reads (`Restore/RestoreBlobSet`). On a target that keeps attributes as streams, the plan declares the files carrying one on a line of their own, `attribute-stream-names`. That line says the attribute will not be kept as a stream and that the file's other attributes will be. On a target that keeps no streams, the attributes line already speaks for every file, and no name is judged. As with [ADR-0174 Amendment 3](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md#amendment-3-2026-10--the-plan-names-a-file-whose-stream-has-a-name-no-stream-may-be-written-under), the plan gives the count, not the names, which the receipt gives.

**Tests.**
- `Domain.Tests/ExtendedAttributeStreamsTests` holds the judgement of five attributes, one of each outcome, and its count, which leaves out the kernel's.
- `Repository.Tests/RestoreMetadataHonestyTests` holds the plan to it on every platform. Of five files:
  - one keeps every name;
  - one has an attribute named with a colon;
  - one has two names that differ only in case;
  - one has a name its own stream has, but for case;
  - one has only a security label.

  The new line declares three. The kernel's file stays on its own line. A target that keeps no streams declares no such line.

## Amendment 2 (2026-10) — a folder's attributes are kept as its streams

### Context

§4 left a folder's attributes listed as not applied, because a restore writes no folder's own streams: capture records none for a folder (ADR-0174). That says nothing about whether one can be written. NTFS keeps named streams on a folder as it does on a file, opened as `folder:name`, and architecture 06 §3 has extended attributes on Windows preserved as streams, whatever carries them. A macOS folder's Finder information, its colour label among it, and a Linux folder's user tags were still lost there.

### Decision

A folder's attributes are kept as streams of their names by the rule a file's are (§§1–3). They are written onto the folder the restore makes, when its own metadata goes on, after everything in it has landed (ADR-0086), and before its times. One that only Linux's kernel reads is not kept, and is named. So is one whose name no stream may have. A folder has no captured stream for an attribute's name to meet.

A folder already at the destination keeps its own metadata (ADR-0086), and nothing is written onto it. A symlink keeps none, since a stream joined to a link's path would be written through it.

The plan counts a folder's attributes applied on such a target, and declares a folder with one only Linux's kernel reads, or one no stream is kept under, on the lines a file is declared on.

### Consequences

- A macOS or Linux folder restored on Windows keeps its attributes beside it, readable as `folder:name`.
- Each folder's attributes cost a stream write each, on the folder itself.

### What this does not do

- **Capture a folder's streams.** A later backup of the folder on Windows still records none (ADR-0174), so a folder kept this way does not carry its attributes onward from there.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. On a target that keeps a file's alternate streams and writes no attributes of its own, the writer every way of restoring shares keeps a file's extended attributes as streams of their names, before the times; one only Linux's kernel reads, a name no stream may have, one the file's own stream has, one an attribute before it has but for case, and a stream the target refuses are each named; a folder's are not kept; and the plan declares the kernel's on a line of its own. `Domain/MetadataWriter`; `Domain/MetadataRules`; `Domain/MetadataTarget`; `Restore/RestoreMetadata`; `Domain.Tests/ExtendedAttributeStreamsTests`; `Repository.Tests/RestoreAlternateStreamsTests`; `Repository.Tests/RestoreMetadataHonestyTests` |
| 2026-10 | Amended | Amendment 1: the plan names a file whose attribute no stream is kept under, by the writer's own judgement of each name, which moved to one rule the two share. `Domain/MetadataRules`; `Domain/MetadataWriter`; `Restore/RestoreMetadata`; `Restore/RestoreBlobSet`; `Domain.Tests/ExtendedAttributeStreamsTests`; `Repository.Tests/RestoreMetadataHonestyTests` |
| 2026-10 | Amended | [Amendment 2](#amendment-2-2026-10--a-folders-attributes-are-kept-as-its-streams): a folder's attributes are kept as its streams by the rule a file's are, written onto the folder the restore makes before its times; the plan counts them applied, and declares a folder's kernel attributes and refused names on the lines a file's are. `Domain/MetadataRules`; `Domain/MetadataWriter`; `Domain/MetadataTarget`; `Restore/RestoreBlobSet`; `Domain.Tests/ExtendedAttributeStreamsTests`; `Repository.Tests/RestoreAlternateStreamsTests`; `Repository.Tests/RestoreMetadataHonestyTests` |
| 2026-10 | Accepted (related) | [ADR-0179](0179-a-restore-on-linux-and-macos-keeps-a-files-alternate-streams-as-extended-attributes.md): on Linux and macOS a file's streams are kept as attributes of their names, so an attribute this record kept as a stream comes back as itself. |

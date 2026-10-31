# ADR-0179 — A restore on Linux and macOS keeps a file's alternate streams as extended attributes

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: on a target that writes extended attributes and keeps no alternate streams, a file's streams come back as attributes of the same names, and each that does not is named), FR-RST-003 (amended: the plan names the files with a stream Linux keeps no attribute of)
**Related:** [ADR-0174](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md) (a file's streams where the target keeps them, read and proved like a segment, after the content and before the times), [ADR-0177](0177-a-restore-on-windows-keeps-a-files-extended-attributes-as-alternate-streams.md) (the other way: a file's attributes as streams of their names on Windows, which left this undone), [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) (the attributes a restore writes where the target writes them, and which namespaces Linux gives whom), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (alternate data streams degraded on Linux and macOS)

**Built:**
- The rule: `Domain/MetadataRules` says when a target keeps a file's streams as attributes, and why a stream is not kept as one. `Restore/RestorePlan` says a local target takes names only in a namespace on Linux.
- The plan: `Restore/RestoreMetadata` counts a file's streams applied on such a target, and declares on a line of its own the files with a stream Linux keeps no attribute of, from the count `Restore/RestoreBlobSet` gives each item's facts. The planner no longer declares the streams of such a target as unwritten.
- The write: `Restore/RestoreExecutor` reads each stream ahead with the file's segments, as it does where the target keeps streams, and writes it as the attribute of its name before the file's own attributes and permissions.
- The tests:
  - `Domain.Tests/StreamsAsAttributesTests` — on every platform: a file's streams are kept as attributes only where the target writes attributes and keeps no streams, and never a folder's or a link's; a name in the user namespace is kept wherever, and one outside it is refused where names take a namespace and kept where they do not; the streams outside it are counted.
  - `Repository.Tests/RestoreAlternateStreamsTests` — the profile says a local target takes names only in a namespace on Linux, and counts streams applied where it keeps them as attributes; on a target that keeps them in no form, the executor still names them as not written and degrades the file. On Linux and macOS, through the executor, a stream in the user namespace lands as the attribute of its name and the file is restored, with no plan line for unwritten streams; one whose name the file's own captured attribute has is not written over it, and is named. On Linux a stream outside the user namespace is named and the file degraded while the rest land; on macOS one of any name lands. Streams kept as attributes are read ahead with the segments, not fetched a stream at a time.
  - `Repository.Tests/RestoreMetadataHonestyTests` — on a target that keeps streams as attributes and takes names only in a namespace, the plan declares the file whose stream is outside the user namespace, one of two, on a line of its own, and a name no stream may have is an attribute's like any other; where names take no namespace, none.
  - `Repository.Tests/RestorePlanTests` — a target that keeps streams in no form still has the planner declare them.

---

## Context

Capture records a file's alternate data streams on Windows (specification 06 §4.1). A restore writes them back where the target keeps streams ([ADR-0174](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md)), which only Windows does. On Linux and macOS the planner declared every such file as restoring its main stream only, and each one came back degraded, its streams named as not written.

[ADR-0177](0177-a-restore-on-windows-keeps-a-files-extended-attributes-as-alternate-streams.md) went the other way: on Windows a file's extended attributes are kept as streams of the same names. So a macOS file restored to Windows kept its Finder information as a stream, a backup there captured that stream as a stream, and a restore of that backup to a Mac lost it. ADR-0177 named that as not done: a stream is not turned back into the attribute it came from.

Linux and macOS keep a file's named data as extended attributes. macOS takes an attribute of any name. Linux takes one only in a namespace, and a file's own attributes, which any account that may write the file may set, only in its user namespace: the others are the kernel's, or root's ([ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md)).

## Decision

### 1. A file's streams are kept as attributes where the target keeps no streams

Where the target writes extended attributes of its own and keeps no alternate streams, as Linux and macOS do, each of a file's captured streams is written as the extended attribute of the same name, holding the stream's content. A folder is captured with no streams, and a link has none, so only a file's are kept.

### 2. Linux takes a stream's name only in its user namespace

Where a target takes an attribute's name only in a namespace, as Linux does, a stream is kept as an attribute only where its name is in the user namespace, with something after it. Any other, such as Windows' own `Zone.Identifier`, is not written, and the file names it and is restored degraded, as a stream that does not land leaves a file ([ADR-0174](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md)). A stream ADR-0177 made from a Linux file's attribute was in the user namespace there, since the kernel's namespaces were never kept as streams, so it comes back as itself. macOS takes any name.

### 3. The file's own attribute keeps its name

Where a file was captured with both an attribute and a stream of the same name, the attribute is the file's own on this target, and the writer gives it the file after the streams. The stream of that name is not written, and the file names it, as ADR-0177 leaves a file's own stream to a name an attribute shares.

### 4. Read and written as a stream is

Each stream is read ahead with the file's segments and proved like one, as where the target keeps streams. It is written after the content and before the file's own attributes and permissions, since a mode that forbids writing would forbid it too, and before the times. A volume that refuses one, as one that takes no attributes or none that large, leaves the file degraded, naming it.

### 5. The plan predicts by the same rule

On such a target a file's streams count applied, and the planner no longer declares them as restoring the main stream only. Each item's facts count its streams outside the user namespace (`MetadataRules.StreamsOutsideUserNamespace`), and where the target takes names only in a namespace the plan declares those files on a line of their own, `alternate-stream-attribute-names`. The rule ADR-0174 holds a stream's name to, as a path, is not applied: an attribute's name is no path.

## Consequences

- A macOS file restored to Windows, backed up there and restored to a Mac keeps its Finder information, and a Linux file's user tags come back as themselves.
- A file downloaded on Windows, restored to Linux, is restored degraded, naming `Zone.Identifier`. Restored to a Mac, it keeps it as an attribute.
- A restore on Linux and macOS reads a file's streams, which it read nothing of before. The plan already counted their records in what a run reads.

## What this does not do

- **The recovery tool and `restore-file`.** They write a file's streams back by the shared write-back where the target keeps streams, and note them as not written elsewhere. Keeping them as attributes there is the next record's.
- **Rename a stream into the user namespace.** A stream named outside it is named, not given a `user.` name it never had: that name would not round-trip.
- **Judge a value's size in the plan.** A volume that takes no attribute that large refuses it at the write, which the receipt names.

## Alternatives considered

- **Prefix every stream's name with `user.` on Linux.** Rejected. It would keep `Zone.Identifier`, but under a name it never had. An attribute ADR-0177 kept as a stream would come back as `user.user.tag`.
- **Keep a stream as a file beside the restored one.** Rejected. It writes a name the snapshot never had, which a later backup would capture as a file of its own.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. On a target that writes extended attributes and keeps no alternate streams, the executor keeps each of a file's streams as the attribute of its name, before the file's own attributes and permissions; on Linux one outside the user namespace is named and not written, and one whose name the file's own attribute has is left to it; the plan counts them applied, and declares on a line of its own the files with a stream Linux keeps no attribute of. `Domain/MetadataRules`; `Restore/RestoreExecutor`; `Restore/RestoreMetadata`; `Restore/RestorePlan`; `Domain.Tests/StreamsAsAttributesTests`; `Repository.Tests/RestoreAlternateStreamsTests`; `Repository.Tests/RestoreMetadataHonestyTests` |

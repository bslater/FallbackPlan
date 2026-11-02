# ADR-0184 — Format 6: a manifest keeps a file's flags and a link's form

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-MAN-003 (amended: a format-6 manifest may keep a file's or a folder's flags, and say that a link is a mount point); NFR-COMP-004 (amended: `fixture-repository-v6`). Does not establish FR-RST-003 or FR-RST-004: no restore reads either key yet
**Related:** [ADR-0180](0180-capture-names-the-file-flags-it-does-not-keep.md) (the flags capture reads and names, which deferred a key), [ADR-0183](0183-capture-reads-a-reparse-point-as-a-link-only-where-its-tag-names-one.md) (a mount point captured as a link, which left junctions as junctions), [ADR-0014](0014-format-versioning-and-stability.md) (refuse by name; Amendment 2 records this), [ADR-0066](0066-the-format-upgrade-record.md) (the upgrade record), [ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md) (format 5, whose index format 6 keeps), [specification 06 §4](../../specifications/repository-format/06-manifests.md#4-file-version-manifest) and [§4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map)

**Built:**
- The constants: `Domain/FormatVersions` (`FileFlagsAndLinkForms` = 6) and `Domain/FormatLimits`, which create and upgrade at 6. `Domain/LinkForm` names a link's two forms, and `Domain/EntryMetadata` carries a file's flags.
- The wire: `Repository.Format/Manifests/FileVersionManifestCodec` reads and writes metadata key 11 and manifest key 14, and refuses each where it names nothing, a reserved bit, an unassigned form, or a form on anything but a link with a target. `Repository.Format/Descriptor/RepositoryDescriptorCodec` adds feature `0x0006`, and `Repository/RepositoryLifecycle` lists it at format 6.
- The tests:
  - `Repository.Tests/Format/ManifestCodecTests`: key 11 round-trips, is the last key written, and counts in the metadata digest; a key 11 naming no flag, or a reserved bit, is refused at read and at write; a tree's metadata keeps a folder's flags; key 14 round-trips for a mount point and is absent for a symbolic link; an unassigned form, or a form on a file or on a link without a target, is refused; key 15 is unknown
  - `Repository.Tests/Format/RepositoryDescriptorCodecTests`, `Repository.Tests/EndToEnd/RepositoryLifecycleTests`, `Domain.Tests/FormatVersionsTests`: a format-6 descriptor lists all four features, either mismatch is a violation, format 7 is refused naming 2 to 6, a repository is born at 6, and format 5 is still creatable without `0x0006`
  - `Hosts.Tests/CreatedFormatTests`: a set created today is format 6 and lists its feature
  - `Repository.FuzzTests/FuzzCorpus`: the decoder is fuzzed from a mount point's manifest carrying flags
  - `Repository.ConformanceTests/FixtureRepositoryV6Tests`: the committed `fixture-repository-v6` declares format 6 and its four features, keeps format 3's blobs and format 5's index, rebuilds a catalogue with no finding, and holds a file whose manifest keeps the no-dump flag and restores byte for byte, and a junction whose manifest names it a mount point

---

## Context

Two records stopped at the same wall.

- **ADR-0180** reads a Linux or macOS file's immutable, append-only, no-dump, compressed, encrypted and hidden flags, and names them among its capture diagnostics. No key keeps them, so no restore can give them back. Its alternatives deferred a key: "It is a format change, and naming what is lost comes first."
- **ADR-0183** captures a Windows mount point, which is what a junction is, as a link. Nothing in a manifest says which kind of link it was, so a restore on Windows makes a folder's symbolic link where a junction stood. Its "What this does not do" left junctions as junctions.

A manifest's keys are closed. A reader refuses a key it does not know, rather than skipping it, because an unknown key is exactly where a physical-location field would hide ([06 §3.1](../../specifications/repository-format/06-manifests.md#31-no-physical-location--and-why)). So a key a reader of today has never seen makes every manifest carrying it unreadable to that reader. ADR-0014 asks such a change to come as a format version, which an older reader refuses by name.

## Decision

### 1 Metadata key 11 keeps a file's or a folder's flags

The metadata map gains key 11, `file_flags`, a `u32` whose bits are the flags ADR-0180 names, in its order: `0x01` immutable, `0x02` append-only, `0x04` no-dump, `0x08` compressed, `0x10` encrypted, `0x20` hidden.

- **Absent where none is set.** A key naming no flag would be a second encoding of an entry with none, so a reader refuses 0.
- **No reserved bit.** Every bit above `0x20` is reserved. A reader refuses one, and a writer refuses to write one.
- **A folder's too.** A tree manifest's metadata is this map, so key 11 can keep a folder's flags, which ADR-0180 could not name, since a tree carries no diagnostics.
- **Counted in the metadata digest.** The digest an incremental compares covers the metadata map, so a file whose flags alone change gets a new version, as ADR-0180 §3 already has it.

  > **2026-11 ([ADR-0180 Amendment 3](0180-capture-names-the-file-flags-it-does-not-keep.md#amendment-3-2026-11--format-6-keeps-the-flags-a-folders-too)).** The digest counts the flags key 11 keeps as it counts those a diagnostic names, rather than as part of the map, so a manifest of either form digests alike and an upgrade restates no flagged file.

Windows' attribute bits stay in key 10. The two are kept apart because each platform's bits would be read as the other's ([ADR-0180](0180-capture-names-the-file-flags-it-does-not-keep.md), alternatives).

### 2 Manifest key 14 says that a link is a mount point

The file version manifest gains key 14, `link_form`, a `u8`. Its one value is 1: the link is a mount point, as a Windows junction is.

- **A symbolic link omits it.** So a symbolic link's manifest is exactly the one an earlier format wrote, and 0 is never written. A reader refuses 0 and every value but 1.
- **Only on a link with a target.** A reader refuses the key on a file, a folder, a special file, or a link that carries no `link_target`, since a mount point is a link to a path.

### 3 Format 6 names itself, and the product creates at it

A format-6 descriptor lists `0x0006` (`file-flags-and-link-forms`) beside `0x0003`, `0x0004` and `0x0005`. An earlier descriptor never lists it, and either mismatch is a format violation ([01 §3.2](../../specifications/repository-format/01-object-layout.md#32-body)). So a reader that predates format 6 refuses a repository created at it by name, at open.

Format 6 changes what a manifest may say and nothing else. Its index is format 5's, and its blobs and records are format 3's, stamped 3 ([00 §5](../../specifications/repository-format/00-conventions.md#5-versioning-and-feature-negotiation)). `FormatLimits.FormatVersion` is 6, so a repository is born at 6, and formats 2 to 5 remain creatable on request and readable for ever. A set below format 6 is upgraded on request by [ADR-0066](0066-the-format-upgrade-record.md)'s record, to the latest.

### 4 A writer writes neither key below format 6

A reader that predates format 6 refuses a manifest carrying either key as an unknown key, and reports it as damage. So a repository such a reader is entitled to read must never hold one. `FormatVersions.HasFileFlagsAndLinkForms` is the gate the writers ask.

> **2026-11: key 11 is written by [ADR-0180 Amendment 3](0180-capture-names-the-file-flags-it-does-not-keep.md#amendment-3-2026-11--format-6-keeps-the-flags-a-folders-too)** from format 6, for a file and a folder. No restore gives a flag back yet, and key 14 is still unwritten.
>
> **2026-11: and read by [ADR-0180 Amendment 4](0180-capture-names-the-file-flags-it-does-not-keep.md#amendment-4-2026-11--a-restore-gives-the-flags-back-where-it-may),** whose restore gives the flags back where the platform and the account let it. Key 14 is still unwritten.
>
> **2026-11: key 14 is written by [ADR-0183 Amendment 1](0183-capture-reads-a-reparse-point-as-a-link-only-where-its-tag-names-one.md#amendment-1-2026-11--capture-keeps-a-junctions-form)** from format 6, for a junction. No restore reads it yet.
>
> **2026-11: and read by [ADR-0183 Amendment 2](0183-capture-reads-a-reparse-point-as-a-link-only-where-its-tag-names-one.md#amendment-2-2026-11--a-restore-on-windows-makes-a-junction-again),** whose restore makes a junction a junction on Windows.

Nothing writes either key yet. Capture still names a file's flags among its diagnostics and nowhere else, and still captures a mount point as a link with no form. The writers come with the restores that read them, one record each:

- **the flags,** under ADR-0180, where capture keeps them in key 11 at format 6 and a restore gives them back where the platform and the account allow;
- **junctions,** under ADR-0183, where capture keeps the mount-point tag in key 14 and a restore on Windows makes a junction.

Publication, which builds each manifest, already holds the repository's effective version, so each writer asks the gate where it builds one.

## Consequences

**Positive**
- **A file's flags and a link's form have somewhere to live.** The two records that deferred a key can now keep what they read.
- **Nothing a format-5 manifest says changes.** A manifest with no flags and no mount point is byte for byte the one format 5 writes, so a repository created at 6 holds format 5's manifests until a writer has something to keep.

**Negative**
- **A build that predates format 6 cannot open a repository created at it.** It refuses it by name at open, as ADR-0014 asks.
- **An older reader of an upgraded repository will report a manifest carrying either key as damage.** The descriptor of a repository upgraded to 6 still says what it was created at, so an older build opens it, refuses each such manifest's key, and reports an object it cannot read. It cannot restore that file. That is ADR-0066's stated price, as at formats 4 and 5, and it is paid only once a writer keeps a flag or a form there. A repository created at 6 is refused at the door.

## Alternatives considered

- **One key per finding, in one format each.** Flags in format 6, junctions in format 7. Two format changes where one serves, and two refusals for every older reader, for keys that change nothing else.
- **Keep the flags in key 13's diagnostics.** They are there today. Diagnostics are informational text every reader accepts and any writer may word; a restore that set a flag from a sentence would trust prose to carry a fact.
- **A link form with both values written.** Writing 0 for a symbolic link would change every link's manifest, and an older reader would refuse every one of them in an upgraded repository, not only the junctions.
- **A key on the tree entry rather than the manifest.** A tree entry carries an entry's name, kind and object, and a link's form is what its own manifest says it is. A tree entry has no key to spare without a format change of its own.

## What this record does not do

- **Write either key.** Capture writes neither, and no restore reads either. The writers and the restores are the next records', under ADR-0180 and ADR-0183, and each will be held to format 6.

  > **2026-11: key 11 is taken up by [ADR-0180 Amendment 3](0180-capture-names-the-file-flags-it-does-not-keep.md#amendment-3-2026-11--format-6-keeps-the-flags-a-folders-too),** and every reader of a file's flags reads it.
>
  > **2026-11: and key 14 by [ADR-0183 Amendment 1](0183-capture-reads-a-reparse-point-as-a-link-only-where-its-tag-names-one.md#amendment-1-2026-11--capture-keeps-a-junctions-form).** No restore reads it yet.
>
  > **2026-11: and read by [ADR-0183 Amendment 2](0183-capture-reads-a-reparse-point-as-a-link-only-where-its-tag-names-one.md#amendment-2-2026-11--a-restore-on-windows-makes-a-junction-again)** for the engine's restore.
- **Keep a flag Windows names.** Windows' attribute bits stay in key 10 ([ADR-0175](0175-a-restore-writes-back-the-windows-attribute-bits-it-may.md)).
- **Keep a placeholder a cloud provider leaves unfetched.** ADR-0183's other limit names no key here.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first. Metadata key 11 keeps a file's or a folder's flags and manifest key 14 says that a link is a mount point, each refused where it names nothing, a reserved bit or an unassigned form, or sits on anything but a link with a target; a format-6 descriptor lists `0x0006`, and a repository is born at 6. Nothing writes either key yet. `Domain/FormatVersions`; `Domain/FormatLimits`; `Domain/LinkForm`; `Repository.Format/Manifests/FileVersionManifestCodec`; `Repository.Format/Descriptor/RepositoryDescriptorCodec`; `Repository/RepositoryLifecycle`; `Repository.Tests/ManifestCodecTests`; `Repository.Tests/RepositoryDescriptorCodecTests`; `Repository.Tests/RepositoryLifecycleTests`; `Domain.Tests/FormatVersionsTests`; `Hosts.Tests/CreatedFormatTests`; `Repository.ConformanceTests/FixtureRepositoryV6Tests` |
| 2026-11 | Accepted (related) | [ADR-0180 Amendment 3](0180-capture-names-the-file-flags-it-does-not-keep.md#amendment-3-2026-11--format-6-keeps-the-flags-a-folders-too) writes key 11 from format 6, a file's and a folder's flags, and counts them in the digest as it counts flags a diagnostic names. `Repository/SnapshotPublication`; `Repository.Format/Manifests/FileVersionManifestCodec` |
| 2026-11 | Accepted (related) | [ADR-0180 Amendment 4](0180-capture-names-the-file-flags-it-does-not-keep.md#amendment-4-2026-11--a-restore-gives-the-flags-back-where-it-may) has the engine's restore give back the flags key 11 keeps, a file's, a folder's and a link's, where the platform and the account let it. `Domain/FileFlags`; `Restore/RestoreExecutor` |
| 2026-11 | Accepted (related) | [ADR-0183 Amendment 1](0183-capture-reads-a-reparse-point-as-a-link-only-where-its-tag-names-one.md#amendment-1-2026-11--capture-keeps-a-junctions-form) writes key 14 from format 6 for a junction, a mount point naming a folder by its drive's path, which below 6 stays named among its diagnostics. `Filesystem.Local/ReparsePoints`; `Domain/MetadataRules`; `Repository/SnapshotPublication` |
| 2026-11 | Accepted (related) | [ADR-0183 Amendment 2](0183-capture-reads-a-reparse-point-as-a-link-only-where-its-tag-names-one.md#amendment-2-2026-11--a-restore-on-windows-makes-a-junction-again) reads key 14, and the diagnostic before format 6: the engine's restore makes a junction a junction on Windows, and elsewhere a symbolic link it names as one. `Domain/MountPoints`; `Restore/RestoreExecutor` |

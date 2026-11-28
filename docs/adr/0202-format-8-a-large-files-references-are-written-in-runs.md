# ADR-0202 — Format 8: a large file's references are written in runs

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-ARCH-002 (amended: a file too large for one manifest), FR-ARCH-010 (a file version is its ordered references), FR-MAN-004 (no single manifest grows with what it describes), NFR-PERF-001
**Related:** [Q25](../open-questions.md#q25--the-largest-file-one-manifest-can-describe), [ADR-0026](0026-phase-1-capture-shapes.md) (a tree is sharded into a chain), [ADR-0014](0014-format-versioning-and-stability.md) (refuse by name), [ADR-0066](0066-the-format-upgrade-record.md) (the upgrade record), [ADR-0197](0197-format-7-a-failure-says-what-stands-in-its-place.md) (format 7, whose objects this keeps), [ADR-0201](0201-a-snapshots-bookkeeping-waits-in-its-spool.md) (what a snapshot holds in memory)

**Built:**
- The wire: `Domain/ObjectType` assigns `ReferenceRun` (`0x12`), and `Domain/FormatVersions` defines format 8 (`ReferenceRuns`) and its writer gate, which this build neither reads nor writes yet. `Repository.Format/Manifests/ReferenceRun` and `Repository.Format/Manifests/ReferenceRunCodec` encode, decode and check a run, and a file's runs against its manifest. `Repository.Format/Manifests/FileVersionManifestCodec` reads and writes key 15 and holds a manifest listing runs to decision 2.
- The tests: `Repository.Tests/Format/ReferenceRunCodecTests` (a run round-trips; a run that does not cover its stretch exactly, has a gap or an overlap, holds more than 262,144 pieces or none, or carries an unknown key is refused; a manifest listing runs round-trips in key 15 and holds no references or holes of its own; one run, or runs on a link, are refused; a file's runs from 0 to its length, each but the last full, are its references, and runs that do not join, stop short, are not full before the last or are not as many as listed are refused), `Repository.Tests/Format/ManifestCodecTests` (key 16 is unknown), `Domain.Tests/ObjectTypeTests` and `Domain.Tests/FormatVersionsTests`.
- The readers that keep and trace a version's content (decision 4): `Repository.Format/Manifests/ReferenceRunReader` reads a version's runs one at a time in its order and holds each to the manifest as it arrives, and a run that does not read is a `Repository.Format/Manifests/ReferenceRunMissingException` naming it. `Retention/StagingMark` keeps every run a kept version lists and every segment the runs name, and a run that will not read makes the walk unwalkable, so the sweep deletes nothing. `Repository/CatalogueProjector` and `Repository.Catalogue/Forensic/ForensicRebuilder` record a run's segments as content its version needs, the rebuilder needs them as it needs a manifest's own, and both record a run that will not read as missing. `Repository.Format/Manifests/FileVersionManifest` names a version's runs among the objects it needs.
- Their tests: `Repository.Tests/Format/ReferenceRunReaderTests` (runs are read in order and none for a manifest without them; a run that does not read is named and nothing after it is read; a run that does not follow the one before, is not full before the last, does not decode, or leaves the file short is refused) and `Repository.Tests/EndToEnd/ReferenceRunReachTests` (over a snapshot built by hand, one file of two runs: the mark keeps both runs and both segments and is unwalkable over a missing run; a projected or forensic catalogue traces each run's segments, and the run blob, to the file, records a missing run as missing, and a forensic rebuild needs the segments a run names).
- The readers that read a version's content (decision 4): `Repository/RestoreEngine` and `Repository/VerifyEngine` read a file a stretch at a time through `Repository/RestorePart`, its own pieces or each run in turn, which `Repository/RepositoryReader` reads. A restore refuses a run that will not read or does not follow, and puts nothing down; it takes a kept part up across runs, and emits a file with holes from where its data is rather than from its pieces again. Their tests: `Repository.Tests/EndToEnd/ReferenceRunRestoreTests` (a two-run file restores byte for byte into a stream, a new file and where it lands; a missing run, a run out of sequence, or a segment only a run names that will not read refuses the restore and puts nothing down; a part holding the first run is taken up and the second run's segment alone read, and one holding the whole file is taken up whole; verification reads the runs as a restore does).
- What a restore plans before it reads a file, and the readers outside the product: `Repository/RestorePart` measures a file once, a run at a time, for what it writes and what a kept part holds of it, which `Restore/RestoreSpace` and `Restore/RestoreBlobSet` ask of it. The plan probe reads a file's runs as it reads its manifest, needs their blobs and those of the segments they name, and cannot reach a file a run or one of its segments has gone from. `Recovery/RecoverySession` reads a file through its runs with its own reader, noting a run it cannot read as its record. `Cli/CliApplication`'s inspect-manifest names a manifest's runs and decodes a run. Their tests: `Repository.Tests/EndToEnd/ReferenceRunPlanTests` (the plan needs the runs' and the segments' blobs and counts the file's 70,000 bytes; a lost segment or run makes the file unreachable; a part is counted across runs; a restore run puts the file down, fresh and resumed across runs), `Repository.Tests/EndToEnd/ReferenceRunRecoveryTests` (the tool puts the file down, and fails it alone naming the run it cannot read or that does not follow) and `Cli.Tests/InspectionCommandTests`.

**Not yet built:** the receipt's word on which copies a large file's segments were read from, where only its runs name them (FR-RST-007); what the capture path records a renamed file's version as needing; the writer, which writes runs; the descriptor's feature `0x0008`; and format 8 as one this build reads and writes. Until then a file too large for one manifest is recorded as too large and the rest of its snapshot is backed up (Q25).

---

## Context

A file version manifest holds every one of its file's segment references, and its holes, in one object. An object may be at most 16 MiB (specification 00 §8), and a reference costs about 49 bytes encoded. 300,000 references encode to 14.7 MB and 400,000 are refused. So the largest file one manifest describes is about 343,000 segments: about 335 GiB at the default 1 MiB segments. A 2 TiB file, which FR-ARCH-002 names, needs about two million.

A tree already meets the same limit. A directory too large for one tree manifest is split into a chain, each manifest naming the next (06 §9). The maintainer chose the same for files ([Q25](../open-questions.md#q25--the-largest-file-one-manifest-can-describe)): a file too large for one manifest is described by several, written as the file is archived. That also bounds what one huge file holds in memory, which is the part of [Q24](../open-questions.md#q24--what-a-snapshot-holds-per-object-it-stores) a spill of the finished files does not reach.

A tree's chain cannot be written that way. Each link names its successor by object identifier, and an identifier is derived from the bytes, so a link can be written only once the one after it has been. A tree writes its chain from the tail, which it can, because a directory's entries are all known before it writes any. A file's references are not known until the file has been read to its end.

## Decision

### 1. A reference run holds one stretch of a file

A **reference run** is an object of type `0x12`. It holds the segment references and the holes of one contiguous stretch of one file, in the shapes 06 §3 and §4 give them, with the stretch's start and length:

| Key | Type | Value |
|-----|------|-------|
| 1 | u64 | `start` — where in the file the stretch begins |
| 2 | u64 | `length` — how much of the file it covers |
| 3 | array | `segment_references` — as 06 §3, offsets in the file |
| 4 | array | `sparse_extents` — as 06 §4 key 6, offsets in the file; absent when there are none |

Its references and holes, in ascending order, cover `[start, start + length)` exactly, as a manifest's cover the file (06 §3.2). A run holds at most **262,144** pieces, references and holes together, which is at most about 14 MB encoded and so always inside the limit.

The run has a type of its own because it is not a file version. A tree never names one, and an identifier binds its type (02 §3), so a run can never be read as a version or a version as a run.

### 2. The file's manifest lists its runs

A file version manifest whose file needs runs carries the new key **15**, `reference_runs`: the object identifiers of its runs, in file order. It then carries no references and no holes of its own: key 5 is the empty array and key 6 is absent. Its runs, in order, start at 0, each where the one before it ends, and the last ends at `logical_length`.

Every other key is the manifest's as before: the file's name, length, whole-file hash, segmentation profile, metadata and the rest. A manifest without key 15 is byte for byte the one format 7 writes.

A list in the manifest, rather than a chain through the runs, is what lets the runs be written first. Each run is complete once its stretch of the file has been read, and the manifest, written last, names them all. A reader reads the manifest and then each run in order, so it reads a file's references front to back, as it does now.

### 3. A file needs runs past 262,144 pieces

A file whose references and holes together number more than 262,144 is described by runs, and one whose number no more than that by one manifest, as before. The rule is a count rather than an encoded size so that the same file is described by the same objects on every device, which is what lets two devices deduplicate a version's manifest as they do its segments. Each run but the last holds exactly 262,144 pieces.

### 4. A run is part of its version everywhere a version's content is

A reader MUST read every run of a manifest that lists them before it treats the version as read. A run it cannot find, or whose stretch does not start where the one before ended, is a damage finding, as a missing tree continuation is (06 §9). A version's runs are part of it wherever its content is: what a restore reads, what a verify reads, what the collector keeps reachable, what the catalogue records a version as needing, and what damage traces back to it.

### 5. Format 8

Writing a run, or key 15, is format **8**, which lists feature `0x0008` (01 §3.2). A writer MUST NOT write either below format 8, so a reader that predates it never meets one in a repository it is entitled to read, and refuses a format-8 repository by name (ADR-0014). A repository below format 8 keeps recording a file too large for one manifest as too large. One upgraded by a format-upgrade record (ADR-0066) writes runs from its next sealed object.

### 6. 06 §3.2's reference limit is per object

06 §3.2 limits a manifest to 1,048,576 references and says a file needing more needs a larger segment size. The limit becomes one an object holds: a manifest without runs at most 1,048,576 references, which 16 MiB reaches first, and a run at most 262,144 pieces. A file's own count is bounded by the 16 MiB its manifest's list of runs may take, about 490,000 runs: a file of about 7 PiB at the smallest segment size, and about 120 PiB at the default.

## Consequences

- A file of any size the format's offsets describe can be backed up, at any segment size.
- A file that needs runs costs one object more for each 262,144 pieces of it, about 256 GiB at 1 MiB segments, and its manifest about 34 bytes more for each.
- Every reader of a file version's references changes, the collector's mark among them, and so does the recovery tool. A reader that missed a run would restore a short file, or let a collection take a run a version still needs. Damage findings, closures and reachability all count runs.
- Positional comparison with a file's prior version is unchanged: it compares content identifiers, which a run does not hold.

## Alternatives considered

- **A chain through the runs, as a tree has.** Each run would name the next, so each could be written only after the one after it: the whole file's references in memory first, which is the cost this removes.
- **A chain backwards, each run naming the one before.** It can be written as the file is read, but a reader then meets the references back to front, and must hold them all to restore the file in order.
- **A larger segment size for a large file** (Q25 (b)). No format change, but a file that crossed the size would lose positional reuse against its previous version, and a huge file would still hold its references in memory.
- **Runs as file version manifests without a name.** A run is not a version, and one readable as a version would be one more thing every reader of versions had to tell apart.

## What this record does not do

It changes nothing about trees, snapshots, the index or blobs. It does not decide how a run's references are written as the file is archived, which is the writer's business; it only makes it possible.

## Status history

| Date | Status | Note |
|---|---|---|
| 2026-11 | Accepted | The maintainer chose to chain file manifests (Q25). The wire is built, tests first: `Domain/ObjectType`, `Domain/FormatVersions`, `Repository.Format/Manifests/ReferenceRun`, `Repository.Format/Manifests/ReferenceRunCodec` and `Repository.Format/Manifests/FileVersionManifestCodec`, held by `Repository.Tests/Format/ReferenceRunCodecTests`. Format 8 is not yet read or written: its readers come first. |
| 2026-11 | Accepted | The readers that keep and trace a version's content follow its runs, tests first: `Repository.Format/Manifests/ReferenceRunReader` and `Repository.Format/Manifests/ReferenceRunMissingException`, used by `Retention/StagingMark`, `Repository/CatalogueProjector` and `Repository.Catalogue/Forensic/ForensicRebuilder`, held by `Repository.Tests/Format/ReferenceRunReaderTests` and `Repository.Tests/EndToEnd/ReferenceRunReachTests`. Restore, verify and the recovery tool come next; format 8 is still neither read nor written. |
| 2026-11 | Accepted | Restore and verify read a version's content through its runs, tests first: `Repository/RestoreEngine` and `Repository/VerifyEngine`, a stretch at a time through `Repository/RestorePart` and `Repository/RepositoryReader`, held by `Repository.Tests/EndToEnd/ReferenceRunRestoreTests`. What a restore plans, the recovery tool and the CLI come next; format 8 is still neither read nor written. |
| 2026-11 | Accepted | What a restore plans reads a file through its runs, and so do the recovery tool and inspect-manifest, tests first: `Repository/RestorePart`, `Restore/RestoreSpace`, `Restore/RestoreBlobSet`, `Recovery/RecoverySession` and `Cli/CliApplication`, held by `Repository.Tests/EndToEnd/ReferenceRunPlanTests`, `Repository.Tests/EndToEnd/ReferenceRunRecoveryTests` and `Cli.Tests/InspectionCommandTests`. Every reader of a file's content now follows its runs; the writer comes next. |

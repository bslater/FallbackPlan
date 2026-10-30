# ADR-0174 — A restore writes a file's alternate streams back where the target keeps them

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a file's captured alternate streams come back where the target keeps them, and one that does not is named), FR-RST-005, FR-RST-004 (amended by Amendment 1: the recovery tool gives them back by the same rule), FR-RST-004 (amended by Amendment 2: so does the single-file restore), FR-RST-003 (amended by [Amendment 3](#amendment-3-2026-10--the-plan-names-a-file-whose-stream-has-a-name-no-stream-may-be-written-under): the plan names a file whose stream has a name no stream may be written under)
**Related:** [ADR-0026](0026-phase-1-capture-shapes.md) §5 (a stream is captured whole as one segment record), [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (what a restore says it does not write back), [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) (each attribute on its own, and a receipt that records what each write did), [ADR-0109](0109-a-restore-cut-short-is-resumed.md) (a resumed run leaves what an earlier one landed as it stands), [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) (the recovery tool, which still writes none), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (alternate data streams are preserved on Windows), [specification 06 §4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map) (key 9, each stream's name, object identifier and length), [restore review RR-6](../review/2026-08-restore-pipeline-review.md#rr-6--alternate-data-streams-are-captured-and-never-restored)

**Built:**
- The calls and the rule: `Domain/AlternateStreams` writes a stream onto a file and reads one back, on Windows alone, and holds the rule a captured stream's name is judged by before it is joined to a file's path. Off Windows it writes nothing and says so.
- The restore: `Restore/RestoreExecutor` writes each stream onto a file it landed or found in place, before its times, read and proved like a segment and read ahead with its segments. A stream already standing as captured is left. One that does not land leaves the file degraded, naming it and why. `Restore/RestorePlan` says a local target writes streams where the platform keeps them, and `Restore/RestoreMetadata` counts a file's streams applied there.
- The tests:
  - `Domain.Tests/AlternateStreamsTests` — a stream's own name is its name, one with a separator, a colon, a NUL or a trailing period or space is refused, as are bytes that are not UTF-8 and a name longer than 255 units; on Windows a stream is written onto a file, read back, written over whole, and not read at another length; elsewhere none is written and nothing is made beside the file
  - `Repository.Tests/RestoreAlternateStreamsTests` — a file's streams are written back onto it and the file is restored, with its captured modification time though writing a stream moves it; a stream whose name no stream can have reaches no call, and one the volume refuses or whose record will not read leaves the file degraded, naming it and why; one damaged in its own store is read from another copy, which the file names; a resume leaves a stream standing as captured and reads it from nowhere, and writes one missing or altered; streams written back cost a tree no fetch a stream; the local profile writes streams only on Windows; on Windows a file's streams round-trip through capture and restore; elsewhere the platform's calls make nothing beside the file
  - `Repository.Tests/RestorePlanTests`, `Repository.Tests/RestoreWithinAFileTests` — the cases that hold a target that writes no streams to the main stream alone now say that is the target they mean

---

## Context

Capture records each named alternate data stream of a file on Windows as its name, the object identifier of one segment record holding its content, and its length ([ADR-0026](0026-phase-1-capture-shapes.md) §5). Architecture 06 §3 says Windows preserves them. The restore review found that no restore did ([RR-6](../review/2026-08-restore-pipeline-review.md#rr-6--alternate-data-streams-are-captured-and-never-restored)): the executor wrote the main stream, dropped the others and reported the file restored.

The review's honesty half was built first. The catalogue carries whether a file has streams, the plan declares that a target restores only the main stream, and the receipt reports such a file degraded and the run Partial. The target's `SupportsAlternateStreams` was false everywhere, Windows included, "until the write-back half lands". So every file downloaded on Windows, which carries a stream naming where it came from, came back without it, and every restore of a tree of them was Partial.

A stream is opened on Windows as the file's path, a colon and the stream's name. That makes the name part of a path, and the name is read from a manifest, which is untrusted.

## Decision

### 1. The executor writes each stream onto the file, where the target keeps them

Where the target writes streams, each stream a file was captured with is read by its object identifier and proved as any segment is, against the identifier the repository's content key derives, then written onto the file as its whole content. A stream that already holds something is written over.

A local target writes streams where the platform keeps them, which is Windows alone. A Windows volume that keeps none, as a FAT one, refuses each stream, and only the receipt can say so.

### 2. A stream's name is judged before it is joined to the path

Windows resolves the joined path before it opens it. `file:..\..\x` is three components, the second `..`, so it names `x` beside the file. A trailing period or space is stripped, so `file:..` names the file itself, and writing it would replace the file's content. A stream's name must therefore be UTF-8, hold no `\`, `/`, `:` or NUL, end in neither a space nor a period, and be no longer than the 255 units Windows holds in one. One that is not is never written.

It is not held to the rule a file's name is held to ([ADR-0170](0170-a-restore-plan-judges-names-by-the-targets-naming-rules.md)). Windows names its own property-set streams with a leading control character, which a file's name may not hold.

### 3. Streams go on after the content and before the times

Writing a stream moves its file's modification time on NTFS, as writing the file does. Streams are written once the file's content has landed and verified, and before the metadata the rule writes back, so the file keeps the time it was captured with.

A run cut short between the two leaves a whole file without its streams, which a resume finds in place ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)). A stream already standing as captured, proved against its identifier and length, is left and read from nowhere. That is what the plan of a resume already counted: a file its run landed reads nothing of its streams. One missing or holding something else is written.

### 4. A stream that does not land degrades the file and is named

A name no stream can have, a record that will not read, and a write the volume refuses each leave that stream unwritten. The file's content stays where it landed, the other streams are written, and the item is degraded. Its detail names each stream not written and why, and `alternate_streams` is listed as not applied. Where every stream lands, the item is restored and streams are not listed.

### 5. Streams are read like segments

A file's streams are read ahead with its segments in the prefetch wave, so a tree of files that each carry a stream costs no fetch a stream. They are read around a damaged copy as a segment is, and the item names the copy they came from ([FR-RST-007](../requirements/functional.md)). Where the target writes none, none is read.

### 6. The calls are passed in

The executor takes the calls that write and read a stream as an argument, as it takes the call that makes a hard link. The platform's are the default. So what it asks for, in what order and with what names, is judged on every platform against calls that hold streams in memory. Off Windows the platform's calls write nothing: there a colon is a name's own character, and the joined path would make another file beside the one restored.

## Consequences

- A file restored on Windows carries the streams it was captured with, its zone identifier among them, and a tree of downloads restores Complete rather than Partial.
- A plan on Windows declares no alternate-streams line. A plan for a target that writes none still does.
- The first test of capture's streams against a real file runs on Windows, as the round trip.
- The executor reads each stream's record where it writes streams, so a restore on Windows reads what the plan already counted it would.

## What this does not do

- **The recovery tool.** It still writes no streams back, and lists them as not applied ([ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md)).

  > **2026-10 ([Amendment 1](#amendment-1-2026-10--the-recovery-tool-writes-a-files-streams-back-by-the-same-rule)).** Taken up: the tool writes a file's streams back by this record's rule, and notes one that does not land.
- **Judge a stream's name in the plan.** The plan's items carry whether a file has streams, not their names, so a name no stream can have is found at execution and named in the receipt.

  > **Built 2026-10 ([Amendment 3](#amendment-3-2026-10--the-plan-names-a-file-whose-stream-has-a-name-no-stream-may-be-written-under)).** The plan's facts now count the streams whose names §2 refuses, and the plan names those files.
- **Extended attributes on Windows.** Architecture 06 §3 would keep them as streams there. They are still listed as not applied.
- **A handle-relative write.** A stream is written by path, as the times and attributes are, so the window [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) records covers it too.

## Alternatives considered

- **Write the streams onto the spool, before it is moved into place.** Rejected. A file found in place by a resume has no spool, so the two paths would differ, and one that landed without its streams would need the other path anyway.
- **Hold a stream's name to the rule a file's name is held to.** Rejected. It refuses streams Windows itself makes, and the rule's purpose, a name that comes back as written, is met by the narrower one.
- **Fail the file when a stream does not land.** Rejected. Its content landed and verified, and a failed item would say the content did not. A stream is held as metadata is: what did not land is named, and the content stands.
- **Write streams after the times and set the times again.** Rejected. It writes the times twice for every file with a stream, to undo an order that can simply be kept.

## Amendment 1 (2026-10) — the recovery tool writes a file's streams back by the same rule

The record left the standalone recovery tool writing no streams. It counted each file's streams as not applied, as [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) had it, so a person recovering from the worst case got the files the executor would give back, less their streams.

**Decision.** The tool writes them by §§2–4:

- **once a file's content has landed and its whole-file hash verified,** each stream is read from its record, which the blob reader authenticates and checks against its content identifier, and written onto the file before its times;
- **a captured name is judged before it is joined to the file's path,** by the rule the executor uses;
- **a stream that does not land is noted, saying which and why,** and counted not applied, and the file still restores. A name no stream can have, a record in no readable blob or one that will not read, and a write the output refuses each do so;
- **off Windows the tool asks the platform for no stream,** and counts them not applied, as before.

The tool has no resume, so it does not look for a stream already standing as captured.

**Built:** `Recovery/RecoverySession`; `Recovery/RecoveryNote`:

- `Repository.Tests/RecoveryAlternateStreamsTests`: a file's streams are written back onto it and not counted; with calls that move a file's time as NTFS does, it keeps its captured modification time; a stream whose name no stream can have reaches no call and is noted, and so is one the output refuses or whose record is damaged, while the file restores; off Windows the platform is asked for none and the streams are counted not applied; on Windows a real file's streams round-trip through capture and the tool.

The in-memory calls the executor's suite used move to a helper both suites share, `Repository.Tests/HeldStreams`.

**Not done.** The CLI's `restore-file` still writes no streams.

> **2026-10 ([Amendment 2](#amendment-2-2026-10--the-single-file-restore-writes-a-files-streams-back-by-the-same-rule)).** Taken up: `restore-file` writes them by the same rule, through a helper the recovery tool now shares.

## Amendment 2 (2026-10) — the single-file restore writes a file's streams back by the same rule

Amendment 1 left the command line's `restore-file` writing no streams. It writes a file's metadata by the executor's rule ([ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md)), and listed its streams as not applied, so a single file restored on Windows came back without the stream naming where it came from.

**Decision.** The recovery tool's stream writes become one Domain helper, and `restore-file` calls it too:

- **`Domain/AlternateStreams` writes each stream a file was captured with onto the file,** its name judged by §2 before it is joined to the path, its content as the caller reads it, verified, and the write through the calls it is given. It answers why each stream that did not land did not, in order, and goes on to the next;
- **the recovery tool reads a stream from the record the blob reader authenticates and checks** (Amendment 1), and says what the helper answers in its notes;
- **`restore-file` reads a stream through the repository reader,** which proves it against its identifier as it proves a segment, once the file has landed and before its times. Each stream that does not land is said in a line of the command's output, and its streams are listed not applied; where every one lands they are not;
- **where the platform keeps no streams, `restore-file` asks for none,** and lists them not applied as before.

The executor keeps its own write, which also proves a stream already standing as captured before it reads one (§3), for the resume the other two do not have.

**Built:** `Domain/AlternateStreams`; `Recovery/RecoverySession`; `Cli/CliApplication`:

- `Domain.Tests/AlternateStreamsTests`: the helper writes each stream with the content it reads and has nothing to say; a stream whose name no stream can have is neither read nor written, and is named, while the next is written; one that will not read is not written and says why; one the calls refuse says why; a file with no streams reads and writes nothing;
- `Cli.Tests/InspectionCommandTests`, on Windows: a real file's two streams go through `backup` and `restore-file` and come back, with the file's modification time, and nothing is listed not applied for them;
- `Repository.Tests/RecoveryAlternateStreamsTests` holds the recovery tool to the same answers through the helper.

Off Windows no capture carries a stream, so nothing there reaches `restore-file`'s write; its case runs on Windows alone.

## Amendment 3 (2026-10) — the plan names a file whose stream has a name no stream may be written under

**Gap.** §2 judges a stream's name before it is joined to the file's path. A stream whose name would reach another file or the file itself is neither read nor written, and the file is restored degraded. The plan's items carried whether a file had streams, not their names. So the plan said nothing of such a file, and it was found only at execution, where the receipt named it.

**Decision.** Each item's facts now count the captured streams whose names §2's rule (`Domain/AlternateStreams.TryName`) refuses. The plan's probe counts them from the manifest it already reads (`Restore/RestoreBlobSet`). On a target that writes streams, the plan declares the files carrying one on a line of their own, `alternate-stream-names`. The line says that stream will not be written and the file will be restored degraded. On a target that writes none, the planner's own line already speaks for every stream, and no name is judged. The plan does not repeat the names, which the receipt gives and which are the manifest's to say.

**Tests.** `Repository.Tests/RestoreMetadataHonestyTests` holds the plan to it on every platform, against manifests the fake source writes. Of three files, one has a zone identifier, one a stream named `..\..\escape` and one a stream named `..`. On a target that writes streams, the plan declares two of them on the new line. On one that writes none, it declares no such line.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The executor writes a file's alternate streams back on Windows, each proved like a segment, judged by name before it is joined to the path, and written after the content and before the times; one that does not land degrades the file, named. A stream standing as captured is left. `Domain/AlternateStreams`; `Restore/RestoreExecutor`; `Domain.Tests/AlternateStreamsTests`; `Repository.Tests/RestoreAlternateStreamsTests` |
| 2026-10 | Amended | Amendment 1: the recovery tool writes a file's streams back by the same rule, after its content and before its times, and notes one that does not land. `Recovery/RecoverySession`; `Repository.Tests/RecoveryAlternateStreamsTests` |
| 2026-10 | Amended | Amendment 2: `restore-file` writes a file's streams back by the same rule, through one helper the recovery tool now shares, and says each that does not land. `Domain/AlternateStreams`; `Cli/CliApplication`; `Domain.Tests/AlternateStreamsTests`; `Cli.Tests/InspectionCommandTests` |
| 2026-10 | Amended | Amendment 3: the plan names a file whose stream has a name no stream may be written under, from the count each item's facts now carry. `Restore/RestoreMetadata`; `Restore/RestoreBlobSet`; `Repository.Tests/RestoreMetadataHonestyTests` |

# ADR-0192 — A set's status says how many changed files its next backup will take

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-013 (new: a set's status says how many changed files its next backup will take, counted as that backup will count them, or that it will walk in full and why); FR-SNP-013 (amended by [Amendment 1](#amendment-1-2026-11--the-files-the-next-backup-tries-again-are-waiting-too): the files the last backup could not read are counted too, since the next backup tries them again, and where its failures will not read the next backup walks in full and the status says why)
**Related:** [ADR-0186](0186-a-run-reuses-a-folder-it-was-told-did-not-change.md) (a run reuses a folder it was told did not change), [ADR-0188](0188-the-service-tells-a-backup-what-changed-since-the-sets-last.md) (the service tells a backup what changed since the set's last), [ADR-0189](0189-a-sets-full-walk-is-a-setting-of-the-set.md) (a set's full walk is a setting of the set), [architecture 06 §4](../architecture/06-filesystem-capture.md#4-change-detection) (change detection)

**Built:**
- The count: `Repository/SourceComparer` counts the files a backup told which folders changed will write, walking only what that backup will.
- The failures: `Repository/SnapshotFailures` reads the paths a snapshot's failures name, which the next backup walks to try again (Amendment 1).
- The judgement: `Agent/ChangeHints` says what the next backup will be told, or why it will walk in full, for the backup and the count alike.
- The board: `Agent/PendingChangesBoard` keeps each set's last count, and asks for another on the reader lane once it is a minute old.
- The wire: a set's status in `Api/Results` carries the count, at contract 1.83.
- The clients: `Cli/CliApplication` prints a set's `waiting:` token; the console's card says it at a glance and in words.
- The tests:
  - `Repository.Tests/ChangedFilesCountTests` — a file changed in a named folder is counted and a folder nothing named is not entered; a file new, and every file in a folder the last snapshot does not hold, is counted; so is a file changed only in its metadata, and one moved; a file unchanged, or one the rules leave out, is not; nothing named counts nothing; nothing is opened.
  - `Hosts.Tests/PendingChangesTests` — the files changed and new in a named folder are waiting, and none where nothing is named; the files the last backup could not read are waiting though nothing is named, and the next backup takes them; where the last snapshot's failures will not read the next backup walks in full, and they are read once for a snapshot (Amendment 1); the next backup walks in full, and says why, for a set never backed up, one whose full walk is due, and one whose journal cannot say; a set whose backups ask no journal says nothing; a count moves no place; the status gives the last count and asks for another once it is a minute old, never two at once.
  - `Api.Tests/ContractAdditiveFieldsTests`, `Web.Tests/StatusRelayNamesTests` — the wire names at 1.83, the console's names, and an older service's silence.
  - `Cli.Tests/StatusPendingTokenTests` — `waiting:12`, `waiting:0`, `waiting:full-walk`, and nothing where none was counted.
  - `Web.DomTests/PendingChangesDomTests` — the card's count at a glance and in words, the singular, a full walk and why, nothing changed, and nothing for a set that asks no journal.

---

## Context

A backup is told what changed since the set's last one (ADR-0188), on Windows, Linux and macOS alike. The owner asked that the console show how many files have been found changed that the next backup will take.

The journals name folders, not files. A folder named may hold one changed file or a thousand, and a file in it may be new, changed, changed only in its metadata, or not changed at all. Only a walk of the folder tells.

The console asks for status every five seconds, so a count made inside a status would walk every set's changed folders twelve times a minute.

## Decision

### 1. The count is the next backup's walk, made dry

The count walks what the next backup will walk, and judges each file as that backup will judge it.

- **It asks what the next backup will be told.** One judgement, `ChangeHints.WhatChanged`, now serves the backup's plan and the count alike. It moves no place.
- **It enters what folder reuse will enter** (ADR-0186): each folder named, the folders above it, and any folder the last snapshot does not hold, through the scan's own hook. Every other folder is passed over unopened.
- **It judges each file against the last snapshot's version,** found at its path or by its identity as publication finds a prior version, with the backup's own change detection and under the set's rules.
- **It counts a file new, changed, changed only in its metadata, or moved.** Each of those is a version the next backup will write. A file unchanged is not counted.
- **It opens nothing.** An online-only file is not fetched to be counted.

### 2. Where the next backup walks in full, the status says why

The next backup walks every folder rather than those named, and nothing is counted, where:

- the set has no backup yet;
- its last backup kept no place in the change journal, or the places kept are another backup's;
- its full walk is due (ADR-0189);
- its folders changed since its last backup;
- the change journal cannot say what changed.

The reason is given in words for a person.

A set whose backups ask no journal says nothing. Every one of its backups walks in full, and a count would claim a knowledge nothing has.

### 3. A count is kept, and renewed at most once a minute

- **The service keeps each set's last count**, with when it was counted.
- **A status reads the last count.** Where there is none, it is a minute old, or it was counted after the status's own time, the status asks for another and gives the last meanwhile, or none before the first.
- **The count runs on the reader lane,** one at a time for a set, beside a backup.
- **A count that cannot read the source leaves the last one standing.** The next backup reports what it could not read.

### 4. The wire and the clients

- **Contract 1.83:** a set's status carries `pending_changes`: `files`, or `walks_in_full_because` where it walks in full, and `counted_at`. Additive. A service before 1.83 says nothing.
- **The command line:** a set's status line prints `waiting:12`, `waiting:0`, or `waiting:full-walk`.
- **The console:** a set's card says `12 changed` at a glance. Opened, it says beside the next run `12 changed files will be backed up next`, `nothing changed since the last backup`, or `next backup walks in full`, with the reason on hover.

## Consequences

- A person sees what the listener has found before the backup runs, counted as the backup will count it.
- The count is a minute old at most while someone looks, and costs nothing while nobody does.
- A file changed after the count and before the backup is in the backup and not yet in the count. A count is a reading, not a promise.
- The backup's plan and the count share one judgement, so they cannot drift apart.

## What this does not do

- **Count the files the last backup could not read.** The next backup tries them again, and they are not counted. That would need the snapshot's error manifest, which is in the archive's blobs, not the catalogue.

  > **2026-11: done ([Amendment 1](#amendment-1-2026-11--the-files-the-next-backup-tries-again-are-waiting-too)).** The count reads the last snapshot's failures from the archive, and enters the folders they name as the backup does.
- **Count files deleted.** A walk of some folders cannot tell a file gone from one not looked at, and a deletion writes no version.
- **Name the files.** The preview, `What changed?`, walks the whole set and names them.
- **Count for a set whose backups ask no journal.**

## Alternatives considered

- **Count the journal's entries.** Fast, but a journal names folders, and one entry can be a file written ten times or a folder made and gone. It would count what the backup will not write.
- **Count inside each status.** Exact to the moment, but a console asking every five seconds would walk every set's changed folders twelve times a minute.
- **Count after each change the journal hears.** It would need every journal to call out, and Windows' journal is read, not heard.

## Amendment 1 (2026-11) — the files the next backup tries again are waiting too

### Context

The next backup walks every folder its last one failed a file in, to try it again (ADR-0186): a file that would not read, or an online-only file whose provider did not deliver it (ADR-0185). The count entered only the folders the change journal named, so a set whose last backup missed files said nothing was waiting. The failures are in the snapshot's error manifest, in the archive's blobs; the count holds the archive, so it can read them.

### Decision

- **The count reads the last snapshot's failures, as folder reuse reads them,** and enters the folder each lies in beside the folders the journal named. Each such file is judged as the backup will judge it: new where the snapshot holds none of it, changed where it holds the version before (ADR-0185 Amendment 3).
- **Where they will not read, the next backup walks in full,** as folder reuse does, and the status says so: `its last backup's failures would not read`.
- **They are read once for a snapshot,** not once a count. A snapshot never changes, and a direct-ship set's store reads through its destinations, billed by the request.

### Consequences

- A set whose last backup missed files says how many its next backup will try again, and the console's `nothing changed since the last backup` is no longer said of it.
- A file that still will not read is counted at each count until a backup takes it.

### What this does not do

- **Say which of the waiting files are retries.** The count is one number.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first, at contract 1.83. |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--the-files-the-next-backup-tries-again-are-waiting-too): the count enters the folders the last snapshot's failures name, as the next backup does to try them again, and where those will not read the next backup walks in full and the status says why; they are read once for a snapshot. `Repository/SnapshotFailures`; `Agent/PendingChangesBoard`; `Hosts.Tests/PendingChangesTests`; `Repository.Tests/OnlineOnlyCaptureTests` |

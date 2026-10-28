# ADR-0135 — Direct mode resumes within a file

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: a restore in direct mode cut short is resumed from what it had assembled of a file, as one through the service is), FR-RST-003 (amended: in direct mode the working space is where its parts are kept, and a resume's plan reads nothing they prove)
**Related:** [ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md) (a restore cut short resumes within a file, and the record that left direct mode out), [ADR-0109](0109-a-restore-cut-short-is-resumed.md) (a restore cut short is resumed, in direct mode where `--output` points), [ADR-0131](0131-the-service-measures-a-restores-working-space-where-its-parts-are-kept.md) (the working space where the parts are kept), [ADR-0132](0132-a-resumes-plan-counts-only-what-it-still-reads.md) (a resume's plan counts only what it still reads), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The parts: `Cli/DirectRestoreParts` names the folder a run in direct mode keeps its parts in, under the state directory, by where the run lands, and lets it go.
- The run: `Cli/OperationGateway`'s direct gateway gives each run that folder, takes the parts up on a resume as the executor does through the service, lets them go when the run finishes, and says how many files it resumed within. Its plan of a resume reads nothing the parts prove, and its probe of room measures the working space where they are kept.
- The tests:
  - `Cli.Tests/RestoreWithinAFileDirectTests` — a resume takes up the parts its destination keeps, reading none of them again, says so, and lets them go when it finishes; a run that is not a resume takes up no part; a run that does not finish keeps them until a run there does; each destination keeps its own, apart from the service's runs; a resume's plan reads nothing its parts prove; and the room to assemble a file is measured where the parts are kept

---

## Context

Through the service, a run assembles each file in a part under the state directory, kept under the run's name, and a resume takes the part up as far as it proves and reads only the rest ([ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md)). A run in direct mode kept no parts. Its engine assembled each file in a spool of its own in the system's temporary folder and deleted the spool however the file ended. So `restore --resume` in direct mode found the files the run had landed ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)) and read again all of the one it was cut short in. ADR-0129 recorded this as undone.

A run in direct mode has no name a resume could ask by: it lands in place where `--output` points, and a resume is the same command with `--resume`. What it does have is where it lands.

## Decision

### 1. A run in direct mode keeps its parts by where it lands

Each run in direct mode assembles every file in a part, in a folder under the command line's state directory named by where the run lands: `direct-restore-parts/{destination}`, the first 32 hex digits of the SHA-256 of its full output paths. A run to the original location of a set with several roots keeps one folder for all of them. The folder sits apart from the service's `restore-parts`, whose start sweeps away every run it has no record of. A part holds the backup's plaintext, as the service's do, so it stays under the state directory and never where the run writes.

### 2. A resume there takes them up, and a run there that finishes lets them go

A resume to the same place takes each part up as far as it proves, by the rule the service's resume and the plan share, and reads only the rest. A run that is not a resume takes up no part. A run that completes, or skips only what the target cannot hold, is finished, as the service has it ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)), and its parts go. A run that does not finish keeps them for a resume there.

### 3. The rest follows the service's records

- **What it says.** A resume says how many files it resumed within, as it does through the service ([ADR-0130](0130-a-restore-says-which-files-it-resumed-within.md)), and the receipt says where each was resumed.
- **What its plan reads.** A resume's plan reads nothing its parts prove ([ADR-0132](0132-a-resumes-plan-counts-only-what-it-still-reads.md)).
- **Where it needs room.** The room to assemble the largest file is measured where the parts are kept ([ADR-0131](0131-the-service-measures-a-restores-working-space-where-its-parts-are-kept.md)), since every file is assembled in one.

## Consequences

- A large file a run in direct mode was cut short in is read again only from where the run stopped.
- A run there that does not finish leaves, under the state directory, as much of the file it was cut short in as it had assembled, until a run there finishes.

## What this does not do

- **Sweep the parts of a destination no run finishes.** Direct mode keeps no record of its runs, so nothing knows a folder is no longer wanted. A run there that finishes lets it go, and so does deleting the folder.
- **Keep two runs at once to one destination apart.** They share its parts, as they share the files they write.
- **List what a destination holds,** as a run left unfinished does through the service ([ADR-0133](0133-a-run-left-unfinished-says-what-its-parts-hold.md)). Direct mode lists no runs.

## Alternatives considered

- **Keep the parts in the service's `restore-parts`.** Rejected. A service started on the same state sweeps every folder it has no run on record for.
- **Name a run in direct mode, as the service does.** Rejected. A resume in direct mode is the same command run again with `--resume`, and asking a person for a name they were never given would be a new step for nothing a destination does not already say.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A run in direct mode keeps its parts under the state directory by where it lands, a resume there takes them up and says how many files it resumed within, and a run there that finishes lets them go. Its plan of a resume reads nothing they prove, and its working space is measured where they are kept. No contract change. |

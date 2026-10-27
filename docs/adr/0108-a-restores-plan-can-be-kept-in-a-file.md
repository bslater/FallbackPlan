# ADR-0108 — A restore's plan can be kept in a file

**Status:** Accepted (amended 2026-10: [Amendment 1](#amendment-1-2026-10--one-shape-for-every-client-and-the-run-as-asked))
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: the command line keeps a plan in a file of its own as JSON, refusing one already there, and restores nothing)
**Related:** [ADR-0107](0107-the-command-line-plans-a-restore-on-its-own.md) (the plan the command line prints, and the record that left export owed), [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (the plan, and export and resume left owed), [ADR-0081](0081-diagnostic-bundle.md) (a file the command line writes, and one already there left alone), [ADR-0089](0089-a-backups-file-names-need-the-passphrase.md) (a backup's file names need the passphrase), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The verb: `Cli/CliApplication`'s `restore` takes `--export <file>`, which implies `--plan` and refuses a file already there before anything is asked.
- The file: `Cli/RestorePlanExport` writes the plan beside the run it was made for, in the contract's own words, creating the file new or not at all. `Cli/RestorePlanReport` keeps it where the request names a file, whichever gateway made the plan.
- The shape, since [Amendment 1](#amendment-1-2026-10--one-shape-for-every-client-and-the-run-as-asked): `Api/RestorePlanFile`'s, version 2, which the console keeps too ([ADR-0113](0113-the-console-keeps-a-restores-plan.md)).
- The tests:
  - `Cli.Tests/RestorePlanCommandTests` — in direct mode a kept plan holds the plan's figures, its missing files among them, and the run it was made for, and exits as the plan does; `--export` alone plans and restores nothing; a file already there is refused and left as it was, before anything is asked, and one that appears between that check and the write is refused too
  - `Cli.Tests/GatewayRestoreReportTests` — through the service, the kept plan holds what the service answered

---

## Context

`restore --plan` prints a plan ([ADR-0107](0107-the-command-line-plans-a-restore-on-its-own.md)), and the shell keeps it only as long as its scrollback does. Architecture 08 §2 asks that plans be exportable: kept beside a change, sent to whoever decides whether to run it, or read by a script after the fact. ADR-0107 left that owed.

A plan names the backup's files. It is made under the passphrase, through a source it unlocked, and it is printed to the person who typed it ([ADR-0089](0089-a-backups-file-names-need-the-passphrase.md)).

## Decision

### 1. `--export <file>` keeps the plan, and implies `--plan`

`restore --export <file>` prints the plan as `--plan` does and keeps it in the file. It never restores, with or without `--plan` beside it, because a plan kept is a plan and not a run.

### 2. The file is the plan in the contract's own words

The file is JSON. It holds:
- what it is, `fallbackplan-restore-plan`, and its shape's version, 1;
- the contract the plan was answered under;
- the snapshot, the path within it and the destination the run was planned for, and when;
- the plan itself, field for field as `restore_plan` crosses the wire.

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--one-shape-for-every-client-and-the-run-as-asked)).** Version 2 keeps these fields with their meaning and adds the run as asked beside one path: its paths, its target, its existing-file policy and whether it lands in place. The restore source the plan was asked through is not kept.

A reader of the file reads the fields a client of the contract does, and the contract register already says what each one means.

### 3. It is written where the command was typed

The plan comes back to the command line through either path, so the file is written on the machine running it. That is where the person who unlocked the plan asked for it. It names the files the printed plan names, and nothing more.

### 4. A file already there is left alone

A file already at that path is somebody's. It is refused before anything is asked, as `diagnostics-export` refuses one ([ADR-0081](0081-diagnostic-bundle.md)). The file is then created new or not at all, so one that appears between the check and the write is left alone too.

## Consequences

- A plan can be kept, sent, and read by a script without parsing the printed words.
- The file's shape follows the contract's. A field the contract adds appears in the file, and the file's version moves only when its own envelope changes.

## What this does not do

- **Run a kept plan.** A plan is a picture of the run at the time it was made. Running one later means planning again, because the replica, the destination and what is free may all have moved.
- **Resume a restore.** A restore cut short is run again from the start, as before.

  > **2026-10 ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)).** A restore cut short is resumed with `restore --resume`. A resume is not kept as a plan: `--export` beside it is refused.
- **Keep a plan from the console.** The wizard shows its plan, and keeping one from there is not built.

  > **2026-10 ([ADR-0113](0113-the-console-keeps-a-restores-plan.md)).** The wizard keeps the plan on screen as this file, which the console's endpoint gives the browser to save.

## Alternatives considered

- **Print the plan as JSON instead of words.** Rejected. A person reading the plan reads words, and a script reading the file reads fields. One flag gives both.
- **Write the file whatever is there.** Rejected. A plan names a backup's files, and a file at that path may be somebody else's.
- **A shape of the file's own.** Rejected. It would be a second description of the same plan, and the two would drift.

## Amendment 1 (2026-10) — one shape for every client, and the run as asked

[ADR-0113](0113-the-console-keeps-a-restores-plan.md) has the console keep a plan too. The wizard asks for a run the command line does not: several paths, a target, an existing-file policy, and a landing in place. A file of version 1 would describe a different run from the one on screen.

**Decision.** The file's shape moves into the contract's assembly (`Api/RestorePlanFile`), and both clients write it. It is version 2:
- **Every field of version 1 stays,** with the meaning it had: the kind, the version, the contract, the snapshot, the one path, where the run writes, when, and the plan.
- **The run as asked is added:** `paths`, where it asked for several; `target`; `existing`; and `in_place`. Each is null, or false, where the run did not ask it.
- **The restore source is not kept.** It is an open handle, and means nothing once it closes.

The command line describes the run its command plans as the service is asked it, and writes version 2.

**Consequences.** A script that read version 1 reads the same fields with the same meaning. One that checks the version is told the shape grew, and reads the run as asked from the added fields.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. `restore --export <file>` keeps the plan as JSON, in the contract's own words beside the run it was made for, implies `--plan` so nothing is restored, and refuses a file already there. Running a kept plan, and resume, stay owed. |
| 2026-10 | Accepted | [ADR-0109](0109-a-restore-cut-short-is-resumed.md) resumes a restore cut short, which this record left owed. A resume is not planned, so `--export` beside `--resume` is refused. |
| 2026-10 | Accepted; built (amended) | [Amendment 1](#amendment-1-2026-10--one-shape-for-every-client-and-the-run-as-asked): the file's shape moves into the contract's assembly at version 2, which keeps version 1's fields and adds the run as asked (paths, target, policy, in place), and leaves out the source. Both clients write it, the console since [ADR-0113](0113-the-console-keeps-a-restores-plan.md). |

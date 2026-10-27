# ADR-0114 — A kept plan is run as it was approved

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: a restore held to a kept plan runs only the run it was kept for, and only while planning it again says what the kept plan said)
**Related:** [ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md) (a restore's plan kept in a file, and the record that left running one owed), [ADR-0113](0113-the-console-keeps-a-restores-plan.md) (the console keeps one too, in the same shape), [ADR-0107](0107-the-command-line-plans-a-restore-on-its-own.md) (the plan the command line prints), [ADR-0109](0109-a-restore-cut-short-is-resumed.md) (a resume, which is not planned), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The reader: `Api/RestorePlanFile` reads a kept plan back, of either version, as the run it was kept for and the plan.
- The verb: `Cli/CliApplication`'s `restore` takes `--as-planned <file>`, which holds the run asked to the file's and refuses one the command line does not ask, before anything is asked.
- The check: `Cli/RestorePlanReport` holds a plan made now to the kept one, whichever gateway made it, and names each change.
- The tests:
  - `Api.Tests/RestorePlanFileTests` — a kept plan reads back as its run and its plan, never a source; a version-1 file reads back as a run of one path; a file of another kind, a newer version, no plan, or not JSON is refused, saying why
  - `Cli.Tests/RestoreAsPlannedCommandTests` — in direct mode the run a plan was kept for restores; one whose content has gone since names what is missing now and writes nothing; another run, a console's plan of several paths, a file that is not a plan, and the flag beside `--plan` or `--resume` are refused

    > **2026-10 ([ADR-0117](0117-the-command-line-asks-a-restore-as-the-console-does.md)).** A console's plan asked as it was kept now runs, and asked otherwise is refused as another run, naming how it was kept.

  - `Cli.Tests/GatewayRestoreReportTests` — through the service a plan held to a kept one names each change and runs nothing, and an unchanged one says so even where it would fail files

---

## Context

A plan is kept so that someone can decide whether to run it: beside a change, sent to whoever decides, or read by a script ([ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md)). Once they had decided, nothing tied the run to what they had read. `restore` planned afresh and ran whatever it found.

The world a plan describes moves. A replica can lose content, or a store can move it to an archival tier, and files can arrive where the restore writes. Each was learned from the run, after the decision had been made on a picture that no longer held. ADR-0108 and [ADR-0113](0113-the-console-keeps-a-restores-plan.md) both left running a kept plan owed.

## Decision

### 1. `restore --as-planned <file>` holds a restore to a kept plan

The flag names a kept plan, and the restore it goes with runs only as that plan says. It cannot go beside `--plan` or `--export`, which run nothing, or `--resume`, which is not planned.

### 2. The file is read, and held to the run asked, before anything is asked

- **Read back,** of either version, as the run it was kept for and the plan. A file that is not a kept plan, is of a version this build does not read, or holds no plan is refused, saying which.
- **The run must be the run it was kept for:** the same snapshot, the same path within it, and the same destination. Another run is refused, naming the one the file keeps.
- **A run the command line does not ask is refused by name:** several paths, a target, an existing-file policy, a landing in place. The console keeps plans of such runs.

  > **Superseded 2026-10 ([ADR-0117](0117-the-command-line-asks-a-restore-as-the-console-does.md)).** The command line asks each of those, so no kept run is refused for its shape. The run asked is held to the kept one field by field, and another is refused as another run, naming how the plan was kept.

### 3. It is planned again, and held to what the kept plan said

The restore is planned again, through the service or in direct mode, and the plan made now is held to the kept one:
- the files it restores;
- what is missing;
- what is held only in an archival tier;
- the conflicts.

**Unchanged means run.** The restore then runs as `restore` runs it. Its lines follow one saying the plan is as kept, and when it was planned. What the kept plan already said would fail was part of what was approved, and is not news.

**Changed means nothing is written.** Each change is named, a name at a time, each way: a file missing now or no longer, archived now or no longer, and the counts of files and conflicts. Nothing is restored, not even the folder, and the person is told to plan again.

**Room is left to the run.** It refuses a restore that will not fit unless told otherwise, whatever the kept plan said of room.

## Consequences

- The run that happens is the run that was approved, or nothing happens and the person learns why.
- A script can keep a plan, have it approved, and run it later knowing it will not do something else.
- A run held to a plan costs a second plan, as a plan kept from the console does.

## What this does not do

- **Run a console's plan from the command line.** The command line asks one path, so a plan of several paths, a target, a policy or a landing in place is refused rather than run as some other run.

  > **2026-10 ([ADR-0117](0117-the-command-line-asks-a-restore-as-the-console-does.md)).** The command line asks several paths, the original location, a policy and a landing in place, so a console's plan runs from it where it asks the run as kept.
- **Run a kept plan from the console.**
- **Hold room to the kept plan.** Free space moves by the minute, and the run's own refusal of a restore that will not fit is the check that matters.
- **Hold the bytes written or read to the kept plan.** They follow from the files, which are held.

## Alternatives considered

- **Run the kept plan without planning again.** Rejected. A plan is a picture of the run when it was made, and the replica, the store and the target may all have moved since.
- **Refuse whatever the plan now says would fail.** Rejected. A plan approved knowing a file would fail is approved, and refusing it would make an approved restore impossible to run.
- **Read the run from the file and ask nothing on the command line.** Rejected. The person names the run they mean, and the file is held to it: a file swapped for another one is refused rather than run.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. `restore --as-planned <file>` reads the kept plan back, holds it to the run asked before anything is asked, plans again through either gateway, and runs only where the files, what is missing, what is held archived and the conflicts are what the kept plan said. Otherwise it names each change and writes nothing. |
| 2026-10 | Accepted (amended) | [ADR-0117](0117-the-command-line-asks-a-restore-as-the-console-does.md): the command line asks the run the console asks, so §2's refusal of a run it does not ask is superseded; the run asked is held to the kept one field by field, and a console's plan runs from the command line. |

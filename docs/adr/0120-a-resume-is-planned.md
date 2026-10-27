# ADR-0120 — A resume is planned

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: a resume is planned, as it will meet what its run landed), FR-RST-003 (a plan says what its run would do, for a resume too)
**Related:** [ADR-0109](0109-a-restore-cut-short-is-resumed.md) (a restore cut short is resumed, and the record that refused a plan of one), [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md) (a run named and resumed by its name alone), [ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md) (the record that left the plan of a resume owed), [ADR-0107](0107-the-command-line-plans-a-restore-on-its-own.md) (the plan the command line prints), [ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md) (a kept plan), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The measure: `Restore/RestoreSpace` counts the files a resumed run already landed where it looks, at their length, which need no room.
- The contract (1.72): `Api/PlanRestoreCommand` takes `resume`, the run the planned run continues. `Api/PlanResumeRestoreCommand` plans a run left unfinished by its name alone, and `Api/RestorePlanResult` says how many files the run landed.
- The service: `Agent/ServiceCommandHandler` measures a plan of a resume where the run landed, as the resume does, and refuses what the resume would refuse. It plans a run left unfinished as it was asked.
- The command line: `Cli/CliApplication`'s `restore --resume --plan` and `restore-resume <run> --plan`; `Cli/OperationGateway` asks them through the service and plans the first in direct mode; `Cli/RestorePlanReport` says what the run landed.
- The tests:
  - `Repository.Tests/RestoreSpaceTests` — a resume counts the files already there at their length, and a run that resumes nothing counts none
  - `Hosts.Tests/RestoreUnfinishedServiceTests` — a plan of a resume is measured where the run landed and says what it landed, running nothing, and a plan afresh names nothing landed; a run left unfinished is planned by its name alone as it was asked, under the session that asks it; a plan of a run whose folder is gone, of a run not on record, or of what is not a run is refused; `restore-resume <run> --plan` says what the run landed and resumes nothing
  - `Cli.Tests/RestoreResumeCommandTests` — in direct mode a plan of a resume says how many files the run landed and restores nothing, fits where only what is left fits, names no run, and is not kept
  - `Cli.Tests/GatewayRestoreReportTests` — through the service a plan of a resume asks for the run it continues, one naming no run is refused, and a run left unfinished is planned by its name alone through a source of its snapshot
  - `Cli.Tests/RestoreResumeVerbTests` — `restore-resume --plan` names its run, is not asked beside `--forget`, and in direct mode names none
  - `Api.Tests/ConfigurationContractTests` — contract 1.72's wire names

---

## Context

A restore cut short is resumed ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)), through the service by its name alone ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)), and from the console ([ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md)). It could not be planned. ADR-0109 §5 refused a plan of a resume, on the ground that saying which files a resume would find in place means hashing them, which is most of the resume's work.

That left a person resuming a large run with no way to ask, before it began, whether it would fit or what it would read. A plan of the same run afresh said it would not fit where the resume would, because a run afresh needs room for every file.

## Decision

### 1. A plan of a resume goes by length, as the resume's measure does

The resume is already measured by length (ADR-0109 §4): a file where it lands at the snapshot's length needs no room, and the run checks its hash only when it reaches it. A plan of a resume is measured the same way, so it needs no hashing and no read:
- **The room** is measured where the run landed, as the resume will meet it.
- **The files the run landed** at their length are counted, and the plan says how many. It also says that the resume checks each by its hash and leaves it as it stands, so the count is never claimed for more than it is.
- **Everything else** the plan says is the run's: the files, what they write and read, and what cannot come back.

### 2. Planned for the run it continues, or by the run's name alone

- **`restore --resume --plan`** plans the run asked, as a resume. Through the service it names the run it continues, and in direct mode it lands in place and names none, as the resume does.
- **`restore-resume <run> --plan`** asks the service to plan a run left unfinished as it was asked, by its name alone.

Refused as the resume would be: a resume naming no run through the service, a quarantined run whose folder is gone, a run not left unfinished, and a name that is not a run's.

### 3. A plan of a resume is not kept

A kept plan is of a run a person approves before it begins ([ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md)), and a resume continues one already begun. `--export` beside `--resume` is still refused, in those words.

## Consequences

- A person resuming a large run asks first whether it fits, and hears how much of it is already there.
- A plan of a resume says what the resume will say: the measure is the same, by length, in the same code.

## What this does not do

- **Hash what the run landed.** The plan says what lies there at its length. A file of that length whose content differs is landed under the policy when the resume reaches it, as ADR-0109 §4 says.
- **Plan a resume from the console.** The console resumes a run by its name alone, and plans none.

  > **2026-10 ([ADR-0121](0121-the-console-plans-a-resume.md)).** The console plans the resume before it runs it, by the run's name alone, and arms one that will not fit only when the person says to go on.
- **Keep the plan of a resume** (§3).

## Alternatives considered

- **Keep the refusal.** Rejected. Its reason was the hashing, which the resume's own measure never does.
- **Hash every landed file in the plan.** Rejected, for ADR-0109's reason: it is most of the resume's work, done twice.
- **Plan a run left unfinished from the list's description.** Rejected. The list names no paths, so a client could not ask the run as it was asked, only the service can.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first, at contract 1.72. A plan of a resume is measured where its run landed, by length, as the resume is, and says how many files the run already landed. `restore --resume --plan` plans the run asked, through the service or in direct mode, and `restore-resume <run> --plan` plans a run left unfinished by its name alone; neither restores anything, and a plan of a resume is not kept. |
| 2026-10 | Accepted | [ADR-0121](0121-the-console-plans-a-resume.md): the console plans a resume too, which this record left owed. |

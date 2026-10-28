# ADR-0133 — A run left unfinished says what its parts hold

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: each run left unfinished says what the parts it kept hold, and the command line and the console say it)
**Related:** [ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md) (a restore cut short resumes within a file, from the parts its run kept under the state directory), [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md) (the service lists the runs left unfinished, and lets one go), [ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md) (the console lists them), [ADR-0131](0131-the-service-measures-a-restores-working-space-where-its-parts-are-kept.md) (the room the parts need), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The contract (1.77): `Api/Results`' unfinished-run descriptor says what the parts it kept hold.
- The service: `Agent/RestoreParts` sums a run's parts, and `Agent/ServiceCommandHandler` lists each run with it.
- The clients: `Cli/OperationGateway`'s listing of the runs left unfinished, and the console script of `FallbackPlan.Web`, say what each holds.
- The tests:
  - `Hosts.Tests/RestoreUnfinishedServiceTests` — a run that kept no part holds nothing, one that kept parts says what they hold, and `restore-resume` says it
  - `Api.Tests/ConfigurationContractTests` — contract 1.77's wire name, and a pre-1.77 run that holds none
  - `Cli.Tests/RestoreResumeVerbTests` — a run listed says what its parts hold, and one holding nothing says nothing of it
  - `Web.DomTests/RestoreUnfinishedDomTests` — the console's list of runs left unfinished says what each holds

---

## Context

Through the service, a run assembles each file in a part under `restore-parts/{run}` in the state directory, and keeps the part of each file it was cut short in, so a resume can take it up ([ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md)). A run left unfinished keeps its parts until it is resumed or let go. Each is the backup's plaintext, as much of a file as the run had assembled, and the parts of a large file can take a great deal of room. ADR-0129 left the run's notice to say that the run is there, and nothing said how much it holds. A person choosing whether to resume a run or let it go could not see what letting it go would free.

## Decision

### 1. Each run left unfinished says what its parts hold

The service's list of the runs left unfinished gains, for each, `held`: the bytes of the parts it kept (contract 1.77). They are summed from the run's own folder when the list is asked for, so they are what lies there now. A run that kept no part, or whose parts will not be read, holds none.

### 2. The command line and the console say it

`restore-resume`, listing the runs left unfinished, and the console's list of them on its snapshots page each say what a run holds. A run that holds nothing says nothing of it.

## Consequences

- A person can see what letting a run go would free under the state directory, and what a resume would take up rather than read again.
- The list names no file still: what a run holds is a count of bytes.

## What this does not do

- **Say it in the run's notice.** The notice says the run is there, and leads to the list, which says what it holds as it is asked.
- **Say which files the parts are of.** Their names are the files' whole-file hashes, and which files those are is the backup's to say, through the passphrase.
- **Say it in direct mode.** Direct mode keeps no parts.

## Alternatives considered

- **Keep the total in the run's record.** Rejected. The record is written as a run begins and ends, and a part grows while it runs. A total read from the folder is what lies there.
- **Count the parts in files.** Rejected. A person deciding what to free asks how much room a run takes, and a part can be a few bytes or most of a large file.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Each run left unfinished says what the parts it kept hold (contract 1.77's `held`), summed from its own folder as the list is asked for; `restore-resume` and the console's list say it, and a run holding nothing says nothing of it. |

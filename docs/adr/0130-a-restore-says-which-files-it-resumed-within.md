# ADR-0130 — A restore says which files it resumed within

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: the receipt says where each file was resumed), FR-RST-008 (amended: a resume's result counts the files it resumed within, and the command line and the console say it)
**Related:** [ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md) (a restore cut short resumes within a file, and the record that left the receipt silent on it), [ADR-0109](0109-a-restore-cut-short-is-resumed.md) (a file found in place is said to be, at receipt schema 8), [ADR-0026](0026-phase-1-capture-shapes.md) (the receipt is versioned JSON, Decision 10), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The engine: `Repository/RestoreEngine` says how many of a file's bytes it took up from its part, on `Repository/RestoreResult`.
- The receipt: `Restore/RestoreExecutor`'s receipt is at schema 11, and each file a resume wrote from its part says where it was resumed.
- The contract (1.76): the restore result of `Api/Results` counts the files a resume wrote from their parts.
- The service: `Agent/ServiceCommandHandler` counts them from the receipt.
- The clients: `Cli/OperationGateway` and the console script of `FallbackPlan.Web` say how many there were.
- The tests:
  - `Repository.Tests/RestoreWithinAFileTests` — the receipt says where a file was resumed, at schema 11, a file that lands degraded included, and nothing of one written whole; the engine says how much of a file it took up
  - `Hosts.Tests/RestoreUnfinishedServiceTests` — a resume's result counts the files it took up from their parts and the receipt says where each was resumed, a run that resumes nothing says none, and `restore-resume` says how many
  - `Api.Tests/ConfigurationContractTests` — contract 1.76's wire name, and a pre-1.76 result that says none
  - `Web.Tests/ConsoleRestoreResultScriptTests`, `Web.DomTests/RestoreWizardDomTests` — the wizard's result says how many files a resume took up from their parts

---

## Context

A resume takes up the part of each file its run was cut short in, as far as the part proves, and reads only the rest ([ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md)). The receipt counted such a file restored, as it counts any file the run wrote. A file found in place says so ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)), and a resume's result counts those. A file resumed within said nothing. A person who resumed a large run had no way to see that it had picked up where it stopped. ADR-0129 recorded this as undone.

## Decision

### 1. The receipt says where each file was resumed, at schema 11

Each file a resume wrote from its part gains `resumed_at`: how many of its bytes were taken up from the part, and not read again. It is written only where some were, so a file written whole says nothing of it. Schema 11 is the version that added it, as every field the receipt gained has had one.

### 2. A resume's result counts them

The service's result of a restore gains `resumed_within`: how many of the files it restored it wrote from a part (contract 1.76). They are counted among the files restored, as the files found in place are.

### 3. The command line and the console say it

`restore` and `restore-resume` through the service, and the console's result of a restore, each say how many files were resumed from what the run had assembled of them.

## Consequences

- A person who resumes a run cut short can see that it picked up the files it was writing where it stopped, and from the receipt how far into each.
- A file found in place, a file resumed within and a file written whole are told apart by the receipt and the result alike.

## What this does not do

- **Say it in direct mode.** Direct mode keeps no parts (ADR-0129), so it resumes no file within.
- **Say it in the listing of kept receipts.** The listing counts what became of each run's files, and a file resumed within is a file restored.
- **Name a receipt kept before schema 11.** It says nothing of where a file was resumed, as nothing was.

## Alternatives considered

- **Count the bytes not read again in the result.** Rejected. A person asks which files were picked up; the receipt says how far into each.
- **Mark the file as found in place.** Rejected. A file found in place was not written by this run at all, and one resumed within was.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The receipt at schema 11 says where each file a resume wrote from its part was resumed; contract 1.76's `resumed_within` counts those files on a restore's result; `restore`, `restore-resume` and the console's result say how many. |

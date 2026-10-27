# ADR-0118 — The console runs a kept plan as it was approved

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: the console runs a kept plan only as it was approved, held to it as the command line holds one)
**Related:** [ADR-0114](0114-a-kept-plan-is-run-as-it-was-approved.md) (a kept plan run as approved from the command line, and the record that left the console's run owed), [ADR-0113](0113-the-console-keeps-a-restores-plan.md) (the console keeps a plan in the file both clients keep), [ADR-0117](0117-the-command-line-asks-a-restore-as-the-console-does.md) (the command line asks the console's runs), [ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md) (the wizard's resume mode, which this one sits beside), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the guided restore), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The comparison: `Api/RestorePlanFile` holds a plan made now to a kept one, and `Cli/RestorePlanReport` says the same changes in the same words.
- The endpoints: `Web/WebConsoleHost`'s `/api/restore-plan-read` reads a kept plan with the reader the command line uses, asking the service nothing. `/api/restore-plan-held` plans the kept run again through the source the person unlocked, under their session, and says what changed.
- The page: the snapshots page takes a kept plan's file, and the wizard runs it in a mode of its own.
- The tests:
  - `Api.Tests/RestorePlanFileTests` — a plan made now names each change from the kept one, each way, and one as kept names none even where it would fail files
  - `Web.Tests/ConsoleKeptPlanTests` — a kept plan reads as its run and asks the service nothing; a file that is not one is refused, saying why; a plan held to it is asked as the kept run through the source unlocked and under the session, and runs nothing; one that changed says each change; a refused plan gives its answer; without the token nothing is read or asked
  - `Web.DomTests/RestoreKeptPlanDomTests` — a kept plan is run as it was kept, under the typed word, where nothing changed; one that changed names each change and offers nothing to run; a file that is not a kept plan, a backup no set here holds, and a source that does not hold it each run nothing

---

## Context

A plan is kept so somebody can approve it, and the command line runs a kept plan only as it was approved ([ADR-0114](0114-a-kept-plan-is-run-as-it-was-approved.md)). It asks the run of either client since [ADR-0117](0117-the-command-line-asks-a-restore-as-the-console-does.md). The console keeps plans ([ADR-0113](0113-the-console-keeps-a-restores-plan.md)) but could not run one, so a plan approved from the console had to be run from a terminal.

## Decision

### 1. The console process reads the file

The snapshots page takes a kept plan's file from the person's machine. The console process reads it with the reader the command line uses, so the two clients take the same files. A file that is not a kept plan is refused in the words the command line refuses it. Reading asks the service nothing.

### 2. The run is the one the plan was kept for

The wizard opens in a mode of its own:
- **Unlock:** the passphrase, as any restore asks it.
- **Source:** the set whose backup the plan restores, fixed, and whichever source of it the person chooses.
- **Refused before anything is planned:** a backup no set here holds, and a source that does not hold it.

### 3. It is planned again, and held to the kept plan

The console process asks the service the kept run's plan through that source, under the person's session, and holds it to the kept one. The comparison is the command line's, now shared: the files, a name missing or held archived each way, and the conflicts. Only a plan is asked; nothing runs.

- **Unchanged:** the wizard says the plan is as kept, and what it restores, and offers the run under the typed word. The run is asked exactly as it was kept.
- **Changed:** each change is named, and nothing can be run. The person is told to plan the restore again.
- **Room** is left to the run, as ADR-0114 left it. The run refuses a restore that will not fit unless the person says to go on.

### 4. The file input outlives the page's redraws

The page redraws its views on a poll. A file input inside one would be replaced while its picker was open, and the file chosen would land nowhere. So the input stands outside every view, and the snapshots page offers it.

## Consequences

- A plan approved from the console runs from the console, held to what was approved.
- The two clients read the same files and say the same changes in the same words, because they share the reader and the comparison.

## What this does not do

- **Change the run.** A plan kept is run as kept. One the person wants otherwise is planned again.
- **Keep the file anywhere.** The console reads it for the run and keeps nothing.
- **Hold room or bytes to the kept plan** (ADR-0114).

## Alternatives considered

- **Read and compare in the page.** Rejected. A second reader and a second comparison would drift from the command line's, and the two clients would then disagree about the same file.
- **Walk the whole wizard, prefilled with the plan's choices.** Rejected, for ADR-0116's reason: each choice could be changed, and a changed choice is another run.
- **Run without planning again.** Rejected, for ADR-0114's reason: the replica, the store and the target may all have moved since.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The console process reads a kept plan with the command line's reader and holds a plan made now to it with the comparison both clients now share. The wizard runs it, from a source of the plan's own set, only where nothing changed, asked exactly as kept; otherwise it names each change and runs nothing. |

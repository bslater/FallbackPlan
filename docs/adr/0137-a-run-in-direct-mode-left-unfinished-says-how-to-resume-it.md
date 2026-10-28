# ADR-0137 — A run in direct mode left unfinished says how to resume it

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: a restore in direct mode that does not finish says how to resume it, and what its parts keep and where)
**Related:** [ADR-0109](0109-a-restore-cut-short-is-resumed.md) (a restore cut short is resumed, in direct mode where `--output` points), [ADR-0110](0110-the-console-resumes-a-restore-cut-short.md) (Decision 3: through the service, the command line says how to resume a run), [ADR-0133](0133-a-run-left-unfinished-says-what-its-parts-hold.md) (a run left unfinished through the service says what its parts hold), [ADR-0135](0135-direct-mode-resumes-within-a-file.md) (direct mode keeps its parts by where a run lands, and the record that left them unlisted), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The parts: `Cli/DirectRestoreParts` says how many bytes a destination's folder holds.
- The run: `Cli/OperationGateway`'s direct gateway, for a run that does not finish, says to run it again with `--resume`, and how much of the backup's files its parts keep and where, where they keep any.
- The tests: `Cli.Tests/RestoreWithinAFileDirectTests` — a run that does not finish says how to resume it and what its parts keep and where; one that assembled nothing says only how to resume it; and one that finishes, or skips only what the target cannot hold, says nothing of a resume

---

## Context

Through the service, a run that failed or was stopped says to run it again with `--resume` and its name ([ADR-0110](0110-the-console-resumes-a-restore-cut-short.md)). A run left unfinished there is listed, and the list says what its parts hold ([ADR-0133](0133-a-run-left-unfinished-says-what-its-parts-hold.md)).

A run in direct mode said neither. It is resumed by the same command with `--resume` ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)), which nothing it printed said. Since [ADR-0135](0135-direct-mode-resumes-within-a-file.md), it also keeps the parts of the files it assembled under the state directory until a run there finishes. Those parts are the backup's plaintext, outside the folder a person restored to. Direct mode lists no runs, so nothing said they were there. ADR-0135 recorded this as undone.

## Decision

### 1. A run that does not finish says how to resume it

A run in direct mode that does not finish says to run it again with `--resume`. A run is finished as ADR-0135 has it: one that completed, or skipped only what the target cannot hold. Any other run that ends with its receipt says it.

### 2. It says what its parts keep, and where

Where the folder of its destination holds any parts, the same line says how many bytes of the backup's files they keep, the folder, and that they stay until a run there finishes. The figure is what lies in the folder as the run ends, as the service's list counts a run's parts as it is asked.

> **2026-10 ([ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md)).** After "until a run there finishes", the line names the command that lets them go: `restore-parts --state` with the state directory, and `--forget` with the folder's name. The figure counts the folder's parts alone, not the record each run now keeps beside them.

## Consequences

- A person whose restore in direct mode did not finish learns how to resume it, and that some of their backup's plaintext stays under the state directory.
- A part left by an earlier run to the same place is counted with this run's, since they share the folder.

## What this does not do

- **List what each destination keeps.** Direct mode keeps no record of its runs. A run says what its destination keeps only as it ends.

  > **2026-10 ([ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md)).** `restore-parts` lists them now, from a record each run keeps beside its parts.
- **Say it of a run stopped before it answers.** A run the process does not see end, or one cancelled before its receipt is made, prints nothing. Its parts stay, as ADR-0135 has it.

  > **2026-10 ([ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md)).** It still prints nothing, but `restore-parts` lists its destination, from the record the run wrote before it assembled anything.

## Alternatives considered

- **Delete the parts of a run that does not finish.** Rejected. A resume takes them up, which is what ADR-0135 keeps them for.
- **Keep a record of each direct run, as the service does.** Rejected for now. Saying what is kept as the run ends needs none. A listing would need one, and a way to let a destination's parts go.

  > **2026-10 ([ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md)).** Taken up with the listing: each run keeps one beside its parts, naming none of the backup's files, and the line a run that does not finish prints ends with the command that lets them go.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A run in direct mode that does not finish says to run it again with `--resume`, and, where its destination's parts keep any of the backup's files, how many bytes they keep and where, until a run there finishes. No contract change. |
| 2026-10 | Accepted | [ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md): each run keeps the record this one turned down for now, restore-parts lists and lets go what each destination keeps, and the line names restore-parts --forget. |

# ADR-0119 — Direct mode restores to the original location

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-001 (amended: in direct mode the command line restores to the original location too, from the set's configured roots)
**Related:** [ADR-0117](0117-the-command-line-asks-a-restore-as-the-console-does.md) (the command line asks the console's runs, and the record that refused the original location in direct mode), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the original-location mapping), [ADR-0040](0040-multi-root-backup-sets.md) (a set's roots and their labels), [ADR-0028](0028-service-boundary-and-deployment-topologies.md) (the same operation performs identically through either path), [ADR-0107](0107-the-command-line-plans-a-restore-on-its-own.md) (the plan the command line prints), [architecture 08 §1](../architecture/08-restore-and-recovery.md#1-restore-paths)

**Built:**
- The mapping: `Restore/RestoreOriginalLocation` slices a plan over a set's roots and merges the slices' receipts into one. The service's `Agent/ServiceCommandHandler` and the command line's `Cli/OperationGateway` both use it.
- Direct mode: `Cli/OperationGateway` reads the snapshot's set from the state directory's configuration, and plans, measures and restores each root's slice in place under the policy asked.
- The plan's words: `Cli/RestorePlanReport` says a plan of the original location puts its files back where they were captured, through either path.
- The tests:
  - `Repository.Tests/RestoreOriginalLocationTests` — a set of one root takes the whole plan; several put each label's files back into their own root without the label; a label no root carries is named and nothing is sliced; one root's receipt is the run's, and several are one receipt with each path under its label
  - `Cli.Tests/RestoreOriginalDirectTests` — in direct mode a file goes back where it was captured and a path not asked is left; several roots each take their own label's files; a file in the way is moved aside where no policy is asked; a snapshot of no set configured here, and a label the set no longer carries, are refused with nothing written; the plan is of the files asked at their roots; the room is measured at every root; a plan kept for the original location runs as kept
  - `Cli.Tests/GatewayRestoreReportTests` — a plan of the original location through the service says its files go back where they were captured

---

## Context

The command line asks the runs the console asks ([ADR-0117](0117-the-command-line-asks-a-restore-as-the-console-does.md)), but in direct mode it refused the original location, on the ground that a set's roots are the service's to know.

That ground was wrong. A set's roots are in the state directory's configuration file, which the service reads them from, and direct mode already reads the same file to back a named set up. The refusal meant a person restoring with no service running could not put a file back where it was captured, though the same command line had just captured it there.

## Decision

### 1. One mapping, shared

The original-location mapping moves out of the service into the restore library ([ADR-0041](0041-guided-restore-and-peer-retrieval.md)):
- **One root** takes the whole plan, its paths as captured.
- **Several roots** hold each root's files under its label ([ADR-0040](0040-multi-root-backup-sets.md)), so each label's files go back into their own root without the label.
- **A label no root carries any more** is named, and nothing is sliced. The run is refused whole rather than put back in part.

The slices' receipts merge into one receipt for the run, each path under the label it was asked by, as the service has always merged them.

### 2. Direct mode reads the roots where the service reads them

The snapshot's set is the one its catalogue row names, and its roots are those the state directory's configuration has for that set now. Each root's slice is planned, measured and restored in place, under the policy asked, as every direct run is and as the service restores the original location. The room is measured at every root together, before the first one is written.

Refused before anything is read or written:
- **A snapshot of no set configured here**, such as one captured from a folder named on the command line. Nothing says where its files were captured, so the person is told to name a folder with `--output`.
- **A label the set no longer carries**, named.

### 3. A plan says where its files go

A plan of the original location names no folder, and the plan's first line said where by the folder asked, which was empty. It now says the files go back where they were captured, through either path.

## Consequences

- With no service running, a person puts a file back where it was captured, from the same command line that captured it.
- The service and direct mode cannot map a plan onto the roots differently, because they map it with the same code.
- A plan kept for the original location, by either client, runs in direct mode where it is asked as kept.

## What this does not do

- **Restore a snapshot of a folder named on the command line to where it was captured.** The catalogue does not keep the folder a snapshot was captured from, only the set it is of.
- **Quarantine in direct mode.** A direct run lands in place, as it always has; `--in-place` still changes nothing there.
- **Keep the receipt in direct mode.** The service keeps each run's receipt, and direct mode still prints what the receipt says.

## Alternatives considered

- **Keep the refusal.** Rejected. It rested on the service alone knowing a set's roots, which it does not.
- **Map the roots again in the command line.** Rejected. Two mappings could put the same file back in different places, depending on whether a service was running.
- **Land the original location under quarantine in direct mode.** Rejected. The service restores the original location in place, and direct mode lands every run in place; a quarantined run would also need a name to resume it by, which only the service keeps.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The original-location mapping and the receipts' merge move into the restore library, used by the service and by direct mode, which now plans, measures and restores each root's slice in place from the set's configured roots, refusing a snapshot of no configured set and a label no root carries. A plan of the original location says its files go back where they were captured. |

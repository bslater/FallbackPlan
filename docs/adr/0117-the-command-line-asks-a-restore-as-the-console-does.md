# ADR-0117 — The command line asks a restore as the console does

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-001 (amended: the command line selects several paths, the original location, a policy and a landing in place), FR-RST-003 (amended: a plan the console kept runs from the command line where it asks the run as kept)
**Related:** [ADR-0114](0114-a-kept-plan-is-run-as-it-was-approved.md) (a kept plan run as approved, and the record that refused a console's plan by name), [ADR-0107](0107-the-command-line-plans-a-restore-on-its-own.md) (the plan the command line prints, and the record that planned no other policy), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the shapes the guided restore asks), [ADR-0113](0113-the-console-keeps-a-restores-plan.md) (the console keeps a plan in the same file), [architecture 08 §1](../architecture/08-restore-and-recovery.md#1-restore-paths), [§2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The verb: `Cli/CliApplication`'s `restore` takes several paths, `--target folder|original`, `--existing rename|overwrite` and `--in-place`, and needs `--output` only for a folder. `--as-planned` holds every field of the run to the kept plan's.
- The gateways: `Cli/OperationGateway` asks the service the run and its plan in that shape. In direct mode it restores and plans several paths under the policy asked, and refuses the original location.
- The kept plan: `Cli/RestorePlanExport` keeps the run as asked, as the console keeps one.
- The tests:
  - `Cli.Tests/RestoreShapeCommandTests` — in direct mode several paths restore each and nothing else; rename keeps a file in the way and writes beside it; overwrite replaces it; the room a run and its plan need is measured under the policy asked; in place lands where any direct run lands; the original location is refused, and so is it beside `--output`; a restore to a folder without `--output` and a choice that is not one are refused; the plan is of the paths asked, and a plan kept is of the run asked, one path kept as version 1 kept it
  - `Cli.Tests/RestoreAsPlannedCommandTests` — a console's plan asked as it was kept restores, and asked with another policy, landing or paths, or to a folder where it was kept for the original location, is refused as another run, naming how it was kept
  - `Cli.Tests/GatewayRestoreReportTests` — through the service the run and its plan are asked in the shape the command line asks, and the original location is asked with no folder
  - `Hosts.Tests/ClientModeTests` — routed end to end, a file is put back where it was captured, and a path not asked is left as it stands

---

## Context

The console's guided restore asks a run of several paths, to a folder or to where the files were captured, under a policy for a file in the way, in place or quarantined ([ADR-0041](0041-guided-restore-and-peer-retrieval.md)). The command line asked one path to a folder, moving aside whatever was in the way.

That gap had two costs. A person at a terminal could not ask what a person at the console could, and the contract had carried every one of those choices since 1.11. And a plan kept from the console ([ADR-0113](0113-the-console-keeps-a-restores-plan.md)) could not be run from the command line: [ADR-0114](0114-a-kept-plan-is-run-as-it-was-approved.md) refused it by name, because the command line could not ask the run it was kept for.

## Decision

### 1. `restore` asks the run the console asks

- **Paths:** one or several, after the snapshot. One is asked as a path, as version 1 of a kept plan asked it. Several are asked as paths. None asks all of it.
- **`--target`:** `folder`, the default, restores under `--output`. `original` puts each file back where it was captured, as the set's configured roots say, and takes no `--output`.
- **`--existing`:** a file in the way is moved aside by default. `rename` keeps it and writes the restored copy beside it; `overwrite` replaces it.
- **`--in-place`:** through the service, the run lands where `--output` points rather than in a quarantine folder of its own.
- **A choice that is not one** is refused before anything is asked. So is a restore to a folder without `--output`, and the original location beside one.

### 2. Through the service the run is asked in that shape

The run, its plan and a kept plan are each asked as the command line asks them, so the service measures, plans and lands the run that was asked.

### 3. In direct mode, where it can

A direct-mode restore lands in place where `--output` points, as it always has.
- **Several paths** are planned and restored together.
- **The policy** is the one asked, measured as the run will meet it.
- **`--in-place`** changes nothing, since the run lands in place either way.
- **The original location** is refused. It is a set's configured roots, which only the service knows.

### 4. A kept plan is held to every field

`--as-planned` holds the run asked to the kept one: the snapshot, the paths in any order, the target, where it writes for a folder, the policy and the landing. One path asked as a path or as a list of one is the same run, and so is the default target however it was spelled. Another run is refused, naming how the plan was kept. A plan the console kept therefore runs from the command line, asked as it was kept.

## Consequences

- A person at a terminal asks the same runs a person at the console does.
- A plan approved in the console runs from a script, held to what was approved.
- `restore`'s `--output` is no longer required on its own; the verb says when it is needed.

## What this does not do

- **Run a kept plan from the console** ([ADR-0114](0114-a-kept-plan-is-run-as-it-was-approved.md)).

  > **2026-10 ([ADR-0118](0118-the-console-runs-a-kept-plan-as-it-was-approved.md)).** The console runs a kept plan too, held to it with the command line's own comparison, and only where nothing it said has changed.
- **Restore to the original location in direct mode.** It needs the set's configuration, which the service holds.
- **Read the run from the file.** The person still names the run they mean, and the file is held to it (ADR-0114).

## Alternatives considered

- **Run a console's plan as the file says, with nothing asked.** Rejected again, for ADR-0114's reason: a file swapped for another one would be run rather than refused.
- **Take the run's shape as one option, in the contract's words.** Rejected. Each choice is a person's, and a flag apiece says what it does.
- **Refuse the new options in direct mode.** Rejected for paths and policy, which the executor in direct mode already takes, and kept only for the original location, which it cannot know.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. `restore` takes several paths, `--target`, `--existing` and `--in-place`, asks the service the run and its plan in that shape, restores and plans several paths under the policy in direct mode while refusing the original location there, keeps a plan of the run as asked, and holds `--as-planned` to every field, so a plan the console kept runs from the command line. |
| 2026-10 | Accepted | [ADR-0118](0118-the-console-runs-a-kept-plan-as-it-was-approved.md): the console runs a kept plan, which this record left to another. |

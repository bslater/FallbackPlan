# ADR-0113 — The console keeps a restore's plan

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: the console keeps a plan as the file the command line keeps, in one shape that holds the run as the wizard asks it)
**Related:** [ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md) (a restore's plan kept in a file, the record that left the console owed, and its [Amendment 1](0108-a-restores-plan-can-be-kept-in-a-file.md#amendment-1-2026-10--one-shape-for-every-client-and-the-run-as-asked) for the file's second version), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the guided restore), [ADR-0089](0089-a-backups-file-names-need-the-passphrase.md) (a backup's file names need the passphrase), [ADR-0081](0081-diagnostic-bundle.md) (a file the console hands the browser to save), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The shape: `Api/RestorePlanFile`, version 2, which `Cli/RestorePlanExport` writes too.
- The endpoint: `Web/WebConsoleHost`'s `/api/restore-plan-file` asks the service for the plan the wizard sends, and gives back the file as an attachment.
- The wizard: its plan step offers Keep plan while a plan is on screen, and saves the file the endpoint gives.
- The tests:
  - `Api.Tests/RestorePlanFileTests` — the run as asked, with several paths, its target, its policy and in place, beside the plan in the contract's words, and no source; a plan of one path keeps version 1's fields with their meaning
  - `Web.Tests/ConsoleRestorePlanFileTests` — the endpoint gives the file the command line keeps, asked as the wizard asks it; a refused plan gives no file; anything but a plan is refused and nothing is asked; without the token nothing is asked
  - `Web.DomTests/RestoreWizardDomTests` — a plan on screen downloads as that file and restores nothing, and a refused plan offers nothing to keep
  - `Cli.Tests/RestorePlanCommandTests` — the command line's export writes version 2

---

## Context

`restore --export` keeps a plan as JSON ([ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md)), for a person to keep, send to whoever decides, or let a script read. That record left the console owed. The wizard showed its plan and let it go when the dialog closed.

The file's shape held one path and where the run writes, which is all the command line asks. The wizard asks for more: several paths, a target, an existing-file policy, and a landing in place. A file without them would describe a different run from the one on screen.

## Decision

### 1. One shape, held in the contract's assembly

The file's shape moves into `FallbackPlan.Api`, beside the contract it quotes, and both clients write it. It is version 2 ([ADR-0108 Amendment 1](0108-a-restores-plan-can-be-kept-in-a-file.md#amendment-1-2026-10--one-shape-for-every-client-and-the-run-as-asked)):
- **Kept:** every field version 1 had, with the meaning it had.
- **Added:** the run as asked beside one path, namely its paths, target, existing-file policy and whether it lands in place.
- **Left out:** the restore source the plan was asked through. It is an open handle that means nothing once it closes.

### 2. The console asks the service, and hands the browser the file

The console gains an endpoint that takes the plan command the wizard sends:
- **It asks the plan again** under the console's token and the browser's session, through the source the wizard unlocked. The file then holds the service's answer, not the page's copy of it.
- **It gives back the file** as an attachment, named for the snapshot and the moment, and kept by no cache.
- **Only a plan.** A run is never asked through it. A plan the service refused comes back as the command relay would give it, with no file.

The service writes no file, and neither does the console. The browser saves it where its person chooses, on the machine of the person who unlocked the plan, as the command line writes it where the command was typed.

### 3. The wizard offers it while a plan is on screen

The plan step offers Keep plan beside the restore's confirmation, only while a plan is shown. It asks with the request the plan step was asked with, one function the two share, so the kept plan is the plan of the run on screen. Keeping it restores nothing.

## Consequences

- A plan made in the console can be kept, sent, or read by a script, in the shape a plan from the command line is.
- A script that read version 1 reads the same fields with the same meaning. One that checks the version is told the shape grew.
- A plan kept from the console costs the service a second plan of the same run.

## What this does not do

- **Run a kept plan,** from either client, as before.

  > **2026-10 ([ADR-0118](0118-the-console-runs-a-kept-plan-as-it-was-approved.md)).** The console runs a kept plan too, held to it with the command line's own comparison, and only where nothing it said has changed.

  > **2026-10 ([ADR-0114](0114-a-kept-plan-is-run-as-it-was-approved.md)).** The command line runs a kept plan of a run it asks, held to what the plan said. A console's plan of several paths, a target or a policy is refused there by name, and the console runs none.
- **Keep the plan the page showed.** The file holds the plan asked when the person kept it. Between showing and keeping, the replica or the free space may have moved, and the kept plan says what they are then.
- **Keep a resume's plan.** A resume is not planned ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)).

## Alternatives considered

- **Build the file in the page from the plan it shows.** Rejected. The page holds the plan in the console's camel-cased words, and rebuilding the contract's words from them is a second description of the same plan that would drift.
- **A service verb that writes the file.** Rejected. A plan names a backup's files, and the service writes no file a client did not ask it to land ([threat T-16](../threat-model.md)). The command line in direct mode has no service to ask.
- **A shape of the console's own.** Rejected, as ADR-0108 rejected one of the command line's own.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The file's shape moves into the contract's assembly at version 2, which keeps version 1's fields and adds the run as asked, and both clients write it. The console's endpoint asks the service for the plan the wizard sends and gives back the file as an attachment, and the wizard offers to keep a plan while one is on screen. |
| 2026-10 | Accepted | [ADR-0114](0114-a-kept-plan-is-run-as-it-was-approved.md): the command line runs a kept plan of a run it asks; a plan kept from the console of a run it does not ask is refused there by name. |
| 2026-10 | Accepted | [ADR-0118](0118-the-console-runs-a-kept-plan-as-it-was-approved.md): the console runs a plan kept in this file, read with the command line's reader and held to it with the command line's comparison. |

# ADR-0107 — The command line plans a restore on its own

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: the command line produces the plan on its own, through either path, restoring nothing, and says by its exit whether the run would restore every file and fit)
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (the plan's room and what will not come back, and the record that left export and resume owed), [ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md) (what the plan names archived), [ADR-0028](0028-service-boundary-and-deployment-topologies.md) §3 (an operation performs identically through either path), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the guided restore and its sources), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The verb: `Cli/CliApplication`'s `restore` takes `--plan`, which asks `Cli/OperationGateway` for the plan of the run `restore` would make and restores nothing.
- The two paths: through the service, the plan is asked for the run's own shape, from a source the passphrase unlocked. In direct mode the CLI makes the plan itself, from the same pieces the service uses. `Restore/RestorePlanFigures` reckons what a plan's files write, its conflicts and what the target will not get back, for the service's plan and the CLI's own.
- The words: `Cli/RestorePlanReport` renders a plan whichever path made it, and says whether the run would restore every file and fit.
- The tests:
  - `Cli.Tests/RestorePlanCommandTests` — in direct mode a clean plan names the files and the destination and creates nothing; one that will not fit says so with the figure and exits non-zero; one of files whose content is gone names them as missing and exits non-zero
  - `Cli.Tests/GatewayRestoreReportTests` — through the service the plan is asked for the run's own shape and nothing is run; a plan naming files held only in an archival tier, or a shortfall, is not a success
  - `Hosts.Tests/ClientModeTests` — routed through a running service, a plan writes nothing
  - `Repository.Tests/RestorePlanTests`, `Repository.Tests/RestoreMetadataHonestyTests` — the shared reckoning carries each conflict the planner found, and a file with holes writes what its manifest says rather than its length

---

## Context

A plan is how a person finds out about a restore's problems while they are still cheap (architecture 08 §2). The console's wizard shows one before a restore can be armed. It says what the run would write and read, whether it fits, what no copy holds, what the store holds only in an archival tier, and what the target will not get back.

The command line had no way to ask for any of it. `restore` measured the room and refused a run that would not fit, then wrote first and reported after. A person restoring from a shell met a missing file or an archived one part-way through, in the run's failures. A script could not ask whether a restore would work before starting it.

Architecture 08 §2 also says plans are exportable and resumable. Neither was built, and [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) left both owed.

## Decision

### 1. `restore --plan` plans the run `restore` would make

`--plan` takes everything `restore` takes and restores nothing. The plan is for the run `restore` would make with the same words: the same snapshot and path, where it writes, and how it lands.

### 2. Either path makes the same plan

- **Through the service,** the CLI opens a source under the grant its passphrase gives, as `restore` does. It asks for the plan of the run `restore` sends: where it writes, under quarantine.
- **In direct mode,** the CLI makes the plan itself, as it runs the restore itself. It reads the manifests, asks for every blob they name, and measures the room where the run writes, in place and preserving what it finds.

What a plan's files write, the planner's conflicts and what the target will not get back are reckoned in one place for both, so the two plans cannot drift (ADR-0028 §3).

### 3. One rendering, and the exit says whether the run would work

The plan prints in the order a person deciding needs it:
- the files, what they write and what the run reads;
- the room on each volume, and a shortfall where it would not fit;
- the files whose content no copy holds;
- the files held only in an archival tier;
- the conflicts;
- what the target will not get back.

The last line says nothing was restored.

The verb succeeds only where the run would restore every file the plan names and fit. A file missing or archived, or a volume too small, exits non-zero. A conflict does not, because the existing-file policy settles it.

## Consequences

- A person restoring from a shell can see the plan the console shows before anything is written, and a script can plan before it restores.
- A plan made by the service and one made here read alike, and a change to what a plan reckons reaches both.

## What this does not do

- **Export a plan to a file, or run one.** The plan prints. Architecture 08 §2's export, a plan another person or tool can keep and run later, is still owed.

  > **2026-10 ([ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md)).** `restore --export <file>` now keeps the plan as JSON. Running a kept plan is still not done.
- **Resume a restore.** A restore cut short is run again from the start. Its existing-file policy settles what the first run wrote.

  > **2026-10 ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)).** A restore cut short is resumed with `restore --resume`, which leaves what the first run landed as it stands. A resume is not planned.
- **Plan a run with another existing-file policy, or in place.** `restore` asks for neither, so its plan does not.

## Alternatives considered

- **A verb of its own, `restore-plan`.** Rejected. A plan answers for a run, so it takes the run's words, and one verb keeps them from drifting apart.
- **Plan only through the service.** Rejected. Direct mode runs the restore itself, so it can plan it itself, and ADR-0028 §3 holds an operation to one behaviour through either path.
- **Exit zero for any plan made.** Rejected. A script planning before it restores needs to know whether the restore would work, without parsing the words.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. `restore --plan` prints the plan for the run `restore` would make and restores nothing, through the service or in direct mode alike, and exits non-zero unless the run would restore every file and fit. Export to a file and resume stay owed. |
| 2026-10 | Accepted | [ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md) takes up the export this record left owed: `restore --export <file>` keeps the plan as JSON and implies `--plan`. |
| 2026-10 | Accepted | [ADR-0109](0109-a-restore-cut-short-is-resumed.md) takes up the resume this record left owed: `restore --resume` leaves what a run cut short landed and restores the rest. `--plan` beside it is refused. |

# ADR-0110 — The console resumes a restore cut short

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: a restore's result names the run a resume of it gives, and the console offers to resume a run that failed or was stopped)
**Related:** [ADR-0109](0109-a-restore-cut-short-is-resumed.md) (a restore cut short is resumed, the record that left the console owed, and its Amendment 1 for rename), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the guided restore and its policies), [ADR-0028](0028-service-boundary-and-deployment-topologies.md) (a restore from the console writes on the service's machine), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The contract: 1.68's `RestoreResult.Run`, which `Agent/ServiceCommandHandler` sets to the run's own name, or, for a resumed quarantined run, to the run it continued.
- The console: the wizard's result step offers to restore what is left when a run failed or was stopped and its result names a run, and says what a resume found in place.
- The command line: `Cli/OperationGateway`'s service gateway says how to resume a run that failed or was stopped.
- The tests:
  - `Web.DomTests/RestoreWizardDomTests` — a restore that failed or was stopped offers a resume, which asks for the run as it was confirmed and names the run the service said, and says what it found in place; a run that completed, one that failed nothing, and one from a service that names no run offer none
  - `Web.Tests/ConsoleRestoreResultScriptTests` — the result step offers the resume and shows what it found
  - `Hosts.Tests/RestoreResumeServiceTests` — every run says the name a resume of it gives, the same name for a quarantined run and the run that resumes it
  - `Cli.Tests/GatewayRestoreReportTests` — a run that failed or was stopped says how to resume it, and one that completed or failed nothing says nothing of it
  - `Api.Tests/ConfigurationContractTests` — the wire name, and what an older service's result reads as

---

## Context

[ADR-0109](0109-a-restore-cut-short-is-resumed.md) resumes a restore cut short. Through the service a resume names the run it continues, by the name of the folder that run landed in. Its Amendment 1 lets a resume run under rename, the console wizard's default policy.

The console could not resume one, for two reasons:
- **The wizard offered nothing.** Its result step showed what a run achieved, a failure among it, and a Close button.
- **The result named no run.** A client could read a run's name only out of a path: the quarantine folder's name, or the receipt's file name. Those are where a run is kept, not what it is called.

The command line had the second problem too. It printed where a run landed, and a person had to read the run's name out of that path to resume it.

## Decision

### 1. A restore's result names the run a resume of it gives

`RestoreResult` gains `run` (contract 1.68): the name a client sends back as `resume`.
- For a run of its own, it is that run's name.
- For a resumed quarantined run, it is the run it continued, whose folder it landed in. A run cut short twice is then resumed by the same name both times, and lands in the same folder.

### 2. The console offers to restore what is left

When a run failed or was stopped and its result names a run, the wizard's result step offers to restore what is left.
- **What it asks for:** the run exactly as it was confirmed (the same files, the same place, the same policy) with `resume` set to the run's name.
- **What follows:** its result replaces the one before it, and says how many files were found in place.
- **When it is offered again:** if the resume fails too, the offer stands again, under the name that result gives.

**Not offered:**
- **For a run that completed.**
- **For one that failed nothing but skipped what the target cannot hold.** A resume would skip it again.
- **For a service before 1.68,** whose result names no run.

### 3. The command line says how

Through the service, a run that failed or was stopped says to run it again with `--resume` and its name. A run that completed, or failed nothing, says nothing of it.

> **2026-10 ([ADR-0137](0137-a-run-in-direct-mode-left-unfinished-says-how-to-resume-it.md)).** In direct mode too, a run that does not finish says to run it again with `--resume`, and what its parts keep of the backup and where.

## Consequences

- A person restoring from the console resumes a run cut short without leaving the wizard, and without reading a run's name out of a path.
- A script reads the run's name from the result, or from the line the command line prints, rather than from the folder.

## What this does not do

- **Resume a run the console did not see end.** The wizard offers a resume for the result in front of it. A run cut short by the service stopping returns no result, so it is resumed from the command line by its folder's name, as before.

  > **2026-10 ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)).** The service names such a run and says so in a notice, which the console's notices show. The command line resumes it by its name alone with `restore-resume`. The wizard still offers a resume only for the result in front of it.

  > **2026-10 ([ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md)).** The console resumes such a run too: its snapshots page and the run's notice offer to restore what is left, and the wizard asks the service to resume it by its name alone.
- **Resume a plan.** A kept plan is still not run.

## Alternatives considered

- **Read the run's name from the receipt path.** Rejected. A path is where a run is kept. Every client would parse it, and a change to the layout would break each one.
- **Offer a resume for every result.** Rejected. A run that completed has nothing left. A partial one skipped what the target cannot hold, and a resume would skip it again and look like it had done something.
- **Resume in the background and return.** Rejected. The wizard runs a restore while the person waits, and a resume is the same restore.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A restore's result names the run a resume of it gives (contract 1.68). The wizard offers to restore what is left for a run that failed or was stopped, asking for it as it was confirmed and naming that run, and says what was found in place. The command line says how to resume such a run through the service. |
| 2026-10 | Accepted | [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md): a run the console did not see end is named by the service and noticed, and the command line resumes it by its name alone (contract 1.71). The wizard's offer is unchanged. |
| 2026-10 | Accepted | [ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md): a run the console did not see end is resumed from the console, from the snapshots page's list of runs left unfinished or from the run's notice, by its name alone. |
| 2026-10 | Accepted | [ADR-0123](0123-the-console-plans-the-resume-a-result-offers.md): restoring what is left of a result is planned first, by the run's name, and asked only from beneath its plan. |
| 2026-10 | Accepted | [ADR-0137](0137-a-run-in-direct-mode-left-unfinished-says-how-to-resume-it.md): Decision 3 holds in direct mode too. A run there that does not finish says to run it again with `--resume`, and what its parts keep and where. |

# ADR-0115 — A restore cut short is named, and resumed by its name alone

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: the service names each restore run it began and has not seen finish, says so, and resumes one by its name alone)
**Related:** [ADR-0109](0109-a-restore-cut-short-is-resumed.md) (a resume, and the record that left finding the run to whoever read its folder), [ADR-0110](0110-the-console-resumes-a-restore-cut-short.md) (a result names its run, and the record that left a run the console did not see end to the command line), [ADR-0089](0089-a-backups-file-names-need-the-passphrase.md) (a backup's file names need the passphrase), [ADR-0049](0049-service-lifecycle-hygiene.md) (the job journal's crash leftovers, noticed at start), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The journal: `Application/RestoreRunJournal` keeps each run the service began in `restore-runs.json`, from before it writes until a run of it finishes, and knows which are under way.
- The service: `Agent/ServiceCommandHandler` names each run under the name its result gives, settles it by its outcome, and lists, resumes and lets go a run left unfinished. `Agent/UnfinishedRestoreNotice` says so, and `Agent/ServiceRuntime` says so at start of each run a previous process left.
- The contract: 1.71's `list_restore_runs` and `restore_runs`, `resume_restore` and `forget_restore_run`.
- The verb: `Cli/CliApplication`'s `restore-resume`, which lists the runs left unfinished, resumes one by its name through `Cli/OperationGateway`, or lets one go.
- The tests:
  - `Application.Tests/RestoreRunJournalTests` — a run begun is kept whole and outlives its process; one under way is not unfinished until it ends so; every run on record at opening is unfinished; one finished is dropped; one begun again under its name is one run; one continued under a name of its own is replaced by it; one let go is dropped, but not one under way; a journal that will not read, or that a newer build wrote, is set aside
  - `Hosts.Tests/RestoreUnfinishedServiceTests` — a run that finishes leaves nothing named or noticed; a run stopped is named, without the paths it was asked, noticed, and resumed by its name alone in its own folder, which ends both; a run that failed files is named and resumed as it was asked, in place under rename; a run the service did not see end is noticed when it starts again; a run let go is named no more; a resume of a run not on record, or of a name that is not a run's, is refused
  - `Hosts.Tests/ClientModeTests` — routed end to end, the CLI lists a run that failed files and resumes it by its name
  - `Cli.Tests/RestoreResumeVerbTests` — the list asks no passphrase; a run named is resumed through a source of its snapshot and says what the restore says, and how to resume it again where it does not finish; a run not left unfinished is refused before the passphrase is asked; a run let go is forgotten; in direct mode no run is named
  - `Api.Tests/ConfigurationContractTests` — the wire names, and the pin at 1.71

---

## Context

A restore through the service lands in a quarantine folder of its own, named for its run, and a resume names that run ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)). A run's result says its name ([ADR-0110](0110-the-console-resumes-a-restore-cut-short.md)), so a run whose result someone read is resumed by it.

The runs most in need of a resume are the ones whose result nobody read:
- **A caller that hung up.** A console tab closed, or a command line killed with its terminal. The run stops between files and answers, and the answer goes nowhere.
- **The service stopping.** The run stops the same way, and its caller is cut off with it.
- **The machine stopping.** The run ends mid-file, and nothing answers at all.

Each was resumed only by someone who knew a restore had been running, found its folder, read the run's name off it, and asked the restore again: the same snapshot, the same path, the same place, the same policy. Nothing told them it had not finished. ADR-0109 left finding the run to whoever read the folder, and ADR-0110 left a run the console did not see end to the command line and its folder's name.

## Decision

### 1. The service names each run before it writes

Once a run is planned and found to fit, and before its first file, the service records it in `restore-runs.json` beside its other state:
- **What it keeps:** the run's name, its snapshot and set, the paths it was asked for, where it writes, its target, policy and whether it lands in place, and when it began.
- **Under which name:** the one its result gives (ADR-0110). A resumed quarantined run is the run it continues, begun again. A resumed run in place names itself, and takes the place of the run it continues.
- **Which runs are under way:** the running process's own knowledge, never written. Every run on record when the service starts was left by a process that has gone.

### 2. A run stays named until a run of it finishes

- **Finished:** a run that completes, or skips only what the target cannot hold. A resume would add nothing (ADR-0110), so the run is dropped.
- **Left unfinished:** a run that was stopped, failed files, or ended without answering. It stays named.

### 3. A run left unfinished is said

A run that ends unfinished raises a notice naming the run, its snapshot, its set and where it writes, and how to resume it or let it go.
- **When it is said:** as the run ends. A run that fails again is said again, even after an acknowledgement.
- **A run the service did not see end** is said when the service next starts, unless that same notice was already acknowledged.
- **When it is withdrawn:** once a run of it finishes or a person lets it go.

### 4. Each run left unfinished is listed, without the backup's file names

`list_restore_runs` (contract 1.71) answers each run left unfinished, oldest first: its name, snapshot, set, where it writes, how it lands, and when it began. The paths it was asked for are the backup's file names ([ADR-0089](0089-a-backups-file-names-need-the-passphrase.md)), so the list leaves them out and needs no passphrase. A run under way is not listed.

### 5. A run is resumed by its name alone

`resume_restore` takes a run's name, a restore source and whether to ignore free space. The service asks the run again exactly as it was asked, as a resume, and answers as a restore does.
- **A name that is not a run's** is refused before it reaches a path.
- **A run not left unfinished** is refused, saying how a run left before the service named its runs is resumed.
- **The source** is the caller's, unlocked by the passphrase, because the run reads the backup's files.

### 6. A run can be let go

`forget_restore_run` drops a run left unfinished and withdraws its notice. What it landed stays where it landed, and is still resumed by its folder's name. A run under way is not the person's to let go.

### 7. `restore-resume`

Through the service only:
- **With no run,** it lists the runs left unfinished. No passphrase is asked.
- **With a run,** it finds the run's snapshot in the list, opens a source of it under the passphrase, and resumes it. It prints what `restore` prints, and where the run does not finish, how to resume it again by its name.
- **With `--forget`,** it lets the run go.
- **A run not left unfinished** is refused before the passphrase is asked.

In direct mode a run lands in place and names none, so the verb is refused there, pointing at `restore --resume`.

## Consequences

- A restore stopped by a closed tab, a stopped service or a stopped machine is learned of from a notice, and finished with one command naming one word.
- A script resumes a run knowing only its name, never the snapshot, path or folder it was asked.
- A run left unfinished stays named until it is finished or let go. A person who never does either sees it listed, and noticed until they acknowledge it.

## What this does not do

- **Resume from the console.** The wizard still offers a resume only for the result in front of it ([ADR-0110](0110-the-console-resumes-a-restore-cut-short.md)). Its notices list shows a run left unfinished, which the command line resumes.

  > **2026-10 ([ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md)).** The console lists the restores left unfinished on its snapshots page, offers to restore what is left from there and from the notice, and lets one go. Its wizard resumes one by its name alone, through a source of the run's own set.
- **Name a run in direct mode.** It lands in place and needs no name.
- **Resume within a file.** A file cut short mid-write never landed, so it is restored whole (ADR-0109).
- **Find a run its folder no longer holds.** A quarantined run whose folder is gone is refused as ADR-0109 refuses it, and can be let go.

## Alternatives considered

- **Read the runs from the quarantine folders.** Rejected. A folder says a run was there, not what it was asked, and its files are the backup's: the service would have to read them to tell one snapshot's run from another's.
- **Keep only runs that ended without answering.** Rejected. A run that answered with files failed was answered to a caller who may have gone, and its notice is the one place it is learned of.
- **Resume through `restore --resume` with the snapshot and folder taken from the run.** Rejected. That verb names the snapshot and the output it restores, and holding those to a run would be two answers to one question. A verb that takes the run alone says what it does.
- **Let acknowledging the notice let the run go.** Rejected. Acknowledging says a person has seen it, not that they will not resume it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The service names each restore run in `restore-runs.json` before it writes, under the name its result gives, keeps it until a run of it finishes, and says each one left unfinished, including at start for one the service did not see end. Contract 1.71 lists such runs without the backup's file names, resumes one by its name alone as it was asked, and lets one go; `restore-resume` does each from the command line. |
| 2026-10 | Accepted | [ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md) takes up the console's resume, which this record left owed: the snapshots page lists the runs left unfinished, the list and each run's notice offer to restore what is left by its name alone, and a run can be let go. |
| 2026-10 | Accepted | [ADR-0120](0120-a-resume-is-planned.md): a run left unfinished is planned by its name alone too, with `restore-resume <run> --plan`, as it was asked and as the resume will meet it. |
| 2026-10 | Accepted | [ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md): each run keeps the parts of the files it was cut short in under its name, a resume under a new name carries them, and a run let go, or one that finishes, takes its parts with it. |
| 2026-10 | Accepted | [ADR-0133](0133-a-run-left-unfinished-says-what-its-parts-hold.md): each run this record lists says what the parts it kept hold (contract 1.77), which letting it go removes. |

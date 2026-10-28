# ADR-0116 — The console resumes a restore it did not see end

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: the console names each restore left unfinished and resumes one by its name alone, or lets it go)
**Related:** [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md) (the service names each run until a run of it finishes, and the record that left resuming one from the console owed), [ADR-0110](0110-the-console-resumes-a-restore-cut-short.md) (the wizard resumes the run whose result is in front of it), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the guided restore and its sources), [ADR-0089](0089-a-backups-file-names-need-the-passphrase.md) (a backup's file names need the passphrase), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The page: `Web/wwwroot/app.js` lists the restores left unfinished on the snapshots page, offers to restore what is left from that list and from the notice the service raised, and lets one go.
- The wizard: in its resume mode it unlocks the passphrase, opens a source of the run's own set, and asks the service to resume the run by its name.
- The tests:
  - `Web.DomTests/RestoreUnfinishedDomTests` — the snapshots page names each restore left unfinished, without its files; one is resumed by its name alone through a source of its own set, asked to go on where it will not fit only when the person says so, and offered again under the name its result gives where it does not finish; going back from the resume returns to its source; a source that does not hold its backup resumes nothing; one is let go without the passphrase; its notice offers to restore what is left, and a run no longer left unfinished opens nothing; a service that names no run shows none

---

## Context

The service names each restore run it began until a run of it finishes, says each one left unfinished in a notice, and resumes one by its name alone ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)). The command line could do all of it. The console could not: its wizard offered a resume only for the result in front of it ([ADR-0110](0110-the-console-resumes-a-restore-cut-short.md)).

That left the console's person where ADR-0115 found everyone. The runs most in need of a resume, cut off when a tab closed or the service stopped, are the ones a console never saw end. The notice told them, and sent them to a command line.

## Decision

### 1. The snapshots page names each restore left unfinished

Above the snapshots, a card lists each run the service names (contract 1.71):
- **What it shows:** the run's name, the backup and its set, where it writes, how it lands, and when it began.
- **What it does not show:** the paths it was asked for. They are the backup's file names, so nothing there needs the passphrase ([ADR-0089](0089-a-backups-file-names-need-the-passphrase.md)).
- **When it is empty, or the service is before 1.71,** there is no card.

### 2. Restore what is left, from the list or the notice

Each run, and each notice the service raised for one, offers to restore what is left. The run is asked of the service again first, because a notice outlives its run until it is resolved, and the run may have finished elsewhere. A run no longer left unfinished opens nothing, and the person is told.

### 3. The wizard's resume mode asks only what the resume needs

The guided restore opens in a mode of its own:
- **Unlock:** the passphrase, as any restore asks it.
- **Source:** the run's own set, fixed, and whichever source of it the person chooses. A source that does not hold the run's backup is refused, saying so, and nothing is resumed.
- **Resume:** what the run restores and where, and whether to go on where it will not fit. There is no date, file or target to choose, and no plan: the run is asked again exactly as it was asked, and was confirmed when it began.
- **Result:** as any restore's. A resume that does not finish offers to resume again, under the name its result gives.

### 4. A run can be let go

Each run on the list can be let go, once the person confirms it. The service names it no more and withdraws its notice; what it landed stays where it landed. No passphrase is asked, since letting a run go reads nothing of the backup.

## Consequences

- A restore cut off when a tab closed is resumed from the console, in the same few steps it began with, without a command line.
- The notice that says a restore did not finish is the way to finish it.

## What this does not do

- **Plan the resume.** A resume is not planned (ADR-0109 §5). The service measures what it still writes, and refuses one that will not fit unless told to go on.

  > **2026-10 ([ADR-0120](0120-a-resume-is-planned.md)).** The command line plans a resume, for the run it continues or by a run's name alone, and the service answers it. The console still plans none.
  >
  > **2026-10 ([ADR-0121](0121-the-console-plans-a-resume.md)).** The console's resume mode plans the run before it resumes it, and shows the plan as a restore's plan is shown.
- **Show what a run left unfinished was asked for, file by file.** That needs the passphrase. The run's receipt names its files.
- **Run a kept plan from the console** ([ADR-0114](0114-a-kept-plan-is-run-as-it-was-approved.md)).

  > **2026-10 ([ADR-0118](0118-the-console-runs-a-kept-plan-as-it-was-approved.md)).** The console runs a kept plan too, held to it with the command line's own comparison, and only where nothing it said has changed.

## Alternatives considered

- **Offer the resume on the notice only.** Rejected. An acknowledged notice stops surfacing, and the run it named is still left unfinished.
- **Walk the whole wizard, prefilled with the run's choices.** Rejected. Every choice would be one the person could change, and a changed choice is another run, not a resume of this one.
- **Let the list's resume open the run's original source.** Rejected. The service does not keep which source a run read: a source is an open handle that means nothing once it closes. The person chooses one, as any restore does.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The snapshots page lists the restores left unfinished without their files; that list and each run's notice offer to restore what is left, which unlocks the passphrase, opens a source of the run's own set, and has the service resume it by its name; a run can be let go. |
| 2026-10 | Accepted | [ADR-0118](0118-the-console-runs-a-kept-plan-as-it-was-approved.md): the console runs a kept plan, which this record left to another. |
| 2026-10 | Accepted | [ADR-0120](0120-a-resume-is-planned.md): a resume is planned from the command line, which this record left owed; the console plans none. |
| 2026-10 | Accepted | [ADR-0121](0121-the-console-plans-a-resume.md): the resume mode plans the run before it resumes it. |
| 2026-10 | Accepted | [ADR-0133](0133-a-run-left-unfinished-says-what-its-parts-hold.md): the console's list of runs left unfinished says what each holds of the files it was cut short in. |

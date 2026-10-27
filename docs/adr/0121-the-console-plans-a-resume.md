# ADR-0121 — The console plans a resume

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: the console plans a resume before it runs one), FR-RST-003 (a plan says whether the run fits before it writes)
**Related:** [ADR-0120](0120-a-resume-is-planned.md) (a resume is planned, and the record that left the console's plan owed), [ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md) (the wizard's resume mode), [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (a restore says whether it fits), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the guided restore), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The page: the console script of `FallbackPlan.Web` asks `plan_resume_restore` once the wizard's resume mode has its source, and shows the plan in the words a restore's plan is shown in: the files, how many the run already landed, what is left to write and read, the room each volume needs, and what cannot come back. A resume that will not fit is armed only when the person says to go on, and a service that cannot plan one still resumes it, as before.
- The tests:
  - `Web.DomTests/RestoreUnfinishedDomTests` — a run left unfinished is planned by its name alone through the source opened, saying what it landed and where it needs room, before it is resumed; one that will not fit is armed only once the person says to go on; one naming files that cannot come back says so and resumes nothing; a service that cannot plan it still offers the resume

---

## Context

A resume is planned since [ADR-0120](0120-a-resume-is-planned.md), from the command line through either path. The console's resume mode ([ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md)) asked nothing before it ran: it said a resume that will not fit is refused, and offered to go on regardless, without saying whether this one would fit.

## Decision

### 1. The resume is planned once its source is open

The wizard's resume mode asks the service for the run's plan by its name alone, through the source the person just opened, as the resume itself will be asked. Nothing is run.

### 2. The plan is shown as a restore's plan is shown

The same figures and warnings as a restore's plan, from the same code:
- **The files**, and how many the run already landed.
- **What is left to write and read.**
- **The room** each volume needs, against what is free there.
- **What will not come back:** files missing or held archived, and the conflicts.

### 3. A resume that will not fit is armed only when the person says to go on

Where a volume is short of room, the resume's button stays disabled until the person ticks to restore anyway, as a restore's plan arms its confirmation. A resume that fits needs no word, because it repeats a run that was confirmed when it began.

### 4. A service that cannot plan the resume still resumes it

A service before contract 1.72 does not know the command. The wizard says the resume was not planned, and offers it as it did before, with the choice to go on where it will not fit, which the service still measures before it writes.

## Consequences

- A person resuming from the console sees, before anything is written, how much of the run is already there and whether the rest fits.
- A restore's plan and a resume's plan read alike in the console, because one function draws both.

## What this does not do

- **Change what is resumed.** The run is still asked again by its name alone, as it was asked.
- **Plan the resume offered from a result.** A run the wizard has just seen fail, or stop, is resumed from its result without a plan, as before.

  > **2026-10 ([ADR-0123](0123-the-console-plans-the-resume-a-result-offers.md)).** The resume a result offers is planned too, by the run's name, before it runs.

## Alternatives considered

- **Show the plan's figures without arming.** Rejected. A plan that says the resume will not fit, beside a button that runs it anyway, says one thing and offers another.
- **Refuse the resume where the service cannot plan it.** Rejected. A service before 1.72 resumes a run correctly, and measures it before it writes.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The wizard's resume mode plans the run by its name alone once its source is open, shows the plan as a restore's plan is shown, with how many files the run already landed, arms a resume that will not fit only when the person says to go on, and offers the resume as before where the service cannot plan it. |
| 2026-10 | Accepted | [ADR-0123](0123-the-console-plans-the-resume-a-result-offers.md): the resume a result offers is planned too, which this record left undone. |

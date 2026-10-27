# ADR-0123 — The console plans the resume a result offers

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: the resume a result offers is planned before it runs)
**Related:** [ADR-0121](0121-the-console-plans-a-resume.md) (the console plans a resume, and the record that left this one unplanned), [ADR-0120](0120-a-resume-is-planned.md) (a resume is planned by a run's name alone), [ADR-0110](0110-the-console-resumes-a-restore-cut-short.md) (the result that offers to restore what is left), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the guided restore), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The page: the console script of `FallbackPlan.Web` plans the resume a failed or stopped result offers, by the run's name and through the source the run read from, once the person chooses to restore what is left. It shows the plan as a restore's plan is shown, and asks the resume only from the button beneath it. A resume that will not fit is armed only when the person ticks to go on, that choice goes with it, and a service that cannot plan it still resumes it.
- The tests:
  - `Web.DomTests/RestoreWizardDomTests` — a restore cut short is planned by its run's name before it is resumed, and resumed in its own shape only from the plan; one that will not fit is armed only once the person says to go on, and goes on; one the service cannot plan is still resumed
  - `Web.DomTests/RestoreUnfinishedDomTests` — a resume that does not finish again offers its own resume, planned under the name its result gives

---

## Context

The console plans a resume before it runs one ([ADR-0121](0121-the-console-plans-a-resume.md)), but only in the resume mode the snapshots page opens. A result that failed or was stopped offers to restore what is left ([ADR-0110](0110-the-console-resumes-a-restore-cut-short.md)), and that button resumed at once. ADR-0121 recorded it as undone. A person who had just watched a run fail for want of room was offered its resume without being told whether the rest would fit.

## Decision

### 1. The offer is planned first

Choosing to restore what is left asks the service for the run's plan by its name alone ([ADR-0120](0120-a-resume-is-planned.md)), through the source the run read from. Every run the service began is named, whichever way it was asked: in the wizard, as a kept plan, or as a resume. Nothing is run.

### 2. It is shown as a resume's plan is shown

The plan appears beneath the result, in the words of a restore's plan: the files and how many the run already landed, what is left to write and read, the room each volume needs, and what will not come back. The resume is asked only from the button beneath it, as it was asked before: in the run's own shape, as kept, or by name.

### 3. Arming is the resume mode's

- **One that will not fit** is armed only when the person ticks to restore anyway, and that choice goes with the resume.
- **A service that cannot plan it** still resumes it, saying the resume was not planned. The service still measures the resume before it writes.

## Consequences

- Every resume the console offers is planned before it runs, from the snapshots page or from a result.
- A person who watched a run stop for want of room hears whether the rest fits before asking again.

## What this does not do

- **Change what is resumed.** The resume is asked as it was before this record, only later.

## Alternatives considered

- **Plan the resume as soon as the result arrives.** Rejected. A plan names the backup's files and reads the store, and most results offer nothing a person goes on to take.
- **Plan in the run's own shape rather than by its name.** Rejected. A run asked by name or as kept has no shape in the page to plan, and the name plans each the way the service will resume it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The resume a failed or stopped result offers is planned by the run's name once the person chooses it, shown as a resume's plan is shown, and asked only from beneath the plan, armed only on the person's say where it will not fit, and offered as before where the service cannot plan it. |

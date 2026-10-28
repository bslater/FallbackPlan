# ADR-0138 — The working space is said as room to assemble files one at a time

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: a working-space need is said as room to assemble the run's files one at a time)
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (a restore says whether it fits, and Decision 2 measures the engine's working directory), [ADR-0136](0136-a-resumes-working-space-credits-what-its-parts-hold.md) (a resume's working space credits what its parts hold, and the record that left the need's words as they were), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The words: `Restore/RestoreSpace`'s refusal, `Cli/RestorePlanReport`'s room line and the console's plan say a working-space need is room to assemble the run's files one at a time.
- The tests:
  - `Repository.Tests/RestoreSpaceTests` — a working need says it is working space to assemble the run's files one at a time, where it is and what is free
  - `Cli.Tests/RestoreWithinAFileDirectTests` — the command line's plan says the same of the parts root
  - `Web.DomTests/RestoreWizardDomTests` — the console's plan says it too, and says nothing of a largest file

---

## Context

A restore is measured on each volume it writes to ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)). One need is the engine's working directory, which holds each file until its hash verifies. The refusal said the run needed working space "for its largest file". The console said "of working space for the largest file in", and the command line's plan said the folder's need "while files verify".

Since [ADR-0136](0136-a-resumes-working-space-credits-what-its-parts-hold.md), a resume's working space needs only what its parts do not yet hold of each file. The figure is then the most that remains of any one file. That is not the largest file, whose part may hold most of it. ADR-0136 recorded the words as undone.

## Decision

A working-space need is said as room to assemble the run's files one at a time:
- **the refusal:** "needs … of working space in '…' to assemble its files one at a time, which has … free";
- **the command line's plan:** "room: … needs … byte(s) to assemble its files one at a time";
- **the console's plan:** "of working space, to assemble files one at a time, in".

That is true of a run afresh, whose need is its largest file, and of a resume, whose need is the most that remains of one. The figure is unchanged.

## Consequences

- What a person reads of the working space matches the figure beside it, whether or not a resume's parts are credited.
- A script that matched the old words matches no longer. The figure, and the contract's `working` flag, are where a script reads it.

## What this does not do

- **Name the file the need is for.** The need names no file of the backup, as nothing in a plan's figures does.
- **Say how much a resume's parts saved.** The plan says what the run reads ([ADR-0132](0132-a-resumes-plan-counts-only-what-it-still-reads.md)), and the need what it still needs.

## Alternatives considered

- **Say "for its largest file" for a run afresh, and something else for a resume.** Rejected. One wording true of both says the same thing everywhere, and needs nothing more from the measure.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A working-space need is said as room to assemble the run's files one at a time, by the refusal, the command line's plan and the console, since a resume's need is the most that remains of any one file. No contract change: the figure and the working flag are as they were. |

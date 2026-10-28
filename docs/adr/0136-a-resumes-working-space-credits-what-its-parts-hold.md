# ADR-0136 — A resume's working space credits what its parts hold

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: a resume's working space needs only what its parts do not yet hold of each file, for its plan and its run alike)
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (a restore says whether it fits, and Decision 2 measures the engine's working directory), [ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md) (a restore cut short resumes within a file), [ADR-0131](0131-the-service-measures-a-restores-working-space-where-its-parts-are-kept.md) (the working space is where the parts are kept, and the record that left a part uncredited), [ADR-0132](0132-a-resumes-plan-counts-only-what-it-still-reads.md) (a resume's plan counts only what it still reads, by a part's proof), [ADR-0135](0135-direct-mode-resumes-within-a-file.md) (direct mode keeps parts too), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The measure: `Restore/RestoreSpace` takes what each file writes and what of that its run holds, and gives the working space room for the most that remains of any one file. A run measured from its manifests proves each part its run kept as the resume would take it up.
- The probe: `Restore/RestoreBlobSet` gives what a plan of a resume holds of each file among its facts, from the proof it already makes.
- The runs: `Agent/ServiceCommandHandler` measures a resume with the parts of the run it continues, and `Cli/OperationGateway`, in direct mode, with those its destination keeps.
- The tests:
  - `Repository.Tests/RestoreWithinAFileTests` — a plan of a resume credits its working space what a part proves, only as far as it proves, and not the folder the file lands in; a run short of room for a whole file fits where its part holds the rest, and leaves the part as it was
  - `Hosts.Tests/RestoreUnfinishedServiceTests` — through the service, a plan of a resume needs no working space once its parts hold every file, the resume runs where whole files would not fit, quarantined or in place, and parts that do not prove are credited nothing
  - `Cli.Tests/RestoreWithinAFileDirectTests` — in direct mode, a resume's plan and run alike are credited what its parts hold, and a run that is not a resume nothing

---

## Context

A restore is measured before it writes, on each volume it writes to ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)). The engine's working directory holds a file until its whole-file hash verifies, so the measure gives it room for the largest file the run restores. Through the service ([ADR-0131](0131-the-service-measures-a-restores-working-space-where-its-parts-are-kept.md)), and now in direct mode ([ADR-0135](0135-direct-mode-resumes-within-a-file.md)), that directory is where the runs keep their parts, and a resume takes each part up as far as it proves ([ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md)).

What a part proves is already on that volume, and the free space the platform reports is what is left beside it. The measure still asked for the whole file. A resume of a large file cut short near its end, on a volume left with less free than the file, was refused as not fitting, though it needed room only for the rest. ADR-0131 and ADR-0132 recorded this as undone.

## Decision

### 1. The working space needs what remains of each file

For each file, the measure takes what restoring it writes and what of that the run already holds: the segments its part proves, from the file's start, by the rule the resume takes it up by ([ADR-0132](0132-a-resumes-plan-counts-only-what-it-still-reads.md)). The working space needs the most that remains of any one file, in whole clusters. A run that is not a resume takes up no part, and is credited nothing.

### 2. The folder a file lands in still needs all of it

The part is copied to where the file lands once its hash verifies, so that folder needs the whole file, as before.

### 3. A plan and a run measure it alike

- **A plan of a resume** credits what its probe already proved of each part, reading nothing more.
- **A run** measures by logical length first, which names no part and credits nothing. Only when that says it is short does it read the manifests, and then it proves each part as the resume would, writing nothing. A part that does not prove is credited nothing.
- **Through the service,** a resume is measured with the parts of the run it continues, where they lie until it carries them to the name it runs under. **In direct mode,** it is measured with those its destination keeps.

## Consequences

- A resume short of room for a whole file runs where the rest of the file fits.
- A resume that is short by logical length proves its parts twice: once to measure, and again as it takes each up. Both read the state directory, not the store.

## What this does not do

- **Credit a part when measuring by logical length.** That measure reads no manifest, so it names no part. A run it finds short is measured again from its manifests, crediting the parts then.
- **Count the parts of runs left unfinished.** As ADR-0131 has it, they take room until each run is resumed or let go, and the free space measured is what is left beside them.
- **Say what remains.** The need still says it is working space for the run's largest file. With a part held, it is the most that remains of any one file.

## Alternatives considered

- **Credit a part by its length.** Rejected. A part is taken up only as far as it proves. A part of the right length holding something else would be credited room the run then needs.
- **Credit the part where the file lands too.** Rejected. The part is copied there, not moved, so that folder needs all of the file.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A resume's working space needs only what its parts do not yet hold of each file, proved as the resume takes them up, through the service and in direct mode, for its plan and its run. The folder a file lands in still needs all of it. No contract change. |

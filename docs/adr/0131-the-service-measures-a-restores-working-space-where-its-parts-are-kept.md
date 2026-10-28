# ADR-0131 — The service measures a restore's working space where its parts are kept

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: through the service, the working space is where the runs keep their parts)
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (a restore says whether it fits, and Decision 2 measures the engine's working directory), [ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md) (a restore cut short resumes within a file, so the service assembles each file in a part), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The service: `Agent/ServiceRuntime`'s probe of room names the folder its runs keep their parts in as the working directory, for a plan and a run alike.
- The tests: `Hosts.Tests/RestoreHonestyServiceTests` — a plan measures the working space on the state directory's volume, where the parts are kept, and says when it will not fit there; a run whose state directory will not hold its parts is refused and writes nothing; a run is not refused for a temporary folder it does not write to.

---

## Context

A restore is measured before it writes, on each volume it writes to ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)). The measure includes the engine's working directory, which holds a file until its whole-file hash verifies and so needs room for the largest file. That directory was the system's temporary folder, where the engine spooled.

Through the service, each run now assembles every file in a part, under `restore-parts/{run}` in the state directory, so that a resume can take it up ([ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md)). The measure still asked the temporary folder. Where the two are on different volumes, a run could be refused for room it would not use, or pass with none where it would. A service's state kept under a system folder, beside a temporary folder held in memory, is such a machine.

## Decision

### 1. Through the service, the working space is where the runs keep their parts

The service's probe of room names the folder its runs keep their parts in as the working directory. A plan and a run measure it as ADR-0083 measures any working directory: it joins the volume it shares with a folder the run writes to, or is a need of its own, labelled as working space. A folder not yet made is asked about at its nearest existing ancestor.

### 2. Direct mode is unchanged

A run in direct mode keeps no parts. Its engine spools in the system's temporary folder, and that is what it measures.

> **2026-10 ([ADR-0135](0135-direct-mode-resumes-within-a-file.md)).** Direct mode keeps parts now, under the state directory, and its working space is measured where they are kept, as the service's is.

## Consequences

- A run through the service is refused where its parts will not fit, before anything is written. It is not refused for a temporary folder it does not write to.
- A plan names the parts folder as its working space where that folder does not share a volume with the one a person restores to.

## What this does not do

- **Credit what a part already holds.** A resume's working space counts its largest file whole, though the part may hold some of it already. The figure errs high, as ADR-0083's figures do.
- **Count the parts of runs left unfinished.** They take room on the state directory until each run is resumed or let go (ADR-0129). The free space measured is what is left beside them.

## Alternatives considered

- **Assemble a file in the temporary folder again, and keep its part there.** Rejected. A temporary folder may be cleared when the machine starts, which is when a run cut short with the machine needs its parts.
- **Measure both folders.** Rejected. The run writes to one of them only, and a need where nothing is written refuses a run that fits.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The service's probe of room names the folder its runs keep their parts in as the working directory, so a plan and a run measure the working space there. Direct mode measures the temporary folder its engine spools in, as before. No contract change. |
| 2026-10 | Accepted | Direct mode's working space is measured where its parts are kept, by [ADR-0135](0135-direct-mode-resumes-within-a-file.md), as Decision 1 has the service's. |

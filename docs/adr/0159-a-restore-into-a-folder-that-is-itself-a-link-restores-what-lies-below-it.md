# ADR-0159 — A restore into a folder that is itself a link restores what lies below it

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-005 (amended: a restore into a folder that is a link, or lies below one, refuses nothing it would restore into that folder itself)
**Related:** [ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md) (the rule every way of restoring holds a path to), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) (repository path text is untrusted), [the 2026-08 restore pipeline review](../review/2026-08-restore-pipeline-review.md) (RR-2, where the link check began)

**Built:**
- The rule: `Domain/RestorePaths` follows the folder a restore writes into through its own links, and then each folder below it from there, refusing a path at the first folder that leads out of where the root really is. It answers any path followed through its links for other callers to judge by.
- The tests:
  - `Domain.Tests/RestorePathsTests` — a path below the top level of a root that is a link is taken, and so is one below a root that lies below a link; a link inside that leads elsewhere inside is taken by either spelling, the root's own link included; a link that leads out is refused, from a root that is a link too, and so is one that leads out and back in; a path is followed through every link it passes, a relative target from its own folder, and a loop no further than a platform would
  - `Cli.Tests/RestoreShapeCommandTests` — a restore into a folder that is a link lands every file, those below its top level included
  - `Repository.Tests/EndToEnd/RecoveryContainmentTests` — the recovery tool, recovering into an output that is a link, restores what lies below its top level

---

## Context

A restore refuses a path that passes through a folder that is a link leading out of the folder it writes into, so a link planted there cannot carry a write elsewhere ([the 2026-08 review, RR-2](../review/2026-08-restore-pipeline-review.md)). Since [ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md), every way of restoring holds a path to that one rule.

The rule followed the root through its link to fence it, but followed each folder below it only where that folder was itself a link. A folder below a root that is a link was therefore read by its spelling through the root's link, and that spelling never lies inside where the root's link leads. So a restore into a folder that is a link refused every file below the top level, with "the path traverses a symlink that leaves the restore root". A person who keeps the folder they restore to as a link to another disk saw every file in a subfolder fail. The same was true of the recovery tool, and of a link inside the output that led elsewhere inside it by way of the root's link.

## Decision

### 1. A path is followed through every link it passes

The rule follows the root through its links first, then each folder below it in turn from where the one above it really is. Where a folder is a link, its target is followed in the same way, from the folder the link is in, so a target that itself passes through a link lands where that one leads. A chain is followed no further than a platform follows one.

### 2. A path is refused at the first folder that leads out

Each folder is held, as it is followed, to lie inside where the root really is. A path is refused at the first that does not, as before, with the same words. One that leads out and back in is refused at the folder that leads out.

### 3. The root is the person's choice

The root is followed, never judged: wherever its link leads is the place the person chose to restore into. A root that lies below a link, as a temporary folder does on some platforms, is followed in the same way.

The same following is offered for any path, so other judgements of where a restore lands can be made where it really lands.

## Consequences

- A restore into a folder that is a link lands every file, as a restore into the folder it leads to does.
- A link planted inside that folder and leading out of it is still written through by nothing, for the executor and the recovery tool alike.

## What this does not do

- **Fold case.** Each folder is held to the root by its spelling, so on a platform that folds case a link whose target spells the root differently is refused, as before.
- **Make the link that stands at a file's own name the rule's.** The executor and the recovery tool each replace whatever stands at the name a file lands at, as before.

## Alternatives considered

- **Follow only the root, and each folder below it only where it is a link.** Rejected. That was the rule, and a folder below a root that is a link is not itself a link.
- **Ask the platform for the real path of the deepest folder that exists.** Rejected. Windows and POSIX each answer it differently, and a folder not yet made has none; following each link the path passes answers the same way on both and needs nothing to exist.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The rule every way of restoring holds a path to follows the root through its links and each folder below it from there, refusing a path at the first folder that leads out, so a restore into a folder that is a link restores what lies below its top level. |

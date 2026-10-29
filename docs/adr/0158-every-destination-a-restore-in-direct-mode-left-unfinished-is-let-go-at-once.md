# ADR-0158 — Every destination a restore in direct mode left unfinished is let go at once

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: every destination's parts are let go at once, but one with a run under way, and none is let go as a run takes it)
**Related:** [ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md) (the parts are listed and let go one destination at a time), [ADR-0143](0143-a-destination-takes-one-restore-in-direct-mode-at-a-time.md) (a run holds its destination's folder, and `--forget` lets none of a held folder go), [ADR-0157](0157-a-restore-in-direct-mode-is-refused-a-destination-that-nests-with-one-under-way.md) (the claim a run takes its folder under)

**Built:**
- The parts: `Cli/DirectRestoreParts` lets every destination's folder go under one claim, passing over a folder a run holds and anything else beside them, and judges one folder let go by its name under the same claim.
- The verb: `Cli/CliApplication`'s `restore-parts --forget-all` says, through `Cli/DirectRestorePartsReport`, what each folder held, which it kept because a run is under way there, and the whole.
- The tests:
  - `Cli.Tests/RestoreWithinAFileDirectTests` — every destination left unfinished is let go at once, saying what each held; one with a run under way is passed over and every other let go; none kept is said; nothing else under the parts' folder is let go; `--forget` with `--forget-all` is refused and lets nothing go; one destination let go, and every one, waits for a run taking its folder and then spares it; the listing names `--forget-all`

---

## Context

A restore in direct mode keeps the parts of the files it assembles under the state directory, in a folder named by where it lands ([ADR-0135](0135-direct-mode-resumes-within-a-file.md)). A part is the plaintext of a backup's file. `restore-parts` lists every folder, and `--forget <name>` lets one go ([ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md)). A folder a run holds is let go by nothing ([ADR-0143](0143-a-destination-takes-one-restore-in-direct-mode-at-a-time.md)).

ADR-0142 left letting them go other than one at a time undone. A person who will resume none of them named each folder in turn.

`--forget` asked whether a run held the folder, and then removed it. A run could take the folder between the two, and lose what it then assembled. A run takes its folder under one claim of every destination's folder ([ADR-0157](0157-a-restore-in-direct-mode-is-refused-a-destination-that-nests-with-one-under-way.md)), and `--forget` took no claim.

## Decision

### 1. `--forget-all` lets every destination's parts go

`restore-parts --forget-all` removes every destination's folder from the state directory, and says what each held and what they held in all.

- **A folder a run holds** is passed over and keeps its parts. The command says so of each, and counts them.
- **Nothing else kept beside them** is let go, a file or a folder not named as a destination's.
- **A folder not all removed** is named with why, and the rest are let go regardless. The command then fails, naming each.
- **Where none is kept,** it says so and succeeds.
- **`--forget` with `--forget-all`** is refused before anything is let go.

### 2. A folder is let go under the claim a run takes it under

Both `--forget` and `--forget-all` judge whether a run holds a folder, and remove it, under the claim a run takes its folder under. A run starting meanwhile waits for the claim. It then takes its folder afresh, holding none of what was let go. A run that took its folder first holds it, and is passed over.

The listing's last line names `--forget-all` beside `--forget`.

## Consequences

- A person who will resume no restore in direct mode lets every destination's plaintext go in one command.
- A run in direct mode never loses its folder to a person letting parts go as it starts.

## What this does not do

- **Let folders go by age.** Every one is let go, or one by its name.

  > **2026-10 ([ADR-0165](0165-restores-left-unfinished-are-let-go-by-age.md)).** `restore-parts --forget-all --older-than-days <days>` lets go only the destinations whose run began longer ago, and keeps and names one whose run said nothing of when it began.
- **Let the service's parts go.** The service keeps its runs' parts apart, under its own state directory, and lets them go one run at a time by its name.

  > **2026-10 ([ADR-0162](0162-every-restore-run-the-service-left-unfinished-is-let-go-at-once.md)).** `restore-resume --forget-all` lets every run the service left unfinished go at once, each by its name, and its parts with it.

## Alternatives considered

- **Fail where a run is under way.** Rejected. A run under way is the one destination the person did not mean, and passing over it is what `--forget` already does by refusing it.
- **Let folders go by the date their run began.** Rejected for this record. A run's record says when it began, but a folder kept before records were written says nothing of it, and the person who wants that sees each folder's date in the listing and names it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. restore-parts with --forget-all lets every destination's parts go under one claim, passing over one with a run under way and anything else beside them, and says what each held. --forget judges under the same claim. No contract change. |
| 2026-10 | Accepted | Noted by [ADR-0162](0162-every-restore-run-the-service-left-unfinished-is-let-go-at-once.md): every run the service left unfinished is let go at once, which this record left one run at a time. |
| 2026-10 | Accepted | Noted by [ADR-0165](0165-restores-left-unfinished-are-let-go-by-age.md): destinations are let go by age when a person asks, which this record left undone. |

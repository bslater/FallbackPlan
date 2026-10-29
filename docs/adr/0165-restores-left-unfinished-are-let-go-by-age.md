# ADR-0165 — Restores left unfinished are let go by age

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: the runs the service left unfinished, and direct mode's destinations, that began longer ago than a person says are let go at once, and the rest stay)
**Related:** [ADR-0158](0158-every-destination-a-restore-in-direct-mode-left-unfinished-is-let-go-at-once.md) (every destination direct mode left unfinished let go at once), [ADR-0162](0162-every-restore-run-the-service-left-unfinished-is-let-go-at-once.md) (every run the service left unfinished let go at once), [ADR-0164](0164-kept-restore-receipts-are-let-go-by-age.md) (receipts let go by age, the rule this follows, and the record that left these undone)

**Built:**
- The verbs: `Cli/CliApplication`'s `restore-resume --forget-all --older-than-days <days>`, through `Cli/OperationGateway`, lets go only the runs the service left unfinished that began more than that many days ago. `restore-parts --forget-all --older-than-days <days>`, through `Cli/DirectRestorePartsReport` and `Cli/DirectRestoreParts`, lets go only the destinations whose run began that long ago, under the claim a run takes its folder under. Each says how many stay, and a destination whose run said nothing of when it began stays and is named. `Cli/OlderThanDays` is the one cutoff all three verbs judge by.
- The tests:
  - `Cli.Tests/RestoreResumeVerbTests` — only the runs that began longer ago are let go, by their names, and how many stay is said; without `--forget-all`, or below one day, it is refused
  - `Hosts.Tests/RestoreUnfinishedServiceTests` — against a running service, a run begun within the days stays, and its parts with it
  - `Cli.Tests/RestoreWithinAFileDirectTests` — only the destination whose run began longer ago is let go, and how many stay is said; one whose run said nothing of when it began stays and is named; without `--forget-all`, or below one day, it is refused and nothing is let go

---

## Context

The service names each restore it began and has not seen finish, and keeps the parts of the files each was cut short in. Direct mode keeps a destination's parts under the state directory, with a record of what its run restores ([ADR-0133](0133-a-run-left-unfinished-says-what-its-parts-hold.md), [ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md)). Parts are the plaintext of the backup's files. Each run or destination is let go by its name, or every one at once ([ADR-0158](0158-every-destination-a-restore-in-direct-mode-left-unfinished-is-let-go-at-once.md), [ADR-0162](0162-every-restore-run-the-service-left-unfinished-is-let-go-at-once.md)).

[ADR-0164](0164-kept-restore-receipts-are-let-go-by-age.md) let receipts go by when their run ended, when a person asks, and left runs and parts undone. A person who might still resume last week's restore, and never last year's, named each older run in turn or let every one go.

## Decision

### 1. `--older-than-days` lets go what began longer ago

`restore-resume --forget-all --older-than-days <days>` lets go each run the service left unfinished that began more than that many days ago, by its name, as `--forget` lets one go. The service lists when each began, so the contract is unchanged. `restore-parts --forget-all --older-than-days <days>` lets go each destination whose run began that long ago, under the one claim every destination is let go under ([ADR-0158](0158-every-destination-a-restore-in-direct-mode-left-unfinished-is-let-go-at-once.md)). A destination with a run under way is passed over as before.

Each says what it let go and how many, as `--forget-all` does, and how many began within the days and stay. The cutoff is the one receipts are judged by ([ADR-0164](0164-kept-restore-receipts-are-let-go-by-age.md)), so the same days mean the same age to all three verbs.

### 2. A destination whose run said nothing of when it began stays, and is named

A destination's record is what says when its run began. A folder whose record is missing or does not read says nothing of it, and when the folder was last written is not that. It stays, and is named, as an unreadable receipt does ([ADR-0164](0164-kept-restore-receipts-are-let-go-by-age.md)). Every run the service lists says when it began.

### 3. It goes beside `--forget-all`, and takes at least one day

On either verb, `--older-than-days` without `--forget-all`, or below one day, is refused before anything is let go.

## Consequences

- A person keeps the restores they might still resume, and the plaintext their parts hold, and lets the older go in one command.

## What this does not do

- **Expire them on their own.** Nothing goes unless a person asks.
- **Offer it in the console.** The console lets one run go, or every one.

  > **2026-10 ([ADR-0169](0169-the-console-lets-go-by-age-what-began-or-ended-longer-ago.md)).** Done for the service's runs: the snapshots page lets them go by the same days. Direct mode's parts stay the command line's.

## Alternatives considered

- **Take a folder's age from when it was last written.** Rejected, as for receipts ([ADR-0164](0164-kept-restore-receipts-are-let-go-by-age.md)). A part written to an hour ago belongs to a run that may have begun last year, and parts let go are gone.
- **Judge a run by when it was last cut short.** Rejected. When it began is what each run's listing already says, so the age a person reads there is the age they sweep by.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. restore-resume and restore-parts with --forget-all and --older-than-days let go only the runs, and the destinations, that began longer ago, keeping and naming a destination whose run said nothing of when it began. No contract change. |
| 2026-10 | Accepted | Noted by [ADR-0169](0169-the-console-lets-go-by-age-what-began-or-ended-longer-ago.md): the console lets go by age too, by the same days and the same cutoff. |

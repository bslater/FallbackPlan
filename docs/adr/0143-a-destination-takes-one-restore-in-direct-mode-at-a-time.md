# ADR-0143 — A destination takes one restore in direct mode at a time

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: a restore in direct mode holds its destination's folder of parts while it runs, a second run there is refused, and the listing says a run is under way there and lets none of its parts go)
**Related:** [ADR-0135](0135-direct-mode-resumes-within-a-file.md) (direct mode keeps its parts by where a run lands, and the record that left two runs at once to one destination sharing them), [ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md) (the parts are listed and let go, and the record that could not tell a run under way from one left unfinished)

**Built:**
- The hold: `Cli/DirectRestoreParts` takes a destination's folder for one run, and says whether a run holds it.
- The run: `Cli/OperationGateway`'s direct restore holds its folder from before it says what it restores until it ends, and is refused where another run holds it.
- The listing: `Cli/DirectRestorePartsReport` says a run is under way at a destination it lists, and lets none of its parts go.
- The tests:
  - `Cli.Tests/RestoreWithinAFileDirectTests` — a second run to a destination under way is refused before anything is written; a run that ends lets the destination go for the next run there; a destination with a run under way is listed as such, and one left unfinished is not; the parts of a destination with a run under way are not let go

---

## Context

A restore in direct mode keeps its parts in a folder named by where it lands ([ADR-0135](0135-direct-mode-resumes-within-a-file.md)). Two runs at once to the same place shared that folder, as they share the files they write. ADR-0135 left keeping them apart undone. One could assemble a file into a part the other was taking up, and the one that finished first let go of the parts the other was still writing.

`restore-parts` lists each destination left unfinished and lets one go ([ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md)). It could not tell a run under way in another process from one left unfinished, so letting a destination go could cost a run under way what it had assembled.

## Decision

### 1. A run holds its destination's folder while it runs

A run in direct mode takes its folder once it is measured to fit, before it says beside its parts what it restores (ADR-0142) and before it assembles anything. It holds the folder until it ends, however it ends, and lets it go before a run that finishes lets the parts go.

The hold is the operating system's lock on a file in the folder, so it goes with the process if the process dies.

### 2. A second run there is refused

A run whose destination's folder another run holds is refused before it writes anything, naming the folder. A plan writes nothing there and holds nothing.

### 3. The listing says a run is under way, and lets none of its parts go

`restore-parts` says of a destination it lists that a run there is under way. `--forget` refuses that destination's name, and lets nothing go.

> **2026-10 ([ADR-0158](0158-every-destination-a-restore-in-direct-mode-left-unfinished-is-let-go-at-once.md)).** `--forget` asked whether a run held the folder and then removed it, so a run could take the folder between the two. It now does both under the claim a run takes its folder under, as `--forget-all` does.

## Consequences

- Two runs to one destination no longer assemble into the same parts, or let go of each other's.
- A person reading `restore-parts` knows which destinations are left unfinished and which are being restored now.
- A run cut short by the machine stopping leaves its folder unheld, so the next run there, or `--forget`, takes it.

## What this does not do

- **Keep a run to one destination from a run to another that overlaps it.** The folder is named by where each run lands. Two runs to paths inside one another are two destinations, as they were.

  > **2026-10 ([ADR-0157](0157-a-restore-in-direct-mode-is-refused-a-destination-that-nests-with-one-under-way.md)).** A run is now refused where a run under way lands in a folder that holds or lies inside one it lands in, judged under one claim of every destination's folder.
- **Keep a run in direct mode from the service's runs.** The service keeps its parts apart, under `restore-parts`, and holds its own runs.

## Alternatives considered

- **Hold the record a run keeps, not a file of its own.** Rejected. The listing reads the record of a run under way, and a hold on the record would stop it reading it.
- **Write the process's identifier into the folder, and ask whether it lives.** Rejected. An identifier is reused, and asking whether a process lives differs on every platform. A lock the operating system lets go when the process ends needs neither.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A restore in direct mode holds its destination's folder of parts from before its record until it ends; a second run there is refused before it writes anything; restore-parts says a run is under way there and lets none of its parts go. No contract change. |
| 2026-10 | Accepted | Noted by [ADR-0157](0157-a-restore-in-direct-mode-is-refused-a-destination-that-nests-with-one-under-way.md): a run whose destination nests with one under way is refused too, which this record left apart. |
| 2026-10 | Accepted | Noted by [ADR-0158](0158-every-destination-a-restore-in-direct-mode-left-unfinished-is-let-go-at-once.md): --forget judges whether a run holds a folder, and removes it, under the claim a run takes its folder under, so none is let go as a run takes it. |

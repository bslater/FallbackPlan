# ADR-0161 — Every kept restore receipt is let go at once

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: every kept restore receipt is let go at once, from a state directory or through a paired service)
**Related:** [ADR-0139](0139-a-kept-restore-receipt-is-let-go.md) (one run's receipt let go by its name, and the record that left letting them go all at once undone), [ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md) (the service lets one go through the contract), [ADR-0158](0158-every-destination-a-restore-in-direct-mode-left-unfinished-is-let-go-at-once.md) (a restore's parts let go at once, whose shape this follows)

**Built:**
- The receipts: `Restore/RestoreExecutor`'s `RestoreReceipt` lets every run's kept receipt go at once, reading only a file a run's receipt is kept in, and says of each that it could not remove why.
- The verb: `Cli/CliApplication`'s `restore-receipts --forget-all` lets every receipt go from the state directory `--state` names, or, with `--connect`, asks the paired service for every receipt it keeps and to let each go by its run's name, through `Cli/RestoreReceiptReport`. It says each run's and how many.
- The tests:
  - `Cli.Tests/RestoreReceiptsVerbTests` — every run's receipt is let go at once, saying each; nothing else the folder holds is let go; none kept is said; a run named beside it is refused, removing nothing; through a paired service each receipt it lists is let go by its run's name, one it no longer keeps is named while the rest go, and a run named beside it is refused before the service is dialled

---

## Context

Every restore keeps a receipt that names every file its run was asked for, and where each landed. `restore-receipts --run <run> --forget` lets one run's receipt go from the state directory ([ADR-0139](0139-a-kept-restore-receipt-is-let-go.md)), and through a paired service too ([ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md)).

Both records left letting them go by age, or all at once, undone. A person who wanted none of the names they kept named each run in turn.

## Decision

### 1. `--forget-all` lets every receipt go

`restore-receipts --forget-all` removes every run's receipt from the state directory, and says each run's and how many.

- **Only a run's receipt is let go:** a file named by a run's sixteen hex digits and `.json`. One half-written beside it, the peers' receipts in the folder beside, and anything else kept there stay, as the listing passes them over ([ADR-0124](0124-a-kept-restore-receipt-is-read-back.md)).
- **A receipt not removed** is named with why, and the rest go regardless. The command then fails, naming each.
- **Where none is kept,** it says so in the listing's words and succeeds.
- **A run named beside it, or `--forget`,** is refused before anything is let go.

### 2. Through a paired service, each it lists is let go by its name

With `--connect`, the command asks the service for every receipt it keeps and then to let each go by its run's name, as `--forget` asks for one. The contract is unchanged. One the service no longer keeps by the time it is asked is named with the service's words, the rest go regardless, and the command then fails. A run named beside it is refused before the service is dialled.

## Consequences

- A person who wants none of the file names their restores kept lets every receipt go in one command, here or on a paired service.
- A receipt let go is gone, as one let go by its name is.

## What this does not do

- **Let receipts go by age.** Every one is let go, or one by its run's name.
- **Offer it in the console.** The console's Maintenance view lets each receipt go beside it, as before.

  > **2026-10 ([ADR-0163](0163-the-console-lets-every-restore-left-unfinished-and-every-kept-receipt-go-at-once.md)).** The Maintenance view lets every receipt the service keeps go at once, those it does not show too, once a person confirms.

## Alternatives considered

- **A contract command that lets every receipt go at once.** Rejected. The listing and the single let-go already say everything a person would be asked to confirm, and a second way to remove what the service keeps would be a second way to get it wrong.
- **Let receipts go by when their run ended.** Rejected for this record. A receipt that does not read says no end, and the person who wants that sees each run's end in the listing and names it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. restore-receipts with --forget-all lets every run's receipt go from the state directory, and nothing else kept there, or asks a paired service to let each it lists go by its run's name, and says each and how many. No contract change. |
| 2026-10 | Accepted | Noted by [ADR-0163](0163-the-console-lets-every-restore-left-unfinished-and-every-kept-receipt-go-at-once.md): the console lets every receipt the service keeps go at once, which this record left one at a time. |

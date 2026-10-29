# ADR-0140 — The service lets a kept restore receipt go

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a receipt the service keeps is let go through the service, from the command line and the console)
**Related:** [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md) (the service lists the receipts it keeps), [ADR-0128](0128-the-command-line-asks-a-paired-service-for-its-restore-receipts.md) (the command line asks a paired service for them), [ADR-0139](0139-a-kept-restore-receipt-is-let-go.md) (a kept receipt is let go from its state directory, and the record that left the service out), [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md) (a run left unfinished is let go by its name alone, the shape this follows)

**Built:**
- The contract: `Api/Commands`' `ForgetRestoreReceiptCommand`, `forget_restore_receipt` at contract 1.79, names the run alone.
- The service: `Agent/ServiceCommandHandler` lets that run's receipt go by the rule `RestoreReceipt` keeps for a state directory, and no other.
- The command line: `Cli/RestoreReceiptReport` asks a paired service to let a receipt go when `restore-receipts --connect --run <run> --forget` names it.
- The console: the Maintenance view offers each receipt it lists to be let go, once a person confirms.
- The tests:
  - `Api.Tests/ConfigurationContractTests` — contract 1.79, and the command's wire name
  - `Hosts.Tests/RestoreReceiptsServiceTests` — the service lets one run's receipt go by its name and keeps the rest; a run it keeps none of is not found; what is not a run's name is refused and lets nothing go
  - `Cli.Tests/RestoreReceiptsVerbTests` — the command line asks the paired service by the run's name alone, says so, and is refused as the service refuses; what is not a run's name is refused before the service is dialled
  - `Hosts.Tests/RestoreReceiptsRemoteTests` — a paired console lets a receipt the service keeps go by its run alone, the service keeps every other run's, and asking again is refused
  - `Web.DomTests/RestoreReceiptsDomTests` — a receipt is let go only once a person confirms, asking no passphrase, and the list is asked again

---

## Context

The service keeps a receipt of every restore it runs, and lists them ([ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md)). The command line asks a paired service for them ([ADR-0128](0128-the-command-line-asks-a-paired-service-for-its-restore-receipts.md)). [ADR-0139](0139-a-kept-restore-receipt-is-let-go.md) let one go from its state directory, and refused `--forget` with `--connect`. A person at the console, or on another machine, could not let one go at all: they had to reach the service's machine and name its state directory.

## Decision

### 1. The service lets one run's receipt go, by its name alone

`forget_restore_receipt` names a run, at contract 1.79. The service lets that run's receipt go by the rule a state directory's is let go by (ADR-0139): its file alone, and every other run's stays. It answers `acknowledged`.
- **A run it keeps no receipt of** is not found, named.
- **What is not a run's name** is refused, and never joined to a path.

It names none of the backup's files, so it asks for no restore source, as the listing does not.

### 2. The command line asks it

`restore-receipts --connect --run <run> --forget` asks the paired service to let that run's receipt go, and says so. What the service refuses is refused as it says. What is not a run's name is refused before the service is dialled, as `--run` alone is.

### 3. The console offers it

The Maintenance view offers each receipt it lists to be let go, an unreadable one included. Nothing is let go until a person confirms which run's receipt it is. The list is then asked again.

## Consequences

- A person lets a receipt go wherever they read it: from the folder, through a paired service, or in the console.
- A receipt let go through the service is gone from its state directory, as one let go there is.

## What this does not do

- **Let receipts go by age, or all at once.** One run at a time, by its name, as ADR-0139 has it.

  > **2026-10 ([ADR-0161](0161-every-kept-restore-receipt-is-let-go-at-once.md)).** With `--connect`, `--forget-all` asks the service for every receipt it keeps and lets each go by its run's name, with no change to the contract. The console still lets one go at a time.
- **Say what the receipt was of before it goes.** The console's dialog names the run. The listing beside it says what the run did.

## Alternatives considered

- **Let it go by the receipt's position in the listing.** Rejected. A run's name is what every reader of a receipt names it by, and a position changes as runs end.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. forget_restore_receipt at contract 1.79 lets one run's kept receipt go through the service, by the run's name alone, and only that one. restore-receipts with --connect, --run and --forget asks it, and the console's Maintenance view offers it once a person confirms. |
| 2026-10 | Accepted | Noted by [ADR-0161](0161-every-kept-restore-receipt-is-let-go-at-once.md): the command line lets every receipt a paired service keeps go at once, each by its run's name. |

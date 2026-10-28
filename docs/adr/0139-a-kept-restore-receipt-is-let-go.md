# ADR-0139 — A kept restore receipt is let go

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a kept restore receipt is let go by its run's name, from the state directory that keeps it)
**Related:** [ADR-0122](0122-direct-mode-keeps-a-restores-receipt.md) (direct mode keeps a restore's receipt), [ADR-0124](0124-a-kept-restore-receipt-is-read-back.md) (a kept restore receipt is read back, and the first record to leave letting one go undone), [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md) (the service lists the receipts it keeps), [ADR-0128](0128-the-command-line-asks-a-paired-service-for-its-restore-receipts.md) (the command line asks a paired service for its receipts), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The receipt: `RestoreReceipt`, in `Restore/RestoreExecutor`, lets one run's kept receipt go by the run's name, and never joins what is not a run's name to a path.
- The verb: `Cli/CliApplication`'s `restore-receipts --run <run> --forget` lets that run's receipt go from the state directory `--state` names, through `Cli/RestoreReceiptReport`, and says so.
- The tests:
  - `Repository.Tests/RestoreReceiptReadBackTests` — a receipt let go is gone and only its run's is; a run whose receipt is gone keeps none to let go; what is not a run's name lets nothing go
  - `Cli.Tests/RestoreReceiptsVerbTests` — one run's receipt is let go and the rest are kept; a run that kept none, what is not a run's name, `--forget` without a run, and `--forget` through a paired service are refused, and nothing is removed

---

## Context

Every restore keeps a receipt under its state directory: through the service, and in direct mode ([ADR-0122](0122-direct-mode-keeps-a-restores-receipt.md)). `restore-receipts` reads them back ([ADR-0124](0124-a-kept-restore-receipt-is-read-back.md)). The service lists them, and the console shows them ([ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md)). The command line asks a paired service for them ([ADR-0128](0128-the-command-line-asks-a-paired-service-for-its-restore-receipts.md)).

Each record left the same thing undone: every receipt kept stays until a person removes it. A receipt names every file its run was asked for, and where each landed. A person who wants one gone had to find its file under the state directory, and know which file was which run's.

## Decision

### 1. A receipt is let go by its run's name

`restore-receipts --run <run> --forget` removes that run's receipt from the state directory `--state` names, and says so. Every other run's receipt stays. The name is checked to be a run's, sixteen lowercase hex digits, before it reaches a path, as `--run` alone checks it.

### 2. What is refused removes nothing

- **A run that kept no receipt there** is refused, naming it, as reading it back is.
- **What is not a run's name** is refused before it reaches a path.
- **`--forget` without `--run`** is refused: a receipt is let go one run at a time.
- **`--forget` with `--connect`** is refused before the service is dialled. A paired service's receipts are let go on its machine, by naming its state directory.

  > **2026-10 ([ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md)).** It asks the paired service now, at contract 1.79, and what is not a run's name is still refused before the service is dialled.

## Consequences

- A person removes a receipt they no longer want by its run's name, as they read it.
- A receipt let go is gone. The listing no longer counts it, and nothing brings it back.

## What this does not do

- **Let receipts go through the service.** A paired service's receipts are let go on its machine, from its state directory. Neither the contract nor the console lets one go.

  > **2026-10 ([ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md)).** Both do now: `forget_restore_receipt` at contract 1.79, asked by the command line and offered by the console's Maintenance view.
- **Let receipts go by age, or all at once.** One run at a time, by its name.

## Alternatives considered

- **Let the service let its receipts go through the contract now.** Rejected for this record. The folder is the receipts' home, as every reader of them has it, so the command line that reads it lets a receipt go there first. Through the service it would ask a command of its own, and the console a way to confirm one, which are a record of their own.
- **Expire receipts after a time.** Rejected. A receipt is the claim of what a restore did (FR-RST-004), and nothing about its age says it is no longer wanted.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. restore-receipts with --run and --forget lets one run's kept receipt go from the state directory, and says so; every other receipt stays. A run that kept none, a name that is not a run's, --forget without a run, and --forget through a paired service are refused, and nothing is removed. No contract change. |
| 2026-10 | Accepted | [ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md): a receipt the service keeps is let go through the service, at contract 1.79, from the command line and the console; --forget with --connect asks it. |

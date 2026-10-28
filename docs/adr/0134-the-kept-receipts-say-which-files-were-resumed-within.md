# ADR-0134 — The kept receipts say which files were resumed within

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: the listing of kept receipts counts each run's files resumed within, as it counts those found in place)
**Related:** [ADR-0130](0130-a-restore-says-which-files-it-resumed-within.md) (a restore says which files it resumed within, and the record that left the listing silent on it), [ADR-0124](0124-a-kept-restore-receipt-is-read-back.md) (a kept receipt is read back), [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md) (the service lists the receipts it keeps, and the console shows them), [ADR-0128](0128-the-command-line-asks-a-paired-service-for-its-restore-receipts.md) (the command line asks a paired service for them)

**Built:**
- The contract (1.78): the kept-receipt descriptor of `Api/Results` counts a run's files resumed within.
- The service: `Agent/ServiceCommandHandler` counts them from each receipt it lists.
- The clients: `Cli/RestoreReceiptReport`, listing a state directory's receipts or a paired service's, and the console script of `FallbackPlan.Web` say how many.
- The tests:
  - `Cli.Tests/RestoreReceiptsVerbTests` — a run listed from the folder says how many of its files were resumed within, beside those found in place or alone, and so does one listed through a paired service
  - `Hosts.Tests/RestoreUnfinishedServiceTests` — the kept receipt of a resume that took up its parts is listed with the files it resumed within
  - `Api.Tests/ConfigurationContractTests` — contract 1.78's wire name, and a pre-1.78 receipt that says none
  - `Web.DomTests/RestoreReceiptsDomTests` — the console's Maintenance view says how many of a run's files were resumed within

---

## Context

A resume through the service takes up the part of each file its run was cut short in, and the receipt, at schema 11, says where each such file was resumed ([ADR-0130](0130-a-restore-says-which-files-it-resumed-within.md)). The run's result counts those files. The listing of kept receipts, from the command line and the console alike, counts what became of each run's files: restored, with those found in place among them, skipped, failed and degraded ([ADR-0124](0124-a-kept-restore-receipt-is-read-back.md), [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md)). ADR-0130 left it silent on files resumed within, since such a file is a file restored. So did a file found in place, which the listing counts.

## Decision

### 1. The listing counts each run's files resumed within

Each receipt the listing names says how many of its run's files were resumed within, counted from its items as those found in place are: the items that say where they were resumed. The service's listing carries the count as `resumed_within` (contract 1.78).

### 2. They are counted among the files restored

The command line and the console say the count beside the files restored, with those found in place: "3 restored (1 found in place, 1 resumed within)". A run that resumed none within says nothing of it.

## Consequences

- A person reading the receipts kept can see which runs picked a file up where an earlier run stopped, without reading each receipt whole.
- A file found in place, a file resumed within and a file written whole are told apart in the listing, as in the receipt and the result.

## What this does not do

- **Name the files.** The listing names none of the backup's files, and how far into each a resume went is the receipt's, read whole.
- **Count a receipt kept before schema 11.** It says nothing of where a file was resumed, so the listing counts none.

## Alternatives considered

- **Count them apart from the files restored.** Rejected. A file resumed within was restored, as a file found in place was; counting it apart would make the restored figure differ from the receipt's.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The listing of kept receipts counts each run's files resumed within from its items, carried by the service as contract 1.78's `resumed_within`; the command line, from the folder or a paired service, and the console say it among the files restored. |

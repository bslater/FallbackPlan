# ADR-0122 — Direct mode keeps a restore's receipt

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a restore in direct mode keeps its receipt where the service keeps one)
**Related:** [ADR-0119](0119-direct-mode-restores-to-the-original-location.md) (the record that left direct mode's receipt unkept), [ADR-0028](0028-service-boundary-and-deployment-topologies.md) (the same operation performs identically through either path), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the service's receipts, merged across a set's roots), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification)

**Built:**
- The keeper: the receipt in `Restore/RestoreExecutor` keeps itself as its run's own file under a state directory's receipts, written whole beside its place and moved there. The service's `Agent/ServiceCommandHandler` keeps its receipts through it.
- Direct mode: `Cli/OperationGateway` keeps each direct run's receipt under `Cli/CliSession`'s receipts folder, the one the service keeps its in, and says where.
- The tests:
  - `Repository.Tests/RestoreReceiptKeptTests` — a receipt kept is its run's own file and reads back as written, with nothing left beside it, and one kept again under its run is replaced whole
  - `Cli.Tests/RestoreReceiptDirectTests` — in direct mode a restore keeps its receipt under the state directory, named by its run, and says where; two restores keep two; a restore refused before it begins, and a plan, keep none
  - `Cli.Tests/RestoreOriginalDirectTests` — a restore to the original location of several roots keeps one receipt, each path under its label

---

## Context

A restore through the service keeps its receipt under the state directory's receipts, named by its run (FR-RST-004), so "the restore succeeded" stays a claim a person can check after the dialog closes. A restore in direct mode built the same receipt with the same executor, printed what it said, and kept nothing ([ADR-0119](0119-direct-mode-restores-to-the-original-location.md) recorded it as undone). Once its terminal scrolled away, a direct run's receipt was gone.

## Decision

### 1. One keeper for both paths

The receipt keeps itself: as `{run}.json` under the folder a state directory keeps receipts in, written beside its place and moved there, so a reader never meets half of one. A receipt kept again under its run replaces the last. The service keeps its receipts through it, as it kept them before.

### 2. Direct mode keeps each run's receipt where the service keeps one

Direct mode works in a state directory the service would use, so it keeps its receipt in that directory's receipts folder, under the run's identifier, which every run is given. A run of several roots keeps its one merged receipt. The report says where it was kept.

### 3. Only a run that began keeps one

A restore refused before it begins wrote nothing, and keeps no receipt. A plan restores nothing, and keeps none.

## Consequences

- A direct run's receipt outlives the terminal it was printed in, as a service run's does.
- The service and direct mode keep receipts alike, in the same place, because they keep them with the same code.

## What this does not do

- **List the receipts.** Each is a file a person or a script reads; no verb reads them back.
  > **2026-10 ([ADR-0124](0124-a-kept-restore-receipt-is-read-back.md)).** `restore-receipts` reads them back: each run newest first, with what it did and where it wrote, or one run's receipt whole.
- **Keep a run in the service's journal.** A direct run lands in place and names no run to resume (ADR-0109), so it is not listed as left unfinished.

## Alternatives considered

- **Keep the receipt beside the files restored.** Rejected. It would be one more file among the person's, under the folder they asked for, and the original location has no one folder to put it in.
- **Keep it only when asked.** Rejected. The service keeps every one, and a receipt that is kept only sometimes cannot be relied on to be there.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The receipt keeps itself as its run's own file under a state directory's receipts, written whole; the service keeps its receipts through it, and direct mode now keeps each run's receipt there too and says where, while a run refused before it begins and a plan keep none. |
| 2026-10 | Accepted | [ADR-0124](0124-a-kept-restore-receipt-is-read-back.md): the receipts kept are read back by `restore-receipts`, which this record left undone. |

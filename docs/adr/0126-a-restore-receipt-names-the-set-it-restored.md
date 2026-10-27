# ADR-0126 — A restore receipt names the set it restored

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a receipt names the set whose snapshot its run restored)
**Related:** [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md) (the service lists the receipts it keeps), [ADR-0124](0124-a-kept-restore-receipt-is-read-back.md) (a kept receipt is read back), [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md) (a run left unfinished is listed with its set), [ADR-0026](0026-phase-1-capture-shapes.md) (the receipt is versioned JSON, Decision 10), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification)

**Built:**
- The receipt: `Restore/RestoreExecutor`'s receipt is at schema 9, and names the set whose snapshot its run restored, where the run knew it.
- The service: `Agent/ServiceCommandHandler` names the set its run was asked through, and its listing answers it (contract 1.74).
- Direct mode: `Cli/OperationGateway` names the set this state's configuration has the snapshot as a snapshot of, and names none where the configuration has none or does not read; `Cli/RestoreReceiptReport` says it.
- The page: the console script of `FallbackPlan.Web` shows it in the Maintenance view's list of restores.
- The tests:
  - `Repository.Tests/RestoreReceiptKeptTests` — a receipt names the set its run restored, at schema 9, and one that knew none says nothing of one
  - `Repository.Tests/RestoreBreadthTests` — the receipt JSON pinned whole at schema 9
  - `Cli.Tests/RestoreOriginalDirectTests` — a run of a set's snapshot names the set in its receipt, to the original location or to a folder
  - `Cli.Tests/RestoreReceiptDirectTests` — a snapshot of no set configured here names none, and a configuration that does not read names none and stops no restore
  - `Cli.Tests/RestoreReceiptsVerbTests` — `restore-receipts` says the set a receipt names, and nothing where it names none
  - `Hosts.Tests/RestoreReceiptsServiceTests` — a restore through the service names its set, in the receipt and in the listing
  - `Api.Tests/ConfigurationContractTests` — contract 1.74's `set`, and a pre-1.74 row that names none
  - `Web.DomTests/RestoreReceiptsDomTests` — the console names each restore's set

---

## Context

The restore receipts a state directory keeps are read back by run ([ADR-0124](0124-a-kept-restore-receipt-is-read-back.md)), and the service lists them for the console ([ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md)). A receipt said which snapshot its run restored, by a hex identifier, and nothing said whose. A person looking down a list of earlier restores to find the one of their photos had only the time it ended to go by. The runs left unfinished are listed with their set ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)); the receipts of runs that finished were not.

## Decision

### 1. The receipt names the set, at schema 9

The receipt gains `set`: the name of the set whose snapshot the run restored, as the run knew it. It is written only where the run knew one, so a receipt that names none says nothing of a set rather than naming nothing. Schema 9 is the version that added it, as every field the receipt gained has had one.

### 2. Each path names the set it knows

- **Through the service,** the run names the set it was asked through: the restore source's, or the set whose staging archive it read.
- **In direct mode,** the run names the set this state's configuration has the snapshot as a snapshot of. Where the configuration has none, or does not read, it names none. Naming a set never stops a restore.

### 3. The listings say it

- `restore-receipts` says the set after the snapshot it restored.
- The service's listing answers it as `set` (contract 1.74).
- The console shows it in its own column.

A receipt kept before schema 9 names none, and reads as it did.

## Consequences

- A person reading back earlier restores sees whose each was, not only when it ended.
- The service and direct mode name the set alike in the receipt they keep.

## What this does not do

- **Name the set of a receipt kept before schema 9.** Nothing rewrites a kept receipt, and the snapshot's identifier says no more than it did.
- **Say which destination a run read from.** A run may read some files from one copy and some from another, and the items that were read around say so already.

## Alternatives considered

- **Look the set up when the receipt is read.** Rejected. Configuration changes, a set can be renamed or removed, and a receipt that named the set as it is now would not say what the run restored then. The receipt records what the run knew.
- **Name the set by its identifier.** Rejected. The identifier is no more use to a person than the snapshot's, and the name is what the configuration, the console and the listing of unfinished runs all show.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The receipt at schema 9 names the set whose snapshot its run restored, where the run knew it: through the service, the set it was asked through; in direct mode, the set this state's configuration has the snapshot as, or none. `restore-receipts`, the service's listing (contract 1.74) and the console say it. |

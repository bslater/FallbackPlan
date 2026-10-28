# ADR-0125 — The service lists the restore receipts it keeps

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: the service answers the restore receipts it keeps, and the console shows them)
**Related:** [ADR-0124](0124-a-kept-restore-receipt-is-read-back.md) (a kept restore receipt is read back, and the record that left the service's listing undone), [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md) (the runs left unfinished, listed without the backup's file names), [ADR-0089](0089-a-backups-file-names-need-the-passphrase.md) (a backup's file names reach a person only through a source its passphrase unlocked), [ADR-0064](0064-replication-receipts.md) (the Receipts card), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification)

**Built:**
- The contract (1.73): `Api/ListRestoreReceiptsCommand` asks for the restore receipts the service keeps, the newest under a limit, and `Api/RestoreReceiptsResult` answers each as `Api/RestoreReceiptDescriptor` describes it, with how many are kept.
- The service: `Agent/ServiceCommandHandler` reads the receipts under its state directory through the reader `restore-receipts` uses, and answers what became of each run's files and where it wrote them, never the paths.
- The page: the console script of `FallbackPlan.Web` shows them in the Maintenance view, beside the peers' receipts.
- The tests:
  - `Api.Tests/ConfigurationContractTests` — contract 1.73's wire names, and a listing that names no path
  - `Hosts.Tests/RestoreReceiptsServiceTests` — the service lists the receipts it keeps newest first, naming none of the backup's files and saying which does not read; answers each as what became of its files and where it wrote; lists the newest under a limit with how many are kept, and refuses one below 1
  - `Web.Tests/ConsoleRestoreReceiptsScriptTests` — the card is filled from the listing, an older service is no error, it is fetched on entering the view and never by the pollers, and a row the service could not read says so
  - `Web.DomTests/RestoreReceiptsDomTests` — the Maintenance view lists each restore with what became of its files and where it wrote, the newest under the limit it asks with and how many are kept, and says nothing of the rest when it shows them all; a service that kept none says so; a service before the listing is an empty card and no error

---

## Context

A kept restore receipt is read back by `restore-receipts`, from the state directory itself ([ADR-0124](0124-a-kept-restore-receipt-is-read-back.md)). That needs the files: a person at the service's machine, with the state directory in reach. The console reaches the service from anywhere it is paired, and once a run's result was closed it had no way to say what that restore, or any earlier one, did. ADR-0124 recorded this as undone.

A receipt names every file its run was asked for, and those paths are the backup's file names, which reach a person only through a source the set's passphrase unlocked ([ADR-0089](0089-a-backups-file-names-need-the-passphrase.md)). The runs left unfinished are listed without them for the same reason ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)).

## Decision

### 1. The service answers each receipt as what became of its run's files

`list_restore_receipts` answers each receipt the service keeps, newest first by when each run ended, as:
- its run;
- its outcome, when it ended, and the snapshot it restored;
- how many of its files were restored, found in place by a resume, skipped, failed and degraded;
- where it wrote them.

The paths it restored are never answered, so the listing needs no source and no passphrase. A receipt that does not read is answered with why, and nothing else.

### 2. A limit bounds the listing, and the total counts every receipt kept

The console asks for the newest fifty. The answer says how many are kept, so a page showing fewer can say so. A limit below one is refused.

### 3. The console shows them in the Maintenance view

They sit beside the peers' receipts, read on entering the view and never by the pollers, since a restore's receipt changes only when a restore ends. A service before 1.73 refuses the verb by name, and that is an empty card, not an error.

## Consequences

- A person at the console can see what each earlier restore did, and where it wrote, after its result is closed.
- The console learns nothing of the backup's files from it that a person without the passphrase could not already see.

## What this does not do

- **Show a receipt's files.** Which files a run restored, and what became of each, stay in the receipt on the service's machine, read with `restore-receipts --run`.
  > **2026-10 ([ADR-0127](0127-what-a-restore-did-not-restore-is-shown-through-the-passphrase.md)).** The files a run did not restore are shown, through a source of the set the receipt records, behind its passphrase.
- **Let the command line ask the service for them.** `restore-receipts` reads the state directory, as before.
  > **2026-10 ([ADR-0128](0128-the-command-line-asks-a-paired-service-for-its-restore-receipts.md)).** `restore-receipts --connect` asks a paired service for the receipts it keeps, and for what one run did not restore through the passphrase.
- **Let receipts go.** Every receipt kept stays until a person removes it.

  > **2026-10 ([ADR-0139](0139-a-kept-restore-receipt-is-let-go.md)).** From the service's state directory, on its machine, `restore-receipts --run <run> --forget` lets one go. Neither the contract nor the console lets one go.

  > **2026-10 ([ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md)).** The service lets one go through the contract now, `forget_restore_receipt` at 1.79, and the console offers it beside each receipt.

## Alternatives considered

- **Answer the whole receipt through a restore source.** Rejected for now. A run's files are behind the passphrase, but the question a person asks first is what each restore did and where it wrote, which needs none of them.
- **Fold the restores into the Receipts card.** Rejected. That card shows what peers attested under their own signatures, checked again as it is read, and a restore receipt is signed by no one.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Contract 1.73's `list_restore_receipts` answers each restore receipt the service keeps, newest first under a limit with how many are kept, as what became of its files and where it wrote them, never the paths; the console shows them in the Maintenance view. |
| 2026-10 | Accepted | [ADR-0126](0126-a-restore-receipt-names-the-set-it-restored.md): each receipt listed names the set its run restored (contract 1.74). |
| 2026-10 | Accepted | [ADR-0127](0127-what-a-restore-did-not-restore-is-shown-through-the-passphrase.md): the files a run did not restore are shown through the passphrase, which this record left undone. |
| 2026-10 | Accepted | [ADR-0128](0128-the-command-line-asks-a-paired-service-for-its-restore-receipts.md): the command line asks a paired service for the receipts it keeps, which this record left undone. |
| 2026-10 | Accepted | [ADR-0134](0134-the-kept-receipts-say-which-files-were-resumed-within.md): each receipt listed carries its run's files resumed within (contract 1.78), and the console says them. |
| 2026-10 | Accepted | [ADR-0139](0139-a-kept-restore-receipt-is-let-go.md): a receipt the service keeps is let go from its state directory, on its machine; the contract and the console let none go. |
| 2026-10 | Accepted | [ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md): the service lets a receipt it keeps go by the run's name, at contract 1.79, and the console's Maintenance view offers it. |

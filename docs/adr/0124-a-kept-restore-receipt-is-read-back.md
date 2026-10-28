# ADR-0124 — A kept restore receipt is read back

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: the restore receipts a state directory keeps are read back)
**Related:** [ADR-0122](0122-direct-mode-keeps-a-restores-receipt.md) (direct mode keeps a restore's receipt, and the record that left them unlisted), [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md) (every run is named), [ADR-0063](0063-deletion-receipts.md) and [ADR-0064](0064-replication-receipts.md) (the peers' receipts, and the `receipts` verb that reads them), [ADR-0026](0026-phase-1-capture-shapes.md) (the receipt, Decision 10), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification)

**Built:**
- The reader: the receipt in `Restore/RestoreExecutor` reads back the receipts kept under a state directory's receipts, newest first by when each run ended, each under its run. A run's file that does not read as a receipt is answered with why. The same code says what names a run, and `Agent/ServiceCommandHandler` takes a run's name by it.
- The command line: `Cli/CliApplication`'s `restore-receipts`, written by `Cli/RestoreReceiptReport`, lists the receipts kept under a state directory, or prints one run's whole.
- The tests:
  - `Repository.Tests/RestoreReceiptReadBackTests` — the receipts kept read back newest first, each under its run; a file under a run's name that is no receipt is unreadable and the rest still read; what is not a run's receipt is passed over, the peers' receipts beside included; a run's name is sixteen lowercase hex digits
  - `Cli.Tests/RestoreReceiptsVerbTests` — `restore-receipts` lists the receipts kept newest first with when each run ended, what became of its files and where it wrote them; prints one run's receipt whole as it was kept; refuses a run that kept none, and a name that is not a run's before it reaches a path; lists the newest under a limit and says how many are kept; says a receipt that does not read is unreadable, beside the rest; refuses a state directory that does not exist, and says one that keeps none keeps none
  - `Hosts.Tests/RestoreReceiptsServiceTests` — a restore through the service is read back under the run the service named, and its receipt printed whole as the service kept it

---

## Context

A restore keeps its receipt under the state directory, as its run's own file, through the service and in direct mode alike ([ADR-0122](0122-direct-mode-keeps-a-restores-receipt.md)). Nothing read them back. A person who wanted to know what an earlier run restored, and where it wrote, had to find the state directory's receipts folder and open the files one by one, each named by sixteen hex digits that say nothing about which restore it was. ADR-0122 recorded the listing as undone.

The `receipts` verb reads a state directory's receipts folder already, but only the peers' signed receipts in the folders beside the restore receipts ([ADR-0063](0063-deletion-receipts.md), [ADR-0064](0064-replication-receipts.md)), and it promises that every fact it prints comes from signed bytes and is checked again as it is read. A restore receipt is signed by nobody.

## Decision

### 1. One reader of the receipts a state directory keeps

The receipt reads back the receipts kept under a state directory, newest first by when each run ended, each under its run. Only a run's own file is read: one named by the run's sixteen lowercase hex digits and `.json`. A receipt half-written beside its place, the peers' receipts in the folders beside, and anything else left there are passed over.

### 2. A receipt that does not read is said not to

A run's file that does not read as a receipt is listed as unreadable, with why, by when it was written. It does not stop the rest being read, and it is not dropped from the list.

### 3. `restore-receipts` reads them, apart from `receipts`

The command line's `restore-receipts` takes the state directory to read, the service's or one direct mode used, since the two need not be the same. It lists each run with:
- its outcome, when it ended, and the snapshot it restored;
- how many of its files were restored, skipped, failed and degraded, and how many of those restored a resume found in place;
- where it wrote them.

It closes with how many are kept, and `--limit` lists only the newest. `--run` prints one run's receipt whole, as it was kept, for a script to read.

A name that is not a run's is refused before it is joined to a path, as the service refuses one, by the same rule. A state directory that does not exist is refused by its path, so a mistyped one is not taken for one where nothing was restored.

## Consequences

- What a restore wrote, and where, can be asked after the terminal or the dialog that showed it is gone, by run, without opening a file.
- A receipt the service kept and one direct mode kept read alike, because one reader reads both.

## What this does not do

- **Read the receipts through the service.** The verb reads the state directory itself, as `receipts` does. A console on another machine still sees a run's receipt only in that run's result.
  > **2026-10 ([ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md)).** The service answers them, as what became of each run's files and where it wrote them, and the console shows them in its Maintenance view.
- **List them as JSON.** `--run` prints one receipt as JSON, as it was kept, and a script that wants them all reads the folder.

  > **2026-10 ([ADR-0141](0141-the-kept-restore-receipts-are-listed-as-json.md)).** `--json` lists them now, each as the service answers it, from the folder and through a paired service alike.
- **Let receipts go.** Every receipt kept stays until a person removes it.

  > **2026-10 ([ADR-0139](0139-a-kept-restore-receipt-is-let-go.md)).** `restore-receipts --run <run> --forget` lets one run's receipt go from the state directory, and every other run's stays.

## Alternatives considered

- **Add a restore kind to `receipts`.** Rejected. That verb vouches for every fact it prints from signed bytes checked again as it reads, and a restore receipt is signed by no one. Putting the two under one name would lend the restore receipt a check it never had.
- **Print every receipt whole, one after another.** Rejected. A person asking what earlier restores did wants a line for each run, not every file of each, and `--run` prints the one they then pick.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The receipt reads back the receipts a state directory keeps, newest first, each under its run, saying which do not read and passing over what is not a run's; `restore-receipts` lists them with what each run did and where it wrote, or prints one whole, through no service. |
| 2026-10 | Accepted | [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md): the service answers the receipts it keeps, and the console shows them, which this record left undone. |
| 2026-10 | Accepted | [ADR-0128](0128-the-command-line-asks-a-paired-service-for-its-restore-receipts.md): with `--connect`, `restore-receipts` asks a paired service for the receipts it keeps, and `--state` names the console's own state; without it, the verb reads the folder as this record decided. |
| 2026-10 | Accepted | [ADR-0134](0134-the-kept-receipts-say-which-files-were-resumed-within.md): each run listed counts its files resumed within beside those found in place. |
| 2026-10 | Accepted | [ADR-0139](0139-a-kept-restore-receipt-is-let-go.md): the verb lets one run's kept receipt go by the run's name, from the state directory it reads. |
| 2026-10 | Accepted | [ADR-0141](0141-the-kept-restore-receipts-are-listed-as-json.md): the verb lists the receipts kept as JSON with --json, each as the service describes it. |

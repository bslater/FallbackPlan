# ADR-0141 — The kept restore receipts are listed as JSON

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: the kept restore receipts are listed as JSON, from a state directory and through a paired service alike)
**Related:** [ADR-0124](0124-a-kept-restore-receipt-is-read-back.md) (a kept restore receipt is read back, and the record that left a JSON listing undone), [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md) (the service lists the receipts it keeps, in the shape this listing takes), [ADR-0128](0128-the-command-line-asks-a-paired-service-for-its-restore-receipts.md) (the command line asks a paired service for them)

**Built:**
- The listing: `Cli/RestoreReceiptReport` writes the receipts a state directory keeps, or a paired service answers, as JSON, each as the service describes it.
- The verb: `Cli/CliApplication`'s `restore-receipts --json` asks for it, under `--limit` as the listing in words is.
- The tests:
  - `Cli.Tests/RestoreReceiptsVerbTests` — each receipt kept is listed as JSON as the service answers it, newest first, an unreadable one saying why and none naming a file of the backup; a limit lists the newest and the total counts the rest; none kept is an empty list; a paired service's answer is listed as it answers
  - `Hosts.Tests/RestoreReceiptsServiceTests` — listed as JSON from the service's own state directory, the receipts read as the service's answer, field for field
  - `Hosts.Tests/RestoreReceiptsRemoteTests` — a paired console lists the receipts the service keeps as JSON, newest first, naming no file

---

## Context

`restore-receipts` lists the receipts a state directory keeps in words, and prints one run's receipt whole as JSON, as it was kept ([ADR-0124](0124-a-kept-restore-receipt-is-read-back.md)). The service answers its own as descriptors at contract 1.73 ([ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md)), and the command line asks a paired service for them ([ADR-0128](0128-the-command-line-asks-a-paired-service-for-its-restore-receipts.md)), again in words. A script that wanted every receipt had to read the folder and every file in it. ADR-0124 recorded a JSON listing as undone.

## Decision

### 1. `--json` lists the receipts as the service answers them

`restore-receipts --json` writes the listing as one JSON document: `receipts`, each as the service describes it, and `total`, how many are kept. That shape holds whether the receipts are read from the state directory `--state` names or asked of a paired service with `--connect`. Each says what became of its run's files and where it wrote them, never the paths, which are the backup's file names. A receipt that does not read says why. `--limit` lists the newest, as it does in words.

### 2. One run's receipt is printed as it was kept

`--run` already prints one receipt as JSON, whole and as it was kept. `--json` beside it changes nothing.

## Consequences

- A script reads every receipt a state directory keeps, or a paired service keeps, in one document of one shape.
- The command line describes a receipt from the folder as the service does. A test holds the two to the same answer for the same state directory, so neither drifts from the other.

## What this does not do

- **Print every receipt whole.** The listing is each receipt's account, as the service gives it. A receipt whole, with the backup's file names, is still asked for by its run.
- **Change the listing in words.** It reads as before.

## Alternatives considered

- **Print each kept file as it was kept, one after another.** Rejected. They name the backup's files, which the listing never does, and a paired service answers no such thing.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. restore-receipts with --json lists the kept receipts as one JSON document, each as the service describes it, from a state directory or a paired service alike, under --limit, with how many are kept. No contract change. |

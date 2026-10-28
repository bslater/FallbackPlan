# ADR-0128 — The command line asks a paired service for its restore receipts

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a paired console's command line reads the receipts the service keeps), FR-WOR-007 (amended: the command line asks a receipt's files only with the passphrase)
**Related:** [ADR-0124](0124-a-kept-restore-receipt-is-read-back.md) (`restore-receipts` reads a state directory's receipts), [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md) (the service lists the receipts it keeps, and the record that left the command line reading the folder), [ADR-0127](0127-what-a-restore-did-not-restore-is-shown-through-the-passphrase.md) (what a run did not restore is answered through the passphrase), [ADR-0028](0028-service-boundary-and-deployment-topologies.md) (the remote binding, §5), [ADR-0089](0089-a-backups-file-names-need-the-passphrase.md) (a backup's file names need the passphrase), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification)

**Built:**
- The verb: `Cli/CliApplication`'s `restore-receipts` takes `--connect`, `--fingerprint` and `--passphrase-env`. With `--connect` it asks the paired service, and `--state` names the console's own state, as it does for every verb that dials one. A name that is not a run's, and a limit below one, are refused before the service is dialled.
- The report: `Cli/RestoreReceiptReport` writes the receipts the service keeps as it writes a state directory's, and one run's files that it did not restore, through a source of the set its receipt records. It closes the source after.
- The tests:
  - `Hosts.Tests/RestoreReceiptsRemoteTests` — a paired console lists the receipts the service keeps, newest first under a limit and not its own, naming no file; one run's files that did not come back are shown through the passphrase, under a limit with a count of the rest, and the source is closed after; a run that restored everything says so; without the passphrase, for a receipt that records no set, and for a run the service keeps no receipt of, the answer is a refusal naming no file
  - `Cli.Tests/RestoreReceiptsVerbTests` — through a paired service, a name that is not a run's and a limit below one are refused before the service is dialled

---

## Context

`restore-receipts` reads the receipts a state directory keeps ([ADR-0124](0124-a-kept-restore-receipt-is-read-back.md)). The service lists those it keeps ([ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md)) and answers what a run did not restore through the passphrase ([ADR-0127](0127-what-a-restore-did-not-restore-is-shown-through-the-passphrase.md)), and the console shows both. A person at a paired console's command line still had only the folder, which is on the service's machine. ADR-0125 recorded this as undone.

## Decision

### 1. `restore-receipts --connect` asks the paired service

With `--connect`, `restore-receipts` dials the paired service over the remote binding, as every verb that takes it does. `--state` then names the console's own state, its peer identity and pairings, and not a folder to read. Without `--connect` the verb reads the folder `--state` names, as before.

### 2. The listing is the service's, written as a folder's is

The verb lists the receipts the service keeps, newest first under `--limit`, with how many it keeps. Each is written as a state directory's receipt is: its run, its outcome, when it ended, the snapshot and its set, the counts of its files, and where it wrote them. It names none of the backup's files, so it asks no passphrase.

### 3. One run's files come through the passphrase

With `--run`, the verb asks what the service's receipt of that run says, and the files its run failed, skipped or degraded, each with the receipt's words for why. They come under `--limit`, with how many more there are. They are asked through a source of the set the receipt records, which the passphrase `--passphrase-env` names unlocks, and the source is closed after.

It refuses, naming no file:
- a receipt that records no set, before any passphrase is asked, saying it is read on the service's machine with `restore-receipts --state` and `--run`;
- a run the service keeps no receipt of;
- the files asked without the passphrase, naming the flag.

A name that is not a run's, and a limit below one, are refused before the service is dialled.

## Consequences

- A person at a paired console's command line reads the receipts the service keeps, and what a run did not restore, without the service's machine.
- The command line and the console ask the same questions of the service, behind the same gate.

## What this does not do

- **Ask a service on this machine.** Without `--connect` the verb reads the folder `--state` names, as before; a service here is read through its own state directory.
- **Print a receipt whole through the service.** The service answers what a run did not restore, not every file it was asked for; the receipt is printed whole from the folder.
- **Let receipts go.** Every receipt kept stays until a person removes it.

  > **2026-10 ([ADR-0139](0139-a-kept-restore-receipt-is-let-go.md)).** From a state directory, one run's receipt is let go with `--run <run> --forget`. With `--connect`, `--forget` is refused before the service is dialled.

  > **2026-10 ([ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md)).** With `--connect`, `--forget` asks the paired service to let the run's receipt go, at contract 1.79.

## Alternatives considered

- **Ask a service on this machine when `--state` names its state directory.** Rejected. The verb would read the same folder or ask the service depending on whether one was listening, and a person could not tell which answer they had.
- **Give the remote listing its own verb.** Rejected. It is the same question asked of another place, and every other verb that dials a paired service takes `--connect`.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. `restore-receipts --connect` lists the receipts a paired service keeps, as a state directory's are listed, and with `--run` the files that run did not restore, through a source of the set its receipt records that the passphrase unlocks, closed after. No contract change. |
| 2026-10 | Accepted | [ADR-0139](0139-a-kept-restore-receipt-is-let-go.md): --forget lets a receipt go from a state directory only, and is refused with --connect before the service is dialled. |
| 2026-10 | Accepted | [ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md): with --connect, --run and --forget ask the paired service to let that run's receipt go. |

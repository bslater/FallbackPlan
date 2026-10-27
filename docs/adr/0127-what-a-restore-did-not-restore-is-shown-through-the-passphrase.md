# ADR-0127 — What a restore did not restore is shown through the passphrase

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a kept receipt's files not restored are answered through the passphrase), FR-WOR-007 (amended: a receipt's files are a look at the backup's files)
**Related:** [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md) (the receipts listed without the backup's file names, and the record that left their files unshown), [ADR-0126](0126-a-restore-receipt-names-the-set-it-restored.md) (a receipt names its set), [ADR-0089](0089-a-backups-file-names-need-the-passphrase.md) (a backup's file names need the passphrase), [ADR-0026](0026-phase-1-capture-shapes.md) (the receipt is versioned JSON, Decision 10), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification)

**Built:**
- The receipt: `Restore/RestoreExecutor`'s receipt is at schema 10 and records the identity of the set it names. One run's receipt is read alone by its run.
- The contract (1.75): `Api/GetRestoreReceiptCommand` asks for one kept receipt's files that its run did not restore, through a source. `Api/RestoreReceiptResult` answers them, each as `Api/RestoreReceiptItemDescriptor` describes it, with how many more there are. `Api/RestoreReceiptDescriptor` says the set's identity.
- The service: `Agent/ServiceCommandHandler` records the identity of the set a run was asked through. It answers a receipt's files only through a source of that set, unlocked by the session that asks, and refuses a receipt that records no set.
- Direct mode: `Cli/OperationGateway` records the identity of the set it names.
- The page: the console script of `FallbackPlan.Web` offers, on each restore in the Maintenance view that left files behind, to show them, behind the passphrase. It closes the source after.
- The tests:
  - `Repository.Tests/RestoreReceiptKeptTests` — a receipt records its set's identity at schema 10, and one that knew no set records none; one run's receipt is read alone by its run, and a name that is not a run's is never joined to a path
  - `Hosts.Tests/RestoreReceiptFilesServiceTests` — the files a run did not restore are answered through a source of the set its receipt records, under a limit with a count of the rest; without a source, through another set's, for a receipt that records no set, for a run that kept none and for a name that is not a run's, the answer is a refusal naming no file; a restore through the service records the set's identity, and its listing says it
  - `Cli.Tests/RestoreOriginalDirectTests` — a run of a set's snapshot records the set's identity in its receipt
  - `Api.Tests/ConfigurationContractTests` — contract 1.75's wire names
  - `Web.Tests/ConsoleRestoreReceiptsScriptTests` — what a run did not restore is asked only through an unlocked source, closed after
  - `Web.DomTests/RestoreReceiptsDomTests` — what a run did not restore is shown only through the passphrase, and the source is closed after; a run that restored everything offers nothing to show

---

## Context

The console lists each restore the service kept a receipt for, as what became of its files and where it wrote them, naming none of the backup's files ([ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md)). A person who saw that an earlier restore failed three files could not see which three without the service's machine and `restore-receipts --run`. ADR-0125 recorded this as undone.

A receipt's paths are the backup's file names, and those reach a person only through a source the set's passphrase unlocked ([ADR-0089](0089-a-backups-file-names-need-the-passphrase.md)). A receipt names its set ([ADR-0126](0126-a-restore-receipt-names-the-set-it-restored.md)), but a name is not enough to hold a gate by. A set can be renamed and another given its old name. A receipt checked by name would then show the first set's file names to the second set's passphrase.

## Decision

### 1. The receipt records its set's identity, at schema 10

The receipt gains `set_id`: the identity of the set it names, which a rename does not change. It is written only where the run knew a set, as `set` is.

### 2. A receipt's files are answered only through a source of that set

`get_restore_receipt` takes a run, a source and a limit. It answers only through a source of the set whose identity the receipt records, unlocked under a grant by the session that asks, by the same check every look at a backup's files makes. It refuses:
- a receipt that records no set, saying it is read on the service's machine with `restore-receipts --run`;
- a run that kept no receipt;
- a name that is not a run's.

Each refusal names no file.

### 3. It answers what the run did not restore

The answer is the files the run failed, skipped or degraded, in the receipt's order, each with its outcome and the receipt's own words for why. They come under a limit, with how many more there are. A file the run restored is not answered: the listing already counts them, and the question a person brings is what did not come back.

### 4. The console asks behind the passphrase, and closes the source after

Each restore in the Maintenance view that left files behind, and whose receipt records its set, offers to show them. The console asks for that set's passphrase as every look at a backup's files does, asks the receipt's files through the source it opened, shows them, and closes the source. A run that restored everything offers nothing to show.

## Consequences

- A person can see which files an earlier restore did not bring back, and why, from wherever the console is paired.
- The gate holds by the set a run restored, not by whichever set now has its name.

## What this does not do

- **Show the files a run restored, or where each landed.** The receipt says so, on the service's machine.
- **Show a receipt kept before schema 10.** It records no set's identity, so no passphrase here is known to be its own.
- **Restore them again.** A run that did not finish is resumed by its name (ADR-0115); one that finished is restored afresh.

## Alternatives considered

- **Hold the gate by the set's name.** Rejected. A name can pass to another set, and with it this receipt's file names, to a passphrase that never protected them.
- **Answer every file the receipt names.** Rejected. A large restore's receipt names every file it was asked for, and the files that came back are already counted. The ones that did not are the question.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The receipt at schema 10 records its set's identity. Contract 1.75's `get_restore_receipt` answers the files a run failed, skipped or degraded, under a limit, only through a source of that set unlocked by the session that asks. The console shows them behind the passphrase and closes the source after. |

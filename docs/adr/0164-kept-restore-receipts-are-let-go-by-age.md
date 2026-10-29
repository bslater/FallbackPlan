# ADR-0164 — Kept restore receipts are let go by age

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: the receipts of runs that ended longer ago than a person says are let go at once, and the rest stay)
**Related:** [ADR-0139](0139-a-kept-restore-receipt-is-let-go.md) (a kept receipt is let go by its run's name, and expiring receipts after a time turned down), [ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md) (the service lets one go), [ADR-0161](0161-every-kept-restore-receipt-is-let-go-at-once.md) (every kept receipt let go at once, and the record that left them by age undone)

**Built:**
- The verb: `Cli/CliApplication`'s `restore-receipts --forget-all --older-than-days <days>`, through `Cli/RestoreReceiptReport`, lets go only the receipts of runs that ended more than that many days ago, from a state directory or through a paired service. It says each, how many went, and how many stay, and names a receipt that does not say when its run ended, which stays.
- The tests:
  - `Cli.Tests/RestoreReceiptsVerbTests` — from the state directory only the receipts of runs that ended longer ago go, and how many stay is said; a receipt that does not read stays and is named, however long ago its file was written; through a paired service only those it lists as ended longer ago are asked to go, and one with no end stays and is named; without `--forget-all`, or below one day, it is refused, before the service is dialled
  - `Hosts.Tests/RestoreReceiptsRemoteTests` — a paired console lets the service's receipts of runs that ended longer ago go, and the rest stay

---

## Context

A state directory keeps a receipt of every restore it ran, and the service keeps one of each it ran ([ADR-0122](0122-direct-mode-keeps-a-restores-receipt.md), [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md)). Each is let go by its run's name, or every one at once ([ADR-0139](0139-a-kept-restore-receipt-is-let-go.md), [ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md), [ADR-0161](0161-every-kept-restore-receipt-is-let-go-at-once.md)).

[ADR-0139](0139-a-kept-restore-receipt-is-let-go.md) turned down expiring receipts after a time: a receipt is the claim of what a restore did (FR-RST-004), and nothing about its age says it is no longer wanted. That stands. What a person who wants last month's receipts and none older could not do was say so: they named each older run in turn, or let every one go. [ADR-0161](0161-every-kept-restore-receipt-is-let-go-at-once.md) left going by when each run ended undone, because a receipt that does not read says no end.

## Decision

### 1. `--older-than-days` lets go the receipts of runs that ended longer ago

`restore-receipts --forget-all --older-than-days <days>` lets go each receipt whose run ended more than that many days ago, and keeps every other. From a state directory each is removed by its run's name. Through a paired service, `--connect`, every receipt the service keeps is listed with no limit, and each that ended longer ago is let go by its run's name, as one is (contract 1.79). The contract is unchanged, and nothing goes without the person's asking.

It says each receipt let go, in `--forget-all`'s words, and how many. Where some stay, it says how many ended within the days.

### 2. A receipt that does not say when its run ended stays, and is named

A receipt that does not read says nothing of when its run ended. When its file was written is not that, so its file's age is not taken for it. It stays, and is named, so the person can let it go by its name or with every one.

### 3. It goes beside `--forget-all`, and takes at least one day

`--older-than-days` without `--forget-all`, or below one day, is refused before anything is removed or the service is dialled.

## Consequences

- A person keeps the receipts of recent restores and lets the older go in one command, from the folder or through the service.

## What this does not do

- **Expire receipts on its own.** Nothing goes unless a person asks, as [ADR-0139](0139-a-kept-restore-receipt-is-let-go.md) decided.
- **Offer it in the console.** The Maintenance view lets one go, or every one.
- **Go by age for runs left unfinished or for direct mode's parts.** Those still go one by one or every one at once.

## Alternatives considered

- **Take an unreadable receipt's age from its file.** Rejected. When the file was last written says nothing of when its run ended, and a receipt let go is gone.
- **Ask the service to let go by age.** Rejected. The listing says when each run ended, and the single let-go already says everything the person is asked. A contract command would be a second way to get it wrong.
- **A duration with units, such as `30d` or `6w`.** Rejected. Days say every age a person sweeps by, and a number takes no parsing of its own.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. restore-receipts with --forget-all and --older-than-days lets go only the receipts of runs that ended longer ago, from the state directory or through a paired service, keeping and naming a receipt that does not say when its run ended. No contract change. |

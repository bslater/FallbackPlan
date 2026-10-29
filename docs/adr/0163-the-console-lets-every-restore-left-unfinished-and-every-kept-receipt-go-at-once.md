# ADR-0163 — The console lets every restore left unfinished, and every kept receipt, go at once

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: the console lets every run the service left unfinished go at once), FR-RST-004 (amended: the console lets every receipt the service keeps go at once)
**Related:** [ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md) (the console names each run left unfinished, and lets one go), [ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md) (the console lets a kept receipt go), [ADR-0161](0161-every-kept-restore-receipt-is-let-go-at-once.md) (every kept receipt let go at once from the command line, and the record that left the console one at a time), [ADR-0162](0162-every-restore-run-the-service-left-unfinished-is-let-go-at-once.md) (every run the service left unfinished let go at once from the command line, likewise)

**Built:**
- The console: the console script of `FallbackPlan.Web` offers, where more than one is left or kept, to let every run left unfinished go from the snapshots page and every receipt kept go from the Maintenance view. Once a person confirms, it asks the service for them afresh and lets each go by its name, as one at a time is. It names one the service will not let go with the service's words while the rest go.
- The tests:
  - `Web.DomTests/RestoreUnfinishedDomTests` — every run left unfinished is let go by its name, as the service names them once the person confirms, and no passphrase is asked; one the service will not let go is named with its words while the rest go; none left by then is said and none asked to go; a run left alone offers only its own let-go
  - `Web.DomTests/RestoreReceiptsDomTests` — every receipt kept is let go by its run, those the card does not show too, once the person confirms; one the service will not let go is named with its words while the rest go; a receipt kept alone offers only its own let-go

---

## Context

The console names each restore the service left unfinished on the snapshots page, and each receipt the service keeps in the Maintenance view ([ADR-0116](0116-the-console-resumes-a-restore-it-did-not-see-end.md), [ADR-0125](0125-the-service-lists-the-restore-receipts-it-keeps.md)). It lets each go beside it once a person confirms ([ADR-0140](0140-the-service-lets-a-kept-restore-receipt-go.md)). A run's parts are the plaintext of the backup's files, and a receipt names where a run wrote ([ADR-0133](0133-a-run-left-unfinished-says-what-its-parts-hold.md)).

The command line lets every one go at once ([ADR-0161](0161-every-kept-restore-receipt-is-let-go-at-once.md), [ADR-0162](0162-every-restore-run-the-service-left-unfinished-is-let-go-at-once.md)). Both records left the console one at a time, so a person there who would keep none of them confirmed each in turn. The Maintenance view shows only the newest fifty receipts, so the rest could not be let go there at all until those were gone.

## Decision

### 1. Where more than one is left, every one can go at once

The runs left unfinished, where more than one is left, and the receipts kept, where the service keeps more than one, offer to let them all go. One alone is let go beside it, as before. A dialog says how many and what stays, as the dialog for one does, and nothing is let go until the person confirms.

### 2. The service is asked for them afresh, and each goes by its name

Once the person confirms, the console asks the service for every run it left unfinished, or every receipt it keeps with no limit, and lets each go by its name, as one at a time is. The contract is unchanged, and no passphrase is asked. Receipts beyond those the card shows go too, and a run that finished after the card was drawn is not asked after.

### 3. One the service will not let go is named, and the rest still go

A run or receipt the service refuses, say a run under way again, is named in the dialog with the service's words, and how many of how many went. Every other one is still asked. Where every one went, the dialog closes and says how many; where none was left by then, it says so. The list is asked again after.

## Consequences

- A person at the console who will keep none of the restores left unfinished, or none of the receipts, lets every one go in one confirmation, those the Maintenance view does not show among them.

## What this does not do

- **Let them go by age.** Every one is let go, or one beside it.
- **Choose some.** The console lets one go or every one, not a selection.

## Alternatives considered

- **Let go only what the page showed.** Rejected. The Maintenance view shows only the newest receipts, so what it showed is not every one kept. The listing names no run under way, and one resumed between the listing and its let-go is refused by the service and named.
- **A contract command that lets every one go at once.** Rejected, as in [ADR-0161](0161-every-kept-restore-receipt-is-let-go-at-once.md) and [ADR-0162](0162-every-restore-run-the-service-left-unfinished-is-let-go-at-once.md). The listing and the single let-go already say everything the person is asked.
- **Stop at the first one the service refuses.** Rejected, as in [ADR-0162](0162-every-restore-run-the-service-left-unfinished-is-let-go-at-once.md). The one refused is the one the person did not mean, and the rest are still theirs to let go.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The console offers to let every run left unfinished and every receipt kept go at once, asks the service for them afresh once the person confirms, and lets each go by its name, naming one it will not let go while the rest go. No contract change. |

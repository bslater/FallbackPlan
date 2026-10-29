# ADR-0169 — The console lets go by age what began or ended longer ago

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: the console lets go only the runs left unfinished that began longer ago than a person says), FR-RST-004 (amended: the console lets go only the receipts of runs that ended longer ago than a person says)
**Related:** [ADR-0163](0163-the-console-lets-every-restore-left-unfinished-and-every-kept-receipt-go-at-once.md) (the console lets every one go at once, and named going by age as not done), [ADR-0164](0164-kept-restore-receipts-are-let-go-by-age.md) and [ADR-0165](0165-restores-left-unfinished-are-let-go-by-age.md) (the command line lets them go by age, and each named the console as not done)

**Built:**
- The console: the console script of `FallbackPlan.Web` gives the dialog that lets every run left unfinished go, and the one that lets every kept receipt go, an optional number of days. Left empty, every one goes, as before. Given, only the runs begun, or the receipts of runs that ended, longer ago than that are let go, each by its name. The ones that stay are counted, and a receipt that does not say when its run ended stays and is named. Days below one, or not whole, are refused before anything is asked to go.
- The tests:
  - `Web.DomTests/RestoreUnfinishedDomTests` — given days, only the run begun in 2023 goes while two begun hours ago stay and are counted; days of 0 or 2.5 are refused and no run is asked to go; where every run began within the days none is asked to go, and the console says how many stay
  - `Web.DomTests/RestoreReceiptsDomTests` — given days, only the receipt of a run that ended in 2023 goes; one that ended an hour ago stays and is counted, and one that does not say when its run ended stays and is named

---

## Context

The command line lets go by age: `restore-receipts --forget-all --older-than-days` the receipts of runs that ended longer ago ([ADR-0164](0164-kept-restore-receipts-are-let-go-by-age.md)), and `restore-resume --forget-all --older-than-days` the runs the service left unfinished that began longer ago ([ADR-0165](0165-restores-left-unfinished-are-let-go-by-age.md)). The console lets one go, or every one at once ([ADR-0163](0163-the-console-lets-every-restore-left-unfinished-and-every-kept-receipt-go-at-once.md)). A person at the console who keeps last week's restores and not last year's had to let the old ones go one at a time. ADR-0163 named this as not done, and ADR-0164 and ADR-0165 each named the console.

## Decision

### 1. The let-go-every-one dialogs take days

Each of the two dialogs gains an optional field for the days. Left empty, every one goes, exactly as ADR-0163 built. Given, only what is older than that goes.

### 2. Judged as the command line judges, from what the service listed

Once the person confirms, the console asks the service for every run, or every receipt with no limit, as ADR-0163 does. It keeps those whose run began, or ended, before the cutoff: the days counted back from now, never before the epoch. That is the rule `--older-than-days` applies. The listings already say when each run began and when each receipt's run ended, so the contract is unchanged, and each is let go by its name as one at a time is.

### 3. What stays is said

The console says how many stay because they began, or ended, within the days, in the words the command line uses. A receipt that does not say when its run ended cannot be judged. It stays and is named, as the command line names it. Where every one is too young, none is asked to go and the console says how many stay.

### 4. Days that are not a whole number of at least one are refused

Anything but a whole number of at least one is refused before anything is asked to go, as the command line refuses `--older-than-days 0`. The dialog stays open, so the person can correct the days.

## Consequences

- A person at the console lets the older restores left unfinished, or the receipts of older restores, go in one confirmation, and keeps the recent ones.
- The console and the command line judge by one rule, so the same days let the same ones go from either.
- The cutoff is the console's clock, not the service's, as the command line uses its own.

## What this does not do

- **Direct mode's parts.** The console serves a paired service, and parts kept in direct mode are the command line's, which already lets them go by age ([ADR-0165](0165-restores-left-unfinished-are-let-go-by-age.md)).
- **Expire anything on its own.** Nothing goes unless a person asks.
- **Choose some.** The console lets one go, every one, or every one older than the days, not a selection.

## Alternatives considered

- **A contract command that lets go by age.** Rejected, as ADR-0163 rejected one for every one at once. The listing already says when each began or ended, and the single let-go already answers each by its name, so the console needs nothing new from the service.
- **A choice of fixed ages, such as a week, a month or a year.** Rejected. The command line takes any number of days, and a person who asked for 45 at one should be able to ask for 45 at the other.
- **Judge by the service's clock.** Rejected. The listing carries no time of its own to judge by, and the command line judges by its own clock too.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The console's dialogs that let every run left unfinished and every kept receipt go take optional days, and let go only what began or ended longer ago, saying what stays and naming a receipt that cannot be judged. No contract change. |

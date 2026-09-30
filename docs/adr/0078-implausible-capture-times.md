# ADR-0078 — A capture time that does not fit its writer's publication order is flagged and kept

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-GC-012, NFR-TIME-001, FR-GC-010
**Related:** [architecture 04 §7](../architecture/04-concurrency-and-publication.md#7-time-and-clock-skew), [architecture 07 §2](../architecture/07-retention-and-gc.md#2-retention-policy), [ADR-0009](0009-garbage-collection-safety.md) (which promised the flag, and Amendment 7, the margin), [ADR-0077](0077-observed-clock-skew.md) (the skew reading, which this does not use), [ADR-0061](0061-adopt-a-destinations-archives.md) §4 (adoption under a new identity), [specification 08 §2](../../specifications/repository-format/08-journal.md) (one sequence per writer)

**Built:**
- The rule: `Retention/RetentionPlanner` (`FindImplausible`, and `Select`, which keeps what it finds). Its input carries the writer through `Retention/StagingMark`.
- The same margin in every selection: `Retention/RetentionRunner`, `Retention/DestinationConvergence` (each destination's keep-set, the gate's keep-awareness, the converge spare) and `Retention/StagingTrim`.
- The notice: `Agent/ImplausibleCaptureNotice`.
  - It is raised by the retention verb in `Agent/ServiceCommandHandler` and by fan-out's convergence in `Agent/FanOut`.
  - `Application/NoticeStore` holds an acknowledgement of an unchanged finding.
- The listing: `list_snapshots` in `Agent/ServiceCommandHandler`, contract 1.48 (`Api/Results`, `Api/ContractVersion`). It is read by `Cli/CliApplication` and the console's snapshot row in `wwwroot/app.js`.
- The tests:
  - `Retention.Tests/ImplausibleCaptureTimeTests`
  - `Retention.Tests/ImplausibleCaptureRetentionTests`
  - `Hosts.Tests/ImplausibleCaptureServiceTests`, through a capture clock hook on `Agent/BackupRunner`
  - `Application.Tests/NoticeStoreTests`
  - `Api.Tests/ContractAdditiveFieldsTests`
  - `Cli.Tests/SnapshotImplausibleTimeTokenTests`
  - `Web.Tests/ConsoleSnapshotImplausibleTimeScriptTests`
  - `Web.DomTests/ConsoleViewsDomTests`

---

## Context

Architecture 04 §7 and ADR-0009 both said a snapshot whose recorded time is implausible relative to its neighbours "is flagged rather than silently expired". Nothing did that, and ADR-0077 said so.

The retention planner orders a set's snapshots by the capture time the writer's clock stamped into the manifest. Its window rules and its min-generations floor both read that time. So a wrong clock does damage in both directions:

- **A clock set back.** A firmware reset can put the date at 2001.
  - A capture taken then is twenty-five years old by its own record. It expires at the first pass after the clock is put right, and nothing is said.
  - If the clock was set back only a few days, the capture lands in a day that has a real capture of its own, and later in that day. It then takes that day's place, and the real one expires.
- **A clock set ahead.** A capture taken then sorts newest.
  - It takes a min-generations place. Under a floor-only policy the real newest expires, although it is the only capture taken after the clock was put right.

Destination convergence runs the same planner on every sync, and a destination deletes what its keep-set drops (FR-GC-010). So this loss reaches the destinations whether or not anybody runs retention.

The product has two things it could judge a capture time by:

- **Each writer's publication sequence** (specification 08 §2).
  - It is gapless and monotonic, and allocated before the capture runs. It reads no clock.
  - It is one writer's own. An archive adopted under a new identity (ADR-0061 §4) holds two, and the new writer's counters start again near 1.
- **ADR-0077's observed skew.** This is a diagnostic that may not correct a timestamp or shift a retention window (ADR-0077 §7).

## Decision

### 1. What implausible means

A snapshot's capture time is **implausible** when it does not fit the order its writer published it in, by more than the configured clock skew margin. The yardstick is:

- **The run:** the longest run of that writer's snapshots, in publication order, whose capture times never fall.
- **Ties:** where two runs are equally long, the one with the later times is taken as the truth.

A snapshot off the run is implausible in one of two directions:

- **Behind:** its time lies more than the margin *before* the nearest member of the run published ahead of it. The clock read behind when it was taken.
- **Ahead:** its time lies more than the margin *after* the nearest member of the run published after it. The clock read ahead.

The margin is `clock_skew_margin_hours`, a day when the configuration states none (NFR-TIME-002). It is the same margin that already absorbs skew in a write intent's expiry. A step inside it is a clock being set right, not a clock that was wrong.

The tie goes to the later times because of what the choice decides:

- The run is what the policy then reads at face value.
- An earlier-dated run taken as the truth would be judged old, and could expire.
- A later-dated run taken as the truth is judged young and kept, and the other run is kept by its flag.

Nothing is judged in two cases:

- **Across writers.** Neither writer's sequence orders the other's snapshots.
- **A snapshot with no sequence** (0, from a caller that cannot say).

### 2. This machine's clock anchors the order; it does not judge

Only snapshots dated no later than this machine's clock plus the margin may belong to the run. A capture from this machine's future is judged by the run like any other snapshot. It is never flagged on the clock alone.

A clock-only rule was measured before it was rejected:

- Flagging every capture dated more than a day ahead of the collector changed 25 of the retention suite's 108 tests. Those suites capture at the live clock and run the collector at a fixed August date, so every capture already looks weeks ahead.
- What such a rule keeps would depend on two clocks agreeing. NFR-TIME-001 forbids that for correctness, and the window rules already treat this machine's calendar as policy.

Nor is the rule needed to catch a future-dated capture. The first later capture taken under a correct clock contradicts it, and the run cannot include it, because it lies past this clock.

### 3. What happens to a flagged snapshot

A flagged snapshot is **kept whatever the rules say**, with a reason in the dry-run report's words:

- `implausible capture time (dated before earlier publications)`
- `implausible capture time (dated after later publications)`

Its time is exactly what cannot be relied on, so:

- the window rules do not read it;
- it represents no bucket;
- it fills no min-generations place. The floor's places go to the newest captures the rules can trust.

Every selection that decides what a destination holds asks the same question with the same margin:

- each destination's keep-set;
- the replication gate's keep-awareness;
- the direct-ship converge spare;
- the staging trim.

A stamp past what the calendar can hold is read as the calendar's last instant: a capture from the future, as any stamp ahead of this clock always was. Such a stamp used to throw, or be read as 1969 and expire as ancient.

### 4. How it is said

- **The dry-run report** carries the reason on the snapshot's keep line.
- **`list_snapshots`** carries `implausible_capture_time` on each snapshot, `behind` or `ahead`, null for a capture that fits (contract 1.48). The listing asks the same question of the same facts, so it cannot disagree with the report.
  - The CLI prints `time:implausible-BEHIND` or `-AHEAD` after the clock token.
  - The console draws a warning under the capture time.
- **A notice per set**, keyed `capture-time-implausible:<set>`, names the snapshots.
  - It is raised by the retention verb and by fan-out's convergence, and withdrawn once none remains.
  - A misdated snapshot stays misdated once the clock is put right, so an acknowledged finding is not raised again while it says the same thing. A finding that grows says so in new words, which is news again.

## Consequences

**Positive**

- A wrong clock no longer expires what it misdated, and no longer displaces the real newest from the floor. This holds at the hub and at every destination.
- A person learns of it. The notice and the listing name the snapshots, and ADR-0077's reading, where one was recorded, shows how far the clock stood from a peer's.

**Negative**

- **A flagged snapshot is kept indefinitely.** Retention never expires one, and there is no snapshot deletion verb, so its unique content stays until a person has a way to remove it. The cost is bounded by how long the clock was wrong. It is usually a handful of captures, sharing most of their content with their neighbours.
- **Where a misdated run is longer than the history it contradicts, the longer run is taken as the truth.** One example is a new machine whose clock was wrong for its first weeks. The correct captures are then flagged and kept, and the misdated ones are judged by their wrong times. The notice still fires, naming the snapshots on the other side, and a person reading the listing sees both sets of dates.

**Neutral**

- The margin is shared with write-intent expiry. Changing `clock_skew_margin_hours` moves both, which is the point of having one.

## What this does not do

- **It does not judge across writers.** After an adoption under a new identity, each writer's snapshots are judged only against its own.
- **It does not catch a clock that was wrong from its writer's first capture and never contradicted.** Nothing in the order disagrees with it.
- **It does not use the observed skew.** ADR-0077 §7 forbids a reading from correcting a timestamp or moving a window, and this rule needs neither.
- **It does not protect against this machine's own clock running ahead.** The windows follow the collector's calendar, which NFR-TIME-001 makes policy. The floor, a count, still holds.
- **Two listings do not carry it.** The standalone recovery tool's listing has no retention to explain. `open_restore_source` does not judge, and sends none.

## Alternatives considered

**Flagging a capture dated ahead of this machine's clock on the clock alone.** Rejected, as §2 says. It made what retention keeps depend on two clocks agreeing, and it catches nothing the order does not.

**Flagging both sides of every inversion.** Rejected. A clock set ahead by ten years for a day, followed by ten years of correct captures, would flag every one of those years once the glitch's date passed. Retention would then be off for the set.

**Anchoring at the newest publication and walking back under a falling ceiling.** Rejected. A clock set back lowers the ceiling to the wrong date. The correct history before the reset is then flagged, and the misdated captures are judged by their wrong times, which is the loss this exists to prevent.

**Correcting capture times by the observed skew.** Rejected by ADR-0077 §7 and the specification 06 §6 erratum.

**A floor on plausible dates, such as nothing before the format existed.** Rejected. A legacy import (Phase 5) may legitimately carry history older than the format.

**Expiring a flagged snapshot once its neighbours have left the widest window.** Deferred. Its true time lies between its neighbours', so the rule would be sound. But it would let retention delete a snapshot on a guess about its time before a person had looked. It is kept out until the notice has shown what real clock failures look like.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | Built in one slice: the rule, the treatment in every selection, the notice, contract 1.48, the CLI and console surfaces, and the tests above. The clock-only rule it rejected was measured first, and changed 25 of the retention suite's 108 tests. |

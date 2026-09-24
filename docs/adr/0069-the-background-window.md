# ADR-0069 — The background window: the first of NFR-PERF-013's four limits

**Status:** Accepted
**Date:** 2026-09
**Requirements:** NFR-PERF-013, NFR-OPS-004, NFR-TIME-001, FR-SVC-013, FR-SVC-014
**Related:** [ADR-0029](0029-pipeline-and-service-concurrency.md), [ADR-0047](0047-backup-pool-and-priorities.md), [ADR-0027](0027-services-scheduling-status-telemetry.md), [architecture 10 §2](../architecture/10-observability.md#2-technical-metrics), [command-contract](../../specifications/command-contract/README.md)

**Built:** `Application/BackgroundWindow` (the grammar, `IsOpen`, `NextOpen`, `NextClose`, and the strict parse that names its defect), `Application/ClientConfiguration` (schema 6's `background_window`, its validation and its migration), `Agent/Scheduler` (the gate at the four `userInitiated: false` sites, the per-set outcome row, the hold and the release), `Agent/JobScheduler` (the standing background hold, the two filters in the writer pump, and the reason carried to the park), `Agent/PauseGate` (the park reason), `Agent/Log` (events 3782 and 3783), `Agent/ServiceCommandHandler` (the window on `get_status`, evaluated at the instant the status names), `Api/Results.cs` and `Api/ContractVersion.cs` (contract 1.37's `BackgroundWindowDescriptor`), `Cli/CliApplication` (the `status` line), `Web/wwwroot/app.js` (`windowNote` and `until`); `Application.Tests/BackgroundWindowTests`, `Hosts.Tests/BackgroundWindowTests`, `Hosts.Tests/BackgroundHoldTests`, `Hosts.Tests/ClientModeTests`, `Web.Tests/ConsoleBackgroundWindowScriptTests`, `Web.Tests/StatusRelayNamesTests`, `Api.Tests/ConfigurationContractTests`, `Api.Tests/ContractAdditiveFieldsTests`.

---

## Context

**NFR-PERF-013** promises that *"background activity shall observe configured
CPU, disk, network, and time-window limits"*, and until this record none of
the four existed. The round that filled the last unmeasured performance rows
went looking for the CPU cap to measure and found nothing to measure:
`CapturePolicy.Concurrency` bounds parallel work and the memory that follows
from it, and is the only configured bound in the product. So the requirement's
acceptance criterion described a setting that could not be set, and the row was
corrected from *unmeasured* to **unbuilt** — with the correction that its other
clause, the *yielding*, **is** built ([ADR-0029](0029-pipeline-and-service-concurrency.md)
calls that the requirement's concrete meaning and
[ADR-0047](0047-backup-pool-and-priorities.md) implements it). It was the only
**Unproved** row on the proof page.

This record builds one of the four: the **time window**. It is the one that is
deterministically testable here — CPU is exactly the figure
[NFR-PERF-007](../requirements/non-functional.md) already discounts as
container measurement — it is what a person actually asks for (*"don't back up
while I'm working"*), and it reuses machinery that already exists rather than
inventing a suspension mechanism.

## The keystone: the suspension already existed, and already did the right thing

[ADR-0047](0047-backup-pool-and-priorities.md) Amendment 1's preemption pauses
a running job through `IPauseGate`, waits for it to park at a file boundary,
and resumes it when a worker frees up. Its `maxPause` parameter — an hour by
default — is documented in its own constructor as:

> *"How long a parked run may hold its in-memory state and its live write
> intent before it self-cancels to the interruption-safe re-run path — the
> guard against a busy pool pinning a suspended capture's memory for ever."*

That is exactly right for a window that closes over a running capture, and it
was already built and tested. A run caught by the closing window parks; if the
window is still shut when the cap expires, the run self-cancels to the re-run
path and completes from its spool at the next opening. **The window needed no
new suspension machinery at all** — it needed somewhere to ask, and a rule that
keeps the ask standing.

## Decisions

### 1. The window governs exactly what the scheduler starts with nobody waiting

Captures, fan-out syncs, replica sweeps and drills — the four sites that pass
`userInitiated: false`. That predicate already existed at all four, and it is
the requirement's own phrase, *"background activity"*, in code. A window that
held only the capture would be the setting an operator thought they had rather
than the one they got: the thing saturating a domestic uplink at nine in the
morning is as likely to be the fan-out.

### 2. It never governs a person

A restore at noon, a manual run, a console *Back up now* and `agent run --once`
are never gated. This is [ADR-0029](0029-pipeline-and-service-concurrency.md)
§4's standing rule applied to the window rather than to the pool, and building
it found the rule was only half true: `RunPassAsync` hard-coded
`userInitiated: false` at every enqueue site, so a person's pass was let
through the gate and then refused by the pool it had just put a hold on. The
pass's own initiation now travels to the work it queues. That rule is about the
work, not about the tick that queued it.

### 3. Installation-wide, in the configuration file

`ClientConfiguration` schema 5 → 6, beside `max_concurrent_backups` and
`logging`. Not per set: the requirement is about background activity, and
per-set windows are a different and larger thing (named as not done). **Absent
means always open**, which is today's behaviour and the compatibility rule the
whole migration rests on — every configuration written before schema 6 says
"no window" by not mentioning one.

### 4. The grammar is a wall clock, and days of the week are not in it

`HH:mm-HH:mm` in local time, with midnight crossing supported because
`22:00-06:00` is the one a person actually writes. Equal ends are refused by
name rather than read as a whole day or as nothing. Days of the week are the
obvious extension and are deliberately absent: this slice is one limit, not a
scheduling language, and `Schedule` already exists for recurrence.

### 5. Read afresh each pass, not pinned at service start

Unlike `max_concurrent_backups`, which is fixed at boot. A limit whose whole
point is that it changes during the day would be useless fixed at boot. The
pass evaluates it once per tick and skips the four background lanes with a
per-set outcome row naming the reason and when the window next opens — so *"why
did nothing run"* is answerable from the pass's own output, which is where that
question already gets answered.

### 6. A run caught by the closing window parks, and the hold is a standing state of the pool

This is the design's keystone and the least obvious part of it.
`JobScheduler.TryTakeWriterWork` resumes the best-ranked parked run *the moment
a writer worker frees* — which is precisely what a park does. So a one-shot
`PauseGate.Pause()` from the pass would be undone within milliseconds, by the
worker the park itself released. The hold is therefore
`HoldBackgroundAsync`/`ReleaseBackground`, and while it stands the writer pump
neither resumes a parked background run nor starts a queued one.

The queued half is decided by one peek: the lane's key sorts user-initiated
first, so a background head means every entry behind it is background too.
Without it the limit is half a limit — at the default pool width of one,
parking the running set hands its worker straight to the next one queued.

Holding and releasing are driven by the **window's** state and not by who asked
for the pass, because folding the two would have `agent run --once` at two in
the morning release every capture the window had parked: the manual pass
rightly not being gated, and wrongly speaking for the machine.

### 7. The park carries its reason

`PauseGate` hard-coded *"suspended for a higher-priority run"* into the journal
row a park writes. With a second asker that is wrong half the time, and the
journal row is what a person reads the next morning to answer why their backup
stopped at ten. `Pause` now takes the reason and the first ask wins it, so a
window closing over a run a preemption had already asked for does not rewrite
what happened. The CLI's `PAUSED` line drops the cause it was guessing and
leaves it to the detail line beside it.

### 8. Seeing it, but not setting it

Contract **1.37** puts the window's state on `get_status` — open or shut, and
when it next changes — with lines in the console and the CLI. **The setting
stays in the configuration file**, exactly as `max_concurrent_backups` does,
and the console control is named as owed rather than smuggled in behind a
status field. A window whose state nobody can see is a support call, which is
why the reporting half is here and the editing half is a stated limit.

The state is evaluated at the instant the status reports as its own, and from
the same parsed window the pass uses, so a client cannot catch the two
disagreeing across a boundary. Building that found the status handler reading
the clock twice — `UtcNow` for `observed_at` and a separate `.Now` for each
set's next run — which nothing depended on until a window did.

### 9. Time, honestly

A window is a **wall-clock range**, not a span of instants: it says which
positions on this machine's clock face background work may start in.
**NFR-TIME-001** — *no correctness property shall depend on wall-clock
agreement between machines* — is untouched, because this is a policy about one
machine's clock and not a correctness property. A wrong clock mis-times a
backup; it never corrupts one. A daylight-saving transition shortens or
lengthens the window by an hour, which is stated rather than special-cased: an
operator who writes "not while I am working" means the clock on the wall.

There is a trap in that which the call site cannot show. `IsOpen` reads the
wall clock of the `DateTimeOffset` it is handed, so handing it `UtcNow` answers
for UTC's clock face — and on any machine east or west of Greenwich the status
surface would then disagree with the pass by the machine's offset, silently,
and never on a machine that runs UTC. It is pinned by a pure case that hands
one instant to `IsOpen` in two offsets and gets two answers.

## Consequences

**Positive**

- NFR-PERF-013 has its first real limit, and the proof page's only **Unproved**
  row becomes **Partly proved**.
- The suspension, the escalation, the expiry and the interruption-safe re-run
  path are all ADR-0047's, unchanged. A closure that outlasts the pause cap
  costs a re-run from the spool rather than a lost capture, and that behaviour
  was free.
- Two latent defects surfaced and were fixed: a person's pass deadlocking
  against the hold it had just placed, and the journal's park reason about to
  become a lie.

**Negative**

- **Only a capture parks.** Only writer-lane jobs carry a pause gate, so
  fan-out, the deep sweep and the drill are stopped from *starting* and one
  already in flight runs to completion. A suspension point for transfers is a
  second mechanism inside `StoreToStoreCopier`, not a limit, and is not built.
- **The window is enforced to the granularity of a pass tick**, so a run
  started seconds before closure runs until the next one.
- **A person asking for a set whose capture the window parked is told
  `already-running`.** That is what a preemption park has always answered and
  is not made worse here, but it is now reachable for a second reason.
- **The other three limits are still unbuilt** and still named. The requirement
  says one of four.

**Neutral**

- The window cannot be edited from the console, by decision.
- Days of the week and per-set windows are extensions of a grammar this record
  deliberately did not grow.

## Alternatives considered

**Build the CPU cap instead.** The limit the requirement's acceptance criterion
is actually written about — and the one this container cannot honestly settle,
since [NFR-PERF-007](../requirements/non-functional.md) already discounts CPU
figures measured here as container measurement. Its acceptance (a percentage
over a 60-second window) is machine-dependent in a way a time window is not.

**A per-set window.** Rejected as a different and larger thing: the requirement
is about background *activity*, and a per-set window is a scheduling feature
whose interaction with `Schedule` needs its own design.

**Suspend by cancelling the run outright.** Simpler, and it throws away a
capture's accumulated state every night at ten. The pause gate already parks at
a file boundary and holds the state; cancelling is what the max-pause cap falls
back to when holding stops being worth the price, and it is the fallback rather
than the mechanism.

**Gate at the job runner rather than at the pass.** It would catch work queued
by any door, not just the scheduler's. Rejected: the runner does not know
whether a person is waiting — `QueuedJob.UserInitiated` says so, but the rule's
natural home is where due-ness is already evaluated and where the per-set
outcome row is already written, which is the pass.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | The time window built over four commits, the first of NFR-PERF-013's four named limits to exist; the suspension reused whole from [ADR-0047](0047-backup-pool-and-priorities.md) Amendment 1, with a standing pool hold added so a freed worker cannot undo the park |

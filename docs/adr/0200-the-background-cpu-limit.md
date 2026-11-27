# ADR-0200 — The background CPU limit: a share of the machine's processors

**Status:** Accepted
**Date:** 2026-11
**Requirements:** NFR-PERF-013, NFR-OPS-004
**Related:** [ADR-0029](0029-pipeline-and-service-concurrency.md), [ADR-0047](0047-backup-pool-and-priorities.md), [ADR-0069](0069-the-background-window.md), [ADR-0074](0074-background-byte-rate-limits.md), [NFR-PERF-007](../requirements/non-functional.md), [command-contract](../../specifications/command-contract/README.md)

**Built:**
- The share, the measure, the limiter and the pace, in `Application`:
  `CpuShare`, `CpuMeter`, `CpuLimiter` and `BackgroundPace`, with
  `PacedStream` charging a pace rather than a byte rate alone.
- The configuration field: `Application/ClientConfiguration` (schema 12's
  `background_cpu_limit`, its validation and its migration).
- The registry, in `Agent`: `BackgroundPacing`, which hands each seam its pace
  and keeps the one CPU limiter, with the meter injected through
  `ServiceRuntime`'s options as the pacing clock is.
- The seams, all of them ADR-0074's, in `Agent`: `BackupRunner`,
  `DestinationShipSink`, `FanOut`, `SyncSource`, `SetCopies`,
  `ReplicaSweepJob`, `PeerRepairClient`, `PeerRepairTarget` and
  `ServiceCommandHandler.RestoreSources`.
- The surfaces: `Agent/ServiceCommandHandler` (the limit on `get_status`),
  contract 1.97 (`Api/Results.cs`, `Api/ContractVersion.cs`),
  `Cli/CliApplication` (the `status` line) and `Web/wwwroot/app.js`
  (`limitsNote`).
- The tests: `Application.Tests/CpuShareTests`,
  `Application.Tests/CpuLimiterTests`, `Hosts.Tests/BackgroundCpuLimitTests`,
  `Hosts.Tests/ClientModeTests`, `Api.Tests/ContractAdditiveFieldsTests`,
  `Api.Tests/ConfigurationContractTests`,
  `Web.Tests/ConsoleBackgroundLimitsScriptTests` and
  `Web.Tests/StatusRelayNamesTests`.

---

## Context

NFR-PERF-013 promises that background activity observes configured CPU,
disk, network and time-window limits. Three exist: the window
([ADR-0069](0069-the-background-window.md)) and the two byte rates
([ADR-0074](0074-background-byte-rate-limits.md)). Both records left CPU
unbuilt for the same reason. The requirement's acceptance is a percentage of
some machine's processors over sixty seconds, and that is the figure
NFR-PERF-007 already discounts when it is measured in a container.

That reason is about the measurement, not about the limit. A limit is two
things: a reading of what was spent, and arithmetic over the readings that
decides how long to wait. The reading is what a container cannot settle. The
arithmetic can be settled anywhere, once the reading is an argument. The
byte limits already take their clock as an argument, and a meter can be taken
the same way. So this record builds the limit, proves the arithmetic
deterministically over a meter a test moves, and names the real-machine
figure as unmeasured rather than as unbuilt.

## Decisions

### 1. A share of the whole machine, set for the installation

`background_cpu_limit` is a whole percentage of every processor the machine
has together, `1%` to `99%`. On a four-core laptop `25%` is one core's worth.
It is absent by default, which means unlimited. That is also what every file
written before schema 12 says by not mentioning it. The configuration moves
from schema 11 to 12 with nothing to migrate, because unknown fields are
refused and a schema-12 file handed to an older build must be refused by name.

**Of the whole machine**, because the two conventions people meet disagree by
the core count. One tool shows a process's share of the machine and another
its share of one core, so a bare `25%` is a quarter of a core to some readers
and a whole core to others. The setting has to mean one of them, and the
places that show it say which: "of this machine's processors".

**For the installation**, because what it measures is the whole service
(decision 2). A per-set or per-job limit would need a measure that can tell
the jobs apart, and none can.

### 2. What is measured: the service's own processor time

The meter reads the process's processor time, user and kernel together and
all its threads, from `Environment.CpuUsage`, with the machine's processor
count. Nothing finer is portable. A thread's processor time does not follow an
asynchronous job, which runs a continuation on whichever pool thread is free.

So the limit holds background work to a share of what the **service** spends,
a person's work included. A person's work is never paced (decision 3), so
while a person restores, background work gives way. That is NFR-PERF-013's
other clause, and [ADR-0029](0029-pipeline-and-service-concurrency.md) §4 and
[ADR-0047](0047-backup-pool-and-priorities.md) already make background work
yield to a person.

### 3. Background means what the window already means

The limit paces exactly what the scheduler starts with
`userInitiated: false`: captures, fan-out syncs, replica sweeps and drills.
It never paces a person. That is ADR-0069's rule and ADR-0074 §2's, unchanged.

### 4. The pacing: a look at every chunk, one limiter for the installation

Every chunk a background job moves through a paced seam is a look; a chunk is
at most 64 KiB (ADR-0074 §4). A look charges the processor time the service
spent since the previous look. Then it waits whatever keeps the total inside
the share, by the byte limiter's virtual scheduling and its one-second burst:
the limiter keeps the instant at which everything charged so far would have
been spent at the share, and the caller waits for whatever of that lies more
than a second ahead.

Processor time is measured after it is spent, where bytes are asked for
before they move. Three rules follow from that:

- **What was spent since the last look is owed from that look.** Housekeeping
  spread over an idle minute does not hold the next chunk back as though it
  had all just happened. Once background work is running it looks at every
  chunk, so the interval is milliseconds and idle time banks no more than the
  burst.
- **What the limiter owes is remembered for ten seconds.** A person's
  ten-minute restore on four busy cores is forty minutes of processor time.
  Owed in full at one core a second, it would hold the next background chunk
  back for half an hour after the restore had ended. Instead the wait is at
  most nine seconds: ten, less the burst.
- **A cancelled wait keeps its charge.** Unlike a byte that never moved, the
  processor time was spent. The wait itself still ends at once, so a parked
  capture, a preemption and the service's stop are not held by it
  (FR-SVC-017).

**One limiter for the installation**, because the meter reads the whole
service: a limiter per job would charge each job what all of them spent.
`Agent/BackgroundPacing` keeps it, replaces it when the limit's text changes,
and reads the limit afresh from the configuration as each job uses it, so an
edit takes effect without a restart.

### 5. The seams are the byte limits' own

A seam's pace carries its byte rate, if one is set, and the CPU limiter, if
one is set. Every seam ADR-0074 §5 names takes the CPU limit, with or without
a byte rate beside it:
- a capture's source reads;
- a direct-ship run's writes;
- the fan-out's copy, peer push, read-back and heal;
- a sync's reads from the copies it sources from;
- a sweep's reads;
- a drill's restore sources.

A seam with neither limit is given the store or the source itself, as before,
so an installation with no CPU limit never reads the meter.

The bytes are charged first and the processor time second. A chunk a byte rate
held back is looked at after its wait, which is when the work it stands for is
about to happen.

**Not looked at:**
- Work that moves no bytes through a seam. A scan stats and reads no content,
  so an incremental pass over a large unchanged tree spends its scan unpaced.

  > **Amended 2026-11 ([Amendment 2](#amendment-2-2026-11--a-walk-is-looked-at-too)).**
  > A walk is looked at now, every sixty-four scan events, so this bullet no
  > longer holds for a capture's walk. Work that neither moves bytes nor walks
  > a source still goes unlooked.
- The two paths ADR-0074 §5 leaves unpaced by decision: the ship sink's reads
  and the push a retention apply makes. A person may be behind either one.

### 6. The grammar: a whole percentage, strict, with the defect named

A share is digits and a percent sign: `25%`, or `25 %`. It is refused at load
when it is anything else, with a form that would be read named in the refusal.

- **`0%` is refused** as a stop rather than a limit.
- **`100%` or more is refused** as no limit at all. A share the machine cannot
  exceed limits nothing, and leaving the setting out already says
  "unlimited".
- **A fraction is refused** rather than rounded. A share a person wrote as
  `12.5%` and read back as `12%` or `13%` is not the limit they wrote.

### 7. Seeing it, and what follows

Contract **1.97** adds `cpu_limit` to `get_status`'s `background_limits`, as
the configured text and the whole percentage. A CPU limit alone is a limit in
force, so `background_limits` is sent with no byte limit beside it. A pre-1.97
service never sends the field, which a client reads as no CPU limit.

The CLI prints `background work limited to 25% of this machine's processors`
above the matrix, beside the window and the byte limits, and the console's
clause says the same.

Two things are owed to the next slice. The limit is edited in the
configuration file only for now: contract 1.44's settings commands, the CLI's
`settings` verb and the console's settings form do not yet carry it. And the
diagnostic bundle's copy of the configuration does not name it.

> **Amended 2026-11 ([Amendment 1](#amendment-1-2026-11--the-limit-is-set-through-the-service)).**
> Both are built: contract 1.98 sets the limit with the other settings, the
> CLI and the console's card set it, and the bundle names it.

## Consequences

**Positive**

- All four of NFR-PERF-013's limits exist, and a person's work is held to
  none of them.
- The arithmetic is proved without a processor in sight: by the waits a
  virtual clock was asked for, over processor time a test states.
- An installation that sets no limit runs exactly as before and never reads
  the meter.

**Negative**

- **The figure the acceptance criterion names is measured nowhere.** That
  figure is measured agent CPU at or under 30% over any sixty seconds under a
  25% limit. A container cannot settle it, as NFR-PERF-007's throughput is
  unsettled, and the proof is of the arithmetic.
- **The pacing lags the work it paces.** The capture's workers go on hashing,
  compressing and encrypting chunks already read while the reader waits. What
  runs past a look is bounded by what the pipeline holds in flight, which
  `CapturePolicy.Concurrency` and the pipeline's channels bound. It is not
  zero.
- **The measure charges what background work did not do.** A person's restore
  and the service's own housekeeping are charged too. Background work gives
  way to them, for up to ten seconds after they end.
- **A scan is not paced**, as decision 5 says.

  > **Amended 2026-11 ([Amendment 2](#amendment-2-2026-11--a-walk-is-looked-at-too)).**
  > It is, every sixty-four scan events.
- **Each look reads the process's processor time**: a call per chunk, only
  where a limit is set.

## Alternatives considered

**Lower the service's scheduling priority.** That is not a cap. A machine with
idle processors runs the service flat out, and the setting would read as a
limit while being none. It would also lower a person's restore with the
background work, and a person's work is never held back.

**The operating system's own caps.** One platform caps a process's CPU
through the object a job runs in, and another through a control group. Both
are real caps, and both have three costs. They cap the whole process, a
person's work included, with no way for background work alone to give way.
Each is configured differently, and needs privileges or service-manager
configuration that the setting would then depend on. A third supported
platform has no equivalent at all.

**Per-thread processor time.** It does not follow asynchronous work across
threads (decision 2), and reading it is not portable.

**A duty cycle on wall time.** Run for so long, then rest for so long. That
measures how long work was busy, not what it spent on a processor. A job
waiting on a disk or a peer would be held back for waiting.

**A share of one core.** Rejected for decision 1's reason. Either convention
needs saying, and the whole machine is the one whose 100% means the same thing
on every machine.

## Amendment 1 (2026-11) — the limit is set through the service

**Built:** `Api/Commands.cs` and `Api/Results.cs` (contract 1.98),
`Agent/ServiceCommandHandler.Configuration.cs`, `Agent/DiagnosticBundle`,
`Cli/CliApplication` (the `settings` verb's `--cpu-limit`) and
`Web/wwwroot/app.js` (the settings card); held by
`Hosts.Tests/ServiceSettingsCommandTests`,
`Api.Tests/ConfigurationContractTests`, `Hosts.Tests/ClientModeTests`,
`Hosts.Tests/DiagnosticBundleTests`, `Web.Tests/SettingsRelayNamesTests` and
`Web.DomTests/ConfigEditingDomTests`.

Decision 7 left the limit readable on status but settable only in the
configuration file. The window, the read limit and the pool's width have been
set through the service since contract 1.44
([ADR-0037](0037-configuration-over-the-command-contract.md) Amendment 1,
FR-SVC-021), and the CPU limit now joins them on the same terms.

**The command.** `update_service_settings` gains `background_cpu_limit`, and
`service_settings` answers it:
- **Null keeps** what the file says. That is what a pre-1.98 client's request
  says by never naming it, so an older console's save does not remove a limit
  it cannot see.
- **An empty text clears** the limit, which means unlimited.
- **A value is judged with the others** before anything is written, by
  `CpuShare`, the parser the configuration load uses. A refusal names
  `background_cpu_limit`, never the file's path, and lands nothing else in the
  request.
- **A set limit is stored as the parser read it**, trimmed, and said back as a
  share "of this machine's processors".

It takes effect from each background job's next chunk. The limiter is read
afresh as jobs use it and replaced when its text changes (decision 4), so
nothing waits for a pass or a restart.

**The surfaces.**
- The CLI's `settings` verb shows the limit, `none` where none is set, and
  `--cpu-limit` sets it.
- The console's settings card has a field for it, labelled a share of this
  machine's processors, and sends it with the others. An emptied field is
  sent empty, so it clears.
- The diagnostic bundle's copy of the configuration names the limit beside
  the window and the read limit, null where none is stated. A background run
  that took far longer than its bytes explain is the first thing it is asked
  about.

## Amendment 2 (2026-11) — a walk is looked at too

**Built:** `Agent/PacedFileSystemSource`, held by
`Hosts.Tests/PacedFileSystemSourceTests` and
`Hosts.Tests/BackgroundCpuLimitTests`.

Decision 5 named a gap: a scan reads no content, so it never looked at the
limiter. That is the common case rather than an edge. An incremental run of a
tree where nothing changed reuses every file on its stat and reads no content,
so the walk is the whole of its processor time, and all of it went unpaced.

Where a change journal says which folders changed
([ADR-0188](0188-the-service-tells-a-backup-what-changed-since-the-sets-last.md)), the run
does not walk unchanged folders, and the journal saves the walk. Where none
does, the run walks in full: a platform without a journal, or one whose
journal the service lost.

**The walk is looked at.** When a capture's pace carries the CPU limiter, its
paced source looks at the limiter once every sixty-four scan events. Every
event passes through unchanged and in order. Sixty-four is often enough to hold
the walk of an unchanged tree to the share. It is also seldom enough that
reading the process's processor time costs little beside the stats in between.

**Both walks are held.** A capture walks twice, once to count its plan and
once to capture, and both go through the same source.

The wait is on the walk's own cancellation token, so a parked capture, a
preemption and the stop end it at once (decision 4).

**A byte limit alone leaves the walk alone.** A stat moves no bytes, and the
read limit is a limit on what is read (ADR-0074 §5).

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first, on an injected meter and a virtual clock: `Application/CpuShare`, `Application/CpuMeter`, `Application/CpuLimiter` and `Application/BackgroundPace`, the schema-12 field in `Application/ClientConfiguration`, the pace through every `Agent/BackgroundPacing` seam, and contract 1.97's `cpu_limit` on status. Setting it through the service and the diagnostic bundle's copy are owed |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--the-limit-is-set-through-the-service): contract 1.98 sets the limit through the service with the other installation settings — null keeps, empty clears, judged whole — and the CLI's `settings` verb, the console's settings card and `Agent/DiagnosticBundle` carry it |
| 2026-11 | Amended | [Amendment 2](#amendment-2-2026-11--a-walk-is-looked-at-too): `Agent/PacedFileSystemSource` looks at the CPU limiter every sixty-four scan events of a background capture's walk, so a run of an unchanged tree that reads no content is held to the share; a byte limit alone still leaves the walk alone |

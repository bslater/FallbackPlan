# ADR-0074 — Background byte-rate limits: a destination's transfer limit and the source-read limit

**Status:** Accepted
**Date:** 2026-09
**Requirements:** NFR-PERF-013, NFR-OPS-004, FR-SVC-013
**Related:** [ADR-0069](0069-the-background-window.md), [ADR-0029](0029-pipeline-and-service-concurrency.md), [ADR-0047](0047-backup-pool-and-priorities.md), [ADR-0054](0054-scheduled-restore-drills.md), [ADR-0058](0058-peer-write-adapter.md), [architecture 10 §2](../architecture/10-observability.md#2-technical-metrics), [command-contract](../../specifications/command-contract/README.md)

**Built:**
- The grammar, the pacing and the clock, in `Application`: `ByteRate`,
  `ByteRateLimiter`, `PacedStream` and `PacingClock`.
- The two configuration fields: `Application/ClientConfiguration` (schema 7's
  `background_read_limit`, its validation and its migration) and
  `Application/DestinationConfiguration` (`transfer_limit`).
- The registry and the two adapters, in `Agent`: `BackgroundPacing`,
  `PacedObjectStore` and `PacedFileSystemSource`.
- The seams, all in `Agent`:
  - `BackupRunner` — a capture's source reads;
  - `DestinationShipSink` — a direct-ship run's writes;
  - `FanOut` — the copy, the peer push, the read-back and the heal;
  - `ReplicaSweepJob` — the sweep's reads;
  - `RecoveryDrillJob` and `ServiceCommandHandler.RestoreSources` — the
    drill's reads;
  - `Scheduler` — which carries the background flag to all of them.
- The surfaces: `Agent/ServiceCommandHandler` (the limits on `get_status`),
  contract 1.43 (`Api/Results.cs`, `Api/ContractVersion.cs`),
  `Cli/CliApplication` (the `status` lines) and `Web/wwwroot/app.js`
  (`limitsNote`).
- The tests: `Application.Tests/ByteRateTests`,
  `Application.Tests/ByteRateLimiterTests`,
  `Hosts.Tests/BackgroundRateLimitTests`, `Hosts.Tests/PeerRateLimitTests`,
  `Hosts.Tests/ClientModeTests`, `Api.Tests/ContractAdditiveFieldsTests`,
  `Api.Tests/ConfigurationContractTests`,
  `Web.Tests/ConsoleBackgroundLimitsScriptTests` and
  `Web.Tests/StatusRelayNamesTests`.

---

## Context

NFR-PERF-013 promises that background activity observes configured CPU,
disk, network and time-window limits. [ADR-0069](0069-the-background-window.md)
built the window, and chose it first for a reason that decides this record's
scope too. The window was the limit this container could settle
deterministically. CPU is exactly the figure NFR-PERF-007 already discounts
as container measurement: its acceptance is a percentage over sixty seconds of
whatever machine the tests happen to run on.

A byte rate is different. It is arithmetic over bytes and a clock, and the
codebase already takes its clocks as arguments, so a rate can be proved by the
waits a limiter asks for without sleeping through any of them. That makes two
of the three remaining limits buildable and provable here: the network and the
disk. CPU stays named and unbuilt.

The ask behind them is concrete. A peer's link is somebody else's, which is
why a peer is never drilled unless its operator states a cadence
([ADR-0054](0054-scheduled-restore-drills.md) Amendment 3), and why a peer
drill's pull is capped. Nothing paced the sync itself, or the capture reading
a laptop's disk while its owner works.

## Decisions

### 1. Two limits, each where the knowledge is

- **`transfer_limit` on a destination** — the network limit. It is per
  destination because the operator knows which link is slow or shared. An
  installation-wide cap would also throttle a drive on the desk, which is not
  network at all, while a NAS mount or a far-away peer gets exactly the limit
  written on it.
- **`background_read_limit`, installation-wide** — the disk limit. It is the
  rate background captures read their sources at, summed across the captures
  running at once, because the disk it protects is this machine's.

Both are absent by default, which means unlimited. That is also what every
file written before schema 7 says by not mentioning them. The configuration
moves from schema 6 to 7 with nothing to migrate, for the same reason every
optional addition has raised the version: a schema-7 file handed to an older
build must be refused by name rather than half-read.

### 2. Background means what the window already means

A limit paces exactly what the scheduler starts with `userInitiated: false`:
- captures;
- fan-out syncs;
- replica sweeps;
- drills.

It never paces a person. That is ADR-0069's rule, restated for pacing, and
the predicate already reaches every one of those sites. A destination's limit
paces both directions. A sweep or a drill of a peer reads over the same link a
push writes over, and a limit that capped one direction would be the setting an
operator thought they had and not the one they got.

### 3. One limiter per limit, shared by everything it governs

`Agent/BackgroundPacing` keeps one limiter per limited destination and one for
source reads. Every background job that moves bytes to or from a destination
draws from that destination's limiter. So two sets syncing to one peer get the
peer's rate between them. A limiter built per job would give each the full
rate, and the link the operator capped would carry twice what they wrote.

Limits are read from the configuration as each job uses them, as the window
is read each pass. A limiter is replaced when its limit's text changes, so an
edit takes effect without a restart. A job already paced at the old rate
finishes on it.

### 4. The pacing: a virtual-scheduling token bucket with a one-second burst

The limiter keeps the instant at which everything admitted so far would have
finished at the rate. Each acquisition moves that instant on by its own cost,
and the caller waits for whatever part of it lies more than one second past
now. The second is the burst:
- an idle limiter lets a second's worth through at once;
- so a small object is never delayed;
- and a limit costs nothing until it is actually reached.

Idle time never banks more credit than that one second.

**A stream is charged as it moves.** A paced stream moves at most 64 KiB per
call. So a whole-blob read pays as it streams, rather than running up one
debt that stalls everything else sharing the limiter behind it. The stream
changes nothing else — not a byte, a position or a length. Sparse capture
seeks and reads `Length`, and it still works through the wrapper. The holes
it skips are not read, so they are not charged.

**A cancelled wait gives its cost back.** Its bytes never passed. A capture
the window parks, a job a preemption pauses and a service that is stopping
must not leave a debt that holds back the next job to use the limit.

**The clock is injected.** `Application/PacingClock` is the reading and the
wait. The runtime takes one through its options, so a test proves a rate by
the waits it asked for, with no process-wide state.

### 5. The seams, and the two deliberately left unpaced

| Background work | What is paced |
|-----------------|---------------|
| A capture | The source files it reads, through the read limit (`PacedFileSystemSource`). Scanning, probing and revalidation stat rather than read, and pass through. |
| A direct-ship run | The ship sink's writes to each destination, through that destination's limit. |
| A fan-out to a local path | The replica store: the copy's writes and the read-back's reads. |
| A fan-out to a peer | **The session's stream itself**, for the push and the challenge. That is the wire, and it is the one place that counts exactly what crosses the peer's link. The retrieval read-back and the heal pace the peer's replica store. |
| A scheduled sweep segment | The replica store it reads. |
| A drill | The restore source it reads through: a local replica or a peer's retrieval session. |

Pacing the peer push at its source store would have been simpler. It would
also have charged local-only reads, such as the hash that checks a resumable
prefix. The wire is what the limit is about.

The drill needed one seam of its own. It restores through the service's own
restore-source verbs, on a handler it builds for itself. That handler is
marked to pace its sources. Every other handler serves a person, so a person's
restore is never paced.

**Two paths stay unpaced by decision:**
- **The ship sink's reads.** The sink is also a direct-ship set's archive
  store, and a read through it may be a person's restore running beside a
  background backup. Only the run's own writes are paced.
- **The push a retention apply makes** (`ConvergePeersAsync`). A person
  starts it.

### 6. The grammar: binary units, strict, with the defect named

A rate is a whole number and a unit per second: `B/s`, `KiB/s`, `MiB/s` or
`GiB/s`.
- **The decimal units people type are refused**, with the binary one named.
  The two readings of the same text differ by up to seven percent, and a limit
  that quietly means something other than it says is the failure this grammar
  exists to prevent.
- **Anything slower than 1 KiB/s is refused** as a stop rather than a limit.
  Leaving the setting out is how "unlimited" is said.
- **A form this build cannot read is refused at load**, as the window's is. A
  transfer-limit refusal names its destination.

### 7. Seeing them, but not setting them

Contract **1.43** puts the limits on `get_status` as `background_limits`: the
read limit, and each limited destination's rate, as the configured text and
as bytes a second. It is null when nothing is limited, which is also what a
pre-1.43 service says.

The CLI prints one line per limit above the matrix, beside the window. The
console adds a clause to both overview subtitles. The limits are edited in the
configuration file only, as the window and `max_concurrent_backups` are. The
console control ADR-0069 §8 names as owed is owed for these too.

## Consequences

**Positive**

- NFR-PERF-013 has three of its four limits, and the proof page's row says
  which three.
- A paced job is still pausable and cancellable, and a person is never slowed
  by a limit.
- An installation with no limit behaves exactly as it did. The compatibility
  pin is a case of its own in the host suite.

**Negative**

- **CPU is still unbuilt**, for ADR-0069's reason.
- **A limit paces bytes, not requests.** A transfer of many small objects is
  dominated by per-request latency, which a byte rate does not cap.
- **A person can wait behind a paced job.** A person's sync of a pair whose
  background sync is already running is coalesced into that run, as it always
  has been, and that run is now paced. A person asking to back up a set whose
  background capture is running is told `already-running`, as before. Neither
  path paces the person's own work; both can make them wait for background
  work that is paced.
- **A peer's limit counts the wire, and a local path's counts object bytes.**
  The peer's includes the protocol's framing, so the two are close but not
  identical.

**Neutral**

- The burst is one second's worth, fixed.
- Only binary units are read.

## Alternatives considered

**One installation-wide network cap.** Simpler to state. It would throttle a
USB drive by a rule written for an uplink, and it cannot say that one peer is
on a metered link while another is on the same LAN.

**The operating system's throttles** — I/O priority classes, background
processing modes, traffic shaping. They are platform-specific, the service
cannot test them deterministically, and none expresses a rate an operator can
reason about. They could complement these limits later, and they do not
replace them.

**The platform's token bucket** (`System.Threading.RateLimiting`). It ships
with the web framework and as a package of its own, not in the runtime the
service and `Application` are built on, so taking it is a new dependency,
though an operational one ([ADR-0019](0019-third-party-dependency-policy.md)
§1). What decides it is the clock: it takes none. It refills on a timer of
its own, or whenever its owner asks it to, so a test could prove a rate only
by sleeping through it — the measurement this record chose byte rates to
avoid. Its permits are also a count no larger than the bucket, so a stream
would split every read to fit a second's worth.

**Bodu's `RateGate`.** The [Bodu 1.0.0 survey](../bodu-1.0.0-survey.md) parked
it for this slice. It is a different tool: synchronously, it admits at most
one call per interval and drops the calls in between. A byte limit has to
charge every byte, and delay rather than drop. The survey records the outcome.

**Pace the peer push at its source store.** It is one seam for local and
peer alike, and it charges local-only reads that never cross the link. The
wire is what the limit governs.

**Pace every read through the ship sink.** That would pace a person's restore
of a direct-ship set whenever a background run of the same set was live.

**Build the CPU cap.** See [ADR-0069](0069-the-background-window.md): its
acceptance is a machine-dependent percentage this container can only measure
as container measurement.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | The disk and network limits built together, the second and third of NFR-PERF-013's four; red tests first, then the limiter, the seams and contract 1.43, with each seam's removal confirmed to turn its test red. CPU remains unbuilt and named |

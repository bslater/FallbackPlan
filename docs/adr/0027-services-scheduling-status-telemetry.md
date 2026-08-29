# ADR-0027 — Push-2 service shapes: scheduling, job-state store, status model, instrumentation

**Status:** Accepted
**Date:** 2026-08
**Requirements:** NFR-OPS-001, NFR-OPS-002, NFR-PRIV-002, NFR-TIME-001
**Related:** [architecture 10](../architecture/10-observability.md), [architecture 11 §3](../architecture/11-solution-structure.md#3-local-state-separation), [phase-1 plan](../phase-1-execution-plan.md)

---

## Context

Phase-1 push 1 delivered the capture and restore core. Push 2 adds the
service layer — an Agent that runs backups on a schedule, the user-level
status model of architecture 10 §1, and production instrumentation — and
the planning survey flagged three shapes as unspecified: schedule
semantics with missed-run behaviour, where the job-state store lives in
the three-way local state split, and the OpenTelemetry instrument surface
with its privacy bounds. Under the standing rule — every gap is a flagged
decision, never a silent choice — each is pinned here before the first
service byte exists.

A structural decision rides along: the service logic lands in
**`FallbackPlan.Application`** (the use-case layer architecture 11 §1 has
reserved from the start), with **`FallbackPlan.Agent`** as a thin host
process. The client configuration and durable-state types move from the
CLI into Application so the CLI and the Agent share one implementation;
the CLI keeps its behaviour unchanged.

## Decisions

### 1 Schedule semantics and missed runs

A backup set's `schedule` is one of two forms, parsed strictly:

| Form | Example | Meaning |
|------|---------|---------|
| `every <n><unit>` | `every 4h`, `every 30m` | Interval, anchored to the last **completed** run of that set |
| `daily at <HH:mm>` | `daily at 02:30` | Once per local calendar day at that local time |

Rules:

- **Next-run derivation is pure**: `Schedule.NextRun(lastCompleted, now)`
  is a function of its arguments, unit-testable without a clock
  (NFR-TIME-001: no correctness property depends on wall time — a wrong
  clock mis-times a backup; it never corrupts one).
- **Missed runs coalesce to exactly one catch-up run.** If the Agent was
  off through five scheduled times, it owes one run at start-up, not
  five. A backup is idempotent state capture, not an event log; a
  backlog storm after two offline weeks would be all cost and no
  information.
- **One run per set at a time.** A run that overlaps its own next
  scheduled time simply defines the new anchor when it completes;
  nothing queues.
- An empty or absent `schedule` means manual-only — the Agent skips the
  set and says so.

#### Amendment (2026-08): the rules under a pass that ticks, a pool, and a save that backs up

[ADR-0047](0047-backup-pool-and-priorities.md) leaves the schedule grammar
and coalescing untouched and changes what enforces the third rule and what
"nothing queues" covers. **One run per set at a time** was structural while
the pass was serial; a pass now ticks during long captures and during
transfers (the pass no longer awaits its fan-out inline), so the rule is
enforced explicitly — a set whose most recent journal row is unsettled and
still active in the queue is reported `already-running`, never double-queued.
The enforcement sits inside the enqueue itself, so a manual trigger and the
save-time first backup obey the same rule as the pass (ADR-0047
Amendment 3).
"Nothing queues" still holds for *schedule-owed* runs, but no longer
describes every capture: **saving a new set queues its first backup at
once**, and a set gaining a destination queues that destination's seed
(ADR-0047 §5) — a person clicking Save is asking for a backup, not a
schedule entry. The §4 status model also gained a live state this record
predates: a run suspended for a higher-priority one reports `Paused` and
resumes unattended (ADR-0047 Amendment 1); it is in-flight, not settled, and
anchors nothing.

#### Amendment (2026-08-05): the arithmetic moves to a shared library

The grammar above is unchanged and the rules still hold; what changed is
who computes the occurrences. `Schedule` originally derived them inline,
and that hand-rolled arithmetic shipped a defect: the daily branch mixed
the argument's own clock with a machine-timezone conversion, so it was
correct on a UTC machine and wrong by a day on any other — the exact
class of failure a purity rule exists to prevent, and one that a
UTC-only CI matrix could not see.

`Schedule` now delegates to
[`Bodu.Globalization.Recurrence`](../bodu-recurrence-requirements.md),
adopted against a written requirements statement:

| Form | Backed by | Why that type |
|------|-----------|---------------|
| `every <n><unit>` | `AnchoredInterval` | The series `anchor + k·interval`, which cron and RRULE cannot express — our anchor is the last completed run, not a calendar position |
| `daily at <HH:mm>` | `CronExpression` (`<m> <H> * * *`) | Calendar-aligned time of day, in the offset the caller supplies |

Due-ness becomes one comparison against the previous occurrence, which
is what keeps the coalescing rule structural rather than arithmetic: the
answer is a boolean about a single instant, so five slept-through times
cannot become five owed runs.

The grammar stays FallbackPlan's, deliberately. The library could accept
raw cron or RRULE text directly, but a schedule is a user-facing promise
about their backups; `daily at 02:30` is a promise a person can check,
and `30 2 * * *` is not. Richer forms (weekly, monthly, explicit cron)
are now cheap to add and are not added here — this amendment records an
implementation swap with no behaviour change, proven by the schedule
tests passing unchanged, in three timezones.

The dependency is admitted under [ADR-0019](0019-third-party-dependency-policy.md)
on the strength of its own purity guarantee — the library's test suite
scans its compiled assembly for wall-clock and timezone APIs and fails
if it finds any — and is pinned to the Application project by
`DependencyRuleTests.Only_Application_may_reference_the_recurrence_engine`,
with the canary that the reference exists.

### 2 The job-state store

Job state is a **client-domain JSON journal in the state directory**
(`jobs.json`, beside `state.json`), holding the architecture 10 §3 state
machine per run: identity, set, timestamps, state transitions, and the
terminal outcome with its failure class.

Placement rationale, against the 11 §3 three-way split: job state is not
rebuildable from the repository (the repository records snapshots, not
attempts), so it cannot live in the catalogue — but its **loss is
tolerable**: resumability belongs to the engine (spool checkpoints and
the intent journal), never to this file. Losing `jobs.json` loses
history and the schedule anchor — the next Agent pass runs one catch-up
backup and rebuilds the anchor. It is therefore durable-local-state
*adjacent* but its own file, so corruption or deletion of job history
can never touch device identity.

> **Amended 2026-08 ([ADR-0049](0049-service-lifecycle-hygiene.md)):** the
> journal is reconciled at every service start — a row left unsettled by a
> stop, a kill, or a faulted run is landed on `FailedRecoverable` with a
> notice raised, so no row ever claims to run in a process that is not
> running it.

`FailedRecoverable` and `FailedPermanent` stay separate states end to
end (10 §3): the first is retried by the Agent on the next pass, the
second is surfaced and never silently retried.

### 3 Instrumentation: in-box OpenTelemetry API, exporters deferred

Instrumentation uses **`System.Diagnostics.Metrics.Meter` and
`ActivitySource`** — the .NET OpenTelemetry instrumentation API that
ships in the BCL. Any OTLP/Prometheus exporter attaches from the host
process later; **no exporter packages enter the tree now**, keeping the
ADR-0021 committed-feed surface unchanged and the dependency review
honest (a collector stack is not something to vendor as a side effect
of a scheduling commit).

Meter name: `FallbackPlan.Engine`. Instruments (10 §2, NFR-OPS-001) —
snake-cased under a `fallbackplan.` prefix, units in UCUM:

| Instrument | Kind | Unit | What |
|------------|------|------|------|
| `fallbackplan.scan.files` | counter | `{file}` | Files scanned, attribute `outcome`: `captured` \| `reused` \| `failed` |
| `fallbackplan.scan.bytes` | counter | `By` | Logical bytes scanned |
| `fallbackplan.archive.bytes_logical` | counter | `By` | Bytes presented to segmentation |
| `fallbackplan.archive.bytes_stored` | counter | `By` | Bytes actually written after reuse + compression — dedup/compression ratios derive from the pair |
| `fallbackplan.archive.segments` | counter | `{segment}` | Segments produced, attribute `reused`: `true` \| `false` |
| `fallbackplan.blob.sealed` | counter | `{blob}` | Blobs sealed, attribute `class`: `data` \| `meta` |
| `fallbackplan.blob.fill_fraction` | histogram | `1` | Fill fraction at seal — the request-amplification canary (10 §2) |
| `fallbackplan.store.requests` | counter | `{request}` | Provider requests, attribute `operation`: `put` \| `get` \| `list` \| `delete` |
| `fallbackplan.catalogue.lookup_duration` | histogram | `us` | Path/dedup lookup latency (NFR-PERF-004/010) |
| `fallbackplan.publication.duration` | histogram | `s` | Steps 1–9 wall time per snapshot |

**Privacy bound (NFR-PRIV-002), enforced by test:** the attribute
allowlist above is exhaustive — `outcome`, `reused`, `class`,
`operation`. No instrument ever attaches a path, filename, repository
identifier, snapshot identifier, destination endpoint, or anything
derived from file content. An automated assertion enumerates every
emitted measurement's attributes against the allowlist; a new attribute
fails the build until it is added here *and* judged against
NFR-PRIV-002.

Activities (tracing) use source `FallbackPlan.Engine` with spans
`backup`, `scan`, `publish`, `restore` — same attribute discipline.

### 4 The status model

`FallbackPlan.Application` implements the architecture 10 §1.1
vocabulary as a closed enum — `Captured`, `Protected`, `Replicated`,
`Verified`, `PolicyCompliant`, `Degraded`, `Unrecoverable` — with the
two never-merge rules (NFR-OPS-002) enforced by construction: derivation
returns the full per-destination detail, and any summary is computed
from it, never stored beside it.

Phase-1 derivation, honest about its inputs: with a single local
destination, a backup set whose latest snapshot committed is `captured`
— and is reported `protected` **only** when the store demonstrably sits
in a different failure domain than the source (different volume/device
by identity comparison; same device is never `protected`, per PT-8).
`degraded` when the destination is unreachable or `check` reported
findings; `unrecoverable` only from a damage report naming required
objects with no readable copy. `verified` carries coverage and age from
the recorded verify runs, never a bare tick (10 §1.2). The fuller
states (`replicated`, `policy-compliant`) activate with multi-destination
replication in a later phase; the vocabulary and derivation seams exist
now so the UI never has to re-learn the words.

#### Amendment (2026-08): the derivation gains its destination axis

That later phase is [ADR-0034](0034-hub-and-spoke-destinations.md), and the
promise above is called in: the derivation's input becomes **per set, per
configured destination** — reachability, sync state, declared failure domain,
last success — and the single-destination booleans retire. The vocabulary does
not change; what changes is what earns each word. `Captured` is commit to the
set's staging archive. `Protected` requires at least one destination outside
the source's failure domain **in sync** — staging never counts
([ADR-0018 Amendment 1](0018-replica-failure-domains.md)). Every destination
behind, unreachable or refusing is `Degraded`, with the per-destination reason
carried, not summarised away.

> **Amended by [ADR-0050](0050-completed-run-record-and-drill-down.md)
> (2026-08).** The reason clause was unmet on the most common `behind` of
> all: the demotion the derivation itself decides — a destination whose last
> sync predates the set's last completed backup — carried no reason, because
> the ledger's error text is nulled by every success. Since ADR-0050 every
> such demotion states its cause in the row's detail (so the existing
> warning template carries it to every client), and contract 1.22 adds the
> machine cause code plus the compared `last_completed_at` operand to the
> wire. The catch-up window is named as the self-healing transient it is
> ([ADR-0035 §8](0035-destination-fitness.md)'s warning semantics), never a
> bare "behind".

A destination kind the schema accepts but the
runtime cannot serve yet reports as exactly that — a stated incapacity, not a
failure. The never-merge rules hold at the same single derivation site, which
is the point of having one: the matrix is the truth and every roll-up is
computed from it in front of the reader.

With destination verification built
([peer-protocol 04](../../specifications/peer-protocol/04-verification.md)),
`verified` is earned the same per-destination way: the repository-level
verification input this ADR sketched was never produced in practice and is
replaced by per-destination stamps from the sync ledger — when bytes were
last proven, how many ranges of how many eligible objects, and the highest
publication sequence the sample covered. The roll-up says `verified` only
for a destination that is in sync, outside the machine's failure domain
([ADR-0018 Amendment 2](0018-replica-failure-domains.md)), and whose proof
covers what its last sync delivered; the claim carries the real coverage
fraction and its date, never a bare tick, exactly as §4 required.

#### Amendment (2026-08): a warning class that does not move the state

§4 kept the derivation a pure function of observed facts, and deliberately gave
it no clock. That was right, and it had a consequence nobody had noticed: age
could not enter the derivation at all, so a destination proved on day 1 and one
proved on day 400 read identically.

The fix does not give `Derive` a clock. The age arrives as a number on the
destination row, alongside the per-kind bound it is compared against, computed
where the row is built ([ADR-0035 §7](0035-destination-fitness.md)) — so the
derivation stays a function of plain values and gains no new axis of test
surface on a signature this section makes normative.

What it adds is a **warning class that does not move `ProtectionState`**. An
overdue proof, an address that cannot work, a convergence filter that could not
be computed: each is named in the warnings and none of them changes the word. A
state that means two different things is worse than a warning that means one,
and an old proof is still a proof.

This also fixes the boundary between the two channels §4 left implicit. A
condition recomputed on every derivation is a **warning** and therefore
self-clearing; a finding that must survive being ignored is a **notice** and
persists until acknowledged. The notice store gained the ability to resolve an
entry for that rule to be usable — without it, every transient condition became
a permanent nag — and now refreshes a re-raised notice's message, which it had
documented and not done.

## Consequences

### Amendment (2026-08): the peer-host model is superseded

This ADR made the CLI and the Agent **peer hosts** over a shared `Application`
library, each opening the repository and the state directory. That was right
for a world where one process ran at a time and exited.

It does not survive a service that runs continuously. Two processes sharing a
state directory share a writer identity, and therefore the single monotonic
gapless sequence space architecture 04 §2 requires — with consequences up to
and including a write intent reported durable when it was never written, and
void deltas published for sequence numbers another process is still using.

[ADR-0028](0028-service-boundary-and-deployment-topologies.md) supersedes the
peer-host arrangement: the service holds the writer role exclusively and the
CLI becomes a client, keeping a direct mode for when no service is running.
**Everything else in this ADR stands** — the schedule semantics, the job-state
journal, the status model and the instrumentation are unchanged, and the
Application layer they live in is exactly what the service now hosts.

- The Agent stays a thin host; every service behaviour (schedule
  arithmetic, job-state transitions, status derivation) is a pure,
  tested function in Application.
- No new package identities for instrumentation — that remains in-box.
  Exporter wiring is a host concern for the phase that ships a UI or ops
  deployment. Scheduling since took one identity,
  `Bodu.Globalization.Recurrence`, per the amendment to §1: the original
  "no new package identities" claim covered this ADR as first written and
  no longer holds for the schedule arithmetic.
- Missed-run coalescing means the Agent's first act after downtime is
  one incremental backup — bounded work, and exactly what the user
  wants after reopening a laptop.
- Job history is deliberately sacrificial; anything that must survive
  belongs to the engine's journal or the repository itself.

## Alternatives considered

**Cron expressions for `schedule`.** Familiar to the operators who already know
them, and wrong for this audience: the product's schedule is "every few hours"
or "overnight", and a five-field expression makes the common case expressible
in several ways and the uncommon case expressible at all. Rejected in favour of
the two strict forms of §1 — a grammar small enough that an error message can
state the whole of it.

**Replay every missed run after downtime.** The literal reading of a schedule,
and it produces a backlog storm after two offline weeks. Rejected because a
backup is idempotent state capture rather than an event log: five missed runs
and one missed run leave the repository in the same place, so four of them are
pure cost.

**Queue overlapping runs of the same set.** Rejected for the same reason. A run
that overruns its own next scheduled time has nothing to hand to a successor;
letting it define the new anchor when it completes is both simpler and what the
user meant.

**Keep the schedule arithmetic hand-rolled.** This was the original decision,
and the [amendment to §1](#amendment-2026-08-05-the-arithmetic-moves-to-a-shared-library)
records why it was abandoned: the daily branch mixed the argument's clock with
a machine-timezone conversion, so it was right on a UTC machine and a day wrong
everywhere else — invisible to a UTC-only CI matrix. Taking a dependency for
calendar arithmetic bought a library that is tested in timezones this project's
CI does not run in.

**A durable, transactional job history.** Rejected: it would make the Agent
own a second store with its own consistency obligations, to hold data that is
explicitly disposable. What must survive a crash belongs to the engine's
journal, and conflating the two would make the journal's guarantees harder to
reason about, not the history's stronger.

**Ship an OpenTelemetry exporter in the box.** Rejected for §3's reason: an
exporter is a deployment decision with a package identity and a network
destination, and the phase that ships a UI or an ops deployment is the phase
that can make it. The API is in-box so the instrumentation exists and costs
nothing until someone wires it up.

**Derive user-facing status directly from the job state machine.** Tempting,
because the states are right there. Rejected: `Running` is not `Protected`, and
a status model that says what the software is *doing* rather than whether the
user's data is *safe* answers a question nobody asked. §4 keeps them separate
for that reason.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Schedule semantics with missed-run coalescing, the journal-backed job-state store, in-box OpenTelemetry API with exporters deferred, and the derived status model |
| 2026-08 | Amended (§1 ×2) | The due-ness rules restated for a pass that ticks during captures, a pool, and save-queues-first-backup (ADR-0047); the schedule arithmetic moved to the shared library both the service and the console read |
| 2026-08 | Amended (§4 ×2) | The status derivation gained its destination axis — one `DestinationStatus.Describe` both producers call — and a warning class that surfaces without moving the derived state |
| 2026-08 | Amended (Consequences) | The peer-host model superseded: peers are destinations under ADR-0030's pairing, not a second service shape |

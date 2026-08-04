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

## Consequences

- The Agent stays a thin host; every service behaviour (schedule
  arithmetic, job-state transitions, status derivation) is a pure,
  tested function in Application.
- No new package identities. Exporter wiring is a host concern for the
  phase that ships a UI or ops deployment.
- Missed-run coalescing means the Agent's first act after downtime is
  one incremental backup — bounded work, and exactly what the user
  wants after reopening a laptop.
- Job history is deliberately sacrificial; anything that must survive
  belongs to the engine's journal or the repository itself.

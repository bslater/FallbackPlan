# ADR-0047 — The backup pool: concurrency, priorities, and a pass that never waits for the transfer lane

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-SVC-012, FR-SVC-013, FR-SVC-014, FR-SVC-015, FR-DEST-014
**Related:** [ADR-0029](0029-pipeline-and-service-concurrency.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0027](0027-services-scheduling-status-telemetry.md), [ADR-0037](0037-configuration-over-the-command-contract.md)

---

## Context

Three facts collided in the 2026-08 field reports and the owner's direction
that followed them.

First, the scheduler pass was hostage to the transfer lane: `RunPassAsync`
awaited its fan-out and sweep phases inline, and the host awaited the pass —
so for the whole duration of a multi-hour destination copy, **no pass ran at
all**. Due-ness was never evaluated for any set; scheduled incrementals were
not queued, not refused, simply never considered; and the missed slots then
coalesced into a single catch-up when the copy finished. ADR-0029
Amendment 2 had already stated the intent — "a first sync can run for hours,
and the writer lane, where backups queue, must never stall behind one" — and
the pass structure quietly violated it for every scheduled run.

Second, ADR-0029 §4's "one writer lane, one worker" predates one process
holding many per-set archives (ADR-0034 §1): each set now has its own
staging archive, writer sequence, spool namespace and catalogue, so two
sets' captures no longer contend for a writer role — only for the disk.

Third, the owner requires ordering the product had no words for: sets and
destinations carry priorities; a saved set backs up immediately; a
destination newly referenced by a set is seeded immediately and skipped by
incrementals until it holds a full copy.

## Decision

1. **The pass never waits for the transfer lane.** `RunPassAsync` evaluates
   due-ness and awaits its captures, then enqueues fan-out and sweeps and
   returns, handing the still-running transfer work back as
   `AgentPassResult.Transfers`. The service loop deliberately does not await
   it — the loop ticks on its interval whatever the transfer lane is doing —
   and `--once` (host and `AgentPass` alike) does, because it tears the
   runtime down on return. Nothing piles up when passes go un-awaited: the
   per-pair job identities (`sync-{set}-{dest}`, `sweep-{set}-{dest}`) are
   stable, so a duplicate enqueue is refused and the next pass re-evaluates
   the pair. The phases guard their own exceptions, exactly as they did when
   the pass awaited them inline; the single transfer worker preserves
   converge-before-sweep ordering per pair (widening that lane later needs a
   per-pair ordering guard, and this sentence is the reminder).
2. **One run per set at a time, stated.** ADR-0027 §1 always said it; it was
   structural only because the serial pass never looked while its own
   captures ran. A pass that ticks during long captures now checks: a set
   whose most recent journal row is unsettled and still active in the queue
   is reported `already-running`, never double-queued. `Scheduler.Enqueue`
   also honours the queue's refusal instead of silently orphaning its
   completion — unreachable while job identities are fresh GUIDs, and a hang
   waiting to happen the day that changes.
3. **The writer lane is a pool.** `max_concurrent_backups` (configuration
   schema 5; 1..5, absent means 2) sets how many writer-lane workers the
   queue spawns. Safe by construction: per-set archives, sequences, spools
   and catalogues are disjoint, and the sync ledger's mutation is
   lock-correct for concurrent writers. Read once at service start — the
   workers spawn with the runtime, and a configuration that will not load
   answers the default rather than stopping the service. The reader lane
   stays one worker (restores are internally bounded), the transfer lane
   stays one (destinations mostly share an uplink; ADR-0029's amendment
   already names widening it as the measured axis).
4. **Priorities, under people.** Sets and destinations carry an optional
   integer `priority` (schema 5; a set's destination reference may override
   the destination's for that set). The queue orders waiting work by
   `(initiation, -priority, arrival)`: a user-initiated job outranks any
   priority — ADR-0029 §4's rule, unchanged — then higher priority wins,
   then arrival, so nothing starves. A backup's job carries its set's
   priority; a sync's carries its destination's. Contract 1.17 carries both
   fields on the descriptors, null-preserving so a pre-1.17 client edits
   nothing it cannot see.
5. **Saving a set is asking for its backup.** A new set's upsert queues its
   first capture at once (user-initiated — a person just clicked Save) and
   fans out to its destinations when the capture completes; the reply names
   the queued job. A set gaining a destination marks the pair `needs_full`
   in the sync ledger and queues that destination's seed immediately.
6. **The ledger learns what "full" means** (sync ledger schema 2):
   `baseline_completed_at` records when a destination first held a complete
   copy — on the staging architecture every successful sync converges the
   whole archive, so the first success IS the baseline, and rows written
   before schema 2 seed their baselines from their recorded successes at
   open ("replicas seed the ledgers": no install re-ships terabytes to learn
   what it already holds). `needs_full` flags the pairs rule 5 owes a seed;
   `baseline_snapshot_id` and `last_reconciled_at` are declared now and
   filled by the direct-to-destination record that supersedes staging
   (planned as the next record in this sequence), where "skipped by
   incrementals until full" also gains its per-file force.

## Consequences

**Positive** — scheduled incrementals hold their cadence during arbitrarily
long transfers; a saved set produces a populated destination without a
schedule or a person remembering to run one; operators can order sets and
destinations by importance; two sets can back up at once on a machine with
the disks for it.

**Negative** — up to N captures contend for source disks and the pool takes
no I/O measurements before admitting them (the cap and the default of 2 are
the guard); a pool width change needs a service restart; the priority field
is advice to a queue, not a bandwidth guarantee.

**Neutral** — pre-1.17 clients and schema-4 files behave exactly as before
(absent fields mean the defaults); the un-awaited transfer task changes no
observable ledger behaviour, only when the pass answers.

## Alternatives considered

- **Preempting a running transfer when a capture becomes due.** Unnecessary
  once the pass is decoupled — the lanes are independent, so captures never
  wait for copies. Suspend/resume of running *backups* under priority
  pressure is the owner's stated requirement and lands with the handler
  preemption record (planned); this record only widens the pool.
- **A per-destination transfer worker.** ADR-0029's amendment already names
  it as the axis to widen on measurement; the pass decoupling removes the
  starvation that made it look urgent.
- **Evaluating due-ness in a separate timer instead of decoupling the
  pass.** Two clocks racing over one journal, to avoid handing a task to a
  caller.

## Amendment 1 — Preemption: true suspend/resume (2026-08)

Rule 4 ordered the queue; this amendment makes the order bite while work is
already running. The owner's requirement: a triggered backup of higher
priority than a running one must **pause** the running backup when no
concurrent handler is free, and the paused run must resume — not restart —
when a slot frees.

1. **The pause gate.** A backup's job carries a `PauseGate`; the capture
   pipeline checks it between scan events (`IPauseGate`, honoured in the
   publication's scan loop), so a run parks at a file boundary — never
   inside a file — with its walker, catalogue session and spool held in
   memory exactly where they were. A resumed run continues from the next
   scan event: nothing is re-scanned, nothing re-packaged, and any blob
   sealed short at the boundary is the already-proven "durable but
   unreferenced" state.
2. **Park frees the slot.** The scheduler attends a gated writer job by
   racing its task against the gate's park signal. A parked run keeps its
   task alive but loses its worker: the freed slot is announced to the lane,
   and every pickup weighs the best-ranked parked run against the queue's
   head by the same `(initiation, -priority, arrival)` key — a suspended run
   resumes before new work of equal standing starts.
3. **One victim, chosen worst-first.** An arriving writer job preempts only
   when every pool worker is busy, and only the worst-ranked running job
   that carries a gate and is not already pausing — and only when the
   incomer strictly outranks it. A job without a gate is never paused; the
   incomer waits behind it, exactly as before this amendment.
4. **Paused is a live state.** The journal says `Paused` the moment the run
   parks and returns to `Publishing` ("resumed") when it wakes; the console
   and the CLI treat it as in-flight — a waiting `backup` command keeps
   waiting, because the run finishes unattended. The one-run-per-set rule
   already counted an unsettled journal row as running, so a paused set
   cannot be double-queued or deleted from under its run.
5. **Suspension is bounded.** A parked run holds memory and a live write
   intent, so it self-cancels past a max-pause age (an hour by default) into
   the interruption-safe re-run path — the same path shutdown takes:
   disposal cancels the run's own token, which cuts through the park, and
   the void-delta discharge heals the remains on the next run. Crash safety
   is unchanged for the same reason: a paused run's on-disk state is an
   interrupted run's on-disk state.

## Amendment 2 — Preemption hardened: escalation, visibility, honest expiry (2026-08)

The scenario sweep ran Amendment 1's rules through a multi-worker pool for
the first time and found four places where the letter of the rules diverged
from their intent. Each is a rule refinement, not a reversal.

1. **A victim that never parks is escalated past.** Rule 3 asked exactly
   one victim and never re-asked; a victim stuck inside one enormous file
   blocked the incomer indefinitely while a responsive gated job sat beside
   it. After a configurable escalation delay (seconds, not minutes), if the
   chosen victim has not parked and outranked work still waits, the
   next-worst gated, not-yet-pausing job is asked — repeating, worst-first,
   until someone parks or candidates run out. A victim that parks late
   simply joins the paused set (rank still decides resume order), and its
   stale pause request is harmless.
2. **The max-pause expiry is generation-stamped.** Rule 5's timer asked "is
   the job paused *now*", so a run parked, resumed, and parked again just
   before an earlier timer fired was killed by the stale timer. Each park
   stamps a generation; a timer expires only the park it was armed for. The
   bound itself became host-configurable (`ServiceOptions.MaxPauseOverride`
   — a knob for hosts and harnesses, deliberately not configuration: the
   bound guards the service's own memory and intents).
3. **A suspension is visible on the progress stream, not only in the
   journal.** The pause gate accepts additional observers, and the runner
   registers the run's progress reporter: parking emits a `Paused` report
   that keeps the run's live counts — a watcher's meter must not zero — and
   the first scan event after resume refreshes the state.
4. **The terminal record carries the run's summary, never "resumed".** The
   journal keeps the prior detail when a transition supplies none, and a
   preempted run's prior detail was the transient "resumed" — which then
   survived onto the Complete row a person reads. Completion now always
   writes an explicit detail (the run summary); clean-vs-partial stays a
   state distinction.

The park/complete race is closed alongside (insertion into the paused set
is guarded by the job still running, and the supervisor removes paused and
running state under one lock), and a dequeued writer id absent from the
running table is logged (event 3759) rather than silently discarded.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Written from the 2026-08 field reports and the owner's re-architecture direction, alongside the plan that supersedes staging |
| 2026-08 | Built | The decoupled pass and its `Transfers` hand-back, the one-run-per-set guard, the writer pool (schema 5's `max_concurrent_backups`), set/destination priorities end to end (schema 5, contract 1.17, both console editors), first-backup-on-save, gained-destination seeding, and the sync ledger's schema 2 baselines |
| 2026-08 | Built (Amendment 1) | True suspend/resume under priority pressure: the pause gate through the capture pipeline's scan loop, park-frees-the-slot scheduling with resume-before-equal-work pickup, worst-first single-victim preemption, the live Paused journal state (console and CLI both treat it as in-flight), the max-pause self-cancel, and shutdown degrading a parked run to the ordinary cancelled → re-run path |
| 2026-08 | Built (surfaces) | Contract 1.19 puts the rules' full-backup facts on the status matrix — each destination row says when its baseline completed and whether the pair is owed its seed — and the console's overview renders them as a Full backup column ("awaiting seed" against a bare "behind"); a paused run stays a live jobs card that says why it is suspended |
| 2026-08 | Built (Amendment 2) | Escalating worst-first preemption past an unresponsive victim (`JobScheduler`), generation-stamped max-pause expiry with the `ServiceOptions.MaxPauseOverride` knob, pause/resume emitted on the progress stream with counts held (`PauseGate` observers through `BackupRunner`), the explicit terminal summary, and the park/complete race closed — pinned by `Hosts.Tests/JobSchedulerPreemptionTests` across multi-worker pools |

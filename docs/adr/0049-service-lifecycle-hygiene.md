# ADR-0049 — Service lifecycle hygiene: a reconciled journal, an in-process restart, and the startup configuration record

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-SVC-017, FR-SVC-010
**Related:** [ADR-0027](0027-services-scheduling-status-telemetry.md), [ADR-0033](0033-hosting-under-an-os-service-manager.md), [ADR-0045](0045-client-authentication.md), [ADR-0047](0047-backup-pool-and-priorities.md), [ADR-0043](0043-structured-logging-and-diagnostics.md)

---

## Context

A field report (2026-08, with the installation's own state directory as
evidence) showed the console rendering a backup as live for an hour —
"Publishing · waiting for the first progress event" — while Cancel answered
"no job is queued or running". The journal (`jobs.json`) is durable and the
scheduler's queue is not, and nothing reconciled them: a row left unsettled
loads back verbatim at every start, claiming to run in a process that is
not running it. Three ways to orphan one: a fault outside the runner's
catch list on a live service (the queue logs it and forgets the id, the row
stays); a job still queued at a clean stop (its delegate never ran, so it
never transitioned); a process kill. The orphan then closed a loop on the
operator: the live card's Cancel refused (it consulted only the in-memory
queue), deleting the set refused ("cancel it first"), and the overview's
backup buttons stayed disabled — the only exit was hand-editing
`jobs.json`. The uploaded state held two such rows.

The same report asked for two operator facilities: a restart of the
agent/service commandable from the console and CLI, and a startup record of
every configuration parameter the service operates against, so a diagnostic
log alone can explain its posture.

## Decision

1. **The journal is reconciled at start.** `JobStateStore.SettleUnfinished`
   lands every non-settled row on `FailedRecoverable` — the state the
   scheduler retries (10 §3), and not a committed one, so no schedule
   anchor is invented for a backup that never finished — with the detail
   naming the interruption. It runs in `ServiceRuntime.StartAsync`
   immediately after the writer role is acquired, which is the correctness
   licence: the sole writer means no other live process owns any of those
   rows. Not silent: settling anything raises a durable notice, because
   somebody's 3 a.m. run was interrupted and that belongs at breakfast.
   The queued-but-never-started shutdown case is deliberately healed here
   rather than at `JobScheduler` disposal — the sweep is the intended
   catch-all for every orphan class, kill included, not a patch for one.
2. **Cancel settles a run that is no longer live.** A cancel whose id the
   queue does not know, but whose journal row exists unsettled, transitions
   the row to `Cancelled` ("the run was no longer live") and acknowledges —
   the operator's remedy for an orphan created on a running service, where
   no restart intervenes. An absent or already-settled row keeps the
   refusal it always had.
3. **Deletion defers only to runs the queue is running.** `delete_backup_set`
   adopts the enqueue guard's rule (ADR-0047 Amendment 3): unsettled AND
   `Queue.IsActive`. An orphaned row no longer wedges a deletion behind a
   cancel that would refuse it.
4. **`restart_service` recycles in process** (contract 1.21). The host's
   run loop gains one outer iteration: teardown — listeners, queue,
   archives, sessions, the writer role — then `ServiceRuntime.StartAsync`
   again in the same process. Not a process exit, and the generated service
   units are the argument: systemd gets `Restart=on-failure` (a clean exit
   stays down), launchd gets `KeepAlive` (it comes back), Windows gets no
   failure actions and the SCM host transitions to Stopped on self-exit —
   three behaviours for one verb, and ADR-0033 forbids the agent rewriting
   registrations to align them. In process, the behaviour is identical
   everywhere. The acknowledgement is flushed before teardown begins;
   sessions die with the old runtime, which is the documented FR-USR-003
   contract, and both clients say so.
5. **Restart is the Owner's, locally, on a set-up installation.** The
   second Owner-only privilege after account management (ADR-0045): an
   operator is refused by name. Remote scope is refused — a paired console
   must not cut a machine it cannot see (ADR-0028 §6), and this verb would
   sever the very connection carrying its refusal. The no-accounts
   bootstrap window does not admit it: that window exists to create the
   first account, and an unset-up installation is not restartable by
   whoever reaches the socket. A host with nothing to recycle (`--once`, a
   bare handler) refuses with the reason. The console renders the control
   on Maintenance for the Owner alone, behind the typed confirm word; the
   CLI verb is `restart`.
6. **The startup configuration record** (FR-SVC-010). At every start the
   service logs what it RESOLVED, not what was typed: state directory and
   archives root each with provenance (flag / `FALLBACKPLAN_*` variable /
   machine-wide default / profile fallback), poll interval, backup pool
   width, remote binding, passphrase posture (a posture word, never a
   secret), and one line per configured set (roots, schedule, destinations,
   direct-ship, priority) and destination (kind, failure domain). Events
   3760–3763, Information tier. Paths are honest locally and redacted where
   records leave the machine — ADR-0043's rendering boundary, unchanged.

## Consequences

**Positive** — the journal tells the truth about a fresh process; the
stuck-card / can't-cancel / can't-delete loop is gone, with a notice where
silence was; an operator restarts the service from the console or CLI
without shell access to the machine; a support log begins with what the
service was actually operating against.

**Negative** — a run interrupted by a stop is recorded `FailedRecoverable`
even when it might have completed its snapshot commit just before dying (the
journal write raced the stop); the next pass's retry is the designed answer.
A restart interrupts running backups — resume-safe by the engine's own
checkpoints, but interrupted. The startup record adds a handful of
Information lines per start.

**Neutral** — pre-1.21 clients simply lack the verb; the recycle changes no
on-disk format; scheduling already ignored orphaned rows (ADR-0047
Amendment 3), so reconciliation changes what is *reported*, not what runs.

## Alternatives considered

- **Exit and let the service manager restart.** Three platforms, three
  behaviours, and aligning them means regenerating registrations ADR-0033
  §Decision forbids the agent performing. Rejected on the units' own text.
- **Settling orphans lazily (on read) instead of at start.** Every reader
  would carry the rule; the startup sweep runs once, under the writer
  role's guarantee, and leaves readers dumb.
- **Cancelled as the sweep's landing state.** Nobody cancelled anything;
  `FailedRecoverable` both says what happened and schedules the retry.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Written from the 2026-08 stuck-job field report and the owner's restart and diagnostics asks |
| 2026-08 | Built | The startup sweep and notice (`JobStateStore`, `ServiceRuntime`), the orphan-settling cancel and queue-aware delete guard (`ServiceCommandHandler`), the in-process recycle loop (`AgentHost`) with the Owner-gated `restart_service` verb (contract 1.21) surfaced on the console's Maintenance view and the CLI's `restart`, and the startup configuration record (events 3760–3763) — pinned by `Repository.Tests/ApplicationServiceTests`, `Hosts.Tests/JournalReconciliationTests`, `Hosts.Tests/RestartServiceTests`, `Hosts.Tests/AuthenticationGateTests`, `Hosts.Tests/AgentServiceLifetimeTests` (the shipped apphost recycling in place), `Hosts.Tests/AgentHostTests`, `Cli.Tests/SessionVerbTests` and `Web.Tests/ConsoleAdminScriptTests` |

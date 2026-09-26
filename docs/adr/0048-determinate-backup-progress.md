# ADR-0048 — Determinate backup progress: a counted plan, a replayed snapshot, and a stream that idles

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-SVC-006
**Related:** [ADR-0029](0029-pipeline-and-service-concurrency.md), [ADR-0047](0047-backup-pool-and-priorities.md), [ADR-0036](0036-local-web-console.md), [ADR-0027](0027-services-scheduling-status-telemetry.md)

---

## Context

The console's job meter was dishonest twice over. On the wire, `files_seen`
and `files_done` were the same number — the publication reported the
processed count for both — so the meter's ratio pinned at 100% for the whole
run (the page's arithmetic also added the reused count on top of the done
count it is a subset of). And there was nothing honest to divide by: the
scan and the archiving interleave by design (ADR-0029 §2), so the run never
knows, and never says, how much work it has in front of it. The owner's
direction: a backup first determines what it will process, progress then
reports files-to-back-up, files-completed, and a time estimate, on the jobs
page and the overview alike; delivery stays the push model, engineered so
that with no client connected the service does not keep producing events,
resuming when a client connects and stays connected.

Three background facts shaped the shape. The push chain already tore down
end to end — zero browsers means zero SSE requests, zero upstream watches
(one per browser, request-aborted all the way through), zero hub
subscribers — and `EventSource` redials on its own. But the producer paid
regardless: the publication re-walked its whole accumulated file list on
every scan event (O(n²) over a run) and took the hub's lock per file,
watched or not. And two lifecycle gaps: the transport's watch path never
received the per-connection disconnect detection the command path got
(2026-08 pump work), so a dead watcher was reaped only by the next event's
failed write — an idle service produces no next event — and the hub had no
replay, so a console attaching mid-run stared at "waiting for the first
progress event" until the next file completed.

## Decision

1. **A backup counts before it archives.** The tree publication walks the
   source once, before the hygiene sweep and the write intent, under the
   same compiled rules the capture judges by — so the plan and the capture
   cannot disagree about scope. Leaves only, no content read, the metadata
   matrix switched off, the pause gate honoured. The tally is reported
   while it runs (`Scanning`, files-seen growing), and its result — total
   files and total logical bytes — rides every later report. The cost is a
   second stat walk before the first archived byte; the meter being live
   during it is what keeps ADR-0029 §5's rule ("a pipeline that announces
   and then says nothing") satisfied.
2. **The plan is on the wire, additively** (contract 1.20). `JobProgress`
   gains nullable `total_files` and `total_bytes`: null until the count
   completes, and from producers that never count — the single-stream path,
   verification sweeps, pre-1.20 services. A client seeing null falls back
   to the indeterminate meter it always had. The estimate is derived
   client-side from a smoothed file-completion rate against the plan; file
   rate rather than byte rate on purpose, because `bytes_seen` counts
   archived content only and a byte rate would promise hours for a
   mostly-unchanged run that reuses its way to done in seconds.
3. **Progress production is bounded and coalesced.** The publication keeps
   incremental counters (fold-in of new files only — the O(n²) re-walk is
   gone) and emits at most one report per interval (64 files or 100 ms,
   whichever trips first), with state transitions always emitted so the
   numbers a transition carries are exact — the terminal report is precise
   even where per-file reports were skipped.
4. **The hub replays the latest snapshot per live job.** One event per
   unsettled job, handed into a new subscription before it goes live —
   never the missed sequence, and nothing for settled jobs, whose story is
   the journal's (10 §3.1). A console attaching mid-run renders instantly.
5. **A dead watcher is reaped on the hang-up.** The transport's watch path
   arms the same read-behind the command path uses: a watch is one-way
   after its opening frame, so a read resolving means the client went away,
   and that cancels the streaming enumeration — which removes the hub
   subscriber — immediately, not on the next failed write.
   And **the watch carries the client's session**: a watch takes its own
   connection, each connection gets its own ADR-0045 gate, and the session
   presented on the command exchange proved nothing to it — so on any
   installation with accounts, every watch was anonymous and the gate
   answered an empty stream; no progress ever reached a signed-in console.
   The relay suites missed it by faking the client below the transport;
   the live drill for this record found it. The watch frame now carries
   the session token (contract 1.20), the pump presents it to the
   connection's gate before streaming, and a refused or absent session
   still answers the anonymous empty stream it always did.
6. **The browser's stream follows the tab.** The console closes its
   `EventSource` when the tab hides and reopens it when the tab shows,
   matching the pollers that already pause on hidden — a backgrounded
   console holds no subscription nobody is reading. The overview joins the
   jobs page as a progress consumer: each set's card renders its live run's
   meter, percentage and estimate from the same feed.

Together, 3–6 are what "the service does not keep pushing with no client"
means here: with zero subscribers the hub fans out to nobody and only keeps
its per-job snapshot current, per-file work is O(1) amortised and coalesced,
and subscriptions cannot linger past their client — so a connecting client
restores the whole chain, replay included, and a disconnecting one releases
it at once.

## Consequences

**Positive** — an honest percentage from a fixed denominator; an estimate
with a defensible basis; the overview shows a live run without switching
views; mid-run attach renders immediately; a run's progress costs the same
whether zero or five clients watch, and near nothing when none do.

**Negative** — every backup stats its tree twice; on sources where opening
a file handle per regular file is the walk's cost, counting approaches the
scan's own cost and delays the first archived byte by that much. The plan
counts leaves, so a file that fails mid-capture makes handled exceed the
plan by the failure count — clients clamp at 100 rather than divide by a
corrected denominator.

**Neutral** — pre-1.20 clients see no new fields and keep today's
behaviour; the replay changes no durable state; the sequence stays
monotonic per hub, with replayed events carrying their original numbers.

## Alternatives considered

- **The previous snapshot's file count as an instant estimate.** One
  indexed count, zero walk — but approximate, absent on first and full
  runs, and the owner asked for a determined plan, not a guess.
- **An exact plan from the FR-SVC-009 dry rescan.** The comparer's
  judgement is right but its baseline map is O(file count) memory and it
  runs on the reader lane against a second connection; the counting walk
  needs none of that.
- **Polling instead of push.** The counters could ride the already-polled
  `list_jobs` answer — simpler, but 3-second granularity, and the service
  still could not tell nobody was asking. The push chain already existed
  and already tore down correctly; it only needed the producer and the
  reaping fixed.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Written from the owner's progress-visibility direction |
| 2026-08 | Built | The counting pass and coalesced incremental reporting (`PublicationOrchestrator`), the plan on the wire (`JobProgress`, contract 1.20), latest-snapshot replay (`ProgressHub`), watch-path disconnect reaping and the session-carrying watch (`ServiceConnectionPump`, `LocalServiceClient`), and the console's plan-divided meters, estimate, overview row and tab-following stream — pinned by `Repository.Tests/SnapshotPublicationTests`, `Hosts.Tests/ProgressHubTests`, `Hosts.Tests/WatchSessionTests`, `Api.Tests/AbandonedCommandTests` and `Web.Tests/ConsoleProgressScriptTests` |
| 2026-08 | Built (scenario sweep) | The subscription lifecycle pinned end to end at the owner's direction: bounded drop-oldest backpressure and replay/sequence coherence under concurrency (`Hosts.Tests/ProgressHubTests`), reconnect-with-replay, clean shutdown and the expired-session refusal over the real binding (`Hosts.Tests/WatchSessionTests`), the remote binding's watch driven over TCP+TLS for the first time (`Hosts.Tests/RemoteBindingTests`), the browser hang-up closing the upstream watch and two independent SSE streams (`Web.Tests/EventStreamTests`), and the 1.20 wire names and pre-1.20 defaults on the bytes (`Api.Tests/ContractAdditiveFieldsTests`) |

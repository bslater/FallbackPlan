# ADR-0050 — The completed-run record and its drill-down: a job that can say what it did

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-SVC-018, FR-SVC-006, FR-DEST-004
**Related:** [ADR-0027](0027-services-scheduling-status-telemetry.md), [ADR-0048](0048-determinate-backup-progress.md), [ADR-0035](0035-destination-fitness.md), [ADR-0043](0043-structured-logging-and-diagnostics.md), [ADR-0047](0047-backup-pool-and-priorities.md)

---

## Context

Two questions from the same 2026-08 field session, both of the form "the
service knows this — why can't I see it?".

First: *viewing a completed job, can we see what it did — a summary, and
where required the details?* The answer was no, structurally. A run's
terminal `JobProgress` — files done, reused and failed, bytes read and
stored, the counted plan — is dropped the moment the job settles: the hub
removes the row precisely so a finished job cannot replay as live
(ADR-0048), and nothing wrote the numbers anywhere. What survived was one
prose string, at its least informative exactly where numbers matter most —
a partial run's detail is "partial: 3 failure(s)" with no counts, and a
failed or cancelled run commits no snapshot, so its journal row is the only
witness there will ever be. Meanwhile the repository durably holds riches
nobody could reach: the snapshot manifest (capture window, parent link,
capture status), and the **error manifest** — every failed path with a
typed reason and the scanner's own words, written since the format existed
and exposed by no command at all.

Second: *when a backup shows as degraded we should be able to see why* —
reported with the confusing observation that a set read **Degraded**
minutes *after* two successful manual runs, then healed to in-sync
unaided. The mechanism: the moment a run commits, the set's last-completed
timestamp moves past each destination's own last-successful-sync stamp, so
every destination that has not yet received the new snapshot is honestly
demoted to behind (`StatusModel`), healing when the next sync pass records
success. The demotion is correct; the reporting was not. That path's
warning carried no reason — the ledger's `LastError` is nulled by every
success — so the console rendered "'local' is behind." full stop, and
neither operand of the comparison was on the wire. ADR-0027 §4's own
2026-08 clause — "the per-destination reason carried, not summarised away"
— was unmet on the most common degradation there is.

## Decision

1. **The terminal numbers persist on the journal row.** `JobRunStats` — an
   additive nested block on `JobRecord` — is written at every one of the
   runner's terminal transitions from the run's last progress report, the
   failed and cancelled ones included, where those numbers are the only
   surviving record. `Transition` treats it null-preserving, exactly like
   detail and snapshot id. The journal stays **sacrificial** on ADR-0027
   §2's terms: richer history, the same tolerance for loss — the stats are
   a convenience record, and everything a correctness decision rests on
   (the schedule anchor, resumability) is untouched. A pre-1.22 journal
   reads back whole with no stats, never as corrupt.

2. **The summary is a join, not a verb.** The console's job report joins
   the row's own numbers to its committed snapshot (capture status, file
   count, the per-destination vocabulary the snapshot listing already
   carries) client-side, with duration from the row's two timestamps. No
   `describe_job` command exists, deliberately: every fact in the summary
   was already in answers the page holds.

3. **The details live in the repository and are read on demand**, so they
   survive the sacrificial journal and cost nothing to store. Two read
   verbs on the reader lane (contract 1.22):
   - `job_changes` diffs the run's snapshot against the set's previous one,
     entirely from the catalogue. Equal recorded object ids are the exact
     "unchanged" — the same statement the snapshot browser's change badges
     make — and the predecessor is derived by the browser's own
     next-same-set-row rule, so the two surfaces agree by construction.
     The buckets are deliberately coarser than `preview_set_changes`' six,
     and each coarsening is a stated limit of after-the-fact comparison:
     no content/metadata-only split (needs manifest reads), no moved (file
     identity is a scan-time local fact, null in a rebuilt catalogue), no
     deleted/no-longer-included split (needs both runs' rules
     re-evaluated). A first backup answers "everything is new" with a null
     baseline.
   - `job_failures` reads the snapshot's error manifest back: each
     failure's path, typed reason (kebab vocabulary mirroring
     specification 06 §8.1) and the scanner's own detail. Paths flow to
     any authenticated caller — the `list_directory` precedent — and where
     recorded name bytes have no faithful decoding the rendering
     substitutes while the raw truth stays in the manifest.

4. **Counts exact, samples bounded — no cursors.** Both verbs answer the
   exact count with a first-encountered sample under a cap the result
   echoes (`preview_set_changes`' caps for the diff; 100 default / 1000
   ceiling for failures), because the frame codec caps a reply at 8 MiB
   and "send me everything" is not a question these verbs take. For the
   same reason `list_jobs` gains an optional `limit` (newest-N,
   oldest-first order preserved; null keeps the pre-1.22
   return-everything), since the journal now grows for the life of the
   installation with fatter rows.

5. **Every behind demotion states its cause.** `DestinationStatus.Describe`
   — the one place the demotions happen, so the one place they can be
   explained — fills `Detail` with a cause sentence wherever it used to
   demote silently, and stamps a machine cause (`SyncCause`:
   `catching-up`, `awaiting-seed`, `never-synced`, `reported` — the last
   meaning the ledger's own recorded words win). The deriver's existing
   warning template carries the words to every client, current and old,
   with no further change; contract 1.22 additionally puts the cause code
   on the destination row (`reason`) and the demotion's missing operand on
   the set row (`last_completed_at`), so the console can render "behind
   the backup that finished at …" from facts, not re-derivation
   (ADR-0028 §8 holds). The catch-up window is named as what it is — a
   self-healing transient the next sync pass clears unaided (ADR-0035
   §8's warning semantics), not a fault.

6. **The live feed names the file being processed.** `JobProgress` gains
   `current_file` (additive), stamped by the publication's coalesced
   emitter from the newest folded file — a sample of the walk, not a
   per-file ledger, so the unwatched-service cost rule of ADR-0048 holds.
   This re-draws ADR-0029 §5's counts-only stance honestly rather than
   violating it quietly: the stance predates the session-gated watch
   (contract 1.20), and the feed's audience is now exactly the
   authenticated callers `list_directory` already shows every path to.
   The telemetry allowlist (NFR-PRIV-002) is untouched.

7. **The error-manifest decoder is brought to its own specification.**
   Specification 06 §8.1 assigns failure reason 8 (name not representable)
   and the encoder writes it unchecked; the decoder rejected anything
   above 7, so the first snapshot to record such a refusal was unreadable
   at exactly the moment somebody asked what failed. The bound is now 8 —
   a conformance fix, no spec change.

## Consequences

- A completed, failed or cancelled run can finally answer "what did you
  do": captured/unchanged/failed counts, bytes read and newly stored,
  duration, the diff against its predecessor, and the named failures —
  from the console (clickable history rows), the CLI (`jobs`,
  `jobs <id> --changes --failures`), or any contract client.
- `jobs.json` rows are larger and there is still **no pruning**: the file
  is rewritten whole on every transition, so growth is a real, known cost
  deferred deliberately — the journal needs a retention story of its own,
  and bolting one onto this record was not it.
- The two drill-down verbs hold the reader lane while they run; the
  failure read loads blob footers (the restore-grade cost) — accepted, it
  is an on-demand operator ask, not a poll.
- A set with one lagging destination still reads Degraded between a
  commit and the next sync pass — by design (never-merge, arch 10 §1.1) —
  but now says why and that it heals unaided.

## Alternatives considered

- **A `describe_job` verb** answering the summary server-side. Rejected:
  every summary fact was already on surfaces the console holds; a verb
  would be a second way to disagree.
- **Persisting the failure list on the journal row.** Rejected: it is
  already durable in the repository, keyed by the row's snapshot id, and
  the journal is sacrificial — facts only the journal can keep (the
  counts) go there, facts the repository keeps are read back on demand.
- **Cursor pagination for the failure listing.** Rejected for counts plus
  bounded samples: `read_log` paginates because a log has no natural
  summary; a failure listing has one (the exact count), and the bounded
  sample answers the operator question. The cap and the arithmetic
  against the frame limit are stated on the result type.
- **Teaching the console to explain "behind" itself** from
  `last_success_at` and a fetched last-completed time. Rejected: the
  console renders what the service answered, never a state derived in the
  page (ADR-0028 §8); the service knows why it demoted and now says so.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Written from the 2026-08 field asks: the completed-job drill-down and the unexplained Degraded flip |
| 2026-08 | Built | The run record at every terminal site (`JobStateStore`, `BackupRunner`), the drill-down pair and bounded `list_jobs` (contract 1.22, `ServiceCommandHandler`), the decoder bound (`ErrorManifestCodec`), the carried behind-reason (`StatusModel`), the current-file feed (`SnapshotPublication`), the console's clickable history with its report and detail dialogs and the destination reason line, and the CLI `jobs` verb — pinned by `Application.Tests/JobRunRecordTests`, `Application.Tests/DestinationStatusTests`, `Repository.Tests/PartialBackupHonestyTests`, `Repository.Tests/SnapshotPublicationTests`, `Repository.Tests/ManifestCodecTests`, `Hosts.Tests/JobDrilldownTests`, `Api.Tests/ContractAdditiveFieldsTests`, `Web.Tests/ConsoleJobsScriptTests`, `Web.Tests/CommandRelayTests` and `Cli.Tests/JobsVerbTests` |

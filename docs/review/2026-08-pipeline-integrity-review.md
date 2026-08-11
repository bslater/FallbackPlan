# Pipeline integrity review — the code read back against the documents

**Subject:** the backup pipeline as built at `69874b2` — `Repository/SnapshotPublication`, `Repository/PublicationOrchestrator`, `Repository/ArchiveSession`, `Repository/ManifestBuilder`, `Repository.Packing/BlobWriter`, `Repository.Index/WriterSequence`, `Repository.Index/Journal`, `Storage.Local`, and the restore path — read against [architecture 02](../architecture/02-repository-format.md), [04](../architecture/04-concurrency-and-publication.md) and [08](../architecture/08-restore-and-recovery.md), [specifications 05](../../specifications/repository-format/05-blob.md), [07](../../specifications/repository-format/07-index.md) and [08](../../specifications/repository-format/08-journal.md), and ADRs [0016](../adr/0016-blob-identifier-formation.md), [0022](../adr/0022-standalone-metadata-records-and-index-identifiers.md) and [0029](../adr/0029-pipeline-and-service-concurrency.md)
**Purpose:** establish whether what is built keeps the four promises the documents make — immutability, atomicity, verified restorability, and survival of sudden stop — before the remote binding is built on it
**Outcome:** 1 critical, 2 high, 4 medium, 5 candidates checked and cleared. **Every graded finding was confirmed by a test that failed for the documented reason and is fixed in this pass, proven by that test going green with the full suite.** The publication order, the spool resume, the intent journal and the restore verification all held under adversarial reading; what failed was at the edges the previous reviews never reached, because they read documents against documents and this is the first pass to read the code.

---

## Why this pass exists

The [architecture review](2026-08-architecture-review.md) read the original proposal against itself. The [pressure test](2026-08-fix-pressure-test.md) read the fixes against each other. The [Duplicati review](2026-08-duplicati-learnings.md) read another engine's field history against this design. All three were document passes: the code either did not exist yet or was taken at its documentation's word.

This pass takes nothing at its word. Each candidate finding was re-derived from the source, then attacked three ways — is the invariant enforced somewhere else, does an existing test catch it, was it deliberately dispositioned — and only what survived is graded here. Twelve candidates went in; seven survived as findings, five are recorded under [Checked and cleared](#checked-and-cleared) so nobody re-finds them.

The evidence bar is the pressure test's own: a finding that cannot be written as a failing test is not in this document. Every graded finding below names the test that failed before its fix, quotes the failure, and carries a `Resolved` note naming what changed. The suite was built and run in this environment (SDK 10.0.110, Linux container): 963 tests green before the first change, and every new test was watched failing for its documented reason before the fix that turns it green.

Grades:

| Grade | Meaning |
|-------|---------|
| **Critical** | The pipeline can lose or misdescribe committed data: a restore can fail for a snapshot the pipeline reported published. |
| **High** | An invariant the documents promise is unenforced, and the damage needs only circumstances the design already expects — a crash, a failed source, a long-running service. |
| **Medium** | Correct today, but only by an unenforced circumstance, an unconsumed API, or a stamp nobody checks. |
| **Cleared** | Investigated and refuted, with the refutation recorded. |

---

## Critical

### IR-1 — The sequence file is not durable, and a regressed sequence publishes an unrestorable snapshot

**Under test:** [specification 08 §2](../../specifications/repository-format/08-journal.md) — one monotonic gapless sequence space per writer, feeding journal keys, delta sequences and blob counters; [specification 05 §5.1](../../specifications/repository-format/05-blob.md#51-blob-immutability--inv-blob-001) — "the only permitted re-put is byte-identical"; [architecture 04 §2](../architecture/04-concurrency-and-publication.md#2-writer-identity) — a duplicate or regressing sequence is classified as identity cloning; and `WriterSequence.AllocateNext`'s own contract:

> "The pending mark is durable **before** the number is returned — an allocation the disk never saw could not get its void delta."

`FileSequenceStateStore.Save` (`Repository.Index/WriterSequence`) wrote the sequence file with `File.WriteAllText` and a rename — atomic against a torn write, but never flushed. The new bytes sat in the page cache; a power loss after the rename regressed `next` to whatever the platter last held. The contrast is `Application/AtomicFile`, which flushes to disk before its rename and cites `FileSequenceStateStore` as its own precedent — the precedent supplied the rename and not the durability.

This is not the fsync Wave F removed. [Q20](../open-questions.md#closed) dispositioned the *spool checkpoint's* per-record fsync, where authentication of the spooled records replaces durability of the watermark, and 05 §6 requires no fsync. Nothing replaces durability here: the sequence file is the only record of which numbers have been consumed, and every downstream defence assumes it does not run backwards.

**Failure scenario** — mechanically, as the test drives it:

1. A backup completes. The sequence file's replacement never reaches the platter; power is lost.
2. On restart the writer re-allocates the numbers the lost run consumed. The write intent goes to the journal key the dead run used; every blob draws a counter the dead run used, so `BlobId.FromWriterCounter` and the keyed store key repeat exactly ([ADR-0016](../adr/0016-blob-identifier-formation.md); [specification 02 §4.3](../../specifications/repository-format/02-identifiers.md)).
3. The store is immutable under a live key, so each colliding put is answered `AlreadyExists` and the dead run's bytes survive — correct, and 05 §5.1's point. But `ArchiveSession.UploadAsync` treated any non-`PreconditionFailed` outcome as success, and `Storage.Local` never returns `PreconditionFailed`. The upload of a blob **whose salt and records differ from what the store holds** is reported durable.
4. Step 6 publishes an index delta describing the new records — offsets into a blob that was never uploaded. Step 7 publishes the snapshot. The pipeline reports success.
5. Restore resolves the delta, opens the dead run's blob under the new run's record table, and fails authentication. The snapshot is committed, signed, and unrestorable — and the source data may be gone by the time anyone reads it.

**Held by** `InterruptionTests/SequenceRollbackTests`, two tests. Before the fix, both failed with *"Expected exception type:\<System.IO.IOException\> but no exception was thrown"* — the regressed publication sailed through — and a diagnostic run recorded the end state plainly: *publish reported success; 3 snapshots in the store; restoring the third fails with `FormatViolation`.*

> **Resolved (2026-08).** Three layers, because the failure chain has three links.
> `FileSequenceStateStore.Save` now flushes to disk before the rename, making `AllocateNext`'s contract what the disk actually holds. The flush itself cannot be asserted by a unit test — that limit is stated here rather than papered over — so the behavioural guards below are what the suite holds.
> `JournalStore.PublishAsync` treats `AlreadyExists` on a freshly allocated journal key as what it is: on anything but a byte-identical re-put (a provider retry), the sequence state has regressed, and the publication refuses before it builds on an intent that was never written. Held by `Publication_TheSequenceFileRegressed_RefusesRatherThanRepublishingUnderUsedNumbers`.
> `ArchiveSession.UploadAsync` and `ManifestBuilder.SealAndUploadAsync` confirm any `AlreadyExists` against the store via `Repository.Packing/SealedBlobReadback` — stored length plus the footer locator's digest prefix, two cheap reads — and refuse the publication on a mismatch, while a byte-identical re-put still passes. This guard stands even after a future collector prunes retired journal records and frees the journal keys: held by `Publication_TheSequenceRegressedAndTheJournalWasPruned_RefusesAtTheBlobUpload`, which deletes the journal plane before the regressed run.
> In every refused case the previously committed snapshots restore byte-identically, and the refusal happens **while the source data still exists** — detection at write time, the same posture as [C3](2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard)'s remedy.

The alternative fix — CSPRNG-random blob identifiers, which [specification 02 §4](../../specifications/repository-format/02-identifiers.md) explicitly permits — would remove the blob-key collision but not the journal-key collision, and would trade a gapless accountable counter for randomness. Durability plus refusal keeps the accounting.

---

## High

### IR-2 — A publication that dies in its producer leaves upload workers running past disposal

**Under test:** [ADR-0029 §2](../adr/0029-pipeline-and-service-concurrency.md) and the G3 lesson recorded in the [phase-2 plan](../phase-2-execution-plan.md) — a dead worker must not park the producer; by symmetry, a dead producer must not orphan the workers.

`ArchiveSession.DisposeAsync` abandoned the open writer, zeroed the class key, and disposed the key derivers — without completing the upload channel or awaiting the workers. On the success path `FlushAsync` had already drained everything, so nothing showed. On the failure path — the source stream throws, the scanner refuses a path — disposal ran with workers parked on the channel or mid-put. In the long-lived Agent that is a leaked worker set per failed backup; a worker mid-upload raced the disposal of the very derivers it was using, and its failure had nothing left to observe it.

**Failure scenario:** a scheduled backup's source fails mid-file after one blob has sealed and its upload is in flight. The publication reports the source's failure and returns. The upload completes *after* the job has ended — work escaping the lifetime that owned it, against key material disposal is zeroing — or throws unobserved.

**Held by** `InterruptionTests/SessionDisposalTests.Publication_TheSourceFailsMidRun_LeavesNoUploadInFlightWhenItReturns`. Before the fix: *"Assert.AreEqual failed. Expected:\<0\>. Actual:\<1\>. an upload was still in flight when the publication returned."*

> **Resolved (2026-08).** `DisposeAsync` completes the channel and awaits every worker before touching anything they use, swallowing their secondary failures because the exception that ended the session is already propagating and what they uploaded is intent-covered either way (04 §5.1 row 4). The worker-death half was already right — G3 fixed it — and is untouched.

### IR-3 — A spool the seal has orphaned is invisible to resume and reclaimed by nothing

**Under test:** [specification 05 §6.3](../../specifications/repository-format/05-blob.md#6-the-spool) — the spool lifecycle; [architecture 04 §5.1](../architecture/04-concurrency-and-publication.md#51-interruption-at-each-step) row 3.

Sealing deletes the checkpoint sidecar — "a sealed blob is no longer resumable state" — but the spool file itself survives until the upload collects it. A kill in that window leaves `blob-*.spool` with no sidecar: `BlobWriter.TryResume` enumerates only `*.spool.checkpoint`, so no resume will ever see it, no store object references it, and nothing anywhere swept the spool directory. Metadata blobs widen the window: `ManifestBuilder` deliberately creates its writer without a checkpoint (see [Checked and cleared](#checked-and-cleared) for why that asymmetry is load-bearing), so a crash mid-metadata-blob strands a spool the same way. Every such file is disk the state directory never gets back.

**Failure scenario:** a kill between seal and upload; the next run resumes nothing (correct), re-archives (correct), and the orphan sits in the spool directory forever. Repeat per crash.

**Held by** `InterruptionTests/SpoolHygieneTests`. Before the fix: *"Assert.IsFalse failed. a spool no resume can reach must be reclaimed by the next run."*

> **Resolved (2026-08).** `BlobWriter.SweepUnresumable` deletes spools with no sidecar and sidecars with no spool, and both publication paths run it before creating any new spool. The sweep is safe precisely because the spool directory is single-owner (ADR-0028 §2's writer role, plus the scheduler's single writer lane) — the same fact `TryResume`'s first-checkpoint pick already depended on, now written at both sites. `Publication_AResumableSpool_IsNotSweptAwayFromTheResumeWalk` holds the other side: a spool **with** its sidecar survives the sweep and resumes.

---

## Medium

### IR-4 — A crash's void-delta obligations are republished by every later run of the process

**Under test:** [specification 07 §4](../../specifications/repository-format/07-index.md#4-sequence-gaps-and-void-deltas) — a number allocated and never accounted gets a void delta, singular.

`WriterSequence.RecoveredObligations` was a snapshot taken at construction and never consumed. The Agent holds one `WriterSequence` for the process lifetime, and each publication opens by discharging the recovered obligations — so one crash's leftovers were republished as fresh void deltas (new random delta id, no key collision, no error) by every publication the process ever made. `IndexLoader` tolerates the duplicates; the store keeps them, and nothing collects yet.

**Held by** `InterruptionTests/VoidObligationTests.Publication_TwoRunsInOneProcess_DischargeEachCrashObligationOnce`. Before: *"Expected:\<1\>. Actual:\<2\>."*

> **Resolved (2026-08).** An obligation leaves `RecoveredObligations` when it is accounted for — the property now reads through the pending set `MarkAccounted` maintains, under the same lock. Superseded in the base-hardening round ([Amendment 1](#amendment-1-2026-08--the-base-hardening-round)): the property is now `OutstandingObligations`, the live pending set itself, so a cancelled run's numbers are discharged by the next publication in the same process rather than by the next restart.

### IR-5 — The low-level restore computes the whole-file hash and compares it to nothing

**Under test:** [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) — restore verifies "the reconstructed file's length and whole-file hash"; the assembly check `ForensicRebuildTests.Restore_SegmentsAreAssembledWrongThoughEveryTagPasses_IsCaughtByTheWholeFileHash` already holds for `RestoreEngine`.

`RepositoryReader.RestoreAsync` verified contiguity and every segment's own identity, then *returned* the computed hash without comparing it — it took a reference list, not a manifest, so it had nothing to compare against, and nothing in its contract said the assembly check was the caller's job. `RestoreEngine` and `VerifyEngine` do compare; any other caller reaching for the reader got per-part verification and silently no assembly check. Two same-length segments swapped pass every per-part check and restore the wrong file as success.

**Held by** `Repository.Tests/EndToEnd/RestoreAssemblyTests.Restore_SegmentsSwappedButContiguous_IsRefusedByTheExpectedHash`. Driven against the old API first: *"Assert.IsFalse failed. segments assembled in the wrong order must not restore as success"* — the swapped list restored as success.

> **Resolved (2026-08).** An overload takes the expected whole-file hash and refuses a mismatch before a byte reaches the destination; the hash-less overload now says in its contract that it performs no assembly check and that callers holding a manifest should not use it. The swapped-but-plausible file is refused; the honest list still restores.

### IR-6 — Every snapshot asserts a zero-duration capture

**Under test:** [specification 00 §7](../../specifications/repository-format/00-conventions.md) — retention is the one wall-clock consumer and it reads recorded capture times; the snapshot manifest's `capture_started_at` / `capture_completed_at` pair exists to bound the capture window.

`SnapshotPublication` stamped both fields from `job.NowUnixMilliseconds` — a single value the caller took before the run began. A six-hour initial backup published a manifest asserting it started and finished in the same millisecond, six hours before it actually completed; retention then reasons over the wrong window. The engine deliberately takes no clock of its own, which is why the defect existed: there was nothing to stamp completion with.

**Held by** `Repository.Tests/EndToEnd/SnapshotPublicationTests.SnapshotManifest_AClockIsSupplied_StampsCaptureCompletionWhenItHappened`.

> **Resolved (2026-08).** `SnapshotJob` carries an optional clock; completion is stamped from it, and the Agent and CLI both supply one. Without a clock the old behaviour stands, stated in the property's contract rather than implied.

### IR-7 — `TargetedRecordReader` documents itself concurrent-safe, and its disposal is not

**Under test:** the type's own remarks — "everything here is therefore safe to call concurrently" — against a `Dispose` that tears down cached readers and zeroes blob keys with no coordination.

Correct today by scope ordering alone: the `using` in `SnapshotPublication` disposes after the session and builder have drained, so no read is in flight. Nothing enforced that, and the type's contract invited a future caller to get it wrong — a use-after-dispose here races zeroed key material.

> **Resolved (2026-08).** A disposed flag turns a late read into the `Unavailable` refusal every caller already handles, and the read path documents the ownership rule the ordering depends on. Stated honestly: the type is `internal`, so no deterministic test can drive the race from the suites; the guard is defence, the documented ownership is the contract, and the enforcement question rides with the retrospective item on spool-directory ownership.

---

## Checked and cleared

Candidates investigated and refuted, recorded so they are not re-found.

**The void-delta backfill runs before the write intent.** The first store write of a regressed run is uncovered by any intent — but [specification 08 §3.1](../../specifications/repository-format/08-journal.md#31-the-ordering-obligation)'s MUST binds *blobs*, and a void delta is an index-plane standalone record, not a blob. A crash mid-backfill leaves the obligation pending and the next run retries it. In spec, by design.

**Intent extensions cannot extend the wall-clock expiry.** Refuted. `IntentSurveyor` takes the maximum of the original and every extension's `declared_max_duration_ms`, measured from the original intent's issue time — an extension that revises the duration upward does extend expiry, exactly as [08 §4](../../specifications/repository-format/08-journal.md#4-intent-extension) says it MAY. Today's extensions all carry the job's original declaration, so the mechanism is unexercised, not broken. (The related-but-different capture-stamp defect is IR-6.)

**Step 5, "verify acknowledgements", is a no-op method.** Every put's acknowledgement is awaited where it happens, and on `Storage.Local` an acknowledgement follows an fsync — so the step's obligation is discharged inline and the observer callback is a seam, not a lie. What is genuinely not built is [architecture 04 §5](../architecture/04-concurrency-and-publication.md#5-publication-order)'s *optional* sample read-back of uploaded ranges, which becomes worth building when the first provider whose acknowledgements are less honest than a local fsync arrives. Carried as retrospective work, not a defect.

**`PutConditions.IfNotExists` is decorative on the local store.** True — `Storage.Local` answers `AlreadyExists` regardless of conditions and never `PreconditionFailed`, so the precondition guards were dead code on the only shipped provider. Folded into IR-1, whose read-back guard is the defence that does not depend on provider condition support; [05 §7](../../specifications/repository-format/05-blob.md#7-publication) already says conditional create is never load-bearing for correctness.

**The shared spool directory is safe only by the scheduler's single writer lane.** True, and deliberate: the writer role serialises processes and the Writer lane serialises jobs, so one session owns the directory at a time. What was missing was the statement — `BlobWriter.TryResume` and `SweepUnresumable` now carry it — and what remains missing is enforcement, which is a retrospective item rather than a code fix invented here. The `ManifestBuilder` half of the old candidate — metadata blobs get no checkpoint — turned out to be load-bearing rather than an oversight: a metadata checkpoint in the shared directory would be found by the data session's resume walk, whose pinned fields it can never match, forcing a restart that deletes it. The builder now says so where it creates the writer.

---

## Part 2 — What the suite proves, and what it now proves that it did not

The interruption suite was the strongest part of what this pass read: the 04 §5.1 matrix at five step boundaries, byte-identical spool resume with torn-tail and damaged-record restarts, corruption blast-radius classification, collector conservatism against unparseable intents, and restore oracles from cold readers through the printed recovery kit. Three specific oracles were weaker than what they guarded, and this pass strengthened them; the rest of the gap list is retrospective work with named owners in the [phase-2 plan](../phase-2-execution-plan.md#where-to-pick-up).

### IR-T1 — The covering-intent oracle accepted any journal record, not the covering one

`Upload_AtAnyConcurrency_WritesEachBlobsCoveringIntentBeforeItsPut` asserted that *some* journal put preceded each blob put — satisfiable with blob A riding on blob B's extension, which is exactly the interleaving a concurrent uploader could produce. It now decodes the journal and requires the extension **naming that blob id** to precede that blob's put, which is the literal 08 §3.1 obligation.

### IR-T2 — The tree rerun was counted, not verified

`PublishTree_KilledAtAnyStep_LeavesTheCommittedSnapshotIntactAndCompletesOnRerun` asserted the rerun published three files with no failures, then byte-verified only the single-stream baseline. "Three files, no failures" holds without the restored bytes being right. The rerun snapshot's every file is now restored through `RestoreEngine` and byte-compared — crash, rerun over the wreckage, restore, all three files.

### IR-T3 — No test rolled local state backwards

The suite deleted local state (catalogue, state directory) and proved recovery, but never *regressed* it — and a rolled-back sequence file is the one local-state failure that corrupts rather than merely costs. `SequenceRollbackTests` now holds both refusal layers (IR-1). The wider family — a catalogue behind the store, a catalogue ahead of the store, a stale dedup cache — remains open and is on the retrospective list.

### Owed, and deliberately not invented here

Each of these needs test infrastructure or a maintainer decision this pass does not own, and each is now a named item in the [phase-2 plan's pickup list](../phase-2-execution-plan.md#where-to-pick-up):

- **Cancellation of a live publication** — [T-2](2026-08-duplicati-learnings.md#t-2--graceful-stop-is-a-different-code-path-from-a-crash-and-it-is-the-one-that-corrupts)'s five tests, already owed before Phase 2 closes and reaffirmed here as the top test debt. This pass hardened the crash paths; cancellation is a different code path and stays unproven.
- **Torn and vanishing store writes.** Every kill in the suite is a clean exception with orderly unwinding (`InterruptionHarness` says so itself). The documented power-loss window in `Storage.Local` — contents fsynced, directory entry best-effort, so an acknowledged object can vanish — has no test, and simulating it needs a fault-injecting provider the suite does not have.
- **Kill points inside steps**, and the four step boundaries the matrix omits.
- **An interrupted restore** — no test kills a restore and inspects the destination.
- **Two processes, two writers** — everything cross-process is simulated by fresh in-process instances over shared disk; the writer-role lock and grant machinery has never been raced for real.
- **Contract-suite atomicity** — the portable `Storage.ContractTests` do not require atomic visibility or crash durability; those are local-provider tests only, so a second provider could pass the suite while offering neither. Tightening the contract is a design decision for phase 3, not a test to copy across.

---

## Dispositions

| Finding | Grade | Where the defect lived | Disposition |
|---------|-------|------------------------|-------------|
| IR-1 | Critical | `Repository.Index/WriterSequence`, `Repository.Index/Journal/JournalStore`, `Repository/ArchiveSession`, `Repository/ManifestBuilder` | **Fixed**: durable save + refusal guards at journal and blob puts; held by `SequenceRollbackTests` |
| IR-2 | High | `Repository/ArchiveSession.DisposeAsync` | **Fixed**: disposal drains its workers; held by `SessionDisposalTests` |
| IR-3 | High | `Repository.Packing/BlobWriter`, both publication paths | **Fixed**: `SweepUnresumable` at publication start; held by `SpoolHygieneTests` |
| IR-4 | Medium | `Repository.Index/WriterSequence.RecoveredObligations` | **Fixed**: obligations consumed on accounting; held by `VoidObligationTests` |
| IR-5 | Medium | `Repository/RepositoryReader.RestoreAsync` | **Fixed**: expected-hash overload refuses wrong assembly; held by `RestoreAssemblyTests` |
| IR-6 | Medium | `Repository/SnapshotPublication` capture stamps | **Fixed**: job-supplied clock stamps completion; held by `SnapshotPublicationTests` |
| IR-7 | Medium | `Repository/TargetedRecordReader.Dispose` | **Hardened + documented**; no deterministic test expressible against an internal type — enforcement rides with the spool-ownership retrospective item |
| IR-T1..T3 | — | Test oracles | **Strengthened in place** |
| Void backfill order · extension expiry · step-5 no-op · decorative `IfNotExists` · spool-directory sharing | Cleared | — | Recorded above; two retrospective items extracted (read-back sampling; ownership enforcement) |

**What this pass did not change:** no ADR needed amending — every defect was implementation against a correct decision — and no specification erratum was warranted: 05, 07 and 08 already said what the code now does. That is worth saying plainly, because it is the opposite of what the last two reviews found, and it means the document set is currently a sound implementation contract. *(Amendment 1 later found one exception in the sequence-accounting corner — ADR-0022 §Decision 7 now carries a practice note.)*

---

## Amendment 1 (2026-08) — the base-hardening round

The three items this review and its [restore-side companion](2026-08-restore-pipeline-review.md) left on the pickup list — the T-2 cancellation suite, kill-matrix row completeness with in-step kills, and real two-process races — are closed. Same method throughout: every claimed defect demonstrated by a test that failed for the documented reason before its fix.

### Sequence accounting: three defects in one corner

Verifying how the standalone snapshot's number was meant to be accounted (the caution this round opened with) surfaced a cluster, all against [ADR-0022 §Decision 7](../adr/0022-standalone-metadata-records-and-index-identifiers.md#decision-7--sequence-accounting-across-the-shared-space) and [ADR-0029 §4](../adr/0029-pipeline-and-service-concurrency.md):

- **Blob counters were never marked accounted**, though Decision 7 case 4 defines exactly when they are — the durable blob named by a durable intent. Every completed publication left all its counters pending, so the next process life published void deltas falsely claiming numbers that durable blobs embed were skipped. The uploader now marks the counter accounted when the put is acknowledged under a live intent scope, both blob classes.
- **The standalone snapshot consumed a number nothing could ever account.** `/snapshots/…` is not a sequence-addressed key, so the number satisfied none of Decision 7's four cases and surfaced as one junk void delta per publication, forever. The snapshot now rides under the write intent's sequence, hint-style — Decision 7's own arithmetic argument for hints, applied verbatim; key and nonce uniqueness never rested on the counter. The ADR carries the practice note.
- **In-process discharge (supersedes IR-4's fix).** `RecoveredObligations` filtered a construction-time snapshot, so a cancelled or failed run's numbers were discharged only by a restart — against ADR-0029 §4's "discharged by the next publication, exactly as a crash's would". `OutstandingObligations` is now the live pending set, read at publication start where the serialised writer lane guarantees no allocation is in flight.

Held by `InterruptionTests/SequenceAccountingTests`: a completed run leaves nothing pending and a rerun voids nothing; a run that loses a blob to a store fault has its number voided by the same process's next run.

### T-2: the five owed cancellation tests, and the defect they caught

All five exist verbatim-named and pass — four in `InterruptionTests/CancellationTests`, the `ServiceCommandHandler` positive path in `Hosts.Tests/ServiceTests`. The design position held: a cancel lands in the same 04 §5.1 state class as a kill at the same point, nothing durable collectable, every earlier snapshot untouched, and the rerun completes with nothing left pending.

One behaviour is **pinned deliberately**: a cancel drains the buffered and in-flight blob uploads through session disposal, writing to the store after the request. Those writes are intent-covered like every other upload and bounded by the queue; T-2's acceptance is state-class equivalence plus a clean rerun, not an instant stop, and rewiring the worker tokens would trade a bounded drain for a torn upload pipeline.

And T-2's thesis — the running engine's unwind is the path that corrupts — was vindicated precisely: a cancel landing inside `BlobWriter.SealAsync` left the writer marked sealed with the spool open, and `AbandonAsync`'s disposed-guard threw `ObjectDisposedException` **over the propagating cancellation** — so a cancelled job would report a failure instead of `Cancelled`, and `BackupRunner`'s cancellation handling would never run. Abandon and dispose are now tolerant of the interrupted-seal state: release handles, zero keys, leave the spool for the resume walk's authentication to judge, never throw over the exception the caller is propagating. Held deterministically by `BlobWriterAndReaderTests.BlobWriter_TheSealIsCancelledMidWrite_CanStillBeAbandoned`.

### The matrix, row-complete and put-by-put

Both `KillPoints()` sources now run all nine step boundaries; the rows whose durable state equals a neighbour's say so, and running them is what keeps the equivalence true if a step ever grows durable effects. [04 §5.1](../architecture/04-concurrency-and-publication.md#51-interruption-at-each-step) was reconciled to match — it had six rows for nine steps, called step 9's row "8", and described row 3 with an in-step state the boundary does not have.

In-step interruption is the put-budget sweep: `StorePutSweepTests` (eleven distinct puts, single-stream) and its twin in `TreeSnapshotInterruptionTests` (twelve, the scanner-driven path's first meeting with a fault store). The sweep's one casualty, fixed test-first: **a store fault during a source-identity hint put failed the whole publication**, contradicting the hints' own advisory bargain (06 §11). A hint the store will not take now costs a later reader one rename optimisation, never the snapshot. The tree path also gained its steps-in-order observer proof and the step-9 row the enum owns there: killed between retirement and Complete, the store is complete and restorable while the live catalogue never heard of the publication — the projection is a cache catching up by ordinary operation, never a correctness step.

### Two real processes

`Hosts.Tests/ProcessRaceTests` spawns the shipped apphosts and crosses the kernel's file lock — the path no in-process test reaches, because .NET's `FileShare` bookkeeping answers first. A real CLI contender is refused naming the holder's role and pid; a real Agent holder killed without ceremony frees the role by the operating system releasing its handle, stale owner file irrelevant; and two processes with different state directories back up one repository concurrently and both commit — ADR-0028 §4's boundary pinned from both sides. The phase-2 exit-criteria citation that credited the in-process tests with the cross-process claim is corrected.

### Dispositions

| Finding | Where the defect lived | Disposition |
|---------|------------------------|-------------|
| Blob counters never accounted | `Repository/ArchiveSession`, `Repository/ManifestBuilder`, `Domain/IBlobCounterAllocator` | **Fixed**: accounted at acknowledged put under a live intent; held by `SequenceAccountingTests` |
| Snapshot standalone's unaccountable number | Both publication paths, `ManifestBuilder.WriteStandaloneSnapshotAsync` | **Fixed**: rides the intent's sequence, hint-style; ADR-0022 §Decision 7 practice note |
| Obligations discharged only on restart | `Repository.Index/WriterSequence` | **Fixed**: live `OutstandingObligations`; supersedes IR-4's mechanism |
| Cancelled seal throws over the cancellation | `Repository.Packing/BlobWriter.AbandonAsync`/`DisposeAsync` | **Fixed**: unwind tolerant of the interrupted-seal state; held by `BlobWriterAndReaderTests` |
| Hint fault fails the publication | `Repository/ManifestBuilder.WriteSourceIdentityHintsAsync` | **Fixed**: advisory semantics applied to faults; held by `TreeSnapshotInterruptionTests` |
| Cancel drains in-flight uploads post-request | `Repository/ArchiveSession` | **Pinned as documented behaviour** — intent-covered, queue-bounded; `CancellationTests` |

---

## Amendment 2 (2026-08) — the stale-state round

The final proof-debt round: the stale-local-state family this review's own gap list named (IR-T3's "wider family"), plus restore breadth and RR-6's honesty half on the restore side. Same method; findings and dispositions below, tests in `StaleCatalogueTests`, `RestoreBreadthTests`, and the `RestorePlanTests` additions.

### A catalogue ahead of the store could publish dangling references

The sharp member of the family, found where the round's plan predicted: **own-writer dedup reuse was confirmed against nothing** — `DedupTrustGate` returned true for this writer's own rows with zero store I/O, on both the segment path and the manifest path, under the trust attribution the single-device default always takes. A location row that outlived its object (a GC race, a provider rollback, another participant's compaction) let a publication commit a manifest whose references dangle: committed cleanly, unrestorable forever, undetected until restore — the exact inverse of ADR-0006's write-time-detection purpose. The gate now runs a **memoized existence probe** — one `GetMetadata` per distinct blob per publication, deliberately not a read, so FR-DED-002's zero-verification-read acceptance still holds and every `DedupTrustDomainTests` pin is untouched. ADR-0006 carries the note. The alternative — accepting the risk because "a conforming store never loses objects behind a live intent or reference" — was rejected: the probe's cost is a metadata call per blob, and the failure it prevents is a silently unrestorable snapshot.

Companion decisions, each with its written reason:

- **The unchanged-file short-circuit stays unconfirmed, pinned as contained.** It re-emits the prior version with zero I/O — that is NFR-PERF-003's whole point — so confirming it would re-read every unchanged file. A stale row there surfaces as one file's clean restore failure, which is `verify`'s business to find earlier; `StaleCatalogueTests` pins the containment. The rename path already self-confirms (it must read the prior manifest to rewrite it) and degrades safely.
- **`PlanRestore.MissingObjects` asked the catalogue about itself** (`!catalogue.HasLocation` — a cache vouching for a cache) — fixed: the plan probes each located blob against the store, memoized per blob. What the plan still cannot see is a lost blob holding only the items' *segments* — the plan carries manifests, not their references — which is FR-RST-003 completeness territory and said so in the code.
- **A location row without a blob row stays a live candidate by design**: that is a rebuilt catalogue's ordinary state (store keys are derived, not looked up, for exactly this reason), and the existence probe is what defends it — not a join change that would refuse all reuse on every second device.
- **Behind-the-store is cost only**, and **`segment_dedup` is dead on the publication path** — both now pinned so neither can silently change class.

### Restore breadth, and the GET budget measured honestly

`ExistingDestinationPolicy.Replace` and `.Fail` had never been set by any test; both are pinned (`RestoreBreadthTests`), and the full receipt JSON is pinned byte-for-byte by a golden fixture. **NFR-PERF-009 (restore GETs ≤ 1.2 × distinct blobs) is architecturally unmet**: the read path opens every blob in the repository at load — three range reads each, proportional to repository size — then issues one uncoalesced range read per manifest and per segment. The characterization test pins the exact current counts so the budget cannot be mistaken for met and the eventual fix (catalogue-directed blob loading plus range coalescing — real read-path engine work, now a named pickup item) has a test waiting to become its compliance gate.

### RR-6's honesty half

Resolved on the restore review (its Amendment note carries the record): catalogue schema v5 carries `has_alternate_streams` through all three projection paths, the planner declares `alternate-streams`, and the receipt (schema v3) reports the item `degraded` with the aggregate `Partial` — the deferral had understated the receipt half, since nothing consumed `plan.Degradations` at all. Write-back remains owed, and the target capability stays `false` everywhere until it lands.

### Dispositions

| Finding | Where the defect lived | Disposition |
|---------|------------------------|-------------|
| Own-writer reuse unconfirmed against the store | `Repository/DedupTrustGate`, `Repository/TargetedRecordReader` | **Fixed**: memoized existence probe; held by `StaleCatalogueTests` |
| Plan-time MissingObjects never asked the store | `Agent/ServiceCommandHandler.PlanRestoreAsync` | **Fixed**: per-blob store probe; held by `ServiceTests` |
| Unchanged short-circuit over a lost blob | `Repository/SnapshotPublication` | **Pinned as contained** — one clean per-file restore failure; confirming would undo NFR-PERF-003 |
| Catalogue behind the store | — | **Pinned as cost-only**; `StaleCatalogueTests` |
| `segment_dedup` dead table | `Repository.Catalogue` | **Pinned as inert**, so it cannot silently become load-bearing |
| NFR-PERF-009 restore GET budget | `Repository/RepositoryReader`, `Repository.Packing/BlobReader` | **Characterized as unmet**; engine work named on the pickup list |
| RR-6 honesty half | catalogue projection → planner → receipt | **Fixed** (schema v5 / receipt v3); write-back still owed |

---

**See also:** [implementation status](../implementation-status.md) · [phase-2 plan — where to pick up](../phase-2-execution-plan.md#where-to-pick-up) · the prior passes: [architecture review](2026-08-architecture-review.md), [pressure test](2026-08-fix-pressure-test.md), [Duplicati learnings](2026-08-duplicati-learnings.md)

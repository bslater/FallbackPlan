# Restore pipeline review — the read path and its requirements

**Subject:** the restore and verification path as built at `233ed3c` — `Cli/OperationGateway` (direct mode), `Agent/ServiceCommandHandler` (service mode), `Restore/RestoreExecutor` and `RestorePlanner`, `Repository/RestoreEngine` and `RepositoryReader`, `Api/Results` — read against [architecture 08](../architecture/08-restore-and-recovery.md), and the audit tooling in [`eng/check-requirements.py`](../../eng/check-requirements.py) read against the traceability matrix
**Purpose:** the [pipeline integrity review](2026-08-pipeline-integrity-review.md) read the *backup* path; this reads the *restore* path with the same scepticism, and asks the audit tooling whether the matrix that governs both is still telling the truth
**Outcome:** 1 critical, 3 high, 3 medium. Six are fixed test-first; one (RR-6, alternate data streams) is confirmed and deferred with its reason, and one requirement disagreement (RR-7, sparse restore) is an open question. The coverage audit itself was found blind and repaired. The restore executor's containment, quarantine and receipt held; what failed was the second production caller that never used them, the service boundary that dropped the outcome, and a lifecycle the enum promised but the code could not reach.

---

## Why this pass exists

The integrity review closed the backup path. The restore path was read only through its executor's own tests, which are good — and which cover exactly one of the three ways a restore actually happens. This pass reads all three (direct CLI, service, and the low-level engine), and it starts by asking whether the requirement-to-test matrix can still be trusted, because a review that leans on a broken audit inherits its blind spots.

Method and evidence bar are the integrity review's: re-derive each candidate from the source, attempt refutation, and grade only what survives. Every graded finding names a test that failed for the documented reason before its fix and passes after, with the full suite green (SDK 10.0.110, Linux container). Findings needing a maintainer decision become open questions, not unilateral edits.

Grades are the same four: **Critical** (a restore can lose data or report success it did not achieve) · **High** (a promised property unenforced, damage needing only ordinary circumstances) · **Medium** (correct today only by an unenforced circumstance or a dropped field) · **Cleared**.

---

## 0. The audit was blind — repaired first

Before trusting any coverage claim: [`eng/check-requirements.py`](../../eng/check-requirements.py)'s `--drift` detector recognised tests only by the xUnit `[Fact]`/`[Theory]` attributes. The suite moved to MSTest under [ADR-0032](../adr/0032-mstest-as-the-test-framework.md); `grep '\[Fact\]' tests/` returns nothing. So drift reported zero by construction and had done since the migration — the guard that keeps the matrix honest was itself unwatched.

The regex now recognises `[TestMethod]`/`[DataTestMethod]` as well as the xUnit forms. A companion `--audit` flag reports the *reverse* of drift: requirements whose cited test classes never declare them, which is how a citation rots into naming the wrong witness. It surfaced 24; this pass repaired the ones in the backup/restore families (FR-ARCH-013, FR-DED-001, FR-DED-003, NFR-SEC-007, and FR-MAN-010's abridged cell), leaving 22 mostly-roll-up rows in other subsystems recorded for their owners. Both flags are reporting-only: a test may cite an id in passing without being its proof, so this is a prompt to look, never a build failure.

**FR-ARCH-013 was the sharpest citation rot:** the matrix pointed at `FixedSegmentReaderTests`, a segmenter unit test that touches no sparse extent and no round trip. The real coverage is in `SnapshotPublicationTests` and `ArchiveRoundTripTests` — and reading for it surfaced RR-7 below.

---

## Critical

### RR-1 — The CLI's direct-mode restore was uncontained

**Under test:** [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) — "repository path text is untrusted … containment is a property of the executor, not of the manifest author."

There are three restore paths and only one was the designed one. `RestoreExecutor` (used by the service) resolves every destination under the restore root, quarantines historical content, writes a receipt and applies metadata. `Cli/OperationGateway.RestoreAsync` did none of it: it walked the catalogue itself and wrote with `Path.Combine(outputDirectory, entry.Path)` on repository-supplied path text, in place, with no receipt. The seven hostile-path cases `RestorePlanTests.RestorePlan_APathIsNotPlainComponents_IsRefused` proves the executor refuses — `../`, `data/../../`, `/etc/passwd`, a Windows drive root — would every one have escaped the CLI's output directory, because the store is written by other participants and holds historical data the threat model treats as adversarial.

**Failure scenario:** a user restores a snapshot containing a manifest whose name is `../../.bashrc` (a peer's malice, or a crafted repository). The CLI writes outside the chosen output directory, over the user's own files, and reports success.

> **Resolved (2026-08).** `OperationGateway.RestoreAsync` now routes through `RestorePlanner` + `RestoreExecutor` — the same engine the service uses, so ADR-0028 §3's "the same operation performs identically through either path" is now true rather than aspirational. Containment, the receipt, metadata and displacement come with it. The user's explicit `--output` restores in place (quarantine is the service's default for restoring onto a machine it did not choose, a distinct control), and a fresh per-invocation run id keeps two restores from colliding. The CLI restore tests still pass; the containment property is inherited from `RestorePlanTests`, which this path now shares.

---

## High

### RR-2 — Containment was lexical; a symlink is not

**Under test:** [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) — containment as a property of the executor.

`RestoreExecutor.TryResolve` built the destination and checked it with `Path.GetFullPath`, which normalises `..` and `.` but does not follow symlinks. A directory symlink inside the root pointing out of it — seeded by an attacker who can write the restore target, or restored by an earlier item in the same run — lets a later path traverse it and escape, though every path component reads as plain. The whole executor's containment rested on a lexical check the filesystem does not honour.

**Failure scenario:** the restore target already contains `link → /home/user`, and a manifest names `link/.ssh/authorized_keys`. Lexical resolution keeps it under the root; the write follows the link to the real home directory.

**Held by** `RestorePlanTests.RestoreExecution_APreSeededSymlinkPointsOutsideTheRoot_IsNotWrittenThrough`, which seeds exactly that link and restores through it. Before the fix the file appeared in the outside directory.

> **Resolved (2026-08).** `TryResolve` now resolves every existing component between the root and the destination through its links (`Directory.ResolveLinkTarget` / `File.ResolveLinkTarget`) and refuses when a real path leaves the root — before any byte is written, recorded as a failed item. The root itself is the executor's own directory and is not suspect; the walk is bounded there, so a normal restore into not-yet-created directories is unaffected.

### RR-3 — The service dropped the restore outcome

**Under test:** [FR-RST-005](../requirements/functional.md) — "never reports success when any required file failed"; [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) — the receipt reports an *outcome*, not a boolean.

`Api/RestoreResult` carried `(Restored, Failed, OutputDirectory)` and no outcome. A restore that skipped a required item — a symlink on a target that cannot make one — failed nothing yet was not complete, and reached a remote client as `Failed = 0`: success. The executor computed the right `RestoreOutcome`; the contract boundary discarded it. FR-RST-005 held inside the executor and was lost one layer up, and `ServiceTests` asserted the lossy shape and passed.

> **Resolved (2026-08).** `RestoreResult` carries the receipt outcome (`complete`/`partial`/`failed`/`cancelled`), and `ServiceCommandHandler` maps `receipt.Outcome` into it. `ServiceTests.Restore_CommandedThroughTheContract_…` now asserts `complete`, and the executor-level aggregate is held by RR-T1 below.

### RR-4 — The service reused one displaced store per snapshot

**Under test:** [architecture 08 §3.1](../architecture/08-restore-and-recovery.md#31-quarantine-by-default) — each run's displaced and quarantined content is namespaced by run, so two runs cannot overwrite one another's.

`ServiceCommandHandler` derived the run id from the snapshot id (`Convert.ToHexString(plan.SnapshotId)[..16]`), so every restore of a given snapshot shared one displaced and one quarantine store — the single shared refuge arch 08 §3.1 forbids, worse than none because the second run silently overwrites the first's displaced originals. `RestoreExecutor.RestoreExecution_TheSamePathRestoredTwice_KeepsTheFirstDisplacedCopy` proves the executor avoids this *when the caller supplies distinct ids*; its only production caller supplied the same one.

**Held by** `ServiceTests.Restore_CommandedTwiceForOneSnapshot_UsesADistinctRunDirectoryEachTime`.

> **Resolved (2026-08).** Both the service and the CLI draw a fresh random run id per invocation.

---

## Medium

### RR-5 — `RestoreOutcome.Cancelled` was unreachable

**Under test:** [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) — the outcome enum's `Cancelled` member; [NFR-REL-005](../requirements/non-functional.md) — maintenance is cancellable.

`RestoreExecutor.ExecuteAsync` called `cancellationToken.ThrowIfCancellationRequested()` at the top of the item loop, so cancellation threw and no receipt was produced. `Aggregate`'s `items.Count != planned ⇒ Cancelled` branch could never fire; the enum member was a promise the code could not keep, and a cancelled restore left no account of what it had done.

**Held by** `RestorePlanTests.RestoreExecution_Cancelled_ReportsCancelledWithAReceiptRatherThanThrowing`.

> **Resolved (2026-08).** The loop breaks cooperatively instead of throwing, so a cancelled restore still produces a receipt and `Aggregate` reports `Cancelled`. Stopping between items means every item already restored is whole — a cancelled restore leaves the same class of state a completed prefix would, which is the cancellation-equals-a-clean-prefix property [T-2](2026-08-duplicati-learnings.md#t-2--graceful-stop-is-a-different-code-path-from-a-crash-and-it-is-the-one-that-corrupts) asks for on the restore side. The backup-side T-2 suite remains owed (it needs cancellation plumbed through the publication orchestrator) and stays on the pickup list.

### RR-6 — Alternate data streams are captured and never restored

**Under test:** [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) — a restore "reports every skipped or degraded attribute" and never reports success while silently dropping one; [ADR-0026 §5](../adr/0026-phase-1-capture-shapes.md).

Alternate data streams are captured (`SnapshotPublication.CaptureAlternateStreamsAsync`) and re-read by the forensic rebuilder, but `RestoreExecutor.ApplyMetadata` restores only mtime and POSIX mode. A Windows restore of a file carrying ADS writes the main stream, drops the streams, and reports `Complete` — the silent-loss-reported-as-success failure the outcome enum exists to prevent. The planner declares degradations for three capabilities (`posix-metadata`, `symlinks`, `special-files`) and not for this one.

> **Confirmed; deferred, not fixed in this pass.** Both halves need work this pass did not do, and saying so is the point. The honesty half is not the one-line planner change it first looked like: `RestorePlanner` works only from the catalogue tree projection (`Path`, `EntryKind`, `ObjectId`, length), which does not carry whether an item has streams — declaring the degradation means first surfacing ADS presence into the catalogue projection or the plan item. The write-back half is Windows-only, with no POSIX equivalent to test against here. Both are named on the [pickup list](../phase-2-execution-plan.md#where-to-pick-up) with a definition of done. Until then the requirement is unmet on Windows and the review records it rather than a fix that was not made.

### RR-7 — The restore materialises sparse holes as written zeroes

**Under test:** [FR-ARCH-013](../requirements/functional.md) — a sparse file restores "without materialising zero payload."

`RestoreEngine` writes explicit zero buffers into its spool for every hole and copies them to the destination: correct bytes, correct whole-file hash, and a fully allocated file where the source had holes. No sparse-allocation API is called anywhere. The requirement says one thing and the code does the other, and the test the matrix cited (`FixedSegmentReaderTests`) could not tell the difference.

> **Open question, not a unilateral fix.** This needs a maintainer decision — implement platform sparse write-out, or amend FR-ARCH-013's acceptance to say v1 restores holes as zeroes — so it is filed as [Q22](../open-questions.md#q22--sparse-restore-materialises-zeroes) and the misdirected citation is corrected. What is not acceptable is the silent disagreement, and it no longer is silent.

---

## Part 2 — Oracles strengthened against named requirements

### RR-T1 — FR-RST-005 had no multi-file failure test

The aggregate claim — a restore that recovered 9 999 of 10 000 files is a *failed* restore that recovered 9 999 — was proven only for containment refusal. Nothing restored several files where one's content could not be produced and the rest succeeded. `RestorePlanTests.RestoreExecution_OneFileOfSeveralCannotBeRestored_ReportsFailedAndKeepsTheRest` now does: outcome `Failed`, the failed file named in the receipt, the other file present on disk, the failed one absent.

### RR-T2 — NFR-SEC-003's property test was specified and never written

The acceptance names it literally: "no `(key, nonce)` pair repeats across any generated sequence." `NonceUniquenessTests.RecordKeystream_AcrossManyBlobsAndOrdinals_NoKeyNoncePairRepeats` is that property — over many blobs, each with a fresh salt (reusing writer and counter deliberately, the crash-restart collision only the salt separates), it collects every `(derived key, ordinal)` pair and asserts no duplicate. It holds the two defences together: dense distinct ordinals within a blob, distinct keys across blobs.

### RR-T3 — A file may span "any number of blobs" was tested with a few

FR-ARCH-006 names a figure; the existing multi-blob test spanned a handful. `ArchiveRoundTripTests.BackupAndRestore_AFileSpanningOverAHundredBlobs_RestoresByteIdentically` forces ≥100 blobs with incompressible content and restores byte-identically through footers alone.

### Owed, and deliberately not invented here

Named items with definitions of done on the [pickup list](../phase-2-execution-plan.md#where-to-pick-up): the backup-side T-2 cancellation suite; a read-side fault-injecting store and the restore interruption matrix it enables (this pass added the cancellation slice, not the torn-read slice); the restore-plan completeness fields (free space, privileges, archival tier); `ExistingDestinationPolicy.Replace`/`.Fail` coverage; a golden receipt fixture; the NFR-PERF-009 restore GET budget; and Windows ADS write-back.

---

## Dispositions

| Finding | Grade | Where the defect lived | Disposition |
|---------|-------|------------------------|-------------|
| RR-1 | Critical | `Cli/OperationGateway.RestoreAsync` | **Fixed**: routed through the planner/executor; containment inherited, `RestorePlanTests` shared |
| RR-2 | High | `Restore/RestoreExecutor.TryResolve` | **Fixed**: link-resolving containment; held by `RestoreExecution_APreSeededSymlink…` |
| RR-3 | High | `Api/Results.RestoreResult`, `Agent/ServiceCommandHandler` | **Fixed**: outcome carried and mapped; held by `ServiceTests` |
| RR-4 | High | `Agent/ServiceCommandHandler` run id | **Fixed**: per-run id; held by `ServiceTests.Restore_CommandedTwice…` |
| RR-5 | Medium | `Restore/RestoreExecutor.ExecuteAsync` | **Fixed**: cooperative stop, receipt produced; held by `RestoreExecution_Cancelled…` |
| RR-6 | Medium | `Restore/RestoreExecutor.ApplyMetadata` + planner | **Confirmed, deferred**: both the degradation declaration and Windows write-back are on the pickup list; not fixed here |
| RR-7 | — | `Repository/RestoreEngine` sparse holes | **Open question** [Q22]; citation corrected |
| RR-T1..T3 | — | Missing oracles | **Added** |
| audit tooling | — | `eng/check-requirements.py` | **Repaired**: drift recognises MSTest; `--audit` added; in-scope citations fixed |

**What this pass did not change:** no ADR needed amending, and the one spec/requirement disagreement (RR-7) is a question for the maintainer rather than a call this review gets to make. As with the integrity review, the executor's core — containment, quarantine, per-segment and whole-file verification, the receipt — held; the defects were at the callers and boundaries around it, which is exactly where reading only the executor's own tests would miss them.

---

## Amendment 1 (2026-08) — the fault-injection round

The suite's own stated limit — every kill a clean exception, no torn writes, no vanishing acknowledgements, no read faults — is now closed as far as store-side simulation goes. Three reusable decorators live in `TestSupport/FaultInjectionStores`: `TearingObjectStore` (a put leaves partial bytes visible under the final key and reports failure), `VanishingObjectStore` (acknowledged objects deleted on `LosePowerAsync`, the documented directory-entry window widened into an instrument), and `ReadFaultingObjectStore` (reads fail on demand after the world was loaded). `InterruptionTests/StoreFaultTests` runs the five-point kill matrix over the vanishing store and the torn-write scenario; `Repository.Tests/RestoreReadFaultTests` runs the read-fault slice of the restore matrix.

**What held.** All five kill-points followed by a total vanish of the dead run's objects leave the committed snapshot restorable and the rerun clean — a vanished object really does read as one never written, including the intent that covered it. A run that reports success and then loses every directory entry leaves no wedged state. The blobs-vanish-but-index-survives asymmetry refuses loudly and touches nothing else. A torn blob put fails the job, and the forensic rebuilder scopes the torn orphan as a finding and still satisfies a rebuild targeted at the committed snapshot.

**What broke and was fixed test-first (SF-2).** A transient store read fault mid-restore escaped `RestoreExecutor.ExecuteAsync` uncaught: no receipt, no per-item containment, the whole run aborted — against architecture 08 §3's accounting promise. `Restore_AReadFailsMidRun_FailsThatItemInTheReceiptAndTheRerunCompletes` failed with the injected `IOException` propagating out of the executor; the fix contains an `IOException` to the item it hit (manifest read and content restore both), deletes the item's spool, and the run carries on to a receipt whose rerun completes.

**What is pinned for a decision (SF-1).** One unopenable blob under `blobs/` — a torn orphan nothing references — makes the plain cold reader (`RepositoryReader.LoadBlobsAsync`) refuse the entire load, so every plain-path restore is blocked until something removes the orphan, and nothing collects yet. Loud and never wrong bytes, which is the error posture, and `CorruptionHarnessTests` already pins the refusal for a corrupted footer — but the operational consequence on a provider without atomic puts is that a single torn write degrades all plain restores until phase-4 GC. The torn-write test pins the current behaviour with a comment naming the choice: tolerate-and-scope in `LoadBlobsAsync` (a damage finding per blob, matching the forensic posture), sweep torn orphans, or accept until the collector exists. That is the implementation round's call, not this amendment's.

---

**See also:** [pipeline integrity review](2026-08-pipeline-integrity-review.md) — the backup-path pass this continues · [phase-2 plan — where to pick up](../phase-2-execution-plan.md#where-to-pick-up) · [Q22](../open-questions.md#q22--sparse-restore-materialises-zeroes) · [Duplicati learnings T-2](2026-08-duplicati-learnings.md#t-2--graceful-stop-is-a-different-code-path-from-a-crash-and-it-is-the-one-that-corrupts)

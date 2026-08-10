# What Duplicati's bug history says we should be testing

**Subject:** [duplicati/duplicati](https://github.com/duplicati/duplicati) at `a4780b3` — its 162-file unit suite, and its issue tracker (636 open, thousands closed)
**Purpose:** treat fifteen years of another backup engine's field failures as a test-design input, rather than rediscovering them
**Outcome:** 14 structural themes. **9 are already foreclosed by FallbackPlan's design and 7 of those are proven by a test.** 5 name real gaps, of which **one is serious enough to fix before Phase 2 closes**: nothing in the suite cancels a running publication.

---

## Why Duplicati is the right corpus

It is the closest thing to a control group this project has. Same problem — chunked, deduplicated, encrypted, incremental backup to a dumb remote store. Same language and runtime. Same solo-maintainer-plus-community shape. It has been in production since 2012 with a large installed base, which means its issue tracker is not a list of things that *could* go wrong; it is a list of things that *did*, to real people, with their data.

The point of reading it is not to copy its tests. Its design differs from ours in ways that matter — most of all in that Duplicati's local SQLite database is **authoritative** and the remote store is a projection of it, whereas in FallbackPlan the store is authoritative and the [catalogue is a disposable cache](../architecture/02-repository-format.md). A large fraction of Duplicati's worst bugs live exactly in that difference, and reading them is the cheapest available confirmation that the difference was worth paying for.

The other point is less comfortable. Where a theme applies to us *anyway*, Duplicati has usually paid to find out that it does, and we get to skip the tuition.

---

## Part 1 — What the shape of the suite teaches, before any individual test

Three things stand out from the file listing alone.

**Fifty of its ~162 test files are named `IssueNNNN.cs`.** Not `CompactTests.cs` with a case inside — a whole file per field defect, named after the bug report, containing the reproduction. `Issue1570`, `Issue4485`, `Issue5862`, `Issue6688`, `Issue6820`… That is roughly a third of the suite, and it is a third that exists because something shipped broken.

The lesson is not "we should name tests after issues" — we deliberately [keep requirement and issue identifiers out of test names](../adr/0032-mstest-as-the-test-framework.md). It is that **a mature backup engine's regression corpus is dominated by field defects, not by design-time cases**, and that ratio only ever moves in one direction. Our suite is currently 966 tests and ~0% field defects, because there is no field. The useful move now is to mine somebody else's field.

**Almost every one of those regressions is a state-machine defect, not an algorithm defect.** There is no `Issue*.cs` about a wrong hash, a broken cipher, or a mis-encoded integer. They are all about *sequence*: a thing was uploaded but not recorded, recorded but not uploaded, deleted from one place and not the other, interrupted between two writes that had to be atomic together. Duplicati's crypto and compression are fine. Its bookkeeping is where fifteen years of pain lives.

That is a direct argument for where the marginal test should go in FallbackPlan too, and it is worth noticing that our own suite is weighted the other way: 361 of 966 tests are in `FallbackPlan.Repository.Tests`, and a large share of those are codec, CBOR and crypto round-trips. Those are the tests that are easy to write and that almost never fail after the first day.

**The suite is stratified by *disruption*, not by layer.** `DisruptionTests`, `CompactDisruptionTests`, `SyntheticFilelistMetadataTests`, `RepairHandlerTests`, `RepairWithMissingRemoteFiles` — the top-level organising idea is "something went wrong partway; what is the state now, and can the user get out of it?" Our `FallbackPlan.InterruptionTests` project is the same instinct and it is the right one. It is also, at 44 tests, the smallest substantive project we have.

---

## Part 2 — The themes

Each theme names the mechanism, the evidence, where FallbackPlan stands, and what to do. Verdicts:

| Verdict | Meaning |
|---|---|
| **Foreclosed, proven** | The design makes the failure impossible, and a test says so. Nothing to do. |
| **Foreclosed, unproven** | The design makes it impossible, but no test would notice if that stopped being true. Add the test. |
| **Open** | The failure is available to us. Add the test, and possibly the fix. |
| **Deferred** | Applies to a phase we have not built. Belongs in that phase's exit criteria, stated concretely now. |

---

### T-1 — The upload succeeded and the bookkeeping did not

**Mechanism.** A volume is PUT to the destination; the process dies before the local database records it. Next run, the destination holds a file the database has never heard of, and the engine refuses to proceed: *"Found 1 remote files that are not recorded in local storage."*

**Evidence.** [#4485](https://github.com/duplicati/duplicati/issues/4485) is the canonical report and has a dedicated regression test. [#1570](https://github.com/duplicati/duplicati/issues/1570) is the same window costing a full re-upload on resume. `Issue5862` (`TestUploadFailureWithResumeAsync`), `Issue6820` (a stuck state with *two* `Temporary` dlist rows), and the whole `DisruptionTests` family are variations. The recovery is `repair`, which is itself a source of issues ([#4631](https://github.com/duplicati/duplicati/issues/4631) — repair deadlocks on the database it is repairing).

**Where we stand.** This is the exact failure the write-intent journal exists to prevent ([ADR-0022](../adr/0022-standalone-metadata-records-and-index-identifiers.md), [specification 08](../../specifications/repository-format/08-journal.md)): the intent covering a blob is durable at the destination *before* the blob's PUT, so a blob without a covering intent is a defect rather than a surprise, and an intent without its blob is ordinary. There is no local record that can disagree with the store, because the store is the record.

`Upload_AtAnyConcurrency_WritesEachBlobsCoveringIntentBeforeItsPut` asserts the ordering directly at 1 and 4 concurrent uploads. `Publish_KilledAtAnyStep_LeavesTheCommittedSnapshotReadableAndCompletesOnRerun` kills at each of five publication steps and requires a fresh process to finish the job.

**Verdict: Foreclosed, proven.** With one caveat, which is T-2.

---

### T-2 — Graceful stop is a different code path from a crash, and it is the one that corrupts

**Mechanism.** A crash is abrupt and therefore honest: nothing runs, no half-measures are taken, and recovery reads only what was durable. A *cancellation* is worse, because the engine is still running and still writing. It runs `finally` blocks, flushes what it has, marks things partially complete, and produces a state no crash could produce.

**Evidence.** This is the single most productive bug source in the tracker.

- [#4037](https://github.com/duplicati/duplicati/issues/4037) — *"Stop now results in 'Detected non-empty blocksets with no associated blocks!'"* — cancellation produces a database referring to content it never stored.
- [#3982](https://github.com/duplicati/duplicati/issues/3982) — *"'Stop after current file' has various problems with partial backups."*
- [#3644](https://github.com/duplicati/duplicati/issues/3644) — the flagship symptom, *"Unexpected difference in fileset version X: found Y entries, but expected Z."* The reporter's sequence is precise and damning: *stop after upload failed to halt, so they used stop now, and the job database was left inconsistent.* The only recoveries offered are deleting the backup set or rebuilding the database.
- [#6051](https://github.com/duplicati/duplicati/issues/6051), [#5131](https://github.com/duplicati/duplicati/issues/5131) — still open, same symptom.

Duplicati has `DisruptionTests.StopAfterCurrentFileAsync` and `StopNowAsync` because it learned this the hard way, and it still has open issues here.

**Where we stand.** `CancelJobCommand` exists, is routed through `ServiceCommandHandler.CancelJob`, and cancels the queued or running job. Cancellation tokens are threaded correctly — `ContractOperations_EveryMethod_IsAsynchronousAndCancellable` enforces that architecturally.

**The only test of it is `Cancel_WhenTheJobIsNotRunning_SaysSoRatherThanPretending`** — the not-found case. Nothing anywhere in 966 tests cancels a publication that is *actually running* and then inspects the repository.

Our interruption suite kills processes. It never cancels one. Those are different code paths, and Duplicati's history says the one we do not test is the one that breaks.

The design position is defensible — a snapshot object is only written at the end, so a cancelled publication should leave an unretired intent and some orphan blobs, which is the same state a crash leaves, and the next run should treat it identically. But *should* is doing the work in that sentence, and nothing checks it.

**Verdict: Open. This is the headline finding.**

**Tests to add** (`FallbackPlan.InterruptionTests`):

| Test | What it must show |
|---|---|
| `Publish_CancelledMidUpload_LeavesTheSameStateAsAKillAtThatPoint` | Cancel during blob upload; compare the resulting store listing and journal to the `PublicationStep.UploadBlobs` kill case. They must be the same *class* of state — unretired intent, no snapshot object, no orphan the collector cannot classify. |
| `Publish_CancelledMidUpload_LeavesEveryEarlierSnapshotRestorable` | The [T-1](#t-1--the-upload-succeeded-and-the-bookkeeping-did-not) invariant, but reached by cancellation. |
| `Publish_CancelledThenRerun_CompletesWithoutRepairOrOperatorAction` | The point of #3644: recovery must not require a repair verb. |
| `Publish_CancelledDuringTheSnapshotWrite_PublishesNoSnapshotOrACompleteOne` | The narrow window where a partial fileset is conceivable. |
| `CancelJob_JobIsRunning_StopsItAndReportsTheCancelledState` | The `ServiceCommandHandler` path end to end, which currently has no positive case at all. |

> **Resolved (2026-08).** All five exist verbatim-named and pass — four in `InterruptionTests/CancellationTests`, the service path in `Hosts.Tests/ServiceTests`. The design position held, with one pinned nuance: a cancel drains the in-flight upload queue through disposal, writing intent-covered blobs after the request, which is state-class-equivalent to a kill and bounded by the queue. And this finding's thesis was vindicated precisely — the suite caught a cancel landing inside `BlobWriter.SealAsync` throwing `ObjectDisposedException` over the cancellation during the unwind, so a cancelled job would have reported a failure instead of `Cancelled`. Fixed test-first; the rerun's obligation discharge is held by `SequenceAccountingTests`. Full record: [pipeline review, Amendment 1](2026-08-pipeline-integrity-review.md#amendment-1-2026-08--the-base-hardening-round).

---

### T-3 — Recovering the local database is the disaster path, and it is the one that is slowest and least tested

**Mechanism.** When the local database is lost or inconsistent, the engine rebuilds it from the destination. That path is exercised rarely, so it rots; it is also the path a user reaches *only* when already in trouble.

**Evidence.** [#4041](https://github.com/duplicati/duplicati/issues/4041), open since 2020, titled *"Database recreate desperately needs improvement"* — 930+ index files downloaded sequentially, 287 GB over ten days, and then it failed with `Missing block for blocklisthash`. [#2302](https://github.com/duplicati/duplicati/issues/2302), [#1391](https://github.com/duplicati/duplicati/issues/1391) are the same complaint years earlier. [#6205](https://github.com/duplicati/duplicati/issues/6205) is the sharpest: a *failed* recreate left a partial database on disk, which then blocked every subsequent operation — including retrying the recreate, and including starting fresh. [#6688](https://github.com/duplicati/duplicati/issues/6688) is worse still: a recreate that reports success and produces a database with invalid references.

**Where we stand.** Strong, and by design. `Catalogue_DeletedOutright_RebuildsFromTheIndexAndStillRestores` covers the ordinary rebuild; `ForensicRebuild_EveryIndexObjectDeleted_StillRestoresTheSnapshot` covers the case where the index plane is gone too; `ForensicRebuild_TargetedAtOneSnapshot_StopsBeforeScanningEveryDataBlob` is the direct answer to #4041's cost complaint, and it is asserted rather than asserted-about. `RecoveryDrill_AKitAndItsPassphraseAlone_RestoreEverythingWithNoLocalState` closes the loop.

Two gaps, both taught by #6205 and #6688 rather than by #4041:

1. **A rebuild that fails partway must not leave a catalogue that blocks the retry.** Our catalogue is disposable by design, which is exactly why nobody has checked that a half-written one gets discarded rather than opened.
2. **A rebuild must not report success on an incomplete result.** #6688's lesson is that "finished without throwing" and "correct" are different claims.

**Verdict: Foreclosed, unproven.**

**Tests to add** (`FallbackPlan.Repository.Tests/EndToEnd/CatalogueRebuildTests.cs`):

| Test | What it must show |
|---|---|
| `CatalogueRebuild_InterruptedPartway_LeavesNoCatalogueThatBlocksTheRetry` | Kill mid-rebuild; the next rebuild starts clean and succeeds. |
| `CatalogueRebuild_AnIndexDeltaIsUnreachable_ReportsAnIncompleteRebuildRatherThanSuccess` | Partial input must produce a stated partial result, not a quiet one. |
| `CatalogueRebuild_TheStoreIsEmpty_ReportsNothingToRebuildAndLeavesNoDatabase` | #6205's literal case. |

---

### T-4 — Compaction is where bytes actually get deleted

**Mechanism.** Reclaiming space means deleting objects the index says are dead. Every bug in that judgement is unrecoverable data loss, and every interruption leaves a partially-reclaimed store.

**Evidence.** A dense cluster. [#4129](https://github.com/duplicati/duplicati/issues/4129) — *"Error during compact forgot a dindex file deletion, getting Missing file error next run."* [#4693](https://github.com/duplicati/duplicati/issues/4693) — compact produced extra hashes. [#6254](https://github.com/duplicati/duplicati/issues/6254), [#6296](https://github.com/duplicati/duplicati/issues/6296), [#6504](https://github.com/duplicati/duplicati/issues/6504), [#6200](https://github.com/duplicati/duplicati/issues/6200) are all compaction-versus-index defects from the last year. [#5023](https://github.com/duplicati/duplicati/issues/5023) — recreate fails after *interrupted backup then compact*, a two-fault sequence.

The test file to note is `CompactDisruptionTests`, whose four cases are `InterruptedCompact`, `InterruptedCompactPlusNormalCompact`, **`DoubleInterruptedCompact`**, and `RestoreAfterDoubleInterruptedCompact`. Duplicati wrote a *double* interruption test because a single one was not enough to find the bug.

**Where we stand.** Not built — [Phase 4](../roadmap.md#phase-4--retention-pruning-and-healing). The existing exit criteria are already good and already name interruption at every GC step, concurrency with backup, and clock skew. `ConcurrentCollectionTests` proves the reachability discipline early (`GarbageCollection_RunsWhileABackupIsInFlight_DeletesNothing`, `GarbageCollection_AnIntentCannotBeParsed_ProtectsEverythingItMightCover`).

Three things Duplicati's history says those criteria are missing:

1. **Double interruption.** One kill, partial recovery, second kill, then recovery. Duplicati needed it.
2. **Restore *after* the double interruption**, not merely "snapshots preserved" — the reader is the thing that finds out.
3. **Byte-identity across compaction.** Compaction is a physical rearrangement; the strongest possible statement is that a restore of snapshot *S* before compaction and after compaction produce identical bytes. That is cheap to assert and impossible to fudge.

**Verdict: Deferred, with the criteria sharpened.**

**Tests to add** at Phase 4:

- `Compaction_InterruptedTwiceAndRecovered_StillRestoresEverySnapshotByteIdentically`
- `Compaction_RunsWhileARestoreIsReading_NeverRemovesAnObjectTheRestoreNeeds`
- `Compaction_Completed_LeavesEveryManifestUnmodified` (the roadmap already promises "compaction modifies no manifest"; this makes it a test)

---

### T-5 — A well-formed index that lies

**Mechanism.** Corruption tests usually flip bits, which produces *malformed* input. The harder case is input that is perfectly well-formed, passes every structural and cryptographic check, and is simply **wrong** — an index entry pointing at a location that holds a different, equally valid record.

**Evidence.** [#4988](https://github.com/duplicati/duplicati/issues/4988) (`TestManualDindexTamperAndRecreateAsync`), [#5066](https://github.com/duplicati/duplicati/issues/5066) and [#5845](https://github.com/duplicati/duplicati/issues/5845) (duplicated blocklists, duplicated blocks in an orphan index), [#6892](https://github.com/duplicati/duplicati/issues/6892) (six cases of structurally odd but parseable index files), [#6688](https://github.com/duplicati/duplicati/issues/6688) (a dindex referencing a dblock that is not there).

**Where we stand.** Partly. `ForensicRebuild_ADataBlobIsDeleted_SurfacesAMissingBlobFinding` covers the index naming something absent. `CorruptionHarnessTests` covers malformed bytes at four layers. `Restore_SegmentsAreAssembledWrongThoughEveryTagPasses_IsCaughtByTheWholeFileHash` is the closest thing we have to the theme and is the right idea — every record authenticated, whole-file hash still catches it.

What is missing is the *index-side* version of that test: an index delta that is canonical, correctly sealed, and points segment *S* at a real location holding a real record — the wrong one. Our defence is layered (record tag binds the record's identity; the manifest's whole-file hash binds the assembly) but the specific claim "a valid index cannot cause a silently wrong restore" is unasserted.

**Verdict: Foreclosed, unproven.**

**Tests to add** (`FallbackPlan.Repository.Tests/Index/`):

| Test | What it must show |
|---|---|
| `IndexDelta_PointsASegmentAtAnotherValidRecord_IsCaughtBeforeTheBytesAreUsed` | Which layer catches it, named explicitly. |
| `IndexDelta_ClaimsASegmentThatNoBlobHolds_SurfacesAMissingSegmentFindingNamingIt` | The #6688 shape at our layer. |
| `IndexDelta_ListsTheSameSegmentTwiceAtDifferentLocations_ResolvesDeterministicallyOrRefuses` | #5066/#5845; either answer is fine, silence is not. |

---

### T-6 — Two names for one thing, in one snapshot

**Mechanism.** A fileset containing the same path twice. Duplicati has a whole test file for it and a repair path for fixing filesets that already have it.

**Evidence.** `DuplicatePathTests` — four cases, including `TestRepairFixesDuplicatesAcrossMultipleFilesetsAsync`. [#6529](https://github.com/duplicati/duplicati/issues/6529) — `--changed-files`/`--deleted-files` producing a fileset mismatch, with three regression tests. [#4951](https://github.com/duplicati/duplicati/issues/4951) — `RenameCaseChangeUSNAsync`, a rename that changes only case.

**Where we stand.** Partly, and the interesting part is unproven. `RestorePlan_CaseCollisionsAndDegradations_AreSurfacedBeforeAnyByteMoves` handles collisions at *restore* — a snapshot from a case-sensitive source restored to a case-insensitive volume. `Scan_TwoNamesShareAnInode_ReportTheSameIdentityAndLinkCount` captures hardlink identity.

Two gaps sit on the *capture* side:

1. **The source-identity ambiguity rule is unproven.** `SnapshotPublication` drops both hint entries when two file versions in one snapshot share a source key — a hardlink group's two names. That rule is load-bearing for rename lineage and nothing tests it.
2. **A case-only rename** on a case-insensitive volume. `README.md` → `readme.md` is the same inode, the same size, the same mtime, and a *different* name — which means `IsContentUnchanged` returns true and the rename path fires. That is almost certainly correct behaviour, and nobody has checked.

**Verdict: Open (narrow).**

**Tests to add** (`FallbackPlan.Repository.Tests/EndToEnd/IncrementalBackupTests.cs`):

- `SourceIdentityHints_TwoNamesShareOneInode_RecordsNoAncestryForEither`
- `IncrementalBackup_TheFileWasRenamedByCaseAlone_KeepsItsAncestryUnderTheNewName`

---

### T-7 — Restoring somewhere unlike where it was captured

**Mechanism.** Path separators, reserved names, case rules, length limits and legal characters differ by platform. A snapshot is portable; a filesystem is not.

**Evidence.** [#6705](https://github.com/duplicati/duplicati/issues/6705) — the new restore flow crashed with an index-out-of-bounds on cross-OS restore, diagnosed by the maintainer as *"likely a `/` vs `\` issue"* — in 2026, in a fifteen-year-old product, with a dedicated `RestoreAcrossOperatingSystemsAsync` test now guarding it. `ProblematicPathTests` covers wildcards in directory names, long paths, and problematic suffixes.

**Where we stand.** Structurally better than Duplicati: names are captured as [bytes, not decoded strings](../architecture/06-filesystem-capture.md), paths are plain components with no separator in the format, and `RestorePlan_APathIsNotPlainComponents_IsRefused` enforces that. `RestorePlan_CaseCollisionsAndDegradations_AreSurfacedBeforeAnyByteMoves` is the right shape for the degradation report.

The gap is that no test *takes a snapshot captured under POSIX rules and plans a restore under Windows rules*. Every restore test plans on the platform that captured. The degradations that matter — a name containing `:` or `*`, a component named `CON`, a name ending in a space or period, a component over 255 UTF-16 units, two names differing only in case — are exactly the ones that only appear when the two platforms differ, and they must be surfaced in the plan **before any byte moves**, which is a promise our restore plan already makes for a different reason.

**Verdict: Foreclosed, unproven.**

**Tests to add** (`FallbackPlan.Repository.Tests/EndToEnd/RestorePlanTests.cs`), driven by a platform-rules parameter rather than by the running platform, so they run everywhere:

| Test | What it must show |
|---|---|
| `RestorePlan_PosixNamesTargetingWindowsRules_ReportsEveryUnrepresentableNameBeforeAnyByteMoves` | Reserved names, illegal characters, trailing space or period, and the length limit, each named. |
| `RestorePlan_NamesDifferingOnlyByCaseTargetingACaseInsensitiveVolume_ReportsTheCollisionRatherThanOverwriting` | Already partly covered; make the target rules explicit rather than ambient. |
| `RestorePlan_WindowsNamesTargetingPosixRules_PlansWithoutDegradation` | The other direction, which should be clean — and a test that says so is what stops someone "fixing" it into a problem. |

---

### T-8 — The degenerate cases: empty files, empty metadata, empty snapshots

**Mechanism.** A file with no content has no blocks, so every code path keyed on "look up its blocks" has a special case, and every special case is a place to get it wrong — most visibly when the database is rebuilt and there are no blocks to rebuild *from*.

**Evidence.** `EmptyFileTests` has four cases, three of which are about surviving a database recreate, including `OnlyEmptyFileAfterDatabaseRecreateAsync` — a backup consisting of nothing but empty files. `EmptyMetadataTests`, `RepairReplacesZeroLengthMetadataAsync`, and `PreventEmptySourceTest` are the same instinct.

**Where we stand.** `Backup_TheFileIsEmpty_ProducesNoSegmentsRecordsOrBlobs` covers capture. Nothing covers an empty file across a rebuild, and nothing covers a snapshot whose data plane is empty — a tree of empty files and directories, where the index has entries and no blob exists at all.

**Verdict: Open (cheap).**

**Tests to add:**

- `CatalogueRebuild_TheSnapshotHoldsOnlyEmptyFiles_RebuildsAndRestoresThemAsEmpty`
- `Backup_EveryFileIsEmpty_PublishesASnapshotWithNoDataBlobsThatStillRestores`

---

### T-9 — The source moves while you are reading it

**Mechanism.** A backup reads a filesystem that other software is writing. Between enumeration and read a file can be deleted, replaced, truncated, extended, or locked.

**Evidence.** `MissingSourceTest` (four cases, including "all sources missing" as distinct from "one source missing"), [#6909](https://github.com/duplicati/duplicati/issues/6909) (an excluded folder still probed for metadata, producing a permission warning), [#3536](https://github.com/duplicati/duplicati/issues/3536) (recursive folders via symlinks), `RestoreOtherProcessIsUsingFileAsync`.

**Where we stand.** The handle-relative walk forecloses the nastiest member of this family and it is proven: `Scan_NameIsRepointedAfterClassification_ReadsContentFromTheOpenHandle`. `Scan_ADirectoryIsUnreadable_RaisesAFailureEventAndKeepsScanning` and `AgentPass_AMissingRootAndABadSchedule_AreClassifiedRecoverableAndPermanent` cover the coarse cases.

What is untested is the file **changing during the read** — grown or truncated between the `stat` that sized it and the last byte read. Every backup engine has to decide what a torn read means, and ours has not written the decision down as a test. The right answer is almost certainly that the manifest records what was actually read and hashed, so the snapshot is self-consistent even if it matches no instant of the source; but that must be *asserted*, because the alternative — a manifest whose length field disagrees with its content hash — is the kind of thing that only surfaces at restore.

**Verdict: Open.**

**Tests to add** (`FallbackPlan.Filesystem.Tests` / `EndToEnd`):

- `Backup_TheFileGrowsWhileItIsBeingRead_PublishesASelfConsistentManifestForWhatItRead`
- `Backup_TheFileIsTruncatedWhileItIsBeingRead_PublishesASelfConsistentManifestOrAFailureEvent`
- `Backup_TheFileIsDeletedBetweenEnumerationAndRead_IsAFailureEventAndNotAnAbortedScan`

---

### T-10 — Trusting the clock

**Mechanism.** Incremental backup skips a file when its metadata says it has not changed. Everything downstream of that decision is silently wrong if the metadata lies, and metadata lies routinely: archive extraction, `rsync -t`, restores, clock corrections, and deliberate tampering all reset mtime.

**Evidence.** [#4312](https://github.com/duplicati/duplicati/issues/4312) — `ChangeTimestampShouldCreateExtraBackupAsync` plus `BackupFromRecreatedDatabaseShouldUpdateMetadataAsync`. `BorderTests.RunQuickTimestampsAsync`. `TimeZoneHelperTests` carries `CheckRepeatScheduleIsStableOverDSTForward` and `…Backward` — Duplicati has been bitten by daylight saving in its scheduler and now guards both directions.

**Where we stand.** Our short-circuit is stricter than Duplicati's — `IsContentUnchanged` requires mtime **and** logical length **and** device **and** inode to match, so a same-size same-mtime edit still has to defeat inode identity to slip through, and on most editors it does not (write-to-temp-and-rename changes the inode). But an in-place write that preserves size and restores mtime is defeated by nothing, and that is a real pattern.

This is a *documented accepted risk*, not a defect — every mtime-based engine has it, and the escape is a full re-read. What we lack is (a) a test that pins the behaviour so a future change to `IsContentUnchanged` cannot silently widen the hole, and (b) any assertion that a full re-read actually rescues it.

The scheduler side is also untested. `AgentPass_ABackupSetIsDue_RunsItOnceAndSkipsItOnTheNextPass` uses `TimeSpan.Zero` offsets throughout, so the DST transitions that produced `CheckRepeatScheduleIsStableOverDSTForward` have never been exercised here.

**Verdict: Open.**

**Tests to add:**

| Test | Where |
|---|---|
| `IncrementalBackup_ContentChangedInPlaceWithSizeAndTimestampPreserved_IsMissedByTheShortCircuitAndCaughtByAFullRead` | `IncrementalBackupTests` — names the accepted risk *and* the escape |
| `IncrementalBackup_TheTimestampMovesBackwards_StillTreatsTheFileAsChanged` | The clock-correction case; "unchanged" must mean equal, never "not newer" |
| `AgentPass_TheScheduleCrossesADaylightSavingTransition_RunsExactlyOnce` | `AgentPassTests`, both directions, per `CheckRepeatScheduleIsStableOverDST*` |

---

### T-11 — Deleting versions on purpose

**Mechanism.** Retention is a parser driving a destructive operation. A misparse deletes the wrong thing.

**Evidence.** `RetentionPolicyParsingTests`, [#6127](https://github.com/duplicati/duplicati/issues/6127) (multiple retention options interacting), [#5131](https://github.com/duplicati/duplicati/issues/5131) (versions that cannot be deleted at all), `DisruptionTests.KeepTimeRetentionAsync` / `KeepVersionsRetentionAsync` / `RetentionPolicyRetentionAsync`.

**Where we stand.** Phase 4. The roadmap already promises retention floors, mandatory dry-run reports and destructive-action auditing, which is the right posture — Duplicati's retention bugs are largely *silent* deletion.

**Verdict: Deferred.** One criterion to add, from #6127: *two retention rules that disagree must resolve to the more conservative outcome, and the dry-run must say which rule bound.*

---

### T-12 — Destinations that refuse to be written twice

**Mechanism.** The recommended ransomware defence is an immutable or object-locked bucket. That makes deletion conditional, delayed, or impossible — so compaction, retention and repair must all degrade rather than fail.

**Evidence.** `LockingDeleteAndCompactTests` (`DeleteSkipsLockedRemoteFilesetVolumeAsync`, `CompactDetectsAndAvoidsLockedCompactableVolumeAsync`) and `SoftDeleteTests` (eight cases, including a backend that cannot rename). This is recent work in Duplicati and it is a retrofit.

**Where we stand.** Phase 3/4, and better placed than Duplicati was: blob immutability is already a [format invariant](../../specifications/repository-format/) and [ADR-0022](../adr/0022-standalone-metadata-records-and-index-identifiers.md)'s intent discipline never rewrites an object. Our exposure is confined to the collector.

**Verdict: Deferred.** Criterion to add: *a destination that refuses deletes must leave the collector reporting unreclaimed space, never failing the run and never leaving a partially-applied reclamation.*

---

### T-13 — Diagnostics leak the thing being protected

**Mechanism.** Backup software's logs are made almost entirely of file paths, and file paths are user data.

**Evidence.** `SensitiveDataFilterTests` — twelve cases, six of which are *false-positive* guards (do not redact URLs, dates, division). [#6426](https://github.com/duplicati/duplicati/issues/6426) — known path warnings must not carry the exception, because the stack traces bloated logs.

**Where we stand.** `TelemetryPrivacyTests` covers this and the [threat model](../threat-model.md) names it.

There is a new surface, created this week. [C2](../adr/0031-exception-messages-are-resources.md) moved every exception message into resx with positional `{0}` holes, and many of those holes are filled with paths, names and identifiers. Redaction that was written against literal message text may not hold against `string.Format`ed resource text.

**Verdict: Foreclosed, unproven — and newly so.**

**Test to add:** `Telemetry_AnExceptionMessageFormattedFromAResource_RedactsTheUserPathsItInterpolated`.

---

### T-14 — The local database's own schema version

**Mechanism.** The cache has a schema; the schema changes; an old file meets new code.

**Evidence.** `DatabaseUpgraderTests` — five cases, including `UpgradeScriptsMatchSchemaVersion`, a consistency check between the migration scripts and the declared version.

**Where we stand.** Our catalogue is at `CatalogueSchema.Version = 4` and is disposable, so the correct behaviour is to discard and rebuild rather than migrate — which is strictly simpler and strictly safer than Duplicati's position. Nothing tests it. An older catalogue file that is *opened* rather than discarded would read wrong columns and answer wrongly, and the rest of the system trusts those answers.

**Verdict: Foreclosed, unproven.**

**Test to add:** `Catalogue_OpenedAtAnOlderSchemaVersion_IsDiscardedAndRebuiltRatherThanRead`.

---

## Part 3 — What to do, in order

**Before Phase 2 closes** — the cancellation gap is the one that would embarrass us:

1. T-2, all five tests. Cancellation of a live publication, and the `CancelJobCommand` positive path.
2. T-13, one test. New surface, created this week, trivial to check.
3. T-14, one test. Trivial, and the failure is silent.

**Next, cheap and structural:**

4. T-8, two tests — empty-file degenerate cases across a rebuild.
5. T-6, two tests — the source-identity ambiguity rule and the case-only rename.
6. T-3, three tests — rebuild interrupted, rebuild incomplete, rebuild from an empty store.
7. T-10, three tests — the mtime honesty boundary and DST.

**Needs a harness before it is worth doing:**

8. T-7, three tests — restore planning needs target platform rules as a parameter rather than as ambient truth. That is a small refactor and it pays for itself immediately.
9. T-9, three tests — needs a source that mutates mid-scan; `FakeFileSystemSource` is close.
10. T-5, three tests — needs an index-delta forger in test support.

**Phase-gated, recorded as exit criteria now:** T-4 (three), T-11 (one), T-12 (one).

That is **22 tests before Phase 4**, against a current 966. The suite grows about 2%; the part of it that tests *sequence rather than algorithm* grows by about half.

---

## Part 4 — What Duplicati does that we should not copy

Worth stating, so the absence is a decision rather than an oversight.

**A file per issue.** Their `IssueNNNN.cs` convention makes the corpus navigable by bug report and unnavigable by behaviour — `Issue5066.cs` tells a reader nothing, and two files can hold the same case under different numbers. [ADR-0032](../adr/0032-mstest-as-the-test-framework.md)'s naming rule already forbids identifiers in names; requirement and issue traceability lives in metadata. Where a Duplicati issue motivates one of our tests, the number belongs in a comment explaining *why the case exists*, which is exactly what those comments are for.

**`repair` as the recovery story.** A substantial share of Duplicati's issues end with "run repair", and a further share are *about* repair failing ([#4631](https://github.com/duplicati/duplicati/issues/4631), [#6205](https://github.com/duplicati/duplicati/issues/6205), [#6235](https://github.com/duplicati/duplicati/issues/6235), [#6296](https://github.com/duplicati/duplicati/issues/6296), [#6339](https://github.com/duplicati/duplicati/issues/6339)). A repair verb is what a system needs when its two sources of truth can disagree. Ours cannot, and adding a repair verb would be a signal that we had stopped believing that. Rebuild-from-store is not repair; it is re-derivation, and the distinction is the whole architecture.

**Testing through the CLI.** `CommandLineOperationsTests`, `GeneralBlackBoxTesting` and `SVNCheckoutsTest` drive the product as a subprocess against real archives. It buys realism at the cost of diagnosis — a failure tells you the backup did not round-trip, not which layer lost the bytes. Our [command-surface tests](../adr/0028-service-boundary-and-deployment-topologies.md) go through the contract instead, which is the same realism with a stack trace.

---

## Appendix — corpus

| | |
|---|---|
| Repository | `duplicati/duplicati` at `a4780b3`, shallow clone |
| Unit suite | `Duplicati/UnitTest` — 162 files, 44 701 lines, NUnit |
| Of which regression files | ~50 (`IssueNNNN.cs`), plus `IssueTests.cs` holding six more |
| Other suites | `Duplicati.Browser.Test` (Playwright), `LiveTests/Duplicati.Backend.Tests` (real backends), `Duplicati.CommandLine.BackendTester` |
| Issues read | 636 open; sampled by label (`backup corruption`, `core logic`, `local database issue`, `bug`), by comment count, and by symptom string |
| Issues cited | 30 |

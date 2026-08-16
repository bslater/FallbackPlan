# What Duplicati's release notes say we should be testing

**Subject:** the published release notes at [duplicati/duplicati/releases](https://github.com/duplicati/duplicati/releases) — the *shipped fixes*, as a corpus distinct from the issue tracker
**Purpose:** read what a comparable engine actually had to correct, and turn each class of correction into a test against FallbackPlan
**Outcome:** 11 classes of shipped fix. **Four turned out to be live defects here and are fixed in this arc**; four were already foreclosed but unproven and are now proven; three are foreclosed by architecture and stay that way.

---

## Why the release notes are a different corpus from the issue tracker

[The earlier pass](2026-08-duplicati-learnings.md) read Duplicati's unit suite and its issue tracker. This one reads its release notes, and they are not the same evidence.

An issue is a report: somebody thinks something is wrong. A release note is a **verdict**: a maintainer agreed, found the cause, wrote a fix, and shipped it. The tracker tells you what users notice; the notes tell you what was actually broken. The tracker is also survivorship-biased towards the dramatic — corruption, data loss, hangs — while the notes carry the long tail of small, boring, repeated corrections that never generate a thread but do generate a fix. That long tail is the interesting part, because it is where a class of defect reveals itself by *recurring*.

Three fixes for filename handling across three releases is a much stronger signal than one issue titled "backup corrupted", and it is a signal only this corpus carries.

**What was read.** The public release listing, **pages 1 through 12** — the whole 2.1.x canary and stable line, the 2.0.9.x and 2.0.6.x canaries, and back to 2.0.4.38 in December 2019. The first pass of this document read only pages 1, 2, 3, 4, 6 and 9; the second pass closed that gap, and doing so roughly doubled the class list. Everything below reflects the complete reading.

---

## The shape of the corpus, before any individual fix

**Almost nothing is an algorithm.** As with the unit suite, there is no release note fixing a hash, a cipher or a compressor. What ships is: parsing, locale, path handling, state bookkeeping, retry behaviour, and UI honesty. The engine's mathematics were right early; everything else took fifteen years.

**Address and URL parsing is its own recurring class.** "The URL parser returned different results for similar inputs." "A leading slash could be stripped from the path or a URL." "Scheme detection on short paths." "Better detection of valid S3 hostnames." Four separate releases, one subject. Taking an address apart is not a solved problem anybody inherits for free.

**Locale is a second recurring class.** A "locale-sensitive parsing bug for fr-CA", "invariant formatting causing crashes during backup", date handling, culture-dependent sorting. Six or more fixes across eighteen months, in every direction — sometimes a missing invariant, sometimes an invariant applied where a local one was wanted.

**Filenames are a third.** "Filenames containing a dollar sign" shipped *twice*, once in a canary and again in a later stable, alongside separate fixes for filter handling and path comparison.

Those three classes account for a large share of the small fixes, and none of them is exotic. They are what happens when text arrives from outside the program.

---

## The eleven classes, and where FallbackPlan stands

| Verdict | Meaning |
|---|---|
| **Was broken** | The class applied, FallbackPlan had the defect, it is fixed in this arc with a red-first proof |
| **Foreclosed, now proven** | The design already prevented it; a test now says so |
| **Foreclosed by architecture** | Structurally impossible here, and the reason is worth stating |

### D-1 — Address parsing disagrees with itself · **Was broken**

Duplicati's four separate URL-parser fixes name the mechanism: two code paths take an address apart, and they drift.

FallbackPlan had exactly that. `DestinationConfiguration.AddressDefect` and `PeerAddress.TryResolve` each split a peer endpoint on its last colon, so `fe80::1` parsed as host `fe80:` on port `1` — an address a person plainly meant as portless, called well formed by the admission check, reported as fine by status, and then dialled at a host that cannot exist. Checking an address before the first sync exists to catch precisely that.

Fixed in `34e511b`: one `PeerEndpoint.TryParse` in Application, called by both. Bracketed IPv6 accepted, unbracketed multi-colon refused rather than guessed at, port parsed with `NumberStyles.None` under the invariant culture. **Proof:** with the multi-colon refusal removed, all three rows of the portless-IPv6 test fail.

### D-2 — A filename escapes a filter · **Was broken**

The dollar-sign fixes, twice. An exclude list is a promise, and a filename is attacker-influenced input in any directory more than one person can write to.

Metacharacters as literals were already correct here — `Regex.Escape` per glob character holds — and that is now pinned rather than assumed. Two *engine defaults* were not, and both were exclusion bypasses. A newline is legal in a POSIX filename; .NET's `.` excludes it, so a trailing `/**` compiled to `.+` did not reach `secrets/ssh⏎key`. And `$` matches immediately before a final newline, so the rule `keep.txt` also matched the different file `keep.txt⏎`.

Fixed in `51b503a` with `RegexOptions.Singleline` and `\A`…`\z`. Both were conformance violations against spec 06 §7.1 as already written, which says `.` is "any character" and that rules are implicitly anchored over the whole path; §7.1 now states the engine options normatively, because a portable dialect has to pin how accepted constructs *execute*, not only which are accepted ([ADR-0024](../adr/0024-include-exclude-rule-dialect.md) Amendment 1).

The Python reference implementation had the `.` bug too and, using `fullmatch`, not the anchor bug — so the two implementations silently disagreed on `keep.txt⏎`. The dual derivation exists to catch that and missed it because no committed vector carried a newline. Twenty-two vectors now do.

### D-3 — A metadata-only change is discarded · **Was broken**

Duplicati v2.1.0.109: "updated timestamps discarded if no data was changed."

FallbackPlan's reuse short-circuit is keyed on identity, size and modification time, which answers *did the content change*. That is not *did anything about this file change*, and POSIX `chmod` is the counter-example: it moves ctime, not mtime. A file whose permissions were just tightened presented identical signals, the prior version was re-emitted whole, and the new mode, owner, group and extended attributes were discarded. The backup reported success; the loss appeared only at restore.

Fixed in `b2c018c`: catalogue v6 carries a metadata digest, the short-circuit requires content *and* digest agreement, and a metadata-only change takes the inherited-manifest path a rename already took — new version, real ancestry, no payload read.

**The interesting part is what the fix's first version cost.** Including access time in the digest turned an agent's second pass from "2 unchanged" into "0 unchanged", because reading a file moves its atime and the backup's own read is a read. Atime is captured and restored but is not evidence of change: it says something about the observer rather than about the file. That is now its own test.

### D-4 — Locale changes what the program does · **Foreclosed, now proven**

Six-plus locale fixes in eighteen months made this the class most worth checking, and FallbackPlan came out clean: the full suite passes unchanged under `tr-TR` and under `ar-SA`, with the culture verified to actually reach the test host rather than the run being vacuous. CA1304 and CA1311 are build errors solution-wide, so a culture-sensitive case change cannot compile.

Nothing was keeping that true, so `53a297f` closes two blind spots. A whole-suite sweep under one culture cannot catch the **asymmetric** defect — state written under one culture and read under another — because a writer and a reader wrong in the same direction agree with each other, and disagree only for somebody who changed their locale or carried a state directory between devices. `HostileCultureTests` crosses cultures deliberately for that. The opposite blind spot is the code nobody thought to write a culture test for, and a CI leg now runs the whole suite under `tr-TR`; every runner in the build matrix is an English dot-decimal machine, so the matrix proved nothing about locale before.

**Proof:** swapping the invariant parses in `Schedule` and `PeerEndpoint` for current-culture ones fails 3 of the 21 culture tests; removing `RegexOptions.CultureInvariant` from the rule compiler makes `INFO.LOG` stop matching `info.log` under `tr-TR`.

### D-5 — Retry hides a permanent failure · **Foreclosed, now proven**

Duplicati ships retry-behaviour fixes repeatedly, and the failure mode is always the same shape: a retry loop treats a permanent error as transient and burns the window, or treats a transient error as permanent and fails a backup that would have succeeded.

FallbackPlan distinguishes these at the type level rather than by attempt count — a fault is not an outage, which is the distinction ADR-0035 §2 rests on and which the admission probe reports as `Failed` versus `Unavailable`. Both paths gained tests in the C arc (`b6fff8b`), which is what moved this from foreclosed-and-unproven to proven.

### D-6 — Status claims more than it knows · **Foreclosed, now proven**

A recurring class in the notes is the UI reporting success, or a count, that the engine could not actually support.

This is the Y arc's whole subject and it is settled by construction: no verification stamp without proof (`0fd3653`), trim takes proof rather than claim (`962f7d6`), and the four-value vocabulary `proven` / `stale` / `unproven` / `unprovable (accepted)` (`c72eff9`). The Z arc added age as a status input, so a proof old enough to be meaningless says so.

### D-7 — Refusal reasons parsed from prose · **Foreclosed, now proven**

Not a Duplicati note as such, but the class it belongs to — a caller depending on the half of a response that may change freely. `f2fe90e` pins all 12 `PeerRefusalReason` codes and 10 `PeerMessageType` codes to spec 02 §7/§8. The finding that motivated it: renumbering the whole refusal enum passed all 114 existing Protocol tests, because no test anywhere compared a refusal code to a number.

### D-8 — Atomic replace loses the file · **Foreclosed, now proven**

Duplicati has shipped fixes for interrupted writes to its local database. FallbackPlan writes durable state through one `AtomicFile` primitive, and the C arc (`92d547f`) covered its failure and retry paths, taking it from 52.9% to 87.9%. The contention branch cannot be manufactured on POSIX — `rename` over an open file succeeds — so its *policy* is a tested predicate and the test says outright that the branch itself is unreachable here.

### D-9 — Two sources of truth diverge · **Foreclosed by architecture**

The single largest class in Duplicati's history, and the one that does not transfer. Its local SQLite database is authoritative and the remote store is a projection; a large share of its worst defects live in that gap, and `repair` exists to close it.

Here the store is authoritative and the catalogue is a disposable cache. There is nothing for the two to disagree *about*: a catalogue that is wrong is deleted and re-derived. This is worth restating each time the corpus is read, because it is the single most expensive architectural decision the project made and the notes are its ongoing justification.

### D-10 — Compaction deletes something still referenced · **Foreclosed by architecture**

Duplicati's compaction defects come from deciding reachability against the local database. FallbackPlan's retention decides against proven state — the Y3 rule that trim takes proof rather than claim — and refuses to proceed when it cannot establish it, naming the blocker (D2, `cab8bda`).

### D-11 — An upgrade cannot read what the last version wrote · **Foreclosed by architecture**

Format v1 is frozen behind conformance vectors, the catalogue is a cache that rebuilds on any schema mismatch (which is exactly why v6 in D-3 needed no migration), and the peer protocol negotiates features rather than assuming them (W arc). The failure mode Duplicati pays for at every database-version bump is structurally absent.

---

## What this pass cost and returned

Four live defects, three of them found by reading somebody else's changelog rather than by reading our own code. Two — the endpoint parse and the exclude bypass — were reachable from outside the program. One, the metadata discard, was a silent fidelity loss that only a restore would have revealed.

The general lesson is the one the corpus keeps repeating: **the defects were all in how text and state arrive from outside**, never in the mathematics. Every class here is a boundary — an address, a filename, a locale, a filesystem's own bookkeeping — and each was crossed by code that was locally reasonable and globally wrong.

The cheapest single thing this pass produced is not a fix. It is the observation in D-4 that a same-culture sweep cannot find an asymmetric bug, which generalises: **a test that puts both halves of a round trip in the same conditions cannot find a disagreement between them.** That applies to cultures, to platforms, to schema versions, and to the two ends of the peer protocol.

---

---

# Part 2 — the complete corpus, and the tests built against it

The first pass read six of twelve pages and turned four classes into fixes.
This part records the second pass: the remaining pages, the classes they
added, a test built for **every** class, and what each test found.

## What the missing pages added

Six new pages produced nine classes the first pass never saw, and three of
them are the kind that costs data rather than tidiness:

| Release | Fix, verbatim | Why it matters here |
|---|---|---|
| v2.1.0.119 | "Fixed a case where a purge could loose a version" | Deletion losing a version it should have kept |
| v2.1.0.123 | "Fixed an issue where some operations would report success even if failed" | The worst class: silent partial failure |
| v2.1.0.119 | "Fixed a deadlock on restore when transfers failed" | The failure path hangs instead of failing |
| v2.1.0.100 | "Fixed issue with DST changes causing schedule time-of-day to change" | Wall-clock schedules across a transition |
| v2.0.5.106 | "Fixed a case where backups could run immediately and ignore the scheduled time" | The same subject, opposite symptom |
| v2.0.6.103 | "Fixed issue where invalid timestamps would prevent files from being backed up" | A strange date costing the whole file |
| v2.1.0.116 | "Fixed an issue with queries that need more than 128 parameters" | A limit nobody remembered was there |
| v2.1.0.119 | "Fixed issue with empty filesets being created" | The zero end of the range |
| v2.0.5.108 | "Fixed a case where restoring files could fail if the containing folder was not restored" | Restore to a fresh machine |
| v2.0.9.109 | "Fixed an incorrect error masking another error in backups" | One failure hiding another |

## The results

Ten test classes were built, one per class of fix. Seven pass outright —
those are properties FallbackPlan already had and now proves. **Three
failed, and all three are real.**

| # | Class | Tests | Verdict |
|---|---|---|---|
| RN-1 | Schedule across DST and clock jumps | `Application.Tests/ScheduleClockBoundaryTests` (14) | **RN-F1 — defect** |
| RN-2 | Hostile filesystem timestamps | `Filesystem.Tests/HostileTimestampTests` (6) | **RN-F2 — defect** |
| RN-3/4 | Partial backup honesty, error masking | `Repository.Tests/PartialBackupHonestyTests` (5) | **RN-F3 — defect** |
| RN-5 | Retention losing a version | *(no new test — see below)* | Foreclosed |
| RN-6 | Queries past a parameter limit | `Repository.Tests/SnapshotScaleEdgeTests` (5) | Proven |
| RN-7 | Empty and degenerate snapshots | *(same class)* | Proven |
| RN-8 | Restore into a missing structure | `Repository.Tests/RestoreIntoMissingStructureTests` (4) | Proven |
| RN-9 | Endpoint rendering, name matching | `Application.Tests/DestinationIdentityTests` (7) | Proven |
| RN-10 | Deadlock on a failed transfer | *(covered — see below)* | Proven |

### RN-F1 — a daily schedule fires twice on the night the clock goes back

A `daily at 02:30` schedule fires **twice** on the fall-back night, once at
`02:30 +11:00` and again at `02:30 +10:00`. In New York the same defect
produces five firings over four days, including a spurious one at `01:00`.

The mechanism: `Schedule.IsDue` asks the cron for the previous occurrence
*in the offset of the clock it was handed*, then compares it against the
last completed run. When the offset changes, the same wall-clock occurrence
lands at a different absolute instant — one hour later than the run that
already discharged it — so it reads as a fresh occurrence.

Spring forward is fine: 02:30 on the night it does not exist still fires
once, and the week around it fires seven times. Interval schedules are
unaffected by construction, and the test pins their twelve-hour absolute
spacing across the same transition.

**Severity: medium.** It costs a duplicate backup, not data. It is listed
first because it is certain, cheap to fix, and happens twice a year on every
machine with a daily schedule.

### RN-F2 — a pre-epoch timestamp becomes a far-future one on Windows

The POSIX path guards it — `seconds < 0 ? null` — so a 1960 date is reported
as absent, which the test proves on Linux. The Windows path does not:

```csharp
ModifiedAtMs: (ulong)new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
```

An unchecked cast of a negative `long` wraps to roughly 1.8 × 10¹⁹. A
Windows `FILETIME` of zero reads as 1601, and files restored from old media
or old archives carry pre-1970 dates routinely, so this is reachable rather
than theoretical. The two platforms disagree about the same file.

**Severity: medium.** The file is still captured; its recorded date is
nonsense, it sorts after everything, and a restore sets it on disk.

### RN-F3 — a partial backup and a clean one report the same job state

`BackupRunner` maps a capture failure to `JobState.Complete` with
`detail: "partial: N failure(s)"`. There is no other terminal state to map
it to — the vocabulary is `Pending, Scanning, Reading, Segmenting, Packing,
Uploading, Publishing, Verifying, Complete, Paused, Retrying, Cancelled,
FailedRecoverable, FailedPermanent`, and none of them means "finished, but
not with everything".

So the only thing separating "backed up your 40 000 files" from "backed up
39 998 of your 40 000 files" is an English string. Spec 02 §8 already makes
this argument about wire refusals — the code is normative, the message is
explicitly not for parsing — and `WireCodeTests` enforces it there. The same
reasoning applies to a job state a console or a script reads.

The repository half is honest: every failure reaches the error manifest and
`capture_status` goes to 2. It is the operator-facing half that flattens.

**Severity: high.** This is the class Duplicati shipped three separate fixes
for, and the one whose consequence is discovered at restore.

### What was not built, and why

**RN-5 (retention losing a version)** — no new test. The Duplicati fix is
about a *purge* interacting with retention, and FallbackPlan has no purge
verb: retention is the only deletion path. Its planner is already covered by
`RetentionPlannerTests`, whose `MinGenerations_IsTheFloorTheOtherRulesCannotOverride`
is exactly the "must not lose a version" guard, and Y3 made trim require
proof rather than claim. Writing a test for an interaction between a feature
and a feature that does not exist would be theatre. **This becomes real work
the day a purge verb is added**, and is recorded as an exit criterion for it.

**RN-10 (deadlock on a failed transfer)** — no new test.
`RestoreReadFaultTests.Restore_AReadFailsMidRun_FailsThatItemInTheReceiptAndTheRerunCompletes`
already drives a restore through `ReadFaultingObjectStore` and terminates,
and terminating *is* the no-deadlock property — a deadlocked test hangs the
suite rather than failing it.

**A caveat worth stating.** Four of the five `PartialBackupHonestyTests`
express denial with `chmod` and are gated by `UnprivilegedPlatformCondition`,
because root reads a 000-mode file regardless. This container runs as root,
so they **skip here and are unverified**; they execute on CI's non-root
runners. RN-F3 itself does not depend on them: it is asserted against the
`JobState` vocabulary, which is decidable anywhere, and was confirmed failing
here with the `[Ignore]` removed.

## Remediation plan

Three findings, sequenced by severity-over-cost. Each is test-first and each
is **already red** — the acceptance criterion is deleting an `[Ignore]`.

### F3 — **fixed**: a partial capture has its own terminal state

`JobState.CompletedWithFailures`, mapped by `BackupRunner` from
`ErrorManifestObjectId is not null`, with the failure count kept in `Detail`
as the human half. `ContractVersion` 1.5 → 1.6.

**The interesting part was not adding the state; it was the three places that
enumerate terminal states and would each have failed silently.**

- `JobStateStore.LastCompleted` is the **schedule anchor**. Left filtering on
  `Complete` alone, a set whose backup was partial looks as though it never
  ran — so it backs up on every scheduler pass, for ever, on a set with one
  permanently unreadable file. It now rests on a published
  `IsCommitted` predicate, and its six call sites (three anchors, three
  "last backed up" timestamps) all get the right answer.
- `OperationGateway.HasSettled` lists the states at which a job stops moving.
  Left alone, `AwaitJobAsync` would poll a finished partial backup for ever.
- `OperationGateway.Describe` needed a case, or the state rendered through the
  `ToLowerInvariant` fallback as `completedwithfailures`.

**The exit code needed no edit at all**, which is the right outcome:
`OperationGateway` derives success from `State == JobState.Complete`, and the
new state is not `Complete`, so a partial backup exits non-zero by
construction. A comment now says so, because it is exactly the kind of line
somebody widens to `IsCommitted` while tidying.

**The journal tolerates unknown states.** `JobState` is stored by name, and
`JobStateStore.Open` answers a `JsonException` by setting the file aside as
corrupt and starting empty — so a downgraded build would have discarded the
journal and, with it, every set's anchor, making every set due at once. A
converter now reads an unrecognised name as `FailedRecoverable`: an unknown
terminal state read as "retry" costs one redundant run, whereas read as
"complete" it could anchor a schedule against something that never happened. A
journal that is not JSON is still sacrificial, and a test says so.

**Deliberately not done, and recorded rather than dropped:** mapping a partial
capture to `ProtectionState.Degraded`. NFR-OPS-002 does ask status to
distinguish `degraded`, but `StatusModel` derives it from *destination* inputs
and not from capture completeness, so this is a new status input — a feature,
not this fix. It is the natural follow-up and belongs with whoever next opens
`StatusModel`.

### F1 second — an occurrence's identity is its wall clock, not its instant

**The obvious fix is wrong, and that is worth writing down before somebody
tries it.** "Normalise both sides to UTC and compare" does nothing:
`DateTimeOffset` comparison is *already* absolute. The defect is not a
comparison in the wrong frame; it is that **one wall-clock occurrence has two
instants** on the fall-back night, and the second one looks like a new
occurrence the completed run predates.

Concretely, Sydney on 5 April 2026 with `daily at 02:30`:

| | wall clock | instant |
|---|---|---|
| the run that fired | 02:30 +11:00 | 15:30Z |
| the occurrence recomputed an hour later | 02:30 +10:00 | 16:30Z |

`15:30Z < 16:30Z`, so it fires again. Both are "02:30 on 5 April".

So the identity of a daily occurrence must be its **wall clock**, and the
comparison must be `lastCompleted.DateTime < occurrence.DateTime` — 02:30
against 02:30, which is not less than, so it does not fire.

**That change alone is not enough, and this is the part that makes F1 bigger
than it looks.** `Scheduler` builds the anchor with
`DateTimeOffset.FromUnixTimeMilliseconds(...)`, which carries offset `+00:00`,
so `lastCompleted.DateTime` is a UTC wall clock while `occurrence.DateTime` is
a local one. Comparing them would be worse than the bug. The fix therefore has
two halves:

1. **Make the contract explicit.** `IsDue` and `NextRun` require both
   arguments in the operator's wall-clock frame. Say so in the doc comment,
   because nothing in the signature enforces it.
2. **Honour it at all three call sites** — `Scheduler`, `CliApplication`,
   `ServiceCommandHandler` — by converting the stored Unix-millisecond anchor
   into the local frame before passing it. `.ToLocalTime()` is right for the
   agent; the tests supply the frame explicitly, via tzdata, because the
   container's zone is UTC and would prove nothing.
3. Then the daily branch compares wall clocks, and the interval branch is left
   alone — an interval is genuinely absolute and its test pins the twelve-hour
   spacing across the same transition.
4. Delete both `[Ignore]`s in `ScheduleClockBoundaryTests`.

*Care, restated because it is the trap:* the fix must not trade the double
firing for a skipped spring-forward day. Both directions are already pinned —
`…OnTheNightAnHourDoesNotExist_StillFiresThatDay` and
`…TheWeekAroundASpringForward_FiresEveryDay` pass today and must still pass.

### F2 third — guard the Windows conversion the way POSIX already does

1. In `TryStatWindows`, replace each unchecked cast with the same
   negative-guard the POSIX paths use, returning `null` for an
   unrepresentable instant.
2. Consider hoisting the guard into one shared helper so the three call
   sites — modified, created, accessed — cannot drift again.
3. Delete the `[Ignore]` on
   `OnWindows_APreEpochModificationTime_IsAlsoReportedAsAbsent`.

*Care:* cannot be verified in this container. The gated test carries the
proof and runs on CI's Windows leg.

### Not in this plan

Nothing found here justifies changing the format, the wire protocol, or the
catalogue schema. All three findings are in code that reports or schedules,
not in code that stores.

## Appendix — corpus

| | |
|---|---|
| Source | Public release listing at `github.com/duplicati/duplicati/releases` |
| Pages read | 1–12, complete: the 2.1.x line, the 2.0.9.x and 2.0.6.x canaries, back to v2.0.4.38 (December 2019) |
| Method | HTML pages; the GitHub API and `gh` were unavailable to this session, whose repository scope is `bslater/fallbackplan` |
| Not read | Releases before v2.0.4.38. Recurrence counts are lower bounds for that reason alone |
| Recurring classes counted | address and URL parsing ≥ 8 releases; locale and encoding ≥ 6; filename, filter and path handling ≥ 7; index/blocklist completeness ≥ 6; retry and timeout behaviour ≥ 5 |
| Named notes cited | 21, each quoted verbatim where it is used |

**Commits:** `34e511b` (D-1), `51b503a` (D-2), `b2c018c` (D-3), `53a297f`
(D-4) fixed the first pass's findings. This pass adds the ten test classes
above and records RN-F1, RN-F2 and RN-F3 as open.

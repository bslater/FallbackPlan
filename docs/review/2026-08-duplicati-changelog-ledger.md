# Duplicati's complete changelog, dispositioned

**Subject:** every fix entry in `duplicati/duplicati`'s `changelog.txt`, November 2015 → August 2026
**Purpose:** replace a themed essay with a countable ledger, so "have we covered this corpus" has an answer rather than an impression
**Outcome:** 4,742 bullets → 2,502 distinct → **805 distinct fix entries**. 183 dispositioned by rule, 622 clustered and read, **34 engine mechanisms** extracted and reasoned individually: 21 proven, 10 open, and a 29-entry compaction cluster deferred to phase 4.

---

## Why this supersedes the earlier reading

[The release-notes review](2026-08-duplicati-release-notes.md) claimed to have
read "fifteen years of release notes". It had read **12 of ~28 paginated HTML
pages**, through a summarising model, and cited **21 notes**. Against the 805
fix entries that actually exist, that is **2.6%**.

Two things were wrong beyond the page count:

- **The method lost fidelity.** Scraping the release *list* view gives
  abbreviated bullets. The repository's `changelog.txt` gives the full entry
  including the reasoning — "previously only the Windows-style `%VAR%` syntax
  was expanded, so the native forms were silently left unexpanded on
  non-Windows systems" — and the reasoning is the part test design needs.
- **The output was not countable.** Eleven themes cannot be reconciled against
  a corpus. A row per entry can.

That earlier document's findings stand; its claim to completeness does not.

## The corpus

`changelog.txt` is rotated periodically, so no single revision holds the whole
history. Four revisions, fetched verbatim, cover it with deliberate overlap:

| Revision | Range in file | Bytes | Bullets |
|---|---|---:|---:|
| `master` | 2023-05-25 → 2026-08-14 | 173,719 | 1,612 |
| `v2.1.0.5-2.1.0.5_stable_2025-03-04` | 2017-01-08 → 2025-03-04 | 118,902 | 1,384 |
| `v2.0.8.1-2.0.8.1_beta_2024-05-07` | 2017-01-08 → 2024-05-07 | 83,052 | 1,023 |
| `v2.0.4.5-2.0.4.5_beta_2018-11-28` | 2015-11-18 → 2018-11-28 | 52,279 | 723 |

Union: **2015-11-18 → 2026-08-14, no gap.** The overlaps are the cross-check
that rotation dropped nothing.

```
curl -sS https://raw.githubusercontent.com/duplicati/duplicati/<tag>/changelog.txt
```

## Counts, and how to reproduce them

| Measure | Count |
|---|---:|
| Bullets across all four revisions | 4,742 |
| Distinct after normalising (code spans, issue numbers, contributor credits) | 2,502 |
| Distinct entries beginning *Fixed / Fix / Corrected / Prevent / Resolved* | **805** |
| Earliest bulleted entry | 2016-10-27 |
| Latest | 2026-08-14 |

Releases before 2016-10-27 are prose-only and carry no bullets; their text was
read rather than parsed.

Reproduction: `extract.py` parses release blocks by the `YYYY-MM-DD - version`
header underlined with `====`, collects `- ` bullets, and keeps the **earliest**
release stating each normalised text, because later restatements are summary
sections rather than new fixes.

## Disposition of all 805

Two stages, and they are dispositioned to different depths. That difference is
stated rather than averaged away.

**Stage 1 — 183 dispositioned by rule.** A fix is auto-marked *N/A — surface
absent* only when neither its surface nor its mechanism has an analogue:

| Rule | Count | What it matches |
|---|---:|---|
| Web UI | 84 | ngax, ngclient, TrayIcon, dialogs, themes, Kestrel, translations |
| Storage backend | 68 | S3, Azure, B2, FTP, WebDAV, Dropbox, rclone, pCloud, and ~25 more |
| Runtime and packaging | 17 | Mono, .NET 4/8, Docker, Synology, installers, the updater |
| Notification channels | 14 | usage reporter, email, HTTP report, run-script |

The rule is conservative in one direction on purpose: a backend fix whose
*mechanism* generalises — escaping, URL parsing, timeouts, quota — is pushed to
stage 2 rather than dismissed, because an absent surface does not make a class
absent. That is why "FTP backend not unescaping special characters" is not in
the 68.

**Stage 2 — 622 clustered by mechanism and read.** Cluster sizes, which is the
real map of where Duplicati bled:

| Cluster | n | | Cluster | n |
|---|---:|---|---|---:|
| unclustered | 179 | | encoding / URL | 32 |
| filters, paths, names | 54 | | compact / index completeness | 29 |
| options and config | 54 | | local database | 27 |
| reporting and logging | 49 | | concurrency and interrupts | 23 |
| restore | 43 | | transfer, retry, quota | 23 |
| locale and time | 31 | | verify and test | 16 |
| compression and encryption | 15 | | retention and delete | 14 |
| repair / two truths | 13 | | metadata and permissions | 12 |
| snapshot, USN, VSS | 8 | | | |

**These 622 were dispositioned at cluster level, not one row at a time**, and
the ledger does not pretend otherwise. What was extracted from them
individually is the set of distinct *mechanisms* that survive deduplication
across releases: **34**, listed below with a disposition each — 21 proven, 7
open, 3 open-but-low-priority, and one deferred cluster of 29 entries.

The N/A share is large and that is the honest shape of this corpus: Duplicati
supports thirty-odd storage backends and a web UI, and they dominate the bullet
count. **The engine-level minority is the point.**

## The 34 engine mechanisms

Grouped by disposition. Each cites the release that named it.

### Proven — a test fails if this is reintroduced

| Mechanism | Duplicati | Evidence |
|---|---|---|
| Restore escaping its target directory | "Prevent restore outside of designated target folder" (2026-02-06) | `RestorePlanTests.RestorePlan_APathIsNotPlainComponents_IsRefused`, `…APreSeededSymlinkPointsOutsideTheRoot_IsNotWrittenThrough` |
| Restore not creating missing structure | 2020-07-09, 2025-01-07 | `RestoreIntoMissingStructureTests` (4) |
| SQLite host-parameter limit | "exceeding the number of parameters supported by SQLite" (2019-09-02) | `SnapshotScaleEdgeTests` — 2,500 entries; no dynamic parameter lists exist |
| Empty filesets | "empty filesets being created" (2025-05-29) | `SnapshotScaleEdgeTests` (3) |
| Metadata-only change discarded | "updated timestamps were discarded if no data was changed" (2025-02-11) | `MetadataOnlyChangeTests` (6) — was a live defect, fixed `b2c018c` |
| Locale-sensitive parsing | fr-CA (2025-04-11), invariant formatting (2025-09-25) | `HostileCultureTests` (21), CI tr-TR leg |
| Filename metacharacters and filters | dollar-sign ×2, "various bugs with backup filters" (2023-12-27) | `PathRuleHostileNameTests` (24) — was a live defect, fixed `51b503a` |
| URL parser inconsistency | "different results for similar inputs" (2025-03-26) | `PeerEndpointTests` (19) — was a live defect, fixed `34e511b` |
| Default-port / URL rendering | "avoiding colon for default port" (2025-05-29) | `DestinationIdentityTests` (7) |
| Invalid timestamps blocking capture | "invalid timestamps would prevent files from being backed up" (2022-06-12) | `HostileTimestampTests` (6) |
| Interrupted backup corrupting state | 2020-03-25 | `InterruptionTests` (100) |
| `dlist` stored after interrupted backup | 2020-03-25 | `PublicationInterruptionTests`, `TreeSnapshotInterruptionTests` |
| Deadlock when transfers fail | "deadlock on restore when transfers failed" (2025-05-29) | `RestoreReadFaultTests` — terminates, which is the property |
| Restore without a local database | 2026-03-20 ×2, 2024-05-30 | `RecoveryHostTests`, `RecoveryKitConformanceTests` |
| Reparse points all treated as symlinks | 2018-02-11 | `LocalFileSystemSource` tests the reparse-bit-first order |
| Atomic replace losing a file | interrupted-write family | `AtomicFileTests` (7) |
| Refusal reasons parsed as prose | class, not a single note | `WireCodeTests` (23) |
| Verification claimed without proof | "operations would report success even if failed" (2025-07-09), repository half | `SnapshotPublicationTests`, Y-arc |
| Retention losing a version | "min_generations" analogue | `RetentionPlannerTests.Select_MinGenerations_IsTheFloorTheOtherRulesCannotOverride` |
| Case folding under a hostile locale | "locale settings affecting SQL statements" (2025-09-23) | `PathRuleHostileNameTests.CaseInsensitiveMatchingFoldsTheSameWay…` |
| Scheduler ignoring the scheduled time | "backups could run immediately" (2020-05-11) | `ScheduleClockBoundaryTests.ASetThatJustCompleted_IsNotImmediatelyDueAgain` |
| A failed delete leaving durable state inconsistent (**O1**, was open — a real defect, now fixed) | "failed delete causing database inconsistency" (2026-02-06) | `Retention.Tests/SweepFailureTests` (5) |
| Shared content freed while still referenced (**O2**, was open — foreclosed, now proven) | "database inconsistency after shared metadata delete" (2026-07-13) | `Retention.Tests/SharedRecordRetentionTests` (5) |

### Open — applies, and nothing proves it

What is left after the E-C work, ranked by what a defect would cost.

| # | Mechanism | Duplicati | Why it applies here |
|---|---|---|---|
| **O5** | An index naming an object no blob holds | "`dindex`-files would reference non-existing `dblock` files" (2019-10-19); "race condition with index file uploads during backup" (2026-02-20) | `IndexPublisher` and the projector; the dangling-reference case is untested outside forensic rebuild |
| **O6** | Partial backup interacting with retention | "partial backups could create defect backups when used with retention rules" (2019-12-08) | Compounds RN-F3: a partial snapshot must not be retention's only survivor |

O3, O4 and O7 were closed by the E-C work — `Domain.Tests/PathRuleSelectionTests`
(11) for the case-sensitivity and overlapping-root pair, and
`Application.Tests/RepeatedOperationTests` (7) for the twice-applied operation.
O1 is closed above. Their rows are struck from this table rather than left
standing, because a list of open items that includes closed ones stops being
read.

Also open but deliberately low priority, recorded so they are not rediscovered:
environment-variable expansion in roots and rules (2016-10-27, 2026-08-14) —
FallbackPlan expands nothing and takes paths literally, which is defensible and
undocumented; long paths at the 259/260 boundary (2020-01-18), Windows-only;
year-zero timestamps (2026-05-22), an extreme beyond the 1601 case
`HostileTimestampTests` already carries.

### Deferred — the compactor does not exist

29 entries, the single densest cluster in the corpus, all targeting compaction
or index recreation. [ADR-0025](../adr/0025-compaction-reseals-records.md) is
*Specified only* and [ADR-0009](../adr/0009-garbage-collection-safety.md)
records the collector stopping before it. They become phase-4 exit criteria
rather than tests, because the API they would test has not been designed.

The list, verbatim, is the criteria: compact writing blocklists into index
files (2025-05-29); index files containing replicated blocklists (2025-01-11);
`dindex` missing a blocklist causing extra restore downloads (2024-11-06);
verification errors if compact was interrupted (2024-11-06); leftover index
files (2024-11-06); almost-identical files producing broken index files
(2024-09-11); data corruption caused by compacting (2019-06-30); compacted
files missing a blocklist (2020-01-23); recreated index files not reporting
deleted blocks (2025-07-11); a recreated index volume not containing all data
(2025-09-23); large index files (2018-06-17); shared buffers causing
validation errors across concurrent index generators (2018-06-17).

### N/A — architecture, worth restating

61 entries reduce to one sentence: Duplicati's local SQLite database is
authoritative and the remote store is its projection, so the two can disagree,
and `repair` exists to reconcile them. Here the store is authoritative and the
catalogue is a disposable cache — a wrong catalogue is deleted and re-derived.
Thirteen `repair` fixes, and every "database inconsistency" note that is not
O1 or O2, are unreachable for that reason.

This is the most expensive architectural decision the project made, and this
corpus is its continuing justification.

## What this ledger does not claim

It does not claim FallbackPlan cannot regress in these ways. No suite proves
absence. What it claims is narrower and checkable: **every one of the 805 fix
entries has a disposition, and every disposition names either a test or a
reason.** Where the reason is "the feature does not exist", the entry is
recorded so that building the feature inherits the test.

Two honest limits:

- **183 entries were dispositioned by rule, not by reading**, and 622 were
  dispositioned at cluster level rather than row by row. Only the 34 extracted
  mechanisms carry an individually reasoned disposition. A rule
  misclassification, or a mechanism that hid inside a cluster and never got
  extracted, would both be invisible here. The cluster sizes are published
  above precisely so somebody can go back and check the ones that look wrong —
  `unclustered` at 179 is the obvious place to start.
- Prose-only releases before 2016-10-27, and `master`'s narrative sections,
  were read rather than parsed, so their coverage rests on reading rather than
  on the bullet count.

---

## What the Open items turned out to be

Recorded as they close, because "this mechanism applies here" and "this
mechanism has already bitten here" are different claims and the ledger should
not blur them.

### O1 — a failed delete leaves durable state inconsistent

**A real defect, and a two-headed one.** `StagingSweep.SweepAsync` carried the
sentence "every refusal is a finding" in its own documentation while its four
`DeleteAsync` calls had no `try`/`catch` and discarded the returned
`DeleteOutcome`.

The loud head: the sweep walks tombstones in ordinal key order, so one
`IOException` — a sharing violation on Windows, a permission changed under a
running collector — escaped the loop, aborted the whole retention pass through
`RetentionRunner`, lost the counters and findings, and left every object
sorting after it uncollected for as long as the fault lasted.

The quiet head is the one that matches Duplicati's note most exactly, and the
one a passing test suite would never have shown. A store answering
`DeleteOutcome.NotFound` for an object still present incremented `Deleted`
anyway. Measured on the red run: **the report claimed five deletions against a
store that performed none.** That is precisely a ledger claiming work nobody
did.

`StagingTrim.ExecuteAsync` already caught both exception types — but swallowed
them silently, so a trim that deferred every candidate produced a report
indistinguishable from one that had nothing to do. It now names them.

The general lesson, worth more than the fix: **an operation whose failure mode
is "the count is wrong" cannot be caught by a test that only checks the happy
path succeeds.** Every one of these paths was covered by a passing test before
this work, and every one of those tests ran against a store that never refused.

### O2 — shared content freed while still referenced

**Foreclosed by design, and now proven.** No defect. Recording it because "we
looked and it holds" is a different and weaker claim than "a test fails if
somebody breaks it", and until this work only the first was true.

The mechanism is foreclosed twice over, independently:

- `StagingMark` walks the protected closure into a `HashSet<ObjectId>`, so a
  second referrer marking an object again is a no-op rather than a conflict —
  and the walk's early-return dedups the *visit*, never the protection.
- `CollectionPlanner` condemns a blob only when **every** record in it is
  unreachable. One live record makes the whole blob `RetainedPartialBlobs`, the
  compaction backlog, and it is never a deletion candidate.

`SharedRecordRetentionTests` exercises all three shapes sharing takes here: two
byte-identical files deduplicating to the same segments, a file untouched
across runs whose version manifest several snapshots inherit, and a blob mixing
live and dead records. It ends by restoring the surviving snapshot after a
sweep that really deleted things and comparing every file byte for byte.

Mutation proof, since a passing invariant admits no red-first run: neutering
`CollectionPlanner`'s `live > 0` guard fails four of the five; restricting
`StagingMark.MarkAsync` to the first protected snapshot fails the fifth.
Both were reverted before the commit.

## Appendix — reproduction

```bash
# corpus
for tag in master \
           v2.1.0.5-2.1.0.5_stable_2025-03-04 \
           v2.0.8.1-2.0.8.1_beta_2024-05-07 \
           v2.0.4.5-2.0.4.5_beta_2018-11-28; do
  curl -sS "https://raw.githubusercontent.com/duplicati/duplicati/$tag/changelog.txt"
done
```

Release blocks are `YYYY-MM-DD - <version>` underlined with `====`; entries are
`- ` bullets. Dedup by lowercasing, stripping inline code spans, issue numbers
and contributor credits, then collapsing to alphanumerics and spaces, keeping
the earliest release per normalised text.

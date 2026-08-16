# What Duplicati's release notes say we should be testing

**Subject:** the published release notes at [duplicati/duplicati/releases](https://github.com/duplicati/duplicati/releases) — the *shipped fixes*, as a corpus distinct from the issue tracker
**Purpose:** read what a comparable engine actually had to correct, and turn each class of correction into a test against FallbackPlan
**Outcome:** 11 classes of shipped fix. **Four turned out to be live defects here and are fixed in this arc**; four were already foreclosed but unproven and are now proven; three are foreclosed by architecture and stay that way.

---

## Why the release notes are a different corpus from the issue tracker

[The earlier pass](2026-08-duplicati-learnings.md) read Duplicati's unit suite and its issue tracker. This one reads its release notes, and they are not the same evidence.

An issue is a report: somebody thinks something is wrong. A release note is a **verdict**: a maintainer agreed, found the cause, wrote a fix, and shipped it. The tracker tells you what users notice; the notes tell you what was actually broken. The tracker is also survivorship-biased towards the dramatic — corruption, data loss, hangs — while the notes carry the long tail of small, boring, repeated corrections that never generate a thread but do generate a fix. That long tail is the interesting part, because it is where a class of defect reveals itself by *recurring*.

Three fixes for filename handling across three releases is a much stronger signal than one issue titled "backup corrupted", and it is a signal only this corpus carries.

**What was read.** The public release listing, pages 1, 2, 3, 4, 6 and 9, covering the 2.1.x canary and stable line back through the 2.0.x series — roughly the last eighteen months in detail and spot samples further back. Not every page and not the API; the sampling is recorded in the appendix so the gaps are visible rather than implied.

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

## Appendix — corpus

| | |
|---|---|
| Source | Public release listing at `github.com/duplicati/duplicati/releases` |
| Pages read | 1, 2, 3, 4, 6, 9 — the 2.1.x canary and stable line in detail, 2.0.x sampled |
| Method | HTML pages; the GitHub API and `gh` were unavailable to this session, whose repository scope is `bslater/fallbackplan` |
| Not read | Pages 5, 7, 8, and everything before the 2.0.x series. Recurrence counts below are therefore lower bounds |
| Recurring classes counted | address parsing ≥ 4 releases; locale ≥ 6; filename and filter handling ≥ 3 |
| Named notes cited | v2.1.0.109 "updated timestamps discarded if no data was changed"; the fr-CA locale parse; the dollar-sign filename fix and its later restatement |

**Commits from this pass:** `34e511b` (D-1), `51b503a` (D-2), `53a297f` (D-4), `b2c018c` (D-3).

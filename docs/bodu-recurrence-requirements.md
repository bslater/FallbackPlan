# Requirements: a shared Bodu recurrence and scheduling package

**Status: Satisfied upstream, and adopted** — every requirement below is
implemented in `Bodu.Globalization.Recurrence`, verified against a
FallbackPlan-semantics probe in three timezones, and consumed at **0.2.0**
([§8](#8-disposition)).
**Audience:** the Bodu maintainer and FallbackPlan contributors ·
**Raised:** 2026-08-05 · **Verified:** 2026-08-05

FallbackPlan's Agent evaluates backup-set schedules with a deliberately small,
hand-rolled `Schedule` class ([ADR-0027 §1](adr/0027-services-scheduling-status-telemetry.md)).
The [bslater/bodu](https://github.com/bslater/bodu) repository carries
`Bodu.Globalization.Recurrence` — an RFC 5545 RRULE engine, a Vixie-cron
parser, and a recurrence-set composer — which already satisfies the hardest
property FallbackPlan demands of schedule evaluation (purity in its
arguments) and would let the schedule grammar grow without hand-rolling
calendar arithmetic.

This document states what the shared package must provide to serve
FallbackPlan **and** other consumers with similar shape: any agent, service
host, or job runner that needs to answer "is a run due?" and "when is the
next one?" deterministically, testably, and without touching the wall clock.
It is a requirements statement against the *library*, written from the
consumer side; how Bodu implements it is Bodu's decision.

Requirement IDs here are `REC-F-*` (functional) and `REC-N-*`
(non-functional). They are scoped to this document and deliberately do not
use FallbackPlan's `FR-*`/`NFR-*` namespace; where a requirement exists to
satisfy a FallbackPlan requirement, that ID is cited.

---

## 1. Consumers and their shapes

| Consumer | What it needs |
|----------|---------------|
| **FallbackPlan Agent** | Per-backup-set schedules: anchored intervals (`every 4h` *after the last completed run*), time-of-day daily runs, and — as the grammar grows — weekly/monthly/cron forms. Missed occurrences coalesce to exactly one catch-up run. Evaluation must be a pure function of `(lastCompleted, now)` ([NFR-TIME-001](requirements/non-functional.md)). |
| **FallbackPlan CLI `status`** | "Next run" display: the next occurrence after `now`, rendered in the user's local offset. |
| **Generic service hosts** | The same is-due / next-occurrence pair, driven by whatever clock the host injects — real, simulated, or test-fixed. |
| **Calendar-aware consumers** | Occurrence streams composed with `Bodu.Globalization.Calendar` notable dates — e.g. "daily at 02:00, but not on public holidays" — without the recurrence engine itself knowing what a holiday is. |
| **Financial/date engines** | Previous- and next-occurrence queries over rule sets for settlement-date and reporting-window arithmetic (the existing `Bodu.Financial` family is the in-repo example). |

The common thread: **the library computes occurrences; the consumer owns the
clock, the timezone, and the state.** Any feature that would pull wall time,
machine timezone, or persistence into the library is out of scope.

## 2. Current state and gaps

As of bodu `master` (August 2026), `Bodu.Globalization.Recurrence` provides:

| Surface | Provides | Verified property |
|---------|----------|-------------------|
| `CronExpression` | Vixie five-field and optional-seconds six-field parsing, `@yearly`…`@hourly` macros; `GetNextOccurrence` / `GetPreviousOccurrence` over `DateTime` **and** `DateTimeOffset` | No wall-clock or timezone API is called anywhere in the project; `DateTimeOffset` overloads interpret the wall-clock in the argument's own offset and return the occurrence carrying that offset |
| `RecurrenceRule` | RFC 5545 RRULE parse/format round-trip; FREQ DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, COUNT, UNTIL, WKST and the BY* parts; occurrence enumeration and `GetNextOccurrence` | Same purity property; conformance and corpus test suites |
| `RecurrenceSet` | One or more rules composed with RDATE additions and EXDATE exclusions | Same |

Gaps against this document:

1. **No anchored-interval form.** Cron and RRULE are calendar-aligned;
   neither can express "4 hours after the previous run *completed*", which
   is FallbackPlan's default schedule shape (REC-F-002).
2. **`GetPreviousOccurrence` exists only on `CronExpression`.** Due-ness
   evaluation is a previous-occurrence comparison, so `RecurrenceRule` and
   `RecurrenceSet` need it too (REC-F-005).
3. **The purity guarantee is emergent, not contractual.** Nothing stops a
   future change from introducing a `DateTime.Now` or a machine-timezone
   conversion; FallbackPlan has already shipped exactly that bug once in its
   own code (REC-N-001).
4. **Not packaged for the committed feed.** FallbackPlan consumes Bodu as
   committed `.nupkg` files ([ADR-0021](adr/0021-consume-bodu-via-committed-package-feed.md));
   no `Bodu.Globalization.Recurrence` package exists in that feed today
   (REC-N-006).

## 3. Functional requirements

### Schedule forms

- **REC-F-001 — Calendar recurrence.** The package parses and evaluates
  RFC 5545 RRULE strings and Vixie cron expressions (five-field,
  optional-seconds six-field, and the `@` macros). *Already satisfied.*

- **REC-F-002 — Anchored intervals.** The package provides an interval form
  whose occurrences are anchored to a caller-supplied instant rather than to
  the calendar: given `(anchor, interval)`, the next occurrence after `now`
  is `anchor + interval` when `now < anchor + interval`, else `now` (or an
  equivalent formulation the consumer can build due-ness from). The anchor's
  meaning — "last completed run", "first enrolment", "contract start" — is
  the consumer's business; the library never interprets it.
  *Acceptance:* an interval of 4 hours with an anchor at 08:00 is due at
  12:00 exactly, and an evaluation at 20:00 (two missed occurrences) reports
  due-ness identically to an evaluation at 12:01 — the count of missed
  occurrences is never part of the answer.

- **REC-F-003 — Composition with exclusions.** Rule sets compose one or more
  recurrence forms with explicit added dates and excluded dates
  (RDATE/EXDATE semantics). *Already satisfied for RRULE; anchored intervals
  need not compose in v1.*

- **REC-F-004 — Calendar-aware filtering as composition, not a feature.**
  "Skip holidays" and "business days only" are expressible by filtering an
  occurrence stream against a caller-supplied predicate or a
  `Bodu.Globalization.Calendar` date set. The recurrence package itself
  carries no holiday, locale, or business-day data and takes no dependency
  on the Calendar package.

### Occurrence queries

- **REC-F-005 — Next and previous, everywhere.** Every schedule form —
  cron, RRULE, rule set, anchored interval — answers both
  `GetNextOccurrence(after)` and `GetPreviousOccurrence(before)`, each with
  an `inclusive` flag, over both `DateTime` and `DateTimeOffset`. Rationale:
  "is a run due" is the comparison
  `lastCompleted < GetPreviousOccurrence(now, inclusive: true)` — one
  expression, and missed occurrences coalesce structurally because the
  answer is a boolean, not a backlog. Without previous-occurrence on every
  form, each consumer reimplements it by enumeration, which is where the
  off-by-one-day bugs live.

- **REC-F-006 — Bounded enumeration.** Occurrence enumeration over a
  `[from, to)` window is lazy and terminates for every rule, including rules
  with no occurrences in the window. A rule that can never match (e.g. 30
  February yearly) enumerates empty rather than looping.

- **REC-F-007 — No due-ness state.** The library never stores a last-run,
  never marks an occurrence consumed, and never owns a timer. Due-ness is
  the consumer's one-line comparison over library answers (see REC-F-005's
  rationale). A convenience helper is acceptable only if it is itself a pure
  function of explicit arguments.

### Parsing and formatting

- **REC-F-008 — Strict, invariant parsing.** Parsing is culture-invariant,
  rejects rather than guesses, and names the defect ("unit must be m, h, or
  d" beats "invalid format"). A `TryParse` shape with a defect message out
  parameter (or equivalent) exists on every form, so hosts can surface
  configuration errors verbatim without exception-driven control flow.

- **REC-F-009 — Round-trip formatting.** Every parsed schedule renders back
  to a canonical string that re-parses to an equal value. Consumers persist
  schedules as text (FallbackPlan stores them in its configuration file);
  the text form is therefore part of the contract, not a debugging aid.

- **REC-F-010 — Value equality.** Schedule forms are immutable value objects
  with structural equality, so consumers can detect configuration changes by
  comparison rather than by re-parsing and diffing text.

## 4. Non-functional requirements

### Determinism and time semantics

- **REC-N-001 — Purity, enforced.** No API in the package reads the wall
  clock (`DateTime.Now`/`UtcNow`, `DateTimeOffset.Now`/`UtcNow`,
  `Stopwatch`, `Environment.TickCount`) or the machine timezone
  (`TimeZoneInfo`, `DateTimeOffset.LocalDateTime`, `ToLocalTime`). Every
  answer is a pure function of the arguments. This is enforced by a test or
  analyzer in the Bodu repository itself — not left to review — because the
  failure mode is silent: code that consults the machine timezone passes
  every test on a UTC build agent and fails only on a user's machine.
  FallbackPlan's `Schedule` shipped exactly this defect and now carries the
  regression test; the shared library must carry the equivalent guard.
  Satisfies [NFR-TIME-001](requirements/non-functional.md) for FallbackPlan.

- **REC-N-002 — Offset semantics, stated.** `DateTimeOffset` overloads
  interpret the wall-clock in the argument's own offset and return
  occurrences carrying that offset. The library performs no offset
  conversion beyond normalising *between the arguments it was given* (e.g.
  an anchor supplied in UTC compared against a `now` in +10:00). This is
  the current observed behaviour; the requirement is that it be documented
  as a contract and covered by tests with non-UTC offsets, so a UTC-only
  test matrix can never mask a regression.

- **REC-N-003 — DST posture, documented.** The library operates on offsets,
  not timezones, so daylight-saving transitions are the *consumer's*
  concern: a host that wants "02:30 local" across a DST change re-derives
  the offset each evaluation (as FallbackPlan's Agent does by passing
  `DateTimeOffset.Now`). The package documentation states this division of
  responsibility explicitly, including the two ambiguous cases (a
  time-of-day that occurs twice or not at all on a transition day) and what
  the occurrence math yields for each.

### Dependency and platform footprint

- **REC-N-004 — Minimal dependency closure.** The package depends on
  `Bodu.Core` and nothing else — no timezone libraries, no NodaTime, no
  transitive third-party packages. Rationale: FallbackPlan's dependency
  policy ([ADR-0019](adr/0019-third-party-dependency-policy.md)) admits a
  dependency only with a named justification, and every additional
  transitive identity widens the supply-chain surface its lockfile gate
  exists to pin. *Already satisfied; the requirement pins it.*

- **REC-N-005 — Platform reach.** Targets a .NET LTS baseline (net8.0 or
  later), is AOT-compatible, and is trim-safe. Consumers include
  single-file, self-contained agent binaries.

### Packaging and consumption

- **REC-N-006 — Consumable as a pinned package.** The package is produced
  as a versioned `.nupkg` (deterministic build, symbols) that a consumer can
  commit to a local feed and pin by exact version with
  `packageSourceMapping`, per FallbackPlan's
  [ADR-0021](adr/0021-consume-bodu-via-committed-package-feed.md) model.
  Publication to nuget.org is welcome but not assumed; the committed-feed
  path is the contract.

- **REC-N-007 — API stability gates.** The public API is snapshot-tested in
  the Bodu repository (the `PublicApi` baseline pattern already in use), so
  a consumer bumping the pinned version can read an API diff rather than
  discover breaks at compile time. Breaking changes bump the version
  accordingly.

- **REC-N-008 — Independent verifiability.** Occurrence semantics for the
  standardised forms (cron, RRULE) are covered by conformance suites tied to
  the defining documents (RFC 5545; Vixie cron behaviour), so a consumer can
  cross-check disputed behaviour against the standard rather than against
  the implementation. *Largely satisfied today; the requirement keeps it.*

### Performance

- **REC-N-009 — Cheap steady-state evaluation.** A next- or
  previous-occurrence query for typical rules (daily/weekly/cron without
  pathological BY* combinations) completes in microseconds and allocates
  O(1) beyond the returned value. Hosts evaluate every schedule on every
  poll tick (FallbackPlan's Agent defaults to a poll loop); evaluation must
  be cheap enough that per-tick evaluation of hundreds of schedules is
  negligible.

- **REC-N-010 — Pathological inputs bounded.** Rules that match rarely or
  never (REC-F-006's cases) answer within a documented bound — a search
  horizon or iteration cap with a defined "no occurrence" result — rather
  than scanning unboundedly.

## 5. What FallbackPlan would build on top

For traceability, the intended consumption once the package exists
(this is context, not a requirement on Bodu):

- `Schedule.TryParse` keeps FallbackPlan's grammar as the outer layer:
  `every <n><unit>` maps to the anchored-interval form (REC-F-002);
  `daily at HH:mm` and any future `weekly on <day> at HH:mm` / `cron: <expr>`
  forms map to cron or RRULE. The grammar is FallbackPlan's UX; the
  arithmetic is Bodu's.
- `IsDue(lastCompleted, now)` becomes
  `lastCompleted < GetPreviousOccurrence(now, inclusive: true)` for calendar
  forms and the existing subtraction for anchored intervals — preserving
  ADR-0027 §1's coalescing rule (an Agent asleep through five occurrences
  owes exactly one run).
- `NextRun(lastCompleted, now)` becomes `GetNextOccurrence(now)`.
- The existing machine-timezone regression test
  (`Daily_schedule_answers_do_not_depend_on_the_machine_timezone`) stays in
  FallbackPlan and must pass unchanged across the swap.
- Adoption mechanics on the FallbackPlan side: nupkg committed to
  `external/packages`, lockfile identity added deliberately, an architecture
  canary test pinning which project may reference the package (the pattern
  used for `Bodu.Security.Cryptography` and `Bodu.Core`), and an ADR
  amending ADR-0027 §1's grammar.

## 6. Out of scope

- **Execution.** Timers, pollers, job runners, and missed-run persistence
  are consumer concerns (REC-F-007).
- **Timezone data.** The library never resolves a timezone identifier; it
  computes over the offsets it is handed (REC-N-002/003).
- **Localisation of schedule text.** Parsing is invariant (REC-F-008);
  human-language rendering ("every day at 2:30 am") is a consumer feature.
- **Calendar data.** Holidays and business days live in
  `Bodu.Globalization.Calendar` and compose from outside (REC-F-004).

## 7. Open questions

All three were resolved by the upstream implementation; recorded here with
their answers.

1. **Where does the anchored-interval form live?** *Resolved:* a third
   top-level type, `AnchoredInterval`, inside
   `Bodu.Globalization.Recurrence` — one package to pin.
2. **Should the purity guard (REC-N-001) be an analyzer or a test?**
   *Resolved:* a test, and a stronger one than proposed — `PurityTests`
   scans the **compiled assembly's** member-reference metadata rather than
   the source, so a banned call cannot enter through a helper, a generated
   file, or a future refactor.
3. **Version at first consumption.** *Resolved:* 0.2.0 — upstream versions
   the Bodu packages in lock-step, so FallbackPlan consumes all four at that
   version rather than mixing sets that were never built against each other.

## 8. Disposition

Verified against bodu `10226cf` ("Add AnchoredInterval type and validation
corpus for recurrence", #652). The library's own suite is **1277 tests, all
passing**. The consumed 0.2.0 release (`e0f8997`) is a lock-step version bump
across the Bodu packages: the recurrence sources are byte-identical between
the two commits, so the verification below applies to the version actually
consumed and was not re-run against a moving target.

| ID | Requirement | Disposition |
|----|-------------|-------------|
| REC-F-001 | Calendar recurrence | Met — `CronExpression`, `RecurrenceRule` |
| REC-F-002 | Anchored intervals | **Met (new)** — `AnchoredInterval`, series `anchor + k·interval` for `k ≥ 1`; canonical text is the RFC 5545 duration grammar (`PT4H`, `P1D`) |
| REC-F-003 | Composition with exclusions | Met — `RecurrenceSet` (RDATE/EXDATE) |
| REC-F-004 | Calendar filtering as composition | Met — no dependency on `Bodu.Globalization.Calendar`; the package carries no holiday or locale data |
| REC-F-005 | Next **and** previous, everywhere | **Met (new)** — `GetPreviousOccurrence` now on all four forms × `DateTime`/`DateTimeOffset` |
| REC-F-006 | Bounded enumeration | Met — unsatisfiable rules enumerate empty |
| REC-F-007 | No due-ness state | Met, and exceeded — `AnchoredInterval` holds no anchor at all; the anchor is a per-query argument, so one interval serves many anchors |
| REC-F-008 | Strict parsing, defect named | Met — `TryParse(…, out result, out failureMessage)`, messages fit to surface verbatim |
| REC-F-009 | Round-trip formatting | Met — verified by probe for both cron and interval forms |
| REC-F-010 | Value equality | Met — verified by probe |
| REC-N-001 | Purity, enforced | **Met (new), exceeded** — `PurityTests` scans compiled IL member references for banned wall-clock/timezone APIs, and asserts the reference table is non-empty so the scan cannot pass vacuously |
| REC-N-002 | Offset semantics stated | Met — documented on each type; `CronExpressionTests.Offsets.cs` covers non-UTC offsets |
| REC-N-003 | DST posture documented | Met — division of responsibility stated on the occurrence types: offsets are the library's, transitions the caller's |
| REC-N-004 | Minimal dependency closure | Met — `Bodu.Core` only |
| REC-N-005 | Platform reach | Met — net8.0, `IsAotCompatible` |
| REC-N-006 | Consumable as a pinned package | Met — `Bodu.Globalization.Recurrence.0.2.0.nupkg` in the committed feed, pinned exactly |
| REC-N-007 | API stability gates | Met — `PublicApiTests` snapshot baselines |
| REC-N-008 | Independent verifiability | Met, and exceeded — known-answer vectors from **three independent oracles**: Cronos (cron), libical (RRULE), and the RFC 5545 §3.8.5.3 examples |
| REC-N-009 | Cheap steady-state evaluation | Met on the evidence available — the 1277-test suite runs in ~1 s; not separately benchmarked, and not on the critical path until FallbackPlan adopts |
| REC-N-010 | Pathological inputs bounded | Met — twelve-year search horizon in each direction, documented with its rationale (the largest gap any satisfiable expression can have: a 29 February schedule crossing a non-leap century year); unsatisfiable expressions answer `null` at the horizon |

### Semantic probe

Requirements are only as good as the behaviour they buy, so FallbackPlan's
committed schedule assertions were replayed against the library directly —
including the two tests that guard the timezone defect this repository
already shipped once:

- `A_daily_schedule_runs_once_per_calendar_day_at_its_time` — the daily
  cases, with `daily at 02:30` expressed as cron `30 2 * * *`.
- `Daily_schedule_answers_do_not_depend_on_the_machine_timezone` — the same
  cases in UTC+10, plus the mixed-offset case (a UTC journal anchor against
  a local `now`).
- `An_interval_schedule_coalesces_missed_runs` — including the Agent asleep
  through five intervals, which must still owe exactly one run.

Due-ness was expressed as the intended one-liner —
`lastCompleted < GetPreviousOccurrence(now, inclusive: true)` for calendar
forms, and the non-null test on the same call for anchored intervals — and
next-run as `GetNextOccurrence(now)`. **Every assertion passed, identically,
under `Etc/UTC`, `Australia/Sydney` (UTC+10) and `America/Los_Angeles`
(UTC-7)**, confirming REC-N-001/002 hold in practice and not just in the
metadata scan.

The probe also confirmed the coalescing property concretely: an Agent asleep
across twelve occurrences of a four-hourly schedule still evaluates to a
single boolean, never a backlog.

### Adoption (done)

Nothing further is asked of Bodu, and FallbackPlan has adopted the package:
the nupkg is committed to `external/packages`, the identity is pinned in
`Directory.Packages.props` and the lockfiles, `Schedule` delegates to
`AnchoredInterval` and `CronExpression`, `DependencyRuleTests` pins the
reference to the Application project with a canary that it exists, and
[ADR-0027](adr/0027-services-scheduling-status-telemetry.md) §1 carries the
amendment. The schedule tests — including the machine-timezone regression
— pass unchanged, which is the proof that the swap preserved behaviour.

The consumed version is **0.2.0**, taken from upstream's own feed along with
the other three Bodu packages, which are versioned in lock-step
([`external/packages/README.md`](../external/packages/README.md)). An interim
build packed from source bridged the short gap between `AnchoredInterval`
landing on master and upstream publishing a release containing it; it has
been replaced and should not reappear.

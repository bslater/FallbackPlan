# ADR-0043 — Structured logging: abstractions in the libraries, sinks in the hosts, redaction by type

**Status:** Accepted · **Date:** 2026-08 · **Satisfies:** [NFR-OPS-007](../requirements/non-functional.md), [FR-SVC-010](../requirements/functional.md) · **Related:** [ADR-0027](0027-services-scheduling-status-telemetry.md), [ADR-0031](0031-exception-messages-are-resources.md), [ADR-0033](0033-hosting-under-an-os-service-manager.md), [architecture 10](../architecture/10-observability.md)

---

## Context

FallbackPlan had no logging. Not "thin logging" — none. A survey of all
twenty-four source projects found no `ILogger`, no `Debug.WriteLine`, no
`EventSource`, and no log file anywhere on disk. What stood in for it were two
untyped delegates in the Agent: `ServiceOptions.Log`, an
`Action<string, Exception?>` with exactly one consumer, and an
`Action<string>` threaded into the listeners with eleven call sites in
`RemoteServiceListener` and a handful elsewhere. Both wrote a bare local
timestamp and a sentence to a `TextWriter` that, outside a foreground run,
went nowhere.

That was defensible for a long time and stopped being so. Three channels
already answer the questions an *operator* asks — Status says what state a set
is in, Progress says what a running job is doing, Notices say what needs a
person — and [10 §3.1](../architecture/10-observability.md#31-how-a-client-learns-any-of-this)
holds them deliberately apart. None of them answers the question an *engineer*
asks after the fact: why did this blob get skipped, why did dedup stop
reusing, why did the resume restart. That question is asked at a distance,
usually days later, usually by someone who cannot reproduce the machine. The
answer has to have been written down at the time.

The absence also had a cost the codebase was already paying. Diagnostic detail
that had nowhere to go was either dropped or smuggled into an exception
message, which is why several refusals carry a paragraph of context that a
`Debug`-level line should have carried instead.

## Decision

**Every library takes `ILogger`; the hosts own the sinks; redaction is by
declared type at the boundary the record crosses.**

### 1 Abstractions in the libraries, providers in one place

Libraries reference `Microsoft.Extensions.Logging.Abstractions` and nothing
else. The concrete `LoggerFactory` and the sinks live in a new
`FallbackPlan.Diagnostics`, referenced only by the Agent and the CLI.

This is [ADR-0027 §3](0027-services-scheduling-status-telemetry.md)'s rule
applied to a second signal. That decision put metrics and tracing on the in-box
`Meter` and `ActivitySource` and refused to vendor an exporter, because "a
collector stack is not something to vendor as a side effect of a scheduling
commit". The same reasoning holds here: `ILogger` is the interface every .NET
consumer already speaks, and where the bytes land is the host's business. No
provider package and no third-party sink enters the tree.

`Microsoft.Extensions.Logging.Abstractions` reaches `Repository.Format`, and
`Repository.Format`'s closure is the standalone recovery tool's closure, so
[ADR-0019](0019-third-party-dependency-policy.md) applies at its
format-critical bar. It clears it: managed-only, no native dependency (gate 2),
no algorithm surface (gate 4), MIT and therefore compatible with every ADR-0001
option (gate 5), and it reimplements no platform primitive that we would
otherwise call (gate 1) — it *is* the platform's interface for this. Gate 3 is
not engaged; the package touches no encoding. The recovery tool gains one
managed assembly and keeps its property of running on a clean machine.

A project-file canary in `DependencyRuleTests` pins the concrete
`Microsoft.Extensions.Logging` package to `FallbackPlan.Diagnostics`, the same
way `RecurrenceEngine_ProjectFileCanary_StaysInApplication` pins the recurrence
engine to `Application`. Without it, "abstractions only" is a sentence in a
document rather than a property of the build.

### 2 An optional parameter, defaulting to `NullLogger`

Constructors and static factories gain a trailing `ILogger? logger = null`,
stored as `logger ?? NullLogger.Instance`.

The alternative — a required parameter — is cleaner in the abstract and was
rejected on arithmetic. It is a breaking change to several hundred call sites
and most of a 1,731-test suite, and the diff would bury the logging it exists
to add. There is also no container to lean on: composition here is manual
constructor wiring by design, and [ADR-0033](0033-hosting-under-an-os-service-manager.md)
rejected the Generic Host outright.

A logger that is absent is `NullLogger.Instance`, never `null`, so no call site
needs a guard and no hot path pays for a branch it did not ask for.

### 3 `[LoggerMessage]` partials, and templates are not resources

Every call site is a source-generated `[LoggerMessage]` partial method,
collected in one `Log.cs` per project. This is not a preference. With
`AnalysisLevel=latest-recommended` and `TreatWarningsAsErrors=true`, **CA1848**
(use the LoggerMessage delegates), **CA2254** (template must be a static
expression), **CA1727** (PascalCase placeholders) and **CA2017** (argument
count) are all errors; `logger.LogInformation($"…")` does not compile in this
repository.

The constraint is welcome. Source-generated logging allocates nothing when the
level is disabled, and it forces every message to carry a **stable `EventId`**
— which is what makes a log greppable by a person who has the number from a bug
report and not the sentence.

**Log templates stay as literals and do not go through resx.**
[ADR-0031](0031-exception-messages-are-resources.md) left this open, listing
"log and telemetry text" among the messages it did not cover and calling the
question "a separate decision, not an oversight". This is that decision.
Exception messages are the product's voice at the worst moment and belong in
the reader's language; a log line is a diagnostic record read by an engineer
alongside the specification, and translating it would make it *harder* to
correlate across machines and reports. CA2254 requires a compile-time constant
template in any case. Exception messages keep using resx exactly as before.

`EventId` ranges are allocated per project and asserted unique by test:

| Range | Projects |
|-------|----------|
| 1000–1099 | `Domain` |
| 1100–1299 | `Repository.Format` |
| 1300–1399 | `Repository.Crypto` |
| 1400–1999 | `Repository.Segmentation`, `.Packing`, `.Index`, `.Catalogue` |
| 2000–2499 | `Repository` |
| 2500–2799 | `Storage.*`, `Filesystem.*` |
| 2800–3199 | `Restore`, `Retention`, `Replication`, `Recovery` |
| 3200–3399 | `Protocol`, `Keystore` |
| 3400–3599 | `Application` |
| 3600–3699 | `Api` transport |
| 3700–3999 | `Agent` |
| 4000–4199 | `Cli`, `Web` |

### 4 Redaction by declared type, applied where the record crosses a boundary

[Specification 03 §8](../../specifications/repository-format/03-keys.md) is a
MUST: no passphrase, KEK, master key, derived key or blob key in any log, and
redaction **by declared type rather than by string matching**, so a new
secret-bearing field is protected by construction. The idiom already exists —
`Passphrase.ToString()` returns `"passphrase(redacted)"`, and so do `Kek`,
`RepositoryWriteCredential`, `RepositoryReadAuthority`, `KeyBundle` and
`ContentId`. `[LoggerMessage]` calls `ToString()` on `object` holes, so those
six types are already safe wherever they appear. Nothing about them changes.

Paths are the harder half, because they are simultaneously the most useful
thing a log can say and, as
[10 §4](../architecture/10-observability.md#4-diagnostics) puts it, the thing
that "frequently reveal[s] more about a person than file contents do".

The resolution is that **sensitivity is a property of the type and the policy
is a property of the destination**. A captured `LogRecord` keeps its structured
state — event id, level, category, template, name/value pairs — rather than a
rendered string, and rendering happens once per consumer:

- The **on-disk local log** renders plaintext. It sits inside the trust
  boundary, in a state directory only the service account may read, on the
  machine that already holds the files themselves.
- **Anything crossing the boundary** — the client diagnostics feed, an exported
  bundle — renders redacted. A value is redacted when its declared type
  implements `IRedactedValue`: `LogPath` becomes `path#<8 hex>` with the
  extension kept, and the correlatable identifiers (`RepositoryId`,
  `ObjectId`, `BlobId`, `WriterId`, `StoreBlobKey`, `KeyId`, `DeltaId`,
  `CheckpointId`) shorten to a stable prefix. Lines still correlate with each
  other; they no longer name anybody's files.

A hash rather than an elision is deliberate: "the same file failed twice" and
"two different files failed" are different bugs, and an operator reading a
redacted feed still has to be able to tell them apart.

This satisfies NFR-PRIV-003 for anything that leaves, and it is enforced the
way the telemetry allowlist is enforced — by a test that drives a full publish
and restore through a recording logger and asserts that no secret-bearing value
and no plaintext path survives the redacted rendering.

**Telemetry is untouched.** `EngineDiagnostics.AllowedAttributes` and the
ADR-0027 §3 instrument table stay exactly as they are. Logs are a different
channel with different rules, and [10 §3.1](../architecture/10-observability.md#31-how-a-client-learns-any-of-this)
already names conflating them as "how a path ends up in a metrics backend".

### 5 Levels, and what logging is not for

`Trace` is per-record and per-segment detail. `Debug` is per-file and per-blob
steps, resume decisions, discard reasons. `Information` is lifecycle: a job
started, a snapshot published, a set adopted. `Warning` is degraded and
continuing. `Error` is the operation failed. `Critical` is the service cannot
continue. Density is tiered by layer — the engine and the hosts log richly, the
codecs in `Repository.Format` log at `Trace` only, so the recovery tool stays
quiet on a machine where quiet is the point.

**Logging does not become a fourth operator channel.** Status, Progress and
Notices keep their jobs unchanged. 10 §3.1 says of a notice that "it is not a
log line — a log line is what nobody reads until afterwards", and that remains
exactly the distinction: anything a person must act on is still a notice, and
adding a log line is never the fix for a missing one.

### 6 The level is configurable, and diagnostics reach a client

The effective level comes from, in order: a `--log-level` flag, the
environment, the `logging` section of `config.json` (schema 4), then
`Information`. It is also changeable at runtime over the command contract,
because the level a machine needs is only ever known once it has already
misbehaved, and asking someone to restart the service to find out why it
crashed is asking them to destroy the evidence.

Contract 1.13 adds `get_diagnostics`, `set_log_level` and a **paginated**
`read_log` — paginated because `FrameCodec` caps a frame at 8 MiB and "send me
everything" is not a thing a log reader may ask. Records are served from a
bounded in-memory ring buffer with a monotonic sequence, modelled on
`ProgressHub`, whose drop-oldest-with-sequence design exists precisely so a
client can detect that it missed something. The service never hands a client a
file path: [T-16](../threat-model.md) holds that it "exposes no raw filesystem
access to clients", and a log reader is not the place to make an exception.

**Local callers get everything; paired remote callers get a redacted,
read-only view.** Remote clients are already confined — file content is
withheld from them by default (FR-SVC-005) — and a log is closer to file
content than it looks, since it is largely a list of the names of the files a
person owns. A remote caller may read redacted records and see the effective
level; `set_log_level` from the remote binding is refused. Since one
`ServiceCommandHandler` instance serves both bindings, caller scope arrives as
a per-session decorator rather than as a change to the contract interface.

## Consequences

**Positive**

- A failure on a machine nobody can reach now leaves evidence. That was the
  whole point, and it was previously not true in any form.
- Stable event ids make a log correlatable across versions and greppable from a
  bug report that quotes a number rather than a sentence.
- Redaction is a property of a type, so a new secret or a new path-bearing
  field is protected the moment it is declared, without anyone remembering a
  filter list — which is what NFR-SEC-006 asks for and what string matching
  cannot deliver.
- Diagnostic detail that had been inflating exception messages has somewhere
  better to go.

**Negative**

- One package now reaches every project, including the recovery tool's closure.
  It is small, managed and first-party, and it is still a dependency that was
  not there before, judged at ADR-0019's format-critical bar rather than waved
  through.
- `[LoggerMessage]` partials are more ceremony per message than a call. The
  analyzers leave no choice, and the ceremony buys the event id, but a
  one-line diagnostic is now three.
- Two renderings of the same record — plaintext and redacted — is a second
  thing that can be got wrong. It is covered by a test that fails the build,
  which is the only reason it is acceptable.
- A rolling file sink is durable state the service now writes and must retain,
  rotate and bound. Disk that used to be the user's is now, in a small way,
  ours.

**Neutral**

- Config gains a schema version (3 → 4). The migration machinery for that
  already exists and was exercised at 2 → 3.
- The two ad-hoc `Action<string…>` delegates disappear. Their call sites become
  typed and levelled; none of the behaviour they drove changes.

## Alternatives considered

**Serilog, or another third-party stack.** Richer sinks, enrichment and
structured output for free, and a large ecosystem. Rejected: it is a
third-party logging stack in a deliberately dependency-light tree governed by a
committed feed ([ADR-0021](0021-consume-bodu-via-committed-package-feed.md)),
and it would contradict ADR-0027 §3's "no exporter packages" for the sake of
convenience in four host processes. The sinks we need are a ring buffer and a
rotating file; both are small and both are ours to test.

**Keep the `Action<string, Exception?>` delegates and grow them.** Zero new
dependencies. Rejected: it has no level, no category, no event id and no
structure, so it cannot be filtered, correlated or redacted by type — every
property this decision exists to obtain would have had to be hand-built on top
of a delegate, arriving at `ILogger` by a worse road.

**A required `ILogger` constructor parameter.** Impossible to forget, and
honest about the dependency. Rejected on blast radius: several hundred call
sites and most of the test suite, in the same commits as the logging itself.
The optional parameter reaches the same place and can be tightened later if it
ever proves too easy to skip.

**Redact paths everywhere, including the local file.** The strictest reading of
NFR-PRIV-003 and the simplest to reason about — one rendering, no boundary.
Rejected as the wrong trade for the local file specifically: it sits inside the
trust boundary on the machine that holds the files, and a support log that
cannot name the file that failed answers almost none of the questions it exists
to answer. The boundary moved to the transport instead, where the exposure
actually is. [10 §4](../architecture/10-observability.md#4-diagnostics) is
amended to say so rather than left to imply otherwise.

**A `logs` file the client reads by path.** Simplest possible implementation:
hand the client a path and let it tail. Rejected against
[ADR-0028](0028-service-boundary-and-deployment-topologies.md) §5 and T-16 —
the service exposes no raw filesystem access to clients, and a path handed
across the boundary is exactly that, plus a plaintext-path leak by
construction.

**Adopt the Generic Host to get logging configuration for free.** Rejected
again, for the reasons [ADR-0033](0033-hosting-under-an-os-service-manager.md)
first rejected it. That decision named logging as one of the things this
service "either does not need or already has by another means"; what is added
here is the abstraction and two small providers, not a hosting model.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Abstractions in twenty-four projects; sinks in `FallbackPlan.Diagnostics`; contract 1.13 diagnostics verbs |

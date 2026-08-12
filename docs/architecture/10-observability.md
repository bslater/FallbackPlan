# 10 — Observability

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §17 · **Relates to:** [H5](../review/2026-08-architecture-review.md#h5--there-are-no-quantitative-performance-targets-anywhere)

**Built:** Yes — the status model, job states and instrumentation are all implemented — see [implementation status](../implementation-status.md).

---

## 1. User-level status

The user-facing view answers the six questions in [`00-overview.md` §2](00-overview.md#2-product-promise) and nothing else. It does not expose blobs, segments, generations, or index deltas.

| Shown | Source |
|-------|--------|
| Last protected time, per backup set | Latest locally committed snapshot |
| Next scheduled run | Schedule |
| Files awaiting backup | Scan queue depth |
| Destination health, **per destination** | Replication state ([`04-concurrency-and-publication.md` §6.1](04-concurrency-and-publication.md#61-the-distinction)) |
| Last verified restore point | Verification coverage ([`09-replication-and-peers.md` §5](09-replication-and-peers.md#5-destination-verification)) |
| Warnings requiring action | Damage reports, quota exhaustion, stale recovery kit, unusual deletion rates |
| Recovery-kit status | Never generated / saved / stale |

### 1.1 States must be distinguishable

The status vocabulary is normative, because collapsing any two of these is how a user comes to believe they are protected when they are not:

| State | Meaning |
|-------|---------|
| `captured` | Snapshot committed to a replica, but only within the source's own failure domain — real, and **not** a defence against losing the machine |
| `protected` | Durable at a replica **outside** the source's failure domain ([`04-concurrency-and-publication.md` §6.4](04-concurrency-and-publication.md#64-protected-requires-an-independent-failure-domain)) |
| `replicated` | Durable at a named destination |
| `verified` | Independently confirmed at that destination, with coverage and age |
| `policy-compliant` | The backup set's durability policy is satisfied |
| `degraded` | Recoverable, but below policy — an offline destination, failed verification, or quota exhaustion |
| `unrecoverable` | Required objects are missing or damaged with no replica able to heal them |

`degraded` and `unrecoverable` are materially different and are never merged into a single "problem" indicator. The first means act soon; the second means data is already gone.

`captured` and `protected` are likewise never merged. A repository sitting on the same disk as the source is a real safeguard against deleting a file by mistake and no safeguard at all against the disk failing — and the most common consumer configuration produces exactly that state. Reporting it as `protected` would be the false-confidence failure this project names as a major risk ([PT-8](../review/2026-08-fix-pressure-test.md#pt-8--protected-does-not-require-a-replica-outside-the-sources-failure-domain)).

### 1.2 Honest degradation

Two rules follow from §23 of the original proposal listing "consumer UI hides degraded state → false confidence" as a major risk:

- **Never show a single green tick derived from an unverified claim.** "Verified" always carries coverage and age ([`09-replication-and-peers.md` §5.3](09-replication-and-peers.md#53-sampling-policy)).
- **Never show "backed up" when only some destinations hold it.** Per-destination state is the display unit; a summary is derived from it, never a substitute for it.

## 2. Technical metrics

Exported via OpenTelemetry. These exist to make the performance targets in [`../requirements/non-functional.md`](../requirements/non-functional.md#performance) measurable in production, not only in benchmarks.

**Pipeline** — scanned files and bytes · changed files · logical versus unique bytes · deduplication ratio · compression ratio · segment and blob rates · per-stage queue depth · stage stall time.

**Storage** — blob utilisation (fill fraction at seal) · upload and download throughput · **provider request counts by operation** · provider error and throttle counts · **PUTs and requests per GB written**.

**Catalogue** — path-resolution latency percentiles · deduplication-lookup latency percentiles · cache hit rate · **catalogue size in bytes and bytes per file version** · applied generation watermark · rebuild progress and rate.

**Repository** — snapshot publication latency · verification coverage and challenge age · damaged and missing object counts · retention and GC estimates · reclaimable bytes.

**Peers** — connectivity path (direct or relayed) · relay bytes · per-set fairness share · resumed-transfer count.

The emphasised metrics are the ones tied directly to NFR-PERF thresholds. Without them, "object-store request amplification" — a named major risk with packing as its mitigation — has no way of being detected when the mitigation stops working.

## 3. Job state machine

```text
Pending
  → Scanning
  → Reading
  → Segmenting
  → Packing
  → Uploading
  → Publishing
  → Verifying
  → Complete

Any active state
  → Paused
  → Retrying
  → Cancelled
  → FailedRecoverable
  → FailedPermanent
```

Every transition and checkpoint is durable and idempotent. `Segmenting` replaces the original `Chunking` per the terminology rule in [`01-domain-model.md` §2](01-domain-model.md#2-terms-we-do-not-use).

`FailedRecoverable` and `FailedPermanent` are separated because the user action differs: the first resolves itself or resumes, the second needs intervention and should say what kind.

### 3.1 How a client learns any of this

Status and progress are computed inside the service and reach a UI over the
command surface ([ADR-0028](../adr/0028-service-boundary-and-deployment-topologies.md)),
not by a client reading the engine's files. A front end that parsed `jobs.json`
would be a second implementation of §1's derivation rules, free to drift from
the first — and the never-merge rules of §1.2 only hold if one place decides
them.

Three channels, deliberately distinct:

| | **Status** | **Progress events** | **Notices** |
|---|---|---|---|
| Answers | "am I protected?" (§1) | "what is happening right now?" | "what happened while I was not looking?" |
| Shape | Queried, derived on demand | Streamed while a job runs | Durable records, held until acknowledged |
| Carries | The §1.1 vocabulary, per set and per destination | Job identity, §3 state, counts of files and bytes | The event and its consequence: a peering ended, terms narrowed, a quota was hit, a removed destination still holds data |
| Survives a restart | Yes — derived from durable state | No — a job restarted is a new stream | Yes — that is the point |

Notices exist because hub-and-spoke ([ADR-0034](../adr/0034-hub-and-spoke-destinations.md))
creates events that are neither a state nor a moment: a friend ending a peering
at 3 a.m. must still be known at breakfast, after a reboot, without the
condition needing to re-occur. A notice is raised once, surfaces in `status`
and its own listing until a human acknowledges it, and names the action it
asks for. It is not a log line — a log line is what nobody reads until
afterwards.

**Progress events are not telemetry.** They may carry job identity because they
travel to an authenticated local caller or a paired remote client and are shown
to the person whose data it is; the OpenTelemetry instruments keep their closed
attribute allowlist untouched ([ADR-0027](../adr/0027-services-scheduling-status-telemetry.md) §3,
NFR-PRIV-002). Conflating the two is how a path or a filename ends up in a
metrics backend, so they stay separate channels with separate rules.

The §3 states are what progress reports. A pipeline that announces `Scanning`
and then says nothing for ten hours is the failure this state machine was
specified to prevent, so a state that is never emitted is a state that is not
implemented.

**A console watching several machines** ([ADR-0028](../adr/0028-service-boundary-and-deployment-topologies.md) §8)
aggregates only by derivation: a machine's summary is computed from its per-set,
per-destination detail, the detail stays reachable, and no roll-up invents a
state the §1.1 vocabulary does not have. A service the console cannot currently
reach is shown as **stale, with the age of the last contact** — never healthy,
never failed, because neither is known. "Unknown" displayed honestly is worth
more than a green tick that means "I have not heard bad news".

## 4. Diagnostics

A diagnostic bundle omits, by default: credentials · keys and recovery material · plaintext paths · repository identifiers that could correlate a user across stores.

Redaction is **by type**, not by string pattern. A field is marked secret at the point it is declared, so a new secret-bearing field is redacted by construction rather than by someone remembering to add it to a filter list. String-based redaction fails silently and fails exactly when it matters (NFR-SEC-006).

Including plaintext paths requires explicit per-bundle opt-in, with the consequence stated plainly — path names frequently reveal more about a person than file contents do.

## 5. Telemetry

No telemetry is transmitted off the device without explicit opt-in. When enabled, what is collected is enumerated in the UI, and it never includes paths, filenames, repository identifiers, destination endpoints, or anything derived from file content (NFR-PRIV-001..003).

A backup product is trusted with the shape of a person's entire life. The default is that it tells nobody anything.

---

**Previous:** [09 — Replication and peers](09-replication-and-peers.md) · **Next:** [11 — Solution structure](11-solution-structure.md)

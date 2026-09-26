# ADR-0011 — Commit versus replication semantics

**Status:** Accepted (amended 2026-08 after [pressure test](../review/2026-08-fix-pressure-test.md))
**Date:** 2026-08
**Requirements:** FR-SNP-001, FR-SNP-003, FR-REP-001, NFR-OPS-002
**Review finding:** [C5](../review/2026-08-architecture-review.md#c5--snapshot-commit-is-defined-so-that-one-offline-destination-stalls-all-protection)

---

## Context

FR-SNP-001 required snapshots to be published "only after all required blobs and index deltas are durable". §8.4 separately described a multi-destination durability policy: "complete when local repository durable **and** at least one trusted peer durable".

Read together, "required" makes a snapshot hostage to the least available destination. A peer switched off for a fortnight's holiday means no snapshot is published for a fortnight. Local protection that is working perfectly is withheld because a remote destination is unavailable.

It compounds: with no recent snapshot to compare against, there is nothing for version comparison to reuse, so the eventual catch-up is far more expensive than a series of incrementals would have been. And the status display has nothing truthful to say — it would show no recent backup, when in fact the data is safely captured locally.

The proposal was caught between two ideas that are each correct in isolation: §7.10's within-a-replica ordering rule, and §8.4's cross-replica health policy. Collapsing them into one requirement broke both.

## Decision

Separate the two.

**Commit is per-replica.** A snapshot is committed to a replica once every object it references is durable *in that replica*, following the publication order in [`../architecture/04-concurrency-and-publication.md` §5](../architecture/04-concurrency-and-publication.md#5-publication-order). This preserves the ordering invariant exactly as written, is always achievable locally, and is what makes a replica independently restorable.

**Replication is separate state,** per `(snapshot, destination)`:

| Status | Meaning |
|--------|---------|
| `pending` | Not yet started for this destination |
| `replicating` | Transfer in progress |
| `durable` | All referenced objects durable there |
| `verified` | Independently confirmed by challenge |
| `degraded` | Previously durable, now failing verification or partially missing |

**Policy is evaluated over replication state**, producing `captured` / `protected` / `policy-compliant` / `healthy` rather than a single boolean.

### Amendment 1 — `protected` requires an independent failure domain

The original policy made `protected` mean "the local repository holds it". That is unsafe as the primary reassuring state, because the most common consumer setup puts the local repository on the same disk as the source data. A user who accepts the default and never brings their offsite peer online would see `protected` right up until the disk failed — the "consumer UI hides degraded state → false confidence" risk the original proposal named, reintroduced by this very fix ([PT-8](../review/2026-08-fix-pressure-test.md#pt-8--protected-does-not-require-a-replica-outside-the-sources-failure-domain)).

Replicas now declare a **failure domain** (`same-volume`, `same-machine`, `same-site`, `independent`), and `protected` requires at least one replica whose domain is disjoint from the source's. A snapshot held only on the source volume is `captured` — accurate, and not reassuring. See [ADR-0018](0018-replica-failure-domains.md).

### Amendment 2 — retention must not outrun replication

Commit is per-replica and retention is per-replica, and nothing connected them. A set keeping 7 days locally, replicating to a peer offline for a fortnight, expires days 1–7 locally on day 8; the peer returns on day 14 and never receives them. That history exists nowhere, and nothing reported a loss because each side applied its configured policy exactly ([PT-9](../review/2026-08-fix-pressure-test.md#pt-9--local-retention-can-silently-erase-history-a-destination-never-received)).

Retention shall not expire a snapshot that has not reached the destinations its policy requires, unless a configured deferral bound is exceeded — at which point the gap is raised as a warning requiring action rather than applied silently. Holding extra snapshots costs disk; expiring them costs history.

### Amendment 3 (2026-08) — "the local repository" becomes the set's staging archive

[ADR-0034](0034-hub-and-spoke-destinations.md) gives each backup set its own
archive on the hub — a staging archive publication always lands in — and makes
every destination a whole-archive replica of it. This ADR's separation carries
over unchanged and becomes cleaner to state: **commit is against the staging
archive** (always local, always achievable, never blocked by a destination), and
**replication state is per `(snapshot, destination)`** exactly as the table
above defines, now with real destinations to be in states about. What this
amendment retires is the *privileged local replica*: staging is a cache the hub
manages, not a destination a policy counts, so no user-facing state may treat
"it is in the local repository" as protection — that judgement belongs entirely
to [ADR-0018](0018-replica-failure-domains.md)'s domains, evaluated over
configured destinations.

The "per-destination snapshot objects" alternative below stays rejected, and
per-set archives are not it: destinations hold byte-identical replicas of one
archive, so the snapshot's identity is never ambiguous. The only lawful
divergence is a lagging replica or a hub-planned retention trim
(ADR-0034 §2, §4).

### Amendment 4 (2026-08) — commit re-unifies with the destinations for direct-ship sets

[ADR-0046](0046-direct-to-destination-publication.md) removes the staging
archive for a set flagged `direct_ship`, and with it Amendment 3's central
clause: for such a set there is no local archive to commit against, so
**commit is per destination**, through the ship sink — the snapshot commits
at each destination that completed the run, and a capture with **no**
reachable destination refuses as a stated recoverable failure rather than
committing anywhere (ADR-0046 §4, FR-DEST-015). "Never blocked by a
destination" — this record's founding property, and the Consequences bullet
below — is thereby consciously given up for direct-ship sets: the owner
traded the staging copy's durability guarantee for a machine that holds no
local copy of its backups. The per-`(snapshot, destination)` state table is
untouched and matters more, not less.

The lawful-divergence list gains a third entry for direct-ship: a destination
**dropped mid-run** (or skipped for want of a baseline) holds a
lagging-but-valid replica — a journal intent nothing retired, exactly an
interrupted copy's state — healed by the next catch-up, never a divergent
archive. Everything else here, the separation of replication state from
commit above all, carries over unchanged; staging sets keep Amendment 3's
shape until the flag flips.

## Consequences

**Positive**

- Local protection is never withheld because a remote destination is offline *(staging sets; Amendment 4 trades this away for direct-ship sets)*.
- Incremental comparison keeps working during a destination outage, so catch-up stays cheap.
- The status model can say "protected locally, waiting on the offsite copy" — a true statement the original could not express.
- Each replica remains independently restorable, which is the property that made the invariant worth having.

**Negative**

- More state to track and display: one status per `(snapshot, destination)` rather than one per snapshot.
- The UI must resist collapsing it back into a single indicator, which is the natural pull. [`../architecture/10-observability.md` §1.2](../architecture/10-observability.md#12-honest-degradation) makes that a rule.

**Neutral**

- `SnapshotCommitResult` reports against the local replica; destination progress is observed through the replication service.

## Alternatives considered

**Keep the coupled definition; require a destination before backing up.** Rejected. It fails exactly when it matters — during an outage — and it makes a laptop that is away from its home peer unprotectable.

**Publish optimistically, retract if replication fails.** Rejected. Retraction contradicts immutability and would mean a snapshot a user saw could later vanish.

**Per-destination snapshot objects.** Rejected. Duplicates metadata proportional to destination count and makes the snapshot's identity ambiguous — which of them is *the* snapshot?

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-08 | Accepted (amended) | Commit/replication split unchanged. `protected` now requires a replica outside the source's failure domain (PT-8); retention may not outrun replication (PT-9). |
| 2026-08 | Accepted (amended) | Amendment 3: commit is against the set's staging archive and no state treats the local copy as privileged ([ADR-0034](0034-hub-and-spoke-destinations.md)). |
| 2026-08 | Accepted (amended) | Amendment 4: for direct-ship sets ([ADR-0046](0046-direct-to-destination-publication.md)) commit is per destination through the ship sink, a capture with no reachable destination refuses rather than committing anywhere, and a mid-run drop is the third lawful divergence. Staging sets are unchanged. |

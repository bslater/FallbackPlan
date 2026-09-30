# ADR-0009 — Garbage collection safety

**Status:** Accepted (amended 2026-08 after [pressure test](../review/2026-08-fix-pressure-test.md))
**Date:** 2026-08
**Requirements:** FR-SNP-004, FR-GC-002, FR-GC-003, FR-GC-006, NFR-TIME-001, NFR-TIME-002
**Review finding:** [C4](../review/2026-08-architecture-review.md#c4--garbage-collection-can-delete-blobs-belonging-to-an-in-flight-snapshot)

---

## Context

Publication order — blobs, then index deltas, then snapshot — is correct and is the most valuable rule inherited from the prior art. It also creates a window: between the first blob upload and the delta publication, potentially hours on an initial backup, a writer's blobs are durable and referenced by nothing. A mark-and-sweep collector cannot distinguish them from garbage.

The proposal closed this with "account for active writer leases and grace periods". A lease cannot carry that weight:

- **Clock skew.** A lease is a timed record and there is no trusted time source. Skew between writer and collector translates directly into blobs swept while in use.
- **Eventual consistency.** The store may not show the collector a lease written seconds ago — and the format explicitly permits stores that behave this way.
- **Suspension.** A closed laptop lid, a suspended VM, or a scheduler hiccup loses a lease while its blobs remain legitimate.
- **No binding.** Nothing ties a lease to *which* blobs it protects, so a collector cannot act on one except by declining to collect at all.

The consequence is data loss inside a snapshot the user was told completed successfully, discovered at restore.

## Decision

### Write-intent records

Before uploading its first blob, a writer publishes to `/journal/<writer-id>/<sequence>`:

```text
write_intent {
  writer_id, sequence, issued_at
  backup_set_id
  intended_blob_ids[]      // extended by further intent records as the job grows
  declared_max_duration
  expiry_generation
}
```

- The collector treats every blob covered by an **unretired** intent as reachable. No exceptions, no heuristics.
- The writer retires the intent when its snapshot is published.
- An abandoned job's intent expires only when **both** the generation and duration conditions below are met.

The only ordering obligation is that the intent covering a blob is durable **before** that blob is uploaded.

### Amendment 1 — the collector is a writer

The original algorithm applied intent protection to backup writers and not to the collector, which also creates blobs during compaction. Between writing a replacement blob and publishing its index entries, that blob is unreferenced — precisely the window intents exist to cover — so a second concurrent collector could sweep it, after which the first publishes index entries into a deleted blob and tombstones the originals. Both copies of every record in the batch are lost ([PT-3](../review/2026-08-fix-pressure-test.md#pt-3--compaction-output-blobs-are-unprotected-between-creation-and-index-publication)).

The rule is therefore stated generally: **any component that creates a blob publishes an intent first, with no exception for maintenance.** The GC algorithm gains explicit publish and retire steps around compaction.

### Amendment 2 — blob identifiers must be writer-allocated

An intent names blobs before they exist, which is impossible for a content-derived identifier. The format never said how blob identifiers are formed, leaving this mechanism unimplementable ([PT-4](../review/2026-08-fix-pressure-test.md#pt-4--blob-identifier-formation-is-unspecified-and-c4-cannot-be-implemented-without-it)). Resolved in [ADR-0016](0016-blob-identifier-formation.md): blob identifiers are writer-allocated and opaque, unlike record identifiers, which remain content-derived and keyed.

### Amendment 3 — expiry needs two conditions

An intent expires only when the repository has advanced past `expiry_generation` **and** the writer's `declared_max_duration` has elapsed with a configured skew margin.

> **Configured 2026-09 ([Amendment 7](#amendment-7-2026-09--the-skew-margin-configured)).** Until then the margin was a fixed five minutes. It is now `clock_skew_margin_hours`, and a day when the configuration file states none.

Generation alone couples one writer's liveness to other writers' activity — generations advance when *others* publish, so a laptop running a three-week initial backup can be expired in two days by siblings backing up hourly, and have its blobs collected mid-job. Wall-clock alone reintroduces the clock dependency this ADR exists to remove. The duration is declared by the writer rather than fixed globally, because a 4 TB first backup and a 20 MB incremental have no single safe constant between them ([PT-5](../review/2026-08-fix-pressure-test.md#pt-5--intent-expiry-mixes-generation-and-wall-clock-and-couples-slow-writers-to-busy-repositories)). An audited administrative force-expire covers genuinely abandoned jobs.

### Safety rests on four mechanisms, none of them a clock

| Mechanism | Protects against |
|-----------|------------------|
| Generation cut-off | Racing with concurrent publication |
| Unretired write intents | Sweeping in-flight work |
| Tombstone grace period | Acting on a stale or incomplete view |
| Pre-delete revalidation | Anything the first three missed |

### Leases are demoted

Leases remain, advisory, for one purpose: stopping two collectors doing the same work. Losing one costs efficiency and nothing else. **No correctness property may depend on a lease.**

## Consequences

**Positive**

- GC concurrent with an in-flight backup is safe by construction, not by timing.
- Safety survives clock skew, store latency, and writer suspension — the three things that actually happen.
- A collector knows exactly which blobs are protected and why, so its dry-run report can say so.

**Negative**

- One extra journal write before the first blob upload, plus extensions as the job grows. Negligible against the payload.
- An abandoned job's blobs occupy space until its intent expires. Bounded by the grace period, and reported as reclaimable-pending.
- Writers must retire intents. A writer that never does delays collection of its blobs until expiry — wasteful, never unsafe.

**Neutral**

- Retention still uses wall-clock time, because "keep daily snapshots for 30 days" is inherently a wall-clock policy. It is applied to recorded capture times, and an implausible timestamp is flagged rather than silently acted on.

## Alternatives considered

**Leases with generous timeouts.** Rejected. Makes the race less likely without eliminating it, and lengthening the timeout trades one failure (data loss) for another (collection never runs).

**Refuse to collect while any writer is active.** Rejected. On a repository with several devices, some writer is nearly always active, so collection would effectively never happen.

**Reference-count blobs at upload.** Rejected. Mutable counters on an eventually consistent store need atomic increment, which is not uniformly available, and a crashed writer leaks counts permanently.

**Collect only blobs older than a long fixed age.** Rejected. An age threshold long enough to be safe for a slow initial backup is long enough to make collection useless, and it is still a clock.

## Amendment 4 (2026-08) — where the collector runs under hub-and-spoke

[ADR-0034](0034-hub-and-spoke-destinations.md) splits one archive into a
staging archive per set plus whole-archive replicas at destinations, and the
collector's world divides the same way. **Marking happens where the keys are:
the hub computes the keep-set and its object closure against the set's staging
archive**, under every safety mechanism above, unchanged — intents, generation
cut-off, grace, revalidation. Destinations are then *converged, not collected*:
a local-path destination has the hub's plan executed against it directly, and a
peer is instructed which objects to delete and deletes exactly those, bounded
by its own granted floor — a spoke holds ciphertext it cannot mark, so it never
runs this algorithm and never decides what is garbage.

Two rules join the four mechanisms for the fan-out world. **Deletion may not
outrun replication**: an object leaves staging only when every configured
destination of the set holds it, or the deferral bound of
[ADR-0011 Amendment 2](0011-commit-versus-replication-semantics.md) has been
raised as a warning — the same gate that makes staging trimmable at all.
And **a destination's deletions are keyed to the hub's plan**, never inferred
from local reachability, because local reachability at a replica is exactly the
partial view this ADR exists to distrust.

## Amendment 5 (2026-08) — the grace generation, realised

Building the collector surfaced a conflation the design had survived on
paper: the number every code path called "the generation" is the **key
generation**, which advances on key rotation — not on publication. A grace
period counted in it would never run, and a replication gate compared
against it would hold nothing, because rotation is rare and publication is
the event both actually wait for.

A per-set staging archive is **single-writer by construction**
([ADR-0034](0034-hub-and-spoke-destinations.md)), and a single writer has
exactly one per-publication monotonic every participant can see: its
**journal sequence**, carried in cleartext as each standalone snapshot
record's counter ([ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md)
§Decision 7). The staging collector therefore counts its grace in that
sequence — a tombstone becomes eligible only after the writer has visibly
published past the decision — and the replication gate compares each
snapshot's publication sequence to the highest sequence a destination's
sync had when it began. Sealing and signing keep using the key generation,
which is what derives keys; only the ordering arithmetic moved. When
multi-writer archives exist, this returns to the index generation
[specification 11 §3.1](../../specifications/repository-format/11-lifecycle-objects.md#31-the-grace-period-is-counted-in-generations-not-in-time)
speaks of; the property preserved is the same — no clock, only visible
advancement.

## Amendment 6 (2026-08) — marking without a staging archive

[ADR-0046](0046-direct-to-destination-publication.md) removes the staging
archive for direct-ship sets, and Amendment 4's placement rewords rather than
moves: **marking still happens where the keys are** — on the hub — but the
keep-set and its closure are computed against the set's **metadata store**
plus the sink, whose blob reads answer from whichever destination holds each
key and whose listings are the union across destinations. The retention
traversal has been proven through that path (ADR-0046's read-paths record).
Every safety mechanism above — intents, generation cut-off, grace counted in
the journal sequence, revalidation — is unchanged, because none of it ever
depended on the marked objects being local, only on the marker holding the
keys and the journal.

What Amendment 4 said about staging's own lifecycle goes moot for these sets:
there is no "object leaves staging" — per-destination convergence is the
deleting half, each destination bounded by its floor exactly as before, and
"deletion may not outrun replication" keeps its force as FR-GC-009's
proof-before-reclaim rule, now guarding the *destinations'* copies since no
local copy exists behind them. A migrated set's leftover staging archive
leaves only by `retire_staging` (ADR-0046, contract 1.18), refused while it
holds a blob the live history reaches that no destination has — never over
one nothing references, which no pass could ever carry (ADR-0046 Amendment
2). Amendment 5's single-writer
grace arithmetic carries over verbatim — the metadata store is single-writer
by the same construction. Compaction was the open question here —
"re-seals in staging and propagates as replication" has no staging to re-seal
in for a direct-ship set — and the compaction record answered it:
[ADR-0067](0067-the-keyless-compactor.md) reads a direct-ship set's candidates
back through the ship sink and writes their replacements through it, so the
hub still does the work and the destinations still receive ordinary objects.
Steps 6 and 9 of this record's algorithm are what make that safe, and the
built pass keeps them in the order this record requires while running the
whole phase after the sweep rather than inside it (architecture 07 §3.3).

## Amendment 7 (2026-09) — the skew margin, configured

Amendment 3 has the duration half of expiry carry "a configured skew margin".
It was not configured. Both places that survey intents added a fixed five
minutes: the collector's pass and the CLI's check. Five minutes absorbs a
clock drifting, not a clock set wrong. Proving NFR-TIME-001 showed what that
would cost. Once an intent's generation has passed, a collector a day ahead
of the writer would expire the intent while its writer was still inside its
declared hour. That cannot happen today only because the key generation,
which expiry is measured in, never advances (Amendment 5).

1. **The margin is an installation setting.** It is
   `clock_skew_margin_hours` in the configuration file, and the file's schema
   rises from 7 to 8. **Absent means a day**, the skew NFR-TIME-001 is proved
   against. The range is an hour to a year, and a value outside it is refused
   at load, by name:
   - zero, because it assumes every clock agrees, which is the one
     assumption the margin exists not to make;
   - a negative value, which would expire an intent before its writer's own
     declared duration had run;
   - more than a year, which no longer absorbs skew but turns expiry off.

   A larger margin costs space and never safety: an abandoned job's blobs
   are held longer, and a live job's are never held shorter.
2. **Read where it is used.** Each retention pass reads the margin afresh, as
   it reads the background window, and surveys intents under it. The pass
   logs the margin beside the generation (event 2904), because together they
   decide which intents are live. What a pass deletes cannot show the margin
   while the generation never advances, so the log is how a person reading it
   afterwards learns which margin was in force.
3. **One margin, not two.** The check counts live intents under the same
   configured margin and names it in its journal line. A check that called
   an intent expired while the collector still honoured it would disagree
   with the thing it checks.
4. **In the file, not yet in the settings verbs.** The window, the limits
   and the pool width are read and set through the service
   ([ADR-0037](0037-configuration-over-the-command-contract.md) Amendment 1).
   The margin is set only in the file, because it binds nothing until a
   generation can pass, and a control that visibly changes nothing reads as
   a broken one. It joins the verbs when key rotation makes it bind. Until
   then `update_service_settings` keeps it, as it keeps every field it does
   not carry.

The tombstone grace needs no margin: it is a publication, not a span of time
(Amendment 5). The other half of NFR-TIME-002, the observed skew recorded in
each snapshot's manifest, is not part of this amendment.

> **Built by [ADR-0077](0077-observed-clock-skew.md) (2026-09).** The
> observed skew is read from a paired peer's signed replication receipt and
> recorded in the manifest of the set's next capture. It is a diagnostic:
> nothing here, the margin included, reads it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-08 | Accepted (amended) | Intent mechanism unchanged. Extended to the collector itself (PT-3, critical); blob identifier formation resolved via ADR-0016 (PT-4); expiry now requires both generation and declared-duration conditions (PT-5). |
| 2026-08 | Accepted (amended) | Amendment 4: the hub marks against staging, destinations are converged on instruction, and deletion never outruns replication ([ADR-0034](0034-hub-and-spoke-destinations.md)). |
| 2026-08 | Accepted (amended) | Amendment 6: for direct-ship sets the hub marks against the metadata store through the sink, convergence is the deleting half, and compaction's placement is deferred to ADR-0025's record ([ADR-0046](0046-direct-to-destination-publication.md)). |
| 2026-09 | Accepted | NFR-TIME-001 proved for GC safety under injected skew: a collector a day ahead, a day behind or a year ahead of the writer sweeps nothing without a publication, keeps the newest snapshot the floor protects, and expires no intent whose generation has not passed (`Retention.Tests/ClockSkewTests`). "None of them a clock" holds for intents only by circumstance: expiry's duration half reads the collector's clock against the writer's stamp over a fixed five-minute margin, and is safe under skew today because the key generation never advances. A configured margin is NFR-TIME-002's |
| 2026-09 | Accepted (amended) | Amendment 7: the skew margin is configured. It is `clock_skew_margin_hours`, a day when absent, where it was a fixed five minutes. Each retention pass and the check read it, and a pass logs it beside the generation it binds against (NFR-TIME-002, `Retention.Tests/ClockSkewTests`, `Hosts.Tests/ClockSkewMarginServiceTests`) |

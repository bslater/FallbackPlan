# 07 — Retention and garbage collection

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §11 · **Resolves:** [C4](../review/2026-08-architecture-review.md#c4--garbage-collection-can-delete-blobs-belonging-to-an-in-flight-snapshot), [C1](../review/2026-08-architecture-review.md#c1--immutable-manifests-embed-physical-locations-that-compaction-changes)

**Built:** The deletion-only collector for staging archives (`FallbackPlan.Retention`): the policy planner with stated reasons (§2), the replication gate (§2.1), the mark over the protected closure, the intent-covered sweep plan with its mandatory dry-run report, and the signed-tombstone → grace-by-publication → revalidate → delete cycle (§3, steps 1–5 and 10–13). Destination convergence (§3.0.1) is built for local-path destinations: fan-out and retention are one filtered convergence, so a destination under an override holds exactly its keep-set's closure. Peer replicas converge the same way on the hub's instruction, floor-bounded at the spoke's edge ([peer-protocol 06](../../specifications/peer-protocol/06-retention.md)). Compaction (steps 6–9) is not built — see [implementation status](../implementation-status.md).

---

## 1. Retention selects; collection deletes

These are separate operations with separate authority, and keeping them separate is what makes the whole model safe.

**Retention** evaluates policy and marks which snapshots remain protected. It deletes nothing.

**Garbage collection** finds objects unreachable from any protected snapshot and — after safety checks and grace periods — removes them.

A retention policy change therefore has no immediate physical effect. It becomes visible as reclaimed space only after a subsequent collection, which is separately authorised, separately reported, and separately reversible during the grace window.

## 2. Retention policy

Policy is declared **per backup set**, with optional **per-destination
overrides** ([ADR-0034](../adr/0034-hub-and-spoke-destinations.md), FR-GC-010):
the set's policy is what its destinations follow unless a destination's entry
narrows or widens it — a small always-on disk keeping four monthly snapshots
while the offsite peer keeps twelve is a configuration, not a fork. The hub
computes every keep-set, because only the hub can read manifests; a destination
never decides what to keep, only what it refuses to drop (its floor, §5).

Composable rules, evaluated against snapshot capture times:

- keep every snapshot for 24 hours;
- keep hourly snapshots for 7 days;
- keep daily snapshots for 30 days;
- keep weekly snapshots for 12 months;
- keep monthly snapshots indefinitely;
- keep tagged snapshots indefinitely;
- keep deleted-file history for a separate, independently configured duration;
- keep at least *N* snapshots regardless of age.

The last rule is a floor that the others cannot override. It exists so that a misconfigured schedule, a clock problem, or a long offline period cannot leave a backup set with nothing.

### 2.1 Retention must not outrun replication

Retention operates per-replica, and so does commit. Left uncoordinated, the two combine to erase history that no replica ever held.

Consider a set keeping hourly snapshots for 7 days locally, replicating to a peer that is offline for a fortnight. On day 8 local retention expires the day-1 snapshots. The peer returns on day 14 and receives whatever remains. Days 1–7 now exist nowhere and never will — and nothing reported a loss, because from each side's local view the configured policy was applied exactly as written ([PT-9](../review/2026-08-fix-pressure-test.md#pt-9--local-retention-can-silently-erase-history-a-destination-never-received)).

The rule is therefore: **retention shall not expire a snapshot that has not reached the destinations its own policy requires**, unless a configured bound on that deferral is exceeded — at which point the resulting history gap is raised as a warning requiring action, never applied silently.

The set's staging archive is allowed to grow past its retention window while a destination is behind. Holding extra snapshots costs disk; expiring them costs history that cannot be recovered. The cheaper failure is the right default. The same gate, run to completion, is what makes staging *trimmable*: once every configured destination holds an object durably, staging may drop it — which is how a set with no local destination stops paying for two local copies ([ADR-0034 §6](../adr/0034-hub-and-spoke-destinations.md#6-the-costs-accepted)).

Deleted-file history is separately configured because it answers a different question. "How far back can I go?" is about snapshot age; "can I still get the file I deleted last spring?" is about how long tombstoned content survives, and users reason about the two independently.

## 3. Garbage collection

Generation-based mark and sweep:

1. Establish a repository **generation cut-off**. Everything published after it is out of scope for this pass.
2. Enumerate all snapshots protected as of that generation.
3. Mark reachable metadata and data objects by walking snapshot → tree → file-version → segment object identifiers, resolving through the index.
4. **Add every blob covered by an unretired [write intent](04-concurrency-and-publication.md#4-write-intent) to the reachable set.**
5. Produce a deletion and compaction plan.
6. **Publish a write intent naming the replacement blobs this pass will create.**
7. Write those replacement blobs, and publish index entries mapping the moved object identifiers to their new locations as **supersessions** ([`02-repository-format.md` §7.2](02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing)). **No manifest is touched.**
8. Publish a new index checkpoint.
9. **Retire the write intent from step 6.**
10. Tombstone superseded blobs.
11. Wait out the configured grace period.
12. Revalidate: confirm no protected snapshot references tombstoned content.
13. Delete eligible objects in bounded batches.

A **dry-run report is mandatory** before any destructive pass, and it states what would be deleted, what would be compacted, how much space would be reclaimed, and which snapshots were treated as protected.

Interruption at any step leaves every published snapshot recoverable. Steps 6–13 are individually resumable and idempotent; re-running a partially completed pass converges rather than compounding.

### 3.0 The collector is a writer

Steps 6 and 9 exist because the collector creates blobs, and **any component that creates a blob publishes an intent first — with no exception for maintenance.**

Without them, a replacement blob is unreferenced between its creation and the publication of its index entries, which is precisely the window step 4 protects writers from. A second collector — permitted, since [`04-concurrency-and-publication.md` §8](04-concurrency-and-publication.md#8-concurrent-maintenance) requires no global exclusive lock — would not mark it and could delete it. The first collector then publishes index entries pointing into a deleted blob and tombstones the originals, destroying both copies of every record in the batch.

That is permanent loss of live data reachable from protected snapshots: the exact failure the write-intent mechanism was introduced to prevent, in the one code path that had not absorbed the lesson ([PT-3](../review/2026-08-fix-pressure-test.md#pt-3--compaction-output-blobs-are-unprotected-between-creation-and-index-publication)).

### 3.0.1 Where the collector runs under hub-and-spoke

Every step above executes against a set's **staging archive**, on the hub,
where the keys and the writer role live ([ADR-0009 Amendment 4](../adr/0009-garbage-collection-safety.md#amendment-4-2026-08--where-the-collector-runs-under-hub-and-spoke)).
Destinations are never collected; they are **converged**: the hub computes what
a destination should hold — that destination's keep-set closure, under its
policy override if it has one — and executes the difference as plain store
operations against a local-path destination, or as deletion instructions to a
peer, who deletes exactly what it is told and nothing else, bounded below by
its own granted floor. A destination's local reachability is never an input,
because a replica's view is exactly the partial view this algorithm exists to
distrust. Compaction likewise runs only in staging and reaches destinations as
ordinary replication ([ADR-0025 Amendment 1](../adr/0025-compaction-reseals-records.md#amendment-1-2026-08--compaction-runs-in-staging-and-propagates)).

### 3.1 Step 4 is the one that matters

Step 4 is the [C4](../review/2026-08-architecture-review.md#c4--garbage-collection-can-delete-blobs-belonging-to-an-in-flight-snapshot) fix. Without it, a writer's blobs are durable but referenced by nothing during the window between upload and index publication — potentially hours on an initial backup — and a collector cannot distinguish them from garbage. The result is data loss inside a snapshot that the user is told completed successfully.

Intent coverage is a *durable, self-describing* statement of what is in flight. It is not a heartbeat, it does not expire because a laptop was closed, and it names the specific blobs it protects.

### 3.2 Step 7 is only possible because of C1

Compaction moves records between blobs, which changes their physical location. It is safe to do that without rewriting history *only* because manifests reference segments by object identifier and never by blob and offset ([`02-repository-format.md` §6.2](02-repository-format.md#62-manifests-hold-logical-facts-only)).

Had physical location stayed in the manifest as originally specified, step 7 would have required rewriting immutable objects — which is to say, it would not have been possible at all.

## 4. Why leases are not load-bearing

Leases exist. They are advisory, and their only job is stopping two collectors from doing the same work at the same time. Losing one costs efficiency and nothing else.

Correctness rests on four things, none of which is a clock or a heartbeat:

| Mechanism | Protects against |
|-----------|------------------|
| Generation cut-off | Racing with concurrent publication |
| Unretired write intents | Sweeping in-flight work |
| Tombstone grace period | Acting on a stale or incomplete view |
| Pre-delete revalidation | Anything the first three missed |

The reasoning for demoting leases — clock skew, eventual consistency, suspension, and the absence of any binding between a lease and the blobs it supposedly protects — is in [`04-concurrency-and-publication.md` §4.3](04-concurrency-and-publication.md#43-why-leases-are-not-enough).

## 5. Destructive-change safeguards

Ransomware and accidental mass deletion look identical to a backup system: a large number of files change or disappear at once. The safeguards are therefore about **not propagating** that quickly, rather than about detecting malice.

- **Flag** unusual deletion or rewrite rates and surface them as a warning requiring action.
- **Never** expire previous snapshots on the basis of source change volume alone. High churn is a reason to keep more history, not less.
- **Destination-side retention floors** that a source device cannot reduce. A compromised source cannot instruct a destination to drop history below its floor.
- **Repository-server policy locks** where a server mediates access.
- **Provider object lock** in a later phase, for destinations that support WORM retention.
- **Stronger authorisation** for retention reduction and bulk snapshot deletion than for ordinary backup.
- **Signed audit records** for every destructive action.

The retention floor is the most valuable of these, because it is the only one that holds when the source device is fully compromised — which is the case that matters. Under hub-planned retention it is enforced at the destination's own edge: a deletion instruction that would take a peer below its granted floor is refused with a stated reason, and the hub records the refusal rather than retrying it (FR-GC-010). The floor is expressed in generations and the policy in wall-clock rules; where they disagree, the destination keeps the union — everything its floor requires *and* everything the policy selects.

None of this substitutes for endpoint security ([`00-overview.md` §4.3](00-overview.md#43-explicit-non-goals-for-the-first-release)), and historical snapshots may contain the malware itself. Restore defaults to a quarantine path for exactly that reason ([`08-restore-and-recovery.md` §3](08-restore-and-recovery.md#3-restore-verification)).

## 6. Healing from replicas

When verification finds a damaged or missing object and another replica holds a good copy, healing fetches it and republishes locally.

Healing is:

- **explicitly invoked** — never a silent side effect of a read or a verification pass;
- **verified** — the fetched object is authenticated and its content identifier confirmed before it is trusted;
- **reported** — what was healed, from where, and what remains damaged;
- **bounded** — damage that cannot be healed from any replica is reported with the affected snapshots and file versions named, rather than retried indefinitely.

Rebuild never repairs ([`02-repository-format.md` §8.3](02-repository-format.md#83-rebuild-never-repairs)). Diagnosis and repair stay separate so that a damage report is always a statement about the repository as it is, not as some automatic process has already altered it.

## 7. Storage-class awareness

Retention and collection consult the store's capability record ([`05-storage-providers.md` §3](05-storage-providers.md#3-capabilities)):

- **`MinimumStorageDuration`** — deleting an object before its minimum duration incurs an early-deletion charge. The plan reports the cost rather than silently paying it.
- **`ArchivalTiers`** — objects in an archival tier need rehydration before they can be read, which affects both verification scheduling and restore time estimates.
- **`ObjectLock`** — a locked object cannot be deleted until its retention expires. The plan reports these as deferred rather than failing.

None of this changes what is *correct* to delete. It changes what is *advisable*, and when — and the user sees the difference in the dry-run report.

---

**Previous:** [06 — Filesystem capture](06-filesystem-capture.md) · **Next:** [08 — Restore and recovery](08-restore-and-recovery.md)

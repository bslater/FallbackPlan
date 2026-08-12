# 04 — Concurrency and publication

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §7.10, §8.1–8.2 · **Resolves:** [C4](../review/2026-08-architecture-review.md#c4--garbage-collection-can-delete-blobs-belonging-to-an-in-flight-snapshot), [C5](../review/2026-08-architecture-review.md#c5--snapshot-commit-is-defined-so-that-one-offline-destination-stalls-all-protection), [C6](../review/2026-08-architecture-review.md#c6--checkpoint-compaction-requires-a-complete-listing-the-design-forbids-relying-on)

**Built:** Publication yes; the collector this section also constrains does not exist — see [implementation status](../implementation-status.md).

---

## 1. Repository ownership modes

**Direct-store mode.** Multiple trusted clients hold store credentials and write immutable objects directly. Appropriate for personal repositories and object stores. This is the mode that makes multi-writer correctness a day-one concern rather than an edge case.

**Repository-server mode.** Clients authenticate to a FallbackPlan Repository Server which authorises devices, quotas, policies, and operations. Clients still encrypt before upload; the server never needs plaintext. Appropriate where store credentials should not be distributed, or where destination-side policy must be enforced against the source's wishes.

Both modes use the same repository format. The server is a gatekeeper, not a different design.

## 2. Writer identity

Each writer holds a device keypair, a repository authorisation grant, a writer ID, and a monotonically increasing journal sequence. Everything it publishes is signed.

The journal sequence is per-writer and gapless. That property does real work in three places: delta chain-walking ([`02-repository-format.md` §7.2](02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing)), rollback detection ([`03-crypto.md` §6](03-crypto.md#6-authentication-of-repository-state)), and write-intent retirement (§4). A duplicate or regressing sequence means identity cloning or rollback and raises a security alert.

## 3. Object visibility rules

Two invariants govern everything below:

> **I1.** A published object never references an object that is not already durable in the same replica.
>
> **I2.** Published objects are immutable. Correction is by publishing new objects, never by rewriting old ones.

I1 is restic's write-ordering rule and it is the most valuable single import from the prior art.

## 4. Write intent

### 4.1 The window

I1 creates a window. A writer uploads blobs, then publishes index deltas, then publishes the snapshot. Between the first blob upload and the delta publication — potentially hours on an initial backup — those blobs are durable in the store and referenced by nothing. To a mark-and-sweep collector they are indistinguishable from garbage.

The original design closed this with leases. That does not hold (§4.3), and the consequence is data loss inside a completed snapshot, discovered at restore.

### 4.2 The record

Before uploading its first blob, a writer publishes a **write-intent record** to `/journal/<writer-id>/<sequence>`:

```text
write-intent {
  writer_id, sequence, issued_at,
  backup_set_id,
  intended_blob_ids  [extended by further intent records as the job grows],
  declared_max_duration,
  expiry_generation
}
```

The rules are then simple:

- The collector treats every blob covered by an **unretired** intent record as reachable. No exceptions, no heuristics.
- The writer **retires** the intent when its snapshot is published.
- An abandoned job's intent expires only when **both** conditions below are met.

Because a job's blob set is not known up front, intents are extended incrementally: a writer publishes an additional intent record naming further blobs before uploading them. The ordering obligation is only that the intent covering a blob is durable **before** that blob is uploaded.

Naming blobs in advance is only possible because **blob identifiers are writer-allocated rather than content-derived** ([`02-repository-format.md` §5.3](02-repository-format.md#53-spooling-and-sealing), [ADR-0016](../adr/0016-blob-identifier-formation.md)). A content-derived identifier cannot be known before the content exists, and this mechanism would be unimplementable.

### 4.2.1 Expiry needs two conditions, not one

An intent expires only when the repository has advanced past `expiry_generation` **and** `declared_max_duration` has elapsed with a configured skew margin.

Either condition alone fails. Generation alone couples one writer's liveness to other writers' activity: generations advance when *others* publish, so a laptop running a three-week initial backup over a domestic uplink can be expired in two days by three siblings backing up hourly, and have its blobs collected mid-job. Wall-clock alone reintroduces the clock dependency §4.3 exists to remove.

`declared_max_duration` is declared by the writer rather than fixed globally, because "the longest permitted job duration" is not a constant — a 4 TB first backup and a 20 MB incremental differ by orders of magnitude, and any single value is either unsafe for the first or wasteful for the second. A writer extends its declaration by publishing an extension. An administrative force-expire exists for genuinely abandoned jobs and is audited ([PT-5](../review/2026-08-fix-pressure-test.md#pt-5--intent-expiry-mixes-generation-and-wall-clock-and-couples-slow-writers-to-busy-repositories)).

### 4.3 Why leases are not enough

A lease is a *liveness* signal, and four independent things break it:

- **Clock skew.** A lease is a timed record and there is no trusted time source. Skew between writer and collector translates directly into blobs swept while in use.
- **Eventual consistency.** The store may simply not show the collector a lease written seconds ago — and the format explicitly permits that.
- **Suspension.** A laptop lid, a suspended VM, or a scheduler hiccup loses a lease while the blobs it covered remain perfectly legitimate.
- **No binding.** Nothing ties a lease to *which* blobs it protects, so a collector cannot act on one except by declining to collect at all.

An intent record has none of these properties: it is durable, self-describing, names its blobs explicitly, and its retirement is an event rather than the absence of a heartbeat.

Leases remain — as an advisory optimisation that stops two collectors doing the same work. They are never load-bearing. See [`07-retention-and-gc.md` §4](07-retention-and-gc.md#4-why-leases-are-not-load-bearing).

## 5. Publication order

A snapshot becomes visible in a replica in exactly this order:

1. **Publish write intent** naming the blobs about to be created.
2. Scan the source and construct file-version and tree objects.
3. Segment, hash, compare, compress, encrypt, and assemble data and metadata blobs.
4. Seal and upload blobs to the replica.
5. Verify acknowledgements; optionally sample-read uploaded ranges.
6. **Publish index deltas** referencing the now-durable blobs.
7. **Publish the signed snapshot manifest** referencing an already-published root tree.
8. **Retire the write intent** and publish the audit/journal record.
9. Mark the local job complete.

Steps 1 and 8 are the additions to the original ordering. Steps 4→6→7 are unchanged, and are the invariant everything else protects.

Readers go the other way: enumerate a stable snapshot set first, then load the index generation needed to resolve it. A reader therefore never observes a snapshot whose objects are not resolvable.

### 5.1 Interruption at each step

| Interrupted after | State | Recovery |
|-------------------|-------|----------|
| 1 | Intent published, no blobs | Intent expires after grace; nothing collectable was written |
| 2 | Scan complete. Single-stream: identical to row 1. Tree path: uploads may already be in flight during the walk, so row 4's state can exist here too | As rows 1 and 4 — the boundary adds no state of its own |
| 3 | Data blobs sealed and durable, metadata not yet flushed | Intent covers what was uploaded; the *mid-step* state — a partial spool, nothing uploaded — is the spool suite's row: resume from spool checkpoint or discard — [`02-repository-format.md` §5.3](02-repository-format.md#53-spooling-and-sealing) |
| 4 | Blobs durable, unreferenced | Intent keeps them reachable; job resumes or blobs expire with the intent |
| 5 | Identical to row 4 — acknowledgements were checked per put | As row 4 |
| 6 | Deltas published, no snapshot | Index entries are harmless; blobs remain intent-covered until retirement or expiry |
| 7 | Snapshot published, intent live | Snapshot is valid and restorable; intent retires on next run or expires |
| 8 | Publication complete: snapshot durable, intent retired | The caller sees a failure over committed work; the live catalogue may be unprojected, which costs nothing — it is a cache |
| 9 | Complete | — |

No interruption at any step can make a previously committed snapshot unreadable (NFR-REL-001), and none can leave a published snapshot referencing a collectable blob.

The boundaries are not the only interruption points: a store can die at *every individual put* inside a step. The put-budget sweep (`StorePutSweepTests`, and its tree twin in `TreeSnapshotInterruptionTests`) kills the publication after each put in turn and holds the same three claims at all of them — nothing durable is collectable, no partial snapshot can exist, and a fresh process completes with no repair. Cancellation is the same matrix by another door: a cancelled publication lands in the row its progress had reached (`CancellationTests`), with in-flight uploads drained and intent-covered.

## 6. Commit versus replication

### 6.1 The distinction

**Commit** is per-replica. A snapshot is committed to a replica once every object it references is durable *in that replica*, following §5. This is a local invariant, always achievable, and it is what makes a replica independently restorable.

**Replication** is separate state. Each `(snapshot, destination)` pair carries its own status:

| Status | Meaning |
|--------|---------|
| `pending` | Not yet started for this destination |
| `replicating` | Transfer in progress |
| `durable` | All referenced objects durable at the destination |
| `verified` | Durability independently confirmed — [`09-replication-and-peers.md` §5](09-replication-and-peers.md#5-destination-verification) |
| `degraded` | Previously durable, now failing verification or partially missing |

### 6.2 Why they were separated

The original FR-SNP-001 required publication "only after all required blobs and index deltas are durable". Read against a multi-destination policy, "required" makes a snapshot hostage to the least available destination: a peer switched off for a fortnight's holiday means no snapshot is published for a fortnight. Local protection that is working perfectly is withheld because a remote destination is unavailable — and because there is then no recent snapshot to compare against, the eventual catch-up costs far more than a series of incrementals would have.

Under the split, a snapshot commits locally and is immediately protective. It becomes *policy-compliant* when its destinations catch up.

### 6.3 Policy evaluation

A backup set declares one or more named destinations — none of which has to be local ([ADR-0034](../adr/0034-hub-and-spoke-destinations.md)) — and its durability policy is evaluated over their replication state. Commit itself is always against the set's **staging archive** on the hub, which is what keeps capture unconditional; staging is internal and no policy counts it:

```text
Snapshot captured when:
  - committed to the set's staging archive

Snapshot protected when:
  - at least one destination outside the source's failure domain: durable

Snapshot policy-compliant when:
  - every destination the set's policy requires: durable

Snapshot healthy when (example policy):
  - a local-path destination: verified within 7 days,  and
  - a peer destination:       verified within 30 days, and
  - a cloud destination:      durable within 24 hours
```

This gives the status model in [`10-observability.md`](10-observability.md) something truthful to say. "Captured, waiting on the offsite copy" is a real state that the original design could not express — it would have shown no recent backup at all.

### 6.4 `protected` requires an independent failure domain

Decoupling commit from replication was necessary, but it must not make the reassuring word mean less than a user assumes. A local repository on the same disk as the source data is not a backup — the disk fails and both copies go with it.

Replicas therefore declare a **failure domain**, and `protected` requires at least one replica whose domain is disjoint from the source's:

| Domain | Example | Independent of source? |
|--------|---------|------------------------|
| `same-volume` | Repository directory on the source volume | No |
| `same-machine` | Second internal disk | Partially — survives disk failure, not theft, fire, or ransomware |
| `same-site` | NAS or peer on the same LAN | Survives machine loss, not site loss |
| `independent` | Offsite peer, cloud store | Yes |

A snapshot committed only to staging, or replicated only to a same-volume destination, is `captured`, never `protected` — the staging archive shares the source's domain by construction and never counts ([ADR-0018 Amendment 1](../adr/0018-replica-failure-domains.md#amendment-1-2026-08--the-domain-is-declared-per-configured-destination)). The first-run flow warns when all of a set's configured destinations share a failure domain with the source.

Without this, the most common consumer setup — accept the default local repository, never bring the offsite peer online — reads as `protected` right up until the disk dies. That is the "consumer UI hides degraded state → false confidence" risk the original proposal named as a major risk, and it would have been reintroduced by the fix for it ([PT-8](../review/2026-08-fix-pressure-test.md#pt-8--protected-does-not-require-a-replica-outside-the-sources-failure-domain), [ADR-0018](../adr/0018-replica-failure-domains.md)).

## 7. Time and clock skew

No component treats wall-clock time as authoritative for correctness. Specifically:

- **GC safety** depends on generations, intent records, and grace periods — never on comparing timestamps across machines (§4.3).
- **Ordering** within a writer is by journal sequence, not timestamp.
- **Retention** does use wall-clock time, because "keep daily snapshots for 30 days" is inherently a wall-clock policy. It is applied to the *capture* timestamps recorded in snapshot manifests, and a snapshot whose recorded time is implausible relative to its neighbours is flagged rather than silently expired.
- Snapshot manifests record **observed clock skew** where a peer or store exposes a time reference, so a device with a badly wrong clock is diagnosable after the fact.
- Grace periods are expressed with enough margin to absorb realistic skew, and the margin is a configured value rather than an assumption.

## 8. Concurrent maintenance

Multiple writers may run backup jobs concurrently with no coordination — this is the normal case, not an exceptional one.

Maintenance operations (compaction, garbage collection, healing) take advisory leases to avoid duplicated work, and are individually safe to run concurrently regardless:

- **Checkpoint compaction** — conflicting same-generation checkpoints are merged, not elected ([`02-repository-format.md` §7.2](02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing)).
- **Blob compaction** — republishes index entries only; manifests are untouched, so two compactors converge ([`02-repository-format.md` §6.2](02-repository-format.md#62-manifests-hold-logical-facts-only)).
- **Garbage collection** — bounded by generation cut-off, intent coverage, and grace periods, all of which are evaluated independently by each collector ([`07-retention-and-gc.md` §3](07-retention-and-gc.md#3-garbage-collection)).

No routine operation requires a global exclusive lock. That was an explicit improvement target over restic ([`00-overview.md` §5.3](00-overview.md#53-restic)) and the mechanisms above are what deliver it.

## 9. Two different concurrencies, and why conflating them is dangerous

Everything above concerns **repository-level concurrency**: many *devices*
writing one repository, each with its own writer identity, coordinating through
immutable objects, write intents and generation precedence rather than through
locks. That is designed in, deliberately lock-free, and is the normal case.

**Local process concurrency is a different question with a different answer.**
A writer in §2 is a *device* — a keypair, an authorisation grant, a writer ID and
a journal sequence. Nothing in that definition says how many *processes* on one
machine may hold it, and the answer is exactly one:

> **A device's writer role is held by one process at a time.** While a service
> is running it is that process; any other local process is a client of it
> ([ADR-0028](../adr/0028-service-boundary-and-deployment-topologies.md)).

Under [ADR-0034](../adr/0034-hub-and-spoke-destinations.md) the hub holds one
such archive — and therefore one writer role, one gapless sequence, one spool —
**per backup set**, all inside the one process the state-directory lock
protects. The rule's arithmetic changes; the rule does not. Destinations never
hold a writer role at all: every object at a destination was sealed in a
staging archive and copied there, so nothing at a destination ever allocates a
sequence number ([ADR-0034 §3](../adr/0034-hub-and-spoke-destinations.md)).

The rule is not fastidiousness. Two processes sharing a state directory share a
writer identity, and therefore share the single monotonic gapless sequence
space §2 requires. Duplicate allocations from it collide on blob identity,
defeat the store's idempotent-retry handling so that a write intent is *reported
durable when it was never written*, and let one process publish void deltas for
sequence numbers another is still using — durable index damage under a valid
signature. And because §2 classifies a duplicate or regressing sequence as
identity cloning, the first symptom is [T-18](../threat-model.md)'s security
alert: an alarm built for a stolen device key, raised by a user running two
commands at once.

So the two rules coexist without contradiction:

| | Repository level | Local process level |
|---|---|---|
| Unit | Device (writer identity) | Process |
| Concurrency | Many, uncoordinated, normal | One holds the writer role |
| Mechanism | Immutable objects, intents, precedence | Exclusive lock on the state directory |
| Lock scope | None — §8's improvement target | The state directory only; never the repository |

The exclusion is on the **state directory**, because that is what carries the
writer identity. The repository itself stays lock-free, so §8's guarantee — no
routine operation requires a global exclusive lock — is untouched: a second
device may still back up to the same repository at the same moment, from
anywhere, with no coordination at all.

---

**Previous:** [03 — Cryptography](03-crypto.md) · **Next:** [05 — Storage providers](05-storage-providers.md)

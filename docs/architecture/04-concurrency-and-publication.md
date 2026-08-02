# 04 — Concurrency and publication

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §7.10, §8.1–8.2 · **Resolves:** [C4](../review/2026-08-architecture-review.md#c4--garbage-collection-can-delete-blobs-belonging-to-an-in-flight-snapshot), [C5](../review/2026-08-architecture-review.md#c5--snapshot-commit-is-defined-so-that-one-offline-destination-stalls-all-protection), [C6](../review/2026-08-architecture-review.md#c6--checkpoint-compaction-requires-a-complete-listing-the-design-forbids-relying-on)

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
  expiry_generation
}
```

The rules are then simple:

- The collector treats every blob covered by an **unretired** intent record as reachable. No exceptions, no heuristics.
- The writer **retires** the intent when its snapshot is published.
- An abandoned job's intent expires only after a grace period exceeding the longest permitted job duration. Its blobs are then genuinely unreferenced and collectable.

Because a job's blob set is not known up front, intents are extended incrementally: a writer publishes an additional intent record naming further blobs before uploading them. The ordering obligation is only that the intent covering a blob is durable **before** that blob is uploaded.

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
| 3 | Partial spool, nothing uploaded | Resume from spool checkpoint or discard — [`02-repository-format.md` §5.3](02-repository-format.md#53-spooling-and-sealing) |
| 4 | Blobs durable, unreferenced | Intent keeps them reachable; job resumes or blobs expire with the intent |
| 6 | Deltas published, no snapshot | Index entries are harmless; blobs remain intent-covered until retirement or expiry |
| 7 | Snapshot published, intent live | Snapshot is valid and restorable; intent retires on next run or expires |
| 8 | Complete | — |

No interruption at any step can make a previously committed snapshot unreadable (NFR-REL-001), and none can leave a published snapshot referencing a collectable blob.

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

A backup set declares its durability policy over replication state:

```text
Snapshot protected when:
  - local repository: durable

Snapshot policy-compliant when:
  - local repository:  durable, and
  - at least one peer: durable

Snapshot healthy when:
  - local repository:  verified within 7 days,  and
  - trusted peer:      verified within 30 days, and
  - cloud replica:     durable within 24 hours
```

This gives the status model in [`10-observability.md`](10-observability.md) something truthful to say. "Protected locally, waiting on the offsite copy" is a real state that the original design could not express — it would have shown no recent backup at all.

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

---

**Previous:** [03 — Cryptography](03-crypto.md) · **Next:** [05 — Storage providers](05-storage-providers.md)

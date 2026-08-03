# 08 — Journal

**Normative.** Derived from [`04-concurrency-and-publication.md` §4](../../docs/architecture/04-concurrency-and-publication.md#4-write-intent) and [ADR-0009](../../docs/adr/0009-garbage-collection-safety.md).

---

## 1 Purpose

The journal at `/journal/<writer-id>/<sequence>` carries per-writer records that make in-flight work visible to other participants.

Its central job is closing a window that the publication order necessarily creates. A writer uploads blobs, then publishes index deltas, then publishes a snapshot. Between the first upload and the delta publication — potentially hours on an initial backup — those blobs are durable in the store and referenced by nothing. To a garbage collector walking reachability, they are indistinguishable from garbage.

**A write-intent record is the durable statement that they are in flight.**

## 2 Record framing

Journal records are metadata records ([04](04-record.md)) stored as standalone objects rather than inside blobs, because they must be readable before any blob exists.

| Key | Type | Value |
|-----|------|-------|
| 1 | u16 | `record_kind` — 1 write-intent, 2 intent-extension, 3 intent-retirement, 4 audit |
| 2 | bytes[16] | `writer_id` |
| 3 | u64 | `sequence` |
| 4 | u64 | `issued_at` |
| 5 | map | `payload` — per kind, §3–§6 |
| 6 | bytes[64] | `signature` — Ed25519 over the canonical encoding of keys 1–5; semantics as [06 §6.1](06-manifests.md#61-signature). The record carries no generation field, so a reader verifies against the signing key of each generation from the key bundle's current value downward and accepts the first that verifies — generations are few, monotonic, and enumerable from the bundle |

`sequence` shares the writer's single monotonic gapless sequence space with index deltas ([07 §4](07-index.md#4-sequence-gaps-and-void-deltas)). One sequence per writer, not one per record type — a gap is then detectable regardless of which kind of record is missing.

## 3 Write intent

`record_kind = 1`.

| Key | Type | Value |
|-----|------|-------|
| 1 | bytes[16] | `backup_set_id` |
| 2 | array | `intended_blob_ids` — array of bytes[16] |
| 3 | u64 | `declared_max_duration_ms` |
| 4 | u64 | `expiry_generation` |
| 5 | u16 | `purpose` — 1 backup, 2 compaction, 3 healing, 4 import |

### 3.1 The ordering obligation

**A writer MUST NOT upload a blob before an unretired intent naming that blob is durable in the store.**

This is the whole mechanism. Everything else about intents is bookkeeping.

Naming blobs in advance is possible only because blob identifiers are writer-allocated rather than content-derived ([02 §4](02-identifiers.md#4-blob-identifier)). A content-derived identifier cannot be known before the content exists, and this mechanism would be unimplementable.

### 3.2 Collectors are writers

`purpose = 2` exists because the garbage collector creates blobs during compaction, and **any component that creates a blob publishes an intent first, with no exception for maintenance**.

Without this, a replacement blob is unreferenced between its creation and the publication of its index entries — exactly the window intents protect writers from. A second concurrent collector, permitted because no routine operation requires a global exclusive lock, would not mark it and could delete it. The first collector then publishes index entries pointing into a deleted blob and tombstones the originals, destroying both copies of every record in the batch. → [PT-3](../../docs/review/2026-08-fix-pressure-test.md#pt-3--compaction-output-blobs-are-unprotected-between-creation-and-index-publication)

## 4 Intent extension

`record_kind = 2`. A job's blob set is rarely known up front.

| Key | Type | Value |
|-----|------|-------|
| 1 | u64 | `extends_sequence` — the original intent's sequence |
| 2 | array | `additional_blob_ids` |
| 3 | u64 | `declared_max_duration_ms` — revised, MAY extend the original |

The ordering obligation of §3.1 applies to each extension independently: the extension naming a blob must be durable before that blob is uploaded.

## 5 Intent retirement

`record_kind = 3`.

| Key | Type | Value |
|-----|------|-------|
| 1 | u64 | `retires_sequence` |
| 2 | u16 | `outcome` — 1 completed, 2 abandoned, 3 force-expired |

A writer retires its intent once the work it covered is reachable — for a backup, after the snapshot manifest is published; for compaction, after the index entries are durable.

Retirement is an **event**, not the absence of a heartbeat. That distinction is why intents work where leases do not.

## 6 Audit record

`record_kind = 4`. Written for destructive operations: retention reduction, bulk snapshot deletion, garbage collection passes, force-expiry.

| Key | Type | Value |
|-----|------|-------|
| 1 | u16 | `action` |
| 2 | text | `actor` |
| 3 | map | `parameters` |
| 4 | u64 | `objects_affected` |

Audit records make destructive actions attributable. They do not make them preventable.

## 7 Expiry

An intent expires only when **both** conditions hold:

1. the repository's current generation exceeds `expiry_generation`; **and**
2. `declared_max_duration_ms` has elapsed since `issued_at`, plus a configured skew margin.

Either alone is wrong.

**Generation alone** couples one writer's liveness to other writers' activity. Generations advance when *others* publish, so a laptop running a three-week initial backup over a domestic uplink can be expired in two days by three siblings backing up hourly — and have its blobs collected mid-job.

**Wall-clock alone** reintroduces the clock dependency the design exists to remove.

The duration is declared by the writer rather than fixed globally, because a 4 TB first backup and a 20 MB incremental have no single safe constant between them. → [PT-5](../../docs/review/2026-08-fix-pressure-test.md#pt-5--intent-expiry-mixes-generation-and-wall-clock-and-couples-slow-writers-to-busy-repositories)

### 7.1 Force expiry

An operator MAY force-expire an intent whose writer is genuinely gone. This MUST write an audit record and MUST NOT be automatic — a heuristic that decides a writer is dead is a heuristic that will eventually decide a slow writer is dead.

## 8 Collector obligations

A collector MUST:

- enumerate `/journal/` before marking, and treat **every blob covered by an unretired, unexpired intent as reachable** — no exceptions, no heuristics;
- publish its own intent before creating compaction output (§3.2);
- treat an intent it cannot parse as **live**, not as absent.

The last rule matters. An unparseable intent means the collector is older than the writer, or the record is damaged. Both call for the conservative reading: failing to collect wastes space, and collecting wrongly loses data.

## 9 Leases

`/leases/<scope>/<lease-id>` holds advisory coordination records so two collectors do not duplicate work.

**No correctness property may depend on a lease.** Losing one costs efficiency and nothing else.

Leases are not load-bearing because four things independently break them: clock skew with no trusted time source; eventual consistency, which may simply not show a collector a lease written seconds ago; suspension, where a closed laptop lid loses a lease while its blobs remain legitimate; and the absence of any binding between a lease and the blobs it supposedly protects.

An intent has none of those properties — it is durable, self-describing, names its blobs explicitly, and its retirement is an event. → [`04-concurrency-and-publication.md` §4.3](../../docs/architecture/04-concurrency-and-publication.md#43-why-leases-are-not-enough)

## 10 Publication order

For reference, the full order a snapshot becomes visible in ([`04-concurrency-and-publication.md` §5](../../docs/architecture/04-concurrency-and-publication.md#5-publication-order)):

| Step | Action |
|------|--------|
| 1 | Publish write intent naming the blobs about to be created |
| 2 | Scan source; construct file-version and tree objects |
| 3 | Segment, hash, compare, compress, encrypt, assemble blobs |
| 4 | Seal and upload blobs |
| 5 | Verify acknowledgements |
| 6 | Publish index deltas referencing the now-durable blobs |
| 7 | Publish the signed snapshot manifest referencing an already-published root tree |
| 8 | Retire the write intent; publish the audit record |
| 9 | Mark the local job complete |

The invariant underneath: **every object a published object references is already durable.** Steps 4 → 6 → 7 are the ordering that guarantees it.

---

**Previous:** [07 — Index](07-index.md) · **Next:** [09 — Segmentation](09-segmentation.md)

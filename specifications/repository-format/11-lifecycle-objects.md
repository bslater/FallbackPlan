# 11 — Lifecycle objects

**Normative.** Derived from [`07-retention-and-gc.md`](../../docs/architecture/07-retention-and-gc.md) and [ADR-0009](../../docs/adr/0009-garbage-collection-safety.md).

---

## 1 What these are

Three namespaces in [01 §2](01-object-layout.md#2-namespace) belong to the collector, and until now none of them had an object format:

```text
/leases/<scope>/<lease-id>
/tombstones/<object-type>/<object-id>
/audit/<period>/<record-id>
```

Nothing before phase 4 writes any of them — no component takes a lease, tombstones an object, or writes an audit period — so their shapes were deliberately left uninvented rather than guessed at ([Q17](../../docs/open-questions.md#closed)). They are specified here, ahead of the collector, so that the collector is written against a format instead of establishing one by accident.

All three are **standalone metadata records**: the `FBPKSREC` framing of [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md) §Decision 1, sealed under the metadata key like an index delta or a journal record, with the object types [02 §3.1](02-identifiers.md#31-object-types) assigns — lease `0x0D`, tombstone `0x0E`, audit-period record `0x0F`.

Only one of the three is signed, and the difference is the point. A **tombstone authorises a deletion**, so it carries an Ed25519 signature and a reader verifies it before acting. A lease and an audit record authorise nothing; AEAD under the metadata key already establishes that a repository member wrote them, and a signature would imply an authority they do not have.

## 2 Lease

`/leases/<scope>/<lease-id>` is the **only mutable namespace in the format** ([01 §4](01-object-layout.md#4-object-immutability)). A lease may be overwritten by its holder and deleted by anyone.

`<scope>` is 16 opaque bytes rendered as 26 lowercase base32 characters, naming what is being coordinated over — the collector defines its own scope allocation, and this format requires only that the value be keyed or opaque ([01 §2.1](01-object-layout.md#21-what-keys-must-not-reveal)). `<lease-id>` is 16 CSPRNG bytes rendered the same way.

```text
lease = {
    1: u16       schema version, 1
    2: bytes[16] holder_writer_id
    3: bytes[16] scope
    4: u64       acquired_at    the holder's clock, epoch milliseconds
    5: u64       expires_at     the holder's clock, epoch milliseconds
    6: u16       purpose        1 mark, 2 sweep, 3 compaction
}
```

### 2.1 What a lease may not be used for

**No correctness property may depend on a lease.** Losing one costs efficiency and nothing else. In particular a collector:

- MUST NOT treat the absence of a lease as permission to delete anything. Deletion is authorised by a tombstone whose grace has expired and by revalidation (§3), never by a lease;
- MUST NOT treat the presence of another holder's lease as proof that work is under way — only as a reason to prefer other work;
- MUST NOT extend a grace period, a retention decision, or an expiry on a lease's authority.

Four independent things break leases, and they are why the rule is absolute: clock skew with no trusted time source; eventual consistency, which may simply not show a collector a lease written seconds ago; suspension, where a closed laptop lid loses a lease while its blobs remain legitimate; and the absence of any binding between a lease and the blobs it supposedly protects. → [08 §9](08-journal.md#9-leases), [`04-concurrency-and-publication.md` §4.3](../../docs/architecture/04-concurrency-and-publication.md#43-why-leases-are-not-enough)

`expires_at` is therefore advisory in the same sense. A reader MAY ignore an unexpired lease and MUST NOT refuse otherwise-correct work because one exists.

## 3 Tombstone

`/tombstones/<object-type>/<object-id>` records that an object has been marked for deletion and when it becomes eligible. `<object-type>` is the two-lowercase-hex-digit rendering of the [02 §3.1](02-identifiers.md#31-object-types) type — `0d`, not `13` — and `<object-id>` is the 52-character base32 rendering of the object identifier.

```text
tombstone = {
    1: u16       schema version, 1
    2: u8        object_type    the type of the object being deleted
    3: bytes[32] object_id      or bytes[16] for a blob (§3.1)
    4: u16       reason         1 unreferenced, 2 retired delta, 3 compacted, 4 superseded
    5: bytes[16] writer_id      who marked it
    6: u64       tombstoned_at  informational; see below
    7: u64       eligible_generation
    8: bytes[64] signature      Ed25519 over the canonical encoding of keys 1-7
}
```

The signature has the semantics of [06 §6.1](06-manifests.md#61-signature): repository-scoped, verified against the derived signing key for the generation in force. A reader MUST verify it before treating the tombstone as authorisation, and MUST treat a tombstone that fails verification as a **security finding** rather than a damage finding — an unsigned or forged tombstone is an attempt to have someone else delete data.

### 3.1 The grace period is counted in generations, not in time

`eligible_generation` is the index generation at or after which the delete may proceed. **There is no trusted clock in this format**, so a grace period measured in wall time is a grace period an adversary can end early by moving a clock. Generations advance only when a writer publishes, are monotonic, and are visible to every participant, which makes them the one ordering every participant agrees on. `tombstoned_at` is recorded for operators reading a diagnostic bundle and MUST NOT be used to decide eligibility.

`object_id` is 32 bytes for every object type except a blob, whose identifier is 16 ([02 §4](02-identifiers.md#4-blob-identifier)). A reader MUST validate the width against `object_type` and refuse a mismatch.

### 3.2 What a collector must do before deleting

A collector MUST NOT delete an object unless **all** of the following hold:

1. a tombstone for it exists and its signature verifies;
2. the repository's current generation ([ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md) §Decision 5) is at or above `eligible_generation`;
3. the object is revalidated as unreferenced **immediately before** the delete, against a snapshot set and index state read after step 2.

Step 3 is not redundant. A tombstone records a decision taken at some earlier moment, and a snapshot published since then may reference the object — a write intent covering it is exactly the case [ADR-0009](../../docs/adr/0009-garbage-collection-safety.md) exists for. A collector that trusts its own earlier decision deletes live data.

A reader that finds a tombstone for an object that is still referenced MUST report a damage finding and MUST NOT delete. That is the signal that a collector's liveness analysis and the object graph disagree, and it is not the reader's to resolve.

After a successful delete, the tombstone itself becomes eligible for deletion one generation later. It is retained that long so a concurrent reader that saw the object disappear can tell a completed collection from a missing object.

## 4 Audit-period record

`/audit/<period>/<record-id>` holds the repository's durable operational history. It is **distinct from the audit journal record** (kind 4, [08 §6](08-journal.md#2-record-framing)), which is per writer and lives under `/journal/`: a journal record is part of a writer's own sequence and shares its lifecycle, while an audit period is repository-scoped and outlives every intent it describes.

`<period>` is the period's start instant as a zero-padded 16-digit decimal `u64` of epoch milliseconds, so lexicographic key order matches chronological order ([01 §2](01-object-layout.md#2-namespace)). `<record-id>` is 16 CSPRNG bytes rendered as 26 lowercase base32 characters. The period length is an implementation choice; a writer MUST NOT assume another writer chose the same one.

```text
audit_period = {
    1: u16       schema version, 1
    2: u64       period_start   epoch milliseconds, equal to <period>
    3: u64       period_end     epoch milliseconds, exclusive
    4: bytes[16] writer_id
    5: array     events
}

event = [ kind, at, count ]
```

| `kind` | Meaning | What `count` is |
|--------|---------|-----------------|
| 1 | Snapshots published | Snapshots |
| 2 | Blobs written | Blobs |
| 3 | Objects tombstoned | Objects |
| 4 | Objects deleted | Objects |
| 5 | Blobs compacted | Blobs |
| 6 | Verification passes completed | Passes |
| 7 | Damage findings raised | Findings |

A reader MUST accept an unknown `kind` and ignore that event, because the vocabulary is expected to grow and a period record is not something a reader should refuse wholesale over one entry it does not recognise. Every other field is mandatory.

### 4.1 What an audit record must not contain

An audit-period record MUST NOT contain a file path, a file name, a directory name, a device name, a user name, or any plaintext content hash. It carries counts and already-keyed identifiers, and nothing else.

The reason is that this namespace is the most likely thing in the repository to be exported: it is what a diagnostic bundle wants, what a support request attaches, and what an operator reads without thinking about it. → [`10-observability.md`](../../docs/architecture/10-observability.md), NFR-PRIV-002

It carries no signature. It authorises nothing, and it describes what one writer did rather than what the repository is; the AEAD tag establishes that a member wrote it, which is what an operational record needs.

## 5 What this section does not settle

Named so a phase-4 implementer does not read silence as completeness:

- **Scope allocation for leases.** The format requires `<scope>` to be opaque; which unit of work a collector leases over — a shard, a generation, the whole repository — is the collector's design, not the format's.
- **Period length for audit records.** Any writer may choose any period, and readers merge across writers by time range rather than by matching periods.
- **Retention of audit periods.** Nothing here says how long they are kept. That is a policy question, and it is the one place in this file where the answer plausibly differs between a household and a managed estate.

---

**Previous:** [10 — Compression](10-compression.md) · **Back to:** [specification index](README.md)

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

A fourth namespace is specified here too, and it belongs to nobody's collector:

```text
/config/<backup-set-id>/<recorded-at>/<config-id>
```

It is here because it shares the shape — a standalone record with its own lifecycle, referenced by no manifest — and not because it shares the purpose. §5 gives it in full.

Nothing before phase 4 writes any of them — no component takes a lease, tombstones an object, or writes an audit period — so their shapes were deliberately left uninvented rather than guessed at ([Q17](../../docs/open-questions.md#closed)). They are specified here, ahead of the collector, so that the collector is written against a format instead of establishing one by accident.

All four are **standalone metadata records**: the `FBPKSREC` framing of [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md) §Decision 1, sealed under the metadata key like an index delta or a journal record, with the object types [02 §3.1](02-identifiers.md#31-object-types) assigns — lease `0x0D`, tombstone `0x0E`, audit-period record `0x0F`, set configuration `0x10`.

Two of the four are signed, and the difference is the point. A **tombstone authorises a deletion**, so it carries an Ed25519 signature and a reader verifies it before acting. A **set-configuration object tells a rebuilt machine what to protect and what to delete**, which is authority of the same kind, so it is signed too (§5.4). A lease and an audit record authorise nothing; AEAD under the metadata key already establishes that a repository member wrote them, and a signature would imply an authority they do not have.

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

A **blob** has no [02 §3.1](02-identifiers.md#31-object-types) type — its identifier is writer-allocated, not content-derived ([02 §4](02-identifiers.md#4-blob-identifier)) — so a blob tombstone uses the type digits `07`: the reserved code that is already the blob domain in the store-key derivation ([02 §4.3](02-identifiers.md#43-not-leaking-writer-identity)) and that no record's `object_type` can ever carry. Its `<object-id>` is the 26-character base32 rendering of the 16-byte blob identifier, and key 3 below carries the 16 bytes — the width rule of §3.1.

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

In a **single-writer archive** — a hub's staging archive is one by construction — the generation this field counts MAY be realised as the writer's journal sequence, which is that archive's per-publication monotonic and is visible in cleartext as each standalone record's counter. The property is the same either way: eligibility arrives only with visible advancement, never with a clock. → [ADR-0009 Amendment 5](../../docs/adr/0009-garbage-collection-safety.md#amendment-5-2026-08--the-grace-generation-realised)

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

## 5 Set-configuration object

`/config/<backup-set-id>/<recorded-at>/<config-id>` records **what a backup set was
configured to do**, so a machine rebuilt from nothing can resume doing it.

Everything else in the repository describes the data. This describes the *operation* — the
roots, the rules, the schedule, the retention policy — and it exists because none of that
survives the machine. A recovering device can already read its history from a replica
([peer-protocol 07](../peer-protocol/07-retrieval.md)); without this object it can read that
history and never protect anything again, which is the failure
[`08-restore-and-recovery.md` §6](../../docs/architecture/08-restore-and-recovery.md#6-what-must-survive-a-clean-machine)
warns about. → [ADR-0047](../../docs/adr/0047-recovering-operation-after-total-loss.md), FR-DR-006

### 5.1 The payload is sealed, not merely encrypted

The record's outer framing is sealed under the metadata key like every other standalone
record, so a writer can locate, order and replace these objects as part of ordinary
operation. **The configuration itself is sealed a second time**, to an asymmetric recipient
key, and only a holder of the passphrase opens it.

The second layer is not redundancy. A format-v2 repository deliberately grants its own
service the whole structure plane ([ADR-0042](../../docs/adr/0042-write-only-repositories.md)) —
so a single-layer record under the metadata key would hand a compromised write-only hub the
user's folder layout, schedule and rules, which is precisely the class of thing v2 exists to
withhold. Sealed, the hub writes an envelope it can never open, exactly as it does for file
contents.

The recipient key is one both formats already have:

| Format | Recipient | Writer obtains it | Recoverer obtains it |
|--------|-----------|-------------------|----------------------|
| v2 | The descriptor's `fbp/seal/v2` X25519 public key ([03 §5](03-keys.md#5-per-blob-keys)) | Public, in the descriptor it already reads | Re-derives the scalar from the passphrase |
| v1 | X25519 derived from the master key under `"fbp/recovery/v1"` ([03 §4](03-keys.md#4-derived-keys)) | Holds the master key, derives both halves | passphrase → KEK → `/keys/` → master key |

The v1 walk works over the wire because `/keys/` is servable within an authorised replica
([peer-protocol 07 §4](../peer-protocol/07-retrieval.md#4-authorization)).

Sealing uses the payload envelope construction of
[ADR-0042](../../docs/adr/0042-write-only-repositories.md) — ephemeral X25519 share, AEAD
under the shared secret — with the AAD `"fbp/config/v1"`.

### 5.2 Outer record

```text
configuration_record = {
    1: u16       schema version, 1
    2: bytes[16] backup_set_id       equal to <backup-set-id>
    3: u64       recorded_at         epoch milliseconds, equal to <recorded-at>
    4: u32       signing_generation  which signing key signed key 5
    5: bytes     envelope            the sealed configuration of §5.3
    6: bytes[64] signature           over the canonical encoding of keys 1-5
}
```

Key 4 is present for the same reason a snapshot manifest carries
`publication_generation` ([06 §6](06-manifests.md#6-snapshot-manifest) key 13): signing keys
are generational ([03 §4.1](03-keys.md#41-generations)), and a verifier that has to guess
which generation signed a record cannot verify it at all. It names the generation, and the
verifier derives that key.

Keys 2 and 3 repeat outside the envelope so a reader can **order and select without opening
it** — a recovering device lists the prefix, takes the last, and only then needs the
passphrase. Neither field leaks: `backup_set_id` is already a key component of
`/snapshots/`, and a modification time is already inferable from any store's own metadata.

### 5.3 Sealed configuration

```text
configuration = {
    1: u16       schema version, 1
    2: bytes[16] backup_set_id
    3: text      set_name
    4: array     roots            [ [label, path], ... ] in raw-UTF-8 label order
    5: array     include_rules    rules-v1 ([06 §7.1](06-manifests.md#71-rule-dialect-rules-v1))
    6: array     exclude_rules
    7: text      schedule         absent means manual-only
    8: map       retention        absent means retention deferred
}
```

`roots` carries each label **with the path it had on the machine that wrote it**. The path is
a recovery *hint*, never an instruction: a rebuilt machine MUST present it for confirmation
rather than capture from it, because the new machine's layout may legitimately differ and
capturing the wrong tree under a name that says otherwise is worse than asking. The label is
authoritative — it is what the snapshot tree is keyed by ([ADR-0040](../../docs/adr/0040-multi-root-backup-sets.md)).

### 5.4 What it MUST NOT contain

A set-configuration object MUST NOT contain **destinations** in any form: no name, no kind,
no path, no endpoint, no fingerprint, no quota.

The repository is held *by* the destinations. [ADR-0034 §5](../../docs/adr/0034-hub-and-spoke-destinations.md)
keeps the destination list in local configuration as a privacy statement — the configuration
"names who stores your backups and where" — and FR-DEST-006 states it normatively. Sealing
defeats today's reader, but defence in depth is the point: a future weakness in the sealing
scheme, or a compromised passphrase, must not additionally hand over the household's network
of peers from repository bytes alone. A recovering device gets its destinations from the
recovery kit, which the user holds and no peer does ([recovery-kit §2](../recovery-kit/README.md)).

It MUST NOT contain store credentials, the passphrase, or any key material, for the reasons
[08 §4.2](../../docs/architecture/08-restore-and-recovery.md#42-what-is-deliberately-excluded)
gives for the kit.

### 5.5 Signature

Ed25519 over the canonical encoding of the map containing keys 1–5, using the signing key for
the generation named in key 4 ([03 §4](03-keys.md#4-derived-keys)) — the same construction
and the same verification duty as a snapshot manifest
([06 §6.1](06-manifests.md#61-signature)), including that a bad signature is a **security
finding** rather than a corruption finding.

It is signed because of what it decides. A configuration object names a retention policy, and
a retention policy names what gets deleted; a recovering machine that adopted a forged one
could be induced to age its own history away. The signature closes that against the realistic
adversary — **a destination holding the replica**, which has no repository key at all and
therefore cannot produce one.

It does not close it against a compromised repository member, which holds the signing key by
construction ([02 §3.2](02-identifiers.md#32-what-this-does-and-does-not-protect)); for a v2
repository the signing sub-root is inside the write bundle, so a compromised hub can forge
here exactly as it can elsewhere. That residue is answered by procedure rather than by
cryptography: a recovering device MUST present the recovered configuration — the retention
policy above all — for confirmation before it takes effect (FR-DR-009).

### 5.6 Lifecycle

A writer publishes a configuration object for a set when it publishes a snapshot for that
set, **and** whenever the set's configuration changes, so a schedule edited between backups is
not lost with the machine.

A configuration change therefore does **not** publish a snapshot, and must not: snapshots are
point-in-time claims about data, and a configuration edit looked at no data. It would also be
unsafe. Retention selects by bucketing snapshots in time and keeping the newest per bucket
([`07-retention-and-gc.md` §2](../../docs/architecture/07-retention-and-gc.md#2-retention-policy)),
so a configuration snapshot published later the same day would be the newest in that day's
bucket — and would expire the day's real backup. A separate namespace avoids the interaction
entirely rather than teaching the component that decides deletions a new exception.

**The newest configuration object for each backup set is a collection root.** A collector MUST
NOT delete it, whatever reachability says: nothing references these objects, so a reachability
walk alone would collect every one of them and quietly disarm recovery of operation. Older
ones for the same set are ordinary garbage and are collected through the tombstone path of
§3. → [ADR-0009 Amendment 6](../../docs/adr/0009-garbage-collection-safety.md)

A reader MUST tolerate a set with no configuration object at all: repositories written before
this revision have none, and a set whose recovery is unarmed is a stated condition, not
damage.

## 6 What this section does not settle

Named so a phase-4 implementer does not read silence as completeness:

- **Scope allocation for leases.** The format requires `<scope>` to be opaque; which unit of work a collector leases over — a shard, a generation, the whole repository — is the collector's design, not the format's.
- **Period length for audit records.** Any writer may choose any period, and readers merge across writers by time range rather than by matching periods.
- **Retention of audit periods.** Nothing here says how long they are kept. That is a policy question, and it is the one place in this file where the answer plausibly differs between a household and a managed estate.

---

**Previous:** [10 — Compression](10-compression.md) · **Back to:** [specification index](README.md)

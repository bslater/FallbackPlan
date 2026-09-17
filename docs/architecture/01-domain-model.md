# 01 — Domain model

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §5 · **Resolves:** [M1](../review/2026-08-architecture-review.md#m1--terminology-drifts-between-synonyms), [M8](../review/2026-08-architecture-review.md#m8--malformed-glossary-table)

**Built:** Normative vocabulary — in force wherever the code names these things — see [implementation status](../implementation-status.md).

---

This document is **normative for terminology**. Where any other document, code identifier, log message, or user-facing string names one of these concepts, it uses the term defined here. The original proposal used two names for several concepts; that is a defect for a format intended to be implemented by third parties, and it is closed here.

## 1. Glossary

| Term | Meaning |
|------|---------|
| **Device** | A cryptographically identified FallbackPlan installation. Holds a keypair; the keypair is not derivable from the repository. |
| **Source** | A device together with the filesystem roots whose state it captures. |
| **Backup set** | A named unit of policy: source selection, exclusions, schedule, retention, destinations, and format profiles. |
| **Snapshot** | An immutable point-in-time representation of a backup set. |
| **Protection state** | A backup set's derived status — the closed vocabulary and its console glance layer are normative at [10 §1.1](10-observability.md#11-states-must-be-distinguishable). |
| **Repository** | The logical collection of encrypted content, metadata, indexes, and snapshots, identified by a repository ID. |
| **Store** | Physical object storage holding repository objects — a local directory, a peer, or a cloud bucket/container. |
| **Replica** | A store holding a copy of a repository's objects. A destination's replica is **whole-archive**: complete, self-verifying, independently restorable. It may lawfully lag the source or hold a hub-trimmed subset under retention; it never diverges ([ADR-0034](../adr/0034-hub-and-spoke-destinations.md)). |
| **Destination** | A named place a backup set replicates to, declared once in the client configuration and referenced by name from sets: a directory on a local or removable drive (`local-path`), a paired peer (`peer`), or — schema-accepted now, implemented later — a cloud store. Holds a whole, independently restorable repository for each set that ships there — a staging set's destinations receive it as a replica of the staging archive by fan-out; a direct-ship set's receive it directly through the ship sink ([ADR-0046](../adr/0046-direct-to-destination-publication.md)). None of a set's destinations has to be local. |
| **Hub** | The service instance on a user's machine, in its orchestrating role: it manages the machine's backup sets, plans retention for every destination, and holds each set's repository seat — a staging archive it fans out from (unflagged sets), or a metadata store it ships through the sink from (direct-ship sets). |
| **Spoke** | A destination, viewed from its hub. A spoke that is a peer runs its own FallbackPlan service and is a hub for its own sets; the roles are per-relationship, not per-installation. |
| **Staging archive** | The per-set repository archive on the hub where an *unflagged* set's publication lands. Internal — a cache the hub manages, not a destination a user configures or a policy counts. What makes a staging set's capture unconditional and fan-out a copy of sealed objects. A direct-ship set has none, and consciously trades the unconditional capture away ([ADR-0046](../adr/0046-direct-to-destination-publication.md) §4); a migrated set's leftover archive is a read-only seed source until `retire_staging` deletes it. |
| **Segment** | A logical portion of a file's byte stream, produced by the backup set's segmentation profile. |
| **Segment record** | The stored form of one segment: compressed, independently encrypted, independently authenticated. |
| **Blob** | An immutable physical container holding many segment or metadata records, plus a recovery footer. |
| **Object identifier** | The keyed, repository-scoped identifier under which a record or manifest is referenced. See [`03-crypto.md` §4](03-crypto.md#4-object-identifiers). |
| **Content identifier** | The plaintext cryptographic hash of a segment. Used for deduplication and verification inside the trust boundary; never exposed to a store. |
| **File-version manifest** | An immutable object describing one version of one file: metadata, logical length, ordered segment references, whole-file hash. |
| **Tree** | An immutable directory object referencing child trees and file-version manifests. |
| **Snapshot manifest** | The immutable root descriptor of a snapshot: source, backup set, capture details, root tree, policy, publication generation. |
| **Index delta** | An immutable, writer-authored mapping from object identifiers to physical locations, published after the blobs it covers are durable. |
| **Checkpoint** | An immutable index generation that subsumes an explicitly enumerated set of deltas. |
| **Generation** | A monotonic marker of published repository index state. |
| **Catalogue** | The local, disposable, transactional database that materialises repository state for fast lookup. Never authoritative. |
| **Write intent** | A journal record published *before* a writer uploads blobs, naming the blobs it will create. Makes in-flight work reachable. See [`04-concurrency-and-publication.md` §4](04-concurrency-and-publication.md#4-write-intent). |
| **Lease** | An advisory, time-limited coordination record. **Never** a correctness mechanism — see [`07-retention-and-gc.md` §4](07-retention-and-gc.md#4-why-leases-are-not-load-bearing). |
| **Tombstone** | A record marking an object as eligible for physical deletion after a grace period. |
| **Root** | The 32 bytes Argon2id derives from the passphrase and the descriptor's public salt and parameters; never stored, never wrapped, used only as the input to every other key's derivation ([spec 03 §2](../../specifications/repository-format/03-keys.md#2-the-root)). *Master key*, *key-encryption key (KEK)* and *key object* named format 1's stored, wrapped root; format 1 was withdrawn before any freeze and those terms no longer name anything on disk ([ADR-0014 Amendment 1](../adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)). |
| **Write credential** | What a writing service holds: the structure root, signing root, content-ID key and key-ID key, plus the sealing, reclaim and claim *public* keys — every member an independent one-way HKDF domain of the root, so the whole credential yields neither the root nor any private authority ([ADR-0042](../adr/0042-write-only-repositories.md)). Stored by first-run setup for the installation, or per set by provisioning. Also *write bundle* in older records. |
| **Sealing key** | The X25519 keypair whose public half sits in the descriptor and whose private scalar exists only where the passphrase is present. Data-blob content keys seal to the public half; the descriptor's copy doubles as the wrong-passphrase verifier. |
| **Reclaim key** | The Ed25519 key that authorises *destruction* — tombstones, and retention instructions on the peer wire — derived per generation on a domain of its own, separate from the signing key that authorises publication ([ADR-0055](../adr/0055-reclaim-authority.md)). **Reclaim authority** is possession of it: a repository declaring the `reclaim-authority` feature acts on nothing destructive signed under any other key. A write-only service is deliberately not provisioned with it, so a service that may publish for ever cannot author a deletion; an ordinary service holds the master key and derives both, and there the retention floor is what holds instead ([07 §5](07-retention-and-gc.md#5-destructive-change-safeguards)). |
| **Claim key** | The Ed25519 key that proves a peer replica's *ownership* — derived from the **installation** (its passphrase and KDF salt) rather than from any one repository, because a machine claiming a replica has lost the repository and holds only the passphrase; the salt comes from the peer, which serves the KDF salts and costs behind its claimable replicas to a paired claimant ([ADR-0053](../adr/0053-peer-claim-and-configuration-recovery.md) Amendment 2). Not per generation, alone among the derived keys: a destination records the public half at first attribution and never replaces it, so a key that turned over would go stale with no way to say so. A **claim** is the ceremony that spends it — two phases in one session, the destination naming the derivations and the claimant answering one entry per derivation, all bound to the live session — and its effect is to re-point an attribution at the claimant's new device identity, granting no new power over the data. |
| **Reclaim grant** | The reclaim sub-root sealed end-to-end to a write-only service's published recipient key and handed to it for one collection run, on the shape the restore grant already uses ([ADR-0042 §5](../adr/0042-write-only-repositories.md)). Zeroed with the run: a service compromised between runs holds nothing that deletes. |
| **Recovery credential** | The passphrase — the one thing a person keeps that a recovery needs. Every archive's descriptor carries the rest (the KDF salt and parameters, the sealing public key that proves the passphrase, the repository id), so a recovery is the passphrase plus reach to an archive ([`08-restore-and-recovery.md` §4](08-restore-and-recovery.md#4-recovery-credential); [ADR-0060](../adr/0060-the-passphrase-is-the-recovery-credential.md)). |
| **Recovery kit** | Withdrawn ([ADR-0060](../adr/0060-the-passphrase-is-the-recovery-credential.md)). The export that once made clean-machine recovery possible; for a format-2 installation its payload was a strict subset of every archive's descriptor, and the product no longer produces one. Older records that cite it are kept as written. |
| **Dedup trust domain** | The scope within which a device is willing to reuse another writer's segments. See [`03-crypto.md` §5](03-crypto.md#5-deduplication-trust-domains). |
| **Direct-ship set** | A backup set flagged `direct_ship` ([ADR-0046](../adr/0046-direct-to-destination-publication.md)): its content is written to its destinations directly through the ship sink, the agent holds metadata only, and a capture with no reachable destination refuses. The flag defaults off until its tail (the peer write adapter, the trimming drill) lands. |
| **Ship sink** | The `IObjectStore` a direct-ship set's publication writes (`DestinationShipSink`): `blobs/` route to the set's in-scope destinations and never to local disk; every other object routes to the metadata store *and* the destinations. Reads answer from whoever holds the bytes — metadata locally, blobs from the first destination in priority order, listings as the union. |
| **Metadata store** | A direct-ship set's local repository seat at `<state>/sets/<setId>/`: descriptor, keys, journal, index, snapshots, hints — everything except blob content. Carries the set's writer role and sequence; deliberately not an openable repository (the destinations are); rebuildable from any destination since they hold every object it does. |
| **Spool** | The per-set working buffer where blobs assemble before sealing and shipping ([`02-repository-format.md` §5.3](02-repository-format.md#53-spooling-and-sealing)). Not staging: a spool file lives from first record to the blob's last destination acknowledgement, bounded by in-flight blobs — a buffer, never a copy of the backup (ADR-0046 §5). |
| **Priority** | An optional integer on a set, a destination, or a set's destination reference ([ADR-0047](../adr/0047-backup-pool-and-priorities.md) §4). Orders waiting work beneath user-initiation — a person always outranks any priority — and orders which destinations a run ships to first. |
| **Baseline** | The fact that a destination holds a full backup of a set, recorded in the sync ledger as `baseline_completed_at`; a pair owed one is `needs_full` — skipped by incrementals and seeded by catch-up (ADR-0047 §§5–6, ADR-0046 §3). |
| **Pause gate** | A run's cooperative suspension point ([ADR-0047 Amendment 1](../adr/0047-backup-pool-and-priorities.md#amendment-1--preemption-true-suspendresume-2026-08)): the capture pipeline checks it between scan events, so a preempted run parks at a file boundary with its state held in memory and resumes without re-scanning. |

## 2. Terms we do not use

Each of these appears in the prior art and in the original proposal. They are listed so readers arriving from another product can map their vocabulary — and so that reviewers can flag their reappearance in our own text.

| Do not use | Use instead | Where it comes from |
|------------|-------------|---------------------|
| chunk | **segment** | content-addressed snapshot repositories |
| block | **segment** | consumer peer backup services; file synchronisers |
| pack, pack file | **blob** | content-addressed snapshot repositories |
| volume, data file | **blob** | plugin-oriented backup clients |
| local database, local index | **catalogue** | products with an authoritative local database |
| lock | **lease** (advisory) or **write intent** (correctness) | content-addressed snapshot repositories |

Two qualifications. The rule governs **nouns**: *packing* is fine as a verb for the act of assembling records into a blob, and *content-defined chunking* keeps its established name because that is what the algorithm is called everywhere. Sections describing prior art may still use the source's own vocabulary where translating it would obscure the point being made — but they name the design, not the product ([naming and attribution](../naming-and-attribution.md)).

Note too that "blob" is overloaded in the wider ecosystem: Azure Blob Storage — an interface this project implements against, so its name stays — calls every stored object a blob. Where that ambiguity could bite — chiefly [`05-storage-providers.md`](05-storage-providers.md) — we say **repository blob** for ours and **store object** for the provider's unit of storage.

## 3. Object relationships

```text
Snapshot manifest
  ├── source device, backup set, capture window, policy ref, publication generation
  └── root tree
        ├── tree (subdirectory)
        │     └── … recursively
        └── file-version manifest
              ├── path identity, metadata, logical length, whole-file hash
              ├── parent file-version manifest (previous version, where known)
              └── ordered segment references
                    └── (logical offset, logical length, OBJECT IDENTIFIER)
                                                          │
                    ┌─────────────────────────────────────┘
                    │  resolved by the index, never by the manifest
                    v
              Index delta / checkpoint
                    └── object identifier → (blob, record offset, stored length, profiles)
                                              │
                                              v
                                            Blob
                                              ├── cleartext envelope (format, key generation, blob salt)
                                              ├── segment records (independently encrypted + authenticated)
                                              └── authenticated recovery footer (record table)
```

The indirection at the marked line is deliberate and load-bearing. A manifest states *what* a file is made of; the index states *where* those parts currently live. Because compaction moves records between blobs, physical location cannot live in an object that is declared immutable — this was the original proposal's most serious internal contradiction ([C1](../review/2026-08-architecture-review.md#c1--immutable-manifests-embed-physical-locations-that-compaction-changes)), and the separation above is what resolves it.

The blob recovery footer holds the same mapping the index does, which is what makes forensic rebuild possible when every index object has been lost.

## 4. Snapshot semantics

A snapshot manifest records:

- source device identity and backup-set identity;
- capture start and completion time;
- parent snapshot(s), where applicable;
- root tree object identifier;
- filesystem capabilities and case-sensitivity observed at the source;
- policy version and the effective format profiles used;
- consistency method — live scan, VSS, or filesystem snapshot;
- errors, unreadable paths, and partial-capture status;
- source clock observations and any detected skew (see [`04-concurrency-and-publication.md` §7](04-concurrency-and-publication.md#7-time-and-clock-skew));
- client and repository format versions;
- a signed declaration by the writing device.

Snapshots are never modified. Corrections create new declarations or administrative records; they never rewrite historical objects.

### 4.1 Commit is not replication

A snapshot is **committed** to a replica once every object it references is durable *in that replica*. Commit is a per-replica property and is always achievable locally.

**Replication** state — whether a given destination holds the snapshot, and whether that has been independently verified — is tracked separately, per `(snapshot, destination)` pair.

Conflating the two makes protection hostage to the least available destination: a peer switched off for a fortnight would block every snapshot, including the local one that is working perfectly ([C5](../review/2026-08-architecture-review.md#c5--snapshot-commit-is-defined-so-that-one-offline-destination-stalls-all-protection)). Full model in [`04-concurrency-and-publication.md` §6](04-concurrency-and-publication.md#6-commit-versus-replication).

## 5. Replication, not synchronisation

The first release replicates repository objects, not live source folders:

- a source scan produces a snapshot, published once — into the set's staging archive (unflagged sets), or through the ship sink to the set's reachable destinations directly (direct-ship sets, [ADR-0046](../adr/0046-direct-to-destination-publication.md));
- the snapshot references immutable trees, file-version manifests, and segments;
- missing immutable objects reach each of the set's destinations as they are available — by fan-out from staging, or by catch-up through the sink from whichever destination holds them — and the ones that were away are caught up;
- a snapshot commits to a replica once its referenced objects are durable there;
- a deletion appears in a later snapshot and erases nothing;
- retention selects which snapshots remain protected, per set and per destination;
- garbage collection removes unreachable objects only after safety checks and grace periods — marked by the hub, executed at each destination on its instruction.

This yields the transfer efficiency of synchronisation while preserving backup semantics.

---

**Previous:** [00 — Overview](00-overview.md) · **Next:** [02 — Repository format](02-repository-format.md)

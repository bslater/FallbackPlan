# 01 — Domain model

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §5 · **Resolves:** [M1](../review/2026-08-architecture-review.md#m1--terminology-drifts-between-synonyms), [M8](../review/2026-08-architecture-review.md#m8--malformed-glossary-table)

---

This document is **normative for terminology**. Where any other document, code identifier, log message, or user-facing string names one of these concepts, it uses the term defined here. The original proposal used two names for several concepts; that is a defect for a format intended to be implemented by third parties, and it is closed here.

## 1. Glossary

| Term | Meaning |
|------|---------|
| **Device** | A cryptographically identified FallbackPlan installation. Holds a keypair; the keypair is not derivable from the repository. |
| **Source** | A device together with the filesystem roots whose state it captures. |
| **Backup set** | A named unit of policy: source selection, exclusions, schedule, retention, destinations, and format profiles. |
| **Snapshot** | An immutable point-in-time representation of a backup set. |
| **Repository** | The logical collection of encrypted content, metadata, indexes, and snapshots, identified by a repository ID. |
| **Store** | Physical object storage holding repository objects — a local directory, a peer, or a cloud bucket/container. |
| **Replica** | A store holding a copy of all or a defined subset of a repository's objects. |
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
| **Recovery kit** | The export that makes clean-machine recovery possible. Contents specified in [`08-restore-and-recovery.md` §4](08-restore-and-recovery.md#4-recovery-kit). |
| **Dedup trust domain** | The scope within which a device is willing to reuse another writer's segments. See [`03-crypto.md` §5](03-crypto.md#5-deduplication-trust-domains). |

## 2. Terms we do not use

Each of these appears in the prior art and in the original proposal. They are listed so readers arriving from another product can map their vocabulary — and so that reviewers can flag their reappearance in our own text.

| Do not use | Use instead | Where it comes from |
|------------|-------------|---------------------|
| chunk | **segment** | restic, Kopia, borg |
| block | **segment** | CrashPlan, Syncthing |
| pack, pack file | **blob** | restic, Kopia |
| volume, dblock | **blob** | Duplicati |
| local database, local index | **catalogue** | Duplicati, CrashPlan |
| lock | **lease** (advisory) or **write intent** (correctness) | restic |

Two qualifications. The rule governs **nouns**: *packing* is fine as a verb for the act of assembling records into a blob, and *content-defined chunking* keeps its established name because that is what the algorithm is called everywhere. Sections describing prior art also use each product's own vocabulary, since renaming restic's pack files inside a paragraph about restic would help nobody.

Note too that "blob" is overloaded in the wider ecosystem: Azure Blob Storage calls every stored object a blob. Where that ambiguity could bite — chiefly [`05-storage-providers.md`](05-storage-providers.md) — we say **repository blob** for ours and **store object** for the provider's unit of storage.

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

- a source scan produces a snapshot;
- the snapshot references immutable trees, file-version manifests, and segments;
- peers exchange missing immutable objects;
- a snapshot commits to a replica once its referenced objects are durable there;
- a deletion appears in a later snapshot and erases nothing;
- retention selects which snapshots remain protected;
- garbage collection removes unreachable objects only after safety checks and grace periods.

This yields the transfer efficiency of synchronisation while preserving backup semantics.

---

**Previous:** [00 — Overview](00-overview.md) · **Next:** [02 — Repository format](02-repository-format.md)

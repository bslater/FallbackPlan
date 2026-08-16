# 00 — Overview

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §1–§4, §27

**Built:** Vision and scope — describes intent throughout, not a component — see [implementation status](../implementation-status.md).

---

## 1. What FallbackPlan is

FallbackPlan is an open-source backup and archival platform for Windows, macOS, and Linux. Its purpose is to restore a capability the consumer market has largely withdrawn: a user can back up one computer to another computer they control or trust, across a LAN or the internet, without a proprietary cloud service.

> **Vision.** FallbackPlan gives every computer a safe fallback: encrypted, versioned copies held on computers and storage that the user chooses.

The technical centre is a **streaming archive engine**. It divides files into segments, hashes and compares them across versions, encrypts changed segments independently, and packs them into immutable blobs. Everything else — snapshots, replication, retention, restore, legacy import — is built on that engine.

A repository is replicated between FallbackPlan instances and storage providers. Live folders are **not** bidirectional synchronisation roots. Each source device publishes immutable snapshots; a destination may hold snapshots for many source devices; deleting a file creates new snapshot state rather than erasing history.

The operational shape is **hub and spoke** ([ADR-0034](../adr/0034-hub-and-spoke-destinations.md)). The **hub** is the service instance installed on a user's machine: it manages that machine's backup sets and holds each set's staging archive. The **spokes** are a set's declared destinations — a peer's FallbackPlan instance, a directory on a local or removable drive, in a later phase a cloud store — and each holds a complete, independently restorable replica of the set's archive. The hub publishes each snapshot once, fans it out to every configured destination that is available, catches up the ones that were not, and routinely runs retention against all of them. No destination has to be local: a set backed up only to a friend's machine is a first-class configuration.

## 2. Product promise

A user should be able to answer all of these without specialist knowledge:

1. Is this computer protected?
2. Where are its backups stored?
3. When was the last independently verified recoverable snapshot?
4. Can I restore after this computer is destroyed?
5. Can I move my archive to another provider without starting again?
6. Can I recover without a FallbackPlan-operated service?

Question 3 is the load-bearing one, and the hardest to answer honestly. See [`09-replication-and-peers.md` §5](09-replication-and-peers.md#5-destination-verification) for why a destination's own claim to hold data is not evidence, and what we do instead.

## 3. Core principles

1. **Recovery is the product.** Backup completion is not sufficient; recoverability must be verified.
2. **The user owns the repository.** No FallbackPlan-operated cloud account is required.
3. **Open format, open protocol, open implementation.** Repository and wire specifications are published and versioned.
4. **No silent destructive synchronisation.** Source deletion is recorded as history; retention and garbage collection are independent operations.
5. **Encryption before transport.** Storage nodes and cloud providers never require access to plaintext content or filenames.
6. **Metadata scales incrementally.** No repository-wide monolithic manifest.
7. **Every durable object is independently verifiable.** Corruption is localised, detectable, and repairable.
8. **Caches are disposable.** A local index accelerates operations; the repository remains the source of truth and can rebuild it.
9. **Interruption is normal.** Every operation tolerates process termination, connectivity loss, and eventually consistent storage.
10. **Compatibility is isolated.** Importers translate legacy formats into a stable neutral model and never dictate the native design.

Principle 8 needs one qualification the original proposal did not make. *Caches* are disposable; **device identity and pairing grants are not** — they cannot be rebuilt from repository contents. See [`11-solution-structure.md` §3](11-solution-structure.md#3-local-state-separation).

## 4. Scope

### 4.1 First production release

Local file and directory selection · scheduled and change-triggered snapshots · versioned point-in-time snapshots · encrypted backup to another FallbackPlan instance · encrypted backup to a local directory or mounted filesystem · repository replication between instances · Azure Blob Storage · Amazon S3 and S3-compatible stores · restore by device, snapshot, date, path, pattern, or selection · configurable retention · integrity verification and repair workflows · bandwidth, CPU, concurrency, and schedule controls · resumable transfers · command-line interface · local web administration · headless service operation · legacy archive assessment and migration for explicitly supported variants · documented recovery kit export.

### 4.2 Later releases

further S3-compatible and cloud object-store providers, SFTP, WebDAV, SMB-aware and removable-media providers · peer discovery through optional public infrastructure · relays that cannot decrypt content · multi-user household and small-business administration · mobile restore and photo ingestion · filesystem snapshots via VSS, APFS, Btrfs, ZFS, LVM · database-aware pre/post hooks · mirroring across storage classes and geographies · immutable/WORM targets · object-lock integration · erasure coding for peer sets · public restore links using separately wrapped scoped keys · importers for other backup tools' repositories where licensing and format stability permit.

### 4.3 Explicit non-goals for the first release

Full-disk imaging or bare-metal recovery · operating-system deployment · bidirectional working-folder synchronisation · collaborative file editing · proprietary cloud hosting operated by the project · ransomware detection as a substitute for endpoint security · automatic deletion of source data after backup · transparent filesystem mounting as the only restore method · guaranteed conversion of every historical variant of any legacy archive format.

## 5. Lessons from prior art

The design borrows deliberately. What follows is what we take and what we do differently — the "differently" column is the part that shapes the format.

### 5.1 Consumer peer-to-peer backup services

The class FallbackPlan most directly replaces splits files into blocks, deduplicates, compresses, encrypts, and stores them with manifests describing paths and versions. Its consumer appeal came from supporting computer and folder destinations rather than requiring a cloud service.

**Adopt:** continuous incremental protection · multiple independent destinations per backup set · computer-to-computer backup · long version retention · restore to a replacement computer · plain consumer language about protection and recovery · source-side encryption · block reuse across versions.

**Improve:** publish the format and migration guarantees · avoid repository-wide monolithic manifests · shard and content-address metadata · ship independent integrity-check and recovery tooling · make key export a first-class workflow · keep the repository usable without authenticating to a vendor · make maintenance online, incremental, bounded, and cancellable · ensure older readers fail safely on newer format features · support migration without restoring the archive to temporary plaintext.

That class of product documents, in its own troubleshooting material, that large file and history manifests interfere with scanning, synchronisation, backup, restore, and maintenance. That is a primary design constraint here, not a late optimisation — it is the direct motivation for principle 6 and for the sharded index design in [`02-repository-format.md` §7](02-repository-format.md#7-index-architecture).

### 5.2 Peer synchronisation protocols

**Adopt:** cryptographic device identities · explicit device approval · direct peer connectivity · LAN discovery · optional global discovery · relay fallback without relay plaintext access · protocol negotiation and capability advertisement · block-level transfer requests · resumable bounded parallel transfers · independent control and data channels · transparent connection-path reporting.

**Do not copy:** a file synchroniser's core model. FallbackPlan is a backup system, not a global working-tree reconciler. It must never choose a single current global file state and propagate deletions or conflict resolution as its primary model.

### 5.3 Content-addressed snapshot repositories

**Adopt:** immutable snapshots · Merkle-style directory trees · content-defined chunking so inserted bytes do not invalidate later chunks · pack files · documented repository invariants and write ordering · checks that rebuild indexes from packs · append-before-reference semantics · local cache as optimisation only · pruning as a separate concern.

This family's write ordering — durable data first, indexes second, snapshot references last — is the single most valuable rule in the prior art, and [`04-concurrency-and-publication.md`](04-concurrency-and-publication.md) adopts it unchanged.

**Improve:** design multi-device writers in from the start rather than treating concurrent maintenance as exceptional · reduce dependence on listing large object namespaces · support event journals and compacted index generations · express recoverability and damage scope in user-facing terms · support background healing from replicas · avoid a global exclusive lock for routine retention.

### 5.4 Layered repositories over a minimal blob store

**Adopt:** a minimal blob-store abstraction · content-addressed encrypted blocks · packs sized for high-latency, request-priced stores · index recovery data embedded in packs · encrypted filenames and metadata · policy inheritance · repository-server mode · caching designed for remote latency · independently configurable chunking, compression, packing, and encryption suites.

**Improve:** minimise repository-wide format configuration that cannot evolve per generation · make the key hierarchy and recovery kit comprehensible to consumers · distinguish password rotation from data-key rotation · establish deterministic conformance fixtures for third-party readers.

### 5.5 Plugin-oriented clients with an authoritative local database

**Adopt:** browser-based local management · broad provider plugin model · scheduling and notification · include/exclude rules · approachable restore workflows · provider-specific settings behind a common interface.

**Improve:** never make a single local SQLite database authoritative · allow complete reconstruction from repository objects · reduce volume-chain fragility · isolate provider plugins from repository semantics · build state-machine and interruption testing in from the start · avoid exposing an unbounded list of backend options in the primary experience.

### 5.6 What the prior art teaches about the service boundary

The sections above take format lessons — chunking, packs, write ordering. The
**process** shape deserves the same treatment, because two of the products
above independently arrived at an engine-as-service with thin front ends, and
both show where that shape goes wrong.

**Adopt:** the engine is a service and every UI is a client of it · the service
keeps working with no UI installed, running, or reachable · one engine
implementation behind CLI and GUI alike, so automation and the interface cannot
diverge in behaviour.

**Improve:** both authenticate a local UI to a local engine over a network
transport — one with a token file, the other with a password on a fixed
loopback port — and both are well known for the resulting failure, a UI
insisting it cannot reach an engine that is running perfectly well. A local
boundary is not a network boundary, and the operating system already knows who
is on the other end of a socket. Version skew between service and client is the
second recurring complaint, so it is refused with a message naming both versions
rather than met with a blank window ([ADR-0028](../adr/0028-service-boundary-and-deployment-topologies.md) §7).

## 6. Architecture at a glance

```text
+---------------------------------------------------------------+
|                        FallbackPlan UI                        |
|              Desktop shell / Local Web / CLI                  |
+-----------------------------+---------------------------------+
                              |
                    THE SERVICE BOUNDARY  (ADR-0028)
        commands, results, progress events — never key material,
        and by default never file content across a remote binding
                              |
+-----------------------------v---------------------------------+
|                    Application Services                       |
| Backup | Restore | Replication | Retention | Verify | Import  |
+-----------------------------+---------------------------------+
                              |
+-----------------------------v---------------------------------+
|                        Domain Core                            |
| Snapshot | Tree | Segment | Repository | Policy | Device | Job |
+------------+----------------+----------------+---------------+
             |                |                |
+------------v----+  +--------v---------+  +---v---------------+
| Filesystem      |  | Repository       |  | Peer Protocol     |
| Scanner         |  | Engine           |  | Discovery/Relay   |
| Watcher/VSS     |  | Crypto/Blob/GC   |  | Transfer/Auth     |
+------------+----+  +--------+---------+  +---+---------------+
             |                |                |
             +----------------+----------------+
                              |
+-----------------------------v---------------------------------+
|                         Store SPI                             |
| Local | Peer | Azure Blob | S3 | S3-compatible | Future       |
+---------------------------------------------------------------+
```

### 6.1 Process model

The engine runs as a service; every user interface is a client of it. This is
the shape the prior art in §5.6 arrived at, and the boundary is specified in
[ADR-0028](../adr/0028-service-boundary-and-deployment-topologies.md).

| Component | Role |
|-----------|------|
| **Agent** | The service. Scans, snapshots, transfers, restores, verifies, and hosts the command surface. Sole holder of the writer role for its machine while running |
| **Desktop** | Optional desktop shell — a client |
| **Web** | Browser UI — a client. Hosted by a service for one machine, or deployed standalone as a console managing several |
| **CLI** | Automation interface — a client, with an explicit direct mode when no service is running |
| **Recovery** | Standalone emergency restore tool. Speaks to no service in any topology — see [`08-restore-and-recovery.md` §5](08-restore-and-recovery.md#5-emergency-recovery) |
| **Relay** | Separately deployable stateless encrypted-transport relay |
| **Discovery** | Optional separately deployable discovery service |
| **Repository Server** | Optional gateway exposing repository operations without granting raw store credentials |

The Agent must remain fully functional without Desktop, Web, a console, Relay,
Discovery, or any project-operated service. A machine whose console is
unreachable, or was uninstalled, keeps backing itself up.

### 6.2 Installation topologies

One service implementation and one command contract; the topologies differ only
in what is installed and whether the remote binding is enabled.

| Topology | Service | Front end | Transport |
|----------|---------|-----------|-----------|
| **All-in-one** | local | local app or web | local binding only |
| **Service only** | local | none | local binding only |
| **Multi-instance console** | one per managed machine | one web console, elsewhere | remote binding on each managed service |
| **Client only** | none locally | CLI or app | remote binding to a named service |

The local binding — a Unix domain socket or named pipe, authenticated by the
operating system — is always present. The **remote binding is off until
explicitly enabled**, and remote clients are paired with pinned device identity
rather than given a password, reusing the mechanism [`09-replication-and-peers.md` §3](09-replication-and-peers.md#3-pairing)
already defines for peers.

A console administers machines it cannot read: control and status cross a remote
binding, file content does not unless separately and explicitly enabled. That
restraint is what makes fleet administration compatible with the promise in
principle 1 — the same reason a destination cannot read what it stores.

## 7. The architectural decision

Proceed as a **snapshot-based backup repository with repository replication**, not a folder-sync product with versioning added later.

The first vertical slice:

> Select a folder → create an encrypted immutable snapshot → store it locally → delete all local cache state → reconstruct the repository index → restore and verify the folder on a clean machine.

The second:

> Pair two computers → transfer the repository snapshot to the destination → destroy the source installation → restore solely from the destination and recovery kit.

The third ([ADR-0034](../adr/0034-hub-and-spoke-destinations.md)):

> Declare a set with two destinations and no other local copy → back up on schedule → the hub fans out to both, catches up the one that was offline → retention ages each destination under its own policy → either peer ends the peering and both sides see a durable notice → restore independently from each destination.

Azure, S3, and the legacy conversion layer on only after all three are reliable — a cloud bucket enters as one more destination kind behind the same fan-out, not as a separate feature. This ordering makes recoverability — not feature count — the foundation.

---

**Next:** [01 — Domain model](01-domain-model.md)

# ADR-0034 — Hub-and-spoke destinations: per-set staging archives, whole-archive replicas

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-DEST-001..008, FR-SNP-003, FR-REP-001, FR-GC-007, FR-GC-009, NFR-OPS-002, NFR-OPS-006
**Related:** [ADR-0011](0011-commit-versus-replication-semantics.md), [ADR-0018](0018-replica-failure-domains.md), [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [ADR-0030](0030-peer-identity-and-pairing.md), [architecture 09](../architecture/09-replication-and-peers.md), [peer-protocol 03](../../specifications/peer-protocol/03-replication.md)

---

## Context

The product's shape has always been hub-and-spoke: a household's service instance
backs its machine up to several places at once — a folder on a removable drive, a
friend's machine across town, eventually a cloud bucket — and each of those places
is supposed to hold a copy that survives the loss of every other one. The
requirements already speak per-destination (FR-SNP-003's replication state is per
`(snapshot, destination)`, FR-REP-001 replicates to "one or more" of them), the
status vocabulary was built for it (ADR-0027 §4 keeps per-destination detail as
the source of every summary), and the wire protocol that moves objects between two
services exists and is proven (peer-protocol 03).

What was never designed is the middle: **how one service fans a backup set out to
N destinations.** Today the service opens exactly one repository at one path
(`ServiceOptions.RepositoryPath`), every backup set publishes into it, a second
copy exists only when an operator runs the `replicate` verb by hand, and a
`BackupSetConfiguration` carries sources and a schedule but no destinations and no
retention. The one archive is simultaneously the working store, the only replica,
and — because architecture 04 §6.3 and 09 §4 made a durable *local* repository the
precondition for everything else — a mandatory one. A user whose only destination
is a friend's machine cannot be expressed at all.

Giving a set N destinations forces the data-model question this record settles,
because everything else — orchestration, retention, status, quota — follows from
it: **what, exactly, does a destination hold?**

Two families of answer exist. Either every destination holds a *complete
repository archive* and the fan-out unit is the archive; or all sets share one
archive and each destination holds a *filtered subset* of it — the objects
reachable from the snapshots of the sets routed there. The second preserves
repository-wide deduplication and was the incumbent assumption. It also breaks the
format's spine: an archive is self-verifying because its journal is gapless and
every record it references is present (architecture 04 §9). A filtered copy is a
repository whose journal names blobs that are deliberately absent — verification
cannot pass on it, GC cannot mark it, and the recovery tool cannot treat it as
what it claims to be. The format has no vocabulary for a partial replica, and
inventing one would touch every reader.

## Decision

### 1. Each backup set publishes into its own staging archive

A backup set owns a repository archive on the hub — its **staging archive**,
under the service's archives root — with its own writer sequence, catalogue and
spool. Capture publishes into it exactly as capture publishes today; nothing
about the publication order, the intent journal, or commit semantics changes.
The staging archive is internal: it is where publication lands and what fan-out
reads from, not a destination a user configures, and it exists whether or not
any local destination does. A backup therefore never blocks on destination
availability — ADR-0011's separation of commit from replication, kept under the
new topology.

### 2. A destination holds a whole-archive replica

Every destination of a set holds a complete replica of that set's staging
archive. Not a filtered subset: a *repository*, with the full journal, index,
snapshots and blobs, independently restorable by the recovery tool with no
knowledge of the hub. This is the property peer replication already proves
whole-repo (peer-protocol 03's completeness rule), and per-set archives are what
let whole-archive replication *be* per-set fan-out — no snapshot-scoped filter,
no partial-replica concept, no new reader semantics anywhere.

This is not ADR-0011's rejected "per-destination snapshot objects" returning.
That alternative made the snapshot's identity ambiguous by minting distinct
metadata per destination. Here there is one snapshot, one archive, one journal —
and N byte-identical copies of it. A destination is a *replica*, never a
divergent archive; the only way two copies may lawfully differ is that a
lagging one is a prefix of the truth and a retention-trimmed one holds a subset
chosen by the hub (§4), both of which the replication inventory already
expresses.

### 3. Publish once; fan out immutable objects; destinations never allocate

A snapshot is captured and sealed exactly once, into the staging archive, under
that archive's single writer sequence. Fan-out copies immutable objects — the
same diff-inventory-then-copy the replication protocol performs, generalised so
its source and target are any two object stores. Nothing is re-published per
destination: there is no per-destination writer sequence to keep gapless, no
N-fold Argon2id-and-seal cost, and no way for two destinations to disagree about
what snapshot *n* contains. Writes originate in staging, always — including
future compaction, which re-seals in staging and propagates as ordinary new
objects (ADR-0025). What the hub does *to* a destination is copy objects in
dependency order and, under retention, delete them; it never asks a destination
to create anything.

### 4. The hub orchestrates; retention at a destination is hub-planned deletion

The hub is the only party that can read the set's manifests, so the hub plans:
which snapshots a destination's retention policy keeps, and therefore which
objects the destination should hold. Against a local-path destination it
executes the plan directly; against a peer it instructs, and the peer deletes
only what it is told, bounded by its own granted floor (ADR-0030 §3's
`RetentionFloorGenerations` — a spoke refuses an instruction that would take it
below what it promised to keep). Retention must still never outrun replication:
an object leaves staging only once every configured destination holds it or the
deferral bound of ADR-0011 Amendment 2 has been raised as a warning.

### 5. Destinations are named configuration, referenced by sets

Destinations are declared once, at the top level of the client configuration,
and referenced by name from each set: a `local-path` destination is a directory,
a `peer` destination is a pinned fingerprint plus the endpoint to dial, and the
cloud kinds (`s3`, `azure-blob`, `dropbox`) are accepted by the schema now and
refused by the runtime with a stated "not yet supported" until a provider
exists, so configuration, status and retention are designed once rather than
re-opened per provider. Endpoints live here and only here — a pairing grant
holds a key and terms, never an address (ADR-0030), and the repository holds no
destination list (ADR-0010's rejected alternative stays rejected). The
configuration is thereby the hub's address book, which is a privacy statement as
much as a convenience: the file now names who stores your backups and where,
and the export guidance says so.

A set must reference at least one destination. **None of them has to be local**
— the durability policy of architecture 09 §4 becomes "at least one configured
destination outside the source's failure domain", and a local directory is just
one destination kind among several. ADR-0018's domains now attach to
destinations, declared in the same configuration entry.

### 6. The costs, accepted

**Deduplication narrows from the repository to the set.** Two sets with
overlapping roots store overlapping content twice, once per staging archive, and
again per destination copy. Accepted deliberately: ADR-0006 already frames dedup
scope as a trust-domain choice rather than a maximum, the overlap case is the
exception in household use, and what it buys — every destination copy a complete
archive, blast radius of one set, per-destination quota that is simply the bytes
of the archives assigned there — is the architecture. **Staging doubles
same-disk storage** until the trim that FR-GC-009's gate enables lands: a set
whose only destination is a folder on the same volume holds its data twice
meanwhile. **N archives mean N key derivations** on a many-set hub, bounded by
opening archives lazily.

## Consequences

**Positive.** Every destination copy is a full, self-verifying, independently
restorable repository — the recovery story is the existing recovery story, N
times. Fan-out reuses the proven replication planner rather than new publication
machinery. Per-destination divergence is confined to deletion, so the
"per-destination writer sequence" problem never exists. A corrupt archive
damages one set. The spoke side needs nothing: a responder already stores
replicas per source repository, so N set archives from one hub land as N replica
stores unmodified.

**Negative.** Cross-set dedup is gone (§6). The service's single
`RepositoryPath` becomes an archives root, which reshapes state-directory layout
and every test that assumed one repository. The recovery kit must enumerate a
set of archives rather than one. And "one writer per repository" (ADR-0028)
multiplies into one writer role per set archive, all held by the same process —
the rule is unchanged, its instances are counted differently.

**Neutral.** The repository format is untouched: no new object types, no reader
changes, no fixture movement. The peer protocol grows messages (roles,
termination, retention instructions) but its session and replication layers
carry over as-is.

## Alternatives considered

**One shared archive, per-destination replication filters.** Preserves
repository-wide dedup. Rejected for the Context's reason: destinations become
partial repositories that cannot verify, cannot be GC-marked, and cannot be
restored from without hub-side knowledge — surrendering the property that makes
a backup a backup. The filter machinery (snapshot-scoped closure computation on
every sync) would also be new, subtle, and load-bearing.

**Re-publish per destination.** Each destination an independent archive built by
running publication N times. Rejected: N gapless writer sequences to keep, N
times the seal cost, and N archives whose contents can lawfully diverge — which
of them is *the* backup? It also re-opens ADR-0011's rejected per-destination
snapshot objects at archive scale.

**Destination-side retention autonomy.** Let each spoke apply its own policy.
Rejected as impossible where it matters: a peer cannot read manifests, so it
cannot compute a keep-set; only the hub can plan. The spoke's authority is its
floor and its quota — what it promised and what it will hold — not the policy.

**Keep a mandatory local repository.** The status quo, minus the pain. Rejected
because it makes the friend's-machine-only user — the founding scenario of the
peer protocol — unconfigurable, and because "local" was never the point:
*off-domain and in-sync* is what protects data, and ADR-0018 already said so.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Decided ahead of implementation, with the config, orchestration and retention slices to follow. Amends ADR-0009/0010/0011/0012/0018/0025/0027/0028/0029/0030 as recorded in each. |

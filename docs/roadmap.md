# Roadmap

**Status:** draft · **Supersedes:** [original proposal](review/2026-08-original-proposal.md) §21, §25 · **Resolves:** [H2](review/2026-08-architecture-review.md#h2--two-phase-0-exit-criteria-cannot-be-met-at-phase-0)

---

## Phase 0 — Archive engine vertical slice

Build the engine first. Prove that files can be segmented, compared, encrypted, packed, indexed, rebuilt, and restored with no application layers above.

**Deliverables**

Architecture decision records and threat model · versioned configuration schemas for segment size, blob size, compression, encryption, concurrency, and spool behaviour · streaming segment reader and content-identifier hasher · prior-version comparison and unchanged-segment reuse · compression policy with the measured threshold · per-blob key derivation and independently authenticated record encryption · config-driven blob assembler, durable spool, sealing, and recovery footer · immutable file-version manifest encoding with logical-only segment references · local SQLite catalogue with segment and blob indexes · immutable index deltas and checkpoints with per-writer chains · write-intent journal records · local filesystem object store · normal and forensic rebuild tools · low-level CLI: `archive`, `inspect-blob`, `inspect-manifest`, `rebuild-index`, `verify`, `restore-file` · deterministic format fixtures · interruption and corruption harnesses · benchmarks across segment sizes, blob sizes, encryption profiles, compressible and incompressible data, large files, and high version counts.

**Exit criteria**

- A multi-terabyte logical file is processed within the NFR-PERF-001 memory bound and spans many blobs.
- Changing one segment writes exactly one new segment record plus a new file-version manifest.
- Configured blob targets are respected without splitting segment records.
- Every segment and every complete restored file is cryptographically verified.
- Interrupted blob construction or publication cannot expose an incomplete committed file version.
- Resume-after-kill produces byte-identical blobs; restart-after-kill produces a different blob salt.
- The catalogue can be deleted and rebuilt from checkpoint plus deltas.
- Forensic rebuild succeeds from blob recovery footers and manifests after every global index object is removed.
- Blob compaction relocates records without modifying any manifest, and all references still resolve.
- Garbage collection concurrent with an in-flight backup deletes none of its intent-covered blobs.
- **A synthetic legacy source adapter feeds an arbitrary byte stream plus a provenance record through the same pipeline**, demonstrating the ingest path is not coupled to the filesystem scanner.

> **Two criteria moved.** The original Phase 0 required one representative legacy file version to be streamable into this pipeline — which needs the Phase 5 reader, itself contingent on a legal review and an archive corpus we do not have — and "an independently written reader can parse public fixtures", which needs a second implementation to exist before the format is drafted. The first is replaced by the synthetic adapter above, which proves the same property. The second moves to the format freeze gate, where it is both achievable and genuinely valuable ([H2](review/2026-08-architecture-review.md#h2--two-phase-0-exit-criteria-cannot-be-met-at-phase-0)).

---

## Phase 1 — Snapshot and local repository MVP

Filesystem capture, immutable tree and snapshot manifests, reliable local restore. *Execution plan:* [`phase-1-execution-plan.md`](phase-1-execution-plan.md).

**Features:** cross-platform streaming scanner · include/exclude rules · file identity and version comparison · immutable file, tree, policy, error, and snapshot manifests · local store and catalogue integration · CLI `init`, `backup`, `snapshots`, `ls`, `restore`, `check`, `key export` · restore planner and verifier · recovery kit · repository inspector · integrity verification · retention selection without physical pruning · three-way local state separation · OpenTelemetry instrumentation · Agent service and basic scheduling.

**Exit criteria:** cross-platform backup and point-in-time restore · path and version lookups meet NFR-PERF-004 · interruption testing at every publication boundary · complete rebuild without the local database · restore begins during partial rebuild · clean-machine recovery using only repository plus kit · public conformance fixtures cover blobs, records, manifests, indexes, and snapshots.

---

## Phase 2 — Peer-to-peer backup, and the service boundary

Restore computer-to-computer backup, and make the engine a service that front ends talk to ([ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md)).

**Features:** device identity and pairing · peer store · direct TLS/QUIC transfer · LAN discovery · **resumable replication** · destination quotas and retention floors · per-destination replication state · keyed random-range verification challenges · dedup trust domains · **command surface and both transport bindings** · **CLI becomes a client, with direct mode** · **writer-role exclusion on the state directory** · **keystore unlock** · **per-job progress events** · local web UI · **multi-instance console** · **service installation** · bandwidth schedules · protocol version negotiation.

**Status:** the service boundary is built on both bindings — writer-role exclusion, the command contract and its versioning, status aggregation, unlock, per-job progress, and a CLI that asks a running service to back up, restore, verify and check, taking direct mode when none is listening. **The remote binding is now built too:** pairing reuses [architecture 09 §3](architecture/09-replication-and-peers.md#3-pairing)'s machinery, which [ADR-0030](adr/0030-peer-identity-and-pairing.md) settles and [`specifications/peer-protocol/`](../specifications/peer-protocol/README.md) makes normative, and `FallbackPlan.Protocol` now carries it over a real TLS 1.3 socket: identity and the pairing ceremony, the grant store and the destination's terms, framing, version and feature negotiation, the channel-bound authentication that replaced RFC 7250, and a durable device key. `RemoteServiceListener` binds only an interface an administrator names and admits only a pinned peer; a paired console reaches the service and an unpaired one is refused, with the ceremony performed by two real processes ([implementation status](implementation-status.md#0030--the-socket-exists)). [Q18](open-questions.md#q18--streaming-restored-content-to-a-remote-client) and [Q19](open-questions.md#q19--console-identity-and-multi-operator-access) remain open and gate what a paired console may *do*, which is a separate question from who it is. The two exit criteria below that needed it — the unpaired-client refusal and the no-plaintext-across-the-remote-binding claim — are now met, closing topologies 3 and 4 of [ADR-0028 §1](adr/0028-service-boundary-and-deployment-topologies.md); what remains of Phase 2 is the rest of peer replication. Its first slice is built: the hub pushes a repository's immutable objects to a paired destination over the peer session ([peer-protocol 03](../specifications/peer-protocol/03-replication.md)) — on every backup via fan-out, on demand via the `sync` verb — the destination holds a replica it cannot read, and the standalone recovery tool restores the original files from it — a source recovered from its destination, proven end to end. Destination verification ([spec 04](../specifications/peer-protocol/04-verification.md)) is built: every sync challenges a bounded sample of the destination's copy — a peer over keyed range proofs, a local path by direct read-back — the newest snapshot is always covered, and `verified` in a status line carries coverage and age from the sync ledger's stamps, earned only by an in-sync destination whose declared failure domain survives losing this machine (FR-SNP-007, [ADR-0018 Amendment 2](adr/0018-replica-failure-domains.md)). The rest of the remainder — pairing roles on the wire, termination notices, quota enforcement (spec 05) — is sequenced in the [hub-and-spoke arc](#the-hub-and-spoke-arc--multi-destination-backup-sets-built) below, and the 03 refinements (snapshot scoping, the compact object-set filter) are superseded there by per-set archives, which make whole-archive replication per-set by construction ([ADR-0034](adr/0034-hub-and-spoke-destinations.md)). Restore, verify and check are served over the command surface, on the job queue's reader lane so a restore runs alongside a scheduled backup rather than behind it. **Service installation is built too** ([ADR-0033](adr/0033-hosting-under-an-os-service-manager.md)): the agent shuts down cleanly on the stop signal systemd and launchd send, bridges the Windows Service Control Manager, and its `install` verb generates the systemd unit, launchd plist or Windows `sc.exe` commands that register it — signed installers and self-contained packaging remain a Phase 4 item. **And the local web UI landed after the phase closed** ([ADR-0036](adr/0036-local-web-console.md)): `fallbackplan-web` is a client of the running service over the same contract — loopback only, a per-run token standing in for the socket's peer credentials, the full command surface with restore and retention-apply behind typed confirmations, and live job progress bridged onto server-sent events. It relays the service's answers and derives nothing; an unreachable service renders as stale with the age of last contact (NFR-OPS-006). Q18 and Q19 stay open and respected: no content streams to the page, and the console serves the operator who launched it. **And the console now manages configuration** ([ADR-0037](adr/0037-configuration-over-the-command-contract.md)): set and destination CRUD over contract 1.7 with schedules validated at the boundary and include rules finally enforced at capture, plus site-to-site pairing by spoken one-time invite ([ADR-0030 Amendment 4](adr/0030-peer-identity-and-pairing.md#amendment-4-2026-08--the-invite-authenticated-ceremony-pairing-without-two-humans-present-at-once)) — issue a code on one console, enter it with an address on the other, and the grant pins on both ends with no simultaneous ceremony. **And a set edit now answers with its meaning** ([ADR-0038](adr/0038-set-change-rescan-and-notice.md), contract 1.8): a material change to a set's root or rules triggers a rescan against the last backup whose finding — new, updated, moved, deleted, and no-longer-included files — stands as a durable notice until the next backup completes, the same comparison is commandable on demand (`fallbackplan changes`, the web editor's preview), and `run_backup --full` is honoured over the service instead of silently dropped. **And the console's operator loop closed** ([ADR-0039](adr/0039-console-operator-loop.md), contract 1.9): notices are listed structured and acknowledged over the contract — with the agent's own ack verb routing through a listening service instead of racing its file — a pairing is ended from the console with the announcement, refusal-while-referenced and tombstone semantics of the agent verb (shared code, not a copy), and the snapshot browser became a small time machine: modification times, new/changed markers against the set's previous snapshot, the names deleted since it, and older/newer navigation at the same path. **And a backup set now captures several folders into one snapshot** ([ADR-0040](adr/0040-multi-root-backup-sets.md), contract 1.10, FR-SNP-008): one root keeps the old byte shape exactly, several publish under persisted labels in the tree codec's raw-byte order with label-prefixed rule subjects and a conservative `source_filesystem` intersection, the upsert materialises labels once and re-anchors saved rules across the 1↔N coordinate change, a vanished root refuses the run recoverably by name, and the console's editor became a machine-wide checkbox tree — tick folders on any drive, untick children, re-tick under an unticked parent and be warned about the exclude-wins wall — with an instant summary and a debounced draft-roots preview that answers even a brand-new set against an empty baseline. **And restore became a guided walk** ([ADR-0041](adr/0041-guided-restore-and-peer-retrieval.md), contract 1.11): a console-local passphrase gate that never touches the contract, restore sources spanning the staging archive, a local replica and a **paired peer's replica over the wire** ([peer-protocol 07](../specifications/peer-protocol/07-retrieval.md) — the retrieval feature that finally lets a hub read back what it fanned out, proven by deleting the staging archive and restoring byte-identical files from the peer), an effective-date picker over the source's own snapshots, multi-select restores as one run with one persisted receipt, and honest run options — a chosen folder or the original roots, keep-both-beside or overwrite — with the quarantine default untouched when nothing is asked for. **And a repository can now be created write-only** ([ADR-0042](adr/0042-write-only-repositories.md), format v2, contract 1.12): one passphrase — entered at setup, at adoption of a moved archive, and at restore, stored nowhere in any form — derives an X25519 public key that seals every file's content and the symmetric write bundle the service keeps, so the hub can add to history, browse, plan, dedup, trim, replicate and structurally verify but **never read a file back**; provisioning and restore grants cross the contract only as envelopes sealed end-to-end to the service's published recipient key (NFR-SEC-009 as amended), the service starts without a passphrase, the CLI creates behind a typed loss acknowledgement, the console runs both ceremonies in its own process, and the whole journey — including moving the archive to a fresh machine, where even metadata stays unreadable until the passphrase re-enters — is proven by service drills, a committed conformance fixture, and a live browser walk ending on byte-identical restored files.

**Exit criteria (service boundary):** a second process cannot take the writer role — it refuses with a stated reason naming the holder · a default install listens on no port · an unpaired remote client is refused, and a substituted identity is refused rather than prompted · a restore commanded remotely writes on the service's machine and no plaintext crosses the remote binding · a running job reports states beyond `Scanning` · a service with no front end installed backs up unattended, and an unreachable console never stops it · client and service at incompatible versions refuse with both versions named.

**Exit criteria (peer-to-peer):** the source can be destroyed and restored using only destination plus recovery kit · no relay required on a LAN · no destination plaintext visibility · multi-day disconnection and resumption tested · quota exhaustion handled distinctly from disk-full · verify-on-reuse prevents a hostile writer corrupting another device's backup.

---

## The hub-and-spoke arc — multi-destination backup sets *(built)*

[ADR-0034](adr/0034-hub-and-spoke-destinations.md) re-architects the middle of the product: a backup set gains named destinations and retention, each set gets its own staging archive on the hub, and the hub fans every snapshot out to all of its destinations and runs retention against them. The arc cuts across the phase list rather than following it — it **completes Phase 2's remainder** (pairing roles on the wire, termination notices, terms enforcement — spec 05), **pulls Phase 4's retention forward** (nothing in "one archive becomes N, each aging under policy" is sensible to build twice), and **prepares Phase 3** (cloud kinds are modelled in configuration, status and retention now, so a provider lands later as an `IObjectStore` behind the existing fan-out, not a feature).

Ordered slices, each shippable, each proven by a failing-test-first suite:

1. **Configuration schema v2** — top-level named destinations (`local-path`, `peer`; `s3`/`azure-blob`/`dropbox` schema-reserved), per-set destination references and retention policy, v1 rejected with a migration message (FR-DEST-001/005/006).
2. **Per-set staging archives** — `ServiceRuntime` holds one archive handle per set (own writer sequence, catalogue, spool), opened lazily; two sets publish independently and each archive restores alone.
3. **Fan-out to local-path destinations** — the store-to-store copier extracted from the replication initiator, per-`(set, destination)` sync state, the transfer job lane, scheduler-driven catch-up, the `sync` verb superseding `replicate` (FR-DEST-002/003/004).
4. **The status matrix** — per-destination inputs, `protected` requiring an off-domain in-sync destination, real volume-identity failure-domain comparison (ADR-0027 amendment).
5. **Peer destinations** — the negotiated role in the pairing ceremony, fan-out over the peer session from the configured endpoint.
6. **Termination and notices** — the termination message, the durable notice store, `Revoked` as the fallback signal, `unpair --notify` (FR-DEST-008).
7. **Terms enforcement** — quota at the blob boundary, quota ≠ disk-full ≠ transient, narrowing surfaces as degraded ([spec 05](../specifications/peer-protocol/README.md)).
8. **Local retention engine** — planner, mark and sweep against staging under every ADR-0009 mechanism, the replication gate (FR-GC-009), first production deletion.
9. **Retention against local-path destinations** — per-destination convergence under policy overrides (FR-GC-010).
10. **Retention against peers** — hub marks, spoke deletes, floor refusals (spec 06).
11. **Staging trim** *(the stretch slice, landed; superseded for direct-ship sets by the arc below — with no staging copy there is nothing to trim, and per-destination convergence is the deleting half)* — once every destination entitled to a historic data blob verifiably holds it, staging drops it under `retention --apply`; the newest snapshot's closure stays as the dedup cache, so a set whose only destination shares its volume pays same-disk storage for the current generation, not for history ([ADR-0034 §6](adr/0034-hub-and-spoke-destinations.md#6-the-costs-accepted)).

**Status:** all eleven slices are built and proven, the stretch trim included, and the operator surface landed with them — `sync` superseded `replicate`, and `retention` is commandable from a paired console ([implementation status](implementation-status.md#0034--the-hub-fans-out-ages-and-trims)). Destination verification ([spec 04](../specifications/peer-protocol/README.md#documents)) remains the Phase-2 tail outside this arc. This shape is no longer the product's resting state: the direct-to-destination arc below supersedes the staging archive and the fan-out-as-write-path for `direct_ship` sets, and this arc's machinery remains for unflagged sets until that flag's default flips.

**Exit criteria:** a set with two destinations — one local-path, one peer — and no other local copy backs up on schedule to both when available · the destination that was offline catches up automatically · `status` shows the per-destination matrix and `protected` is earned only by an off-domain in-sync destination · retention ages each destination under its own policy with the spoke's floor honoured · either peer ends the peering and both sides see a durable notice · each destination archive restores independently with the recovery tool.

### The destination-fitness arc — is this destination one a backup can be built on? *(built)*

[ADR-0035](adr/0035-destination-fitness.md) closes the gap the arc above left: the hub could fan out to a destination, age it, trim against it and challenge it — but almost everything it knew about whether that destination was *fit* it learned by trying to use it. Six findings, each verified against the code: a destination that had silently lost data was silently re-seeded; age was invisible, so day 1 and day 400 read identically; an uncomputable convergence filter was discarded without a word; nothing checked capacity anywhere; nothing probed a destination before the first full copy counted on it; and sampling coverage never accumulated, so FR-VER-002's weighting was specified and unimplemented.

Underneath all six sat one structural fact — **verification only ever ran inside a sync, and a sync only ran when the archive moved on** — which is why an idle set froze its own proof with nothing saying so, and why the scheduled sweep had to land before staleness could honestly be complained about.

**Status:** built. Admission (address defects reported, never refused at load; `verify-destination --probe` for both kinds), shortfall detection from what the destination declares holding, named convergence refusals, the scheduled deep sweep on the transfer lane with its on-demand depths, an accumulating sampler rotation, age as a warning that does not move the state, and capacity on both halves — headroom on the replication inventory frame, a free-space floor for a local path ([implementation status](implementation-status.md#0035--a-destination-has-to-earn-being-relied-on)). **Peer-side deep verification is the one piece not built:** a peer replica has no readable store this side of the wire, so re-reading its bytes needs the session-establishment half of the push extracted first — the admission probe took the first half of that extraction.

**Exit criteria:** a destination that quietly deletes objects is named on the next sync rather than silently re-seeded · a destination unproven past its bound is named in status without the protection state becoming ambiguous · a convergence filter that could not be computed says so instead of quietly taking a whole copy · a typo'd endpoint is reported before anything counts on it and a new destination can be probed before the first full copy · a peer push about to run out of room says so a pass early and a local copy that would fill the volume does not start · a replica's stored bytes are re-confirmed against their seals on a schedule, with sampling coverage that provably accumulates.

### The direct-to-destination arc — no staging copy, a backup pool, priorities and preemption *(built behind a default-off flag)*

[ADR-0046](adr/0046-direct-to-destination-publication.md) and [ADR-0047](adr/0047-backup-pool-and-priorities.md) re-architect where a backup's bytes live and how runs are ordered, on the owner's direction. A set flagged `direct_ship` publishes through the **ship sink**: blobs go straight to the set's in-scope destinations, metadata to a local **metadata store** plus the destinations, and the agent holds no whole-archive copy — the accepted trade being that a capture with no reachable destination refuses (ADR-0046 §4). Fan-out survives as the catch-up and seeding pump through the sink; restore, verification and the retention traversal are proven destination-backed. Around it, the **writer pool** (`max_concurrent_backups` 1..5, default 2), set and destination **priorities** under user-initiation (contract 1.17), first-backup-on-save, baseline/`needs_full` seeding (sync ledger v2, surfaced by contract 1.19), **preemption with true suspend/resume** (a higher-priority run parks a lower one at a file boundary and it resumes without re-scanning — ADR-0047 Amendment 1), and the migration path with its explicit `retire_staging` verb (contract 1.18).

**Status:** built and proven end to end, and now the default for new local-path sets — the flag rides the contract (1.23) and the console, the retention-with-trimming drill has run, and staging is the explicit opt-out. The remaining tail is the peer write adapter (a peer destination is a stated `NotSupported` in the ledger for direct-ship; peer-only sets default to staging until it lands), after which the staging machinery can retire.

**Exit criteria:** a saved set with a local destination on another volume holds a restorable repository there immediately after the triggered first backup, with nothing but metadata under the agent's state directory · a scheduled incremental starts promptly while a long transfer runs · a higher-priority trigger pauses a lower-priority running backup when the pool is full and resumes it to a single whole snapshot · a destination without a full copy is skipped by incrementals and seeded by catch-up · retiring a migrated set's staging archive is refused while anything would be lost and succeeds after catch-up.

---

## Phase 3 — Cloud object stores

Reframed by [ADR-0034](adr/0034-hub-and-spoke-destinations.md) and again by [ADR-0046](adr/0046-direct-to-destination-publication.md): a cloud store is one more **destination kind** behind an `IObjectStore` — served by the fan-out for staging sets and by the ship sink for direct-ship sets; configuration, status, retention and quota semantics already exist for it, and what Phase 3 adds is the provider.

**Features:** Azure Blob provider · S3 and S3-compatible provider · credential integrations · multipart and staged-block uploads · request and cost telemetry · tier and lifecycle guidance · replication between peer and cloud stores.

**Exit criteria:** provider contract suites pass, including eventual-visibility and quota simulation · large interrupted uploads resume or safely restart · no object listing required per segment · PUTs per GB within NFR-PERF-008 · documented and tested restore from a clean machine.

---

## Phase 4 — Retention, pruning, and healing

Largely absorbed by the hub-and-spoke arc, which builds the retention engine, mark and sweep, the replication gate, and per-destination retention (arc slices 8–10). What remains here is the tail: blob compaction, replica healing, and the audit machinery around destructive change.

**Features:** ~~retention engine · generation-based mark and sweep · write-intent-aware reachability~~ *(arc)* · blob compaction via index republication · tombstone grace periods · mandatory dry-run reports · replica healing · damaged-snapshot scoping · ~~retention floors~~ *(arc)* and destructive-action auditing.

**Exit criteria:** interruption at every GC step preserves published snapshots · GC concurrent with backup never deletes in-flight blobs · compaction modifies no manifest · deleted content retained per policy · corruption healed from another replica · clock skew of ±24 h changes no GC outcome · **and the twelve compaction criteria of [ADR-0025 Amendment 2](adr/0025-compaction-reseals-records.md#amendment-2-2026-08--the-twelve-things-compaction-is-known-to-get-wrong)**, each with a test.

Those twelve are not padding. Compaction is the densest cluster of shipped fixes in the surveyed product's entire fifteen-year changelog — 29 of 805 distinct entries, more than any other mechanism, recurring every year from 2016 to 2026 ([ledger](review/2026-08-prior-art-changelog-ledger.md)). They are written down now, while nothing is built, because that is the only moment the list is free.

---

## Format v1 freeze gate

Not a phase — a gate that must pass before the format is declared stable. Until it does, [ADR-0014](adr/0014-format-versioning-and-stability.md) applies and repositories carry **no** forward-compatibility guarantee.

1. **Segmentation benchmark published.** `fixed-v1` versus `cdc-v1` on a representative corpus, reporting deduplication ratio, storage growth, and CPU cost. If CDC wins decisively, the default changes here — while it is still free to change ([`architecture/02-repository-format.md` §3.3](architecture/02-repository-format.md#33-the-freeze-gate)). *First round published:* [`segmentation-benchmark.md`](segmentation-benchmark.md) — cdc-v1 wins decisively on shifted content (4–6×) and is recommended as the gate-time default; a real-corpus run on reference hardware must still settle the target size before the item closes.
2. **Independent reader.** A reader written from the published specification alone, by an author who did not write the format, in a different language, passes the conformance fixtures. This is the real test of NFR-COMP-004 — and it is what [Q4](open-questions.md#closed) handed over when it closed: `conformance/generate.py` already reproduces every object byte for byte with its own encoder in another language, but the same author wrote both, which is exactly the limitation this item removes.
3. **Specification and fixtures public** under `specifications/`.
4. **External format review** completed.
5. **Threat model reviewed** against the frozen format.
6. **Licence decided** — [ADR-0001](adr/0001-licence-and-contribution-model.md) Accepted, `LICENSE` present. *Done:* dual AGPL-3.0-only + commercial for code, Apache-2.0 for `specifications/` — the permissive spec carve-out is what keeps item 2's independent reader unencumbered.

Supporting measurements published against the gate: [segmentation](segmentation-benchmark.md) for item 1, and [metadata encoding size](metadata-encoding-benchmark.md), which closed [Q4](open-questions.md#closed) and handed its residue to item 2.

Two decisions the gate forced rather than measured are written up in [freeze-gate decisions](freeze-gate-decisions-2026-08.md): whether verify-on-reuse outcomes get a durable repository object (they do not), and how a POSIX name with no valid decoding is rendered where a host string is unavoidable (percent-encoding). Both were settled here because the alternative was a v1 object designed on speculation, or four call sites each inventing a convention.

---

## Phase 5 — Legacy archive import preview

Controlled read-only import for validated archive variants. Gated on the legal review in [ADR-0015](adr/0015-legacy-importer-isolation.md) — **no parser work begins before that gate passes**.

**Features:** archive inspector and variant detection · key-source adapters · latest-state import · selected historical import · resumable checkpoints · provenance and migration reporting · source/destination hash comparison · opaque archive-preservation option.

**Exit criteria:** compatibility matrix published · diverse real-world fixtures tested · no source mutation, verified by digest · unsupported cases identified before high-volume processing · imported snapshots restore independently of the importer.

Treated as **experimental** until validated against diverse real archives, and never broadly claimed before then.

---

## Phase 6 — Consumer-ready release

**Features:** polished desktop shell · guided pairing · plain-language policy templates · notifications · update controls · recovery drills · replacement-device workflow · accessibility (WCAG 2.2 AA) · localisation · signed installers and packages.

**Exit criteria:** non-technical usability testing · one-click recovery drill · degraded protection is visibly distinguished from healthy and from unrecoverable · accessibility audit passed · external security review completed.

---

## Backlog

### P0 — Engine and foundations

1. Governance: licence decision, `CONTRIBUTING.md`, `SECURITY.md`, release checklist.
2. .NET 10 solution, architecture-boundary tests, benchmark projects.
3. Repository, segment, blob, manifest, key-generation, and profile identifiers.
4. Configuration schemas with validation.
5. Streaming segment reader and sparse-extent representation.
6. Content-identifier hashing, positional prior-version comparison, reusable-segment lookup.
7. Canonical metadata encoding, chosen after cross-language determinism tests ([ADR-0003](adr/0003-canonical-metadata-encoding.md)).
8. Per-blob key derivation and AEAD record encryption with enforced profiles ([ADR-0005](adr/0005-aead-suite-and-nonce-construction.md)).
9. Compression selection with the measured threshold.
10. Local immutable object store.
11. Blob spool, writer, sealer, reader, authenticated footer, corruption detection.
12. Immutable file-version manifests with **logical-only** segment references ([ADR-0007](adr/0007-logical-object-identifiers-in-manifests.md)).
13. SQLite catalogue with path, version, segment, blob, and generation indexes.
14. Index deltas with per-writer chains, and checkpoints that enumerate what they subsume ([ADR-0008](adr/0008-index-generations-and-checkpoints.md)).
15. Write-intent journal records ([ADR-0009](adr/0009-garbage-collection-safety.md)).
16. Normal catalogue rebuild.
17. Forensic catalogue rebuild.
18. File restore with per-segment and whole-file verification.
19. Publication ordering and an exhaustive interruption harness.
20. Repository specification draft, conformance fixtures, recovery vectors.
21. Benchmark `fixed-v1` **and** `cdc-v1` — this is a Phase 0 deliverable, not a later spike.

### P1 — Usable local backup

Streaming scanner · include/exclude rules · snapshot pipeline · CLI · restore planner and verifier · recovery kit format and drill · three-way local state separation · scheduling and Agent service · OpenTelemetry · clean-machine recovery test.

### P2 — Peer destination

Device identity and pinning · pairing · transfer protocol · resumable blob transfer · LAN discovery · quotas, permissions, retention floors · durability receipts and verification challenges · dedup trust domains · **service boundary: command surface, transports, writer-role exclusion, keystore unlock, progress events** · web dashboard · **multi-instance console** · network fault and long-disconnection tests · relay design spike.

### P3 — Cloud destinations

Provider capability contract · Azure Blob · S3 · emulator integration tests · nightly real-provider tests with cost limits · multipart recovery · credential rotation and expiry · request and cost telemetry.

### P4 — Retention and healing

Retention engine · generation GC · compaction · tombstones and grace periods · dry-run reporting · replica healing · clock-skew testing.

### P5 — Legacy format research

**Legal and licence review gate first.** Then: archive corpus and variant catalogue · read-only inspector · key-material adapters · manifest and history parser prototypes · block decrypt/decompress pipeline · neutral legacy model · single-version streaming import proof · historical state reconstruction · comparison and reporting.

---

## Definition of done for 1.0

- Windows, macOS, and Linux agents create and restore native snapshots.
- One computer backs up directly to another with no project-operated service.
- A backup set fans out to every configured destination, none of which has to be local; protection means a configured destination outside the source's failure domain is in sync, never merely that a local copy exists ([ADR-0034](adr/0034-hub-and-spoke-destinations.md)).
- Azure Blob and S3 repositories pass the shared contract suite.
- All content and metadata are encrypted before leaving the source trust boundary.
- A repository restores from a clean machine using only repository access and a recovery kit.
- Deleting the catalogue loses no repository history, and does not take device identity with it.
- Interruption tests cover publication, replication, restore, and maintenance.
- Repository check identifies missing and corrupted objects and names the affected scope.
- Retention and pruning are implemented with dry-run and grace-period support.
- Repository and peer protocol specifications are public.
- Reproducible conformance fixtures are public.
- Signed release artifacts and a standalone recovery tool are available.
- User documentation explains what is and is not protected.
- External security and format reviews are complete.
- The format v1 freeze gate has passed.
- Legacy import is released with a narrow verified compatibility matrix, or clearly retained as preview — never broadly claimed.

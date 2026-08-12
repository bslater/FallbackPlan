# Implementation status

**Status:** maintained · **Checked by:** [`eng/check-adr-status.py`](../eng/check-adr-status.py)

---

Thirty decision records say what this system should do. This says which of them the code actually does, and — where the answer is "some of it" — which part.

It exists because the two drift apart silently and in one direction. An ADR is written before the work and is never wrong afterwards; nothing in it goes red when the thing it decided turns out to be half-built. The [traceability matrix](requirements/traceability.md) had exactly this failure and had to be rebuilt from fiction: 73 of its 86 test citations named classes nobody had written. That repair is the reason this page cites files rather than intentions, and the reason a checker resolves it on every run.

**A row claims only what a named file establishes.** Where a decision is partly built, the row says so and the section below it names the missing half. "Specified only" is not a criticism — most of those are phase 3 and 4 work that is correctly not started — but it is never left to be inferred from silence.

**Legend**

| State | Means |
|-------|-------|
| **Built** | The decision is in the code and tests hold it to it |
| **Partly built** | A named part shipped and a named part did not — see the notes below |
| **Specified only** | Decided and written down; nothing implements it yet |
| **Applied** | Not code: a licence, a policy, or a build arrangement that is in force |

---

## By decision

| ADR | Decision | State | Where it is |
|-----|----------|-------|-------------|
| [0001](adr/0001-licence-and-contribution-model.md) | Licence and contribution model | **Applied** | [`LICENSE`](../LICENSE), [`LICENSING.md`](../LICENSING.md), [`CONTRIBUTING.md`](../CONTRIBUTING.md) |
| [0002](adr/0002-segmentation-strategy.md) | Segmentation strategy | **Built** | `Repository.Segmentation/FixedSegmentReader`, `Repository.Segmentation/CdcSegmentReader` · `Repository.ConformanceTests/SegmentationConformanceTests` |
| [0003](adr/0003-canonical-metadata-encoding.md) | Canonical metadata encoding | **Built** | `Repository.Format/Cbor/CanonicalCbor*` · `Repository.FuzzTests/ParserFuzzTests` |
| [0004](adr/0004-segment-hash-function.md) | Segment hash function | **Built** | `Repository.Crypto/ContentHasher`, `Domain/Profiles/ContentHashProfile` · `Repository.ConformanceTests/IdentifierConformanceTests` |
| [0005](adr/0005-aead-suite-and-nonce-construction.md) | AEAD suite and nonce construction | **Built** | `Repository.Crypto/RecordCipher`, `Repository.Crypto/BlobKeyDeriver` · six requirements, all traced |
| [0006](adr/0006-object-identifiers-and-dedup-trust-domains.md) | Object identifiers and dedup trust domains | **Built** | `Repository/DedupTrustGate` · [notes](#0006--the-integrity-guard-is-built-and-one-thing-is-deliberately-not) |
| [0007](adr/0007-logical-object-identifiers-in-manifests.md) | Manifests carry logical identifiers only | **Built** | `Repository.Format/Manifests/*`, `Repository.Format/Manifests/SourceIdentityHint`, `Repository/SourceIdentityLookup` · `Repository.Tests/Index/IndexPrecedenceTests`, `Repository.Tests/Format/SourceIdentityHintCodecTests` · [notes](#0007--device-specific-facts-live-outside-the-manifest-and-one-of-the-two-is-built) |
| [0008](adr/0008-index-generations-and-checkpoints.md) | Index generations, deltas, checkpoints | **Built** | `Repository.Index/CheckpointCodec`, `Repository.Index/IndexDeltaCodec`, `Repository.Index/WriterSequence` |
| [0009](adr/0009-garbage-collection-safety.md) | Garbage collection safety | **Partly built** | `Repository.Index/Journal/IntentLifecycle` · [notes](#0009--the-intents-are-written-nothing-collects-yet) |
| [0010](adr/0010-local-store-separation.md) | Local store separation | **Built** | `Application/LocalState` · `Repository.Tests/EndToEnd/LocalStateSeparationTests` |
| [0011](adr/0011-commit-versus-replication-semantics.md) | Commit versus replication semantics | **Partly built** | `Repository/SnapshotPublication` · [notes](#0011-0018--commit-is-per-replica-and-there-is-one-replica) |
| [0012](adr/0012-storage-provider-contract.md) | Storage provider contract | **Partly built** | `Storage.Abstractions`, `Storage.Local` · `Storage.ContractTests` · [notes](#0012--the-contract-is-real-it-has-one-provider) |
| [0013](adr/0013-recovery-kit.md) | Recovery kit contents and format | **Built** | `FallbackPlan.Recovery`, [`specifications/recovery-kit/`](../specifications/recovery-kit/README.md) · `Repository.ConformanceTests/RecoveryKitConformanceTests` |
| [0014](adr/0014-format-versioning-and-stability.md) | Format versioning and pre-1.0 posture | **Built** | `Repository/RepositoryLifecycle` · `Repository.Tests/EndToEnd/RepositoryLifecycleTests` |
| [0015](adr/0015-crashplan-importer-isolation.md) | CrashPlan importer isolation | **Partly built** | `FallbackPlan.Import.Abstractions` · [notes](#0015--the-seam-is-the-decision-and-the-seam-is-built) |
| [0016](adr/0016-blob-identifier-formation.md) | Blob identifiers are writer-allocated | **Built** | `Domain/Identifiers/BlobId`, `Domain/IBlobCounterAllocator` · `InterruptionTests/SequenceRollbackTests` holds the refusal when an identifier is ever reused |
| [0017](adr/0017-index-entry-supersession.md) | Index entry supersession and precedence | **Built** | `Repository.Index/IndexEntry`, `Repository.Index/IndexLoader` · `Repository.Tests/Index/IndexPrecedenceTests` |
| [0018](adr/0018-replica-failure-domains.md) | Replica failure domains | **Specified only** | [notes](#0011-0018--commit-is-per-replica-and-there-is-one-replica) |
| [0019](adr/0019-third-party-dependency-policy.md) | Third-party dependency policy | **Applied** | `ArchitectureTests/DependencyRuleTests` — the policy is a test, not a promise |
| [0020](adr/0020-ed25519-signing-key-semantics.md) | Ed25519 signing key semantics | **Built** | `Repository.Crypto/RepositorySigner` · `Repository.ConformanceTests/Ed25519ConformanceTests` |
| [0021](adr/0021-consume-bodu-via-committed-package-feed.md) | Bodu from a committed local feed | **Applied** | [`external/packages/`](../external/packages/README.md), [`nuget.config`](../nuget.config) |
| [0022](adr/0022-standalone-metadata-records-and-index-identifiers.md) | Standalone records and index identifiers | **Built** | `Repository.Format/Records/*`, `Repository.Index/IndexDeltaCodec` · `Repository.FuzzTests/ParserFuzzTests`, `Repository.Tests/Index/IndexPlaneTests` |
| [0023](adr/0023-cdc-v1-rabin-parameters.md) | cdc-v1 Rabin fingerprint parameters | **Built** | `Repository.Segmentation/RabinFingerprint` · `Repository.FuzzTests/CdcPropertyTests` |
| [0024](adr/0024-include-exclude-rule-dialect.md) | Include/exclude rule dialect | **Built** | `Domain/PathRules` · `Repository.ConformanceTests/PathRulesConformanceTests` |
| [0025](adr/0025-compaction-reseals-records.md) | Compaction re-seals records | **Specified only** | [notes](#0025--nothing-compacts-yet-so-nothing-re-seals-yet) |
| [0026](adr/0026-phase-1-capture-shapes.md) | Phase-1 capture shapes | **Partly built** | `Filesystem.Local/LocalFileSystemSource`, `Filesystem.Local/PosixInterop`, `Filesystem.Local/PosixHandleInterop`, `Filesystem.Local/PosixDirectoryScope` · `Filesystem.Tests/LocalScanTests` · [notes](#0026--the-shapes-are-captured-the-posix-traversal-is-handle-relative-and-one-gap-is-left) |
| [0027](adr/0027-services-scheduling-status-telemetry.md) | Scheduling, job state, status, telemetry | **Built** | `FallbackPlan.Agent`, `Application/JobStateStore` · `Hosts.Tests/*` |
| [0028](adr/0028-service-boundary-and-deployment-topologies.md) | The service boundary | **Partly built** | `FallbackPlan.Api`, `Cli/OperationGateway` · [ADR §Implementation status](adr/0028-service-boundary-and-deployment-topologies.md#implementation-status-2026-08) |
| [0029](adr/0029-pipeline-and-service-concurrency.md) | Pipeline and service concurrency | **Built** | `Repository/ArchiveSession` · [ADR §Implementation status](adr/0029-pipeline-and-service-concurrency.md#implementation-status-2026-08) |
| [0030](adr/0030-peer-identity-and-pairing.md) | Peer identity and pairing | **Partly built** | `FallbackPlan.Protocol` · [notes](#0030--the-socket-exists) |
| [0031](adr/0031-exception-messages-are-resources.md) | Exception messages are resources | **Built** | `Domain/Resources/Strings.g.cs`, `Repository.Format/Resources/Strings.g.cs`, [`eng/generate-resources.py`](../eng/generate-resources.py) · CI: accessors match their resx |
| [0032](adr/0032-mstest-as-the-test-framework.md) | MSTest is the test framework | **Built** | `TestSupport/PlatformFacts.cs`, `TestSupport/PropertyCheck.cs`, `TestSupport/SequenceAssert.cs` · 966 tests, count verified identical across the move |
| [0033](adr/0033-hosting-under-an-os-service-manager.md) | Hosting under an OS service manager | **Partly built** | `Agent/ServiceProcessHost.cs`, `Agent/WindowsServiceHost.cs`, `Agent/ServiceUnit.cs` · [notes](#0033--the-os-can-own-the-process) |
| [0034](adr/0034-hub-and-spoke-destinations.md) | Hub-and-spoke destinations | **Partly built** | `Application/DestinationConfiguration` · `Application.Tests/ClientConfigurationTests` · [notes](#0034--the-configuration-speaks-it-nothing-serves-it-yet) |

---

## Where "partly" is doing work

### 0006 — the integrity guard is built, and one thing is deliberately not

Object identifiers, the keyed derivation behind them, the domain enumeration — and now the guard the enumeration was for.

`DedupTrustGate` decides every reuse, segments and metadata objects alike, and it reads **writer attribution first, domain second**. A segment this writer wrote is reused in every domain with no read at all, which is what makes the default affordable and is the literal text of FR-DED-002's acceptance: a second backup of an unchanged single-writer tree issues **zero** store reads, measured rather than asserted. Another writer's object is refused outright under `device`, referenced unread under `repository-unverified`, and under the default `repository` is fetched, decrypted and confirmed before it is referenced. The confirmation is the record read's own 04 §6 step 7, so there is no second verification path that could disagree with the first.

**This is [C3](review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard)'s remedy, present.** A record that reads and does not verify is written again from the bytes this device holds and reported as a damage finding against the object — detection at write time, while the source data is still there. [T-10](threat-model.md) is mitigated under the default and closed under `device`. Six end-to-end tests in `Repository.Tests/DedupTrustDomainTests` hold it, and they are the only suite in the repository with two writers, because with one writer all three domains are indistinguishable by design.

**What is deliberately not built** is a durable home for verification outcomes. They live in the catalogue, so deleting it re-imposes the read once. The alternative was a repository object recording them — format surface frozen into v1 before anything consumes it, to avoid a cost that only exists in a multi-writer repository. [PT-12](review/2026-08-fix-pressure-test.md#pt-12--device-attribution-and-verify-on-reuse-state-live-only-in-a-disposable-cache) offered both and this takes the second; FR-DED-003's acceptance criterion was amended to say so rather than left claiming otherwise.

`FR-DED-004` is the row that stays unmet: `repository-unverified` works, but nothing requires the acknowledgement that turning it on means accepting another member can corrupt your backup. That gate belongs where the domain is chosen, which is a client, not the engine.

### 0007 — device-specific facts live outside the manifest, and one of the two is built

ADR-0007's rule is that a manifest carries logical identifiers only, and its [amendment](adr/0007-logical-object-identifiers-in-manifests.md) settled what happens to the device-specific facts that rule excludes: they become separate optional objects per snapshot, so a manifest's bytes stay identical across devices and cross-device deduplication keeps working.

Two such objects are specified. The **source-identity hint** ([06 §11](../specifications/repository-format/06-manifests.md#11-source-identity)) is built — written by `PublicationOrchestrator`, read by `SourceIdentityLookup`, and consulted when the catalogue cannot say which prior version an inode belongs to. That is the case a catalogue rebuild produces, and without it a file renamed in that window would record no `parent_version` at all, losing its history permanently because a disposable cache was cold. Three end-to-end tests hold it: the rename keeps its ancestry across a rebuild, a file untouched for several snapshots still finds its ancestor when it moves, and deleting every hint costs exactly that ancestry and nothing else.

It is keyed by **source key** rather than by snapshot, and the difference is the whole of [Q21](open-questions.md#closed): one object per file version created, so per-snapshot cost follows what changed. The first shape named every file the snapshot contained and cost ~52 bytes per file every run — the growth NFR-PERF-005 forbids, and the reason that requirement could not be asserted on total store bytes until this changed.

A renamed file no longer pays for its move in reads either. `PriorManifestSource` resolves the prior version's location through the catalogue and opens that one blob through its recovery footer, so the publisher can fetch the prior manifest and rewrite it under the new name instead of re-reading the file — a handful of range reads in place of the whole file. It is best-effort: a manifest that cannot be fetched or decoded sends the file down the ordinary capture path and raises no finding. It also needs the catalogue, so after a rebuild the hints recover the ancestry and the content is read again, because a hint names an object and not its location.

The **placement hint** ([06 §10](../specifications/repository-format/06-manifests.md#10-placement-hint)) is specified and not built. It is a `MAY`, and the thing it accelerates — single-file emergency recovery without an index — has no implementation to accelerate yet; it is worth writing alongside that path rather than before it.

### 0009 — the intents are written; nothing collects yet

The half that protects data is built: write-intent journal records, the intent lifecycle, and the rule that any component creating a blob publishes an intent first — including the collector, per [PT-3](review/2026-08-fix-pressure-test.md). Leases are advisory, as decided.

The collector itself does not exist. There is no mark, no sweep, no compaction; `grep` for a collector in `src/` returns the journal record type that *describes* one. That is phase 4 and on plan. The consequence worth stating plainly is that **nothing currently reclaims space**, so a repository grows monotonically, and the safety machinery in place is protecting against a process that has not been written.

### 0011, 0018 — commit is per-replica, and there is one replica

The decision that a snapshot commits per destination rather than globally is in the publication model, and a local repository exercises the single-replica case. Everything that makes the decision *matter* — a second destination, per-destination replication state, failure domains that differ — arrives with replication. ADR-0018 is therefore specified only: `FR-SNP-007` has no test because the situation it describes cannot yet occur.

### 0012 — the contract is real; it has one provider

`Storage.Abstractions` defines the contract, `Storage.ContractTests` is a reusable suite any provider must pass, and `Storage.Local` passes it. This is the shape the decision asked for, and the shape is what protects the design.

It is still one provider. A contract with a single implementation has not yet been tested by the thing it exists for — the second implementation that disagrees with it. Azure and S3 are phase 3, and `NFR-PORT-002` is traced against the architecture tests and the contract suite rather than against a provider that proves portability by being different.

### 0015 — the seam is the decision, and the seam is built

ADR-0015's decision was to isolate a CrashPlan importer behind a boundary, not to write one. `FallbackPlan.Import.Abstractions` is that boundary, and phase 0's exit criteria proved it with a synthetic adapter feeding an arbitrary byte stream through the same pipeline ([roadmap](roadmap.md#phase-0--archive-engine-vertical-slice)).

No CrashPlan reader exists and none should yet: it is phase 5 and gated on a legal review that has not happened. The row reads "partly built" rather than "built" so that nobody reads the seam's existence as the feature's.

### 0025 — nothing compacts yet, so nothing re-seals yet

The decision is sound and unexercised for the same reason as 0009: compaction is part of the collector. What *is* built is the constraint the decision protects — the record ordinal stays in the AAD, and `Repository.Tests/Index/IndexPrecedenceTests` holds the supersession rules a compaction would rely on.

### 0026 — the shapes are captured, the POSIX traversal is handle-relative, and one gap is left

All ten shapes are built and tested: hardlink groups, the diagnostics vocabulary, capture-status triggers, special files, alternate streams, directory entries, the filesystem capability record, and the catalogue casefold key.

The traversal underneath them is now handle-relative on POSIX (`Filesystem.Local/PosixDirectoryScope`, `Filesystem.Local/PosixHandleInterop`). Each directory is held open and its children are listed, stat'd, descended into, opened, and readlink'd by raw name bytes against that descriptor, with `O_NOFOLLOW` throughout — so the object that was classified is the object that is read, and revalidation stats the same handle rather than resolving the name again. An object carrying both a directory marker and a link marker is still classified as a link first, which is what keeps a junction from walking the scanner out of the approved root. Windows keeps the path-based walk and gains the identity check instead: a name that has come to mean a different object is recorded as `captured-identity-changed` and not re-read.

What is left is **capturing a POSIX name that is not valid UTF-8**, which the scanner can now open but the pipeline above it cannot carry: the relative path is a host string all the way through rules, the catalogue's path tables, and restore. [Specification 06 §4.3](../specifications/repository-format/06-manifests.md#43-what-name-must-contain) records why storing a lossy one would be worse than refusing it.

The **decision** that half of it depended on is now made rather than pending: where a host string is unavoidable, such a name renders **percent-encoded**, which is the only convention of the three considered that is lossless, valid UTF-8, and typeable back in. The remaining half is cost, and it is real — a byte-native relative path end to end means a catalogue schema bump, a receipt schema bump, byte-native rule matching, and native `openat`/`mkdirat` writes in a restore path that does not reference `Filesystem.Local` at all today. **Deferred past the format freeze deliberately**: the format needs nothing here, today's behaviour is a clean refusal rather than silent loss, and the freeze gate has no claim on it. Nothing will be built against a guess in the meantime, because the guess has been replaced by a rule.

### 0028 — the local binding, not the remote one

Recorded in the ADR's own [implementation status](adr/0028-service-boundary-and-deployment-topologies.md#implementation-status-2026-08) and not duplicated here. In short: writer-role exclusion, the versioned command contract, status aggregation, keystore unlock, per-job progress, and a CLI that asks a running service and falls back to direct mode. The remote binding — once a terminal refusal that bound nothing — now binds a real socket once an administrator names an interface; see [0030](#0030--the-socket-exists) for the transport it waited on.

The [restore pipeline review](review/2026-08-restore-pipeline-review.md) closed the gap that "falls back to direct mode" had hidden: the direct-mode restore was a second, uncontained implementation of the read path, and it now routes through the same `RestorePlanner`/`RestoreExecutor` the service uses — so ADR-0028 §3's "the same operation performs identically through either path" is enforced rather than asserted. The service also now carries the restore outcome across the contract and namespaces each run's displaced store.

### 0030 — the socket exists

Built, in `FallbackPlan.Protocol`: peer identity and fingerprints; the pairing ceremony's key agreement, transcript, short authentication string and confirmation signatures, **and the four messages that carry them**; the grant store, its pinning and revocation, and the destination's terms; frame encoding and refusal; session hello, accept and refuse; version selection and feature negotiation; and — after [Amendment 1](adr/0030-peer-identity-and-pairing.md#amendment-1-2026-08--authentication-moves-out-of-tls) — the channel-bound authentication that replaced RFC 7250, with a test that runs the man-in-the-middle it defeats.

And now **the transport that carries it.** `PeerTlsConnection` opens TLS 1.3 over TCP with the ephemeral certificate — a container for a per-connection key that authenticates nobody — and `PeerSessionDriver` drives the four-state machine over that real duplex stream: both authentication messages sent without waiting, each decoded frame admitted only in a state that permits it, every body length bounded before allocation, and every protocol violation answered with a stated refusal before the socket closes. A device's key persists in `<state>/peer.key` (`PeerKeypairStore`), so its identity survives a restart. `PairingCeremony` runs the ceremony over that stream, holds a confirmation that arrives before the local human has approved, and pins the grant only on mutual approval. The whole session and pairing layer is exercised over loopback TCP in `FallbackPlan.Protocol.Tests` — including the man-in-the-middle relay reproduced through two real TLS connections — and the ceremony is performed by two real operating-system processes in `FallbackPlan.Hosts.Tests`.

The service side binds it. `RemoteServiceListener` (in `FallbackPlan.Agent`) accepts on an interface named by an explicit administrative act — `fallbackplan-agent run --remote-interface <addr> --remote-port <n>`, off by every default — admits only a peer it has a grant for, and then runs the ADR-0028 command contract over the opened session through the same dispatch the local binding uses (`ServiceConnectionPump`). `RemoteServiceClient` (in `FallbackPlan.Cli`) is the paired console's other end, and the shipped CLI now drives it: `fallbackplan <verb> --connect <host:port> --fingerprint <fp> --state <dir>` routes `backup`, `verify`, `check`, `restore`, `snapshots`, `ls` and `status` to a remote paired service, naming the pinned service by fingerprint because a grant records a key, never an address. The two exit criteria this was blocked on now hold end to end in `FallbackPlan.Hosts.Tests`, through both the client directly and the CLI surface: an unpaired console is refused as `not_paired` while the local binding still answers, and a restore commanded from a paired console **writes on the service's machine** — the console is told the counts and the path, never sent the files.

And now the cargo the wire was built for: **peer replication** ([specification 03](../specifications/peer-protocol/README.md#documents)). Over an Open session, `ReplicationInitiator` (source) and `ReplicationResponder` (destination) move a repository's immutable objects — the source offers the repository, the destination declares what it already holds, and the source streams the rest in chunked frames, each object committed whole so an interrupted run resumes with no checkpoint. `RemoteServiceListener` routes on the peer's grant role: a peer entitled to store objects here speaks replication, a console speaks the command contract. `fallbackplan-agent replicate --to <host:port> --fingerprint <fp>` drives it, forwarding ciphertext with no passphrase. `FallbackPlan.Hosts.Tests` proves it end to end over loopback: a source's objects mirror to a destination byte for byte, and the standalone recovery tool restores the original files from the replica — a source destroyed and recovered from its destination, the Phase-2 peer criterion in its first concrete form.

What is left is the rest of replication and the gated console features: snapshot-scoped replication and the compact object-set filter (03 refinements), destination verification and quotas ([specifications 04–05](../specifications/peer-protocol/README.md#documents), still unwritten), and the console features gated on the two open questions of ADR-0028 — streaming restored content to the operator (Q18) and per-operator identity on a shared console (Q19).

### 0033 — the OS can own the process

Built, in `FallbackPlan.Agent`: the agent now behaves as a service the operating system starts and stops. `ServiceProcessHost` routes Ctrl+C and the `SIGTERM` that systemd and launchd send onto the one cancellation token the run loop and listeners already unwind cleanly — so a manager's stop is a clean shutdown (exit 0, writer lock freed) rather than the default terminate, proven by a test that spawns the shipped apphost and signals it. `WindowsServiceHost` bridges the Windows Service Control Manager through `ServiceBase` without adopting the Generic Host (ADR-0033). `ServiceUnit` and the `install` verb generate the registration an operator applies — a systemd unit, a launchd plist, or the Windows `sc.exe` commands, printed and never performed — from the same `--repo`/`--state` surface the agent runs with, so the unit cannot drift from the CLI.

Not built, and honestly so: the Windows SCM and launchd *lifecycles* cannot run on this Linux CI, so their live Start/Stop is verified manually while their testable parts — the generation, and the Windows adapter's cancel path — are unit-tested. Self-contained publishing and signed installers remain a Phase 4 concern; the generated artifacts reference whatever executable path is deployed.

### 0034 — the configuration speaks it; nothing serves it yet

The first arc slice is built: configuration schema v2 (`Application/ClientConfiguration.cs`, `Application/DestinationConfiguration.cs`) declares named destinations (`local-path` and `peer` operational-to-be, the cloud kinds schema-reserved), per-set destination references with optional retention overrides, and refuses a destination-less set, a dangling reference, and the v1 schema — the last with the migration in the message. `ClientConfigurationTests` pins all of it, and the upsert command preserves what a client cannot yet express.

Everything that *serves* the declaration is still the pre-0034 shape: one service archive (`Agent/ServiceRuntime.cs`), whole-repository replication driven by hand (`Agent/ReplicationInitiator.cs`, `Application/ReplicationStateStore.cs`), no fan-out, no retention. The remaining slices are sequenced in the [roadmap](roadmap.md): per-set staging archives next, then fan-out, then retention staged behind it.

---

## By phase

| Phase | State |
|-------|-------|
| [0 — Archive engine](roadmap.md#phase-0--archive-engine-vertical-slice) | Complete; every exit criterion traced to a named test |
| [1 — Snapshot and local repository](roadmap.md#phase-1--snapshot-and-local-repository-mvp) | Complete, both pushes |
| [2 — Peer-to-peer and the service boundary](roadmap.md#phase-2--peer-to-peer-backup-and-the-service-boundary) | Service boundary built on both bindings; peer protocol carried over a real socket; object replication built (whole-repository scope), a replica recovered from end to end; verification (spec 04) ahead; the remainder — roles, termination, quotas — sequenced in the hub-and-spoke arc |
| [Hub-and-spoke arc](roadmap.md#the-hub-and-spoke-arc--multi-destination-backup-sets-current) | In progress ([ADR-0034](adr/0034-hub-and-spoke-destinations.md)): configuration schema v2 built; the serving slices ahead — see [0034](#0034--the-configuration-speaks-it-nothing-serves-it-yet) |
| 3 — Cloud object stores | Not started; reframed as destination kinds behind the arc's fan-out |
| 4 — Retention, GC, compaction | Retention pulled forward into the hub-and-spoke arc; compaction and healing remain here — see [0009](#0009--the-intents-are-written-nothing-collects-yet) |
| 5 — CrashPlan import | Not started, gated on legal review |

---

## What keeps this true

[`eng/check-adr-status.py`](../eng/check-adr-status.py) refuses a build where an ADR is missing from the table above, where a row names an ADR that does not exist, where a state is not one of the four in the legend, or — the one that matters — **where a cited project, directory or type is not on disk.** It is the same discipline `eng/check-requirements.py` applies to the traceability matrix, adopted for the same reason: a status page nobody verifies becomes a status page nobody can trust, and the failure is invisible until someone acts on it.

What the checker cannot do is judge whether "built" is generous. That is a reading, and it is repeated whenever a phase closes. It also deliberately does not compare these states against each ADR's `Status:` line: that line records whether a *decision* was accepted, which is a different question from whether the code does it, and collapsing the two would lose both.

---

**See also:** [Abandoned choices](decisions-abandoned.md) — what was considered and rejected, and why · [Traceability](requirements/traceability.md) — requirements to tests · [Roadmap](roadmap.md)

# 11 — Solution structure

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §15, §22 · **Resolves:** [H3](../review/2026-08-architecture-review.md#h3--disposable-conflates-three-stores-with-incompatible-durability-requirements)

---

## 1. Layout

```text
FallbackPlan.slnx
├── src/
│   ├── FallbackPlan.Domain/                  entities, value objects, no infrastructure
│   ├── FallbackPlan.Application/             use cases over domain abstractions
│   ├── FallbackPlan.Repository/              repository engine composition
│   ├── FallbackPlan.Repository.Format/       canonical encodings, manifests, versioning
│   ├── FallbackPlan.Repository.Crypto/       key hierarchy, AEAD, identifiers
│   ├── FallbackPlan.Repository.Segmentation/ segmentation profiles
│   ├── FallbackPlan.Repository.Packing/      blob spool, seal, read, recovery footer
│   ├── FallbackPlan.Repository.Index/        deltas, checkpoints, rebuild
│   ├── FallbackPlan.Repository.Catalogue/    disposable local catalogue
│   ├── FallbackPlan.Filesystem/              scanner contracts
│   ├── FallbackPlan.Filesystem.Local/        local scanner, per-platform interop inside
│   ├── FallbackPlan.Protocol/                peer protocol
│   ├── FallbackPlan.Protocol.Grpc/
│   ├── FallbackPlan.Discovery/
│   ├── FallbackPlan.Replication/
│   ├── FallbackPlan.Restore/
│   ├── FallbackPlan.Retention/
│   ├── FallbackPlan.Verification/
│   ├── FallbackPlan.Storage.Abstractions/    IObjectStore, capabilities
│   ├── FallbackPlan.Storage.{Local,Peer,AzureBlob,S3}/
│   ├── FallbackPlan.Import.Abstractions/     neutral legacy model
│   ├── FallbackPlan.Import.CrashPlan/        optional, separately licensed
│   ├── FallbackPlan.Agent/                   long-running service
│   ├── FallbackPlan.Api/
│   ├── FallbackPlan.Web/
│   ├── FallbackPlan.Desktop/
│   ├── FallbackPlan.Cli/
│   ├── FallbackPlan.Recovery/                standalone emergency restore
│   ├── FallbackPlan.Relay/
│   ├── FallbackPlan.Discovery.Server/
│   └── FallbackPlan.Repository.Server/
├── tests/
│   ├── FallbackPlan.Domain.Tests/
│   ├── FallbackPlan.Repository.Tests/
│   ├── FallbackPlan.Repository.ConformanceTests/
│   ├── FallbackPlan.Repository.FuzzTests/
│   ├── FallbackPlan.Restore.Tests/
│   ├── FallbackPlan.Retention.Tests/
│   ├── FallbackPlan.Protocol.Tests/
│   ├── FallbackPlan.Storage.ContractTests/
│   ├── FallbackPlan.Import.CrashPlan.Tests/
│   ├── FallbackPlan.ArchitectureTests/       enforces §2
│   ├── FallbackPlan.IntegrationTests/
│   ├── FallbackPlan.InterruptionTests/
│   ├── FallbackPlan.PerformanceTests/
│   ├── FallbackPlan.TestSupport/            platform gating, shared by test projects
│   ├── FallbackPlan.Cli.Tests/              drives real commands in process
│   ├── FallbackPlan.Hosts.Tests/            drives the Agent and Recovery hosts
│   └── FallbackPlan.EndToEndTests/
├── external/
│   └── packages/                  committed Bodu package feed — see §5.1
├── specifications/                repository-format, peer-protocol, discovery-protocol,
│                                  recovery-kit, conformance-vectors
├── docs/                          this set
├── tools/                         repository-inspector, fixture-generator,
│                                  corruption-injector, network-fault-proxy
├── samples/  build/  eng/
```

Two changes from the original list. `Repository.Segmentation` and `Repository.Catalogue` are separated out because both are pluggable along axes the design explicitly anticipates: segmentation profiles ([`02-repository-format.md` §3.1](02-repository-format.md#31-profiles)) and the catalogue engine ([ADR-0010](../adr/0010-local-store-separation.md)). Hiding either inside a larger project makes the boundary a convention rather than something the compiler enforces.

Projects are created when the phase that needs them arrives, not up front. Empty placeholder projects make the boundary map look complete while enforcing nothing.

## 2. Dependency rules

- `Domain` has **no** infrastructure dependencies.
- `Application` depends on domain abstractions, never on provider implementations.
- Storage providers depend only on `Storage.Abstractions` and their provider SDK.
- `Repository.Format` has no UI, host, or provider dependencies. It must be usable by the standalone recovery tool.
- `Protocol` does not depend on `Desktop` or `Web`.
- `Import.CrashPlan` depends on `Import.Abstractions` and may feed application services. **Nothing in the core ever references it** — see §4.
- `Filesystem.Local` implements the shared contracts from `Filesystem`; platform differences (statx/lstat/Win32, xattrs, alternate streams, hole probing) are confined inside it behind platform guards rather than split into per-OS projects — one project keeps the identical scan semantics in one place, and the CI matrix proves each platform's interop. Both filesystem projects depend only on `Domain` and `Repository.Format`: the scanner describes what exists, it never decides what happens to it.
- `Recovery` depends on format, crypto, packing, index, and storage only. It must build and run with no Agent, no catalogue engine, and no UI.
- **Third-party cryptography lives only in `Repository.Crypto`.** The two primitives .NET does not supply — Argon2id and XChaCha20-Poly1305 — do not inherit the platform's audit posture, so the exposure is confined to one project rather than spread wherever a call site finds it convenient ([ADR-0019](../adr/0019-third-party-dependency-policy.md)).

`FallbackPlan.ArchitectureTests` enforces these as tests. A rule that is only written down is a rule that erodes.

### 2.1 Coverage

`eng/coverage.py` reports line coverage per production assembly, and takes a
`--floor` so it can gate rather than inform.

A host whose logic lives in `Main` cannot be covered by anything except
launching a process, so all three measured near zero — not for want of
tests, but because nothing could call them. Each therefore exposes a
callable type (`CliApplication`, `AgentHost`, `RecoveryHost`) with the entry
point reduced to a line or two, and output goes to injected writers rather
than `Console`, so a test captures it without mutating global state.
Together they took the codebase from 64.7% to 85.0%, most of the gain
landing in the engine rather than the shells, because the hosts are where
the engine is integrated.

What stays in `Main` is what genuinely belongs to the process: the Ctrl+C
handler the Agent installs, and nothing else.

Coverage from a single OS is a partial answer by construction: the scanner's
Linux, Darwin and Windows interop can only run on its own platform, so a
one-OS report understates it and the honest total merges the CI matrix.

### 2.2 Environment-specific tests

Two environment dimensions decide whether a test's subject exists at all:
the **operating system** (POSIX modes, xattrs and symlinks against Windows
alternate streams and security descriptors) and the **process privilege**
(permission denial is unobservable as root). A third — the machine's
**timezone** — must decide nothing, and is treated accordingly.

- **A test that does not run must not report as passed.** Platform gating
  goes through `FallbackPlan.TestSupport`'s `[PlatformFact]` /
  `[UnprivilegedPlatformFact]`, which skip with a stated reason. The pattern
  they replace — an early `return` in the test body — is recorded by the
  runner as a pass, so a green Windows run silently included tests that
  asserted nothing and the count could not distinguish "verified here" from
  "not applicable here".
- **Platform-specific assertions live in platform-specific tests**, not in
  `if (!OperatingSystem.IsWindows())` blocks inside shared ones, so each
  test states one contract and the shared test stays honest about being
  shared. `[PlatformTrait]` publishes a `Platform` trait, so one platform's
  surface can be run on its own (`--filter Platform=Posix`).
- **Where the platform is also a compile-time contract** — a call the BCL
  marks unsupported on Windows — the method carries
  `[UnsupportedOSPlatform]` too. The runtime skip and the analyzer then
  agree, rather than the analyzer being silenced.
- **Timezone is a test input, never an ambient condition.** Schedule
  derivations are pure functions of their arguments (NFR-TIME-001), so they
  are asserted across a fixed set of offsets — including a non-hour offset
  and one past the date line — rather than in whatever offset the host
  happens to be in. This repository shipped a schedule defect that was
  correct in UTC and a day wrong everywhere else; a UTC-only build agent
  cannot see that class of bug, and CI was green throughout.

## 3. Local state separation

Three stores, three lifecycles. The original proposal put all three in one sentence and one SQLite database ([H3](../review/2026-08-architecture-review.md#h3--disposable-conflates-three-stores-with-incompatible-durability-requirements)), which made NFR-REL-002 — "deleting it shall not cause data loss" — true of one and false of the others.

| Store | Contents | Rebuildable? | Consequence of loss |
|-------|----------|--------------|---------------------|
| **Catalogue** | Path, version, segment, blob, and generation indexes; watermarks | ✅ From the repository | Slow rebuild. No data loss. |
| **Durable local state** | Device keypair, pairing grants, destination authorisations, job history | ❌ | Device identity lost; every pairing must be re-approved manually at the other end |
| **Configuration** | Backup sets, schedules, policies, provider settings | Partially — policy manifests record what each snapshot used | Backups silently stop happening |

They are separate stores on disk, not separate tables in one file, so that "delete the catalogue and let it rebuild" — a legitimate and documented recovery action — cannot take the device identity with it.

**Durable local state** is backed up separately or re-established by re-pairing. The device *private key* is never written to the recovery kit; a recovering device establishes a new identity and is re-authorised ([`08-restore-and-recovery.md` §4.2](08-restore-and-recovery.md#42-what-is-deliberately-excluded)).

**Configuration** is file-based, schema-versioned, validated before use, and exportable without secrets (NFR-OPS-003).

## 4. Import isolation

`Import.CrashPlan` is a separately packaged optional component:

- the core never references it — dependency direction is enforced by `ArchitectureTests`;
- it depends on `Import.Abstractions`, which defines a neutral legacy model independent of any specific legacy format;
- its own dependencies and licence obligations stay contained within it, which matters because the licence question is open ([ADR-0001](../adr/0001-licence-and-contribution-model.md), [ADR-0015](../adr/0015-crashplan-importer-isolation.md));
- it opens legacy archives **read-only** and never mutates a source.

The neutral model exists so that the same import pipeline serves restic, Kopia, and Duplicati importers later without any of them reaching into the core.

## 5. Technology

| Concern | Choice | Note |
|---------|--------|------|
| Runtime | .NET 10 LTS | |
| Local API, repository server | ASP.NET Core | |
| Typed control operations | gRPC | |
| Peer transfer | QUIC/HTTP-3 under evaluation, TLS fallback | |
| Catalogue | SQLite behind an abstraction | Disposable; engine replaceable — [ADR-0010](../adr/0010-local-store-separation.md) |
| Canonical encoding | Canonical CBOR, pending benchmark | [ADR-0003](../adr/0003-canonical-metadata-encoding.md) |
| Compression | Zstandard | [`02-repository-format.md` §4](02-repository-format.md#4-compression) |
| Segment hash | Profile-selected, SHA-256 default | [ADR-0004](../adr/0004-segment-hash-function.md) |
| Telemetry | OpenTelemetry | Opt-in — [`10-observability.md` §5](10-observability.md#5-telemetry) |
| Streaming | `System.IO.Pipelines` | |
| Pipeline stages | Bounded `Channel<T>` | Bounded, so memory is a function of configuration not workload |
| Recovery tool | Native AOT under evaluation | After compatibility is established |
| SHA-256, HMAC, HKDF, AES-256-GCM | Platform (`System.Security.Cryptography`) | In-box and audited |
| Argon2id | `Bodu.Security.Cryptography` | **No platform implementation exists** — [ADR-0019](../adr/0019-third-party-dependency-policy.md) |
| XChaCha20-Poly1305 | Third-party, not yet selected | **No platform implementation exists.** .NET's `ChaCha20Poly1305` is the 12-byte-nonce RFC 8439 variant and is not a substitute — [spec 03 §6.1](../../specifications/repository-format/03-keys.md#61-where-each-primitive-comes-from) |
| General utilities | `Bodu.Core` | Referenced from `Repository.Packing` |
| Base32 rendering | `Bodu.Text.Encoding` | **No platform implementation exists** — behind the strict lowercase adapter in `Domain.Base32` ([ADR-0019](../adr/0019-third-party-dependency-policy.md) §4) |

### 5.1 Vendored dependencies

Bodu is not published to nuget.org, so it is consumed as **prebuilt packages from the committed feed at `external/packages`** — the `bodu-local` source in `nuget.config`, version-pinned in `Directory.Packages.props`. The pin is the committed nupkg plus the upstream commit SHA recorded in [`external/packages/README.md`](../../external/packages/README.md); upgrades are deliberate, reviewed changes. Because the feed travels with the tree, a plain `git clone`, a GitHub ZIP download, and Visual Studio's clone dialog all restore identically — no submodule, no `--recursive`, and CI enforces archive-buildability with a dedicated job. → [ADR-0021](../adr/0021-consume-bodu-via-committed-package-feed.md)

Two gates remain scoped to exclude `external/`: the warnings-as-errors build check (defensively — nothing under `external/` compiles today) and `eng/check-links.py`. The dependency policy — tiers, gates, containment, cross-verification — is unchanged. → [ADR-0019](../adr/0019-third-party-dependency-policy.md)

## 6. Public API shapes

Conceptual starting points. Public abstractions stay minimal until prototypes establish the right boundaries.

```csharp
public interface IRepository
{
    ValueTask<RepositoryInfo> GetInfoAsync(CancellationToken ct);
    ValueTask<SnapshotCommitResult> CommitSnapshotAsync(SnapshotDraft draft, CancellationToken ct);
    IAsyncEnumerable<SnapshotDescriptor> EnumerateSnapshotsAsync(SnapshotQuery query, CancellationToken ct);
    ValueTask<SnapshotReader> OpenSnapshotAsync(SnapshotId id, CancellationToken ct);
}

public interface ISnapshotSource
{
    ValueTask<SourceScan> BeginScanAsync(SourceSelection selection, ScanOptions options, CancellationToken ct);
}

public interface IReplicationService
{
    ValueTask<ReplicationPlan> PlanAsync(RepositoryEndpoint source, RepositoryEndpoint destination,
                                         ReplicationScope scope, CancellationToken ct);
    IAsyncEnumerable<ReplicationProgress> ExecuteAsync(ReplicationPlan plan, CancellationToken ct);
}

public interface IVerificationService
{
    IAsyncEnumerable<VerificationFinding> VerifyAsync(VerificationScope scope, VerificationOptions options,
                                                      CancellationToken ct);
}

public interface IRestoreService
{
    ValueTask<RestorePlan> PlanAsync(RestoreRequest request, CancellationToken ct);
    IAsyncEnumerable<RestoreProgress> ExecuteAsync(RestorePlan plan, CancellationToken ct);
}
```

`SnapshotCommitResult` reports commit against the **local replica**; per-destination replication progresses separately and is observed through `IReplicationService` ([`04-concurrency-and-publication.md` §6](04-concurrency-and-publication.md#6-commit-versus-replication)).

The corrected `IObjectStore` is in [`05-storage-providers.md` §2](05-storage-providers.md#2-the-store-interface).

---

**Previous:** [10 — Observability](10-observability.md) · **Next:** [12 — Worked example](12-worked-example.md)

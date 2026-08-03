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
│   ├── FallbackPlan.Filesystem.{Windows,MacOS,Linux}/
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
│   └── FallbackPlan.EndToEndTests/
├── external/
│   └── bodu/                      vendored submodule — see §5.1
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
- Platform-specific filesystem projects implement shared contracts from `Filesystem`.
- `Recovery` depends on format, crypto, packing, index, and storage only. It must build and run with no Agent, no catalogue engine, and no UI.
- **Third-party cryptography lives only in `Repository.Crypto`.** The two primitives .NET does not supply — Argon2id and XChaCha20-Poly1305 — do not inherit the platform's audit posture, so the exposure is confined to one project rather than spread wherever a call site finds it convenient ([ADR-0019](../adr/0019-third-party-dependency-policy.md)).

`FallbackPlan.ArchitectureTests` enforces these as tests. A rule that is only written down is a rule that erodes.

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

### 5.1 Vendored dependencies

`external/bodu` is a **git submodule** pinned to a commit, referenced by project reference rather than by package, because it is not published to nuget.org. Contributors clone with `--recursive`; CI checks out with `submodules: recursive`.

Two gates are scoped to exclude `external/`: the warnings-as-errors build check and `eng/check-links.py`. Vendored code is held to its own repository's standards. Our code stays at zero warnings, and a submodule bump cannot fail our build on someone else's style rule — the alternative trains people to ignore the gate, which costs more than it saves. → [ADR-0019](../adr/0019-third-party-dependency-policy.md)

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

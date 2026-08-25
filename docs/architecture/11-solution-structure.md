# 11 — Solution structure

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §15, §22 · **Resolves:** [H3](../review/2026-08-architecture-review.md#h3--disposable-conflates-three-stores-with-incompatible-durability-requirements)

**Built:** Describes the tree as it stands — see [implementation status](../implementation-status.md).

---

## 1. Layout

```text
FallbackPlan.slnx
├── src/                           ✓ = exists today; the rest arrive with their phase
│   ├── FallbackPlan.Domain/                  ✓ entities, value objects, no infrastructure
│   ├── FallbackPlan.Application/             ✓ use cases over domain abstractions
│   ├── FallbackPlan.Repository/              ✓ repository engine composition
│   ├── FallbackPlan.Repository.Format/       ✓ canonical encodings, manifests, versioning
│   ├── FallbackPlan.Repository.Crypto/       ✓ key hierarchy, AEAD, identifiers
│   ├── FallbackPlan.Repository.Segmentation/ ✓ segmentation profiles
│   ├── FallbackPlan.Repository.Packing/      ✓ blob spool, seal, read, recovery footer
│   ├── FallbackPlan.Repository.Index/        ✓ deltas, checkpoints, rebuild
│   ├── FallbackPlan.Repository.Catalogue/    ✓ disposable local catalogue
│   ├── FallbackPlan.Filesystem/              ✓ scanner contracts
│   ├── FallbackPlan.Filesystem.Local/        ✓ local scanner, per-platform interop inside
│   ├── FallbackPlan.Protocol/                ✓ peer identity, pairing, session, authentication (ADR-0030)
│   ├── FallbackPlan.Protocol.Grpc/
│   ├── FallbackPlan.Discovery/
│   ├── FallbackPlan.Replication/             ✓ store-to-store copier and replica verifier (ADR-0034)
│   ├── FallbackPlan.Restore/                 ✓ restore planner and executor
│   ├── FallbackPlan.Retention/               ✓ planner, replication gate, mark, sweep, convergence, staging trim (ADR-0034)
│   ├── FallbackPlan.Verification/
│   ├── FallbackPlan.Storage.Abstractions/    ✓ IObjectStore, capabilities
│   ├── FallbackPlan.Storage.{Local ✓,Peer,AzureBlob,S3}/
│   ├── FallbackPlan.Import.Abstractions/     ✓ neutral legacy model
│   ├── FallbackPlan.Import.Legacy/           optional, separately licensed
│   ├── FallbackPlan.Agent/                   ✓ the service host (ADR-0028)
│   ├── FallbackPlan.Api/                     ✓ command contract + local transport,
│   │                                         hosted by Agent, consumed by clients
│   ├── FallbackPlan.Keystore/                ✓ platform unlock (ADR-0028 §9)
│   ├── FallbackPlan.Web/                     ✓ local web console (ADR-0036)
│   ├── FallbackPlan.Desktop/
│   ├── FallbackPlan.Cli/                     ✓
│   ├── FallbackPlan.Recovery/                ✓ standalone emergency restore
│   ├── FallbackPlan.Relay/
│   ├── FallbackPlan.Discovery.Server/
│   └── FallbackPlan.Repository.Server/
├── tests/                         ✓ = exists today; the rest arrive with their phase
│   ├── FallbackPlan.Domain.Tests/            ✓
│   ├── FallbackPlan.Application.Tests/       ✓
│   ├── FallbackPlan.Api.Tests/               ✓ command contract and local binding
│   ├── FallbackPlan.Repository.Tests/        ✓ also holds the end-to-end suites
│   ├── FallbackPlan.Repository.ConformanceTests/ ✓
│   ├── FallbackPlan.Repository.FuzzTests/    ✓
│   ├── FallbackPlan.Filesystem.Tests/        ✓
│   ├── FallbackPlan.Restore.Tests/
│   ├── FallbackPlan.Replication.Tests/       ✓ the shared range reader and replica verifier
│   ├── FallbackPlan.Retention.Tests/         ✓ planner, replication gate, staging trim
│   ├── FallbackPlan.Protocol.Tests/          ✓ pairing, grants, framing, negotiation, channel binding
│   ├── FallbackPlan.Storage.ContractTests/   ✓
│   ├── FallbackPlan.Import.Legacy.Tests/
│   ├── FallbackPlan.ArchitectureTests/       ✓ enforces §2
│   ├── FallbackPlan.InterruptionTests/       ✓
│   ├── FallbackPlan.PerformanceTests/        ✓
│   ├── FallbackPlan.TestSupport/             ✓ platform gating, shared by test projects
│   ├── FallbackPlan.Cli.Tests/               ✓ drives real commands in process
│   ├── FallbackPlan.Web.DomTests/            ✓ the console page in a real browser (ADR-0049)
│   ├── FallbackPlan.Web.Tests/               ✓ the web console over real loopback HTTP
│   └── FallbackPlan.Hosts.Tests/             ✓ drives the Agent and Recovery hosts
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

That policy needs the map to say which half is which, and for a while it did not. The `tests/` tree was read as a statement of fact by [`requirements/traceability.md`](../requirements/traceability.md), which named test classes inside projects this list had merely promised — and 73 of its 86 citations ended up naming nothing at all. Hence the ✓ marks, now on both trees: they cost a character each and they are the difference between a plan and a claim. Two entries dropped rather than gained a mark. `IntegrationTests` and `EndToEndTests` were never built as separate projects because the suites that would have filled them live in `Repository.Tests/EndToEnd/`, next to the engine they exercise; splitting them out now would move code to satisfy a diagram.

## 2. Dependency rules

- `Domain` has **no** infrastructure dependencies.
- `Application` depends on domain abstractions, never on provider implementations.
- Storage providers depend only on `Storage.Abstractions` and their provider SDK.
- `Repository.Format` has no UI, host, or provider dependencies. It must be usable by the standalone recovery tool.
- `Protocol` depends on `Domain` alone — not `Application` (it did, for two utility types that now live in `Domain`), and never `Desktop` or `Web`.
- **`Replication` may reference `Protocol`; storage providers still may not.** Fan-out serves two transport shapes — plain store-to-store copy for `local-path` and cloud kinds, the peer protocol for `peer` — and the second must live somewhere. It lives in `Replication`, so a provider stays a dumb byte store and the "providers depend only on `Storage.Abstractions` and their SDK" rule above survives hub-and-spoke intact ([ADR-0034](../adr/0034-hub-and-spoke-destinations.md), [ADR-0012 Amendment 2](../adr/0012-storage-provider-contract.md#amendment-2-2026-08--the-contract-is-also-the-fan-out-seam)).
- `Import.Legacy` depends on `Import.Abstractions` and may feed application services. **Nothing in the core ever references it** — see §4.
- `Filesystem.Local` implements the shared contracts from `Filesystem`; platform differences (statx/lstat/Win32, xattrs, alternate streams, hole probing) are confined inside it behind platform guards rather than split into per-OS projects — one project keeps the identical scan semantics in one place, and the CI matrix proves each platform's interop. Both filesystem projects depend only on `Domain` and `Repository.Format`: the scanner describes what exists, it never decides what happens to it.
- `Recovery` depends on format, crypto, packing, and storage only — not index: the tool reads blobs through their recovery footers precisely so it works when every index is gone. It must build and run with no Agent, no catalogue engine, and no UI. (This sentence briefly claimed index too; the enforcing test's exact whitelist was right and the sentence was not.)
- **Third-party cryptography lives only where it is named.** The primitives .NET does not supply do not inherit the platform's audit posture, so each is confined rather than spread wherever a call site finds it convenient ([ADR-0019](../adr/0019-third-party-dependency-policy.md) §3 and Amendment 2): Argon2id and XChaCha20-Poly1305 to `Repository.Crypto`, where a defect is already in the user's stored bytes; Ed25519 and X25519 to `Protocol`, where a defect costs a re-pairing. The allowlist is two projects by name, not a tier — widening it should take an argument.
- **User interfaces depend on the client contract, never on the engine.** `Desktop` and `Web` reference `Api`'s client surface and nothing below it — not `Application`, not `Repository`, not a store provider. A UI that could open the repository directly would be a second writer, which [`04-concurrency-and-publication.md` §9](04-concurrency-and-publication.md#9-two-different-concurrencies-and-why-conflating-them-is-dangerous) forbids, and it would let a front end derive status by its own rules rather than the service's. `Web` is the first front end held to it ([ADR-0036](../adr/0036-local-web-console.md)): the rule is enforced at the IL and as an exact project-reference whitelist, and unlike the CLI it has no direct-mode exception at all.
- **`Cli` is a client too**, with one exception: its direct mode ([ADR-0028](../adr/0028-service-boundary-and-deployment-topologies.md) §3) takes the writer role when no service is running, so it alone among the front ends may reference `Application`. That exception is why the CLI is the one place the rule must be checked rather than assumed.
- **`Recovery` references neither `Api` nor `Application`.** It speaks to no service in any topology (NFR-OPS-005).

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

What stays in `Main` is a single line. Even the process lifetime moved out:
the Agent's `Main` calls `ServiceProcessHost`, which installs the Ctrl+C and
`SIGTERM` handlers and, under the Windows Service Control Manager, hands off to
`WindowsServiceHost` — the hosting layer that lets the operating system own the
process (`ServiceProcessHost`, `WindowsServiceHost`, `ServiceUnit`;
[ADR-0033](../adr/0033-hosting-under-an-os-service-manager.md)). It is a process
concern that is nonetheless worth a test, so it follows the same
callable-type-with-a-thin-entry-point rule as the hosts above it.

Coverage from a single OS is a partial answer by construction: the scanner's
Linux, Darwin and Windows interop can only run on its own platform, so a
line unreachable on the runner is not uncovered, it is uncovered *there*.
CI therefore collects coverage on all three platforms and a merge job unions
them, reporting the total to the run summary — the number that is not
understated. The floor it enforces sits below today's weakest module
deliberately: it exists to catch a collapse (a project dropping out of the
run, a suite silently not executing), not to police ordinary drift, since a
threshold nobody can move without a fight only teaches people to game it.

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
| **Configuration** | Backup sets, schedules, **named destinations and retention policies** ([ADR-0034](../adr/0034-hub-and-spoke-destinations.md)), provider settings | Partially — policy manifests record what each snapshot used | Backups silently stop happening |

They are separate stores on disk, not separate tables in one file, so that "delete the catalogue and let it rebuild" — a legitimate and documented recovery action — cannot take the device identity with it.

Hub-and-spoke adds two journal-shaped files **beside** the durable state, deliberately not inside it ([ADR-0010 Amendment 1](../adr/0010-local-store-separation.md#amendment-1-2026-08--where-the-hub-and-spoke-state-lands-in-the-split)): per-destination **sync state** (what each destination holds, when it was last reached, why it last failed) and **notices** (peering ended, terms narrowed, quota hit). Both are sacrificial the way `jobs.json` is — sync state re-derives from a destination inventory pass, and a lost notice is re-raised by the condition still holding — so their corruption or deletion can never touch the device identity. And a privacy note the export guidance now carries: configuration holds no secrets, but with destinations in it, it names who stores your backups and where.

**Durable local state** is backed up separately or re-established by re-pairing. The device *private key* is never written to the recovery kit; a recovering device establishes a new identity and is re-authorised ([`08-restore-and-recovery.md` §4.2](08-restore-and-recovery.md#42-what-is-deliberately-excluded)).

**Configuration** is file-based, schema-versioned, validated before use, and exportable without secrets (NFR-OPS-003).

## 4. Import isolation

`Import.Legacy` — a placeholder name for any per-format importer, none of which exists yet — is a separately packaged optional component:

- the core never references it — dependency direction is enforced by `ArchitectureTests`;
- it depends on `Import.Abstractions`, which defines a neutral legacy model independent of any specific legacy format;
- its own dependencies and licence obligations stay contained within it, which matters because the licence question is open ([ADR-0001](../adr/0001-licence-and-contribution-model.md), [ADR-0015](../adr/0015-legacy-importer-isolation.md));
- it opens legacy archives **read-only** and never mutates a source.

The neutral model exists so that the same import pipeline serves an importer for any legacy archive format later without that importer reaching into the core.

## 5. Technology

| Concern | Choice | Note |
|---------|--------|------|
| Runtime | .NET 10 LTS | |
| Command surface, both bindings | Local: Unix domain socket / named pipe. Remote: TLS over TCP, off by default | [ADR-0028](../adr/0028-service-boundary-and-deployment-topologies.md) §5 |
| Local web console | Kestrel (in-box shared framework), loopback only, per-run token; embedded static page, no framework | [ADR-0036](../adr/0036-local-web-console.md) |
| Typed control operations | gRPC | Transport-independent contract; the binding is chosen per §5, not per message |
| Remote client authentication | Paired device identity, pinned on approval | Reuses [09 §3](09-replication-and-peers.md#3-pairing); no password, no token file |
| Repository server | ASP.NET Core | A separate remote-destination gateway, not the client surface |
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

`SnapshotCommitResult` reports commit against the **set's staging archive**; per-destination replication progresses separately and is observed through `IReplicationService` ([`04-concurrency-and-publication.md` §6](04-concurrency-and-publication.md#6-commit-versus-replication)). `IReplicationService.PlanAsync` is deliberately endpoint-shaped — source, destination, scope — because under [ADR-0034](../adr/0034-hub-and-spoke-destinations.md) the same planner serves the wire path to a peer and the store-to-store copy to a directory or, later, a cloud bucket.

The corrected `IObjectStore` is in [`05-storage-providers.md` §2](05-storage-providers.md#2-the-store-interface).

---

**Previous:** [10 — Observability](10-observability.md) · **Next:** [12 — Worked example](12-worked-example.md)

# external/packages — committed local NuGet feed

This directory is the `bodu-local` package source declared in the repository's
`nuget.config`. It carries the prebuilt Bodu packages that FallbackPlan
consumes, committed to the repository so that a plain `git clone` and a GitHub
"Download ZIP" both restore and build with no extra steps — including opening
`FallbackPlan.slnx` directly in Visual Studio on Windows. See ADR-0021.

## Contents and provenance

| Package | Version | Origin |
|---|---|---|
| `Bodu.Core` | 0.2.0 | `local-packages/` feed of <https://github.com/bslater/bodu.git> at commit `e0f8997` ("Rev lock-step version to 0.2.0 and rebuild the local package feed") |
| `Bodu.Security.Cryptography` | 0.2.0 | same feed, same commit (depends on `Bodu.Core 0.2.0`) — Argon2id, cross-verified against Konscious on every CI run |
| `Bodu.Text.Encoding` | 0.2.0 | same feed, same commit (depends on `Bodu.Core 0.2.0`) — base32 rendering of identifiers, behind the strict lowercase adapter in `FallbackPlan.Domain.Base32` |
| `Bodu.Globalization.Recurrence` | 0.2.0 | same feed, same commit (depends on `Bodu.Core 0.2.0`) — schedule occurrence arithmetic, behind `FallbackPlan.Application.Schedule` ([ADR-0027 §1](../../docs/adr/0027-services-scheduling-status-telemetry.md), [requirements](../../docs/bodu-recurrence-requirements.md)) |
| `Bodu.Collections.Concurrent` | 0.2.0 | `local-packages/` feed of <https://github.com/bslater/bodu.git> at commit `591b152` ("Add the missing Bodu.Text.Serialization package README ahead of the v0.3.0 release (#658)") — the lock-free Vyukov MPMC `ConcurrentCircularBuffer<T>` behind `FallbackPlan.Diagnostics.LogRing`, the buffer a client reads diagnostics from ([ADR-0043 §6](../../docs/adr/0043-structured-logging-and-diagnostics.md)) |
| `Bodu.Collections` | 0.2.0 | same feed, same commit — not used directly; it is `Bodu.Collections.Concurrent`'s declared dependency (depends on `Bodu.Core 0.2.0`) |

Upstream versions the four in lock-step, so they are taken and upgraded
together: a mixed set would pair assemblies that were never built or tested
against each other. The `0.1.1 → 0.2.0` upgrade changed no repository bytes
— the committed conformance fixtures regenerate byte-identically, which is
the assertion that would have caught an Argon2id or base32 behaviour change
(NFR-COMP-004).

The packages target `net8.0` and are consumed by the `net10.0` projects via
NuGet's nearest-TFM selection. Versions are pinned centrally in
`Directory.Packages.props`; `nuget.config`'s `packageSourceMapping` routes all
`Bodu.*` IDs to this feed and everything else to nuget.org.

## Candidates for later adoption

Upstream Bodu also ships packages this repository does not consume yet. They
are recorded here so a later phase reaches for the reviewed candidate instead
of hand-rolling or pulling a stranger from nuget.org:

| Package | Would serve | Earliest need |
|---|---|---|
| `Bodu.Text.Filter` | Wildcard and regex pattern filters — a candidate engine for the Phase 1 streaming scanner's include/exclude evaluation (policy manifest keys `include_rules`/`exclude_rules`, [06 §7.1](../../specifications/repository-format/06-manifests.md#71-rule-dialect-rules-v1)) | Phase 1 |

Adoption follows the same flow as an upgrade: pack from a pinned upstream
commit, add the nupkg here with provenance in the table above, pin the version
in `Directory.Packages.props`, regenerate lockfiles, full sweep. The rule
semantics are now **specified** — the `rules-v1` dialect ([ADR-0024](../../docs/adr/0024-include-exclude-rule-dialect.md),
06 §7.1) with a dependency-free reference implementation in
`FallbackPlan.Domain.PathRules` — so adopting this package additionally
requires demonstrating it passes every case in
`conformance/vectors/path-rules.json`; the specification defines the
dialect, never the library.

## Upgrading Bodu

An upgrade is a deliberate, reviewed change — not an automatic pull:

1. In a checkout of the upstream Bodu repository at the commit you want,
   take the nupkgs from its `local-packages/` feed if they are current for
   that commit — check, rather than assume: that feed has carried builds
   older than the source beside it. Otherwise pack from source
   (`dotnet pack <Project>/src -c Release`), and if you pack a version
   upstream has not published, set `<Version>` inside the `.csproj` rather
   than passing `-p:Version=` — a global property propagates to project
   references and rewrites packaged dependency versions to ones this feed
   does not carry, which breaks restore.
2. Take all four together, at one version. Replace the `.nupkg` files here
   and update the table above (versions and upstream commit SHA).
3. Bump the four `PackageVersion` entries in `Directory.Packages.props`.
4. Regenerate lockfiles (`dotnet restore FallbackPlan.slnx --force-evaluate`)
   and run the full verification sweep (conformance vectors, build, tests,
   `eng/` checks) before committing. The fixture byte-identity tests are the
   ones that matter most here: Argon2id and base32 sit under committed
   bytes, so a behaviour change upstream surfaces as a fixture diff rather
   than as a test that merely still compiles.

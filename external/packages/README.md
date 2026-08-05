# external/packages — committed local NuGet feed

This directory is the `bodu-local` package source declared in the repository's
`nuget.config`. It carries the prebuilt Bodu packages that FallbackPlan
consumes, committed to the repository so that a plain `git clone` and a GitHub
"Download ZIP" both restore and build with no extra steps — including opening
`FallbackPlan.slnx` directly in Visual Studio on Windows. See ADR-0021.

## Contents and provenance

| Package | Version | Origin |
|---|---|---|
| `Bodu.Core` | 0.1.1 | `local-packages/` feed of <https://github.com/bslater/bodu.git> at commit `597e7f4b78e835b7f6041fc862083ef4e8c20ef5` |
| `Bodu.Security.Cryptography` | 0.1.1 | same feed, same commit (depends on `Bodu.Core 0.1.1`) — Argon2id, cross-verified against Konscious on every CI run |
| `Bodu.Text.Encoding` | 0.1.1 | same feed, same commit (depends on `Bodu.Core 0.1.1`) — base32 rendering of identifiers, behind the strict lowercase adapter in `FallbackPlan.Domain.Base32` |
| `Bodu.Globalization.Recurrence` | 0.1.2-local.10226cf | **packed from source** at commit `10226cfda8b10a09899370bbfb4a9ea5beead362` (depends on `Bodu.Core 0.1.1`) — schedule occurrence arithmetic, behind `FallbackPlan.Application.Schedule` ([ADR-0027 §1](../../docs/adr/0027-services-scheduling-status-telemetry.md), [requirements](../../docs/bodu-recurrence-requirements.md)) |

### Why the recurrence package carries a different version

The other three were taken verbatim from upstream's `local-packages/` feed,
where the published `0.1.1` matches the source it was built from. The
recurrence package's committed `0.1.1` does **not**: it predates the
`AnchoredInterval` type this repository depends on, so a build against it
fails to compile. Publishing our own build under the same `0.1.1` would put
two different assemblies behind one version — and NuGet's global cache is
keyed on id plus version, so whichever landed in a developer's cache first
would win, silently.

Hence `0.1.2-local.<commit>`: ahead of the stale `0.1.1` so it sorts after
it, suffixed so it can never be mistaken for an upstream release. When
upstream refreshes its `0.1.1` (or cuts a release containing
`AnchoredInterval`), the fix is to drop that nupkg in here, change the one
version in `Directory.Packages.props`, and regenerate lockfiles — the
`-local` build exists only to bridge the gap and should not outlive it.

Packing it, should it need repeating, differs from the upgrade procedure
below in one respect: set `<Version>` inside
`Bodu.Globalization.Recurrence/src/…csproj` rather than passing
`-p:Version=` on the command line. A global property propagates to the
`Bodu.Core` project reference and rewrites the packaged dependency to a
version this feed does not carry, which breaks restore.

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
   pack the two libraries (`dotnet pack Bodu.Core/src -c Release` and
   `dotnet pack Bodu.Security.Cryptography/src -c Release`), or take the
   nupkgs from its `local-packages/` feed if they are current.
2. Replace the `.nupkg` files here and update the table above (version and
   upstream commit SHA).
3. Bump the two `PackageVersion` entries in `Directory.Packages.props`.
4. Regenerate lockfiles (`dotnet restore FallbackPlan.slnx`) and run the full
   verification sweep (conformance vectors, build, tests, `eng/` checks)
   before committing.

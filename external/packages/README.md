# external/packages — the vendored Bodu feed, and why it is gone

This directory was a committed local NuGet feed. It carried the prebuilt Bodu
`.nupkg` files FallbackPlan consumes, checked into the repository so that a
plain `git clone` or a GitHub "Download ZIP" restored and built with no extra
steps ([ADR-0021](../../docs/adr/0021-consume-bodu-via-committed-package-feed.md)).

**It exists no longer.** The arrangement rested on one fact, stated in as many
words by both `nuget.config` and the record: *the Bodu packages are not
published to nuget.org*. They are now, so the packages come from nuget.org like
every other dependency and the vendored copies are deleted
([ADR-0021 Amendment 1](../../docs/adr/0021-consume-bodu-via-committed-package-feed.md),
2026-09-24). What survives is `nuget.config`'s `packageSourceMapping` entry for
`Bodu.*`, which was never about the source being local: it states that one feed
answers to a name, so a package squatted on a second source cannot shadow a
real one.

The file is kept rather than deleted with the packages, because it held the only
record of where the vendored bytes came from — and a directory that silently
empties tells a later reader nothing about what used to be in it.

## What was vendored, and its provenance

Versions moved in lock-step, upstream and here; the last vendored set was
**0.2.0**.

| Package | Origin |
|---|---|
| `Bodu.Core` | `local-packages/` feed of <https://github.com/bslater/bodu.git> at commit `e0f8997` ("Rev lock-step version to 0.2.0 and rebuild the local package feed") |
| `Bodu.Security.Cryptography` | same feed, same commit — Argon2id, cross-verified against Konscious on every CI run |
| `Bodu.Text.Encoding` | same feed, same commit — base32 rendering of identifiers, behind the strict lowercase adapter in `FallbackPlan.Domain.Base32` |
| `Bodu.Globalization.Recurrence` | same feed, same commit — schedule occurrence arithmetic, behind `FallbackPlan.Application.Schedule` ([ADR-0027 §1](../../docs/adr/0027-services-scheduling-status-telemetry.md), [requirements](../../docs/bodu-recurrence-requirements.md)) |
| `Bodu.Collections.Concurrent` | same repository at commit `591b152` — the lock-free Vyukov MPMC `ConcurrentCircularBuffer<T>` behind `FallbackPlan.Diagnostics.LogRing` ([ADR-0043 §6](../../docs/adr/0043-structured-logging-and-diagnostics.md)) |
| `Bodu.Collections` | same feed, same commit — not used directly; it is `Bodu.Collections.Concurrent`'s declared dependency |

## Upgrading now

An upgrade is still a deliberate, reviewed change rather than an automatic
pull, and the mechanics are simply those of any pinned package:

1. Take **all six together, at one version**. Upstream versions them in
   lock-step, and a mixed set pairs assemblies that were never built or tested
   against each other.
2. Bump the `PackageVersion` entries in `Directory.Packages.props`, then
   `dotnet restore FallbackPlan.slnx --force-evaluate`.
3. Run the full sweep, and read the **conformance fixtures first**. Argon2id
   and base32 sit beneath committed bytes, so a behaviour change upstream
   surfaces as a fixture diff rather than as a test that merely still
   compiles (NFR-COMP-004). A fixture that moves is a decision for the owner,
   not something to absorb by re-running the generator.

The packages target `net8.0` and are consumed by the `net10.0` projects through
NuGet's nearest-TFM selection.

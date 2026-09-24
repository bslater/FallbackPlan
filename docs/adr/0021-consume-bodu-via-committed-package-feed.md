# ADR-0021 — Consume Bodu as prebuilt packages from a committed local feed

**Status:** Accepted
**Date:** 2026-08
**Requirements:** NFR-SUP-002, NFR-SUP-003, NFR-PORT-001
**Related:** [ADR-0019](0019-third-party-dependency-policy.md), [`../../external/packages/README.md`](../../external/packages/README.md)

---

## Context

The working assumption behind [ADR-0019](0019-third-party-dependency-policy.md) §6 was that whoever builds this repository arrives via `git clone --recursive` on a machine where building a second repository's source tree is acceptable overhead. Both halves of that assumption failed the first time the repository had to build on a Windows laptop with Visual Studio:

- **A GitHub "Download ZIP" contains no submodule content.** The archive ships an empty `external/bodu`, so the three project references dangle *and* NuGet restore fails, because the `bodu-local` package feed lived inside the submodule. A plain `git clone` without `--recursive` fails the same way, and nothing enforces the flag.
- **Building Bodu from source is a heavier transaction than it looks.** The submodule is a 126 MB monorepo with its own nested submodule; a FallbackPlan build compiled five of its projects (the two libraries plus the `Bodu.CodeStyle` analyzer closure), pulled an analyzer package from the submodule's own feed, and needed a hand-maintained global-property set (`BoduProjectProperties`) to keep Bodu's analyzers and warnings-as-errors list from failing our build.

Bodu is not published to nuget.org, but its repository carries a `local-packages/` feed of prebuilt nupkgs — including the only two packages FallbackPlan consumes.

## Decision

> **Amended 2026-09 by [Amendment 2](#amendment-2-2026-09--bodu-is-published-so-the-feed-is-gone).** Upstream now publishes to nuget.org, so the committed feed and the `bodu-local` source are deleted and every `Bodu.*` id resolves from nuget.org at 0.7.0. The source mapping survives; everything below describes the arrangement this amendment replaced.

Bodu enters the build as **prebuilt NuGet packages committed to this repository** under `external/packages/`, which is the `bodu-local` source in `nuget.config`. The `external/bodu` submodule is removed.

- The feed carries exactly the packages the dependency graph needs: `Bodu.Core` and `Bodu.Security.Cryptography`, taken from upstream's `local-packages/` feed at the commit the submodule was pinned to. Versions are pinned centrally in `Directory.Packages.props`; `packageSourceMapping` routes all `Bodu.*` IDs to the committed feed and everything else to nuget.org.
- **The pin is the committed nupkg plus the upstream commit SHA recorded in [`external/packages/README.md`](../../external/packages/README.md)**, replacing Amendment 1's "the pin is the gitlink". An upgrade is a deliberate change: pack (or take) new nupkgs upstream, replace the files, bump the pinned versions, regenerate lockfiles, run the full verification sweep. Nothing upstream can move what this repository restores.
- The three former `ProjectReference`s into the submodule become `PackageReference`s. `BoduProjectProperties` is deleted — with no Bodu source in the build there is nothing to shield, and the Bodu packages sit inside the `NuGetAudit`, lockfile, and vulnerable-package-gate graph like any other dependency (a strengthening of the NFR-SUP-002 posture: the previous source build was outside all three).
- CI checks out without `submodules: recursive`, and a new `archive-build` job builds and tests from `git archive` output on every run, so "a ZIP download builds" is enforced, not assumed.
- ADR-0019's **dependency policy is unchanged**: the tiers, the five gates, the Argon2id containment in `Repository.Crypto`, and the Konscious cross-verification all stand. Only the §6 vendoring mechanics are superseded. The `Argon2idCrossVerificationTests` continue to drive the packaged assembly and are the acceptance check for any package upgrade.

## Consequences

**Positive**

- A plain `git clone`, a GitHub ZIP download, and Visual Studio's own clone all produce the same buildable tree; opening `FallbackPlan.slnx` on Windows restores from the committed feed with no network dependency beyond nuget.org.
- The repository shrinks by the submodule (126 MB of source, none of it shipped) and the build no longer compiles five foreign projects or evaluates a second MSBuild configuration island.
- The Bodu packages join the audited, locked dependency graph; the previous source build bypassed `NuGetAudit`, lockfiles, and the vulnerable-package gate entirely.
- The `WarningsAsErrors` coupling hazard is gone: no property set of ours can make Bodu's compile fail our build, because Bodu does not compile here.

**Negative**

- No stepping into Bodu source in the debugger and no source browsing in Solution Explorer; the packages carry XML docs but not symbols. Accepted: FallbackPlan calls exactly one Bodu API today (Argon2id), and upstream source is one clone away.
- Prebuilt binaries are a coarser provenance unit than source at a SHA. Compensated, not removed: the upstream commit is recorded, the feed is `<clear/>`-ed and source-mapped, and the conformance suite exercises the packaged bits on every run.
- The committed packages can go stale relative to upstream without anything failing. Accepted deliberately — that is what a pin is; ADR-0019 already treats dependency bumps as reviewed changes.

**Neutral**

- ~840 KB of nupkgs are tracked in git instead of a gitlink. (This bullet also claimed `.gitignore` re-included exactly `external/packages/*.nupkg`. It never did — the re-include it describes named a `local-packages/` directory this repository does not have, and the nupkgs were tracked regardless. Noted by Amendment 2, which removed the dead entry along with the packages.)
- The build-warning gate keeps its `/external/` exclusion defensively, though nothing under `external/` compiles today.

## Alternatives considered

**Keep the submodule and document `--recursive` harder.** Rejected: no amount of documentation makes a ZIP download contain the submodule, and the Windows/Visual Studio flow was the requirement, not an option.

**Vendor a source subset in-tree.** Workable — a ~25 MB closure (two libraries, three analyzer projects, Bodu's MSBuild island, one feed nupkg) was designed in full. Rejected in favour of packages: it imports a second build system's props/targets/analyzers into every build, reintroduces the double-build and warnings-coupling hazards that `BoduProjectProperties` existed to manage, and carries fifty files of infrastructure to compile two libraries whose prebuilt form already exists.

**`git subtree` merge.** Rejected: imports the full 126 MB monorepo and its history into this repository to use two libraries.

**Publish Bodu to nuget.org and consume normally.** The cleanest end state, but not this repository's decision to make — upstream publishing cadence and ownership are the maintainer's. The committed feed is forward-compatible with it: if the packages appear on nuget.org, the migration is a source-mapping change.

## Amendment 1 (2026-08) — Bodu.Core is a solution-wide vocabulary, not a contained dependency

The original decision left `Bodu.Core` where it happened to land: one
`PackageReference`, in `Repository.Packing`, with a canary test asserting it
stayed there and a comment telling whoever moved it to think first. This is
that thinking.

**`Bodu.Core` is now referenced by every project that validates an argument**
— eighteen of the twenty-one — and `ThrowHelper` replaces the BCL's
`ArgumentNullException.ThrowIfNull` and its relatives at all 291 guard sites.

**Why the containment rule was worth losing.** It was never protecting
anything about `Bodu.Core`; it was protecting the habit of not spreading a
third-party dependency without an argument. That habit is still correct for
`Bodu.Security.Cryptography`, where the blast radius is the user's stored
bytes and the rule stays exactly as it was, and for
`Bodu.Globalization.Recurrence`, which stays in `Application`. Guard clauses
are the opposite case: the cost of *not* sharing them is a validation
vocabulary that differs per project, which is how a parameter check ends up
throwing the wrong exception type in one layer and nothing at all in another.

**What this costs, stated rather than discovered later.** `Repository.Format`
is what the standalone recovery tool links, and its closure is now
`Bodu.Core` and `Bodu.Text.Encoding` rather than the latter alone. Both are
pure managed libraries with no native assets and both restore from the
committed feed, so the clean-machine property NFR-PORT-001 asks for survives —
but the closure grew, and that is the kind of growth that happens once per
convenience until the recovery tool needs a package manager. The old canary is
replaced by a rule that tests the thing actually at risk:
`Recovery_tool_closure_admits_only_the_two_intended_bodu_packages`. A third
Bodu package reaching `Repository.Format` now fails a test rather than passing
review.

**Behaviour is unchanged at the call sites.** `ThrowHelper.ThrowIfNull` infers
the parameter name through `CallerArgumentExpression` and produces the same
`ArgumentNullException` with the same `ParamName` and message as the BCL
helper, verified before the migration rather than assumed. Three calls had no
one-to-one equivalent and were rewritten rather than approximated:
`ThrowIfZero` became `ThrowIfEqual(value, 0)`, `ThrowIfNegativeOrZero` became
`ThrowIfZeroOrNegative`, and `ObjectDisposedException.ThrowIf(flag, this)`
became `ThrowHelper.ThrowIfDisposed(flag, nameof(BlobWriter))`.

## Amendment 2 (2026-09) — Bodu is published, so the feed is gone

This record's last alternative reads:

> **Publish Bodu to nuget.org and consume normally.** The cleanest end state, but not this repository's decision to make — upstream publishing cadence and ownership are the maintainer's. The committed feed is forward-compatible with it: if the packages appear on nuget.org, the migration is a source-mapping change.

They have appeared, at 0.5.0, 0.6.0 and 0.7.0 — all six of them — and the
forecast held exactly: the migration was a source-mapping change.
`external/packages` is deleted, `nuget.config`'s `bodu-local` source with it,
and every `Bodu.*` id now resolves from nuget.org at **0.7.0**.

**What the decision rested on, and what became of it.** The Context above opens
with a fact rather than a preference — *"Bodu is not published to nuget.org"* —
and everything the feed bought followed from it. It never bought independence
from the network: `System.Formats.Cbor`, `Microsoft.Data.Sqlite`,
`ZstdSharp.Port` and the two logging packages already made nuget.org a
requirement for any restore at all, which the Consequences said in as many
words (*"no network dependency beyond nuget.org"*). It bought availability of
packages that could not otherwise be fetched. That is no longer a problem to
solve, so the solution goes.

**What survives, and why it is not an oversight.** `packageSourceMapping` keeps
an entry for `Bodu.*`, now pointing at nuget.org. It reads like a leftover and
is not: the mapping was never about the source being local. It states that one
feed answers to a name, so a package squatted on a second source cannot shadow
a real one — the `<clear/>`-plus-mapping posture of NFR-SUP-002/003, untouched
by where the bytes come from.

**The two Negative consequences this closes**, and they close honestly rather
than by restatement. *"Prebuilt binaries are a coarser provenance unit than
source at a SHA"* — the provenance is now the published package identity and
the lockfile hash, which is what every other dependency in the tree has and is
strictly more checkable than a SHA recorded in a README by hand. *"The
committed packages can go stale relative to upstream without anything failing"*
— still true, and still deliberate, because that is what a pin is; what changes
is that noticing is now an ordinary outdated-package check rather than a
comparison against a foreign repository's feed.

**What is given up.** A `git clone` with no network can no longer restore. This
was already true of every other package in the tree, so the Positive
consequence it weakens (*"a plain `git clone`, a GitHub ZIP download, and
Visual Studio's own clone all produce the same buildable tree"*) survives in
substance: all three still produce the same tree and all three still need
nuget.org, as they always did. Nothing about the Windows/Visual Studio flow
that prompted this record regresses.

**The upgrade, and why the verification is the whole of it.** All six move
together at one version, as they always have: upstream versions them in
lock-step and a mixed set pairs assemblies never built or tested against each
other. Five minor versions is a real risk under committed bytes — Argon2id and
base32 sit beneath the conformance vectors and both frozen repository fixtures,
so an upstream behaviour change surfaces as a **fixture diff** rather than as a
test that merely still compiles (NFR-COMP-004). It did not: the generator
reproduced every vector file byte-identically, both fixtures read back from
their frozen bytes, the Argon2id cross-verification against Konscious passed,
the full suite was green, and `eng/recovery-drill.sh` recovered eleven files
byte-identical from the passphrase alone on the Release binaries. A fixture
that moves here is a decision for the owner — pin at the last compatible
version, or take the change and say what it costs — never something to absorb
by re-running the generator.

**Adoption candidates, re-homed from the feed's README** now that the file it
lived in documents a directory rather than a practice. Upstream ships packages
this repository does not consume; they are recorded so a later phase reaches
for the reviewed candidate rather than hand-rolling or pulling a stranger:

| Package | Would serve | Earliest need |
|---|---|---|
| `Bodu.Text.Filter` | Wildcard and regex pattern filters — a candidate engine for the streaming scanner's include/exclude evaluation (policy manifest keys `include_rules`/`exclude_rules`, [06 §7.1](../../specifications/repository-format/06-manifests.md#71-rule-dialect-rules-v1)) | Phase 1 |

Adoption is now an ordinary reviewed dependency addition (ADR-0019's gates,
`Directory.Packages.props`, lockfiles, full sweep) rather than a packing
exercise. For this candidate it additionally requires demonstrating the library
passes every case in `conformance/vectors/path-rules.json`: the `rules-v1`
dialect is **specified** ([ADR-0024](0024-include-exclude-rule-dialect.md),
06 §7.1) with a dependency-free reference implementation in
`FallbackPlan.Domain.PathRules`, and the specification defines the dialect,
never the library.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Submodule replaced by committed `external/packages` feed; ADR-0019 §6 mechanics superseded, dependency policy unchanged |
| 2026-08 | Accepted (amended) | Amendment 1: `Bodu.Core` becomes the solution-wide guard-clause vocabulary; its containment canary is replaced by a rule on the recovery tool's closure |
| 2026-09 | Accepted (amended) | Amendment 2: upstream published to nuget.org, so the committed feed is deleted and all six packages come from nuget.org at 0.7.0 — the migration this record's own last alternative forecast as "a source-mapping change". The `Bodu.*` source mapping survives, pointing at nuget.org |

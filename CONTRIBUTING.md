# Contributing to FallbackPlan

> ## ⚠️ External code contributions cannot be merged yet
>
> **The licence is decided** — dual AGPL-3.0-only + commercial, with the specification and conformance suite under Apache-2.0 ([ADR-0001](docs/adr/0001-licence-and-contribution-model.md), [LICENSING.md](LICENSING.md)). What is still missing is the **contributor licence agreement**: dual licensing requires the project to retain the right to offer contributed code under both arms, and until a CLA is published there is no instrument by which you could grant that. Please do not open pull requests yet: we would have to leave them unmerged, which wastes your time.
>
> Issues, questions, specification defect reports, and **independent reader implementations** (the spec is Apache-2.0 precisely so you can build one under any licence you choose) are welcome now and depend on nothing.

---

## What is most useful right now

The repository format specification is required to be implementable by someone who has never read the reference implementation, in another language, from the published text alone. That is a release gate ([freeze gate](docs/roadmap.md#format-v1-freeze-gate) item 2).

So the single most valuable thing anyone can do today is **try to implement part of it and tell us where the text is insufficient**. If you cannot build something from what is written in [`specifications/repository-format/`](specifications/repository-format/README.md), that is a defect in the specification, not in your reading of it.

## Repository layout

| Path | Contents |
|------|----------|
| [`docs/architecture/`](docs/architecture/) | How the system works and why |
| [`docs/requirements/`](docs/requirements/) | FR-* and NFR-*, with a traceability matrix |
| [`docs/adr/`](docs/adr/) | Decision records |
| [`docs/review/`](docs/review/) | Design reviews, including findings against earlier drafts |
| [`specifications/`](specifications/) | Normative on-disk format and conformance vectors |
| `src/`, `tests/` | Implementation |
| `eng/` | Repository integrity checks |

Start with [`docs/README.md`](docs/README.md). The [worked example](docs/architecture/12-worked-example.md) is the fastest way to see how the parts fit together.

## Building

A plain clone is all you need — a GitHub "Download ZIP" works identically:

```bash
git clone https://github.com/bslater/fallbackplan.git

dotnet build FallbackPlan.slnx -c Release
dotnet test  FallbackPlan.slnx -c Release
```

Requires the .NET SDK pinned in [`global.json`](global.json).

Bodu — the library supplying Argon2id, one of two primitives .NET does not provide ([ADR-0019](docs/adr/0019-third-party-dependency-policy.md)) — is consumed as prebuilt packages from the committed [`external/packages`](external/packages/README.md) feed ([ADR-0021](docs/adr/0021-consume-bodu-via-committed-package-feed.md)), so restore needs nothing beyond this tree and nuget.org. Upgrading those packages is a deliberate, reviewed change; the procedure is in the feed's README.

**Warnings are errors.** This is a backup engine; a warning we habitually ignore is a defect we ship. The gate covers `src/` and `tests/` and excludes `external/`.

### Building on Windows with Visual Studio

- **Visual Studio 2022 17.14 or later** (or Visual Studio 2026) — earlier versions cannot open the `.slnx` solution format.
- **.NET SDK 10.0.1xx** — [`global.json`](global.json) pins the band; VS uses it automatically once installed.
- Open `FallbackPlan.slnx`, build, and run tests from Test Explorer. Restore resolves the Bodu packages from the committed feed, so this works from a `git clone`, from Visual Studio's own clone dialog, and from an extracted "Download ZIP" alike.

## Checks that run in CI

```bash
python3 eng/check-links.py                                              # links and anchors
python3 eng/check-requirements.py                                       # requirement IDs, incl. citations in C# comments
python3 eng/check-solution.py                                           # every csproj is in the solution
python3 specifications/repository-format/conformance/generate.py --check # vectors reproducible
```

Run them before proposing a change. They are fast and they catch the errors that are tedious to find by reading.

CI also restores in **locked mode**: every project commits a `packages.lock.json`, and a dependency change must regenerate it (`dotnet restore FallbackPlan.slnx`) in the same commit or CI fails. That is deliberate — what a build restores is part of what the build *is* (NFR-SUP-003).

## Conventions that are not negotiable

These exist because breaking them has already caused real defects in this project's history.

1. **Terminology.** *Segment* and *blob* are the nouns — never *chunk*, *block*, or *pack*. The normative glossary is [`01-domain-model.md`](docs/architecture/01-domain-model.md).
2. **The specification is normative for format; architecture documents are normative for rationale.** Do not duplicate rules between them. A rule stated in two places drifts.
3. **Requirements are testable.** A requirement that cannot fail a test is not a requirement. Every FR/NFR states an observable acceptance criterion and appears in the traceability matrix.
4. **ADRs record what was rejected and why.** An ADR with no negative consequences has not been thought through.
5. **Do not weaken a rule without reading why it exists.** Several rules in this codebase look arbitrary and are not — the spool checkpoint storing sealed bytes, write intents preceding blob creation, index entries carrying generations. Each closed a specific failure documented in [`docs/review/`](docs/review/).

## Reporting a security issue

See [SECURITY.md](SECURITY.md). Please do not open a public issue for a vulnerability.

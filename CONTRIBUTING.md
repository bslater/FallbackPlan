# Contributing to FallbackPlan

> ## ⚠️ External contributions cannot be accepted yet
>
> **The project has no licence.** This is a deliberate open decision, not an oversight — see [ADR-0001](docs/adr/0001-licence-and-contribution-model.md) and [Q1](docs/open-questions.md#q1--project-licence).
>
> Until it is settled there is no basis on which you could grant, or we could accept, rights to your work. Please do not open pull requests: we would have to leave them unmerged, which wastes your time.
>
> Issues, questions, and specification defect reports **are** welcome now and do not depend on the licence.

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

The build depends on a vendored submodule, so clone recursively:

```bash
git clone --recursive https://github.com/bslater/fallbackplan.git

# Already cloned without it, or the pinned commit moved:
git submodule update --init --recursive
```

`external/bodu` supplies Argon2id — one of two primitives .NET does not provide ([ADR-0019](docs/adr/0019-third-party-dependency-policy.md)). Without it the solution does not restore, and the error you get is a missing project reference rather than anything that names the submodule.

```bash
dotnet build FallbackPlan.slnx -c Release
dotnet test  FallbackPlan.slnx -c Release
```

Requires the .NET SDK pinned in [`global.json`](global.json).

**Warnings are errors.** This is a backup engine; a warning we habitually ignore is a defect we ship.

That gate covers `src/` and `tests/` and deliberately **excludes `external/`**. Vendored code is held to its own repository's standards; a submodule bump must not be able to fail this build on a style rule. If you see warnings from `external/`, they are not yours to fix here.

## Checks that run in CI

```bash
python3 eng/check-links.py                                              # links and anchors
python3 eng/check-requirements.py                                       # requirement IDs
python3 specifications/repository-format/conformance/generate.py --check # vectors reproducible
```

Run them before proposing a change. They are fast and they catch the errors that are tedious to find by reading.

## Conventions that are not negotiable

These exist because breaking them has already caused real defects in this project's history.

1. **Terminology.** *Segment* and *blob* are the nouns — never *chunk*, *block*, or *pack*. The normative glossary is [`01-domain-model.md`](docs/architecture/01-domain-model.md).
2. **The specification is normative for format; architecture documents are normative for rationale.** Do not duplicate rules between them. A rule stated in two places drifts.
3. **Requirements are testable.** A requirement that cannot fail a test is not a requirement. Every FR/NFR states an observable acceptance criterion and appears in the traceability matrix.
4. **ADRs record what was rejected and why.** An ADR with no negative consequences has not been thought through.
5. **Do not weaken a rule without reading why it exists.** Several rules in this codebase look arbitrary and are not — the spool checkpoint storing sealed bytes, write intents preceding blob creation, index entries carrying generations. Each closed a specific failure documented in [`docs/review/`](docs/review/).

## Reporting a security issue

See [SECURITY.md](SECURITY.md). Please do not open a public issue for a vulnerability.

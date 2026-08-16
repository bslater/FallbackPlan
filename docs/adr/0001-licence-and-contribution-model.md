# ADR-0001 — Licence and contribution model

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-GOV-001, FR-GOV-002, FR-GOV-003

---

## Context

The proposal describes FallbackPlan as "open source, cross-platform, local-first" and makes governance the first item of the P0 backlog. It never names a licence.

This is not a formality that can be settled later. The choice determines:

1. whether third parties will implement independent readers — the property NFR-COMP-004 exists to guarantee, and the one that makes "your data is not locked in" a true statement rather than a slogan;
2. whether GPL-licensed prior art could ever be reused in a legacy importer;
3. whether distributions will package the project;
4. what external contributors are agreeing to.

Point 2 has a sequencing consequence that makes this urgent: if the answer is a permissive licence, then nobody who intends to write a legacy-format parser may read GPL-licensed reader source first, because doing so contaminates the clean-room option permanently. The licence decision therefore gates the *order* of work in Phase 5, not just its terms.

## Decision

**Dual licensing: the code is AGPL-3.0-only, with commercial licences available from the maintainer. The specification and conformance suite (`specifications/`) are Apache-2.0.** The map lives in [`LICENSING.md`](../../LICENSING.md); the licence texts are [`LICENSE`](../../LICENSE) and [`specifications/LICENSE`](../../specifications/LICENSE).

The maintainer weighed the recommendation below (Apache-2.0 for everything) and decided differently, for a stated reason: for a backup product, the guarantee that **every derivative — distributed or offered as a network service — stays open** is worth more than permissive adoption. The AGPL's network clause is deliberate: the plausible closed fork of a backup engine is a hosted service.

The decision keeps what the Apache recommendation was protecting. Its argument was that independent readers (point 1 of the context) need maximum implementability — but independent readers implement from the **specification and conformance vectors**, not from the code; that separation is the discipline this repository is built on. Licensing `specifications/` under Apache-2.0 gives a third-party reader author permissive terms and a patent grant over everything they actually need, under any licence they choose, owing this project nothing. Copyleft on the code then deters only closed *forks of the code*, which is precisely the intent.

Three further consequences of the choice, recorded rather than implied:

1. **The commercial arm requires copyright unity.** Dual licensing works today because the project has a single copyright holder. External code contributions require a CLA preserving the dual-licensing right; until one is published, external contributions remain unmergeable ([`CONTRIBUTING.md`](../../CONTRIBUTING.md) says so plainly). "AGPL-3.0-only" rather than "or-later" is the same instinct: only the copyright holder moves the terms, deliberately.
2. **The GPL-reuse door opens** (point 2 of the context, reversed): AGPL-3.0 code can incorporate GPL-3.0-compatible prior art, so a legacy importer's clean-room burden may be avoidable — subject entirely to [ADR-0015](0015-legacy-importer-isolation.md)'s gate, which still requires verifying any existing reader's licence before anyone reads its source.
3. **Charging is unaffected.** The AGPL obliges providing source to recipients, not giving everything away: selling binaries, packaged builds, and support for the AGPL edition is permitted alongside the commercial arm, and the project name is not licensed with the code.

Per-file licence headers are deferred to first-public-release preparation; `LICENSE` at the root governs in the meantime.

## Options

| Option | Third-party readers | GPL reuse | Proprietary forks | Distro packaging |
|--------|--------------------|-----------|--------------------|------------------|
| **Apache-2.0** | Best — permissive with explicit patent grant | ❌ | Permitted | Easiest |
| **MPL-2.0** | Good — file-level copyleft only | ❌ | Permitted alongside | Easy |
| **GPL-3.0** | Reduced — copyleft deters embedding | ✅ | Prevented | Easy |
| **AGPL-3.0** | Reduced further — network clause deters hosted use | ✅ | Prevented | Some friction |

### Recommendation

**Apache-2.0**, with any legacy importer implemented clean-room.

The reasoning is that the project's central promise — that a user can leave, and that their repository is readable by software we did not write — depends on independent implementations actually existing. A permissive licence with a patent grant maximises the chance of that. The importer is already isolated in its own package specifically so that this choice does not have to be made to suit it ([`../architecture/11-solution-structure.md` §4](../architecture/11-solution-structure.md#4-import-isolation)); if a GPL reader turns out to be reusable and valuable, it can be a separately licensed optional package without the core adopting copyleft.

The argument against is real and should be weighed: a permissive licence permits a commercial fork that closes the improvements. For a backup product where trust is the currency, some maintainers would rather have the guarantee.

## Consequences

**Whatever is chosen:**

- `LICENSE`, `CONTRIBUTING.md` (stating DCO or CLA), and `SECURITY.md` must exist before the first public release.
- A legacy importer keeps its own licence statement and dependency set, isolated from the core.
- **No Phase 5 parser work begins before this and [ADR-0015](0015-legacy-importer-isolation.md) are resolved.** Reading incompatibly licensed source first forecloses the clean-room path.

**Now that it is decided:** freeze-gate item 6 is satisfied (`LICENSE` present, [`../roadmap.md`](../roadmap.md#format-v1-freeze-gate)). External contributions remain gated — on the CLA now, not on the licence — and `CONTRIBUTING.md` states the current posture honestly.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Analysis recorded; decision explicitly deferred by the maintainer |
| 2026-08 | Accepted | Dual AGPL-3.0-only + commercial for code; Apache-2.0 for `specifications/`; the recorded Apache-2.0 recommendation consciously overridden with the reader-ecosystem goal preserved via the specification carve-out |

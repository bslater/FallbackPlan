# ADR-0001 — Licence and contribution model

**Status:** Proposed — *deliberately left open at the maintainer's direction*
**Date:** 2026-08
**Requirements:** FR-GOV-001, FR-GOV-002, FR-GOV-003

---

## Context

The proposal describes FallbackPlan as "open source, cross-platform, local-first" and makes governance the first item of the P0 backlog. It never names a licence.

This is not a formality that can be settled later. The choice determines:

1. whether third parties will implement independent readers — the property NFR-COMP-004 exists to guarantee, and the one that makes "your data is not locked in" a true statement rather than a slogan;
2. whether GPL-licensed prior art could ever be reused in the CrashPlan importer;
3. whether distributions will package the project;
4. what external contributors are agreeing to.

Point 2 has a sequencing consequence that makes this urgent: if the answer is a permissive licence, then nobody who intends to write the CrashPlan parser may read GPL-licensed reader source first, because doing so contaminates the clean-room option permanently. The licence decision therefore gates the *order* of work in Phase 5, not just its terms.

## Decision

**Deferred.** The maintainer has chosen to record this as an open decision rather than settle it now. See [`../open-questions.md#q1--project-licence`](../open-questions.md#q1--project-licence).

The analysis below stands, and the constraints in "Consequences" apply regardless of which option is eventually chosen.

## Options

| Option | Third-party readers | GPL reuse | Proprietary forks | Distro packaging |
|--------|--------------------|-----------|--------------------|------------------|
| **Apache-2.0** | Best — permissive with explicit patent grant | ❌ | Permitted | Easiest |
| **MPL-2.0** | Good — file-level copyleft only | ❌ | Permitted alongside | Easy |
| **GPL-3.0** | Reduced — copyleft deters embedding | ✅ | Prevented | Easy |
| **AGPL-3.0** | Reduced further — network clause deters hosted use | ✅ | Prevented | Some friction |

### Recommendation

**Apache-2.0**, with the CrashPlan importer implemented clean-room.

The reasoning is that the project's central promise — that a user can leave, and that their repository is readable by software we did not write — depends on independent implementations actually existing. A permissive licence with a patent grant maximises the chance of that. The importer is already isolated in its own package specifically so that this choice does not have to be made to suit it ([`../architecture/11-solution-structure.md` §4](../architecture/11-solution-structure.md#4-import-isolation)); if a GPL reader turns out to be reusable and valuable, it can be a separately licensed optional package without the core adopting copyleft.

The argument against is real and should be weighed: a permissive licence permits a commercial fork that closes the improvements. For a backup product where trust is the currency, some maintainers would rather have the guarantee.

## Consequences

**Whatever is chosen:**

- `LICENSE`, `CONTRIBUTING.md` (stating DCO or CLA), and `SECURITY.md` must exist before the first public release.
- The CrashPlan importer keeps its own licence statement and dependency set, isolated from the core.
- **No Phase 5 parser work begins before this and [ADR-0015](0015-crashplan-importer-isolation.md) are resolved.** Reading incompatibly licensed source first forecloses the clean-room path.

**While it stays open:** external contributions cannot be accepted, and the format cannot be declared v1-frozen ([`../roadmap.md`](../roadmap.md#format-v1-freeze-gate)).

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Analysis recorded; decision explicitly deferred by the maintainer |

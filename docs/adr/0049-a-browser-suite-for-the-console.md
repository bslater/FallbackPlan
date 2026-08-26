# ADR-0049 — A browser suite for the console

**Status:** Accepted
**Date:** 2026-08
**Requirements:** NFR-OPS-006
**Related:** [ADR-0036](0036-local-web-console.md), [ADR-0041](0041-guided-restore-and-peer-retrieval.md), [ADR-0042](0042-write-only-repositories.md), [ADR-0044](0044-first-run-setup.md), [architecture 11 §2.2](../architecture/11-solution-structure.md#22-environment-specific-tests)

---

## Context

Three defects in the console page shipped past every gate this repository has, in one week, and
each stranded a person on a screen that looked finished:

- the kit-save page's buttons silently ate clicks when the page held no kit;
- the rebuild-from-passphrase page's submit button could never be enabled by typing, because the
  input handler only wired a different page's button — and the stray re-render that sometimes
  enabled it also replaced the field mid-keystroke and stole its focus;
- the shared modal dialog lived inside the hideable `#app` container, so during the setup and
  sign-in gates an open dialog painted **nothing** while its modal state made the entire
  document inert — enabled-looking controls that swallowed every click, on two screens in a row.

Nothing could have caught them, because nothing executes `app.js` against a DOM. `Web.Tests`
pins the wire names the page reads and drives the console's endpoints over real loopback HTTP —
the right suite for the contract — but the defect class above lives in the browser: `<dialog>`
modality and top-layer painting, CSP enforcement, focus, downloads. A DOM emulation cannot
stand in for it; the emulators do not implement modality or CSP, which are precisely the
semantics that failed.

Meanwhile two accepted decision records ([ADR-0041 §status](0041-guided-restore-and-peer-retrieval.md),
[ADR-0042 §status](0042-write-only-repositories.md)) and the implementation status already cite
"a live Playwright walk" as evidence — verification that was performed by hand and committed
nowhere, which is a claim the checkers cannot see and a walk nobody can re-run.

## Decision

**A browser suite, `FallbackPlan.Web.DomTests`, driving real Chromium through Playwright for
.NET against the real console host and the fake service client.** The shape:

1. **The suite is C# and MSTest, like every other suite.** Playwright for .NET is one NuGet
   package; the alternative — a JavaScript test runner — would bring a second toolchain
   (Node, a package manager, a second lockfile discipline) into a repository that deliberately
   has none, to run tests against an emulated DOM that cannot represent the failing class.
   The page itself stays framework-free; this decision is about *testing* it, not building it.

2. **The split with `Web.Tests` is by what the test needs.** Wire names, endpoint behaviour,
   refusals, and log records stay in `Web.Tests` — fast, browser-free, on every OS. What needs
   a real DOM — gating chains walked by real clicks, modality, downloads, focus — lives in
   `Web.DomTests`. A test that could live in either belongs in `Web.Tests`.

3. **The suite runs where a browser is provisioned, and only there.** CI runs `dotnet test` on
   the whole solution four ways (three OSes, the source-archive sandbox, the hostile locale),
   and the solution gate forbids per-configuration exclusions. So the suite compiles
   everywhere and its tests skip — reported as **skipped with a reason, never passed** (the
   platform-facts rule) — unless the run opts in. One dedicated Linux job installs Chromium
   and opts in. One platform is deliberate: the semantics under test are the browser's, not
   the operating system's, and three browser installs would buy matrix minutes, not coverage.

4. **The harness is a copy, and visibly so.** The suite needs the console harness and fake
   client that `Web.Tests` holds as internals. This repository has no `InternalsVisibleTo`
   and that absence is a recorded position; the duplication is the price of that rule and is
   meant to be visible — the same trade the two `ConsoleLogging` copies document.

## Consequences

- The three defects above each have a browser-level pin: the full ceremony walk (no modal over
  the ceremony, a real download arming the chain, the end-of-ceremony dialog *visible* and the
  next screen live), the rebuild page enabling by keystroke without losing focus, and the jobs
  view carrying an acknowledged cancel on the card.
- The "live Playwright walk" evidence in ADR-0041/0042 now has a committed, re-runnable
  counterpart: the restore wizard's full walk (unlock against a real archive's key files, plan,
  run, source release) and the write-only provisioning ceremony live in the suite, alongside
  the views, sign-in, configuration editing, and the chrome's staleness presentation.
- CI gains one job and a browser download in it; the other jobs are untouched and the coverage
  gate is unaffected (test-suffixed modules are excluded from the module table by name).
- The suite's `packages.lock.json` participates in locked restore like every other; the .NET 10
  SDK prunes Playwright's framework-provided dependencies, so the lock gains exactly one
  package entry.

## Alternatives considered

- **A JavaScript runner with a DOM emulation (jsdom or similar).** Rejected on both halves: a
  second toolchain in a single-language repository, and an emulation that does not implement
  `<dialog>` modality, top-layer painting, or CSP — the exact semantics that failed.
- **Playwright's MSTest wrapper package.** Rejected as surface: the base-class convenience is
  small, the extra dependency is not, and the plain API keeps the suite shaped like every
  other MSTest suite here.
- **Running the suite on all three OSes.** Rejected as cost without coverage; revisit on the
  first browser-behaviour defect that is genuinely OS-specific.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Written with the suite, after the setup-ceremony strandings showed the gap |

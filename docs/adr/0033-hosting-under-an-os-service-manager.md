# ADR-0033 — Hosting the agent under an OS service manager

**Status:** Accepted · **Date:** 2026-08 · **Builds on:** [ADR-0028](0028-service-boundary-and-deployment-topologies.md)

---

## Context

[ADR-0028](0028-service-boundary-and-deployment-topologies.md) decided the service *boundary* — one writer per repository, a local binding always present, the passphrase drawn from the platform keystore so a machine backs itself up unattended — and deliberately stopped there. It said nothing about how the process is *launched and supervised* by the operating system. Until now the agent ran only as a foreground console process: `fallbackplan-agent run …` looped until Ctrl+C, and the only signal it handled was `Console.CancelKeyPress`.

That is enough to run it by hand and no more. It cannot be registered in the Windows service registry (the Service Control Manager), started by systemd at boot, or supervised by launchd. Worse, under systemd and launchd a *stop* is delivered as `SIGTERM`, which the agent did not handle — so the default action, terminating the process outright, would take effect: the kernel would release the writer lock, but no in-flight backup would finish and none of the disposal that flushes state would run. "The service backs its machine up unattended" (FR-SVC-008) is only true once the OS can own the process's lifetime.

Two things ADR-0028 already settled make this a small piece of work rather than a large one. Unattended unlock is solved: the passphrase comes from the per-account keystore, seeded once by `unlock`. And graceful shutdown already works end to end — a single `CancellationToken` threads from the entry point through the run loop and the listeners, and cancelling it unwinds the `finally`/`await using` chain that releases the writer lock and returns exit 0. What was missing was only the wiring from each manager's *stop* onto that token, and a way to produce the registration itself.

## Decision

**Translate every manager's stop onto the existing cancellation, and add nothing else to the run loop.** `ServiceProcessHost` is the one entry the process calls. On a console or under systemd/launchd it registers `Console.CancelKeyPress` (SIGINT, as before) *and* `PosixSignalRegistration` for `SIGTERM` — the signal systemd and launchd send — both routed to the same `CancellationTokenSource.Cancel()`. There is no new shutdown path: the manager's stop and an operator's Ctrl+C converge on the code that already unwinds cleanly.

**On Windows, bridge the Service Control Manager with `ServiceBase`, not the Generic Host.** `WindowsServiceHost` is a `System.ServiceProcess.ServiceBase` subclass: the SCM starts the process, `OnStart` launches the agent on the cancellation token, and the SCM's stop cancels it and waits for the writer lock to release before the process exits. The process detects that the SCM (rather than a console) started it and hands off; otherwise it takes the signal path above. We did **not** adopt `Microsoft.Extensions.Hosting` and its `UseWindowsService()`/`UseSystemd()`. The agent is a hand-rolled console app by consistent choice across this solution, and pulling in the Generic Host to attach one lifetime would be a large architectural change to gain machinery — readiness, restart, logging — that this service either does not need or already has by another means.

**Generate the registration; never perform it.** The `install` verb prints the artifact that would register the agent — a systemd unit, a launchd LaunchDaemon plist, or the Windows `sc.exe` commands — to standard output, so it can be redirected to a file, with the apply steps and the unlock reminder on standard error. It opens no repository and no keystore and changes nothing on the machine. The alternative, shelling out to `systemctl`/`launchctl`/`sc.exe` to register the service directly, was rejected: it would run privileged, mutate the system in ways an operator cannot inspect first, and — because it is an OS mutation — could not be tested on CI at all. A printed definition is inspectable before it is applied, and its generation is pure text that is verified on every platform.

**Provisioning stays an explicit, out-of-band step.** The generated artifact names the account the service runs as, and the printed guidance states the rule ADR-0028 §9 already implies: run `unlock` once *as that same account* before the service starts, or the boot-started process exits 1 with no passphrase. The keystore is scoped to the account, so the operator seeding it and the service reading it must be the same identity.

The generated unit uses `Type=simple` (systemd) / a plain LaunchDaemon (launchd) / `start= auto` (Windows). There is no `sd_notify` readiness protocol and no shutdown deadline: the run loop has no readiness handshake to report, and the writer lock is released by the OS on death, so a manager that kills a slow stop loses nothing it needs a heuristic to recover.

## Consequences

**Positive**

- The stop a service manager actually sends is now a clean shutdown on every platform: the SIGTERM lifecycle is proven by a test that spawns the shipped apphost, signals it, and asserts exit 0 and a freed writer lock.
- The registration an operator applies is generated from the same argument surface the agent runs with, so the unit cannot drift from the CLI the way a hand-maintained template would, and it is inspectable before it touches the system.
- No framework was adopted: the change is three small types in the Agent host and one package for the Windows SCM handshake.

**Negative**

- The Windows SCM lifecycle and the launchd lifecycle cannot run on this project's Linux CI. Their *testable* parts — the artifact generation, and on Windows the adapter's cancel-on-stop path — are unit-tested; the live Start/Stop handshake through a real Service Control Manager and a real launchd is verified manually. This is stated rather than hidden: a green CI run does not claim to have exercised the Windows or macOS service lifecycle.
- Service-context detection on Windows uses `Environment.UserInteractive`, the same signal the framework's own helper falls back to. If a deployment ever defeats it, the sturdier parent-process check is the documented replacement.
- Self-contained/single-file publishing and signed installers are still ahead (roadmap Phase 4); the generated artifacts reference whatever executable path is deployed, framework-dependent or not.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | The agent hosts under systemd, launchd and the Windows SCM over the existing cancellation token; `install` generates the registration artifact for each. ADR-0028 stopped at the boundary; this carries the process across it. |

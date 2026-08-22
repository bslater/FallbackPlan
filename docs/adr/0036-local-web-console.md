# ADR-0036 — The local web console: a browser front end without a second writer

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-SVC-001, FR-SVC-003, FR-SVC-005, FR-SVC-006, NFR-OPS-006, NFR-UX-001, NFR-UX-002
**Related:** [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [ADR-0029](0029-pipeline-and-service-concurrency.md), [ADR-0019](0019-third-party-dependency-policy.md), [ADR-0031](0031-exception-messages-are-resources.md), [architecture 11 §2](../architecture/11-solution-structure.md#2-dependency-rules), [Q18](../open-questions.md#q18--streaming-restored-content-to-a-remote-client), [Q19](../open-questions.md#q19--console-identity-and-multi-operator-access), [T-16](../threat-model.md)

---

## Context

Every front end is a client of the service over one command contract — FR-SVC-001,
built and enforced since Phase 2. The CLI is that client today. The roadmap has
promised a web front end since the original proposal, `FallbackPlan.Web` has held
a reserved line in the solution layout, and the contract already carries
everything a dashboard needs: the status matrix, snapshots, directory listings,
jobs, per-job progress events, and the full action surface.

What no document had decided is how a **browser** joins a trust model built on
operating-system peer credentials. The local binding authenticates by filesystem
permissions on a socket only the operator can reach (FR-SVC-003, T-16). A browser
cannot speak to a Unix domain socket or a named pipe; it speaks HTTP to a port —
and a listening port is reachable by every process on the machine, every other
local user, and (through a hostile web page the operator happens to have open)
by script running in the browser itself. Two classes of attack exist against
local web consoles specifically: cross-site requests from a page the operator is
visiting, and DNS rebinding, where an attacker's hostname resolves to
`127.0.0.1` so the hostile page's requests arrive same-origin. Both are well
documented against development servers and local tools; neither has anything to
do with how well the UI is built.

Two open questions sit next to this and must not be quietly answered by an
implementation: [Q18](../open-questions.md#q18--streaming-restored-content-to-a-remote-client)
(may restored *content* stream to a client?) and
[Q19](../open-questions.md#q19--console-identity-and-multi-operator-access)
(are a console's actions attributable to a person?).

## Decision

### 1. The console is a separate client process, and it can never write

`FallbackPlan.Web` builds to `fallbackplan-web`, a process the operator runs
beside the service. It references `FallbackPlan.Api` and nothing below it — not
`Application`, not the engine — so the architecture 11 §2 rule for UIs holds by
construction and `ArchitectureTests` holds it by assertion. It talks to the
running service through `LocalServiceClient` exactly as any client does, and the
service applies the same queueing, the same refusals, and the same writer-role
discipline it applies to every caller.

There is **no direct mode**. The CLI's direct mode exists because a person at a
terminal may legitimately take the writer role when no service runs; a web
server holding the writer role would be a second writer with a network face.
When no service is listening, the console starts, says exactly that, and keeps
retrying — a person who starts the service then sees the console attach without
restarting anything.

### 2. Loopback only, alive only while the operator runs it

The listener binds `127.0.0.1` and nothing else, with no flag to widen it.
Remote access to a service is the paired remote binding's job (ADR-0028 §5,
ADR-0030): it authenticates devices, pins identity, and refuses strangers —
none of which an HTTP listener on a LAN interface would do. Offering the weaker
path would make it the used path.

FR-SVC-003's "a default install listens on no port" survives untouched: the
console is not installed as a service, not started by the agent, and listens
only between its launch and its Ctrl+C. The port is ephemeral by default
(`--port` to fix it), printed at start.

### 3. A per-run token stands in for the socket's peer credentials

At start the console draws a 256-bit token and prints one URL:
`http://127.0.0.1:<port>/?token=…`. Every request to the API and the event
stream must present that token as a bearer credential; the page exchanges the
URL's query form for session storage on first load. Comparison is
constant-time. Requests whose `Host` is not loopback are refused outright,
which closes DNS rebinding; requiring the token on every data request closes
cross-site requests, because another origin cannot read or send it.

This is deliberately the Jupyter posture, chosen over three alternatives (§
below): the operator who launched the process is the operator holding the URL,
which answers Q19 for this surface **without deciding it** — one operator, the
launcher, no identity model invented. Q18 is likewise left closed in practice:
the console never requests file content, and a restore commanded from it writes
on the service's machine with the console told counts and a path (FR-SVC-005's
posture, applied to the local console too).

### 4. The console relays; it never derives

Endpoints map one-to-one onto `ServiceCommand`s and return the service's
results; the status view renders `StatusResult` as sent. The one thing the
console adds is honesty about its own connection, which NFR-OPS-006 requires of
any client: when the service stops answering, every panel is marked stale with
the age of last contact — never left green, never painted failed.

Progress arrives by bridging `WatchAsync` onto a server-sent-events stream.
Completion is confirmed from the job list, not inferred from the stream, which
drops events for a slow watcher by design (ADR-0029 §5).

### 5. The page is self-contained and first-party

Hand-written HTML, CSS and JavaScript, embedded in the assembly — no CDN, no
package manager, no framework, nothing fetched at runtime. The HTTP host is
Kestrel via the in-box ASP.NET Core shared framework: first-party, so
ADR-0019's third-party surface grows by nothing. User-facing strings follow
ADR-0031 (resources, generated accessors); layout and contrast aim at
NFR-UX-001's WCAG 2.2 AA bar from the start rather than retrofitting it at
Phase 6.

## Consequences

**Positive** — a browser UI lands with no new writer, no new port by default, no
new dependency tier, and no quiet answers to Q18/Q19; the contract's "every
front end is a client" claim is finally tested by a second front end, which is
the same service the contract rule got from a second storage provider.

**Negative** — a token in a URL is showable over a shoulder and lands in
browser history until the page strips it; the console is single-operator by
design, so a household sharing one machine account shares one console; loopback
only means no phone on the same LAN, which will disappoint before Phase 6
answers it properly; and SSE over one origin costs a browser connection slot
per tab.

**Neutral** — the console process is another thing to start (or not: nothing
depends on it running); the UI polls the service for lists and watches only
progress, so its load on the service is a client's load, bounded by the queue
like everyone else's.

## Alternatives considered

- **Host the UI inside the Agent.** One process, no attach dance — and a
  network listener inside the writer-role process, on by a flag that would
  erode "a default install listens on no port" one release at a time. The
  service's face is the contract; front ends stay clients.
- **OS-credential handshake instead of a token** (browser fetches a nonce, a
  helper on the socket signs it). Stronger in theory, but it reintroduces the
  socket dependency the browser cannot hold, needs per-platform helpers, and
  still ends in a bearer secret in the page. The token is the same trust with
  less machinery.
- **A localhost certificate and HTTPS.** Loopback traffic does not cross a
  network; the threats here are same-machine and same-browser, which TLS does
  not address, and trusting a generated certificate would train operators to
  click through warnings.
- **An off-the-shelf SPA framework.** Faster to a first screen, then a
  dependency tier ADR-0019 would have to carve an exception for, a build
  toolchain in CI, and a supply-chain surface in the one component that holds a
  bearer token. The UI's scope — panels over a small typed contract — does not
  need it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written with the Phase-2 service boundary built and both bindings proven |
| 2026-08 | Accepted | First front end beyond the CLI; Q18/Q19 left open and respected |

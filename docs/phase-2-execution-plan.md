# Phase 2 — Execution plan: the service boundary and pipeline concurrency

**Status:** in progress · **Scope:** the service-boundary half of [Phase 2](roadmap.md#phase-2--peer-to-peer-backup-and-the-service-boundary) · **Predecessor:** [Phase 1 plan](phase-1-execution-plan.md) · **Decisions:** [ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md), [ADR-0029](adr/0029-pipeline-and-service-concurrency.md)

---

## What this is for

Phase 1 built an engine two processes could both drive. ADR-0028 established
that they must not: writer identity is per state directory, so an Agent on its
poll loop and a user running `backup` are *the same writer* by construction,
drawing from a sequence space architecture 04 §2 requires to be monotonic and
gapless. The damage escalates from colliding spool paths to a write intent
reported durable when it was never written, to void deltas published for
sequence numbers another process is still using — and because 04 §2 classifies a
duplicate sequence as identity cloning, the first symptom is
[T-18](threat-model.md)'s security alert.

So this phase makes the service the sole holder of the writer role, gives it a
versioned command contract, and turns every front end into a client of it. It
then does the concurrency work ADR-0029 sequenced: measure, remove serial cost,
*then* parallelise.

## What is in scope, and what is deliberately not

**In scope.** The local binding, the command contract, the service, the CLI as a
client with direct mode, per-job progress, cancellation, keystore unlock on all
three platforms, and the full ADR-0029 §6 sequence.

**Not in scope, and named rather than implied.** The remote TLS binding, device
pairing, the multi-instance console, and the desktop and web front ends.
Topologies 3 and 4 of ADR-0028 §1 are designed and not built; the remote binding
exists as a seam that is off by default and refuses to enable, because pairing
reuses machinery [architecture 09 §3](architecture/09-replication-and-peers.md#3-pairing)
defines for peers and that does not exist yet.
[Q18](open-questions.md#q18--streaming-restored-content-to-a-remote-client) and
[Q19](open-questions.md#q19--console-identity-and-multi-operator-access) gate the
console and stay open.

## What already exists

| Prerequisite | State |
|---|---|
| The engine, the scanner, publication, restore, the recovery tool | Phase 1, every exit criterion traced to a test |
| Schedule arithmetic, the job-state journal, status derivation, instrumentation | Phase 1 push 2 ([ADR-0027](adr/0027-services-scheduling-status-telemetry.md)) |
| The vocabulary a client needs — `JobState` (10 §3), `ProtectionState` (10 §1.1) | Written, but **8 of 14 job states were never emitted** |
| A progress channel | **None.** `IPublicationObserver` is nine payload-free callbacks serving the interruption harness |
| Any IPC | **None.** No socket, pipe, or listener anywhere in `src/` |

---

## Waves

```text
A · Ownership ──▶ B · Contract ──▶ C · Service ──▶ D · Clients
                                        │
                        E · Measure ──▶ F · Serial cost ──▶ G · Concurrency ──▶ H · Close
```

### Wave A — Ownership

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| A1 | This plan | — | Every wave has an acceptance criterion before code exists |
| A2 | `JobState`, `ProtectionState`, `VerificationDetail`, `BackupSetStatus` move to Domain; `JobProgress` and `IJobProgressReporter` added | ADR-0029 §5 | The engine can emit job states and the contract can carry them without either referencing the application layer |
| A3 | `StateDirectoryLock` — an OS advisory lock on the state directory, with an informational owner file | FR-SVC-002, ADR-0028 §4 | A second caller is refused **naming the holder**; killing the holder releases the role with no stale-lock heuristic |
| A4 | `AtomicFile`; `state.json`, `jobs.json` and `config.json` replaced atomically | — | A crash mid-write cannot leave a truncated state file |

### Wave B — The command contract

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| B1 | `FallbackPlan.Api` — commands, results, events, client and service interfaces, status roll-up | FR-SVC-001, NFR-OPS-006 | The project references Domain and nothing else, so "UIs depend on the contract, never the engine" is mechanically enforceable |
| B2 | The local binding — Unix domain socket or named pipe, authenticated by the operating system | FR-SVC-003, [T-16](threat-model.md) | No password, no token file, no port; the caller is identified by peer credentials |
| B3 | The remote binding as a seam, off by default | FR-SVC-003 | A default install listens on no port, and enabling the remote binding without pairing is refused with a stated reason |
| B4 | Contract-version negotiation | FR-SVC-007 | Incompatible versions refuse **naming both**, per service rather than wholesale |

### Wave C — The service

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| C1 | The Agent becomes long-lived: opens the repository once, holds the writer role, hosts the endpoint | FR-SVC-001, FR-SVC-008 | Argon2id runs once per service lifetime rather than once per poll; a service with no front end installed backs up unattended |
| C2 | The job queue — sets serialised, restore and verify alongside, user work outranks scheduled work, cancellation | ADR-0029 §4 | A cancelled job records `Cancelled` and its sequences become void-delta obligations the next publication discharges |
| C3 | Progress events threaded through the engine | FR-SVC-006 | A running job reports states beyond `Scanning`; a client distinguishes a working job from a stalled one without reading engine files |
| C4 | Keystore unlock — DPAPI, Keychain, and the Linux equivalent | NFR-SEC-009, ADR-0028 §9 | Scheduled backup runs with no passphrase in the environment; key export still takes a passphrase per invocation |

### Wave D — The clients

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| D1 | `IOperationGateway` with service and direct implementations; mode resolution and reporting | FR-SVC-001, ADR-0028 §3 | The CLI says which mode it used; with a service running, `--direct` is refused naming the holder |
| D2 | Verbs routed through the contract; engineering verbs stay direct and say so | FR-SVC-001 | The same operation performs identically through either path |
| D3 | The 11 §2 dependency rules and `ApiShapeTests` | NFR-PORT-004 | Rules that were written but unenforced become tests; every public contract operation is async, cancellable, and returns a result |

### Wave E — Measure

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| E1 ✅ | `PerformanceTests/ThroughputBenchmarks` | NFR-PERF-007 | Throughput at several concurrency settings, with compression and segmentation profile attributed |
| E2 ✅ | [`phase-2-benchmarks.md`](phase-2-benchmarks.md) | — | The numbers the next waves are aimed at are published before either begins |

### Wave F — Remove serial cost

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| F1 | The spool checkpoint is written once at create; the resume walk authenticates; `TryResume` gains a production caller | NFR-SEC-003 | An interrupted blob resumes byte-identically, proven by test rather than assumed |
| F2 ✅ | Upload leaves the archive loop, with a drain barrier before the index delta | ADR-0029 §2 | Every blob's covering intent is durable before its own PUT, with several in flight |
| F3 ✅ | The per-record cipher construction is removed | — | Measured, not assumed: [the benchmark](phase-2-benchmarks.md) shows it is below the noise, which is itself the result |

**F1 is not a smaller change than it looks, and is deliberately not rushed.** The
checkpoint's watermark is what makes resume safe: records beyond it are discarded
and their ordinals re-used, so a watermark that lags by *N* records would re-use
ordinals that were already used with different bytes if the source changed
between runs — nonce reuse under one `(blob_key, ordinal)`, the failure
specification 05 §6.1 calls catastrophic. The safe form is a resume walk that
**authenticates** each record and treats the last authenticating record as the
resume point, which removes the need for a per-record watermark entirely. That
touches the checkpoint format and `TryResume`, and it must land with a proof that
resume is byte-identical — not before one.

### Wave G — Concurrency

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| G1 ✅ | `Concurrency` on `CapturePolicy`, validated | NFR-PERF-001, NFR-OPS-004 | A value outside 1..64 is a named defect; the default is 2, below a 4-core laptop's capacity; 1 stays valid and tested |
| G2 | The staged pipeline with the ordering barrier at ordinal assignment | NFR-PERF-002, ADR-0029 §1 | Read, hash and compress run concurrently; ordinal assignment, encryption, append and digest stay strictly ordered |
| G3 | Acceptance and interruption proof | NFR-PERF-002 | Restored bytes identical at concurrency 1, 2 and 4; *N* in-flight blobs each independently intent-covered |

### Wave H — Close

| # | Item | Acceptance |
|---|------|-----------|
| H1 | ADR-0028 and ADR-0029 accepted, with amendments recording what implementation and measurement decided | [Q20](open-questions.md#q20--where-the-concurrency-default-sits-and-whether-pinning-survives-measurement) closed with a number, not an opinion |
| H2 | Traceability, roadmap, and 11 §1 updated | No requirement points at a test that does not exist; the roadmap says what the remote binding still owes |

---

## Exit criteria

From [the roadmap](roadmap.md#phase-2--peer-to-peer-backup-and-the-service-boundary),
restated here with the test that will prove each:

| Criterion | Proven by |
|---|---|
| A second process cannot take the writer role — it refuses naming the holder | `Application.Tests/StateDirectoryLockTests` |
| A default install listens on no port | `Api.Tests` — the remote-binding assertion |
| A running job reports states beyond `Scanning` | `Api.Tests` — the progress-event assertion |
| A service with no front end installed backs up unattended | `Hosts.Tests` |
| Client and service at incompatible versions refuse with both versions named | `Api.Tests` — the negotiation assertion |
| Restored bytes identical regardless of concurrency setting | **not yet met** — the setting exists and validates; the staged pipeline that would make it mean something does not |
| Recovery still works with no service and no state directory | `Hosts.Tests` — the recovery drill, unchanged |

### What is not met, said plainly

- **An unpaired remote client refused**, and **a restore commanded remotely
  writing on the service's machine.** Both need the remote binding, which needs
  pairing.
- **Restored bytes identical regardless of concurrency.** `Concurrency` is a
  validated setting and nothing consumes it yet, so every value runs the same
  sequential pipeline. The benchmark reports the settings anyway, which makes the
  spread across them visible as noise — a calibration any future concurrency
  result has to beat.
- **Restore, verify and check over the command surface.** The contract carries
  them; this service build answers them with a stated
  "this is a read path, run it directly" rather than a silence.
- **An interruption case for *N* blobs in flight.** ADR-0029 §2 makes the
  "durable but unreferenced" state of architecture 04 §5.1 reachable *N* at a
  time. The existing matrix passes unchanged and covers the state at *N*=1; the
  case that kills a job with several uploads outstanding is not written.

---

**Previous:** [Phase 1 execution plan](phase-1-execution-plan.md) · **Decisions:** [ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md) · [ADR-0029](adr/0029-pipeline-and-service-concurrency.md)

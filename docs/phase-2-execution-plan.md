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
| F1 ✅ | The spool checkpoint is written once at create; the resume walk authenticates; `TryResume` gains a production caller | NFR-SEC-003 | An interrupted blob resumes byte-identically, proven by test rather than assumed |
| F2 ✅ | Upload leaves the archive loop, with a drain barrier before the index delta | ADR-0029 §2 | Every blob's covering intent is durable before its own PUT, with several in flight |
| F3 ✅ | The per-record cipher construction is removed | — | Measured, not assumed: [the benchmark](phase-2-benchmarks.md) shows it is below the noise, which is itself the result |

**F1, as built.** The resume walk now authenticates every spooled record, so the
tags themselves bound the resume and the sidecar is written once at create. The
per-record `fsync` and the whole-file sidecar rewrite — about 128 of each per
128 MiB blob, both blocking calls inside `async` methods — are gone.

Two things came out of doing it that the plan had left open.

**The specification does not require a per-record watermark, so no erratum was
filed.** This was worth checking before touching anything: 05 §6.2's MUST list is
exactly seven fields — `blob_salt`, `blob_id`, `blob_counter`, `key_generation`,
segmentation profile and parameters, compression profile and codec version,
encryption profile — and every one is fixed for the blob's life. The words
"watermark", "fsync" and "per-record" appear nowhere in 05 §6, and
`SealedWatermark` was an implementation artefact with no normative counterpart.
What §6 actually requires is four things: the checkpoint stores sealed bytes
rather than a recomputable plaintext offset (§6.1), the seven pinned fields are
compared and any mismatch restarts (§6.2), resume is byte-identical and continues
at the next ordinal (§6.3), and a partial spool is never uploaded.

**A failed tail restarts; it is not truncated.** The earlier sketch had the walk
treat the last authenticating record as the resume point and truncate past it.
That returns the torn record's ordinal to a blob whose salt already covered it,
which is the one thing 05 §6.1 calls catastrophic. Restarting instead draws a
fresh salt, so no ordinal is ever re-used under one salt — and it is what 05 §6.2
already says to do: *"Restart is always the safe failure. A writer in any doubt
MUST restart."* This is also strictly safer than the watermark it replaces, under
which a record could be fully durable yet beyond the watermark — the `fsync`
preceded the checkpoint write — and have its ordinal re-used while intact.

One behaviour changed to make resume reachable at all: a session disposed with a
blob still open now **abandons** that writer rather than deleting its spool.
`FlushAsync` seals the open blob on normal completion, so reaching disposal with
one open is the interrupted case (04 §5.1 row 3). Before this the session's own
unwinding deleted the very spool resume exists for, and only a true process kill
could leave one behind.

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
| Restored bytes identical regardless of concurrency setting | **partly** — `InterruptionTests/ConcurrentUploadTests` proves it at 1 and 4 for the upload workers, which is the only part of the setting that is real; the staged pipeline is not built |
| Recovery still works with no service and no state directory | `Hosts.Tests` — the recovery drill, unchanged |

### What is not met, said plainly

- **An unpaired remote client refused**, and **a restore commanded remotely
  writing on the service's machine.** Both need the remote binding, which needs
  pairing.
- **Restored bytes identical regardless of concurrency**, in full. `Concurrency`
  sizes the upload workers and nothing else, so the stages that dominate a
  backup are still sequential at every value. The benchmark reports the settings
  anyway, which makes the spread across them visible as noise — a calibration any
  future concurrency result has to beat.
- **Restore, verify and check over the command surface.** The contract carries
  them; this service build answers them with a stated
  "this is a read path, run it directly" rather than a silence.
- **A kill test with several uploads outstanding.** `ConcurrentUploadTests` now
  proves the per-blob invariant — every blob's covering intent is durable before
  its own PUT, at concurrency 1 and 4 — and that restores are byte-identical
  across both. What is still not written is the case that *kills* a job mid-
  upload with several blobs in flight, which is the harder half of architecture
  04 §5.1 at *N* > 1.

---

## Where to pick up

The next round starts here. Everything below is in priority order, and each item
says what "done" looks like so it does not have to be re-derived.

F1 is done and off this list; what it decided is recorded under Wave F above.
The measurement it was aimed at has not been re-run, which is the first item.

### 1. Re-measure, now that the per-record cost is gone

F1 removed ~128 `fsync`s and ~128 whole-file sidecar rewrites per 128 MiB blob.
Nothing has yet said what that bought. `ThroughputBenchmarks` exists and
[phase-2-benchmarks.md](phase-2-benchmarks.md) still carries the pinned-mode rows
measured before the change, so they now describe code that is gone.

**Done means:** the benchmark is re-run on the reference machine — not a
container — and the pinned-mode rows either show a number or are struck with the
statement that the cost was below noise, which is itself a result (F3's
precedent). This is ADR-0029 §6 step 1 applied to its own step 2, and it is what
[Q20](open-questions.md#q20--where-the-concurrency-default-sits-and-whether-pinning-survives-measurement)
needs closing with a number rather than an opinion.

### 2. G2 — the staged pipeline

ADR-0029 §1 has the correctness boundary already drawn: read, hash and compress
may run concurrently; **assign ordinal → encrypt → append → digest** must stay
strictly ordered, with a reorder buffer restoring order before the barrier.
Encryption sits *below* the barrier because it consumes the ordinal it is nonced
with.

Four pieces of shared state have to move or be rented per work item, all four
located:

| What | Where | Why |
|---|---|---|
| `_segmentBuffer` / `_compressed` | `ArchiveSession.cs` ctor | one rental per session, and `plaintext` aliases the buffer the next read overwrites |
| `ZstdSegmentCodec` | `ArchiveSession.cs` ctor | stateful, documents itself as not thread-safe |
| `_writtenThisSession` | `ArchiveSession.cs` | unsynchronised `HashSet`; belongs below the barrier |
| `CdcSegmentReader`'s Rabin window | `CdcSegmentReader.cs` | persists across segments by design, so the reader stays the single-threaded producer |

`TreeWalkPublisher`'s `Stack<Frame>` DFS is untouched: the walk stays
single-threaded and the concurrency is in what happens to the bytes it yields.

**Done means:** `ConcurrentUploadTests`'s byte-identical theory extends to a full
tree at 1, 2 and 4, and the concurrency rows in
[phase-2-benchmarks.md](phase-2-benchmarks.md) stop being noise.

### 3. A kill test with several uploads outstanding

Architecture 04 §5.1's "durable but unreferenced" state is now reachable *N* at a
time. The matrix proves it at *N*=1; the case that kills a job mid-upload with
several blobs in flight is not written.

### 4. The remote binding

Topologies 3 and 4, and the two exit criteria they own. Blocked on pairing, which
reuses [architecture 09 §3](architecture/09-replication-and-peers.md#3-pairing)'s
machinery, and gated by
[Q18](open-questions.md#q18--streaming-restored-content-to-a-remote-client) and
[Q19](open-questions.md#q19--console-identity-and-multi-operator-access).

### Watch list

The intermittent `Hosts.Tests` failure recurred during F1, and this time it has a
name: **`ServiceTests.A_backup_commanded_by_a_client_runs_and_reports_states_beyond_scanning`**.
It failed once in a full-suite run, then passed in isolation and in two
subsequent full runs — the same pattern as before, and consistent with the
original suspicion that a timing-sensitive service test is sensitive to load,
since the suite runs twelve projects in parallel and those tests bind sockets.

It is unrelated to F1: the change touches the blob spool and this test commands a
backup over the service contract, and the whole suite is green either side of it.

**Diagnosed by inspection, not yet reproduced under control.** `ProgressHub`
registers a subscriber's channel *inside* `WatchAsync`'s iterator, so the
subscription is not established until the first enumeration, and `Report` writes
only to subscribers already registered — there is no replay. The test starts its
watcher with `Task.Run` and then commands the backup on the calling thread, so
when the thread pool is saturated (twelve projects in parallel) the backup can
emit `Scanning`, and sometimes more, before the watcher has subscribed. Those
events go to nobody and the `Assert.Contains` lines fail. That is precisely a
failure that appears only under load.

The window is not the test's alone: **any** caller of `WatchAsync` has an
unobservable gap between deciding to watch and being subscribed, which for a UI
attaching to a running job means silently missing events. `ProgressHub.Latest`
exists and documents itself as being "for a client that arrives late", and
nothing reads it.

So the fix is a product one, not a test one: make subscription happen when the
watcher is created rather than when it is first enumerated. Replaying `Latest`
on subscribe is worth doing too, but it does not fix this on its own — a late
watcher would still miss the intermediate states this test asserts.

---

**Previous:** [Phase 1 execution plan](phase-1-execution-plan.md) · **Decisions:** [ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md) · [ADR-0029](adr/0029-pipeline-and-service-concurrency.md)

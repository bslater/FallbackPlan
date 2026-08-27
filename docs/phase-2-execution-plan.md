# Phase 2 — Execution plan: the service boundary and pipeline concurrency

**Status:** the service boundary is built on both bindings; the remote binding carries a paired console over a real socket, and the peer-protocol documents this phase owed (03–06) are written and implemented — what remains is the direct-to-destination tail ([ADR-0046](adr/0046-direct-to-destination-publication.md): the peer write adapter, the retention-with-trimming drill, and the default flip) and the reference-machine measurement H1 still owes · **Scope:** the service-boundary half of [Phase 2](roadmap.md#phase-2--peer-to-peer-backup-and-the-service-boundary) · **Predecessor:** [Phase 1 plan](phase-1-execution-plan.md) · **Decisions:** [ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md), [ADR-0029](adr/0029-pipeline-and-service-concurrency.md)

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

**Originally out of scope, since delivered.** The remote TLS binding and device
pairing were named here as out of scope for this plan's first waves, blocked on
machinery [architecture 09 §3](architecture/09-replication-and-peers.md#3-pairing)
defines for peers. That machinery now exists, and a later round built the binding
on top of it: topologies 3 and 4 of ADR-0028 §1 are built, and the two exit
criteria they own are met (see [§1 below](#1-the-remote-binding--built) and the
exit-criteria table). Still genuinely out of scope: the multi-instance console
and the desktop and web front ends.
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
| A1 | ✅ This plan | — | Every wave has an acceptance criterion before code exists |
| A2 | ✅ `JobState`, `ProtectionState`, `VerificationDetail`, `BackupSetStatus` move to Domain; `JobProgress` and `IJobProgressReporter` added | ADR-0029 §5 | The engine can emit job states and the contract can carry them without either referencing the application layer |
| A3 | ✅ `StateDirectoryLock` — an OS advisory lock on the state directory, with an informational owner file | FR-SVC-002, ADR-0028 §4 | A second caller is refused **naming the holder**; killing the holder releases the role with no stale-lock heuristic |
| A4 | ✅ `AtomicFile`; `state.json`, `jobs.json` and `config.json` replaced atomically | — | A crash mid-write cannot leave a truncated state file |

### Wave B — The command contract

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| B1 | ✅ `FallbackPlan.Api` — commands, results, events, client and service interfaces, status roll-up | FR-SVC-001, NFR-OPS-006 | The project references Domain and nothing else, so "UIs depend on the contract, never the engine" is mechanically enforceable |
| B2 | ✅ The local binding — Unix domain socket or named pipe, authenticated by the operating system | FR-SVC-003, [T-16](threat-model.md) | No password, no token file, no port; the caller is identified by peer credentials |
| B3 | ✅ The remote binding as a seam, off by default | FR-SVC-003 | A default install listens on no port; the seam was later carried to a real socket that admits only a pinned peer (see [§1](#1-the-remote-binding--built)) |
| B4 | ✅ Contract-version negotiation | FR-SVC-007 | Incompatible versions refuse **naming both**, per service rather than wholesale |

### Wave C — The service

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| C1 | ✅ The Agent becomes long-lived: opens the repository once, holds the writer role, hosts the endpoint | FR-SVC-001, FR-SVC-008 | Argon2id runs once per service lifetime rather than once per poll; a service with no front end installed backs up unattended |
| C2 | ✅ The job queue — sets serialised, restore and verify alongside, user work outranks scheduled work, cancellation | ADR-0029 §4 | A cancelled job records `Cancelled` and its sequences become void-delta obligations the next publication discharges |
| C3 | ✅ Progress events threaded through the engine | FR-SVC-006 | A running job reports states beyond `Scanning`; a client distinguishes a working job from a stalled one without reading engine files |
| C4 | ✅ Keystore unlock — DPAPI, Keychain, and the Linux equivalent | NFR-SEC-009, ADR-0028 §9 | Scheduled backup runs with no passphrase in the environment; key export still takes a passphrase per invocation |

### Wave D — The clients

| # | Item | Resolves | Acceptance |
|---|------|----------|-----------|
| D1 | ✅ `IOperationGateway` with service and direct implementations; mode resolution and reporting | FR-SVC-001, ADR-0028 §3 | The CLI says which mode it used; with a service running, `--direct` is refused naming the holder |
| D2 | ✅ Verbs routed through the contract; engineering verbs stay direct and say so | FR-SVC-001 | The same operation performs identically through either path |
| D3 | ✅ The 11 §2 dependency rules and `ApiShapeTests` | NFR-PORT-004 | Rules that were written but unenforced become tests; every public contract operation is async, cancellable, and returns a result |

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
| G2 ✅ | The staged pipeline with the ordering barrier at ordinal assignment | NFR-PERF-002, ADR-0029 §1 | Read, hash and compress run concurrently; ordinal assignment, encryption, append and digest stay strictly ordered |
| G3 | ✅ Acceptance and interruption proof | NFR-PERF-002 | Restored bytes identical at concurrency 1, 2 and 4 ✅; *N* in-flight blobs each independently intent-covered ✅; a kill with several uploads outstanding ✅ |

**G2, as built.** Throughput roughly doubled on fixed-v1 and rose about a third
on cdc-v1 ([phase-2-benchmarks.md](phase-2-benchmarks.md), fourth run: ten sweeps
a side in both run orders, with before and after ranges that do not overlap on
any row). Two things carried it, and only one was expected. Compression left the
serial path, as ADR-0029 §1 intended — and so did the per-segment SHA-256, which
is the "second hash" §6 step 2 named and F1 never reached. The pipeline had been
running two hash passes over every byte on one thread when only the whole-file
hash has to be there.

The barrier is proven rather than asserted: every blob a publication produces is
checked for dense ordinals and strictly increasing offsets at concurrency 1, 2
and 4, and that assertion was verified to fail against a deliberately broken
barrier before being kept.

Two consequences worth naming. **cdc-v1 gains far less**, because
`CdcSegmentReader`'s rolling window chains across boundaries and so the reader
cannot be parallelised at all — under cdc-v1 more of the remaining work sits on
the one thread that cannot move. And **`Concurrency = 1` is no longer strictly
serial**: the channel holds `Concurrency + 1`, so one segment is prepared while
the previous is appended. Everything the barrier protects still holds, but
NFR-PERF-013's CPU cap should now be measured rather than inferred from the
setting's name.

Two concurrency defects were fixed on the way in, both predating this work: the
sealed-blob list was read without the lock its writers hold, which had been a
live race since upload left the archive loop; and the catalogue lookup behind
segment reuse runs on a shared SQLite connection that the concurrent stage would
otherwise have called from several threads.

### Wave H — Close

| # | Item | Acceptance |
|---|------|-----------|
| H1 | ✅ ADR-0028 and ADR-0029 accepted, with amendments recording what implementation and measurement decided | [Q20](open-questions.md#closed) closed with a number, not an opinion |
| H2 | ✅ Traceability, roadmap, and 11 §1 updated | No requirement points at a test that does not exist; the roadmap says what the remote binding still owes |

---

## Exit criteria

From [the roadmap](roadmap.md#phase-2--peer-to-peer-backup-and-the-service-boundary),
restated here with the test that will prove each:

| Criterion | Proven by |
|---|---|
| A second process cannot take the writer role — it refuses naming the holder | `Hosts.Tests/ProcessRaceTests` — a real spawned CLI process refused across the kernel's file lock, and a killed holder's role freeing itself; `Application.Tests/StateDirectoryLockTests` holds the in-process API surface |
| A default install listens on no port | `Api.Tests` — the remote-binding assertion |
| A running job reports states beyond `Scanning` | `Api.Tests` — the progress-event assertion |
| A service with no front end installed backs up unattended | `Hosts.Tests` |
| Client and service at incompatible versions refuse with both versions named | `Api.Tests` — the negotiation assertion |
| Restored bytes identical regardless of concurrency setting | `InterruptionTests/ConcurrentUploadTests` at 1, 2 and 4 — the whole setting now, not the upload workers alone, because ADR-0029 §1's staged pipeline carries it into segmentation, hashing and compression |
| Recovery still works with no service and no state directory | `Hosts.Tests` — the recovery drill, unchanged |
| An unpaired remote client is refused, and a substituted identity refused rather than prompted | `Hosts.Tests/RemoteBindingTests` — an unpaired console dialling a real socket is refused `not_paired` while the local binding still answers; `Protocol.Tests/PeerWireTests` proves `identity_changed` over the wire and the man-in-the-middle relay caught by channel binding |
| A restore commanded remotely writes on the service's machine, no plaintext crossing | `Hosts.Tests/RemoteBindingTests` — a paired `RemoteServiceClient` commands a restore; the files land under the service-side path and the console receives counts, outcome and a path string, never content |
| *(peer criterion)* No relay required on a LAN | **Unproven either way** — peers dial declared endpoints; no relay exists and none is needed on a routable LAN, but nothing exercises discovery or NAT traversal. Deferred with LAN discovery. |
| *(peer criterion)* Multi-day disconnection and resumption | **Proven at pass scale, not soak scale** — an offline destination is recorded, retried under back-off, and converges with no command when it returns (`Repository.Tests/EndToEnd/AgentPassTests`, `Hosts.Tests/PeerReplicationTests`); the resumption mechanics carry any gap length because each object commits whole. The literal multi-day soak has not been run. |

### What is not met, said plainly

- **NFR-PERF-007's ≥400 MB/s on the reference machine.** Every number on the
  benchmarks page is container measurement. It compares configurations and
  versions against each other honestly and says nothing about the requirement,
  which has never been tested on the machine it is stated against.

---

## Where to pick up

The next round starts here. Everything below is in priority order, and each item
says what "done" looks like so it does not have to be re-derived.

**The lead item is the direct-to-destination tail
([ADR-0046](adr/0046-direct-to-destination-publication.md)).** The write path,
the read paths, the migration, retirement, and the kill sweep are built and
held by tests; what stands between the `direct_ship` flag and being the
default is the peer write adapter (a declared peer is a stated `NotSupported`
in the ledger today), a retention-with-trimming drill over aged direct-ship
snapshots, and the flip itself. Done looks like: a set shipping to a peer
destination through the sink, a trim proven destination-side, and new sets
defaulting to direct-ship with staging the explicit opt-in.

F1 is done and off this list, and it has now been measured — what it decided is
recorded under Wave F above, what it bought in
[phase-2-benchmarks.md](phase-2-benchmarks.md).

**What the measurement said.** Ten sweeps per side against `786324f`, five in
each run order so a warm cache could not read as an improvement, with the
harness confirmed byte-identical between them. **All ten configurations improved
in both orders** — twenty comparisons, twenty the same direction — by 4% to 16%,
largest at high concurrency and with compression off, which is the shape the
change predicts. Individual percentages sit inside the run-to-run spread and
should not be quoted alone; the unanimity is the result. The quieter finding may
be the better one: seven of ten configurations became markedly *more
predictable*, some from a 40% spread to under 10%, which is what removing an
`fsync` from a hot loop does to a measurement that was inheriting the disk's
variance.

**So pinning survives measurement**, which is half of what
[Q20](open-questions.md#closed) asks — resumability now costs one `fsync` and one
sidecar write per blob instead of one of each per record, and the question of
whether it was worth its price does not arise at that price. G2 answered the
other half: 360.8 MiB/s at concurrency 2 against 356.9 at 4, so the default of 2
is measured rather than merely reasoned. **Q20 is closed on both halves.**

**Still owed: the reference machine.** Everything on the benchmarks page is
container measurement. It compares configurations and versions against each
other honestly and says nothing at all about NFR-PERF-007's ≥400 MB/s, which has
never been tested. That is not a small remainder, and it is H1's to close.

G2 is done too, and what it decided is under Wave G above. So are G3, D1, D2 and
H1–H2; what each decided is recorded with its wave.

**G3 found a deadlock rather than only closing a gap.** Standing several blobs in
row 4's state needed a store that holds blob puts open, and reaching for it
showed that an upload worker which threw left the channel uncompleted — so once
every worker had died and the bounded channel filled, the producer blocked in
`WriteAsync` with nothing alive to drain it and the job neither finished nor
failed. The files under test had never sealed more blobs than the channel holds,
which is the only reason it had not been seen.

**D2 landed in two steps.** The first routed the one verb that both writes and
had a service equivalent: `backup`. The second made restore, verify and check
service equivalents too — they had been answered "read path, run it directly",
which was honest but meant a console could ask the service to make a backup and
not to check one. They run on the queue's reader lane, which ADR-0029 §4
described and nothing had used, so a restore runs alongside a scheduled backup
rather than behind it.

`archive`, `rebuild-index` and `verify --file` have no service equivalent at all
and say so by name. A service can only run a configured set, so an ad-hoc backup
root is refused with what to do instead rather than quietly run against state the
service owns.

### 1. The remote binding — built

Topologies 3 and 4, and the two exit criteria they own, are now met over a real
socket. Both criteria above cite the tests that prove them.

Pairing reuses
[architecture 09 §3](architecture/09-replication-and-peers.md#3-pairing)'s
machinery, settled by [ADR-0030](adr/0030-peer-identity-and-pairing.md) and
specified in
[`specifications/peer-protocol/`](../specifications/peer-protocol/README.md)
documents 01 and 02 — a peer keypair unrelated to the repository, a short
authentication string both humans confirm, and a pinned identity whose change is
a hard failure. `FallbackPlan.Protocol` implements both documents in full, and
now carries them over the wire: `PeerTlsConnection` opens TLS 1.3 over TCP with
the per-connection ephemeral certificate, `PeerSessionDriver` drives the
four-state machine over the live stream, `PeerKeypairStore` persists the device
key, and `PairingCeremony` runs the ceremony with a human's approval on each
side. `RemoteServiceListener` (Agent) binds the interface an administrator names
and admits only a pinned peer; `RemoteServiceClient` (Cli) is the console's end,
and the shipped CLI drives it — `fallbackplan <verb> --connect <host:port>
--fingerprint <fp> --state <dir>` routes `backup`, `verify`, `check`, `restore`,
`snapshots`, `ls` and `status` to a remote paired service, the service named by
fingerprint because a grant holds a key and never an address. The
man-in-the-middle relay that channel binding defeats is reproduced through
two real TLS connections, and the ceremony is performed by two real
operating-system processes
([implementation status](implementation-status.md#0030--the-socket-exists)).

What remains blocked is narrower still.
[Q18](open-questions.md#q18--streaming-restored-content-to-a-remote-client) and
[Q19](open-questions.md#q19--console-identity-and-multi-operator-access) gate
what a paired console may *do* — whether restored content may stream to it, and
whether its actions are attributable to a person. Neither gates who it is, so
the identity and session layers can be built while they stay open.

Of the peer-protocol documents this once owed — 03 (replication), 04
(verification) and 05 (quotas) — all are since written and implemented,
with 06 (retention instructions) landed beside them. 04 was the last:
its keyed random-range challenge now rides every sync, with the tampered-byte
cases proven against a peer over the wire and a local-path replica by
read-back ([spec 04](../specifications/peer-protocol/04-verification.md)).

### 2. The proof debt the pipeline integrity review left on the table

The [pipeline integrity review](review/2026-08-pipeline-integrity-review.md)
read the built pipeline back against the documents and fixed everything it
graded — the durability of the writer sequence, the refusal guards on reused
identifiers, session disposal, spool hygiene, the restore assembly check —
each proven by a test that failed first. What it deliberately did not invent
is the test infrastructure below. Each item says what "done" looks like.

The [restore pipeline review](review/2026-08-restore-pipeline-review.md)
closed a second round on the read path — the CLI's uncontained restore,
symlink write-through, the dropped outcome and shared run store, unreachable
cancellation — and repaired the coverage audit itself (`--drift` recognised
only xUnit attributes and had been blind since the MSTest move; `--audit` now
reports rotted citations). What both reviews deliberately left is below.

1. ✅ **Cancel a live publication** — done
   ([pipeline review Amendment 1](review/2026-08-pipeline-integrity-review.md#amendment-1-2026-08--the-base-hardening-round)):
   all five of
   [T-2](review/2026-08-prior-art-learnings.md#t-2--graceful-stop-is-a-different-code-path-from-a-crash-and-it-is-the-one-that-corrupts)'s
   named tests exist and pass — four in `InterruptionTests/CancellationTests`,
   the `ServiceCommandHandler` positive path in `Hosts.Tests/ServiceTests`.
   The suite caught and fixed the exact defect class T-2 predicted: the
   cancelled seal's unwind threw over the cancellation, so a cancelled job
   reported failure instead of `Cancelled`. The upload drain after a cancel
   is pinned as documented behaviour; the rerun's obligation discharge
   (ADR-0029 §4, in the same process) is held by `SequenceAccountingTests`.
2. ✅ **A store that tears and forgets** — built and run
   ([review Amendment 1](review/2026-08-restore-pipeline-review.md#amendment-1-2026-08--the-fault-injection-round)):
   `TestSupport/FaultInjectionStores` delivers torn puts, vanishing
   acknowledged objects, and read faults; `InterruptionTests/StoreFaultTests`
   runs the five-point kill matrix over them and the 04 §5.1 claims held.
   The decision it surfaced (SF-1) is **resolved**: `LoadBlobsAsync`
   tolerates an unopenable blob and reports it in `SkippedBlobs`, so one
   torn orphan no longer blocks every plain-path restore — the reader
   converged on the recovery tool's posture, and the torn-write assertion
   flipped to a restore as promised
   ([review Amendment 1](review/2026-08-restore-pipeline-review.md#amendment-1-2026-08--the-fault-injection-round)).
3. ✅ **Kills inside steps, and the four boundaries the matrix omits** — done:
   both `KillPoints()` sources run all nine boundaries, 04 §5.1 was
   reconciled to match (it had six rows for nine steps), and the in-step
   kills are the put-budget sweep — `InterruptionTests/StorePutSweepTests`
   single-stream, its twin in `TreeSnapshotInterruptionTests` for the tree
   path's first-ever meeting with a fault store, which caught the advisory
   hint put failing the whole publication (fixed test-first).
4. ✅ **An interrupted restore** — both slices done: cooperative cancellation
   (RR-5) and the read-fault slice
   (`Repository.Tests/RestoreReadFaultTests`), which also fixed SF-2 —
   a store fault mid-restore now fails the item in the receipt instead of
   aborting the run with no receipt at all. Still owed at the *process* level
   under item 6 (a real kill, not an exception).
5. ✅ **The stale-local-state family** — done
   ([pipeline review Amendment 2](review/2026-08-pipeline-integrity-review.md#amendment-2-2026-08--the-stale-state-round)):
   each member has its named test or its written reason. Ahead-of-the-store
   was the real defect — own-writer reuse trusted the location row with no
   store I/O and could publish dangling references; the trust gate now runs
   a memoized existence probe, and `PlanRestore` probes located blobs
   instead of asking the catalogue about itself. Behind-the-store is pinned
   as cost-only, the unchanged short-circuit as contained (confirming it
   would undo NFR-PERF-003), and the dead `segment_dedup` table as inert —
   all in `Repository.Tests/StaleCatalogueTests`.
6. ✅ **Two real processes** — done: `Hosts.Tests/ProcessRaceTests` spawns
   the shipped apphosts and crosses the kernel's file lock — a real CLI
   contender refused naming the holder's role and pid, a killed Agent
   holder's role freeing itself with no stale-lock heuristic, and two
   processes with different state directories committing to one repository
   concurrently (ADR-0028 §4's boundary, both sides). The spool directory
   rides the same exclusion — `<state>/spool` sits behind the writer role
   at every call site.
7. **Contract-suite atomicity** (phase 3, with the second provider) — the
   portable `Storage.ContractTests` do not require atomic visibility or
   crash durability, so a second provider could pass while offering neither.
   A design decision about what the contract *requires*, then tests.
8. **Sample read-back after upload** (with the first remote provider) —
   architecture 04 §5's optional step-5 sampling, worth building when
   acknowledgements are less honest than a local fsync.
9. **Restore-plan completeness** — the plan omits free space, required
   privileges, physical-vs-logical size, and archival-tier rehydration
   (FR-RST-003), and is neither exportable nor resumable (arch 08 §2). Needs
   filesystem fault injection on the restore target to test. Done when the
   plan reports each and a plan survives a round trip.
10. **Alternate data streams on restore** (RR-6) — the *honesty* half is
    ✅ done: catalogue schema v5 carries `has_alternate_streams` through the
    live projection and both rebuilders, `RestorePlanner` declares the
    `alternate-streams` degradation, and the receipt (schema v3) reports the
    item `degraded` with the run `Partial`
    ([restore review, RR-6 resolution note](review/2026-08-restore-pipeline-review.md#rr-6--alternate-data-streams-are-captured-and-never-restored)).
    The *write-back* half stays owed: actually restoring the streams,
    Windows-only — done when a file carrying ADS round-trips on a target
    that can take them, at which point `SupportsAlternateStreams` stops
    being unconditionally false.
11. ✅ **Restore breadth** — done (`Repository.Tests/RestoreBreadthTests`):
    `Replace` and `Fail` pinned, the receipt JSON pinned byte-for-byte by a
    golden fixture (schema v3), and NFR-PERF-009 measured honestly — which
    produced item 13 below rather than a pass.
12. **Sparse restore** — [Q22](open-questions.md#q22--sparse-restore-materialises-zeroes):
    a maintainer decision between implementing sparse write-out and amending
    FR-ARCH-013. Blocks nothing; the disagreement is recorded, not silent.
13. **The restore GET budget** (NFR-PERF-009) — architecturally unmet: the
    read path opens every blob in the repository at load (three range reads
    each, proportional to repository size) and issues one uncoalesced range
    read per manifest and per segment.
    `RestoreBreadthTests.Restore_GetRequests_AreCharacterisedAgainstTheDistinctBlobBudget`
    pins the exact current counts so the shortfall cannot be mistaken for
    met. Done when the reader learns catalogue-directed blob loading and
    range coalescing and the characterization becomes the compliance test —
    real read-path engine work, sensibly co-scheduled with the first remote
    provider, where a GET has a price.

### 3. NFR-PERF-007 on the reference machine

The one exit criterion no amount of container measurement can close. Everything
on [phase-2-benchmarks.md](phase-2-benchmarks.md) compares configurations and
versions against each other; the ≥400 MB/s figure is stated against a machine
none of it ran on.

### Watch list

The intermittent `Hosts.Tests` failure recurred during F1, was named
(`ServiceTests.A_backup_commanded_by_a_client_runs_and_reports_states_beyond_scanning`),
diagnosed, and fixed. It was never about F1, and it was not really about the
test.

**The mechanism.** `ProgressHub.WatchAsync` was an `async IAsyncEnumerable`
iterator that registered its subscriber channel *inside* the iterator body. A C#
iterator runs none of its body until the first `MoveNextAsync`, so a caller
holding the enumerable was not yet a subscriber, and `Report` fans out only to
registered subscribers with no replay. The test started its watcher with
`Task.Run` and commanded the backup on the next line; with the pool saturated by
twelve parallel projects, the backup could emit `Scanning` before the watcher
subscribed, and those events went to nobody.

**Why it was a product defect and not a test defect.** Every caller had that
window, unobservably. A UI attaching to a running job would silently miss
events and have no way to know. So `WatchAsync` now subscribes in a non-iterator
method and returns a private iterator for the streaming, which makes the
subscription exist from the call. `LocalServiceClient.WatchAsync` had the same
shape — it opened its connection inside the iterator — and got the same
treatment.

**Proven by four tests that fail against the old code**, each by hanging until
its timeout: a watcher receives what was reported before it enumerated; it
receives nothing reported before it asked to watch (there is deliberately no
replay); `Complete` ends a watcher that never enumerated; and every watcher sees
one event under one sequence number. They are deterministic and thread-free, so
unlike the test they replace they cannot go quiet under load. Twelve concurrent
`Hosts.Tests` runs were green afterwards.

Two things went with it. `ProgressHub.Latest` was written on every report and
read nowhere, grew one entry per job for ever, and promised in its own doc to
serve "a client that arrives late" — it is gone, and replay can be designed when
a front end needs it. And the sequence number was allocated outside the lock that
delivered it, so two concurrent reports could be delivered in the opposite order
to their numbers; allocation now happens under the same lock, which makes the
monotonicity `JobProgressEvent` documents actually true.

**That entry was written as "closed" and it was not.** Under heavier load — four
full suites in parallel rather than four copies of one project — the same test
failed again, and the cause was a *second* race in it that the first diagnosis
missed. Subscribing eagerly guarantees no event is dropped; it does not
guarantee any event has been read yet. The test waited for the job to reach
`Complete` and then read what the watching task had collected, which under load
it had not finished collecting. It now waits for the watcher to see `Complete`,
which by FIFO means it has seen everything before it. Twelve four-way-parallel
full-suite runs are clean where three of four failed.

The lesson is the one the watch list exists for: a plausible mechanism that
explains a flake is not proof it is the only one. The eager-subscription fix was
right and necessary, and stopping there was the error.

#### Open

`Repository.Tests/EndToEnd/AgentPassTests.A_missing_root_is_a_recoverable_failure_and_a_bad_schedule_is_permanent`
fails roughly one run in twelve under four-way parallel full-suite load, on
`Assert.Contains(jobs.Jobs, … State == JobState.FailedPermanent)` — the pass
reports two failures as expected, but the permanent one is not yet visible in the
job-state store when the assertion reads it.

Established as **not** caused by G2: the same load on the commit before the
staged pipeline fails it at the same rate. It archives nothing — a missing root
and an unparseable schedule — so it does not touch the pipeline at all. Recorded
rather than chased, with the shape of the suspicion written down this time: it
looks like the same class of defect as the progress flake, an assertion reading
state that a writer has not finished publishing, rather than anything about
scheduling.

---

**Previous:** [Phase 1 execution plan](phase-1-execution-plan.md) · **Decisions:** [ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md) · [ADR-0029](adr/0029-pipeline-and-service-concurrency.md)

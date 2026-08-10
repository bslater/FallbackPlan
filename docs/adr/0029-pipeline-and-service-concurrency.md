# ADR-0029 — Pipeline and service concurrency: the ordering barrier, the bound, and the order of work

**Status:** Accepted
**Date:** 2026-08
**Requirements:** NFR-PERF-001, NFR-PERF-002, NFR-PERF-007, NFR-PERF-013, NFR-OPS-001, NFR-OPS-004, NFR-SEC-003, NFR-PORT-004
**Related:** [ADR-0005](0005-aead-suite-and-nonce-construction.md), [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [architecture 04 §5](../architecture/04-concurrency-and-publication.md#5-publication-order), [specification 05 §6](../../specifications/repository-format/05-blob.md), [phase-0 benchmarks](../phase-0-benchmarks.md)

---

## Context

Three requirements already assume a concurrent pipeline. **NFR-PERF-002**: "the
pipeline shall parallelise read, hash, compress, encrypt, and write while
preserving deterministic segment order", accepted when "restored bytes [are]
identical regardless of concurrency setting". **NFR-PERF-001** bounds memory by
"configured concurrency". **NFR-PORT-004** requires "bounded concurrency".
**NFR-OPS-001** requires *queue depth* to be observable.

None of it exists. Capture is one sequential `await foreach` over scan events;
there is no concurrency setting anywhere in `src/`; there is no queue to have a
depth. `CapturePolicy` has seven knobs and none of them is this one. The whole
tree contains no `Task.Run`, no `Task.WhenAll`, no `Parallel.*`, no `Channel<T>`,
no `SemaphoreSlim` — the only lock is `WriterSequence`'s.

Measured throughput is **≈51 MiB/s** (`fixed-v1`) and **≈37 MiB/s** (`cdc-v1`)
single-streamed on a 4-core container, against NFR-PERF-007's ≥400 MB/s on the
reference machine. That is roughly an order of magnitude, and the temptation is
to close it with threads.

The measurements say otherwise. Raw `fixed-v1` segmentation runs at **6.4 GiB/s**
— two orders of magnitude above the whole pipeline — so segmentation is not the
constraint, and neither is any other purely CPU-bound stage at these rates.
Three serial costs are visible in the code before any thread is added:

1. `FileArchiver` always constructs its blob writers **pinned**, so every data
   record costs a synchronous `fsync` *and* a full checkpoint sidecar rewrite
   (`File.WriteAllBytes` then `File.Move`) — about 128 of each per 128 MiB blob,
   both blocking calls inside `async` methods. `ManifestBuilder` passes no
   pinning and pays neither, which is why metadata is cheap and data is not.
2. Blob upload is `await`ed **inline in the archive loop**, so the entire
   pipeline stalls for the duration of every blob PUT — the largest structural
   stall in the design, and worst precisely where it matters most, on a slow or
   remote destination.
3. Each segment is hashed **twice** (segment identity, then the whole-file
   digest), and `RecordCipher.Seal` constructs a **new `AesGcm` per record**,
   paying a key schedule per record.

Parallelising an fsync-bound pipeline mostly multiplies fsyncs. So this ADR
settles the *shape* of concurrency — because the shape has correctness
consequences that must be decided before anyone writes the code — while
committing to fix the serial costs first.

There is also a second, independent axis that the service of ADR-0028 introduces:
how many *jobs* run at once, and what happens when a user asks for a restore
while a scheduled backup is running. That question did not exist when the CLI ran
one command and exited.

## Decision

### 1. The ordering barrier sits at ordinal assignment, and everything upstream may be concurrent

The load-bearing constraint is narrower than "the pipeline is ordered". What
correctness actually requires:

- **`(blob_key, ordinal)` must never cover two byte strings.** The ordinal is
  the AEAD nonce and is bound into the AAD (ADR-0005; specification 05 §6.1 calls
  this rule load-bearing and its violation "a catastrophic cryptographic failure
  under ordinary operating conditions"). `BlobWriter.AppendRecordAsync` derives
  the ordinal from `_entries.Count` and is **not re-entrant**: two concurrent
  callers race to the same ordinal, which is exactly the catastrophe.
- **Ordinals must be dense, ascending and gapless, with strictly increasing
  physical offsets**, because `BlobWriter.TryResume` walks the spool and rejects
  any gap, and specification 05 §3.1 requires the footer's record table to be in
  ascending ordinal order with increasing offsets.
- **A resumed blob must re-emit its sealed bytes verbatim** (NFR-SEC-003:
  "resume produces byte-identical blobs").

What correctness does **not** require, and which is therefore free:

- **Segment processing order within a file.** `SegmentReference` carries an
  explicit `(LogicalOffset, LogicalLength, ObjectId)`, and the manifest is
  validated on the *sortedness of the encoded list*, not on the order the
  segments were produced.
- Which blob a record lands in, where blob boundaries fall, and what salt a run
  draws — restart is explicitly permitted to differ.
- Which duplicate segment wins the within-session dedup race (identifiers are
  content-derived, so every winner yields identical bytes).
- The order of `walker.Files`, the `IndexEntry` list, and catalogue projection.
- The relative order of work across **sibling directories**, provided each
  directory's own frame closes with byte-sorted entries and before its parent's.

So the pipeline is staged, with one barrier:

```text
   read ──┐
   hash ──┼── CONCURRENT, bounded by the concurrency setting
compress ─┘
          │
    ╔═════▼══════════════════════════════════════════╗
    ║  ORDERED, single-threaded per blob writer:      ║
    ║  assign ordinal → encrypt → append → digest     ║
    ╚════════════════════════════════════════════════╝
          │
        seal ── upload  (CONCURRENT with the above, see §2)
```

Encryption sits **below** the barrier, not above it, because it consumes the
ordinal it is nonced with. A design that encrypted concurrently would have to
reserve ordinals first and then reorder — buying little, since compression is
the expensive part, and risking the one mistake that is unrecoverable.

The scanner stays a single depth-first walk: its `Stack<Frame>` machine assumes
DFS order, and its byte-sorted sibling order is what the tree encoding requires.
Its per-entry metadata IO (stat, xattrs, hole probing, ADS enumeration) may
overlap the archiving of previously scanned files — the readahead is the
opportunity, not reordering the walk.

### 2. Upload leaves the archive loop

A sealed blob is handed to a bounded set of upload workers and the archive loop
continues. This is separable from §1 and is the larger win on any destination
slower than local NVMe.

The publication invariants are unchanged and must be enforced per blob rather
than per job: the covering **write intent must be durable before that blob's
PUT** (architecture 04 §4.2), and step 6's index deltas may not be published
until every blob they name is durable. With *N* uploads in flight the job's
completion barrier is "all uploads acknowledged", and each in-flight blob is
independently in the "durable but unreferenced" state the interruption matrix
already describes — a state row 4 of architecture 04 §5.1 covers, now reachable
*N* at a time.

### 3. One concurrency setting, defaulting to modest

A single **`Concurrency`** value joins `CapturePolicy`, bounding the concurrent
stage of §1 and the upload workers of §2 together, because NFR-PERF-001 bounds
memory by "configured concurrency" and two independent knobs would make that
bound unstatable. Memory is then `concurrency × segment size × a small constant`,
plus the blobs in flight — the property the memory-bound proof already measures.

The default must satisfy **NFR-OPS-004** ("defaults meet NFR-PERF-013 on a
4-core laptop") and **NFR-PERF-013** (a 25% CPU cap holds measured agent CPU
≤30% over any 60 s window). A backup that makes the machine unpleasant to use
gets switched off, and a switched-off backup protects nothing — so the default
is deliberately below the machine's capacity, and saturating hardware is an
opt-in for someone who has decided this machine is a backup machine.

`Concurrency = 1` must remain a valid, tested setting: it is the configuration
in which the ordering barrier is trivially satisfied, and it is the control case
for NFR-PERF-002's acceptance test.

### 4. Service-level concurrency: one writer, several jobs, explicit priority

ADR-0028 gives the service the sole writer role, which makes this a scheduling
question inside one process rather than a locking question across several.

- **Backup sets run one at a time by default.** They contend for the same disk
  and the same writer sequence, and two sets at once mostly makes both slower
  while doubling the memory bound.
- **Restore and verification are separately queued and may run alongside a
  backup.** A user waiting on a restore must not wait for a scheduled backup to
  finish; a restore is a read path and does not take the writer role.
- **A user-initiated operation outranks a scheduled one.** Where they contend,
  background work yields — the concrete meaning of NFR-PERF-013's "background
  activity shall observe configured limits".
- **Cancellation is a first-class command** (ADR-0028 §7), and it must land in
  the job journal. `JobState.Cancelled` exists in the vocabulary and is written
  nowhere in `src/`; today a cancelled job stays `Publishing` forever. A
  cancelled job records `Cancelled`, and the sequence numbers it allocated
  become void-delta obligations discharged by the next publication, exactly as a
  crash's would.

### 5. Progress is emitted, not inferred

The client contract needs per-job progress that nothing currently produces.
`IPublicationObserver` is nine payload-free callbacks serving the interruption
harness — a ten-hour tree backup fires nine of them — and `EngineDiagnostics` is
job-anonymous by enforced policy (NFR-PRIV-002), so neither can be bent into a
progress feed.

The engine therefore emits **progress events carrying job identity, the
architecture 10 §3 state, and counts** (files and bytes seen, done, reused,
failed). Two constraints:

- Progress events are a **client-facing channel, not telemetry**. They may carry
  job identity because they travel to an authenticated local or paired client;
  the OTel instruments keep their four-attribute allowlist untouched. Conflating
  the two is how a path or a filename would end up in a metrics backend.
- The **10 §3 states become real**. Eight of fourteen are currently written
  nowhere; a pipeline that reports `Scanning` and then nothing for ten hours is
  the reason the state machine was specified in the first place.

### 6. Order of work: measure, remove serial cost, then parallelise

Stated as a decision because the sequence is the point:

1. **Measure first** — establish where the ≈51 MiB/s actually goes, on the
   reference machine rather than a container, with the pinned-mode fsync,
   checkpoint rewrite, inline upload, double hash and per-record `AesGcm` each
   attributed.
2. **Remove serial cost** — pinning is a *policy* (resumability of data blobs),
   not a physical necessity, and its cost per record was never measured against
   its benefit; upload can leave the loop (§2) without any of §1's machinery;
   the second hash and the per-record cipher construction are ordinary
   optimisations.
3. **Then parallelise** under §1 and §3, against a bottleneck that has been
   measured rather than assumed.

`ThroughputBenchmarks` — named in the traceability matrix for NFR-PERF-007 and
**never written** — is step 1's deliverable, and must report throughput at
several concurrency settings so NFR-PERF-002's acceptance is a measurement
rather than an assertion.

## Consequences

**Positive.** The one mistake that cannot be recovered from — two records under
one `(blob_key, ordinal)` — is placed behind a barrier stated in the design
rather than discovered in review. The correctness boundary is written down
precisely, so a future contributor can see that reordering segments *within a
file* is free while reordering *records into a blob* is catastrophic. Upload
leaving the archive loop helps most on the destinations that need it most.
Bounding concurrency, memory and CPU with one setting keeps NFR-PERF-001
statable. And the sequencing means the first performance work is aimed at
measured cost.

**Negative.** A staged pipeline with a reorder barrier is materially more complex
than an `await foreach`, and it makes the interruption matrix larger: *N*
in-flight blobs multiply the "durable but unreferenced" states, and multiple
simultaneously-open spools break `BlobWriter.TryResume`'s assumption that a spool
directory holds at most one checkpoint — a method with **no production caller
today**, so resumption must be wired up and re-proved as part of this, not
assumed. Progress events are a new client-facing surface with its own privacy
obligations, adjacent to but deliberately not part of the telemetry allowlist.
Deferring parallelism behind measurement means the throughput gap closes later
than it otherwise might.

**Neutral.** The repository format is untouched: this changes how bytes are
produced, never what they are. `Concurrency = 1` reproduces today's behaviour.
Repository-level multi-writer semantics are unaffected.

## Alternatives considered

**Parallelise per file rather than within one.** Archive several files
concurrently into one blob writer. Rejected: it puts concurrent callers straight
onto the non-re-entrant `AppendRecordAsync`, which is the catastrophic race, and
solves nothing that §1 does not solve more safely.

**A blob writer per worker.** Gives each worker its own ordinal space and salt,
which is legal — `N` concurrent writers must produce pairwise-distinct salts, and
NFR-SEC-003 already anticipates exactly that. Not chosen *now* because it
multiplies open spools, in-flight intents and partially filled blobs (hurting
blob fill, which NFR-PERF-008 cares about), and because §6 says the current
bottleneck is not the ordered stage. It remains the natural next step if
measurement shows the barrier is the constraint, and §1's shape does not preclude
it.

**Reserve ordinals, encrypt concurrently, append in order.** Legal, and it moves
encryption above the barrier. Rejected for now: encryption is not where the time
goes at these rates, and it puts the nonce-assignment invariant into the hands of
a reorder buffer for a gain that has not been measured.

**Add threads first, measure later.** The fastest-looking path. Rejected: the
pipeline is currently doing ~128 fsyncs and ~128 sidecar rewrites per blob, and
concurrency multiplies that work rather than removing it. Optimising the wrong
layer first also makes the real cost harder to see afterwards.

**Leave the pipeline sequential and declare NFR-PERF-002 aspirational.**
Rejected: the requirement has an acceptance criterion and a phase, and a
requirement nobody intends to meet should be deleted rather than left to imply a
guarantee that is not tracked.

## Implementation status (2026-08)

**All of it is built.**

§3's `Concurrency` setting, validated on `CapturePolicy` with `1` a tested value.
§4's service-level scheduling — sets serialised, read work in its own lane,
user-initiated work ahead of scheduled, and cancellation recording
`JobState.Cancelled` — with §4's full acceptance now held by tests: the five
T-2 cancellation tests (`InterruptionTests/CancellationTests`,
`Hosts.Tests/ServiceTests`), and "discharged by the next publication, exactly
as a crash's would" held **in the same process** by
`InterruptionTests/SequenceAccountingTests` after the base-hardening round
made `WriterSequence` read its live pending set. §5's progress events, with
the states beyond `Scanning` actually emitted. §6's order of work, all three
steps, with results in [`phase-2-benchmarks.md`](../phase-2-benchmarks.md).

§2's upload workers: a sealed blob is handed to a bounded worker set and the
archive loop continues, with the covering intent made durable per blob before
its own PUT.

§1's staged pipeline: content id, prior-version reuse, object id, the dedup claim
and compression run concurrently; assign ordinal → encrypt → append → digest stay
strictly ordered on one thread. Ordering is by construction rather than
reconstruction — segments enter a bounded channel in reader order and the barrier
takes them out in it — so the reorder buffer this ADR anticipated is the channel
itself. The reader stays single-threaded, as §1 requires and `CdcSegmentReader`'s
rolling window makes mandatory.

Three things the implementation decided that the decision did not.

**The second hash was the bigger serial cost, not the cipher.** §6 step 2 named
the per-record `AesGcm` construction and "the second hash" together. The cipher
measured below the noise, which was reported as a result. The second hash — a
per-segment SHA-256 running on the same thread as the whole-file SHA-256 — moved
above the barrier with the rest of the concurrent stage, and the combined effect
more than doubled throughput.

**`Concurrency = 1` is no longer strictly serial**, and this is worth being
explicit about. The channel holds `Concurrency + 1` items, so at 1 one segment is
hashed and compressed while the previous one is appended. The barrier is still a
single thread, so everything §1 protects still holds, and this mirrors what §2's
upload channel already does for the same reason. But a reader who takes "1" to
mean "one thing at a time" would be wrong, and NFR-PERF-013's CPU cap should be
measured against that rather than assumed from the number.

**Alternative "reserve ordinals, encrypt concurrently" stays rejected**, and the
measurement supports it: with compression moved off the ordered stage, the
barrier is not the constraint at these rates.

**Q20 is [closed](../open-questions.md#closed)**, and by measurement rather than
opinion on both halves. **`Concurrency` stays at 2**: it was chosen below a
4-core laptop's capacity because a backup that makes the machine unpleasant to
use gets switched off, and a switched-off backup protects nothing — and 360.8
MiB/s at 2 against 356.9 at 4 says the reasoning cost nothing, because with a
single-threaded reader and a single-threaded barrier there is little left to give
the concurrent stage anyway. **Pinning survives** too, at one `fsync` and one
sidecar write per blob rather than per record; at that price whether it earns its
cost is no longer a question worth asking.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written alongside ADR-0028, after benchmarks showed the gap is serial cost rather than thread count |
| 2026-08 | Accepted | The shape is settled; §6's sequence is under way and its status is recorded above |
| 2026-08 | Accepted | §6 steps 1 and 2 measured; Q20 closed on both halves, with the concurrency default and pinning's cost each settled by a number |

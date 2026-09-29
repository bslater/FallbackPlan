# ADR-0075 — A restore reads around damage from the set's other copies

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-RST-007, FR-RST-002, FR-RST-003, FR-RST-004, FR-RST-005, FR-VER-005, FR-VER-007
**Related:** [ADR-0034](0034-hub-and-spoke-destinations.md) §6 (amended here), [ADR-0035](0035-destination-fitness.md) Amendments 1 and 2, [ADR-0041](0041-guided-restore-and-peer-retrieval.md), [ADR-0046](0046-direct-to-destination-publication.md), [ADR-0054](0054-scheduled-restore-drills.md), [ADR-0068](0068-the-catalogue-directed-restore-read.md), [architecture 08 §3.2](../architecture/08-restore-and-recovery.md#32-reading-around-damage), [command-contract](../../specifications/command-contract/README.md)

**Built:**
- The reader: `Repository/RepositoryReader` (`UseOtherCopies`, `ReadAround`,
  `Refusals`) and `Repository/CopySource` (a copy, why a copy did not serve,
  and a record read around).
- The copies, in `Agent`: `SetCopies`, from which `ReplicaRepairer` now takes
  its sources too.
- The restore: `Restore/RestoreExecutor` (receipt schema 5's `read_from` and
  `read_around`), `Restore/RestoreBlobSet` and `Restore/FirstHolderStore`
  (the plan).
- The service: `Agent/ServiceCommandHandler` (the plan probe, the run, and
  what a run found), and `Agent/RestoreSourceRegistry` (which handle is the
  set's own archive).
- The surfaces: contract 1.45 (`Api/Results.cs`, `Api/ContractVersion.cs`),
  `Cli/OperationGateway` and `Web/wwwroot/app.js`.
- The tests: `Repository.Tests/ReadAroundTests`,
  `Repository.Tests/RestoreBreadthTests`, `Hosts.Tests/RestoreReadAroundTests`,
  `Api.Tests/ConfigurationContractTests`, `Cli.Tests/GatewayRestoreReportTests`,
  `Web.Tests/ConsoleRestoreResultScriptTests` and
  `Web.DomTests/RestoreWizardDomTests`.

---

## Context

The deep sweep finds damage at a destination and, for a local path, repairs it
([ADR-0035](0035-destination-fitness.md) Amendments 1 and 2). Followed to the
moment damage matters, a restore, the reading showed a gap: **a restore read
exactly one store.**

- With no destination named, it read the set's own archive: the staging
  archive, or, for a direct-ship set, the ship sink, which answers each blob
  from the first local destination holding the key
  ([ADR-0046](0046-direct-to-destination-publication.md)) and never looks at
  the bytes.
- A record that failed its checks there failed its file, as FR-RST-002 and
  FR-RST-005 require. Nothing tried another copy, although every copy of a
  blob is the same bytes: blobs are immutable, and a replica holds them key
  for key.

So:

| Set | What happened | What the restore did |
|---|---|---|
| Direct-ship, two local paths (the shape a new local-path set is born with) | The first destination's copy rotted | Failed those files, with the second destination holding them sound |
| Staging | The staging copy rotted | Failed those files, with every destination holding them sound |
| Staging | Staging trimmed the history's data blobs, by design ([ADR-0034](0034-hub-and-spoke-destinations.md) §6) | Planned those files as missing and failed them, "the destination replica is the restore path" — which a person had to know |
| One local copy and a peer | The local copy rotted, or its drive was away | Failed those files, with the peer holding them sound |

The sweep does not close this. It reaches damage only when its circuit does —
up to its interval, a week by default, plus the circuit's length — and it never
repairs a peer. Nor did the other half of the answer exist: nothing a restore
met was ever recorded, so a restore that failed a file over a rotted blob told
the person that one file failed, and nobody that a device had altered a backup.

## Decisions

### 1. Only the set's own archive reads around damage

A restore of the set's own archive — no source named, or the source opened for
the set with no destination — reads around a record it will not serve. **A
destination opened by name is read alone**, as a stranger would read it
([ADR-0041](0041-guided-restore-and-peer-retrieval.md) §2).

The reason is the drill. [ADR-0054](0054-scheduled-restore-drills.md)'s drill
restores through the ordinary guided-restore verbs, from the destination it is
drilling, to prove that copy restores. A named source that read around damage
would pass the drill of a rotted destination. A person who names a destination
asks about that copy too, and is told what it holds.

### 2. The copies, in the repair's order

`Agent/SetCopies` lists a set's copies once, nearest and cheapest first:

1. the staging archive, when it is not itself the store being read or
   repaired;
2. the set's local-path destinations, by priority;
3. its paired peers, over the retrieval session.

The repair has always taken its sources in this order (ADR-0035 Amendment 1),
and now takes them from here: the repair and the restore cannot disagree about
where a sound copy may be. For a restore the staging archive is never among
them, since a staging set's restore reads it first and a direct-ship set has
none.

Each copy is opened on first use and at most once: a peer is dialled only once
every nearer copy has failed to serve, and a local path whose replica directory
is missing is a copy that could not be reached. A person is waiting, so no copy
is paced (ADR-0074).

### 3. Record by record, through the same checks

A record the restore's own store will not serve is read from the next copy:

- a record that fails authentication, framing or its content identifier (04 §6);
- a blob that will not read — an I/O error;
- a blob the store does not hold.

The same record sits at the same offset of the same blob wherever the blob is
held, so the next copy is read at the record's one location, and through the
whole 04 §6 sequence. Once every segment has verified, the whole-file hash still
covers the reassembly, whichever copies served which segments. **Nothing is
trusted more for being second.**

Content sealed to a key the reader does not hold, and a profile it does not
implement, are not read around: every copy would give the same answer.

### 4. A location that fails is asked of the blob's footer, at every copy

The catalogue is a cache and never authoritative
([ADR-0068](0068-the-catalogue-directed-restore-read.md) §2). At the own store,
a read at a location that fails already falls back to the blob's footer, which
is the path built to say what is wrong with a blob. At another copy the same
rule applies: the location read, then the footer. A sound footer that does not
list the record says the location was wrong, and that is no fault of the copy.
Without this, a stale location would have been taken for damage at every copy
of the set in turn.

### 5. What a copy passed over is called

| Fault | Meaning | A finding? |
|---|---|---|
| Damaged | The copy holds the blob, and the record or the blob's framing is not what was sealed | Yes |
| Unreadable | An I/O error | No: one read cannot tell a bad sector from a device going away (ADR-0035 Amendment 1) |
| Not held | The blob is not there, or its footer does not list the record | No: staging's trimmed history, or a destination not yet caught up |
| Unreachable | The copy could not be opened or dialled | No |

A direct-ship set's own store is a way of reading its destinations, not a copy
of its own, so it is never named: each destination it reads is tried, and
named, in its own turn.

With every copy failed, the record fails as it did (FR-RST-005), and the failure
names each copy tried.

### 6. What is said

- **The receipt (schema 5).** Each item says which copies its content came from
  when some came from another than the restore's own store (`read_from`), and
  what was wrong with those it was read around (`read_around`) when any was
  damaged or would not read. The golden run of `Repository.Tests/RestoreBreadthTests`
  writes neither and is otherwise byte-identical.
- **The answer (contract 1.45).** `read_around` counts the files read around
  damage or an unreadable copy, and `read_around_sample` gives up to twenty
  lines naming the copy each came from and what the copies passed over held.
  The CLI prints them and the console's result step shows them.

A file read from a destination only because staging no longer holds it is in
the receipt's `read_from` and is not counted: its bytes came from where they
were meant to.

### 7. What is recorded

Damage a restore finds is held where the rest of the service acts on it.

- **At a destination:** the objects go on its ledger row as the sweep's
  findings do, the pair is failed (FR-VER-005), and a notice
  (`restore-found-damage`) names the destination and the objects. The next
  sync re-checks the row: a local path's objects are replaced from a copy
  proven sound (FR-VER-007), and a peer's are held until its owner removes
  them, with the notice naming exactly what to remove (ADR-0035 Amendment 2).
- **In the staging archive:** a notice alone. The staging archive has no
  ledger row, and nothing repairs it in place.

A copy that was unreadable, or that did not hold a blob, records nothing.

### 8. The plan answers for the run it plans

The plan probe counts a file as missing only when no copy of the set holds what
it needs (FR-RST-003). It probes through `Restore/FirstHolderStore`, which asks
the own store first and opens a copy only for a blob the store does not hold.
The healthy case costs what it did.

> **This amends [ADR-0034](0034-hub-and-spoke-destinations.md) §6**, whose
> first stated residual cost was that "restoring a trimmed snapshot from
> staging is impossible and the restore plan says so". It is no longer
> impossible: the restore reads the trimmed history from the destinations,
> which were always the restore path for it, without a person having to
> choose them.

## Consequences

**Positive**

- A direct-ship set's default restore survives a rotted first destination. A
  staging set's survives both a rotted staging archive and a trimmed history.
- A restore finds damage too. What it meets goes on the same ledger the sweep
  writes, so the next sync repairs it.
- Nothing is paid when nothing is damaged. The copies are opened only for a
  record the own store would not serve, and the plan asks them only about a
  blob it lacks.

**Negative**

- **Reading around a damaged blob costs its footer, record by record.** The own
  store's footer fallback (ADR-0068 §2) is attempted for each record of a
  damaged blob before a copy is asked, a few reads each. Remembering a refused
  blob across records was considered and not taken, because a peer's transient
  short read would then fail every later record of that blob for the rest of
  the run.
- **A restore of the set's own archive can now dial a peer.** It does so only
  for records no nearer copy served.
- **A damaged staging archive is only said, not repaired.**
- **A restore that found damage fails the pair until the next sync.** This is
  FR-VER-005's rule, applied to a finding that did not come from verification.

**Neutral**

- The receipt schema moves to 5, and the contract to 1.45. Both are additive.
- A direct-ship set's first destination is read twice for a damaged record:
  once through the set's read path, and once in its own right, which is how it
  comes to be named.

## Alternatives considered

**Read around at the store.** A store that tried the next copy on an I/O error
or a missing key would have been one decorator. It could not see damage: a
record is verified above the store, and a flipped byte reads perfectly well.

**Read around whole blobs.** Proving the own store's blob sound, and switching
copies per blob, would cost a whole-blob hash for every blob a restore touches.
A record that fails its checks is already a precise verdict about the bytes it
was read from.

**Read around a named source too.** This would break the drill's proof and the
stranger's view it rests on.

**Repair during the restore.** A restore runs on the reader lane, and the
destination may be syncing on the transfer lane at the same time. The next
sync's re-check does the repair under the lane that owns the pair.

**Record an unreadable copy as damage.** Refused for ADR-0035 Amendment 1's
reason: it would condemn a drive for being unplugged.

## What this record does not do

- **Repair the staging archive.** Damage found there is said, and stays.
- **Name in the plan which copies will serve.** FR-RST-003's "replicas" is
  still only implicit: a plan says nothing is missing, not where each piece will
  come from.
- **Read around for the CLI's own restore without a service.** That path reads
  the one store it opened.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | Built tests first (the reader, the receipt, the service, the contract, the CLI and the console), with each rule's removal confirmed to turn exactly its tests red: a named source read around, the findings dropped, the plan blind to the copies, and a failed location taken for damage at a copy or at the own store. Amends ADR-0034 §6 |

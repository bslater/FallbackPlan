# ADR-0010 — Local store separation

**Status:** Accepted · Implemented — see [implementation status](../implementation-status.md#by-decision)
**Date:** 2026-08
**Requirements:** NFR-REL-002, NFR-REL-007, NFR-OPS-003, FR-MAN-002
**Review finding:** [H3](../review/2026-08-architecture-review.md#h3--disposable-conflates-three-stores-with-incompatible-durability-requirements)

---

## Context

The proposal specified "SQLite for disposable local cache, job state, and UI configuration — not repository authority", and NFR-REL-002 stated that "deleting or corrupting it shall not cause repository data loss".

The catalogue genuinely is disposable, and insisting on that is right — it is the direct answer to the surveyed product's failure mode the proposal identifies. But three things were placed in one sentence and one database, and they do not share the property:

- The **device private key** is not rebuildable from the repository. Losing it means the device loses its identity, and every pairing must be re-approved by hand at the other end.
- **Pairing grants and destination authorisations** are likewise not derivable from repository contents.
- **Job history and schedules** are not needed for recovery, but silently losing them means backups stop happening — and nothing alerts, because the thing that would have alerted is gone too.

NFR-REL-002 was therefore true of the catalogue and false of its housemates. Anyone acting on it — a support article saying "delete the database and let it rebuild", or the rebuild tooling itself — would destroy the device identity while following documented advice.

## Decision

Three stores, three lifecycles, **separate on disk**:

| Store | Contents | Rebuildable | Loss consequence |
|-------|----------|-------------|------------------|
| **Catalogue** | Path, version, segment, blob, generation indexes; watermarks | ✅ From repository | Slow rebuild; no data loss |
| **Durable local state** | Device keypair, pairing grants, destination authorisations, job history | ❌ | Device identity lost; pairings must be re-approved manually |
| **Configuration** | Backup sets, schedules, policies, provider settings | Partially — policy manifests record what each snapshot used | Backups silently stop |

Separate files, not separate tables in one file, so "delete the catalogue and let it rebuild" cannot take the device identity with it.

**Catalogue:** SQLite behind an abstraction so another embedded engine can replace it. Never repository authority.

> **Amendment 2 (2026-09).** "The catalogue none" is carried down to the
> engine: its commits are atomic but not flushed, so a power loss can take
> the newest of them — see
> [Amendment 2](#amendment-2-2026-09--the-catalogues-commits-are-atomic-not-flushed).

> **Amendment 4 (2026-09).** "Slow rebuild" had nothing doing the rebuild:
> the service now rebuilds a catalogue it finds discarded or lost when it
> opens the set, before anything reads it — see
> [Amendment 4](#amendment-4-2026-09--a-catalogue-discarded-is-rebuilt-before-it-is-read).

> **Amendment 5 (2026-10).** One catalogue file, several connections: a
> set's backup has the archive's connection to itself, and every job that
> may run beside it opens its own — see
> [Amendment 5](#amendment-5-2026-10--a-sets-backup-has-the-catalogues-connection-to-itself).

**Durable local state:** separate store, OS-key-store protected where available. The device *private key* is never written to the recovery kit — a recovering device establishes a new identity and is re-authorised.

**Configuration:** file-based, schema-versioned, validated before use, exportable without secrets. Files rather than a database because users edit, version-control, and diff them.

## Consequences

**Positive**

- NFR-REL-002 becomes true as stated, because it is now scoped to the thing it is true of.
- Deleting the catalogue — a legitimate, documented recovery action — is safe.
- Each store gets the protection it warrants: the catalogue none, durable state key-store protection, configuration file permissions.
- Configuration is version-controllable, which advanced users will do regardless.

**Negative**

- Three stores to manage, back up, and reason about instead of one.
- Users must be told that durable local state is separately backed up or re-established by re-pairing — an extra concept in the recovery story.

**Neutral**

- Recovery of **data** needs only repository plus kit. Recovery of **operation** additionally needs configuration and re-pairing. Stating the difference plainly is better than implying restore-and-done, which leaves a user believing they are protected when they are not.

## Alternatives considered

**One database, with NFR-REL-002 narrowed to specific tables.** Rejected — the property becomes unenforceable in practice, and any tool or instruction that deletes the file destroys the device identity.

**Device identity in the recovery kit.** Rejected. It would let a stolen kit impersonate the device to its destinations, and it is unnecessary: a new device establishing a new identity is the correct recovery model.

**Configuration in the repository.** Attractive — it would survive a clean machine — but it would leak backup-set names and destination endpoints into a store we treat as untrusted, and it would make configuration edits repository writes. Rejected. Policy *manifests* already record what each snapshot used, which covers the audit need.

## Amendment 1 (2026-08) — where the hub-and-spoke state lands in the split

[ADR-0034](0034-hub-and-spoke-destinations.md) adds three kinds of client-side
state, and each slots into an existing row of the table rather than a new one.
**Destination declarations** — names, kinds, paths, peer fingerprints and
endpoints, per-set references and retention policies — are **configuration**:
user-edited, schema-versioned, exportable. The export guidance changes with
them: the file still holds no secrets, but it now names who stores your backups
and where, so "exportable without secrets" is no longer "shareable without
thought", and the docs say so. **Per-destination sync state** (what each
destination holds, when it was last reached, why it last failed) and **notices**
(a peering ended, terms narrowed, a quota was hit) are journal-shaped files
beside `jobs.json`: not rebuildable from the repository, tolerable to lose —
sync state re-derives from a destination inventory pass, and a lost notice is
re-raised by the condition still holding — and therefore deliberately outside
the durable-state file whose loss costs the device its identity.

The rejected alternative "configuration in the repository" stays rejected, and
gains its sharpest example yet: destination endpoints in the repository would
hand every destination the list of all the others.

## Amendment 2 (2026-09) — the catalogue's commits are atomic, not flushed

The decision gave the catalogue no protection, but the engine was still
running SQLite's default for it, `synchronous = FULL`: a flush of the
write-ahead log to disk at every commit. Publication records each blob,
directory, file version and segment reference as a commit of its own, so a
first backup of N files paid roughly N × (2 + segments per file) flushes to
keep a cache durable. On Linux and macOS a flush is cheap enough to hide.
On Windows it costs milliseconds, and it became most of the cost: in one CI
run Repository.Tests took 42 minutes on the Windows runner against 2
minutes 8 seconds on macOS, and of the 197,968 flushes that suite makes,
182,148 were the catalogue's log.

The catalogue now runs WAL with `synchronous = NORMAL`. Every commit is
still atomic. What changes is durability: a process crash still loses
nothing, because a commit has reached the operating system before it
returns, but a power loss or an operating-system crash can take the newest
commits.

That is safe for the reason the decision gives, and it puts the catalogue
in no state it could not already reach:

- **It only ever leaves the catalogue behind the store.** Flushing every
  commit never made the catalogue current after a power loss either; it
  kept the commits up to the moment of the loss, where now the survivors
  can stop a little earlier. Behind is the direction the design already
  prices as a rewrite, never as a lost restore
  (`Repository.Tests/EndToEnd/StaleCatalogueTests`). The collector marks
  from the repository and never reads the catalogue
  ([07 §3](../architecture/07-retention-and-gc.md#3-garbage-collection)),
  and a rebuild from the index plane restores anything else
  ([02 §8](../architecture/02-repository-format.md#8-catalogue-rebuild)).
- **The file stays consistent.** Each WAL frame carries a checksum, and
  recovery replays the log only up to its last whole commit.

Nothing else loosens. The store's objects, the blob spools and the state
files are flushed exactly as before.

SQLite ignores a pragma it cannot honour instead of refusing it, so the
setting is held by reading it back from the engine:
`Repository.Tests/Catalogue/CatalogueTests` fails if the catalogue opens
with anything but WAL and `NORMAL`.

## Amendment 3 (2026-09) — the catalogue's connections are not pooled

Microsoft.Data.Sqlite pools connections by default, and the catalogue took
the default. The pool has a race. When it hands out a connection it marks
the connection active before it records who holds it, and it counts a
connection that is active with nobody holding it as leaked. A pool clear
that lands between those two writes reclaims the connection it has just
handed out and disposes its handle, and the caller's next command fails on
a disposed handle. The race is in 10.0.10, which this repository pins, and
in 10.0.12, the newest release. The library's 10.0 branch has swapped the
two writes, and no release carries that yet.

This process clears pools often, and every clear reaches every pool in it.
The service cleared them itself so that it could delete a catalogue file a
pooled connection still held: when a restore source closed, when a
catalogue was recreated for another repository, and when an adoption
rebuilt one. The service also opens catalogues from many operations at
once. Where the victim is a restore drill, nothing is said at all:
`Agent/RecoveryDrillJob` treats a disposed object as the runtime shutting
down and records no outcome. That is the likeliest reading of a drill in CI
that left no trace. The race itself was caught where it cannot hide, as a
catalogue open that failed outright.

> **Amended (2026-09):** a drill now takes a disposed object for shutdown
> only while the service is stopping. Met while the service runs, it is a
> drill that did not complete, and the drill records that
> ([ADR-0054 Amendment 4](0054-scheduled-restore-drills.md#amendment-4--a-drill-that-did-not-complete-says-so-2026-09)).

The catalogue now opens its connections with pooling off. A connection that
belongs to no pool is out of reach of any clear, and it releases its file
when it is disposed, which is all the clears were for, so the service no
longer makes them. Nothing measurable was given up. On the container this
was measured in, 3,000 opens of one catalogue, each with a query and a
dispose, cost 537 to 614 µs apiece unpooled and 546 to 625 µs pooled,
because every open already runs the pragmas and the compatibility check.
Windows was not measured.

`Repository.Tests/Catalogue/CataloguePoolingTests` holds this. It opens
catalogues on four threads while two others clear every pool in the
process. With pooling on it failed three runs of three within about two
seconds, after 2,242 to 2,898 clean opens. With pooling off it runs its full
five seconds clean.

## Amendment 4 (2026-09) — a catalogue discarded is rebuilt before it is read

The decision prices losing the catalogue as a slow rebuild. That held only
while something ran the rebuild, and in the service nothing did.
`Catalogue.Open` has always discarded a file it cannot use rather than
migrate it: one another schema version or another repository wrote is
deleted and created again, empty. The service opens a set's catalogue once,
when it first opens the archive, and reads it from then on. The rebuild
recipe — the index plane, then the manifest projection — ran for a restore
source's throwaway copy, for adoption
([ADR-0061](0061-adopt-a-destinations-archives.md)) and for the heal after a rollback
([ADR-0062](0062-the-destination-is-the-rollback-witness.md)), and never
for the set's own catalogue.

So every catalogue schema change emptied the history of every set it met.
The v7 change, when the consistency method became a column, said the
catalogue would drop and rebuild on first open; only the drop happened.
Measured against main before this amendment: back up, stop, stamp the
catalogue with an older schema version, start again, and `list_snapshots`
answers no snapshots. Nothing was lost from the repository, but nothing
read it back: the snapshots taken before the upgrade were not listed, a
restore planned from the catalogue could not find them, and the next
backup stored again whatever the empty catalogue could not locate.

Now:

- A catalogue `Catalogue.Open` creates — new, or in place of one it
  discarded — says so in its own file, and keeps saying so until a rebuild
  that saw every record it needed clears it (`NeedsRebuild`,
  `MarkRebuilt`). In the file rather than the process, so a rebuild cut
  short is tried again instead of being taken for one that finished.
- The service rebuilds a marked catalogue when it opens the set, before the
  handle is shared, through the same recipe as every other rebuild, reading
  the set's own store.
- A rebuild clears the mark only when the projection saw every record it
  needed. A direct-ship set's metadata blobs live at its destinations
  ([ADR-0046](0046-direct-to-destination-publication.md)); with the one holding them
  away, the projection lists the snapshot from its local record and cannot
  see its files, and counts the records it could not see. The mark stays,
  and the next open tries again. A record that was seen and would not read
  is damage, a finding like any other, and does not keep the mark: another
  open would only meet it again.
- Reading those blobs went through the ship sink, which opened a replica
  store for every destination present — and opening one creates the
  replica's root. So the rebuild wrote a directory at a destination the set
  held nothing at, including one under its floor that the set must not
  write to at all (FR-DEST-010); `DirectShipFaultSweepTests` caught it.
  Reads through the sink now pass over a destination that holds no replica
  of the repository. Writes still create one.
- A rebuild that cannot read the repository at all leaves the mark and the
  set open. A backup into a catalogue short of its history costs a rewrite
  and never a restore, the direction Amendment 2 already prices. Events
  3788 to 3790 say what each rebuild did.

`Repository.Tests/CatalogueTests` holds the mark: a catalogue created, or
recreated over another schema's file, needs rebuilding, and marking it
rebuilt lasts across an open. `Hosts.Tests/CatalogueRebuildAtOpenTests`
holds the rest through restarts of the real service: a staging set's
catalogue stamped with an older schema, and a direct-ship set's deleted
while the service was stopped, both list their snapshots and files again,
and a rebuild with the destination away is tried again at the next open.

## Amendment 5 (2026-10) — a set's backup has the catalogue's connection to itself

The service opens one connection to a set's catalogue when it opens the
set's archive, and keeps it on the archive handle. The set's backup reads
and writes through it. Four other jobs used it too, and each can run while
that backup does:

- **A sync's verification**, reading the digests and Merkle roots it proves
  a replica against. Syncs run on the transfer lane, beside the writer pool.
- **Retention**, looking up where objects now live, forgetting the
  snapshots a deletion took, and compacting. It runs on the writer pool,
  whose default width is two. One run per set at a time is a rule for
  backups, and retention is not one. Collection beside an in-flight backup
  is designed for: the write-intent rule keeps the backup's blobs from it
  (FR-GC-003).
- **A snapshot deletion**, the same way.
- **The heal after a rollback**
  ([ADR-0062](0062-the-destination-is-the-rollback-witness.md)), which
  rebuilds the catalogue from inside a sync.

The set gate
([ADR-0029 Amendment 2](0029-pipeline-and-service-concurrency.md#amendment-2-2026-08-the-transfer-lanes-premise-and-the-set-gate))
keeps a sync from a retention apply. It keeps neither from a backup.

A Microsoft.Data.Sqlite connection is not safe to share between threads,
and sharing one does not fail where it happens. Two threads creating and
disposing commands at once can corrupt the connection's own bookkeeping, so
that a later call fails somewhere else. It showed once, in one full run of
the suite: a `NullReferenceException` from inside the connection's close
as the runtime disposed. A probe then logged each digest lookup a sync made
while the same set had a backup in progress: 36 in five runs of
`SetChangeTests`, and 21 in three runs of them on the main branch of the
time. Sharing
has a quieter cost too. On one connection every command joins whatever
transaction is open, so a job could read a backup's write that was never
committed.

Now:

- **The set's backup has the archive's connection to itself.** The runtime
  uses it as well while it opens the archive and, under Amendment 4,
  rebuilds a discarded catalogue, both before the handle is shared. Nothing
  else uses it.
- **Every job that may run beside the backup opens a connection of its
  own.** A sync's verification reads through one, as `SetChangeScan`
  already did for the same reason. Retention, a deletion and the heal write
  through one opened by `ArchiveHandle.OpenWritableCatalogue`, which
  differs from the read opener only in the name it gives its caller.
- **SQLite keeps the connections apart.** In WAL mode (Amendment 2) one
  connection writes at a time, and Microsoft.Data.Sqlite retries a busy
  database until the command timeout, thirty seconds. A reader sees the
  last commit and never waits behind a writer.

What it costs:

- **An open per job.** Each costs about half a millisecond on the container
  Amendment 3 measured.
- **A write can now wait.** A write behind another connection's waits for
  it to commit, where before it went ahead and was unsafe. Every catalogue
  call commits before it returns, so a write waits for the other side's
  calls, never for the whole of its job. On the container this was built
  in, three connections wrote in tight loops for ten seconds each, two of
  them one-row commits and one 500-row transactions. The longest any single
  write waited was 4.4 s, and none failed. The product's worst case, a
  backup and a heal of one set projecting at once, was not measured. It is
  two writers rather than three, and each does other work between its
  writes.

How it is held:

- `ArchitectureTests/CatalogueConnectionTests` reads the compiled service
  and names every method that uses the backup's connection. A call written
  in a lambda, a local function or an async method is charged to the
  method it was written in. Against the code before this amendment it
  named seven: two in the sync's verification, four in retention and
  deletion, and the heal.
- The race itself is not raced. It is rare enough that a test setting two
  jobs against each other would pass on the code it should fail. Instead
  `Hosts.Tests/DirectShipVerificationTests`,
  `Hosts.Tests/PeerReadBackVerificationTests` and
  `Retention.Tests/SnapshotDeletionServiceTests` close the backup's
  connection, then run a sync at a local path, a sync at a peer and a
  deletion. A closed connection fails at once where a shared one fails only
  when two threads meet. Each failed before this amendment and passes
  after.
- `Repository.Tests/Catalogue/CatalogueConnectionsTests` holds the SQLite
  behaviour the rule relies on. A write behind another connection's waits
  for it and then commits, and a read beside another connection's
  uncommitted write sees the last commit at once.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-08 | Accepted | Built and held to: `LocalStateSeparationTests` deletes the catalogue and asserts device identity and configuration survive, which is the whole claim. Nothing about the three-way split awaits a later phase. |
| 2026-08 | Accepted (amended) | Amendment 1: destinations are configuration, sync state and notices are sacrificial journals beside `jobs.json` ([ADR-0034](0034-hub-and-spoke-destinations.md)). |
| 2026-09 | Accepted (amended) | Amendment 2: the catalogue's commits are atomic but no longer flushed one by one, so a power loss can take the newest of them and leave it behind the store, which costs a rewrite. Set in `Repository.Catalogue/Catalogue`, read back from SQLite by `CatalogueTests`. |
| 2026-09 | Accepted (amended) | Amendment 3: the catalogue's connections are not pooled, so a pool clear anywhere in the process cannot dispose one under the operation that opened it, and the service no longer clears pools to delete a catalogue file. Set in `Repository.Catalogue/Catalogue`, held by `CataloguePoolingTests`. |
| 2026-09 | Accepted (amended) | Amendment 4: a catalogue the service finds discarded or lost is rebuilt from the repository when it opens the set, before anything reads it, and a rebuild that could not see every record it needed is tried again at the next open. Marked by `Repository.Catalogue/Catalogue`, rebuilt by `Agent/ServiceRuntime` through `Agent/CatalogueRebuild`, held by `Hosts.Tests/CatalogueRebuildAtOpenTests`. |
| 2026-10 | Accepted (amended) | Amendment 5: a set's backup has the archive's catalogue connection to itself. A sync's verification, retention, a snapshot deletion and the heal each open one of their own (`Agent/ArchiveHandle`), and SQLite keeps the connections apart. Held by `ArchitectureTests/CatalogueConnectionTests`, by three tests run with the backup's connection closed, and by `Repository.Tests/Catalogue/CatalogueConnectionsTests`. |

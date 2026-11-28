# ADR-0201 — A snapshot's bookkeeping waits in its spool, not in memory

**Status:** Accepted
**Date:** 2026-11
**Requirements:** NFR-PERF-001, FR-ARCH-002, FR-MAN-008
**Related:** [ADR-0029](0029-pipeline-and-service-concurrency.md), [Q24](../open-questions.md#q24--what-a-snapshot-holds-per-object-it-stores), [02 §3.4](../architecture/02-repository-format.md#34-capture-algorithm), [07 — Index](../../specifications/repository-format/07-index.md)

**Built:**
- The spill: `Repository/RecordTableSpill`, a session's file of the record
  tables of the blobs it has uploaded, opened exclusively in its spool and
  deleted when it closes.
- The session: `Repository/ArchiveSession` writes each uploaded blob's table
  to its spill, and its `ArchivedBlob` reads it back from there; the spill is
  the last thing the session disposes. `Repository/FileArchiver`, whose
  single-file result outlives its session, reads its tables back first.
- Publication: `Repository/IndexDeltaBudget` cuts deltas as it reads blobs,
  and `Repository/PublicationOrchestrator` projects each blob's entries as the
  cut reaches it.
- The tests: `Repository.Tests/SessionRetentionTests`,
  `Repository.Tests/SpilledRecordTableTests` and
  `Repository.Tests/IndexDeltaBudgetTests`, with
  `Repository.Tests/DeltaSplitPublicationTests` reading a tree's tables from
  its blobs' footers.
- The written set ([Amendment 1](#amendment-1-2026-11--the-written-set-waits-in-the-spool)):
  `Repository/WrittenSet`, which keeps the claims and the records of blobs not
  yet uploaded in memory and the rest in `Repository.Catalogue/SpooledObjectSet`,
  an SQLite table beside the spill; held by `Repository.Tests/WrittenSetTests`
  and `Repository.Tests/SessionRetentionTests`.
- The files' references ([Amendment 2](#amendment-2-2026-11--a-tree-snapshots-files-references-wait-in-the-spool)):
  `Repository/ArchiveSession` writes each captured file's segment references
  and content ids to the same spill once the file's manifest is written, and
  `Repository/SnapshotPublication` keeps the file's result with them there;
  held by `Repository.Tests/SpilledReferenceTests` and
  `Repository.Tests/SessionRetentionTests`.

## Context

A snapshot runs in one archive session, and the session keeps a little for
every record it stores and every segment it reads until the snapshot
publishes. Over 32 GiB of unique content the memory gate measured the live
set growing by about 0.4 to 0.6 MiB per GiB, which [Q24](../open-questions.md#q24--what-a-snapshot-holds-per-object-it-stores)
recorded and put to the maintainer. A heap dump partway through a 16 GiB
run, 10,440 stored records in, says where it goes:

| Held | Bytes | Per stored record | Until |
|---|---|---|---|
| The session's written set: one node a record, and its buckets | 959 KB | about 92 | the session ends |
| The record tables of the blobs uploaded: one array a blob | 926 KB | about 89 | the session ends |
| Each file's segment references and content ids | 1.31 MB | about 125 a segment | publication, because `PublishedFileVersion` keeps each file's `ArchiveResult` for the catalogue |

None of it is a leak, since publication reads every byte of it. But all of it
grows with what one snapshot stores. At 2 TiB it would be on the order of a
gigabyte, past NFR-PERF-001's 256 MB for the file and its 1 GB for the agent.
Publication made a second copy besides: it projected every blob's table into
one list of index entries before it cut a single delta.

The maintainer chose the direction: spill to the spool. The spool is on disk
already, private to the installation, and sized for blobs.

## Decisions

### 1. A blob's record table waits in the session's spool

When a data blob has been uploaded, its record table is written to the
session's spill and dropped from memory. What the session keeps for the blob
is its identity, digest, Merkle root, record count and length, and the
position of its table in the spill. Reading the table reads it back from
there, whole, one positional read.

The spill is fixed-width, 61 bytes an entry, and written once per blob, so a
table is a position and a count. A snapshot of two million records keeps
about 120 MB of it on the spool's volume, beside the blobs that volume
already holds.

### 2. Publication streams the tables

Deltas are cut at blob boundaries under the delta budget, as before (07 §2,
FR-MAN-008). The cut now reads blobs as it goes and hands each delta out as
soon as the next blob would not fit it, and each blob's entries are projected
from its table as the cut reaches it. So publication holds one delta's
entries and one blob's table at a time, rather than the snapshot's. The cut
is the same one: `Split` is `SplitAsItGoes` taken whole.

### 3. The written set keeps only what is in flight

> **Amended 2026-11 ([Amendment 1](#amendment-1-2026-11--the-written-set-waits-in-the-spool)).** Built, and without the
> in-memory filter this decision proposed: a lookup in the spooled set costs
> microseconds, so a filter in front of it would save nothing a segment
> notices.

The written set settles which of two concurrent duplicates is written
(ADR-0029 §1), and remembers every record the session has stored so that a
later duplicate is reused. Only the claims still in flight need memory. The
rest can live in the spool, behind an in-memory filter that lets a lookup
which misses skip the disk.

### 4. Files' references and content ids *(not yet decided in detail)*

> **Amended 2026-11 ([Amendment 2](#amendment-2-2026-11--a-tree-snapshots-files-references-wait-in-the-spool)).** Built, in
> the session's spill beside the record tables. The catalogue reads them in the
> shape it always did, a list per file, read back from the spill rather than
> held. What a file being archived holds of its own references is outside this
> decision, and stays open under Q24.

What the catalogue projects at the end of a snapshot is each file's segment
references and content ids. These can wait in the spool too once the file's
manifest is written. That is the same direction, and it needs its own
amendment, because the catalogue reads them in a different shape.

### 5. What a spill is, and how long it lives

A spill is the session's alone. It is opened exclusively and deleted when it
closes, and the session closes it last, after the upload workers that append
to it have stopped. So a blob's table is readable while its session runs,
and not after.

The single-file archive's result outlives its session. It reads its tables
back into memory before the session ends, because a single file's tables are
that file's to hold. A tree snapshot's published blobs keep their counts and
lengths. Their tables are in their footers, and asking a spilled table after
its session ends says so by name.

A session that dies cannot delete its spill. So the first spill each session
makes removes every spill its spool holds that no live session has open:
holding one exclusively is what makes "no live session" observable on every
platform. A spill holds object identifiers and positions, which name content
without revealing it, so one left until the next session leaks nothing.

## Consequences

- What a session keeps per stored record fell from 160.6 bytes to 78.0 to
  78.5, measured by `SessionRetentionTests` over a hundred thousand
  one-record files. The rest is the written set's, which decision 3
  addresses.
  > **Amended 2026-11 ([Amendment 1](#amendment-1-2026-11--the-written-set-waits-in-the-spool)).** With decision 3
  > built it is 1.7 bytes.
  > **Amended 2026-11 ([Amendment 2](#amendment-2-2026-11--a-tree-snapshots-files-references-wait-in-the-spool)).** And what a tree
  > snapshot keeps for each segment its files reference fell from 84 bytes to
  > 3.9.
- Publication reads the spill once, sequentially, in the order the blobs were
  uploaded. The disk traffic is 61 bytes a record written once and read once.
- The format is unchanged. Nothing durable is written differently, and a
  crashed session's spill is removed by the next.
- A caller that read a tree snapshot's content blobs' tables after
  publication now reads their footers. One test did; no production code did.

## Alternatives considered

- **Publish deltas as the snapshot goes.** This would bound the tables
  without the disk, but it makes deltas visible before their snapshot, and
  the collector would have to protect objects that no published snapshot
  names yet. Q24 put it to the maintainer, who chose the spool.
- **State the cost instead.** Amending the requirements to a per-object cost
  is honest about what was built, but it leaves a 2 TiB file at a gigabyte.
- **Trim the in-memory tables.** A table's array is about 45% list headroom,
  and trimming it would halve what decision 1 removes entirely.

## Amendment 1 (2026-11) — the written set waits in the spool

Decision 3 is built. `Repository/WrittenSet` is the session's written set. It
keeps in memory the claims still in flight and the records whose blob has not
yet uploaded. When a blob uploads, its records go into
`Repository.Catalogue/SpooledObjectSet` and leave memory. That is one SQLite
table, keyed by object identifier, in a file beside the session's spill and
named for it: `record-tables-<id>.written`.

**No filter.** The decision proposed an in-memory filter in front of the
spooled set, so that a lookup which misses would skip the disk. A lookup is a
point read of an SQLite key through a fixed page cache, which costs
microseconds, while a segment takes milliseconds to prepare. So a filter would
cost memory, about a byte per record, to save time nobody would see.
`Repository.Tests/SessionRetentionTests` runs a hundred thousand files, a claim
and a lookup each, in the same five seconds with the set spooled as with it in
memory.

**The race.** A record leaves memory only once it is in the spooled set, and a
claim looks in the spooled set again after taking its place in memory. A claim
can find the record in neither place, because the record moved between its two
looks. That claim takes the place the record left, sees the record in the
spooled set on its second look, and is undone rather than written. Only a
concurrent run reaches this. The tests hold the common case: a duplicate of a
record whose blob has uploaded is reused, not written again
(`Repository.Tests/WrittenSetTests`).

**What it is on disk.** The spooled set keeps no journal and never waits on a
flush, because nothing in it outlives its session. It holds its lock
exclusively for as long as it is open. The session creates it after its spill
and deletes it first, so a set beside a spill belongs to the spill's session.
A set with no spill beside it belongs to a session that is gone, and the first
spill each session makes removes both.

**What it costs.** What a session keeps per stored record fell from 78 bytes
to 1.7, measured by `Repository.Tests/SessionRetentionTests`, whose bound is
now 16. The rest is each blob's own identity, shared by every record in it.
The spooled set's pages, a mebibyte of them at most, are native memory. The
heap measurement does not see them, and they do not grow with the set. On
disk the set is about 32 bytes a record plus its B-tree, beside the spill's
61.

## Amendment 2 (2026-11) — a tree snapshot's files' references wait in the spool

Decision 4 is built. When a tree snapshot has written a captured file's
manifest, `Repository/ArchiveSession` writes the file's segment references and
their content ids to the session's spill, and the file's result keeps a
position and a count for each. That is the same spill as the record tables:
the references as 48 bytes each, the object id and the logical offset and
length, and the content ids after them, 32 bytes each. So it shares the
spill's exclusive hold, its deletion when the session ends, and the next
session's removal of what a crashed one left.

**What the catalogue reads.** The catalogue's projection at the end of the
snapshot runs inside the session, and reads each file's references back as it
records the file: one positional read a file. Nothing else reads them while
the session runs. Every other part of a file's result stays in memory,
because the snapshot's progress and its summary read them: its length, its
blobs, its whole-file hash, and how many segments and content ids it has.

**After the session.** The published snapshot outlives its session. Asking one
of its files for its references then says to read the file's manifest, which
declares the same references. Asking for its content ids says that they were
never written anywhere durable, which 02 §2 requires, so nothing else holds
them. A single file's archive is unchanged: its result is the caller's to
hold, and it carries its references in memory.

**What it costs.** `Repository.Tests/SessionRetentionTests` measures what a tree
snapshot's published result holds over eight files of 1,024 segments each.
That is 3.9 bytes a segment, where it was 84.0. The rest is each file's own
result, shared by its segments. On disk it is 80 bytes a segment, beside the
record tables' 61 a record. `Repository.Tests/SpilledReferenceTests` holds that
damage to a snapshot's content blobs still reaches every file that needs
them, so the catalogue still learns what each version is made of.

**What it leaves.** Two costs still grow with what a snapshot holds, and
[Q24](../open-questions.md#q24--what-a-snapshot-holds-per-object-it-stores)
stays open for them:
- A file holds its own references and content ids while it is archived, and
  its manifest is built from them whole. That is about 80 bytes a segment of
  the one file, released when its manifest is written. A single 2 TiB file
  still costs on that order, about 160 MB at 1 MiB segments.
- Each captured file's own result stays in memory until the snapshot
  publishes, for the catalogue: its path and name, its identity, its digests
  and its result, and its manifest's entry in the metadata blobs' tables. Over
  20,000 one-segment files the published result holds about 1.2 KB a file,
  which grows with how many files a snapshot captures, not what they hold:
  around 1.2 GB at a million files. The walk holds a little more besides, for
  the source-identity hints and the folders, which that figure leaves out.

## Status history

| Date | Status | Note |
|---|---|---|
| 2026-11 | Accepted | Decisions 1, 2 and 5 built, tests first: `Repository/RecordTableSpill`, `Repository/ArchiveSession`, `Repository/FileArchiver`, `Repository/IndexDeltaBudget` and `Repository/PublicationOrchestrator`, held by `Repository.Tests/SessionRetentionTests`, `Repository.Tests/SpilledRecordTableTests` and `Repository.Tests/IndexDeltaBudgetTests`. Decisions 3 and 4 are not yet built. |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--the-written-set-waits-in-the-spool): decision 3 built, without the filter it proposed. `Repository/WrittenSet` keeps claims and records of blobs not yet uploaded in memory and the rest in `Repository.Catalogue/SpooledObjectSet`, beside the spill. A session keeps 1.7 bytes per stored record where it kept 78. Held by `Repository.Tests/WrittenSetTests` and `Repository.Tests/SessionRetentionTests`. Decision 4 is not yet built. |
| 2026-11 | Amended | [Amendment 2](#amendment-2-2026-11--a-tree-snapshots-files-references-wait-in-the-spool): decision 4 built. `Repository/ArchiveSession` writes a captured file's references and content ids to `Repository/RecordTableSpill` once `Repository/SnapshotPublication` has written its manifest. A tree snapshot keeps 3.9 bytes per referenced segment where it kept 84.0. Held by `Repository.Tests/SpilledReferenceTests` and `Repository.Tests/SessionRetentionTests`. Every decision is built. A file's own references while it is archived, and each captured file's result, still grow with the snapshot (Q24). |

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

**Not yet built:** decision 3, the written set; decision 4, the files'
references.

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

### 3. The written set keeps only what is in flight *(not yet built)*

The written set settles which of two concurrent duplicates is written
(ADR-0029 §1), and remembers every record the session has stored so that a
later duplicate is reused. Only the claims still in flight need memory. The
rest can live in the spool, behind an in-memory filter that lets a lookup
which misses skip the disk.

### 4. Files' references and content ids *(not yet decided in detail)*

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

- What a session keeps per stored record falls from 160.6 bytes to what
  `SessionRetentionTests` measures after it, over a hundred thousand
  one-record files. The rest is the written set's, which decision 3
  addresses.
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

## Status history

| Date | Status | Note |
|---|---|---|
| 2026-11 | Accepted | Decisions 1, 2 and 5 built, tests first: `Repository/RecordTableSpill`, `Repository/ArchiveSession`, `Repository/FileArchiver`, `Repository/IndexDeltaBudget` and `Repository/PublicationOrchestrator`, held by `Repository.Tests/SessionRetentionTests`, `Repository.Tests/SpilledRecordTableTests` and `Repository.Tests/IndexDeltaBudgetTests`. Decisions 3 and 4 are not yet built. |

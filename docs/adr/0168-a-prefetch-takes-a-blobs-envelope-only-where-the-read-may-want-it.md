# ADR-0168 — A prefetch takes a blob's envelope only where the read may want it

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-009 (amended: a prefetch reaches down to a blob's envelope only where the read may want it)
**Related:** [ADR-0068](0068-the-catalogue-directed-restore-read.md) (the coalesced read and the envelope fold it called required), [ADR-0166](0166-a-format-3-data-record-is-read-without-its-blobs-envelope.md) and [ADR-0167](0167-a-format-3-metadata-record-is-read-without-its-blobs-envelope.md) (a format-3 record read without its envelope, and the fold named as not done), [ADR-0066](0066-the-format-upgrade-record.md) (a repository upgraded from format 2 still holds its format-2 blobs)

**Built:**
- The reader: `Repository/RepositoryReader` remembers what the envelopes it has framed show. A prefetch's first run reaches down to offset 0 for an unframed blob only while the reader cannot read that blob's records before its envelope, no framed envelope has shown format 3, or one has shown an older format.
- The tests:
  - `Repository.Tests/SparseRecordReadTests` — once a framed envelope shows format 3, a prefetch of data records, and of metadata records whose generation the catalogue recorded, starts at the first record wanted and reads nothing below it; a metadata blob whose generation is not recorded still takes its envelope in the same read; in a format-2 repository every prefetch takes its blob's envelope, at one read a blob as before; in a repository holding both formats the first format-2 blob costs its envelope a read of its own, once, and every prefetch after it takes its blob's envelope again
  - `Repository.Tests/RestoreBreadthTests` — unchanged, and still passing: the whole-snapshot restore it measures at format 2 costs what it did

---

## Context

A restore prefetches a file's records in runs ([ADR-0068](0068-the-catalogue-directed-restore-read.md)). Where the first record a run wants lies within the bridge of a blob's start, up to 1 MiB in, the run reaches down to offset 0 and takes the envelope with it. ADR-0068 called that fold required rather than an optimisation, because then a blob's framing needed its envelope and paying for it separately was a second read for every blob.

Since [ADR-0166](0166-a-format-3-data-record-is-read-without-its-blobs-envelope.md) and [ADR-0167](0167-a-format-3-metadata-record-is-read-without-its-blobs-envelope.md), a format-3 record is read without its envelope. For such a blob the fold buys nothing, and it fetches every byte between the envelope and the first record wanted. A restore of one file out of the middle of many blobs pays up to the whole bridge for each blob it touches. ADR-0166 named this fold as not done.

The difficulty is the one ADR-0166 met: nothing before a blob's envelope says whether the blob is format 2 or format 3. A format-2 record cannot be read without its envelope, so a format-2 blob not folded costs its envelope a read of its own. That is the second read the fold was built to save. A repository created at format 2 holds nothing else, and one upgraded from it holds both kinds.

## Decision

### 1. The reader goes by the envelopes it has framed

A reader remembers what the envelopes it has framed have shown: format 3 or later, or something older. Both facts are about the repository, not about one blob, and they are learnt from bytes the reader read anyway.

### 2. A blob's envelope is folded in unless all three say it is not wanted

A prefetch's first run reaches down to a blob's envelope, on the bridge and window tests it always used, unless all three of these hold:
- the reader can read the blob's records before their envelope: a grant for a data blob, or the recorded key generation for a metadata blob;
- an envelope it framed has shown format 3;
- none has shown an older one.

So a reader folds as it always did until its first framed envelope shows the repository is format 3. In a format-2 repository it folds on every blob, at one read a blob, exactly as before. In a format-3 repository the blobs of the reader's first prefetch fold, because nothing has been framed yet. Once one of them is framed, every run after that starts at the first record it wants.

### 3. A repository holding both formats pays one read, once

In a repository upgraded from format 2, a reader that has framed a format-3 envelope may meet a format-2 blob unfolded. That blob costs its envelope a read of its own, which also shows the older format, and from then on the reader folds again as before. The cost is one read for the whole run, not one per blob.

### 4. Nothing a read checks changes

This decides only where a run starts. Each record is read and verified as it was. A run that does not reach a blob's envelope sends that blob's records to the read ADR-0166 and ADR-0167 decided, and an envelope is still read whenever a record will not read without it.

## Consequences

- A restore out of a format-3 repository no longer fetches the bytes between a blob's envelope and the first record it wants, up to 1 MiB a blob, after its first prefetch.
- A restore out of a format-2 repository reads exactly what it did.
- A restore out of a repository holding both formats may read once more than it did. That is within NFR-PERF-009's budget for any restore of five or more blobs.

## What this does not do

- **Learn from the repository's descriptor.** A descriptor names the format a repository was created at, which would settle the question before any envelope is framed. But the readers that restore are handed a store and keys, not the descriptor, and the first prefetch's folds cost at most the bridge for each of its blobs, once.
- **Fold per class.** A format-2 data blob and a format-2 metadata blob both turn the reader back to folding. The older format is a fact about the repository's past, not about one class.

## Alternatives considered

- **Never fold for a blob read before its envelope.** Rejected. A repository created at format 2 would pay a second read for every blob, which is the shortfall ADR-0068 closed, and its measured whole-snapshot restore would leave one read a blob.
- **Take the repository's descriptor format as every blob's.** Rejected for now, as above: it needs each reader to be told the descriptor, for a saving the evidence rule already gets after one blob. A repository created at format 3 holds no format-2 blob, so it would be safe.
- **Fold only within a smaller bridge for such blobs.** Rejected. Any fold for a blob read without its envelope fetches bytes nothing reads. The bridge is the bound on waste where the envelope is wanted, not a reason to fetch it where it is not.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A prefetch takes a blob's envelope only where the read may want it, judged by what the envelopes the reader has framed show. A format-2 repository reads what it did, and one holding both formats reads once more. |

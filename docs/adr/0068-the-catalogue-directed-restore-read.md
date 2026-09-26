# ADR-0068 — The catalogue-directed restore read

**Status:** Accepted
**Date:** 2026-09
**Requirements:** NFR-PERF-009, NFR-PERF-001, FR-RST-003, NFR-REL-004
**Related:** [ADR-0041](0041-guided-restore-and-peer-retrieval.md), [ADR-0012](0012-storage-provider-contract.md), [ADR-0010](0010-local-store-separation.md), [ADR-0052](0052-relocatable-records-format-v3.md), [architecture 05 §5](../architecture/05-storage-providers.md#5-request-economics), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:** `Repository/PrefetchPolicy` (the three bounds a coalesced read works under, named rather than written as literals), `Repository/RepositoryReader` (the location source, the one-envelope framing open, the lazy footer fallback, the run cache, `PrefetchAsync` and `PlanRuns`), `Repository.Packing/BlobReader` (`RecordSpan`, `BlobRun` and the read served from bytes already in hand, `OpenFramingAsync` and `OpenFramingFrom`, the shared `DeriveKeys` and `ReadRecordCoreAsync`), `Repository.Packing/RecordFraming` (`MaxRecordLength`, which lets a range be sized before the envelope that settles it is read), `Restore/RestoreExecutor` (`PrefetchWave`, the bounded read-ahead), `Repository/RestoreEngine` and `Repository/RepositoryReader.RestoreAsync` (a file's segments asked for together), `Agent/ServiceCommandHandler` and `Cli/OperationGateway` (both restore paths on the location source, and the probe out of the restore), `Restore/RestoreBlobSet` (the probe, kept, in the one place that still wants it); `Repository.Tests/RestoreBreadthTests`.

---

## Context

[NFR-PERF-009](../requirements/non-functional.md#performance) budgets a
restore at **1.2 × the distinct blobs holding the segments it needs**, and it
was written because on an object store every GET is billed and every round
trip is latency somebody waits through. It had never been met, and the way it
had not been met was recorded honestly rather than quietly: a characterisation
test pinned the exact counts the read path issued, said in its own comment
that the budget was *architecturally* unmet, and named what would make it a
compliance test.

Three things cost requests, and they compounded:

- the reader **listed the whole blob namespace and opened every blob in the
  repository** before touching a byte of the file anybody asked for — three
  ranged reads each, proportional to repository size rather than to the
  restore, so recovering one document from a decade of backups paid for the
  decade;
- each blob it read from was **opened through its locator and footer**, which
  is three GETs before a byte of payload;
- every manifest and every segment was **one ranged read of its own**, with no
  merging of neighbours, although a file's records sit next to each other in
  the blob they were written into.

## Decision

### 1. Three terms, and the arithmetic that says all three were needed

Writing `B` for the data blobs and `M` for the metadata blobs a restore needs:

| | GETs |
|---|---|
| whole-store load | 3 × blobs in store + 1/manifest + 1/segment |
| targeted load | 3(B + M) + 1/manifest + 1/segment |
| located reads, no coalescing | (B + M) envelopes + 1/record |
| coalesced, envelope paid separately | 2(B + M) |
| coalesced, envelope folded in | (B + M) |

Only the last fits. Two consequences follow that are easy to get wrong and are
therefore stated: **opening a blob costs three GETs before a byte of payload,
so no amount of coalescing reaches `1.2B` while the read opens the blob**; and
**the envelope fold is required rather than an optimisation**, which is why
the first run of a blob reaches down to offset 0 and takes the envelope with
it instead of only merging neighbours.

### 2. The catalogue is the location source, and it fails closed

`Repository.Catalogue/Catalogue.ResolveLocation` already answers every field a
ranged record read wants: the blob, the store key, the physical offset, the
stored length and both profiles. The record's own 54-byte header supplies the
rest. So a restore reads the record straight from where the catalogue says it
is, and the blob costs one 88-byte envelope read — held for the run, because
it is the same fact for every record in that blob.

This is safe because it fails **closed**: a record is AEAD-sealed with its
identity bound into the AAD, so a wrong or stale offset produces a tag failure
and never silent corruption. It rests on a posture the catalogue already has —
[ADR-0010](0010-local-store-separation.md) makes it a disposable cache that is
never authoritative, so trusting it for a *hint* and not for correctness is
what it was built for.

What is given up is the footer as a **second, independent** statement of where
records are. `BlobReader.ReadFramedAsync` cross-checks seven header fields
against the authenticated table; a location states four of them. The other
three are not a gap: the object type and, before format 3, the ordinal are AAD
inputs, so a header that lies about either fails its tag, and the logical
length is what specification
[04 §6](../../specifications/repository-format/04-record.md) step 7's plaintext
re-hash checks. The read therefore has the same number of checks and two of
them moved — from the footer to the cipher. What genuinely coarsens is the
**diagnosis**: "the record header disagrees with the footer's table entry"
becomes "authentication failed".

The **lazy fallback** gives the diagnosis back where it is wanted. A reader
holding a location source loads nothing, so a failed fast read has nothing to
fall back to until the blob is opened for it; `OpenForFallbackAsync` opens
through the locator and footer, indexes the blob and lets the footer path
answer. Damage is still described by the reader built to describe it
([architecture 04 §7](../architecture/04-concurrency-and-publication.md),
NFR-REL-004), and a stale offset costs one wasted read rather than a lost
file.

### 3. The object-id cross-check is load-bearing

This is the least obvious thing in the design and the one a later reader is
most likely to delete as redundant.

A record read takes its key and its AAD from **the record's own header**. So a
location pointing at a different — but perfectly valid — record in the same
blob decrypts, authenticates and content-verifies. Every check passes, because
the bytes are genuinely authentic. They are simply not the ones asked for, and
nothing downstream would notice.

Comparing the header's object id against the id the location was resolved for
is the whole of what prevents it, and it is held on **both** read paths, the
dedicated read and the one served out of a coalesced buffer.

### 4. A prefetch is a transport decision and never a verification one

Records are still opened one at a time out of a run: the same header
cross-check, the same tag, the same step 7 re-hash. Only where the bytes came
from moves, and nothing is trusted for having arrived in a bigger read.
`BlobRun.TrySlice` refuses to serve a record a run covers only part of, rather
than handing back a short slice that would read as a truncated record — which
would be a damage finding about the blob, invented by the prefetch.

### 5. Three bounds, because they protect three different things

`Repository/PrefetchPolicy` names them rather than leaving three literals in
the reader, because they are a trade with a cost on each side:

- **`CoalesceWindowBytes`** (8 MiB) caps what one read may fetch. NFR-PERF-001
  bounds memory by configuration and not by what a file happens to be, so a
  large file becomes several runs instead of one enormous buffer.
- **`MaximumBridgeBytes`** (1 MiB) caps what one read may **waste**: a gap
  between two wanted records is bridged only when it is smaller than this, and
  a run reaches down to the envelope only when the first record it wants is
  within it. Without the bound, restoring one late record from a 128 MiB blob
  would fetch the whole blob to save a request — a GET budget bought with a
  bandwidth bill.
- **`BudgetBytes`** (64 MiB) caps what is held at once, and one call never
  fetches more than it, so a caller that asks for more than can be held gets
  the front of what it asked for and the rest reads one record at a time.

### 6. Runs outlive the call, and the executor reads ahead in waves

Prefetching only the file being restored is **not enough**, and the measurement
said so before the argument did. Consecutive files share the blob they were
written into, so each fetches the stretch after the last one's: one read per
*(file, blob)* pair, which is one read a file rather than one a blob.

So two things. Runs are kept rather than scoped to the call that fetched them,
evicted oldest-first at the budget. And `Restore/RestoreExecutor` reads ahead
of its own loop in **waves**: a bounded run of upcoming items' manifests
prefetched, read, and every segment they name asked for together, so
everything one blob owes this stretch of the restore is requested at once. A
wave is bounded twice — by items, and by the bytes the reader reports fetching
at half its own budget — so a wave's own runs are never evicted by its own
later ones before the loop consumes them. Each manifest is still read exactly
once: the wave keeps the record it read and the loop takes it.

Without a location source there is nothing to coalesce, so the wave does not
read ahead at all and the footer path reads each manifest at its own turn.

### 7. The plan probe leaves the restore

`Restore/RestoreBlobSet.ResolveAsync` exists to answer two questions: which
blobs a plan needs, and which paths it cannot reach. Both restore paths ran it
purely for the first, to feed a load that no longer happens, and both discarded
the second. So it leaves the restore and stays where it belongs — the plan
verb, where naming unreachable paths *before any byte moves* is the promise
(FR-RST-003).

## Measured

On a twelve-file snapshot over eleven blobs holding sixty records:

| | GETs |
|---|---|
| before this record | 93 |
| after | **11** |
| budget (1.2 × 11) | 14 |

One read per blob, for sixty records. `Repository.Tests/RestoreBreadthTests`
holds it, and holds the bounds separately: a file's segments in one blob are
fetched together, a gap wider than the bridge is two reads and the bytes
between them never cross, and a prefetch over the window becomes several reads
that still serve every record.

## Alternatives

- **Keep the targeted load and coalesce only.** The smallest change, and the
  arithmetic in §1 refuses it: `3(B + M)` before a byte of payload cannot fit
  `1.2B` however well the payload reads merge.
- **Read ahead past the last wanted record to fill the window.** It would make
  a shared blob one read without any wave machinery. Rejected because it and
  the waste bound cannot both be honest: a read-ahead that reaches far enough
  to help would reach across exactly the gaps `MaximumBridgeBytes` refuses to
  bridge, and the refusal would become decorative.
- **Prefetch the whole plan's segments in one call.** The same coalescing with
  no wave. Rejected on memory: a plan is not bounded, and its own runs would
  evict each other before the loop consumed them — worse than no prefetch,
  because the evicted ones are the ones wanted first. The wave is the bounded
  form of the same idea.
- **Persist each blob's format version in the catalogue** so a format-3 read
  could skip the envelope entirely. It would close the sparse case below. Not
  taken here: it is a catalogue schema change for a case the fold already
  covers whenever a restore reads more than the first record of a blob.
- **Trust the catalogue for the four fields and drop the header cross-check.**
  Four fewer comparisons per record. §3 is why not.

## Consequences

**Positive.** A restore's request count is proportional to the blobs it needs
and to nothing else — not to the repository, not to the file count, not to the
record count. The saving is largest exactly where it matters most: a remote
store, where a GET is billed and a round trip is waited on, and a peer
retrieval session, where the same reads cross a domestic uplink.

**Negative.** The footer stops being a second statement of where a record is
on the happy path, so a damaged blob is diagnosed one step later and by a
coarser name until the fallback opens it. A restore now holds buffers it did
not before — bounded by the policy, and stated. And the object-id cross-check
is now doing work that nothing else does, which is why it has a section rather
than a line.

## What this record does not do

- **It does not close the sparse case.** A restore of one small record out of
  large blobs does not fold the envelope — the first record it wants is beyond
  the bridge — so it costs two reads a blob and stays over the budget. That is
  the right trade: the alternative is fetching megabytes to save a request. The
  option that would close it is named in the alternatives above.
- **It does not change what the plan verb costs.** The probe still opens each
  metadata blob through its footer and reads each manifest, because it answers
  a different question and answers it before anything moves.
- **It does not touch the peer retrieval path's own budget.** That is a
  different transport with a different cost model;
  [ADR-0062 Amendment 2](0062-the-destination-is-the-rollback-witness.md#amendment-2--a-staging-set-is-healed-too-bounded-by-the-history-it-lacks-2026-09)
  gave it a lazy chunked read for memory, not for request count.
- **It does not make the catalogue authoritative.** It is still a disposable
  cache; a location that is wrong costs a wasted read and a fallback, which is
  what §2 is for.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | Built over three commits against the characterisation the 2026-08 restore review left behind: the targeted load (`Restore/RestoreBlobSet` lifted out of the service, both restore paths off the whole-store load); the located read (`Repository.Catalogue/Catalogue.ResolveLocation` as the reader's location source, `BlobReader.OpenFramingAsync`, the lazy footer fallback); and the coalescing (`Repository/PrefetchPolicy`, `BlobRun`, the envelope fold, `Restore/RestoreExecutor`'s `PrefetchWave`). 93 GETs to 11 over eleven blobs, against a budget of 14. Two findings the design turned on: a location pointing at a valid neighbour passes every check but the object-id comparison, and prefetching one file at a time costs one read per (file, blob) pair |

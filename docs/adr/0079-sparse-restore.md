# ADR-0079 — A restore skips a hole rather than writing it

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-ARCH-013, FR-RST-002, FR-RST-005
**Related:** [Q22](../open-questions.md#q22--sparse-restore-materialises-zeroes) (closed by this record), [specification 09 §4](../../specifications/repository-format/09-segmentation.md#4-sparse-extents) (which already said holes restore as holes), [specification 06 §4.2](../../specifications/repository-format/06-manifests.md#42-the-whole-file-hash) (the hash covers the zeroes), [ADR-0026](0026-phase-1-capture-shapes.md) (capture records the extents), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification), [restore review RR-7](../review/2026-08-restore-pipeline-review.md#rr-7--the-restore-materialises-sparse-holes-as-written-zeroes)

**Built:**
- The file a restore leaves holes in: `Domain/SparseFile`. It sits beside `Domain/AtomicFile`, because the engine and the standalone recovery tool both write restored files and Domain is the one assembly both may reach.
- The engine's spool and its emission: `Repository/RestoreEngine`. The service's restore reaches it through `Restore/RestoreExecutor`, and the CLI's restore verb through `Cli/CliApplication`.
- The recovery tool: `Recovery/RecoverySession`.
- Capture of a file that is one hole: `Repository/SnapshotPublication`.
- The tests:
  - `Repository.Tests/SparseRestoreTests`
  - `Domain.Tests/SparseFileTests`
  - the allocation oracle both use, `TestSupport/AllocatedSize`

---

## Context

FR-ARCH-013 asks that sparse extents "restore without materialising zero payload", and specification 09 §4 says a restore materialises them "as holes where" the target filesystem supports sparseness. Neither was true. The engine wrote a buffer of zeroes into its spool for every hole and copied the spool out, so a restored sparse file was fully allocated. The recovery tool did the same into its own spool. The August 2026 restore review recorded the disagreement as RR-7 and filed it as Q22, with two resolutions: build sparse write-out, or amend the requirement to say holes restore as zeroes. The owner chose the first.

The cost of the old behaviour was not only disk. A restore of a 100 GiB sparse disk image holding 10 GiB of data wrote 90 GiB of zeroes twice, once into the spool and once into the destination. It also needed 100 GiB free in the temporary directory as well as at the destination, so it could fail on a machine that held the original comfortably.

Every platform this product supports reads zeroes from a range a write skipped:

- **POSIX.** `lseek` past the end followed by a write, and `ftruncate` extending a file, leave a gap that reads as zeroes. Wherever the filesystem supports holes (ext4, XFS, Btrfs, APFS, tmpfs), the gap is left unallocated.
- **Windows.** An extending write or `SetEndOfFile` leaves a gap that reads as zeroes too. But NTFS allocates and zero-fills it, unless the file was marked sparse with `FSCTL_SET_SPARSE` first.

## Decision

### 1. A hole is hashed and skipped, never written

The whole-file hash still covers the zeroes a hole reads as (06 §4.2), so the hash is unchanged and a file restored onto a filesystem without holes still verifies. The writer then moves past the hole instead of writing it, and sets the file's length once the last piece is placed, so a file that ends in a hole has its full length.

### 2. On Windows, a file that will have a hole is marked sparse before anything is written

Only such a file is marked. A dense file restored with the sparse attribute would not be the file that was captured.

The mark is a synchronous ioctl. It is not attempted on an overlapped handle, because that handle's completion would be posted to the thread pool's port, which holds no request for it. The files `SparseFile` creates are therefore opened with a synchronous handle. A file the mark cannot reach is still correct; it is simply allocated. That covers a filesystem without sparse files, such as FAT, and a caller's overlapped handle.

### 3. Both of the engine's writes leave holes

**The spool.** The spool the engine reassembles into is created through `SparseFile` and skips each hole. The hash is verified over the reassembly before anything leaves it (FR-RST-005), as before. Where the spool lives is the caller's to name, the system temporary directory by default; the tests name one so that they can measure it.

**The emission.** After the hash verifies, the engine copies only the spool's data into the destination, each run at its offset from where the destination stands, and then sets the length. It does this only when the destination can seek and holds nothing past its position, because a skipped range keeps whatever it already held. A destination that cannot seek, such as a pipe, or one that already holds bytes where the file will go, is given the whole file with its zeroes written out. Its bytes are the same either way.

A destination that is a file is marked sparse before the copy, on Windows. The executor's spool and the CLI's output are both created with `File.Create`, whose handle is synchronous.

### 4. The recovery tool does the same

It writes its own spool through `SparseFile` and skips each hole. It reaches no engine, by design ([architecture 11 §2](../architecture/11-solution-structure.md#2-dependency-rules)), so it carries its own loop.

### 5. A file that is one hole is captured as one extent

Capture recorded a file's sparse extents only when the file also had data. A file that is a hole from end to end — a disk image fresh from `truncate` — was therefore described by no segment and no extent. That manifest covers none of its length, and the codec refused to encode it. The extents are now recorded whenever the stored data falls short of the length.

## Consequences

**Positive**

- A restored sparse file occupies roughly what its data does, at the destination and in the spool, on Linux, macOS and Windows.
- A restore no longer writes a sparse file's zeroes at all, so it spends neither the disk time nor the temporary space they took.
- A disk image that is one hole backs up and restores.

**Negative**

- The emission seeks once per data run rather than streaming the spool. The runs are the file's own data, so this costs a seek per run and nothing per byte.

**Neutral**

- The hash still covers every zero, so restoring a 1 TiB file that is almost all hole still hashes 1 TiB. The format defines the hash that way (06 §4.2), and only a format change could alter it.

## What this does not do

- **It does not capture holes on Windows.** The scanner still reads an NTFS sparse file densely (`Filesystem.Local/SparseProbe`), so a file captured there restores dense. Its zero runs are stored, deduplicated, as zero segments. Only a manifest that records extents restores sparse.
- **It does not make holes out of zero data.** A run of zeroes captured as data is restored as data.
- **It does not report a target that cannot hold holes.** Specification 09 §4 makes zeroes the expected materialisation there. The file is correct and fully allocated, and nothing is lost.
- **It does not move the engine's spool.** The spool still lives in the system temporary directory unless a caller names another. A dense file larger than that directory's free space still fails to restore. That matters most where the temporary directory is RAM-backed, and it is recorded as a finding (restore review RR-7) rather than changed here.

## Alternatives considered

**Amending FR-ARCH-013 to say holes restore as zeroes** (Q22's option (b)). Declined by the owner. It would have made the requirement true by lowering it, and left the temporary-space failure in place.

**Punching holes after a dense write** (`fallocate(FALLOC_FL_PUNCH_HOLE)`, `FSCTL_SET_ZERO_DATA`). Rejected. It writes the zeroes and then pays again to release them, and it needs a different call on every platform. Skipping needs no call at all on POSIX.

**Writing the reassembly straight into the destination.** Deferred. It would save the spool's copy for every file, sparse or not. But it changes when unverified bytes reach a caller's stream, which this engine's contract forbids (FR-RST-005), so it belongs to a change of its own.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice with its tests, closing Q22 and RR-7 on option (a). |

# ADR-0167 — A format-3 metadata record is read without its blob's envelope

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-009 (amended: a sparse restore out of format-3 metadata blobs the catalogue recorded is within the budget)
**Related:** [ADR-0166](0166-a-format-3-data-record-is-read-without-its-blobs-envelope.md) (the same read for a data record, and metadata blobs named as not done), [ADR-0068](0068-the-catalogue-directed-restore-read.md) (the catalogue-directed read), [ADR-0052](0052-relocatable-records-format-v3.md) (format 3: a record carries its own nonce and is keyed by its object), [ADR-0008](0008-index-generations-and-checkpoints.md) (a catalogue rebuilt from the index plane)

**Built:**
- The catalogue: `Repository.Catalogue/Catalogue`'s `ResolveLocation` answers, with each location, the key generation the writer recorded for its blob, and none for a blob it knows only by a placeholder row or not at all.
- The reader: `Repository.Packing/BlobReader`'s `AssumingRelocatableMetadata` frames a metadata blob as format 3 frames it, at a generation it is told. `Repository/RepositoryReader` reads a metadata record by its location under that framing when the location carries its blob's generation and the envelope is not in hand. As for a data record, the envelope is read only when the record will not read so, and the record is then framed again from the bytes already fetched.
- The tests:
  - `Repository.Tests/SparseRecordReadTests` — a format-3 metadata record read by its location costs one read and opens no envelope, and each further record of its blob one read more; one whose blob the catalogue holds no row of, or whose recorded generation does not open it, is framed from its envelope from the bytes in hand; a format-2 one at no more reads than before; a write-only holder reads one in one read; one that does not verify is still a damage finding
  - `Repository.Tests/CatalogueTests` — a location carries the key generation of a blob the writer recorded, a recorded zero among them, and none of a placeholder row or of a blob with no row

---

## Context

[ADR-0166](0166-a-format-3-data-record-is-read-without-its-blobs-envelope.md) reads a format-3 data record by its location with no envelope read, so a sparse restore costs one read for each data blob it touches. It left metadata blobs out. A format-3 metadata record carries its own nonce, as a data record does. Its key is expanded from the metadata class key by the record's type and object id, and the class key is derived at the blob's key generation. Of everything the envelope says, that generation is the one thing the record does not already give the reader. So a sparse read of a metadata blob still cost two reads: the envelope, for the generation, and then the record. A restore reads a file's version manifest and the trees above it out of metadata blobs before it reads any of its content.

The catalogue already holds the generation. Every blob a backup seals is recorded with its class and its key generation, and so is every blob a forensic rebuild reads a footer of. Only a backup writes metadata blobs: compaction rewrites data blobs alone and refuses a metadata one ([ADR-0067](0067-the-keyless-compactor.md)). So in the catalogue a backup keeps, every metadata blob has its generation recorded. A row the catalogue makes for a blob it has only seen named in a delta is a placeholder: class 0, generation 0, written by nobody who knew.

## Decision

### 1. A location carries its blob's recorded key generation

`ResolveLocation` answers, beside the location, the key generation of the blob's row when a writer or a forensic rebuild recorded it. For a placeholder row it answers none, and for a blob with no row at all, as it already answered no store key. A placeholder's zero is not taken for a generation: a recorded zero is one, and is answered as one.

### 2. A metadata record is first read as format 3 frames it, at that generation

When a metadata blob's framing is not open, no run in hand covers its envelope, and the location carries the blob's generation, a record is read by its location as format 3 frames it at that generation, with no envelope read. The fetch is sized exactly as ADR-0166 sizes one. A record that verifies is the answer, and the blob is read the same way for every record after it.

### 3. Anything else is framed from its envelope, from the bytes in hand

A record that does not read so is read as before. That covers a format-2 record, which is keyed from its blob's salt, and a record written at a generation other than the one recorded. It also covers damage and a location pointing at another record. Each fails closed at its tag. The envelope is then read, the blob framed from it and held for the run, and the record read again from the bytes already fetched, at two reads as before. A location that carries no generation reads the envelope first, as every metadata record did.

### 4. The generation is a hint, never a fact a read rests on

Nothing is taken on trust from the catalogue that an envelope would have proved. A wrong generation derives a key the record does not open under, so it costs a decryption that fails and is never an answer. A record's additional data binds the repository, the format version, the type and the object id however its blob was framed. The header cross-check against the location and the content check of the plaintext run on this path as on every other.

### 5. Any reader that can read a metadata record

The metadata class key is held by a write-only holder as well as by a reader with a grant ([ADR-0042](0042-write-only-repositories.md)). So unlike a data record, a metadata record is read this way by either.

## Consequences

- A sparse restore out of format-3 blobs the catalogue recorded costs one read a blob, data and metadata alike, within NFR-PERF-009's budget, with no change to the catalogue's schema or to the format.
- A restore out of format-2 blobs, or of a metadata blob the catalogue knows only by a placeholder row, costs what it did.
- The plan's read figure still counts each blob's envelope, so a sparse read now reads up to 168 bytes a metadata blob less than its plan says, as ADR-0166 found for a data blob, and never more.

## What this does not do

- **A catalogue rebuilt from the index plane.** A normal rebuild applies deltas ([ADR-0008](0008-index-generations-and-checkpoints.md)), and a delta names blobs without their key generations, so every metadata blob it knows of is a placeholder row. Its reads frame each metadata blob from its envelope, as before.
- **Other copies.** A record read from another copy of the repository (FR-RST-007) frames its blob from that copy's envelope, as before.
- **Format-2 blobs.** They still cost two reads for their first record.

## Alternatives considered

- **Carry each blob's key generation in the index delta.** Rejected. It is a format change to save a read that only a rebuilt catalogue still pays, and the forensic rebuild already records the generation from each blob's own footer.
- **Try every generation the key set can derive.** Rejected. A blob is written at one generation, and the catalogue that located its record has, in the ordinary case, recorded which. Trying others would make a wrong guess cost a decryption for each generation ever used, to save a read the envelope gives at once.
- **Take the newest generation as every blob's.** Rejected. Blobs written before a key rotation keep the generation they were written at, so each of them would pay a failed decryption and then the envelope.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The catalogue answers a location with its blob's recorded key generation, and a metadata record is read by its location as format 3 frames it at that generation. The envelope is read, and the record re-framed from the bytes already fetched, only when the record does not read so. No schema or format change. |

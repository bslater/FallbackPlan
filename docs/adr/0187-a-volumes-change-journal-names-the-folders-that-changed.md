# ADR-0187 — A volume's change journal names the folders that changed

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-011 (new: on Windows, the folders of a set that changed on an NTFS volume since a place in its change journal are named from the journal, every name of a changed file counted, and a place the journal cannot answer for certain is no answer)
**Related:** [architecture 06 §4](../architecture/06-filesystem-capture.md#4-change-detection) (journals are hints only, and a full walk follows any break in one), [ADR-0186](0186-a-run-reuses-a-folder-it-was-told-did-not-change.md) (a run told which folders changed walks only those)

**Built:**
- The answer: `Filesystem/ChangeJournal` says what a place in a journal is and what a journal answers, or that it cannot.
- The records: `Filesystem.Local/UsnRecords` takes apart a read of the journal and says which folders and files its records name.
- The journal: `Filesystem.Local/WindowsChangeJournal` reads an NTFS volume's journal and finds each folder named.
- The tests:
  - `Filesystem.Tests/UsnRecordsTests` — on every platform: a read's next number and records are taken apart; a record of another version, one running past the buffer, or a name running past its record makes the read unreadable; a file's record names its folder and the file, a folder's names itself and its folder, and an entry deleted is known gone; the layout `OpenFileById` reads is the native one.
  - `Filesystem.Tests/WindowsChangeJournalTests` — on Windows, against the volume's own journal: nothing done names nothing; a file written names its folder and no other; a folder made, moved or deleted names the folders around it; a folder's permissions changed names it; a file changed through one name names every folder holding one; a folder outside the roots is never named; a root spelled in another case is still the root; a place in another journal cannot be answered, and neither can a path that is no folder. On every platform: a place the journal no longer holds, has not reached, or is another journal's is not answered.

---

## Context

A run told which folders changed walks only those (ADR-0186). Nothing could tell it yet.

Windows keeps a change journal on each NTFS volume. It records every change to every entry, with the entry's identity and its folder's, whether or not anything is listening. So it holds what changed while the service was not running, across restarts. It is still a hint (architecture 06 §4). It can be deleted and made anew, and its oldest records are let go of as it grows. A place in it that the journal no longer holds tells nothing.

## Decision

### 1. A place in a journal

A place is the volume, by its volume name, which stays the same whatever drive letter the volume has. It also holds the journal's identity, which is new whenever the journal is made anew, and the number of the next record it will write. A folder that is not there, or a volume whose journal cannot be read, has no place.

### 2. What the journal answers

Asked what changed since a place, under a set's roots on that volume, the journal names each folder in which anything changed:

- the folder holding each entry made, removed, renamed or changed, on both sides of a move;
- a folder whose own metadata changed, its permissions among them, and a folder made or moved there; and
- every folder holding a name of a changed file, since a file with several names changes every folder that holds one.

Each folder is named by the root it is under and its path below it, spelled as the volume holds it. A root spelled in another case, or by its short name, is still the root. A folder outside every root is never named.

### 3. What it cannot answer is no answer

The journal answers nothing where:

- it is another journal than the place's;
- it no longer holds every record since the place, or has not come that far;
- a record is of a version this reader does not read, as ReFS's identities are;
- a root is on another volume;
- a folder its records name cannot be found and was not deleted; or
- it names more than 200,000 entries, past which a walk is the cheaper way to find them.

A place older than what the journal holds is not moved up to what it holds, because the records in between are gone. An entry deleted is known gone, so not finding it is no doubt.

### 4. Only an account that may read the volume

Reading a journal takes a handle to the volume, which the system account and an administrator may open. For anyone else there is no journal, and the caller walks in full.

## Consequences

- **A run can learn what changed without walking,** in what the journal says rather than what a walk finds. Nothing asks it yet: the service's use of it, with a full walk on a schedule, is the next record.
- **Every doubt costs a full walk, never a missed change.** A journal made anew, records let go of, a volume of another kind, or a folder that cannot be found each fall back to the walk a run makes today.
- **A changed file's every name is counted,** at the cost of finding each changed file's names.

## What this does not do

- **Tell a run what changed.** The service's use of the journal is the next record. It keeps a place before each walk and walks in full on a schedule, daily by default.
- **Read ReFS's journal,** whose records carry longer identities. A set there walks in full.
- **Name changes on macOS or Linux.** The owner has asked for them once Windows works, each from its own platform's source.

## Alternatives considered

- **Watch for changes while the service runs.** A watcher misses everything while the service is not running, and overflows under load. The journal is the volume's own, and keeps what a watcher would miss.
- **Move a place up to the oldest record the journal holds.** The records between are gone, so what changed then is unknown. A full walk is the only honest answer.
- **Name only the folder a record names.** A file changed through one of its names would keep its old version under the others, until a full walk.

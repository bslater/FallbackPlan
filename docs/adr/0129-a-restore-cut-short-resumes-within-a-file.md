# ADR-0129 — A restore cut short resumes within a file

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: a file cut short mid-write is resumed from the part its run had assembled)
**Related:** [ADR-0109](0109-a-restore-cut-short-is-resumed.md) (a restore cut short is resumed, and the record that left a file cut short mid-write to be restored whole), [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md) (the service names each run, resumes it by its name alone, and lets it go), [ADR-0075](0075-a-restore-reads-around-damage.md) (a restore reads around damage), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The engine: `Repository/RestoreEngine` assembles a file in a part a caller names, and keeps the part unless the file is emitted or its assembly is refused. Told to take a part up, it proves each segment of it against its object identifier, which `Repository/RepositoryReader` derives, and each hole by reading as zeroes, and reads from the store only what the part lacks.
- The run: `Restore/RestoreExecutor` names each file's part by its whole-file hash in the parts directory its options give, takes a part up only on a resume, and reads nothing ahead for a file whose part it will take up.
- The service: `Agent/RestoreParts` keeps each run's parts under the state directory by the run's name. `Agent/ServiceCommandHandler` gives each run its folder, carries it to the name a resume runs under, and lets it go when the run finishes or is let go. `Agent/ServiceRuntime` sweeps away the parts of any run no longer on record when it starts.
- The tests:
  - `Repository.Tests/RestoreWithinAFileTests` — a resume takes up the part of a file cut short and reads only what it lacks; a part is taken up only as far as its segments prove; a run that is not a resume takes up no part; a run stopped in a file keeps what it had assembled for a resume to take up; a segment that will not read leaves the part as far as it was assembled; an assembly that does not hash to the file lets its part go; a hole in a part is taken up only where it reads as zeroes
  - `Hosts.Tests/RestoreUnfinishedServiceTests` — a resume takes up the parts its run had assembled, reading none of them again, and only a part that proves; a resume in place carries its run's parts to the name it runs under; a run let go takes its parts with it; the parts of a run no longer on record go when the service starts

---

## Context

A restore cut short is resumed: each file already holding the snapshot's content where the run lands it is left as it stands, and the rest are restored ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)). A file cut short mid-write never landed, so it was restored whole. The engine assembled each file in a spool of its own, in the system's temporary folder, and deleted the spool however the file ended. For a large file, that is all of it read from the store again, however little was left. ADR-0109 recorded this as undone.

What the spool held was never in question. Each segment was verified before it was written there (FR-RST-005), and a segment's object identifier derives from its content under the repository's content-ID key. A segment the spool holds can be proved again without the store.

## Decision

### 1. A file is assembled in a part, which is kept unless the file lands or is refused

A run told where to keep its parts assembles each file in a part named by the file's whole-file hash. The part goes when the file is emitted, and when its assembly does not hash to the file, since nothing could resume from that. Otherwise it is kept: a run stopped in the file, or one that a segment would not read for, leaves what it had assembled.

### 2. A resume takes up a part as far as it proves

A resume takes up a file's part before it reads anything of the file, in the file's order:
- **A segment** is taken where the part holds all of it and its bytes derive the object identifier the manifest names, the proof every record read from a store is held to.
- **A hole** is taken where the part holds all of it and it reads as zeroes. Where the destination cannot keep a hole, the part's bytes are what is emitted there.
- **Nothing past the first piece that is not taken** is taken. The part is cut back to the end of the last piece taken.

What it takes is hashed as the rest will be, and only the rest is read from the store, ahead of the loop or in it. The whole-file hash is still checked over all of it before anything is emitted.

A run that is not a resume takes up no part, as it leaves no file in place: without being told, a run cannot know the part is its own.

### 3. Through the service, a run's parts are kept under its name

The service keeps each run's parts in `restore-parts/{run}` under its state directory. The plaintext stays where the engine's spool always kept it: off the folder a person restores to. A resume that runs under a new name, as one in place does, carries the parts of the run it continues to that name.

A run's parts go when it finishes or is let go. When the service starts, the parts of any run no longer on record go too. Each step is best effort: a part not carried is assembled again, and one not removed goes at the next start.

> **2026-10 ([ADR-0131](0131-the-service-measures-a-restores-working-space-where-its-parts-are-kept.md)).** The room a run needs to assemble its largest file is measured here too: the service's working space is the folder its runs keep their parts in, and no longer the system's temporary folder.

## Consequences

- A large file cut short near its end is read again only from where its run stopped.
- A run left unfinished holds, under the state directory, as much of the file it was cut short in as it had assembled, until it is resumed or let go. Its notice is what says the run is there.

## What this does not do

- **Resume within a file in direct mode.** A run in direct mode keeps no parts, and a file it was cut short in is restored whole, as before.
- **Measure what a part saves.** A resume's plan still counts each file it writes whole, so what it says the run will read errs high.
- **Say in the receipt that a file was resumed within.** The receipt counts the file restored, as it counts any file the run wrote.

  > **2026-10 ([ADR-0130](0130-a-restore-says-which-files-it-resumed-within.md)).** It says so now. The receipt, at schema 11, says where each file a resume wrote from its part was resumed, and a restore's result counts those files (contract 1.76).

  > **2026-10 ([ADR-0130](0130-a-restore-says-which-files-it-resumed-within.md)).** It says so now. The receipt, at schema 11, says where each file a resume wrote from its part was resumed, and a restore's result counts those files (contract 1.76).

## Alternatives considered

- **Keep the part beside the file it becomes.** Rejected. The folder a person restores to would hold the backup's plaintext under a name nobody chose, and a run in place would leave it among their files.
- **Trust a part's length.** Rejected. A restore is a claim about content (FR-RST-002), and a part long enough is not the file's beginning.
- **Read the part's segments from the store to compare.** Rejected. Proving a segment against its object identifier is the same proof a read from the store makes, without the read.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A file is assembled in a part named by its whole-file hash, kept unless it lands or is refused; a resume takes it up as far as each segment proves against its object identifier and each hole reads as zeroes, and reads only the rest. The service keeps each run's parts under its name, carries them to a resume's, and lets them go when the run finishes or is let go, and at start for runs no longer on record. No contract or receipt change. |
| 2026-10 | Accepted | The receipt says where a file was resumed within, by [ADR-0130](0130-a-restore-says-which-files-it-resumed-within.md): at schema 11, with a restore's result counting those files at contract 1.76. What this does not do no longer leaves the receipt silent on it. |
| 2026-10 | Accepted | The receipt says where a file was resumed within, by [ADR-0130](0130-a-restore-says-which-files-it-resumed-within.md): at schema 11, with a restore's result counting those files at contract 1.76. What this does not do no longer leaves the receipt silent on it. |
| 2026-10 | Accepted | The working space is measured where the parts are kept, by [ADR-0131](0131-the-service-measures-a-restores-working-space-where-its-parts-are-kept.md): a run through the service is measured for the room its parts need on the state directory, not in the system's temporary folder. |

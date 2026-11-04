# ADR-0190 — On Linux the service watches a set's folders itself

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-011 (amended: on Linux, the folders of a set that changed since a place in the service's own watch of them are named from that watch, and a place the watch cannot answer for certain is no answer); FR-SNP-012 (amended: the service asks that watch on Linux as it asks the volume's journal on Windows)
**Related:** [ADR-0187](0187-a-volumes-change-journal-names-the-folders-that-changed.md) (a volume's change journal names the folders that changed, on Windows), [ADR-0188](0188-the-service-tells-a-backup-what-changed-since-the-sets-last.md) (the service tells a backup what changed since the set's last), [ADR-0189](0189-a-sets-full-walk-is-a-setting-of-the-set.md) (a set's full walk is a setting of the set), [architecture 06 §4](../architecture/06-filesystem-capture.md#4-change-detection) (journals are hints only, and a full walk follows any break in one)

**Built:**
- The watch: `Filesystem.Local/LinuxChangeJournal` keeps an inotify watch of every folder a set asks about, and answers what the change journal answers.
- The service: `Agent/ServiceRuntime` gives every backup the process's one watch on Linux.
- The tests:
  - `Filesystem.Tests/LinuxChangeJournalTests` — on Linux, against real folders:
    - nothing done names nothing;
    - a file written names its folder and no other, and nothing outside the root; a file in the root names the root;
    - a folder made names it with everything below it, and is watched from then;
    - a folder moved names where it was and where it is and goes on being named there, and one moved out is watched no longer;
    - a folder deleted names the folder that held it, and a folder's permissions changed names it.
    - Each of these cannot be answered: a file changed that has another name; a place from another watch, another session, or ahead of the watch; a queue that overflowed, though a place after it can be; a place older than the watch keeps, though a later one can be; a root deleted.
    - A folder not there has no place, and says why. Asking twice gives one watch.
  - `Hosts.Tests/LinuxChangeWatchRunTests` — on Linux, the runtime gives backups the process's watch, and a backup after another walks only the folder that changed.

---

## Context

On Windows a backup is told what changed since the set's last one, from the volume's change journal (ADR-0187, ADR-0188). The owner asked for the same on every other platform once Windows worked. Linux keeps no journal a program can read after the fact. What it has is inotify: a process asks to watch a folder, and the kernel queues an event for each change in it while the watch is held.

So the journal on Linux has to be the service's own, and it lasts only as long as the process holds it. The questions the service asks stay the same:

- Where does the journal stand now for this folder?
- What changed under these folders since that place?

## Decision

### 1. The first place asked for a folder starts watching it

A folder is watched once something asks where the journal stands for it. Watching means a watch on the folder and on every folder below it on the same filesystem. Capture does not walk into another filesystem below a root, so the watch does not either.

The watching is done before the place is given. The backup that takes the place walks in full, since it has nothing earlier to ask from, so every change after the place is heard.

One tree of watches serves every set. inotify gives one watch per folder, so a set whose folders lie inside another set's joins that set's tree rather than watching them twice.

### 2. A place is good only to the watch that gave it

Each root has a session, a random number given when it is first watched. A place carries it.

- A service started again has new sessions, so its first backup of each set walks in full.
- Anything that loses track gives the roots it touches a new session. A place from before is then answered with doubt.

The watch keeps up to 100,000 changed folders. Past that it lets go of the older half, and a place from before them is doubt rather than silence.

### 3. What a change names

- **A change names the folder it happened in:** a file made, written, closed after writing, moved, deleted, or its metadata changed.
- **A folder made or moved in is named whole,** every folder below it too, since nothing watched it as it was filled. It is watched from then.
- **A folder moved within the watch goes on being named where it is.** A folder moved out is watched no longer.
- **A folder gone is named by the folder that held it,** not where it was.
- **A set's root moved or deleted is forgotten.** Its next place watches it afresh, or has none.

### 4. What is doubt

- **A changed file with another name.** Linux keeps no list of a file's names, so its other folders cannot be found, and a backup would take them as they were.
- **A queue that overflowed.** Changes were lost.
- **A name that is not UTF-8.** Its folder could not be named as a walk names it.
- **A watch that cannot be read.**

### 5. What has no place

- **A root over the system's limit on watches** (`fs.inotify.max_user_watches`) has no place until the service starts again. Its backups walk in full, and the reason is kept.
- **A root on a filesystem changed from elsewhere** has no place: NFS, SMB, FUSE and the like, where no watch here hears the other machine's changes.
- **A folder that is not there** has no place.

## Consequences

- **On Linux, a backup after the set's last one lists only the folders the watch named,** and the folders above them, as on Windows.
- **A backup after the service starts walks in full,** unlike Windows, whose journal outlasts the service. So does a backup after the watch lost track.
- **Some changes go unheard until the full walk:**
  - a file written through a memory mapping, which inotify does not report;
  - a change to a hard-linked file through a name outside the set, which no watch of the set's folders hears, as on Windows (ADR-0187 Amendment 1);
  - a change through a name inside the set to a file whose other name was made and removed before the watch could look;
  - a change made on a lower layer of an overlay filesystem.
- **The process holds one inotify instance and a watch per folder of every set,** read by one background thread.

## What this does not do

- **Watch on macOS.** FSEvents keeps a journal of its own, as Windows does, and is the next record.

  > **2026-11 ([ADR-0191](0191-on-macos-the-service-asks-the-volumes-event-history.md)).** On macOS the service asks that history and watches nothing.
- **Watch beyond the process.** A change made while the service is stopped is found by the first backup after it starts, which walks in full.
- **Let go of a set's watches when the set is deleted.** They go when the service stops, or when the folders do.
- **Watch with fanotify.** It needs privileges the service may not hold, and its whole-filesystem marks hear every other program too.

## Alternatives considered

- **fanotify with filesystem marks.** No limit on watches and no tree to keep, but it needs CAP_SYS_ADMIN, and every event on the filesystem must be resolved to a path to know whether a set cares.
- **Keep the watch across restarts.** Nothing records what happened while no process watched. A place kept on disk could not be answered for certain, and a guess would be silence.
- **Watch lazily, after the place is given.** The backup that took the place would walk while the watch was still being set up, and a change in a folder not yet watched would be lost with nothing to say so.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first. The whole Hosts suite passes on Linux with the watch on by default. |
| 2026-11 | Accepted | Noted by [ADR-0191](0191-on-macos-the-service-asks-the-volumes-event-history.md): macOS is asked from the volume's event history, not watched. |

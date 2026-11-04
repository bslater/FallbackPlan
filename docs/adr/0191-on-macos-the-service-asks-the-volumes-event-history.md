# ADR-0191 — On macOS the service asks the volume's event history

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-011 (amended: on macOS, the folders of a set that changed since a place in the volume's event history are named from that history, and a place it cannot answer for certain is no answer); FR-SNP-012 (amended: the service asks that history on macOS as it asks the volume's journal on Windows)
**Related:** [ADR-0187](0187-a-volumes-change-journal-names-the-folders-that-changed.md) (a volume's change journal names the folders that changed, on Windows), [ADR-0188](0188-the-service-tells-a-backup-what-changed-since-the-sets-last.md) (the service tells a backup what changed since the set's last), [ADR-0190](0190-on-linux-the-service-watches-a-sets-folders-itself.md) (on Linux the service watches a set's folders itself), [architecture 06 §4](../architecture/06-filesystem-capture.md#4-change-detection) (journals are hints only, and a full walk follows any break in one)

**Built:**
- The history: `Filesystem.Local/MacChangeJournal` asks the volume's FSEvents history, and answers what the change journal answers.
- What a replay names: `Filesystem.Local/FsEventsRecords`, a rule over the events with no platform call in it.
- The service: `Agent/ServiceRuntime` gives every backup the volume's history on macOS.
- The tests:
  - `Filesystem.Tests/MacChangeJournalTests` — on macOS, against the real history of the volume the temporary folder is on:
    - nothing done names nothing;
    - a file written names its folder and no other, and nothing outside the root;
    - a folder made names it; a folder moved names where it is and a path where it was; a folder deleted names a path in the folder that held it; a folder's permissions changed names it.
    - Each of these cannot be answered: a file changed that has another name; a place in another history; a place ahead of the history.
    - A folder not there has no place, and says why.
  - `Filesystem.Tests/FsEventsRecordsTests` — on every platform, over events built by hand:
    - a file or a link names the folder holding it, and anything else itself;
    - a path is named under every root it lies below and under none it does not, and as it is spelled on disk;
    - the mark's folder is never named;
    - each way the history says it lost track is doubt, as are a file with another name, and a path that cannot be spelled on disk or is spelled outside every root;
    - a place's history is the volume's identity.
  - `Hosts.Tests/MacChangeHistoryRunTests` — on macOS, the runtime gives backups the volume's history, and a backup after another walks only the folder that changed.

---

## Context

On Windows a backup is told what changed since the set's last one, from the volume's change journal (ADR-0187, ADR-0188). On Linux the service keeps a watch of its own, since Linux keeps no journal (ADR-0190). The owner asked for the same on every platform once Windows worked; macOS is the last.

macOS keeps a history of every change on a volume, FSEvents', whether or not anything listens. The system writes it to the volume and numbers every event. Any program may ask for the events under some folders since a number, and the system replays them and then goes on with new ones. That is the Windows shape, not the Linux one, so the service asks it rather than watching. The questions stay the same:

- Where does the journal stand now for this folder?
- What changed under these folders since that place?

## Decision

### 1. A place is the volume's history and an event number

- **The history:** the system gives each volume's history an identity, and a new one whenever it starts the history afresh. A place carries it as the volume and as the journal's identity.
- **The number:** the latest event the system has numbered on any volume.
- **No place:** a folder that is not there, or on a volume that keeps no history.

A change made a moment before the place is taken may be numbered after it. That is safe: the backup taking the place walks after it, and the next backup is told of the change again. A walk too many is the only cost.

### 2. What changed is replayed, then confirmed with a mark

The service opens a stream over the set's roots from the place's number and takes every event until the system says the replay is done.

The system numbers a change a moment after it is made, so a replay could end before a change made just before the question is heard. So the service then writes a mark of its own, a file in a folder it keeps and the stream also watches. It gives no answer until the mark comes back. By then every change made before the mark has been numbered and heard.

A replay that takes longer than two minutes, or a mark that does not come back within thirty seconds, is doubt.

Each root is given to the stream as the system spells it, with no link above it, and the paths heard are taken back to the root as the set names it. Names are matched without regard to case, as the volumes macOS makes compare them. A folder beside a root that differs from it only in case, on a volume that does regard case, is named too. That costs a walk, never a change missed.

### 3. A folder is named as it is spelled on disk

The run's folder reuse compares the paths it is told of exactly, as the walk spells them, which is as they are on disk. Nothing promises the history heard a folder in its own case. A folder told of in another case would be taken as unchanged, and a change in it missed.

So each folder named is resolved before it is told: the nearest of it and the folders above it still there, as the system gives its names on disk, and the rest as heard. A folder gone names nothing a walk enters, so the case of what is gone does not matter. A folder that cannot be resolved, or resolves outside every root, is doubt. Each is resolved once per answer.

### 4. What a change names

- **A file or a link made, written, renamed, deleted, or whose metadata changed** names the folder holding it.
- **A folder made, renamed, deleted, or whose metadata changed** names itself. A run walks every folder above a path it is told of, so a folder gone or moved away is walked from the folder that held it.
- **Nothing outside the set's roots is named.** The mark's folder is never named.

### 5. What is doubt

- **A place in another history.** The root's volume keeps a different history now, or the place's identity is not this history's.
- **A place ahead of the history.** Its number is past the latest.
- **A history that lost track.** The system says events were dropped, the numbers wrapped, the folders must be scanned again, a root moved, or a volume was mounted or unmounted under a root.
- **A changed file with another name.** The history does not name the file's other folders, and a backup would take them as they were. A file is taken as having another name where the system marks it so, or where it has more than one name when asked.
- **A root that is not a folder, or whose spelling cannot be resolved.** So too a folder named that cannot be spelled as it is on disk, or is spelled outside every root.
- **A replay or a mark not in time.**

## Consequences

- **On macOS, a backup after the set's last one lists only the folders the history named,** and the folders above them, as on Windows and Linux.
- **The history outlasts the service,** as the journal does on Windows. A backup after the service starts again asks from the place its last backup kept.
- **Every folder named is resolved on disk once per answer,** a few calls to the system for each, before the run is told of it.
- **Every question waits for a mark,** a file written and removed in a folder of the service's own. Each root of a set is asked on its own, so a set of several roots writes a mark for each.
- **Some changes go unheard until the full walk:** a change to a hard-linked file through a name outside the set, which no stream over the set's folders hears, as on Windows (ADR-0187 Amendment 1).

## What this does not do

- **Watch.** Nothing listens between backups. The system keeps the history whether or not anything does.
- **Keep the history.** How long the system keeps it is the system's to say. The service trusts the system to start a new history, or to say it lost track, when it no longer holds every event since a place. A history pruned without either would be believed until the set's next full walk.
- **Ask a volume that keeps no history,** such as a network share or a volume the system was told not to record. Its backups walk in full.

## Alternatives considered

- **Answer once the replay is done.** A change made just before a backup, not yet numbered, would be left to the next backup: the one asked would not walk its folder, though the change was made before it began. The mark makes a backup hold every change made before it, as the journal on Windows does.
- **Wait a fixed time before answering.** The system gives no bound on how long it takes to number a change, so no wait is certain, and every wait costs every backup.
- **Fold case where the run compares the paths it is told of.** The walk and the snapshot spell every path as it is on disk, and folding would be a change to every run on every platform, which on a volume that regards case would take two folders for one. Resolving what the history names keeps the rule with the source that needs it.
- **Watch as on Linux.** The history already holds what a watch would, and it outlasts the service, so a watch would only lose the first backup after a start.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first. The history is asked against the macOS build job's real volume. |

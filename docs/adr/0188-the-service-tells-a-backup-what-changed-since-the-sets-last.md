# ADR-0188 — The service tells a backup what changed since the set's last

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-012 (new: a backup the service runs is told what changed since the set's last backup, from where the change journal stood before that backup walked, and walks in full where it has nothing to ask from, where the journal cannot say, where the set's folders changed, where a full walk is asked for, and at least once a day)
**Related:** [architecture 06 §4](../architecture/06-filesystem-capture.md#4-change-detection) (journals are hints only, and a reconciliation walk is mandatory), [ADR-0186](0186-a-run-reuses-a-folder-it-was-told-did-not-change.md) (a run told which folders changed walks only those), [ADR-0187](0187-a-volumes-change-journal-names-the-folders-that-changed.md) (a volume's change journal names the folders that changed)

**Built:**
- The plan: `Agent/ChangeHints` says what a backup is told and what it keeps for the next, and keeps it by set.
- The run: `Agent/BackupRunner` takes the journal's places before it walks, tells the walk what changed, keeps the places once the backup has published, and says how many folders it did not walk.
- The journal: `Agent/ServiceRuntime` gives each backup the volume's change journal on Windows, and none elsewhere.
- The tests: `Hosts.Tests/ChangeJournalRunTests` — through a scripted journal, on every platform: a first backup walks in full and asks nothing; the next asks from where that one stood before it walked, and walks only what the journal names; a change the journal does not name waits for the full walk; a journal that cannot say, a volume that keeps none, a full backup asked for, a set whose folders changed, a damaged kept place, or a backup between that kept no place each walk in full; a day after the last full walk the run walks in full, and asks again after; a folder the journal names is spelled as the snapshot spells it, after its label in a set of several folders.

---

## Context

A run told which folders changed walks only those (ADR-0186), and on Windows a volume's change journal can say which changed since a place in it (ADR-0187). Nothing joined them. Every backup the service ran still walked in full.

Architecture 06 §4 sets the terms. A journal is a hint. A full walk stays mandatory, on a schedule and after any break in the journal. Prior art that keeps a journal's place between backups shows where it goes wrong:

- reading the place after the walk, which loses what changed during it;
- moving a place the journal no longer holds up to what it holds;
- not walking in full when the settings changed; and
- never walking in full at all.

## Decision

### 1. A backup takes the journal's places before it walks

Before it walks, a backup asks where the journal stands for each of the set's folders. A change made while it walks is after that place, so the next backup hears of it. A folder whose volume keeps no journal has no place, and then the backup keeps none for the set.

### 2. The places are kept once the backup has published

Once its snapshot has published, the backup keeps three things for the set: that snapshot, when the set was last walked in full, and the places it took. A backup that failed or was cancelled keeps nothing, so the next asks from the last backup that published. The file is one per set under the service's state. A file that does not read is no place.

### 3. The next backup asks from there, or walks in full

A backup asks the journal what changed since the places kept and is told the folders it names, under the set's folders and spelled as the snapshot spells them. It walks in full, told nothing, where:

- the set has no backup yet, or a full backup was asked for;
- nothing was kept, or what was kept names another backup than the set's last;
- the set's folders are not the ones the places were kept for;
- the journal cannot say for one of them (ADR-0187 §3); or
- a full walk is due.

ADR-0186 still reuses nothing where the rules or the folders changed, whatever it is told.

### 4. A full walk at least once a day

A set is walked in full once a day, whatever the journal says. A full walk is a backup that was told nothing, and it is when the kept time moves on. A clock that went back is a doubt too, and walks in full.

> **Amended 2026-11 ([ADR-0189](0189-a-sets-full-walk-is-a-setting-of-the-set.md)).** The day is the default. A set may name its full walk in its schedule's grammar, `every 7d` or `daily at 02:00`, and the first backup after it falls due walks in full. One that does not read walks every backup in full.

### 5. The run says what it did not walk

A backup that reused folders says so in its outcome: `… file(s), … unchanged, … folder(s) not walked`. Its files are the ones it walked. A full walk says what it always has.

## Consequences

- **On Windows, a backup after the set's last one lists only the folders the journal names** and the folders above them. The rest it takes as the last backup held them. The journal is read from the volume, so this holds across restarts and over changes made while the service was not running.
- **A change the journal did not record waits at most a day,** until the next full walk. Every doubt walks in full, and costs the walk a backup makes today.
- **A backup's file count is what it walked.** It says how many folders it did not walk, so a smaller count is not mistaken for files gone.
- **Elsewhere nothing changes.** Linux and macOS have no change source yet, and every backup there walks in full.

## What this does not do

- **Let a person set how often a set is walked in full.** The day is fixed. A setting for each set, in the console and on the command line, and the last full walk shown in status, are the next record.

  > **2026-11 ([ADR-0189](0189-a-sets-full-walk-is-a-setting-of-the-set.md)).** A set's full walk is a setting of the set, carried at contract 1.82, and the command line's status shows the last full walk. The console's editor and status are the next record.
- **Tell a backup in direct mode what changed.** The command line's backup walks in full.
- **Name changes on macOS or Linux.** The owner has asked for them once Windows works.
- **Let go of a deleted set's kept places.** The file stays until the set's id is used again, and it names a backup no set holds.

## Alternatives considered

- **Keep the places in the catalogue.** The catalogue can be rebuilt from the store, which holds no places. A place lost costs only a full walk, so a file beside the service's other state is enough.
- **Keep the places in the snapshot.** A snapshot records what was captured, and goes to every destination. A journal's place belongs to this machine's volume and to nothing a restore reads.
- **Walk in full only after a break in the journal.** Architecture 06 §4 makes a scheduled walk mandatory. It is what catches what a journal can miss without saying so.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first, joining folder reuse to the change journal on Windows. |
| 2026-11 | Accepted | Amended by [ADR-0189](0189-a-sets-full-walk-is-a-setting-of-the-set.md): the day is the default full walk, and a set may name its own in its schedule's grammar. |

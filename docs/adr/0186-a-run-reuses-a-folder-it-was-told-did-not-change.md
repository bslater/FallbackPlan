# ADR-0186 — A run reuses a folder it was told did not change

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-010 (new: a run told which folders changed since its prior snapshot walks only those and the folders above them, and takes every other folder as that snapshot held it; told truly, it makes the snapshot a full walk would); NFR-PERF-003 (unchanged: an unchanged file is not read, whether its folder is walked or reused)
**Related:** [architecture 06 §4](../architecture/06-filesystem-capture.md#4-change-detection) (change journals and watchers are hints only, and a reconciliation walk is mandatory), [ADR-0151](0151-the-catalogue-keeps-a-folders-entries-once-under-its-tree.md) (a folder's rows are kept by tree, and forgetting a snapshot lets go of what nothing else reaches), [ADR-0099](0099-a-folder-that-was-only-read-keeps-its-tree.md) (a folder only read keeps its tree), [ADR-0040](0040-multi-root-backup-sets.md) (a set of several folders names each by its label)

**Built:**
- The job: `Repository/SnapshotPublication` takes the folders a run is told changed, and names in its result how many folders it reused.
- The walk: `Filesystem/ScanModel` asks of each folder whether the run takes it as it was; `Filesystem.Local/LocalFileSystemSource` does not enter one it does, and `Filesystem/MultiRootScan` names it under its label.
- The decision: `Repository/FolderReuse` says which folders are walked and which reused.
- The reads: `Repository.Catalogue/Catalogue` reads a reused folder whole in one read, and `Repository/TargetedRecordReader` reads the prior snapshot's manifest, the configuration it was captured under, and the paths it failed.
- The tests:
  - `Repository.Tests/FolderReuseTests` — a folder not named changed is reused without being walked; a run told truly makes the snapshot a full walk would; a change the run was not told waits for the next walk; a folder holding a file the prior snapshot failed is walked, as is a folder the prior snapshot does not hold; without a prior snapshot, or under other rules or other folders, every folder is walked; a reused folder's trees are part of the new snapshot; a folder under a label is reused; a reused folder stays in the catalogue though the prior snapshot is forgotten during the run; the catalogue reads a folder whole, and not one whose tree would split.
  - `Filesystem.Tests/LocalScanTests` — a folder the run says is unchanged is named and not entered.
  - `Filesystem.Tests/MultiRootStitchTests` — a folder not walked is named under its root's label.

---

## Context

Every backup walks every folder of its set: it lists each folder and reads each entry's metadata. An unchanged file is not read (NFR-PERF-003), but it is still found. That walk is the floor of every run, however little changed, and on a large set it is most of the run.

Architecture 06 §4 already says how to go below that floor. A platform's change journal or watcher can say which folders changed since a moment. It is only a hint: it overflows, resets, and misses what changed while nothing was listening. So a full walk stays mandatory, on a schedule and after any break in the journal.

Nothing could use such a hint. The publication had no way to take a folder without walking it. This record builds that half, so that a change source has an engine to tell.

## Decision

### 1. A run may be told which folders changed

A job may name the folders in which anything may have changed since its prior snapshot: an entry added, removed or renamed, a file's content or metadata, or the folder's own metadata. A file with several names changes every folder that holds one. Paths are as the snapshot spells them, the root is the empty string, and in a set of several folders a path begins with the folder's label.

A job that names nothing walks every folder, as before. A job told an empty list was told nothing changed.

### 2. The run walks what it was told of, and reuses the rest

The run walks a folder, listing it and asking of each folder in it in turn, where:

- the folder, or a folder below it, is named changed;
- the prior snapshot failed to capture a path at or below it, so the file is tried again;
- the prior snapshot holds no folder there; or
- the folder, or a folder below it, has more entries than one tree holds, so its tree was written as a chain whose continuations the catalogue does not name.

The root is always walked. Every other folder is reused. The walk does not enter it. Its parent's tree names the tree the prior snapshot recorded for it, and the new snapshot is made of every tree at and below it. Naming a folder says nothing of the folders in it, each of which is asked in its own right.

### 3. Nothing is reused where the set's configuration changed

A change source sees the files, not the set. A folder the set's new rules cut into, or a label that now names another folder, is one the prior snapshot holds otherwise. So the run reuses nothing where the prior snapshot was captured under other include or exclude rules, or other folders or labels, as its recorded configuration says.

The run also reuses nothing where it has no prior snapshot, no catalogue, or cannot read the prior snapshot's manifest, its configuration or the paths it failed. The other switches of a walk are not configurable, so they cannot change between runs.

### 4. A reused folder is held as a walked one is

A reused folder's catalogue rows are read whole, the folder's and every folder's below it, with the access times their trees state. That is one read, so a snapshot forgotten on another connection is forgotten wholly before it or wholly after (ADR-0151 §4).

The run records those rows again with the folders it walked, after its own snapshot's row. Forgetting the prior snapshot while the run goes on can let go of a reused folder's rows, since nothing else reaches them yet. The run records them again, as it records a walked folder's.

### 5. Told truly, the run makes the snapshot a full walk would

A folder reused is the tree a walk would have written, since nothing in it changed. So a run told truly publishes the same root tree a full walk would.

A run told falsely misses what it was not told of. The change waits for a run that walks that folder, which is one told of it or a full walk. That is the price of not walking, and the reason the full walk stays on its schedule.

### 6. The run counts what it walks

The count a run takes before capture, for progress, walks the same folders. It counts the files it walked and none in a reused folder. The run's result says how many folders it reused.

## Consequences

- **A run told little lists only the folders on the way to what changed.** A reused folder costs a catalogue read in place of a walk, and its rows are held until the run records them, as a walked folder's are.
- **A false hint is a missed change until the next full walk.** The engine trusts what it is told. Telling truly, and walking in full on a schedule and after any doubt, is the change source's work.
- **A reused folder's trees are part of the new snapshot.** Damage to one reaches every snapshot made of it, and forgetting the snapshot that first wrote it lets go of nothing the new one holds.
- **A hard link's group is the same whether its folder is walked or reused,** since a group is derived from the file's identity alone.

## What this does not do

- **Tell a run what changed.** No change source exists yet, so every production run is told nothing and walks every folder. The Windows change journal is the next record, with a full walk on a per-set schedule, daily by default, and after any break in the journal.

  > **2026-11: done on Windows ([ADR-0187](0187-a-volumes-change-journal-names-the-folders-that-changed.md), [ADR-0188](0188-the-service-tells-a-backup-what-changed-since-the-sets-last.md)).** The volume's change journal names what changed, and the service tells each backup what it names since the set's last, walking in full at least once a day and on any doubt.
- **Say in the snapshot whether it was walked in full.** The change source owns its schedule, so it keeps when it last walked in full.
- **Reuse a folder whose tree was written as a chain.** Such a folder, and every folder above it, is walked.
- **Reuse a folder under other rules or other folders.** The first run after a change to either walks in full.

## Alternatives considered

- **Reuse a file rather than a folder.** A file is already reused without being read. What a hint saves is the listing, and only a folder can be passed over unlisted.
- **Let a named folder stand for everything below it.** A change source names the folder that changed, not its parents or children. Walking everything below each named folder would walk most of a set for a change near its root.
- **Read the prior trees back from the store.** The catalogue already holds what each folder's tree holds, by tree (ADR-0151). Reading the trees would cost a fetch for each, for what the catalogue answers locally.
- **Compare configuration by a digest the change source keeps.** The prior snapshot already records what it was captured under, and the engine reads it. A change source cannot then forget to.

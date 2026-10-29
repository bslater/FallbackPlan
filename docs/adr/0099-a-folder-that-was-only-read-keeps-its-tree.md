# ADR-0099 — A folder that was only read keeps its tree

**Status:** Accepted
**Date:** 2026-10
**Requirements:** NFR-PERF-003 (amended: a folder whose only change is its access time keeps the tree it had, as a file whose only change is its access time keeps its version); NFR-PERF-005 (a backup that changed nothing writes no tree)
**Related:** [architecture 06 §4.3](../architecture/06-filesystem-capture.md#43-unchanged-bytes-are-not-an-unchanged-file), whose file rule this extends to folders; [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md), which restores a folder's access time; [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md), whose note on a reused file's access time now holds for folders too; [specification 06 §5](../../specifications/repository-format/06-manifests.md#5-tree-manifest)

**Built:**
- The rule: `Repository/SnapshotPublication` closes each folder by asking the catalogue for the prior snapshot's tree at the same path and the access time it states. Where only the access time differs, it states the prior one, so the tree is the prior object. The identity is derived without appending (`Repository/ManifestBuilder`'s identifier derivation, and the chain writer's head identity, in the same file as the publication).
- The memory: `Repository.Catalogue/Catalogue` at schema 11 (`Repository.Catalogue/CatalogueSchema`) keeps the access time each folder's head tree states, keyed by the tree. The capture writes it for every folder it publishes; `Repository/CatalogueProjector` and `Repository.Catalogue/Forensic/ForensicRebuilder` write it for every head tree they decode.
- The tests:
  - `Repository.Tests/EndToEnd/FolderAccessTimeTests`: a backup after one that only read its folders keeps every tree, the root's included, and the kept root states the time it was captured with; a folder whose mode changed, or that holds a changed file, gets a new tree stating the time it has now; and the rule holds after the catalogue is projected from the repository, and after a forensic rebuild
  - `Retention.Tests/CompactionCollectionTests`: its fixture moves every access time before each backup, as a filesystem that updates them on read does, so what it asserts no longer depends on how the machine mounted its temp directory

---

## Context

A folder's tree states the folder's own metadata ([specification 06 §5](../../specifications/repository-format/06-manifests.md#5-tree-manifest) key 2), its access time included. A tree's identity is derived from its bytes, and a tree whose object the index already locates is not written again ([NFR-PERF-005](../requirements/non-functional.md)). That reuse is what keeps a backup that changed one file from rewriting every tree.

A backup reads every folder it walks. Wherever a read moves an access time, the next backup sees every folder with a time it did not have before. Its tree is then new bytes and a new object, and so is the tree of every folder above it, which names the new child. A filesystem mounted with strict access times moves them on every read. One mounted with `relatime`, the Linux default, moves them at most once a day per folder, and a backup that runs daily meets them moved. Either way, a backup that changed nothing wrote the whole tree again.

Files were spared this when their metadata digest was made to leave access time out ([architecture 06 §4.3](../architecture/06-filesystem-capture.md#43-unchanged-bytes-are-not-an-unchanged-file)): a backup's own read is a read, and it says something about the observer rather than the file. Folders had no such rule, because what decides whether a folder's tree is reused is the tree's own identity, not a digest kept beside it.

It surfaced as a test. `Retention.Tests/CompactionCollectionTests` failed on every Linux run from the day the hosted runner image moved to a release whose temp directory is a tmpfs mounted without `relatime`. A backup there changed nothing, yet wrote a new root tree, so a blob the test expected the collector to condemn was still referenced.

## Decision

### 1 A folder whose tree would be the prior one but for its access time is the prior tree

When a folder closes, after every child has closed, the capture finds the prior snapshot's tree at the folder's path and the access time that tree states. If the folder's access time now is different, the capture derives the identity its tree would have with the prior time instead. Where that is the prior tree, the folder states the prior time, and the tree is the prior object, found already stored. Where it is not, something besides the access time changed. The new tree then states the time the folder has now, as a new file version does.

The test is the identity, not a field-by-field comparison. Two trees with the same identity are the same bytes, so a folder is kept only when nothing it states, its entries included, differs but the access time. Children close first, so a kept child leaves its parent's entries as they were, and the rule carries to the root.

Nothing else is excused. A change of mode, owner, extended attributes, modification time or entries is a change, and states the access time the folder has now.

### 2 The catalogue keeps the access time each tree states

The prior time cannot be read from the repository without opening the prior tree: a fetch and a decrypt per folder per backup. So the catalogue keeps it, at schema 11, keyed by the tree. One tree states one time wherever it is referenced. Every route that fills a catalogue writes it, as each writes the rest of what a capture compares against:
- the capture, for every folder it publishes;
- the projector, for every head tree it decodes;
- the forensic rebuilder, likewise.

The catalogue is a cache, so a catalogue at schema 10 is rebuilt rather than migrated.

### 3 Nothing in the format changes

A tree still states an access time under key 2, and a reader cannot tell which backup chose it. What it means is now what a reused file version's access time already meant: the folder's access time when a tree with these contents was first captured.

## Consequences

**Positive**
- **A backup that changed nothing writes no tree,** whatever the filesystem does with access times.
- **A folder's tree changes when the folder does,** and only then, so the trees a snapshot shares with the last are the folders nothing touched.
- **Folders and files follow one rule,** with one reason: an access time says who looked, not what changed.

**Negative**
- **A folder restores with an older access time.** Restoring a snapshot gives a folder kept this way the time from the tree that first captured it, not its latest read, exactly as a reused file version already does ([ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md)).
- **Each folder costs one more catalogue lookup,** by the snapshot's primary key, and a folder whose access time moved costs one more encode and hash of its tree.
- **After a catalogue rebuild a folder holding files still changes once.** A rebuilt catalogue knows no file identities, so the next backup reads every file again and gives each a version naming the last as its parent ([architecture 06 §4.3](../architecture/06-filesystem-capture.md#43-unchanged-bytes-are-not-an-unchanged-file)), which changes its folder. That boundary is the files' and is unchanged. A folder whose contents did not move is kept as it would be over a live catalogue.

## Alternatives considered

- **Leave access time out of trees.** A format change, and folders would lose the access time [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) restores.
- **Keep a folder's metadata in an object of its own,** so the tree's identity is its entries alone. A format change, and one more object per folder per change, to keep a time whose value is already a convention.
- **Read the prior tree from the repository.** Exact, with no catalogue state, but a targeted read per folder per backup, which costs more than the churn it prevents on a small tree.
- **Mount the test's temp directory with `relatime`.** It would have turned the test green and left every daily backup on a `relatime` disk rewriting its whole tree.

## What this record does not do

- **Restore a folder's latest access time.** A folder kept by this rule states the time it was first captured with, and a restore gives back that one.
- **Change how files are judged.** Their rule is architecture 06 §4.3's, unchanged.
- **Keep a folder's tree after a catalogue rebuild when its files are read again.** See the third negative consequence.

## Status history

| Date | Status | Note |
|---|---|---|
| 2026-10 | Accepted; built | A folder whose tree would be the prior one but for its access time states the prior time and is the prior tree; the catalogue keeps each tree's access time at schema 11, written by the capture, the projector and the forensic rebuilder (`Repository/SnapshotPublication`, `Repository.Catalogue/Catalogue`, `Repository.Tests/EndToEnd/FolderAccessTimeTests`) |
| 2026-10 | Accepted; built | [ADR-0151](0151-the-catalogue-keeps-a-folders-entries-once-under-its-tree.md): a folder that keeps its tree keeps its catalogue entries too, since the catalogue keeps a folder's entries once, under its tree. |

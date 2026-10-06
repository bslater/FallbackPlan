# ADR-0086 — A restore gives a folder its own metadata back, once nothing more lands in it

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004, FR-RST-003
**Related:** [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) (the times, and the rule this extends to folders), [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) (ownership, and a set-id bit only with its owner or group), [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (what a restore says it does not write back), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (no silent drop), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) (metadata after content), [specification 06 §5](../../specifications/repository-format/06-manifests.md#5-tree-manifest) (a tree's first manifest carries its folder's own metadata)

**Built:**
- The folders: `Restore/RestoreExecutor` reads each folder's tree as it reads a file's manifest, makes the folder, and gives it its own metadata once the run has written everything else, deepest folder first, by the rule a file is held to. It gives nothing to a folder that was already there, nor to anything that has taken the place of a folder it made. `Restore/RestoreBlobSet` reads each folder's tree for the plan, and names a folder whose tree the store does not hold. `Restore/RestoreMetadata` holds a folder to the file's rule and counts folders apart from files. `Domain/FileTimes` sets a folder's creation time on Windows too.
- The tests:
  - `Repository.Tests/RestoreFolderMetadataTests` — a folder's times, creation time, permissions, owner and group come back once its contents have landed, permissions that forbid writing included; a set-group-id bit stays only with its group; a folder already there keeps its own and says so; nothing goes through a link, planted before the run or swapped in while it runs; a folder whose tree will not read is still made and says why; and the plan reads each folder's tree, names one the store does not hold, and counts folders apart
  - `Domain.Tests/FileTimesTests` — a folder's creation time is set on Windows and macOS
  - `Hosts.Tests/RestoreHonestyServiceTests` — a real folder's captured modification time comes back through the service
  - `Repository.Tests/RestoreMetadataHonestyTests`, `Repository.Tests/RestoreBreadthTests`, `Cli.Tests/RestoreHonestyCommandTests` — a folder's receipt item now lists what its target does not apply, so the golden receipt's folder carries only a modification time, and on Windows the service and CLI count the folder among the items whose attribute bits are not applied

---

## Context

A tree manifest's first record carries the metadata captured with its folder: times, permissions, owner and group, and the rest of specification 06 §4.1. Capture has always written it. A restore never read it. The executor made each folder, gave it nothing, and wrote the folder's receipt item with nothing listed as not applied. So a restored folder carried the time the restore made it, the restoring process's default permissions and the restoring account as its owner, and the receipt said nothing about it. [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) recorded that as owed, because it is the silent drop [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) rules out.

Four facts shape the rest.

- **Writing into a folder moves its modification time.** Every file, link or folder that lands in a folder changes it. A time applied when the folder is made is overwritten by its first child.
- **A folder's permissions can forbid writing into it.** A folder captured read-only, `r-xr-xr-x`, refuses whatever is still to land in it, unless the restore runs as root.
- **A folder can already be there.** A restore in place, or to a folder's original location, writes into folders that exist. Their permissions and owner are their owner's current choice. A folder restricted since the snapshot was taken would be reopened by an old snapshot restored over it.
- **A folder's metadata goes on later than a file's.** A file's goes on the moment its content lands. A folder's can only go on once nothing more will land in it, which for the top folder is the end of the run. Whoever can write beside a folder has that long to put a link in its place.

## Decision

### 1. A folder is held to the file's rule

A folder the restore makes gets back what a file gets back, in the same order: on a POSIX target its owner and group where their names resolve and the restoring account may give them, then its permissions, which keep a set-id bit only where the owner or group it goes with was given back; then its modification and access times everywhere, and its creation time last where the platform can set one ([ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) §1, [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) §3 and §4). On a folder, set-group-id hands the folder's group to everything made in it. Kept on a folder whose captured group did not land, it would hand them the restoring account's group, so it goes. Windows opens a folder for its times only through the folder's own call, so the times are written through it.

### 2. Last, deepest first

The run reads each folder's tree when it reaches the folder, makes the folder, and holds what was captured. Once every item has been written, it gives each folder its metadata, deepest folder first. Nothing lands in a folder after its times are set, and a folder's permissions never stand between the run and anything still to be written. A child folder goes on before its parent, so a parent captured without search permission cannot hide its children from the restore. A run that is stopped settles the folders it made too. Each is in the receipt, and its item says what it was given.

The run keeps only what the rule can apply, plus which attributes were captured. A tree of many folders holds no security descriptors or extended attributes the run will not write.

> **2026-10 ([ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md)).** A folder's extended attributes are now written back by the file's rule, so the run keeps them with the rest. A folder's default ACL goes on with them, after the files restored into it are made, so none of them inherits from it.

### 3. A folder already there keeps its own

No existing-file policy reaches a folder. The executor already refuses to let Preserve displace a folder or Replace delete one, because a policy that resolved a folder would act on a subtree it never planned. So a folder that stands at the destination before the run reaches it keeps its own metadata. Its item lists everything captured as not applied, with the reason. A folder the restore writes into, whether chosen by the person or made for the run's quarantine, is not an item, and it too keeps its own. The snapshot's root, the folder a single-folder set captured, is restored as its contents, into the folder the restore writes into. Its own metadata is not given to that folder, for the reason [ADR-0085 Amendment 1](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md#amendment-1-2026-10--a-quarantine-restore-keeps-set-id-bits) gave for the quarantine folder: it would change who may reach a folder the person chose.

### 4. Never through a link

Immediately before a folder is given its metadata, the run checks that its path still resolves under the restore's root without leaving it through a link, and that what stands there is a folder and not a link. If anything has taken its place, nothing is applied and its item lists everything captured, with the reason. A link standing where a folder is restored before the run starts is a folder already there (§3), and is given nothing either. Ownership was already written with `lchown`, which never follows a link. The permissions and times are written by path, so without the check they would land on whatever the link points at.

### 5. A folder whose tree will not read is still made

A folder holds what restored under it, so it is made even when its tree will not read. What it carried is unknown, so its item lists nothing and says why. Metadata alone does not change an item's outcome ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)), so the folder is still restored and the run can still be complete.

### 6. The plan reads each folder's tree

The plan's probe reads a folder's tree as it reads a file's manifest, because the run will. It names a folder whose tree the store does not hold among the paths the store cannot serve (FR-RST-003). It predicts each folder by the rule, and counts folders apart from files: "2 file(s) and 1 folder(s)". It cannot know which folders will already be there, so the receipt says that.

## Consequences

- A restored tree's folders carry their captured times, permissions and ownership, where the target and the restoring account allow them. A folder captured read-only comes back read-only, with its contents.
- A restore in place no longer leaves a live folder's permissions and owner as they were by accident. It leaves them by rule, and says so per folder.
- The receipt names, per folder, what was not applied. On Windows that now includes each folder's attribute bits, so the service and CLI tests count three items where they counted two.
- The run reads one more record per folder. A folder's tree sits in the metadata blobs beside its files' manifests, and the read-ahead fetches them together, so the read budget is unchanged (NFR-PERF-009).
- A folder's metadata goes on later than a file's. The check in §4 narrows the window in which a link can redirect it to the one between the check and the write. That window, and the one each file's writes already had between its containment check and its landing, are a restore writing by path into a tree another account can change. [The threat model](../threat-model.md) now says so, and advises restoring in place only into a folder no other account can write to.

## What this does not do

- **A symlink's own metadata, extended attributes, Windows attribute bits, security descriptors and alternate streams.** These are still captured, declared and listed, for folders as for files.

  > **2026-10 ([ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md)).** Extended attributes are now written back, for folders as for files. The rest of this item remains owed.
- **The snapshot's root.** Its metadata is not applied to the folder a restore writes into (§3), and the plan does not declare it, because that folder is not an item.
- **A handle-relative restore.** The run writes by path, and checks containment before each write rather than in one step with it. Opening each folder relative to its parent, as capture does, would close the window §4 narrows.
- **The recovery tool and the CLI's `restore-file`**, which write no metadata.

## Alternatives considered

- **Apply a folder's metadata when the folder is made.** Rejected. Its first child moves the modification time, and permissions that forbid writing refuse the rest of its contents.
- **Apply each folder's metadata as soon as the run leaves it.** Rejected. It needs the plan in depth-first order, which a hand-built or sliced plan need not be, and a later item that writes into an earlier folder would move its time again. Settling at the end needs no order.
- **Give a folder already there its captured metadata.** Rejected. It changes a live folder's permissions and owner with no copy of what it had. Only Replace treats a file that way, and only when a person chooses it. Replace is a policy for files, and a folder is no policy's to resolve.
- **Mark a folder whose tree will not read as degraded.** Rejected. Degraded means content short of what was captured. The folder's content is what restored under it, and its item says what is unknown.
- **Give the snapshot's root metadata to the folder the restore writes into.** Rejected for §3's reason. It would also make a run's quarantine folder take whatever reach the captured folder had, `rwxrwxrwx` included.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A folder the restore makes gets its captured times, permissions and ownership back by the file's rule, last and deepest first. One already there keeps its own, nothing is applied through a link, and the plan and receipt count folders apart from files. |
| 2026-10 | Accepted | [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) writes a folder's extended attributes back by the file's rule, in §2's end pass, so a folder's default ACL goes on after what is restored into it. |

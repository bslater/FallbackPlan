# 06 — Filesystem capture

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §10 · **Resolves:** [M3](../review/2026-08-architecture-review.md#m3--cross-platform-metadata-semantics-are-named-but-never-resolved)

**Built:** Yes, except the no-follow handle-relative traversal §4.1 owes — see [implementation status](../implementation-status.md).

---

## 1. Scanner

The scanner streams directory traversal with memory bounded independently of file count (NFR-PERF-001). It must:

- avoid following symbolic links unless the backup set approves them, and never follow one out of an approved root;
- detect and report mount and junction boundaries rather than silently crossing them;
- detect hard links and record link identity so restore can reconstruct the relationship;
- detect sparse extents and record them as logical zero extents rather than reading zeroes;
- handle inaccessible and concurrently changing files without aborting the snapshot — an unreadable file is an entry in the error manifest, not a failed backup;
- use stable file identity where the platform provides it (`FileId` on Windows, `(device, inode)` on Unix) so a rename is recognised as the same file rather than a delete plus a create — see [§4.2](#42-a-rename-is-a-move-not-a-new-file);
- **revalidate after reading** — compare size, mtime, and identity before and after; a file that changed mid-read is recorded as captured-inconsistent and re-queued, and one whose name has come to mean a different object is recorded as a substitution and **not** re-read ([§4.1](#41-links-are-classified-before-they-are-traversed)).

## 2. Path handling

Paths are the most common source of silent cross-platform data loss, so the rules are normative.

**Storage.** Store the **original path bytes** exactly as the source filesystem reported them, plus the observed Unicode normalisation form. Never normalise destructively on capture — a filesystem that permits both NFC and NFD forms of the same name as distinct files has two distinct files, and rewriting either loses one.

**Get the bytes from the filesystem, not from a string.** A POSIX filename is any byte sequence without NUL or `/`, and the host runtime decodes an invalid-UTF-8 name to U+FFFD irreversibly. Re-encoding that string yields bytes that are not the ones on disk *and* a path that does not open the file — so an entry captured that way is stored under a name it does not have, with content that was never read. Names therefore come from `readdir`, and an entry whose name the host cannot represent is recorded in the error manifest with reason 8 rather than captured under a substitute. → [repository format 06 §4.3](../../specifications/repository-format/06-manifests.md)

**Indexing.** Index by a **casefold key** derived from the NFC form. This is what makes lookup work when the source is case-sensitive and the restore target is not.

**Collision detection.** Detect collisions at **restore-plan** time ([`08-restore-and-recovery.md` §2](08-restore-and-recovery.md#2-restore-planning)), never mid-restore. `README.md` and `readme.md` captured from ext4 and restored to APFS is a conflict the user must be told about *before* files start landing, not after one has overwritten the other.

**Length and reserved names.** Record the source's limits in the snapshot's filesystem capability record. Windows reserved names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`), trailing dots and spaces, and paths exceeding the target's limit are all restore-plan conflicts with an explicit resolution, not runtime failures.

## 3. Metadata matrix

For each metadata class and target platform: **preserve** it, **degrade** it and report, or **refuse** the restore. There is no fourth option and in particular there is no silent drop.

| Metadata | Windows (NTFS) | macOS (APFS) | Linux (ext4/xfs) |
|----------|----------------|--------------|------------------|
| POSIX mode bits | Degrade → report | Preserve | Preserve |
| Owner / group (by name) | Degrade → report | Preserve where the principal resolves | Preserve where the principal resolves |
| POSIX ACLs | Degrade → report | Preserve | Preserve where supported |
| Windows ACLs (DACL/SACL) | Preserve | Degrade → report | Degrade → report |
| Extended attributes | Preserve as alternate streams | Preserve | Preserve where supported |
| Alternate data streams | Preserve | Degrade → report | Degrade → report |
| Resource forks | Degrade → report | Preserve | Degrade → report |
| Finder flags / colour labels | Degrade → report | Preserve | Degrade → report |
| Creation time | Preserve | Preserve | Preserve where supported |
| Modification / access time | Preserve | Preserve | Preserve |
| Hard links | Preserve | Preserve | Preserve |
| Symbolic links | Preserve (privilege permitting) | Preserve | Preserve |
| Sparse extents | Preserve | Preserve | Preserve |
| Immutable / system flags | Degrade → report | Degrade → report | Degrade → report |
| Compression / encryption attributes | Degrade → report | Degrade → report | Degrade → report |

**Degrade → report** means: restore the file's *content* correctly, apply the closest available approximation of the attribute, and record the degradation in the restore receipt ([`08-restore-and-recovery.md` §3](08-restore-and-recovery.md#3-restore-verification)). The user learns what was lost without losing the data.

**Refuse** is reserved for cases where restoring would produce a file that is wrong in a way the user could not detect — for example, restoring a file whose security descriptor cannot be applied into a location where the default descriptor would grant broader access than the original. These are enumerated in the restore plan and require explicit acknowledgement.

Everything a snapshot captured is preserved in the repository regardless of the restore target. Degradation is a property of a particular restore, not of the backup: the same snapshot restored to its original platform is lossless.

## 4. Change detection

Filesystem change journals and watchers are **hints only**:

| Platform | Mechanism |
|----------|-----------|
| Windows | USN Change Journal, `FileSystemWatcher` |
| macOS | FSEvents |
| Linux | inotify / fanotify where suitable |

A periodic reconciliation scan is **mandatory** regardless. Event streams overflow under load, reset across reboots and journal wraps, miss changes made while the agent was not running, and are unavailable on network and removable filesystems. A design that trusts them silently misses files, and the user finds out at restore.

The reconciliation interval is configurable; a full scan is also forced after any detected journal discontinuity.

### 4.1 Links are classified before they are traversed

A link is a link whatever it points at. An object carrying both a directory marker and a link marker — an NTFS directory junction, a directory symlink — **MUST** be classified as a link, and the scanner MUST NOT descend through it. Testing the directory marker first classifies a junction as an ordinary directory and walks out of the approved root, and junctions need no privilege to create, so that is the shape an unprivileged attacker on the source machine actually has.

**The traversal is handle-relative on POSIX.** Each directory is held open, and every operation on its children — listing, stat, descent, opening content, reading a link target — is performed against that descriptor with the child's raw name bytes, opening with `O_NOFOLLOW`. A name is therefore never turned back into a path to be resolved a second time, which is what closes the time-of-check-to-time-of-use gap: the object that was classified is the object that is read, because a descriptor names an inode and nothing can move it to another one. Revalidation stats the same handle, so it compares the read object to itself rather than to whatever the name means afterwards.

**Windows keeps the path-based walk**, and gains the identity half of revalidation instead: the post-read stat carries device and file identifier, and a name that has come to mean a different object is recorded as `captured-identity-changed` ([ADR-0026 §Decision 2](../adr/0026-phase-1-capture-shapes.md)) rather than re-read — re-reading the name would read the substitute. A UTF-16 name has no byte-fidelity problem to solve, so the remaining gap there is the substitution window itself, which needs `NtCreateFile` relative to a directory handle and is not built.

### 4.2 A rename is a move, not a new file

The prior version of a file is found **by path first, then by stable identity**. Path first because it is the common case and the cheaper index; identity second because a rename or a move changes the path and nothing else.

Two things depend on getting this right, and only one of them is speed. The obvious cost of missing a rename is re-reading and re-hashing every byte of a file whose content did not change. The durable cost is that the new version is written with no `parent_version` — so a file the user renamed loses its history permanently, in an immutable object, because the engine could not tell a move from a deletion and a creation.

Identity is never sufficient on its own and is not treated as such. An inode is reused after its file is deleted, so size and modification time are still checked before any content is reused, exactly as they are for a path match.

**A renamed file still gets a new manifest.** A manifest states its own name, so re-emitting the prior object under a new tree entry would produce a tree that says one name and a manifest that says another. The new manifest carries the new name and names the prior version as its parent.

**Identity is durable, not cached.** The catalogue answers the identity question first because it is fastest, but it is a disposable cache — a rebuild recovers paths and not identities. Left there, the rule above would hold only while the cache was warm, and a rename captured in the window after a rebuild would sever a file's history permanently because a local database happened to be cold. Each file version therefore publishes a small keyed source-identity hint ([specification 06 §11](../../specifications/repository-format/06-manifests.md#11-source-identity)), found by listing one prefix, which the next publication consults when the catalogue cannot answer — and only then, because a warm catalogue has already answered and a round trip to learn what is in hand is waste. It is advisory: absent, the engine matches by path and misses renames, which is the behaviour that existed before it.

> **What is not yet built.** The renamed file's *content* is currently re-read, because reusing it without re-reading means fetching the prior manifest and rewriting it with the new name, which needs a manifest-read path the publisher does not have yet. Ancestry is correct today; the read is still paid.

## 5. Consistency

| Method | Platform | Guarantee |
|--------|----------|-----------|
| VSS | Windows | Application-consistent where writers cooperate; crash-consistent otherwise |
| Live capture + pre/post stat validation | All | Best-effort; per-file consistency detected, not guaranteed |
| Filesystem snapshots (APFS, Btrfs, ZFS, LVM) | Later phase | Crash-consistent |
| Application hooks | Later phase | Application-defined |

The snapshot manifest records **which method was used**, and the status display reports it. "Best-effort" and "application-consistent" are materially different promises and a user restoring a database needs to know which one they have.

## 6. Backup set selection

Include and exclude rules operate on the canonical path form, are evaluated deterministically, and are recorded in the policy manifest so a snapshot always states exactly what it was asked to capture.

A path excluded by policy is distinct from a path that failed to be captured. The first is in the policy manifest; the second is in the error manifest. Conflating them is how users end up believing they have a backup of something they excluded two years ago.

---

**Previous:** [05 — Storage providers](05-storage-providers.md) · **Next:** [07 — Retention and garbage collection](07-retention-and-gc.md)

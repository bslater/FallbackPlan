# 06 — Filesystem capture

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §10 · **Resolves:** [M3](../review/2026-08-architecture-review.md#m3--cross-platform-metadata-semantics-are-named-but-never-resolved)

---

## 1. Scanner

The scanner streams directory traversal with memory bounded independently of file count (NFR-PERF-001). It must:

- avoid following symbolic links unless the backup set approves them, and never follow one out of an approved root;
- detect and report mount and junction boundaries rather than silently crossing them;
- detect hard links and record link identity so restore can reconstruct the relationship;
- detect sparse extents and record them as logical zero extents rather than reading zeroes;
- handle inaccessible and concurrently changing files without aborting the snapshot — an unreadable file is an entry in the error manifest, not a failed backup;
- use stable file identity where the platform provides it (`FileId` on Windows, `(device, inode)` on Unix) so a rename is recognised as the same file rather than a delete plus a create;
- **revalidate after reading** — compare size, mtime, and identity before and after; a file that changed mid-read is recorded as captured-inconsistent and re-queued.

## 2. Path handling

Paths are the most common source of silent cross-platform data loss, so the rules are normative.

**Storage.** Store the **original path bytes** exactly as the source filesystem reported them, plus the observed Unicode normalisation form. Never normalise destructively on capture — a filesystem that permits both NFC and NFD forms of the same name as distinct files has two distinct files, and rewriting either loses one.

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

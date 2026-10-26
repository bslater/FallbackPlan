# ADR-0102 — A restore gives a hard-link group back as one file

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004, FR-RST-003
**Related:** [ADR-0026](0026-phase-1-capture-shapes.md) §1 (the group key a manifest carries, keyed on the device and the file but not the filesystem), [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (the plan's space measure, and the record that named this as not done), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the existing-file policies, write-beside among them), [ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md) (the restore's last change to what an item is written as), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (hard links are preserved on every platform), [specification 06 §4](../../specifications/repository-format/06-manifests.md#4-file-version-manifest) (key 12, `hardlink_group`)

**Built:**
- The link: `Domain/HardLinks` makes a further name for a file through POSIX `link` and Windows `CreateHardLinkW`, and answers a refusal as not made. `Restore/RestoreHardLinks` holds what a later name must agree on with its group's first. `Restore/RestoreExecutor` makes each agreeing later name a link to the file its group's first name landed as, through the existing-file policy at its own destination, and records it in receipt schema 7's `linked_to`. It takes how a link is made, the platform's call unless its caller says otherwise, as the space measure takes its probe. `Restore/RestoreSpace` measures a group once, and `Restore/RestoreBlobSet` gives the probe the key it measures by.
- The tests:
  - `Domain.Tests/HardLinksTests` — a further name is the same file, read through one name as written through the other; a name already taken, a file not there and a folder are refused, and what was there is kept
  - `Repository.Tests/RestoreHardLinkTests` — a group lands as one file under each of its names, and each later name says which it shares a file with and lists what that file did not get back; a name whose content or attributes differ is restored as a file of its own and says why, and one whose access time alone differs is still linked; a name the target will not link is restored as a file of its own and says why; each name meets the existing-file policy at its destination, moved aside, failed or linked beside; one name restored alone is a file of its own; the plan measures a group once, from the manifests and from the probe
  - `Repository.Tests/RestoreBreadthTests`, `Repository.Tests/RestoreMetadataHonestyTests` — the receipt at schema 7

---

## Context

Capture records every file that is one of several names for one inode. Manifest key 12, `hardlink_group`, is a keyed digest of the snapshot's device identity and the file's identity, equal for every name of one inode within a snapshot ([ADR-0026](0026-phase-1-capture-shapes.md) §1). That record said a restore reconstructs links by equal groups. None did. Each name came back as a copy with its own bytes on disk, the plan measured the room for every copy, and nothing said the names had been one file ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) recorded that). [Architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) preserves hard links on every platform.

Four facts shape the rest.

- **The group names an inode on the device, not on a filesystem.** The digest keys on the snapshot's device identity and the inode number. A machine with several filesystems repeats inode numbers, so two different files, each with more than one name, can share a group in one snapshot.
- **The names of one file share everything but the access time they were captured with.** One content, one length, one owner, one set of permissions and times. Reading the first name moves the access time the second is then captured with.
- **Every platform has the call, and each can refuse.** POSIX `link` and Windows `CreateHardLinkW` refuse a name already taken, a file not there, a folder, a file on another volume, and a volume that keeps no links. .NET has no call for it.
- **A tree of links is common.** A package store, a series of snapshot folders made with links, or a mail store gives one file many names. Restored as copies, such a tree needs many times its own room, and the plan refused what would have fitted.

## Decision

### 1. The first name lands, and a later name is that file again

The first name of a group the run reaches lands as any file does: read, verified and written, then given its metadata. Each later name of the group is made a further name for that file, a hard link, not a copy. Nothing is read or verified for it: it is the file that was.

### 2. Only a name that agrees with the first

A later name is linked only where it agrees with its group's first name in content (its whole-file hash), its length, and every captured attribute but its access time, which the metadata digest already holds back. A name that does not agree is restored as a file of its own, and its receipt item says it was not linked and why. That covers two files of two filesystems that share a group, and a file that changed between the capture of its names.

### 3. Each name meets the existing-file policy at its own destination

A later name's destination is held to the existing-file policy as a file's is. Under Preserve what stands there is moved aside first, under Fail the name fails and what stands there is kept, under Replace it is removed, and under write-beside the link is made beside it under the dated name, which the item gives as `written_as`. A folder at the destination fails the name, as it fails a file.

### 4. A link the target refuses is a file of its own

Where the target will not make the link, because the first name landed on another volume or the volume keeps no links, the name is restored as a file of its own, and its item says the link was not made. Its content is right, and a copy is less than was captured, not wrong. The plan cannot know a volume's answer beforehand, so only the receipt says it.

### 5. The metadata is the file's

The file's metadata is written once, with its first name. Every later name lists what that file did not get back, which is what its first name lists, because it is the same file.

### 6. The receipt names the file a name shares

A linked name's item carries `linked_to`, the path of the group's first name, and the receipt moves to schema 7. The field is absent for a file of no group, for a group's first name, and for a name restored as a file of its own.

### 7. The plan measures a group once

The space measure counts a later name of a group as writing nothing, from the manifests and from the plan probe alike, by the same agreement §2 links on. A tree of links is measured at its own size.

## Consequences

- A restored tree of links takes the room it took before the loss, and editing one name after the restore edits every name, as it did before.
- The plan's measure for such a tree shrinks, so a restore it refused for room it did not need now runs.
- A restore spanning volumes, or onto a volume without links, lands some names as copies, and the receipt says which.
- A receipt reader at schema 6 sees one field it does not know. Nothing in the client↔service contract changes.

## What this does not do

- **Key the group on the filesystem as well.** A collision across filesystems restores as two files, which §2 ensures. Changing the derivation would give new captures different groups from manifests reused verbatim, so one snapshot would hold both, and their names would restore as copies.
- **Link across runs.** A group is one file again within one run. Names restored by two runs are two files, and one name restored alone is a file of its own.
- **Link to a file already at the destination.** A name lands beside, over or instead of what stands there, by the policy, and is never made a further name for it.
- **The recovery tool and the CLI's `restore-file`**, which restore one file or a tree by their own paths and make no links.

## Alternatives considered

- **Link on the group alone.** Rejected. Two files of two filesystems that share a group would become one, and an edit to either would change both.
- **Link only where the whole manifests agree.** Rejected. Every real group differs in the access time its names were captured with, so nothing would be linked.
- **Keep restoring copies, and say so.** Rejected. The matrix preserves hard links, and every platform has the call.
- **Fail a name the target will not link.** Rejected. Its content landed and verified, as a copy's does. A copy is less than was captured, not wrong, and the receipt says so.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A group's later names are links to the file its first name landed as, where they agree with it in content, length and every attribute but the access time, each through the existing-file policy at its destination. A name that disagrees, or that the target will not link, is a file of its own and says why. The receipt moves to schema 7's `linked_to`, and the plan measures a group once. |

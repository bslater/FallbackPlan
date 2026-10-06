# ADR-0087 — A restore writes back the extended attributes it may

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004, FR-RST-003
**Related:** [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) (ownership, which goes first, and the rule that a privilege comes back only as it was captured), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) (a folder's metadata, which goes on last), [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) (each attribute on its own, and a receipt that records what each write did), [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (what a restore says it does not write back), [ADR-0020](0020-ed25519-signing-key-semantics.md) (the device a snapshot names is attribution by claim), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (extended attributes and POSIX ACLs are preserved), [specification 06 §4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map) (key 8, each attribute's name and value as bytes)

**Built:**
- The writes: `Domain/ExtendedAttributes` writes one attribute by its captured name and never through a link, with Linux's `lsetxattr` and macOS's `setxattr` told not to follow one; Windows writes none. `Restore/RestoreExecutor` writes a file's or a folder's attributes after its owner and group and before its permissions, each alone. It names in the item's detail any attribute it left, and why, and gives the group no more than an ACL that did not come back gave it. `Restore/RestoreMetadata` holds the rule: which attributes are withheld and why, what an ACL names and what it gave the group, and the plan's lines. `Restore/RestoreBlobSet` records which kinds of attribute each item carries. `Restore/RestorePlan` says whether a target writes extended attributes and whether this installation captured the snapshot, and `Restore/RestoreAccount` whether the restore runs as root. `Agent/ServiceCommandHandler` and `Cli/OperationGateway` compare the device a snapshot names with their own.
- The tests:
  - `Domain.Tests/ExtendedAttributesTests` — an attribute is written to a file or a folder and reads back as written, an empty value included; never through a link; a path that is not there answers false; Linux's trusted namespace is refused without privilege and written with it; an ACL becomes the file's ACL and its mask the group's permission bits; nothing is written on Windows
  - `Repository.Tests/RestoreExtendedAttributesTests` — a file's and a folder's attributes come back and are not listed; they go on before permissions that forbid the owner writing and after the owner, a file capability surviving the ownership write; an ACL that names no account comes back and the permissions agree with it; one that names an account by number comes back only where this installation captured the snapshot; macOS lists every ACL; an ACL that does not come back leaves the group only what the ACL gave it; a namespace only root may write is listed for any other account, even where the platform would have let it through; and the plan declares each with counts, folders apart from files
  - `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` — a real file's attribute comes back through the service and through the CLI's direct restore, and on Linux its ACL naming an account by number too, because each path knows this installation captured the snapshot
  - `Repository.Tests/RestoreMetadataHonestyTests` — the honesty test whose not-applied list must read the same on every platform now says its target writes no extended attributes

---

## Context

Capture records every extended attribute a file or folder carries on Linux and macOS, each as its name and value bytes. On Linux that includes a file's POSIX ACL and a folder's default ACL, which Linux keeps as attributes in its system namespace. It also includes an SELinux label and a file capability in the security namespace, and whatever the trusted and user namespaces hold. On macOS it includes the quarantine flag a download carries, Finder information and a resource fork. No restore wrote any of them back. Each was listed as not applied ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)), which kept the receipt honest and left the file short of what was captured. [Architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) says extended attributes and POSIX ACLs are preserved where the platform supports them.

Six facts shape the rest.

- **Linux decides by namespace who may write an attribute.** Any account may write the user namespace of a file or folder it may write. Only root writes the trusted and security namespaces, though an SELinux policy may let an account relabel its own file. Only a file's owner, or root, sets its ACL. macOS has no namespaces. Windows keeps no attributes a restore writes.
- **Giving a file away strips its capabilities.** Changing a file's owner or group removes a file capability, as it clears a set-id bit.
- **Permissions can forbid the owner.** A captured mode without the owner's write bit makes the owner's own attribute writes fail, unless the restore runs as root.
- **An ACL and the mode share bits.** While a file has an ACL with a mask, the group bits of its mode are the mask, which bounds every account and group the ACL names and the group itself. Setting the ACL sets those bits, and setting the mode sets the mask. So the captured mode's group bits are the mask, not what the group was given.
- **An ACL names accounts by number.** `user:54321:r--` means whoever holds 54321 on the machine reading it. Capture records a file's owner and group by name for that reason ([ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md)). It records an ACL's entries as the numbers Linux stores.
- **macOS keeps no POSIX ACLs.** Its own access lists are another format, which capture does not read. Written there by name, a Linux ACL would be an attribute that grants nothing.

## Decision

### 1. Each attribute alone, by its captured name, never through a link

A file or folder the restore lands gets back each extended attribute captured with it, one at a time, by its captured name bytes and value. A write the platform refuses leaves that attribute, and the rest still land. Any attribute left makes the item list `extended_attributes` as not applied, and the item's detail names each left attribute with the reason: withheld by §§3–5, or refused by the target. Metadata alone still changes no outcome ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)). Linux's `lsetxattr` writes a link itself, never what it points at, and macOS's `setxattr` is told not to follow one, so an attribute cannot be redirected through a link at the end of its path. A name that is empty or holds a NUL is refused, because no platform takes one. Windows writes none, so there every attribute is listed, as before.

### 2. After ownership, before the permissions

[ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) put ownership first and the permissions after it. The attributes go between them. After ownership, because giving a file away strips a capability written before it. Before the permissions, because a captured mode that takes away the owner's write bit would refuse the owner's own attributes, and because an ACL sets the mode's group bits, which the captured mode then sets to what was captured beside it. A folder gets its attributes in the same order, once everything in it has landed ([ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md)). So a folder's default ACL goes on after the files restored into the folder are made, and none of them inherits from it.

### 3. An ACL that names accounts by number comes back only where this installation captured it

An ACL whose entries name an account or a group by number is written back only where this installation captured the snapshot. Anywhere else the number may belong to another account, and the ACL would give that account the file. A restore says this installation captured a snapshot when the device the snapshot names is its own. The service and the CLI's direct restore both do. A value that will not parse as an ACL counts as naming someone, because nothing vouches for what it would grant. An ACL that names nobody, only the owner, the group, the mask and everyone else, means the same on any Linux machine and comes back wherever Linux keeps ACLs.

The device a snapshot names is attribution by claim ([ADR-0020](0020-ed25519-signing-key-semantics.md)). This guards an honest restore on another machine. It is no defence against a member of the repository who lies about a snapshot's device. Such a member can already name any owner for a file, which a restore as root gives back.

### 4. macOS gets no POSIX ACL

A Linux ACL is written back only on Linux. On macOS it would land as an attribute that grants nothing, and the receipt would call it applied. It is listed instead, whoever captured it.

### 5. A restore that is not root leaves the root-only namespaces

On Linux a restore that is not running as root does not attempt the security and trusted namespaces. Its plan says they will not land, and its receipt keeps to the plan even where a security policy would have let a label through. A restore that keeps the label its policy gave a file it made is what a restore by an ordinary account should do. A restore as root writes them as captured, a file capability among them.

A capability is not tied to the owner the way [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) §4 ties a set-user-id bit. A set-user-id program runs as its owner, so on another owner it would run as someone the capture never named. A capability grants the same privileges whoever owns the file, so the owner a restore gives it changes nothing it grants.

### 6. The group gets no more than an ACL that did not come back gave it

Wherever a file's captured access ACL does not come back, the group bits of the permissions written are narrowed to what the ACL gave the file's group: its group entry, as far as the mask lets it. That covers §§3 and 4, a volume that refuses ACLs, and a target that writes no extended attributes. Without this the captured mode, whose group bits are the mask, would give the whole group everything the mask allowed the accounts the ACL named. A file shared with one account for writing, and with its group for reading, would come back writable by the group. An ACL that will not parse gives the group nothing. A narrowed mode is not the one captured, so the item also lists `posix_mode`.

That widening was not new. Before this record no restore wrote an ACL, and every file restored with one gave its group the mask's permissions, silently.

### 7. The plan says so beforehand

The plan's probe already decodes each manifest. It now records which kinds of attribute each item carries: one in a namespace only root writes, an ACL that names accounts by number, and an ACL that names nobody. On a target that writes extended attributes it declares, with counts and folders apart from files:

- ACLs that name accounts by number, where this installation did not capture the snapshot;
- every ACL, on a target that keeps no POSIX ACLs;
- on Linux, attributes in the security and trusted namespaces, to a restore that is not root.

Each ACL line says the group keeps no more than the lists gave it. A target that writes no extended attributes keeps its one line for all of them, as before. A refusal by the volume cannot be known beforehand, so only the receipt says it.

## Consequences

- A file restored on Linux or macOS carries its captured extended attributes where the account and the volume allow them. On Linux that includes its ACL wherever this installation captured it, and as root its capabilities and labels. On macOS that includes the quarantine flag, Finder information and resource fork, which are attributes there; no test yet exercises those three.
- A receipt item can now carry a detail on a restored file: the attributes it left and why. The service and the CLI report details only for failed, skipped and degraded items, so their answers are unchanged.
- A restore elsewhere of a file with a shared ACL comes back narrower than captured, never wider, and says so.
- A restore as root recreates a captured file capability, which lets a program do some of what only root may, whoever runs it. That is [ADR-0085 Amendment 1](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md#amendment-1-2026-10--a-quarantine-restore-keeps-set-id-bits)'s case for set-user-id programs, and its advice holds: a historical system tree restored as root belongs in a folder only the restoring account can reach.
- The proof is split by privilege, as ADR-0085's is. A container running as root proves the capability, the trusted namespace written, and the rule that withholds what root could write. CI's unprivileged runners prove the refusal and the read-only mode. macOS's leg proves the macOS rules.

## What this does not do

- **Name an ACL's accounts.** An ACL is captured as Linux stores it, with numbers. Capturing each entry's name, as owner and group are captured, would let one come back on any machine where the names resolve. That is a capture change and a format question, and is owed.
- **macOS's own access lists and Windows security descriptors.** Neither is written back. A security descriptor is still listed. macOS's lists are not captured.
- **Extended attributes on Windows.** Architecture 06 §3 would keep them as alternate streams, which are not yet written back either.
- **A symlink's own attributes.** A link is created with none of its own metadata, as before.
- **A handle-relative restore.** Attributes are written by path, like the permissions and times, so the window [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) describes, between a check and the write it guards, covers them too.
- **The recovery tool and the CLI's `restore-file`**, which write no metadata.

## Alternatives considered

- **Write every attribute and let the platform decide, as ownership is.** Rejected for ACLs and the root-only namespaces. The platform cannot tell that a number names the wrong account, so an ACL from another machine would land and grant. And a policy that lets an ordinary account write a label would make the receipt disagree with the plan.
- **Never write an ACL that names accounts.** Rejected. A restore on the machine that captured the snapshot, by far the common case, would lose every shared file's access list.
- **Translate an ACL's numbers through names on the capturing machine.** Not possible at restore. The numbers are all the snapshot holds. It is the owed capture change.
- **Write the captured mode whole when an ACL does not come back.** Rejected. It widens the group to the mask, which this record found every restore already did.
- **Attributes after the permissions, as one last write.** Rejected. A read-only mode refuses an account's own attributes, and an ACL written after the mode would move the group bits off the captured mode.
- **Write a Linux ACL on macOS as a plain attribute.** Rejected. It grants nothing there, and calling it applied would be the silent drop architecture 06 §3 rules out.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Extended attributes are written back on Linux and macOS, each alone and never through a link, after ownership and before the permissions. An ACL naming accounts by number comes back only where this installation captured the snapshot, macOS gets no POSIX ACL, and a restore that is not root leaves the root-only namespaces. An ACL that does not come back leaves the group no more than it gave, which corrects a widening every earlier restore made. |

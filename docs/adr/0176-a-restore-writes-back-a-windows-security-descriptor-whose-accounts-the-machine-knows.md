# ADR-0176 — A restore writes back a Windows security descriptor whose accounts the machine knows

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a file's and a folder's Windows security descriptor comes back where the target keeps one and knows every account it names, and one that names an account it does not know is named), FR-RST-003 (the plan says so per item)
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (what a restore says it does not write back, and its Amendment 1: an empty descriptor is none), [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) (an owner is given back where its name resolves and the account may give it), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) (a folder's metadata, which goes on last), [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) (an ACL that names accounts by number is written only where they mean what they meant), [ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md) and [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) (the one writer the executor, the command line and the recovery tool share), [ADR-0175](0175-a-restore-writes-back-the-windows-attribute-bits-it-may.md) (the attribute bits, which now go on before it), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (Windows ACLs are preserved on Windows), [specification 06 §4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map) (key 7, the descriptor as captured)

**Built:**
- The reading: `Domain/WindowsSecurityDescriptor` reads a self-relative descriptor for the accounts it names and whether its DACL is protected, and holds every offset and length inside the bytes. `Domain/MetadataRules` gives the plan's facts those accounts.
- The write: `Domain/WindowsSecurity` hands a descriptor to the platform as captured, with its DACL's protection said outright. `Domain/WindowsAccounts` asks the machine whether it knows a SID. `Domain/MetadataWriter` writes the descriptor last of all, on Windows alone, where every account it names is known, and the DACL alone where the owner is refused. `Domain/MetadataTarget` and `Restore/RestorePlan` say a local target keeps descriptors on Windows and how it knows an account. `Restore/RestoreMetadata` counts them applied there and declares the rest. `Restore/RestoreBlobSet` carries each item's accounts into the plan. `Restore/RestoreExecutor` and `Recovery/RecoverySession` keep a folder's descriptor for the writer where it writes one.
- The tests:
  - `Domain.Tests/WindowsSecurityDescriptorTests` — on every platform, against descriptors built byte by byte (`TestSupport/SecurityDescriptors`): the owner, the group and each account the DACL names are read once each, in order; a protected DACL says so; a null DACL is present and names none; a descriptor with no DACL says so; an authority past 32 bits is written in hex; an object ACE's account is found past the GUIDs its flags say, and a callback ACE's past its mask; and thirteen kinds of bytes that do not read as a descriptor are none
  - `Domain.Tests/SecurityDescriptorWriteBackTests` — the local target writes descriptors only on Windows, and an account is known only where the machine names it; on Windows a file gets its protected DACL back with its time kept, a folder gets its DACL, a descriptor naming an unknown SID is not written and the SID is named, the DACL lands where the owner is refused, and one that does not read says so; on POSIX a target said to keep descriptors writes none
  - `Repository.Tests/RestoreSecurityDescriptorTests` — the profile writes descriptors only on Windows, passes them and its account rule to the writer, and the rule counts them applied for a file and a folder where it does; on Windows a real file with a protected DACL and its folder with an added entry come back so through the executor, the plan declaring nothing and the receipt listing nothing
  - `Repository.Tests/RestoreMetadataHonestyTests` — the plan declares, on a line of its own, the files whose descriptor names an account the target does not know, with the rest one that does not read, and every descriptor where the target keeps none
  - `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` — on Windows the service's plan and receipt, and the command line's output, no longer list a descriptor

---

## Context

Capture records each entry's Windows security descriptor as `GetFileSecurityW` gives it: owner, primary group and DACL, self-relative (specification 06 §4.1 key 7). No restore wrote any back. The plan said, of every file and folder on Windows, that its descriptor would not be applied and it would take the permissions of the folder it landed in, and every receipt listed `security_descriptor` for every item. A file kept private came back as open as its new folder, and a file shared came back as closed. Architecture 06 §3 has Windows ACLs preserved on Windows.

A descriptor names accounts by SID. A SID is the number a machine or its domain gave an account, and it names that account only where that machine or domain is the one asked. A domain account's SID means the same on every machine in the domain. A local account's means nothing on another machine, nor on the same machine rebuilt, where the person's new account has a new SID. This is the problem [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) meets for a POSIX owner, which is captured by name and resolved on the target, and [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) meets for a Linux ACL, which names accounts by number and is written only where this installation captured it. A SID, unlike a number, can be asked about: the machine says whether it knows it.

## Decision

### 1. A descriptor is written as captured, in one call

The writer hands the captured descriptor to `SetFileSecurityW`: owner, group and DACL in one call, with the DACL's protection said outright, so a DACL that took nothing from its folder still takes nothing. The DACL keeps the entries its folder had given it when captured. Restored in place, a file has exactly the access it had, and restored elsewhere, the access it had where it was. Nothing beneath a folder is walked.

### 2. Only where the machine knows every account it names

The writer asks the machine whether it knows each SID the descriptor names: the owner, the group, and every account its DACL grants or denies. It asks once per SID. If any is unknown, the descriptor is not written, the entry keeps its folder's access, and the item's detail names each unknown SID. A descriptor that does not read as one is not written either, and the detail says so.

It is all or nothing. Writing the DACL without the entries for unknown accounts could drop a deny, so the file would grant more than it did. Writing it whole would grant the file's access to nobody: on a rebuilt machine, the person's own new account would find their files closed to it.

### 3. Where the owner is refused, the DACL lands alone

Windows lets a process make an owner only of its own account, or of a group it may own by, unless it holds SeRestorePrivilege and has enabled it. It lets the owner of a file write its DACL. So where the call with the owner fails, the writer writes the DACL alone. The descriptor is then not applied, and the detail says that its owner and group were refused and that its access control list landed.

### 4. Last of all

A DACL may take from the restoring account the access the writes before it need: the streams, the times and the attribute bits. So the descriptor is the last write for an entry, after the attribute bits ADR-0175 put last.

### 5. The plan predicts by the same rule

The plan's facts carry the SIDs each item's descriptor names. A target that writes descriptors asks whether it knows each one, as the writer will. Where it does not, the plan declares those files and folders on a line of their own, because no privilege mends that. A descriptor that does not read is declared with the rest. Where the target writes no descriptors, every one is declared as before. Whether the owner will be refused is the receipt's to say: the plan does not judge privilege on Windows.

## Consequences

- On the machine that captured them, or one in the same domain, a file and a folder come back with their own access, owner and group.
- On another machine, a descriptor that names one of the old machine's local accounts is named in the plan and in the receipt. Its file takes its new folder's access, as every file did before.
- A plan on Windows declares descriptors only for those that name an account the machine does not know, and those that do not read.
- A file whose restored DACL withholds deletion from the restoring account is one a later restore with Replace may be refused over. That is said for the file.

## What this does not do

- **Enable SeRestorePrivilege.** A restore gives an owner it is not only where the privilege is already enabled in its token. A service account that holds the privilege without enabling it has its owners refused, and its DACLs land alone.
- **The SACL.** Capture does not record one: reading it needs SeSecurityPrivilege.
- **Map an account.** A SID the machine does not know is not mapped to one it does, by name or otherwise. Capture records SIDs, not names.
- **Refuse where the folder would grant more.** Architecture 06 §3 reserves refusal for a descriptor that cannot be applied into a place whose own access is broader. A descriptor that is not written is degraded and declared, as before. Comparing the two is not built.
- **Predict a refused owner in the plan.** The plan does not judge the restoring account's privilege on Windows.
- **A link's descriptor.** No restore on Windows makes a symlink.

## Alternatives considered

- **Write the DACL through `SetNamedSecurityInfoW`, taking inherited entries from the new folder.** Rejected. It changes what was captured into what the new folder gives. Setting a folder's DACL also walks everything beneath it, and the restore sets folders deepest first, so the walk would repeat for every folder above each entry.
- **Write the DACL without the entries for unknown accounts.** Rejected. A deny dropped grants more than the captured descriptor did.
- **Write every descriptor whole, as a copy would.** Rejected. A descriptor naming only accounts the machine does not know grants nobody, and on a rebuilt machine that closes a person's own files to them.
- **Resolve unknown SIDs by account name.** Not possible: capture records SIDs, not names. It would be a capture change and a format question, as ADR-0087 records for a Linux ACL's numbers.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The writer every way of restoring shares writes a file's and a folder's security descriptor back on Windows, as captured and last of all, where the machine knows every account it names; one naming an account it does not know is not written and is named; the DACL lands alone where the owner is refused; and the plan predicts it per item. `Domain/WindowsSecurityDescriptor`; `Domain/WindowsSecurity`; `Domain/MetadataWriter`; `Restore/RestoreMetadata`; `Domain.Tests/WindowsSecurityDescriptorTests`; `Domain.Tests/SecurityDescriptorWriteBackTests`; `Repository.Tests/RestoreSecurityDescriptorTests` |

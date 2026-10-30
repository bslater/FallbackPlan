# ADR-0176 — A restore writes back a Windows security descriptor whose accounts the machine knows

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a file's and a folder's Windows security descriptor comes back where the target keeps one and knows every account it names, and one that names an account it does not know is named; by [Amendment 1](#amendment-1-2026-10--an-account-that-holds-the-restore-privilege-gives-an-owner-it-is-not), an account that holds SeRestorePrivilege gives an owner it is not), FR-RST-003 (the plan says so per item; amended by [Amendment 2](#amendment-2-2026-10--the-plan-names-an-owner-the-restoring-account-may-not-give): it names a descriptor whose owner the restoring account may not give)
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (what a restore says it does not write back, and its Amendment 1: an empty descriptor is none), [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) (an owner is given back where its name resolves and the account may give it), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) (a folder's metadata, which goes on last), [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) (an ACL that names accounts by number is written only where they mean what they meant), [ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md) and [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) (the one writer the executor, the command line and the recovery tool share), [ADR-0175](0175-a-restore-writes-back-the-windows-attribute-bits-it-may.md) (the attribute bits, which now go on before it), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (Windows ACLs are preserved on Windows), [specification 06 §4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map) (key 7, the descriptor as captured)

**Built:**
- The reading: `Domain/WindowsSecurityDescriptor` reads a self-relative descriptor for the accounts it names and whether its DACL is protected, and holds every offset and length inside the bytes. `Domain/MetadataRules` gives the plan's facts those accounts.
- The write: `Domain/WindowsSecurity` hands a descriptor to the platform as captured, with its DACL's protection said outright. `Domain/WindowsAccounts` asks the machine whether it knows a SID. `Domain/MetadataWriter` writes the descriptor on Windows alone, after the streams and times and before the attribute bits, where every account it names is known, and the DACL alone where the owner is refused. `Domain/MetadataTarget` and `Restore/RestorePlan` say a local target keeps descriptors on Windows and how it knows an account. `Restore/RestoreMetadata` counts them applied there and declares the rest. `Restore/RestoreBlobSet` carries each item's accounts into the plan. `Restore/RestoreExecutor` and `Recovery/RecoverySession` keep a folder's descriptor for the writer where it writes one.
- The tests:
  - `Domain.Tests/WindowsSecurityDescriptorTests` — on every platform, against descriptors built byte by byte (`TestSupport/SecurityDescriptors`): the owner, the group and each account the DACL names are read once each, in order; a protected DACL says so; a null DACL is present and names none; a descriptor with no DACL says so; an authority past 32 bits is written in hex; an object ACE's account is found past the GUIDs its flags say, and a callback ACE's past its mask; and thirteen kinds of bytes that do not read as a descriptor are none
  - `Domain.Tests/SecurityDescriptorWriteBackTests` — the local target writes descriptors only on Windows, and an account is known only where the machine names it; on Windows a file gets its protected DACL's entries back with its time kept, a folder gets its DACL's, a descriptor written with the bits leaves no archive bit the capture did not have, a descriptor naming an unknown SID is not written and the SID is named, the DACL lands where the owner is refused, and one that does not read says so; on POSIX a target said to keep descriptors writes none
  - `Repository.Tests/RestoreSecurityDescriptorTests` — the profile writes descriptors only on Windows, passes them and its account rule to the writer, and the rule counts them applied for a file and a folder where it does; on Windows a real file with a protected DACL and its folder with an added entry come back with those entries through the executor, the plan declaring nothing and the receipt listing nothing
  - `Repository.Tests/RestoreMetadataHonestyTests` — the plan declares, on a line of its own, the files whose descriptor names an account the target does not know, with the rest one that does not read, and every descriptor where the target keeps none
  - `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` — on Windows the service's plan and receipt, and the command line's output, no longer list a descriptor

---

## Context

Capture records each entry's Windows security descriptor as `GetFileSecurityW` gives it: owner, primary group and DACL, self-relative (specification 06 §4.1 key 7). No restore wrote any back. The plan said, of every file and folder on Windows, that its descriptor would not be applied and it would take the permissions of the folder it landed in, and every receipt listed `security_descriptor` for every item. A file kept private came back as open as its new folder, and a file shared came back as closed. Architecture 06 §3 has Windows ACLs preserved on Windows.

A descriptor names accounts by SID. A SID is the number a machine or its domain gave an account, and it names that account only where that machine or domain is the one asked. A domain account's SID means the same on every machine in the domain. A local account's means nothing on another machine, nor on the same machine rebuilt, where the person's new account has a new SID. This is the problem [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) meets for a POSIX owner, which is captured by name and resolved on the target, and [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) meets for a Linux ACL, which names accounts by number and is written only where this installation captured it. A SID, unlike a number, can be asked about: the machine says whether it knows it.

## Decision

### 1. A descriptor is written as captured, in one call

The writer hands the captured descriptor to `SetFileSecurityW`: owner, group and DACL in one call, with the DACL's protection said outright, so a DACL that took nothing from its folder still takes nothing. The DACL keeps the entries its folder had given it when captured, each still marked as given. Restored in place, a file has exactly the access it had, and restored elsewhere, the access it had where it was. Nothing beneath a folder is walked.

The call does not keep the DACL's control bit that marks its inherited entries as given by automatic inheritance. The entries and their own marks land; that bit does not. Setting it is the work of `SetNamedSecurityInfoW`, which the alternatives below reject because it takes the inherited entries from the new folder.

### 2. Only where the machine knows every account it names

The writer asks the machine whether it knows each SID the descriptor names: the owner, the group, and every account its DACL grants or denies. It asks once per SID. If any is unknown, the descriptor is not written, the entry keeps its folder's access, and the item's detail names each unknown SID. A descriptor that does not read as one is not written either, and the detail says so.

It is all or nothing. Writing the DACL without the entries for unknown accounts could drop a deny, so the file would grant more than it did. Writing it whole would grant the file's access to nobody: on a rebuilt machine, the person's own new account would find their files closed to it.

### 3. Where the owner is refused, the DACL lands alone

Windows lets a process make an owner only of its own account, or of a group it may own by, unless it holds SeRestorePrivilege and has enabled it. It lets the owner of a file write its DACL. So where the call with the owner fails, the writer writes the DACL alone. The descriptor is then not applied, and the detail says that its owner and group were refused and that its access control list landed.

> **2026-10 ([Amendment 1](#amendment-1-2026-10--an-account-that-holds-the-restore-privilege-gives-an-owner-it-is-not)).** Where the restoring account holds SeRestorePrivilege, a write refused with the owner is made again with the privilege enabled, for that write alone, and the owner lands. The DACL lands alone only where the account does not hold it.

### 4. After the times, before the attribute bits

A DACL may take from the restoring account the access the writes before it need: the streams and the times. So the descriptor follows them. Writing a descriptor sets a file's archive bit, so the attribute bits ADR-0175 put last stay last, after it, and clear the bit where it was not captured. A DACL that withholds the right to write attributes from the restoring account leaves the bits unwritten, and the receipt says so for the file.

### 5. The plan predicts by the same rule

The plan's facts carry the SIDs each item's descriptor names. A target that writes descriptors asks whether it knows each one, as the writer will. Where it does not, the plan declares those files and folders on a line of their own, because no privilege mends that. A descriptor that does not read is declared with the rest. Where the target writes no descriptors, every one is declared as before. Whether the owner will be refused is the receipt's to say: the plan does not judge privilege on Windows.

> **2026-10 ([Amendment 2](#amendment-2-2026-10--the-plan-names-an-owner-the-restoring-account-may-not-give)).** The plan now judges it. Where the restoring account does not hold SeRestorePrivilege, a descriptor whose owner the account may not give is declared on a line of its own.

## Consequences

- On the machine that captured them, or one in the same domain, a file and a folder come back with their own access, owner and group.
- On another machine, a descriptor that names one of the old machine's local accounts is named in the plan and in the receipt. Its file takes its new folder's access, as every file did before.
- A plan on Windows declares descriptors only for those that name an account the machine does not know, and those that do not read.
- A file whose restored DACL withholds deletion from the restoring account is one a later restore with Replace may be refused over. That is said for the file.
- A file whose restored DACL withholds the right to write attributes from the restoring account does not get its attribute bits back. That is said for the file.

## What this does not do

- **Enable SeRestorePrivilege.** A restore gives an owner it is not only where the privilege is already enabled in its token. A service account that holds the privilege without enabling it has its owners refused, and its DACLs land alone.

  > **Built 2026-10 ([Amendment 1](#amendment-1-2026-10--an-account-that-holds-the-restore-privilege-gives-an-owner-it-is-not)).** A restore whose account holds the privilege enables it, on its own thread, for a descriptor write refused without it. An account that does not hold it still has its owners refused, and its DACLs land alone.
- **The SACL.** Capture does not record one: reading it needs SeSecurityPrivilege.
- **Map an account.** A SID the machine does not know is not mapped to one it does, by name or otherwise. Capture records SIDs, not names.
- **Refuse where the folder would grant more.** Architecture 06 §3 reserves refusal for a descriptor that cannot be applied into a place whose own access is broader. A descriptor that is not written is degraded and declared, as before. Comparing the two is not built.
- **Predict a refused owner in the plan.** The plan does not judge the restoring account's privilege on Windows.

  > **Built 2026-10 ([Amendment 2](#amendment-2-2026-10--the-plan-names-an-owner-the-restoring-account-may-not-give)).** It does now, by the privilege the target says its account holds (Amendment 1) and the owners its token lets it give without it.
- **Keep the mark of automatic inheritance.** A restored DACL lacks the control bit that marks its inherited entries as given by automatic inheritance (§1). Its entries, their own marks and its protection are as captured.
- **A link's descriptor.** No restore on Windows makes a symlink.

## Alternatives considered

- **Write the DACL through `SetNamedSecurityInfoW`, taking inherited entries from the new folder.** Rejected. It changes what was captured into what the new folder gives. Setting a folder's DACL also walks everything beneath it, and the restore sets folders deepest first, so the walk would repeat for every folder above each entry.
- **Write the DACL without the entries for unknown accounts.** Rejected. A deny dropped grants more than the captured descriptor did.
- **Write every descriptor whole, as a copy would.** Rejected. A descriptor naming only accounts the machine does not know grants nobody, and on a rebuilt machine that closes a person's own files to them.
- **Resolve unknown SIDs by account name.** Not possible: capture records SIDs, not names. It would be a capture change and a format question, as ADR-0087 records for a Linux ACL's numbers.

## Amendment 1 (2026-10) — an account that holds the restore privilege gives an owner it is not

**Gap.** Windows lets a process give a file an owner other than its own account, or a group it may own by, only with SeRestorePrivilege enabled. Administrators and backup operators hold the privilege, and an elevated administrator's token holds it disabled until a process enables it. No restore enabled it. So an administrator restoring another account's files left each one owned by the restoring account, and landed only its DACL. The receipt listed the descriptor as not applied for every such file.

**Decision.** The writer enables the privilege, where the restoring account holds it, for a write refused without it, and for nothing else:

- **The target says whether the account holds it.** `HoldsRestorePrivilege` joins `Domain/MetadataTarget` and `Restore/RestorePlan`'s profile. The local target asks the process's token, through `Domain/WindowsPrivileges`, so the executor, `restore-file` and the recovery tool all have it.
- **Only a write refused without it is made with it.** The writer first writes the descriptor as before. Most descriptors name the restoring account, or a group it may own by, as owner, and those land without the privilege. Where that write is refused and the account holds the privilege, the same write is made again with it enabled. Only where that is refused too does the DACL land alone, as §3 has it.
- **Enabled on one thread, for one call.** `WindowsPrivileges.RunWith` makes the call under a copy of the process's token on the calling thread, with the privilege enabled in that copy, and drops the copy once the call returns. The process's own token is left as it was. A privilege enabled there would hold for every thread: with SeRestorePrivilege so enabled, every open made with backup intent, as .NET makes one to set a folder's times, writes past the DACL of what it opens.
- **A thread already under a token of its own is left alone.** Dropping the copy ends any impersonation the thread had, so on such a thread the privilege is not enabled, and the write is refused as before.
- **A copy that cannot be dropped ends the process.** A thread left under it would carry the privilege, enabled, into the next work it is given.

The plan is unchanged. It still does not judge whether an owner will be refused, so it does not judge the privilege either.

**Built:** `Domain/WindowsPrivileges`; `Domain/WindowsSecurity`; `Domain/MetadataWriter`; `Domain/MetadataTarget`; `Restore/RestorePlan`:

- `Domain.Tests/WindowsPrivilegesTests`:
  - off Windows, no privilege is held and no call is made;
  - in an elevated process on Windows, the restore privilege is held and enabled for the call alone, on its thread under a token of its own. Once the call returns, the thread runs under the process's token again, and the privilege is as it was;
  - the call's own answer is the answer;
  - a privilege the account does not hold, and a name no privilege has, is neither held nor enabled, and the call is not made;
  - on a thread already impersonating, the call is not made, and the thread is still impersonating afterwards.
- `Domain.Tests/SecurityDescriptorWriteBackTests`:
  - the local target holds the privilege exactly where its account does;
  - on Windows, a target that does not hold it leaves an owner the account may not give unwritten, the file's owner as it was, and its DACL landed, and says so;
  - in an elevated process, a target that holds it gives the file that owner and its DACL, nothing is said, and the privilege and the thread are as they were.
- `Repository.Tests/RestoreSecurityDescriptorTests`: the local profile holds the privilege exactly where its account does, and passes it to the writer.

The privileged cases need an elevated process, and run where one runs, as on CI's Windows job; elsewhere they are reported as skipped.

## Amendment 2 (2026-10) — the plan names an owner the restoring account may not give

**Gap.** §5 left a refused owner to the receipt: the plan did not judge the restoring account's privilege. An account without SeRestorePrivilege, restoring files another account owned, was told before the run that their descriptors would be applied. After the run, the receipt listed each one as not applied, its owner and group refused. ADR-0083 holds the plan and the receipt to one rule, and here they disagreed. Amendment 1 had the target say whether its account holds the privilege, but the plan did not ask.

**Decision.** The plan judges an owner as the platform will:

- **Each item's facts carry the SID its descriptor names as owner** (`MetadataRules.DescriptorOwnerOf`, through `Restore/RestoreBlobSet`), as they carry every account it names.
- **The target says which owners its account may give without the privilege.** `MayOwnBy` joins `Restore/RestorePlan`'s profile, as `KnowsAccount` did: stated, so a plan for another account can be made and tested anywhere. The local profile answers by `WindowsAccounts.MayOwn`. That reads the process's token for the account's own SID, and for each group the token marks as one it may own by, as an elevated administrator's token marks the administrators' group.
- **An owner the account may not give is declared.** The plan judges it where the target writes descriptors and knows every account the descriptor names, and its account does not hold the privilege. An item whose descriptor names an owner the account may not give is declared on a line of its own, `security-descriptor-owners`. That line says the access control lists will land and the owner and group will not. Such an item is not also counted on the descriptors line.
- **What the plan already declares stays where it is.** A descriptor naming an account the machine does not know is not written at all, and stays on that line alone. One that does not read stays with the rest. One that names no owner is not judged.
- **Where the account holds the privilege, no owner is declared,** since the writer gives every owner then.

**Built:** `Domain/WindowsPrivileges`; `Domain/WindowsSecurity`; `Domain/MetadataRules`; `Restore/RestoreMetadata`; `Restore/RestoreBlobSet`; `Restore/RestorePlan`:

- `Repository.Tests/RestoreMetadataHonestyTests`, on every platform, against manifests the fake source writes, of a file the account may own by, one it may not, and one that names no owner:
  - on a target that writes descriptors and holds no privilege, the plan declares one file on the new line, and nothing on the descriptors line;
  - where the account holds the privilege, it declares no descriptor at all;
  - where a descriptor also names an account the target does not know, it is declared on that line alone;
  - where the target keeps no descriptors, all three are declared as before.
- `Repository.Tests/RestoreSecurityDescriptorTests`:
  - the local profile refuses a stranger and Local Service as owners, and a stated rule is the one it keeps;
  - on Windows, it may give its own account;
  - the executor's case plans clean with the privilege stated absent too, since the account captured those files itself.
- `Domain.Tests/SecurityDescriptorWriteBackTests`:
  - an owner that is a stranger, no SID, or Local Service is none the account may give;
  - on Windows, its own account is one;
  - in an elevated process, so is the administrators' group.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The writer every way of restoring shares writes a file's and a folder's security descriptor back on Windows, as captured, after the times and before the attribute bits, where the machine knows every account it names; one naming an account it does not know is not written and is named; the DACL lands alone where the owner is refused; and the plan predicts it per item. `Domain/WindowsSecurityDescriptor`; `Domain/WindowsSecurity`; `Domain/MetadataWriter`; `Restore/RestoreMetadata`; `Domain.Tests/WindowsSecurityDescriptorTests`; `Domain.Tests/SecurityDescriptorWriteBackTests`; `Repository.Tests/RestoreSecurityDescriptorTests` |
| 2026-10 | Amended | [Amendment 1](#amendment-1-2026-10--an-account-that-holds-the-restore-privilege-gives-an-owner-it-is-not): an account that holds SeRestorePrivilege gives a file an owner it is not; the writer enables the privilege, on its own thread, only for a descriptor write refused without it, and the process's token is left as it was. `Domain/WindowsPrivileges`; `Domain/MetadataWriter`; `Domain/MetadataTarget`; `Domain.Tests/WindowsPrivilegesTests`; `Domain.Tests/SecurityDescriptorWriteBackTests`; `Repository.Tests/RestoreSecurityDescriptorTests` |
| 2026-10 | Amended | [Amendment 2](#amendment-2-2026-10--the-plan-names-an-owner-the-restoring-account-may-not-give): the plan names, on a line of its own, a descriptor whose owner the restoring account may not give without SeRestorePrivilege, where it does not hold it, from the owner each item's facts now carry and the owners the account's token lets it give. `Domain/WindowsPrivileges`; `Domain/MetadataRules`; `Restore/RestoreMetadata`; `Restore/RestorePlan`; `Repository.Tests/RestoreMetadataHonestyTests`; `Repository.Tests/RestoreSecurityDescriptorTests`; `Domain.Tests/SecurityDescriptorWriteBackTests` |

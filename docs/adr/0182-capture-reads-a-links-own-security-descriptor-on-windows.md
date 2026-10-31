# ADR-0182 — Capture reads a link's own security descriptor on Windows

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-MAN-003 (amended: on Windows a link's captured security descriptor is the link's own); FR-RST-004 (amended by [Amendment 1](#amendment-1-2026-10--a-restore-writes-a-links-own-descriptor-back): a restore writes a link's own descriptor back onto it)
**Related:** [ADR-0176](0176-a-restore-writes-back-a-windows-security-descriptor-whose-accounts-the-machine-knows.md) (the descriptor capture records, and its audit list since Amendment 3), [ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md) (a link's own metadata, captured never following it), [ADR-0181](0181-a-restore-on-windows-makes-a-symlink-where-the-account-may.md) (a restore on Windows makes links, and Amendment 2's handle on the link itself), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (symbolic links: "preserve, privilege permitting" on Windows), [specification 06 §4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map) (key 7)

**Built:**
- The read: `Filesystem.Local/WindowsInterop` reads a link's descriptor through a handle opened on the link itself, and `Filesystem.Local/LocalFileSystemSource` asks it for a link's.
- The tests:
  - `Filesystem.Tests/LocalScanTests` — on Windows: a link to a file kept to an account the link's folder does not name is captured as a link whose DACL is its folder's, while the file's names that account; a folder's link is captured as a link, never descended into, with its own DACL, while the folder it points at keeps its own; a link to nothing still carries a descriptor; and, where the account holds the privilege an audit list is read with, a link that takes one from its folder is captured with it.

---

## Context

Capture records each entry's Windows security descriptor as `GetFileSecurityW` gives it (ADR-0176). That call opens the path it is given, and for a link that is what the link points at. So a link's manifest could carry the descriptor of its target: a file elsewhere in the tree, a file outside it, or, for a link to nothing, no descriptor at all.

Everything else capture reads of a link on Windows is the link's own. Its times and attribute bits come from the path's query, which reports a link's own. Its identity comes from a handle opened on the reparse point. Only the descriptor followed the link.

A link's descriptor is its own on Windows. A link takes its DACL from its folder, as a file made there does, and may be given one of its own. Nothing yet writes a link's descriptor back, so no restore has carried the wrong one onto anything. But a manifest that names a target's accounts says something about the link that is not true, and a restore that one day writes it would write the target's permissions onto the link.

## Decision

### 1. A link's descriptor is read through a handle on the link

A link's descriptor is read through a handle opened on the reparse point itself (`FILE_FLAG_OPEN_REPARSE_POINT`, with backup semantics so that a folder's link opens) with `GetKernelObjectSecurity`. That call returns the same self-relative form `GetFileSecurityW` does, so the manifest's key 7 is unchanged in form. The no-follow comes from the open's own flag, which is how ADR-0101 holds every write onto a link, and how [ADR-0181 Amendment 2](0181-a-restore-on-windows-makes-a-symlink-where-the-account-may.md#amendment-2-2026-10--a-links-own-times-on-windows) holds a link's times.

### 2. The handle asks for no more than the read

The handle asks the right to read the descriptor and nothing of what the link holds. Where the audit list is read, it also asks the right to read that, which SeSecurityPrivilege grants only while it is enabled. It is enabled for that read alone, as ADR-0176 Amendment 3 enables it for a file's.

### 3. Files and folders are read as before

Only a link is read this way. A file's and a folder's descriptor is read by its path, as before.

## Consequences

- A link's manifest names only the accounts its own descriptor names. One whose target sits outside the tree no longer carries that target's permissions into the repository.
- A link to nothing carries a descriptor, where it carried none.
- The metadata digest counts the descriptor. A link whose captured descriptor differs from the one its last manifest carries gets a new manifest at its next backup. A link has no content, so nothing is read again.

## What this does not do

- **Write a link's descriptor back.** A restore still writes a descriptor only onto files and folders (ADR-0176, ADR-0181), and a link's is declared and listed as not applied. With the link's own descriptor captured, writing it back through the same kind of handle is the next record's.

  > **2026-10: taken up by [Amendment 1](#amendment-1-2026-10--a-restore-writes-a-links-own-descriptor-back).**
- **Junctions.** A junction is read as a link, so its own descriptor is captured too. It still comes back as a folder's symbolic link (ADR-0181).

## Alternatives considered

- **Leave the read as it was, since nothing restores a link's descriptor.** Rejected. The manifest would go on naming a target's accounts as the link's, and the restore that writes it back would inherit that.
- **Read every entry through a handle.** Rejected for now. A file's and a folder's descriptor is the same whichever way it is read, and the path's read is the one every capture so far has made.

## Amendment 1 (2026-10) — a restore writes a link's own descriptor back

The record captured a link's own descriptor and left a restore writing one only onto files and folders. `SetFileSecurityW`, which writes a file's, opens what a link points at, so it could not be used on a link. Every link captured with a descriptor was declared and listed as not applied.

**Decision.** A link made on Windows gets its own descriptor back, by [ADR-0176](0176-a-restore-writes-back-a-windows-security-descriptor-whose-accounts-the-machine-knows.md)'s rule, onto the link and never through it, by the executor and the recovery tool alike:

- **through a handle on the link.** It is written with `SetKernelObjectSecurity` through a handle opened on the reparse point itself, with backup semantics so a folder's link opens;
- **asking no more than the write needs.** The handle asks the owner's right for an owner or a group, the DACL's for a DACL, and the audit list's for an audit list. The privileges a write runs with (ADR-0176 Amendments 1 and 3) are enabled around the open as well as the write, because the open is where the rights are asked for;
- **by the file's rule, in the file's order.** It goes on after the times and before the attribute bits. An account the machine does not know, an owner the account may not give and an audit list it may not write are refused and named as they are for a file, and the plan counts a link on those lines as it counts a file.

[ADR-0178](0178-the-plan-names-a-file-its-new-folder-would-open-wider-than-it-was.md)'s line for a file its new folder would open wider still judges files and folders alone. A link's DACL guards nothing it holds.

**Built:** `Domain/WindowsSecurity`; `Domain/SymlinkMetadata`; `Domain/MetadataWriter`; `Restore/RestoreMetadata`:

- `Domain.Tests/SymlinkMetadataTests`: only Windows writes a link's descriptor;
- `Domain.Tests/SecurityDescriptorWriteBackTests`: on Windows a link to a file and a link to nothing are given the protected DACL captured, read back through a handle on the link, and the file the link names keeps its own;
- `Repository.Tests/RestoreWindowsLinkTests`: on Windows two links come back with the DACL captured, nothing declared or listed, and the file one names keeps its own; on every platform the rule grants a link's descriptor exactly where the platform writes one;
- `Repository.Tests/RecoveryLinkTests`: on Windows the recovery tool gives a link a DACL kept to an account, and the file it names names no such account;
- `TestSupport/LinkSecurity`: the oracle, a link's own descriptor read without following it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice. On Windows a link's security descriptor is read through a handle opened on the link itself, never what it points at. `Filesystem.Local/WindowsInterop`; `Filesystem.Local/LocalFileSystemSource`; `Filesystem.Tests/LocalScanTests` |
| 2026-10 | Accepted (Amendment 1) | A restore on Windows writes a link's own descriptor back through a handle on the link, by ADR-0176's rule, by the executor and the recovery tool alike. `Domain/WindowsSecurity`; `Domain/SymlinkMetadata`; `Domain/MetadataWriter`; `Restore/RestoreMetadata`; `Domain.Tests/SymlinkMetadataTests`; `Domain.Tests/SecurityDescriptorWriteBackTests`; `Repository.Tests/RestoreWindowsLinkTests`; `Repository.Tests/RecoveryLinkTests`; `TestSupport/LinkSecurity` |

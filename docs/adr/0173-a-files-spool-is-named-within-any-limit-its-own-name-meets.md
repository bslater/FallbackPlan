# ADR-0173 — A file's spool is named within any limit its own name meets

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-005 (amended: a file whose name is as long as its target holds restores, through the executor and the recovery tool)
**Related:** [ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md) (the rule for where a restore writes a path, which the recovery tool shares, and the spool a link must carry nothing out of), [ADR-0170](0170-a-restore-plan-judges-names-by-the-targets-naming-rules.md) (the names a plan says a target cannot make)

**Built:**
- The rule: `Domain/RestorePaths` names a file's spool beside it: the writer's prefix, `.fbp-restore-` or `.fbp-recover-`, sixteen hex digits of a hash of the file's own name, and `.tmp`. That is 33 characters whatever the file is called.
- The writers: `Restore/RestoreExecutor` and `Recovery/RecoverySession` spool by it.
- The tests:
  - `Domain.Tests/RestoreSpoolTests` — a spool is beside its file under a name of one length, for a short name and for one of 255 bytes in ASCII or in three-byte characters. The same file has the same spool, and another file, or the same name in another case, another. The executor's and the recovery tool's are apart.
  - `Repository.Tests/RestoreLongNameTests` — a file whose name is 255 bytes, in ASCII or mostly in three-byte characters, restores through the executor byte for byte, and no spool is left.
  - `Repository.Tests/RecoveryContainmentTests` — one named with 255 ASCII characters restores through the recovery tool. A link standing at a file's spool still carries nothing out of the output.

---

## Context

Each writer writes a file whole beside its destination and then renames it into place. A reader never meets half a file, and the move is one rename on one volume. The executor named that spool by adding `.fbp-restore-tmp` to the destination's name, and the recovery tool by adding `.fbp-recover-tmp`. Either is sixteen characters more in the same component.

A volume holds a name up to a limit: 255 bytes on the common Linux filesystems, and 255 UTF-16 units on NTFS. A name of 240 to 255 fits the volume it was captured from and the one it restores to, but its spool's name did not. Making the spool failed as a name too long, so the file failed, named in the receipt, though the target could hold its name. From Linux to Linux, a file named with 250 bytes could be restored by neither writer. The plan said nothing beforehand, since it judges the file's name and not its spool's.

## Decision

### 1. One rule names the spool

`RestorePaths.SpoolBeside` names it, in Domain beside the rule for where a path is written, since the recovery tool may not reference the restore project (architecture 11 §2). The name is the writer's prefix, sixteen hex digits of SHA-256 over the file's own name, and `.tmp`: 33 characters, under any limit a volume sets.

### 2. A file has the same spool every time

The digits come from the file's name, so a destination's spool is the same on every run. A run killed while writing a file leaves its spool, and the next run to land that file writes over it, as before. Two files in one folder share a spool only if 64 bits of their names' hashes agree. Each writer lands one file at a time, so even two that did would never be written at once.

## Consequences

- A file restores wherever its name fits, through either writer.
- A spool a killed run left is no longer named after its file. It is found by the writer's prefix, and on POSIX it is a dot file, so a plain listing does not show it.

## What this does not do

- **Name a copy written beside under rename.** Under the rename policy, a file in the way keeps its name, and the restored one lands as the name with " (restored" and the date before its extension, 22 characters more, and more after the first such copy. A name that close to its limit fails there, named, as before.
- **Judge a target's limit in the plan.** A POSIX target's limit in bytes is still not judged (ADR-0170). A name longer than the target holds, captured on Windows, fails at execution, named, rather than in the plan.

## Alternatives considered

- **Keep the old name where it fits, and shorten it only where it does not.** Rejected. Whether it fits depends on the volume's limit, which neither writer knows, and two forms of one spool is one more thing to look for in a folder.
- **Spool in a folder of the writer's own.** Rejected. The move must be one rename on one volume, and a folder elsewhere may be on another.
- **Cut the destination's name short in the spool.** Rejected. Two names that start alike would share a spool. A hash keeps them apart.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A file's spool is named in 33 characters, so a file whose name is as long as its target holds restores through the executor and the recovery tool. No contract or format change. |

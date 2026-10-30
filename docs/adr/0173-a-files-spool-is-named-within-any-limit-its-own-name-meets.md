# ADR-0173 — A file's spool is named within any limit its own name meets

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-005 (amended: a file whose name is as long as its target holds restores, through the executor and the recovery tool), FR-RST-002 (amended by Amendment 1: a single-file restore that fails leaves its output as it was)
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

Each writer writes a file whole beside its destination and then renames it into place. A reader never meets half a file, and the move is one rename on one volume.

> **2026-10 ([Amendment 1](#amendment-1-2026-10--the-single-file-restore-lands-its-file-the-same-way)).** A third writer did not: the command line's `restore-file` wrote straight into its output, and deleted it when the restore failed. It now lands its file this way too.

 The executor named that spool by adding `.fbp-restore-tmp` to the destination's name, and the recovery tool by adding `.fbp-recover-tmp`. Either is sixteen characters more in the same component.

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

  > **Built 2026-10 for Linux ([ADR-0170 Amendment 1](0170-a-restore-plan-judges-names-by-the-targets-naming-rules.md#amendment-1-2026-10--a-name-longer-than-a-linux-target-holds)).** The plan names such a name on a Linux target before any byte moves. It still fails at execution, named, as before.

## Alternatives considered

- **Keep the old name where it fits, and shorten it only where it does not.** Rejected. Whether it fits depends on the volume's limit, which neither writer knows, and two forms of one spool is one more thing to look for in a folder.
- **Spool in a folder of the writer's own.** Rejected. The move must be one rename on one volume, and a folder elsewhere may be on another.
- **Cut the destination's name short in the spool.** Rejected. Two names that start alike would share a spool. A hash keeps them apart.

## Amendment 1 (2026-10) — the single-file restore lands its file the same way

The command line's `restore-file` opened its output with a create that truncates, before the engine had verified a byte. When the restore then failed, it deleted the output. So a restore that failed on damage destroyed whatever stood at the output and kept nothing in its place. That might be the very file the person meant to restore over. A link at the output was written through, so its target was what the restore truncated.

**Decision.** `restore-file` lands its file as the executor and the recovery tool do:

- the engine writes into a spool beside the output, named by §1's rule, made new after whatever stood at its name is removed;
- the spool is moved over the output only once the restore succeeds;
- the spool is deleted on every way out that does not move it.

A restore that fails leaves the output as it was. A link at the output is replaced by the restored file, and nothing is written through it, as the executor's rule has it ([ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md)). Metadata is still written back after the content has landed ([ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md)).

The command's type now renames a file into place, so by [ADR-0010 Amendment 9](0010-local-store-separation.md#amendment-9-2026-10--a-file-a-store-renames-into-place-is-read-sharing-deletion-too)'s rule its one direct read, of a kept plan, goes through `AtomicFile`, which shares deletion as a rename on Windows needs.

**Built:** `Cli/CliApplication`; `Cli.Tests/InspectionCommandTests`:

- with every data blob damaged past its envelope, `restore-file` fails, the file that stood at its output reads as it did, and no spool is left;
- over a longer file already there, the restored file replaces it whole, and no spool is left;
- on POSIX, at a link, the output becomes the restored file and the link's target is untouched.

`ArchitectureTests/StateFileReadTests` named the kept plan's read once the type renamed a file, and holds it.

**Not done.** The engine still assembles the file in a working spool of its own, in the system's temporary directory for this command, and needs room there for a dense file's whole length, as the restore pipeline review records.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A file's spool is named in 33 characters, so a file whose name is as long as its target holds restores through the executor and the recovery tool. No contract or format change. |
| 2026-10 | Accepted (related) | [ADR-0170 Amendment 1](0170-a-restore-plan-judges-names-by-the-targets-naming-rules.md#amendment-1-2026-10--a-name-longer-than-a-linux-target-holds): a name longer than a Linux target holds is named in the plan. `Restore/RestorePlan` |
| 2026-10 | Amended | Amendment 1: the command line's `restore-file` writes its file whole beside its output and moves it there once verified, so one that fails leaves the output as it was, and a link at the output is replaced rather than written through. `Cli/CliApplication`; `Cli.Tests/InspectionCommandTests` |

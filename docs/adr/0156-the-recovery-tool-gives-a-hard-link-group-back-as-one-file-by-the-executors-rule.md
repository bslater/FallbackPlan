# ADR-0156 — The recovery tool gives a hard-link group back as one file, by the executor's rule

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004, FR-DRL-001
**Related:** [ADR-0102](0102-a-restore-gives-a-hard-link-group-back-as-one-file.md) (the rule, and the call that makes a link), [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) (which recorded this gap), [ADR-0155](0155-the-recovery-tool-makes-a-link-a-link-and-fails-only-the-entry-the-output-refuses.md) (how the tool replaces what stands at a name a link takes), [architecture 11 §2](../architecture/11-solution-structure.md#2-dependency-rules)

**Built:**
- The rule: `Repository.Format/Manifests/HardLinkGroups` holds what a later name of a hard-link group must agree on with its first name, moved unchanged from the restore project. `Restore/RestoreExecutor`, `Restore/RestoreSpace` and `Restore/RestoreBlobSet` group names by it as before.
- The tool: `Recovery/RecoverySession` makes a later name of a group whose first name landed a link to that file, through `Domain/HardLinks`, where it agrees with that name and the output makes one. A file standing at the name is replaced. A name that does not agree, or that the output will not link, is restored as a file of its own with a note saying why. A linked name is counted as its first name was in what did not land.
- The tests:
  - `Repository.Tests/EndToEnd/RecoveryHardLinkTests` — a group comes back as one file under each of its names; a name whose content or metadata differs from its group's first, or that the output will not link, is restored as a file of its own and says why; a file at a later name is replaced by the link; a linked name is counted as its first name is in what did not land
  - `ArchitectureTests/DependencyRuleTests` — the executor and the recovery tool group hard links by the shared rule, not a copy of it

---

## Context

Capture records every file that is one of several names for one file, and the executor gives such a group back as one file under each name ([ADR-0102](0102-a-restore-gives-a-hard-link-group-back-as-one-file.md)). A later name is linked to the file its group's first name landed as, but only where it agrees with that name in content, length and every attribute but the access time. The group key alone does not prove two names were one file.

The recovery tool restored every name as a file of its own. A tree recovered from the passphrase alone came back with each group's content repeated once per name. That is right in every byte, but larger than was captured, and no longer one file. [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) recorded that.

The call that makes a link already lives in Domain, for both. The rule that says when to make one lived in the restore project, which the tool may not reference ([architecture 11 §2](../architecture/11-solution-structure.md#2-dependency-rules)).

## Decision

### 1. One rule, in the format library

What a later name must agree on moves, unchanged, into the format library, beside the manifest it reads. The executor groups names by it as it always has.

The format library is the one assembly both the restore project and the recovery tool reference that holds the manifest's codec. The rule hashes the manifest's metadata, so it cannot go to Domain.

### 2. The tool gives a group back as the executor does

The tool keeps each group's first name once it has landed, with what that file got of its metadata. A later name that agrees with it is then made a link to that file. Whatever stands at its name, a file or a link, is replaced first, as [ADR-0155](0155-the-recovery-tool-makes-a-link-a-link-and-fails-only-the-entry-the-output-refuses.md) replaces it for a symlink. Nothing is read or verified for that name, since it is the file that was.

A later name that does not agree is restored as a file of its own, and so is one the output will not link, such as on a volume that keeps no links. Each such name gets a note saying why.

A name whose first name did not land is restored as a file of its own and becomes the group's first.

A linked name is the first name's file, so what that file did not get, it did not get. The report counts it so.

## Consequences

- A tree recovered with the tool has its hard-link groups back as one file each, where the output keeps links.
- A group whose names differ is not merged, so no name gets another's content or attributes.
- The rule is one copy, and a canary fails if either the executor or the tool groups names by another.

## What this does not do

- **Restore a special file.** A device, a pipe or a socket is still skipped with a note.
- **Link names across two runs.** A group is formed within one run; a name restored by an earlier run is not linked to.

## Alternatives considered

- **Give the tool its own copy of the rule.** Rejected. Two copies of a rule drift apart, as [ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md) found for containment.
- **Link every name of a group, agreeing or not.** Rejected. The key names a device's identity and a file's, not a filesystem's, so two different files can share one.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The rule a later name of a hard-link group is held to moves, unchanged, into the format library. The recovery tool makes each agreeing later name a link to the file its first name landed as, and restores one that does not agree, or that the output will not link, as a file of its own with a note. |

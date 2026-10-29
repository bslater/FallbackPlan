# ADR-0152 — The recovery tool keeps every path inside its output, by the executor's rule

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-005, FR-DRL-001
**Related:** [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) (which recorded this gap), [ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md) (the same move, for the metadata writer), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) §4 (never through a link), [architecture 08 §5](../architecture/08-restore-and-recovery.md#5-emergency-recovery), [architecture 11 §2](../architecture/11-solution-structure.md#2-dependency-rules)

**Built:**
- The rule: `Domain/RestorePaths` resolves a repository path under the folder a restore writes into. It accepts only a sequence of plain names, kept under that folder lexically, through no folder that is a link leading out of it. `Restore/RestoreExecutor` resolves every item through it, and its receipts word a refusal as before.
- The recovery tool: `Recovery/RecoverySession` holds each tree name to the rule on its own, then places the entry's path by it. A path it refuses is noted and failed with what it would have carried, and nothing is written through a link out of the output. It removes whatever stands at a file's spool name before making the spool new.
- The tests:
  - `Repository.Tests/EndToEnd/RecoveryContainmentTests` — a folder the output already holds that is a link out of it is written through by nothing, the file under it is refused by name, and the rest still restores; a link standing at a file's spool name is replaced, never written through
  - `ArchitectureTests/DependencyRuleTests` — the executor and the recovery tool resolve restore paths through the shared rule, not a copy of it

---

## Context

The executor and the recovery tool both write names a repository's trees supply under a folder a person chose. A repository is exactly as untrusted for the one as for the other.

The executor refuses a path that is not a sequence of plain names, one that resolves outside its root, and one that passes through a folder that is a link leading out of the root. That last check catches a link planted in the output before the run, or one restored by an earlier item.

The recovery tool held each tree name to its own copy of the first check and to nothing else. So a link already standing in the output, `docs` pointing anywhere, carried every file under `docs` to wherever it pointed, with the metadata [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) now writes back. ADR-0150 recorded that and advised recovering into a new, empty folder.

The rule lived in the restore project, which the recovery tool may not reference ([architecture 11 §2](../architecture/11-solution-structure.md#2-dependency-rules)).

## Decision

### 1. One rule, in Domain

The executor's path resolution moves into Domain as it was, with three parts:
- each component must be a plain name;
- the joined path must stay lexically under the root;
- no folder between the root and the destination may resolve, through its links, outside the root.

The executor resolves every item through it and words a refusal as it always has.

> **2026-10 ([ADR-0159](0159-a-restore-into-a-folder-that-is-itself-a-link-restores-what-lies-below-it.md)).** The third part followed the root through its link but read each folder below it by its spelling, so a root that is a link refused every path below its top level. It now follows the root and each folder below it in turn.

### 2. The recovery tool places every entry by it

The tool holds a tree's name to the plain-name rule on its own first, since a name that holds a separator reads as two names once joined. It then resolves the entry's path under the output by the shared rule, for folders and files alike.

A path it refuses is noted as refused, with the rule's reason, and failed with the subtree it would have carried. The rest of the drill continues, as it did for a name that was not plain.

### 3. A spool is made new, never opened

The tool spools each file beside its destination, under the destination's name with a suffix of its own, and renames it into place once it has verified. Opening that name for writing followed a link standing there, so the spool went wherever the link pointed.

The tool now removes whatever stands at the spool's name, a link included, and then makes the spool new. Making it new refuses a name that has appeared again in between, which fails the run rather than writing through it.

## Consequences

- A recovery into a folder that holds a link out of it writes nothing through the link. Each file under the link is refused by name, so the note says where the tree tried to go. A link at a spool's name is replaced, so a file still lands where it should.
- Every way of restoring now resolves a repository's names by one rule, and a canary fails if the executor or the tool resolves them through a copy.
- The tool's note for a name that is not plain now gives the executor's reason, such as `the manifest path contains a '..' component`, where it gave its own.

## What this does not do

- **Close the window between the check and the write.** As in the executor, the path is checked before it is written, not in one step with it. An account that can write in the output while the tool runs can still swap a folder for a link between the two ([ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) §4 records the same window for the executor). Recovering into a folder no other account can write to closes it.
- **Refuse a link the output holds as a file's destination.** A file lands over whatever stands at its destination, a link included, by renaming its verified spool into place. It does not write through the link.

## Alternatives considered

- **Keep the tool's own copy and add the link check to it.** Rejected. Two copies of a containment rule drift apart, which is how this gap arose.
- **Refuse to recover into a folder that is not empty.** Rejected. A person recovering into a folder that already holds part of an earlier attempt is the common case, and the rule already makes that safe.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The executor's path resolution moves into Domain unchanged, and the recovery tool places every folder and file by it, so a link the output holds is written through by nothing. A file's spool is made new after whatever stood at its name is removed. |
| 2026-10 | Accepted | [ADR-0159](0159-a-restore-into-a-folder-that-is-itself-a-link-restores-what-lies-below-it.md): the rule follows the root and each folder below it through their links, so a restore into a folder that is a link, by the executor or the recovery tool, restores what lies below its top level. |

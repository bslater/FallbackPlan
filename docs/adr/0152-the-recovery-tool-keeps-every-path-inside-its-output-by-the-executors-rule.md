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

> **2026-10 ([Amendment 1](#amendment-1-2026-10--the-executors-spool-is-made-new-too)).** The executor opened its spool the same way, and a repository could put a link there. It now makes its spool new too.

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

## Amendment 1 (2026-10) — the executor's spool is made new too

§3 closed this hole for the recovery tool alone. The executor also writes each file whole into a spool beside its destination and renames it into place, and it made that spool with a create that opens whatever stands at the name, following a link. A repository is untrusted, the spool's name follows from the file's ([ADR-0173](0173-a-files-spool-is-named-within-any-limit-its-own-name-meets.md), and before it the file's own name with a suffix), and the executor restores links. So a repository that restored a link at the name a later file spools under, leading out of the restore, had that file's content written wherever the link led, as the account the restore runs as. A folder at that name was a second fault: the create was refused as unauthorised, which the item's containment did not catch, so the run ended with no receipt.

**Decision.** The executor makes its spool as the recovery tool does:

- whatever stands at the spool's name is removed, a link itself and never its target;
- the spool is made new, which refuses a name that has appeared again in between and fails that file rather than writing through what stands there;
- the item's containment catches a refusal as unauthorised beside an I/O fault, so a folder at the spool's name fails that file alone, named, and the run goes on.

**Built:** `Restore/RestoreExecutor`; `Repository.Tests/RestoreSpoolContainmentTests`:

- on POSIX, a link a repository restores at a file's spool name, leading to a file outside the restore, is replaced: the file outside reads as it did, and the restored file lands as a file with its content;
- a folder a repository restores at a file's spool name fails that file alone, and a file after it restores.

> **2026-10 ([Amendment 4](#amendment-4-2026-10--an-entry-a-snapshot-names-at-a-files-spool-is-refused)).** Such an entry is now refused before it is made, so these two cases put the link and the folder in the output before the run, as a run in place of another snapshot can leave one.

**Not done.** A link a repository restores at a later file's spool name is removed when that file spools, and the receipt still counts the link as restored. Only a repository that means harm names a link so, and nothing it names is written through.

> **2026-10 ([Amendment 4](#amendment-4-2026-10--an-entry-a-snapshot-names-at-a-files-spool-is-refused)).** Taken up: the plan names such an entry, the executor fails it and the recovery tool refuses it, each saying which file spools there.

## Amendment 2 (2026-10) — a displaced file is placed under the output's root

A restore in place under the Preserve policy moves a file it would replace aside, into a folder of the run's own under `.fbp-displaced` at the output's root. The executor resolved each displaced path with that run's folder as the containment root. The rule follows a root through its own links ([ADR-0159](0159-a-restore-into-a-folder-that-is-itself-a-link-restores-what-lies-below-it.md)), since a root is the person's choice. This one is not the person's: its name is fixed. A repository could restore a link named `.fbp-displaced`, leading out of the output, before it restored the file that displaces one, and the person's own file was moved wherever the link led.

**Decision.** A displaced path is resolved as `.fbp-displaced/<run>/<path>` under the output's own root, by the rule every restored path is resolved by. A link at `.fbp-displaced` that leads out of the output is refused there like any other. The item fails, naming the link, and the person's file stays where it was. A link that leads elsewhere inside the output is followed, as ADR-0159 has it. All three places that move something aside go through it: a file under Preserve, a symlink's destination, and a hard-link group's later name.

**Built:** `Restore/RestoreExecutor`; `Repository.Tests/RestoreDisplacedContainmentTests`:

- on POSIX, a repository restores a link named `.fbp-displaced` leading to a folder elsewhere, then a file over one the person already has: nothing lands elsewhere, the person's file reads as it did, and the item fails, naming the link;
- on POSIX, the same link, then a symlink over a file the person has, or a hard-linked group's later name over one: nothing lands elsewhere at either place, and each item fails, naming the link, while the group's first name restores;
- the control: with nothing in the way, the person's file is displaced into the run's own folder as it always was.

**Not done.** The quarantine folder, `.fbp-quarantine/<run>`, is still the run's root and still followed through a link standing at its name. A repository cannot put one there in the run that quarantines, since everything it names lands below that folder. An earlier run in place into the same output, or an account that can write in the output, can. Refusing that needs a refusal of the whole run, which the executor does not have.

> **2026-10 ([Amendment 3](#amendment-3-2026-10--a-quarantined-runs-folder-is-resolved-under-the-outputs-root)).** Taken up: the run's folder is resolved under the output's root before anything is written, and a link there leading out refuses the run, failing every item it planned.

## Amendment 3 (2026-10) — a quarantined run's folder is resolved under the output's root

Amendment 2 left the quarantine folder as it found it. A quarantined run lands in `.fbp-quarantine/<run>` under the output, and the executor took that folder as the run's root. The rule follows a root through its own links ([ADR-0159](0159-a-restore-into-a-folder-that-is-itself-a-link-restores-what-lies-below-it.md)), as it should for a folder the person chose, but this one's name is fixed. A run in place into the same output restores whatever a repository names there, a link named `.fbp-quarantine` included. A later quarantined run into that output then wrote its whole restore wherever the link led.

**Decision.** The run's folder is resolved as `.fbp-quarantine/<run>` under the output's own root, by the rule every restored path is resolved by, before anything is written:

- **a link there that leads out of the output refuses the run.** Nothing is written, not even the run's folder. The executor has no refusal of its own for a whole run, and none is added. The receipt fails every item the plan names, each saying the run's quarantine folder was refused and why. So the run is reported failed as any run with a failed item is, and its receipt is kept;
- **a link there that leads elsewhere inside the output is followed,** as ADR-0159 has it;
- **a resumed run's folder is the run it continues,** resolved the same way.

**Built:** `Restore/RestoreExecutor`; `Repository.Tests/RestoreQuarantineContainmentTests`:

- on POSIX, a run in place restores a repository's link named `.fbp-quarantine`, leading to a folder elsewhere. A quarantined run of the same snapshot into the same output then writes nothing there, and fails every item it planned, naming the link;
- on POSIX, a link at `.fbp-quarantine` leading to a folder inside the output is followed, and the run lands below it;
- the control: with nothing at the name, the run lands in its own folder below `.fbp-quarantine`, as it always did.

**Not done.** A link that an account able to write in the output puts at the quarantine folder's name between the check and the write is still followed. The threat model already records that such an account can redirect a write between a check and the write.

## Amendment 4 (2026-10) — an entry a snapshot names at a file's spool is refused

Amendment 1 left one thing at a file's spool name undone. Both writers remove whatever stands there before making the spool new, so nothing standing there is written through. An entry the snapshot itself names at that name, or below it, was restored before the file, then removed when the file spooled. So the receipt, or the recovery tool's report, counted restored an entry that was no longer there. Below a folder so named, an entry made the folder as its parent, and the file then failed for a folder at its spool.

**Decision.** The spool's name is known from the file's name alone, before anything is written, so it is held as the run's own:

- **`Domain/RestorePaths` gives a spool's name from the file's name alone,** and builds the spool's path on it, so the two cannot differ;
- **the plan names each item at a file's spool name, or below it, as a conflict,** saying which file spools there, before any byte moves, as it names every name the executor would refuse ([ADR-0170](0170-a-restore-plan-judges-names-by-the-targets-naming-rules.md));
- **the executor fails each such item, named, before it is made,** so the file restores;
- **the recovery tool refuses such an entry,** noting which file spools there, and carries nothing below it;
- **names are compared without regard to case.** A target that folds case takes every spelling of a name for one. On one that does not, refusing a case variant of a writer's own spool name costs nothing anyone captured on purpose.

The removal before a spool is made stays. It is what holds against what an earlier run, or another account, left at the name.

**Built:** `Domain/RestorePaths`; `Restore/RestorePlan`; `Restore/RestoreExecutor`; `Recovery/RecoverySession`:

- `Domain.Tests/RestoreSpoolTests`: a spool's name is the one its path is given;
- `Repository.Tests/RestoreSpoolContainmentTests`: a file the snapshot names at a file's spool, as spelled and in another case, is the plan's one conflict and fails, naming the file, which restores, and nothing stands at the name after. A folder so named fails with everything below it. On POSIX, a link so named is not made, and nothing is written through it;
- `Repository.Tests/RecoveryContainmentTests`: the recovery tool refuses such an entry, as spelled and in another case, naming the file, and restores the file.

Amendment 1's two cases now put the link, and the folder, in the output before the run, as a run in place of another snapshot can leave one. Named in the snapshot, they would never be made, and the removal they hold the executor to would go untested.

**Not done.** Whatever already stands in the output at a spool's name is still removed when the file spools, whoever put it there, as Amendment 1 has it. Only the writers make such names, and one a run cut short left is the one the next run writes over.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The executor's path resolution moves into Domain unchanged, and the recovery tool places every folder and file by it, so a link the output holds is written through by nothing. A file's spool is made new after whatever stood at its name is removed. |
| 2026-10 | Accepted | [ADR-0159](0159-a-restore-into-a-folder-that-is-itself-a-link-restores-what-lies-below-it.md): the rule follows the root and each folder below it through their links, so a restore into a folder that is a link, by the executor or the recovery tool, restores what lies below its top level. |
| 2026-10 | Amended | Amendment 1: the executor makes its spool new after removing whatever stands at its name, as §3 has the recovery tool do, so a link a repository restores there carries no file's content out of the restore, and a folder there fails that file alone rather than ending the run. `Restore/RestoreExecutor`; `Repository.Tests/RestoreSpoolContainmentTests` |
| 2026-10 | Amended | Amendment 2: a displaced path is resolved under the output's root, so a link a repository restores at the displaced folder's name, leading out, is refused, and the person's file stays where it was. `Restore/RestoreExecutor`; `Repository.Tests/RestoreDisplacedContainmentTests` |
| 2026-10 | Amended | Amendment 3: a quarantined run's folder is resolved under the output's root, so a link an earlier run in place restored at the quarantine folder's name, leading out, refuses the run before anything is written. `Restore/RestoreExecutor`; `Repository.Tests/RestoreQuarantineContainmentTests` |
| 2026-10 | Amended | Amendment 4: an entry a snapshot names at a file's spool name, or below it, is the plan's conflict, and the executor fails it and the recovery tool refuses it, each naming the file. `Domain/RestorePaths`; `Restore/RestorePlan`; `Recovery/RecoverySession`; `Repository.Tests/RestoreSpoolContainmentTests` |

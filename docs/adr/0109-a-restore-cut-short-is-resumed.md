# ADR-0109 — A restore cut short is resumed

**Status:** Accepted (amended 2026-10: [Amendment 1](#amendment-1-2026-10--a-resume-under-rename-looks-beside))
**Date:** 2026-10
**Requirements:** FR-RST-008 (new: a restore cut short is resumed, leaving what it landed and landing the rest; amended by Amendment 1 for rename), FR-RST-004 (amended: the receipt says which files a resumed run found in place)
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (the room a run needs, and the record that left resume owed), [ADR-0107](0107-the-command-line-plans-a-restore-on-its-own.md) and [ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md) (the plan from the command line, each leaving resume owed), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the existing-file policies, the write-beside copy among them), [ADR-0102](0102-a-restore-gives-a-hard-link-group-back-as-one-file.md) (a hard-link group made one file again), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning), [§3.1](../architecture/08-restore-and-recovery.md#31-quarantine-by-default)

**Built:**
- The run: `Restore/RestoreExecutor` takes a resume, leaving each file already holding the snapshot's content as it stands, and reads none of its content ahead of the loop or in it. Under rename it looks beside a file in the way too (Amendment 1). A resumed quarantined run lands in the folder of the run it continues. `Restore/RestoreSpace` measures a resume for what it still writes.
- The receipt: schema 8, whose items say `found_in_place`.
- The contract: 1.67's `RunRestoreCommand.Resume` and `RestoreResult.FoundInPlace`. `Agent/ServiceCommandHandler` refuses a name that is not a run's and a run whose folder is not there.
- The verb: `Cli/CliApplication`'s `restore` takes `--resume`, naming the run through the service and none in direct mode.
- The tests:
  - `Repository.Tests/RestoreResumeTests` — a file already landed is left as the same file, and the rest restored; without a resume it is moved aside; nothing of what landed is read; a file whose content differs, at its length or another, lands under the policy, and so does a link where the file lands, whatever it points at; under fail a file already landed is the run's own; its metadata is applied again; a quarantined run is resumed in its own folder; a hard-link group's first name found in place is the file its later names link to; the receipt names what was found; under rename, the copy an earlier run wrote beside a file in the way is found there, of any day and counted or not, and no second is written, while a copy whose content differs is left and another written, a name the restore would not have written is not its copy, nothing beside is the run's own without a resume, and under another policy nothing beside is looked for
  - `Repository.Tests/RestoreSpaceTests` — a resume needs nothing for a file already there at its length, in place or in the folder of the run it continues
  - `Cli.Tests/RestoreResumeCommandTests` — in direct mode a resume leaves what landed and restores the rest, fits where only what is left fits, names no run, and is not planned

    > **2026-10 ([ADR-0120](0120-a-resume-is-planned.md)).** A resume is planned now, and its plan is not kept; the suite holds both.
  - `Cli.Tests/GatewayRestoreReportTests` — through the service a resume names its run and says what was found in place, and one naming none is refused before anything is asked
  - `Hosts.Tests/RestoreResumeServiceTests` — the service continues the named run in its folder with a receipt of its own, measures it for what is left, refuses a run whose folder is not there and a name that is not a run's, and under rename finds the copy the run it continues wrote beside
  - `Hosts.Tests/ClientModeTests` — routed end to end, the CLI continues the run it names
  - `Api.Tests/ConfigurationContractTests` — the wire names, and what an older peer's frames read as

---

## Context

A restore reads a snapshot and writes its files one at a time, each landing whole or not at all. One cut short leaves the files it landed and none of the rest. Running it again began from the start. In place, the existing-file policy then met every file the first run landed, as somebody else's, and moved each aside before writing it again. Quarantined, the second run wrote everything again into a folder of its own. Either way it read and wrote the whole run once more. In place under the default policy, it also kept a second copy of everything the first run had landed.

It could also be refused. The free-space check measures the whole run against what is free ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)), and the files the first run landed are no longer free space. The larger the run cut short, the likelier the run to finish it was refused.

Architecture 08 §2 says plans are exportable and resumable. Export was built ([ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md)), and resume was left owed by ADR-0083, [ADR-0107](0107-the-command-line-plans-a-restore-on-its-own.md) and ADR-0108.

## Decision

### 1. A resume runs the restore again, and leaves what already landed

A resumed run is the same run asked again. For each file, it looks at the place the file would land:
- **A file there already holds the snapshot's content** when it is a file and not a link, its length is the manifest's, and it hashes to the manifest's whole-file hash. Such a file is left as it stands. It is not moved aside or written again, and nothing of its content is read from the store, ahead of the loop or in it.
- **Its captured metadata is applied again.** A run cut short between a file's content and its metadata leaves the content with whatever it landed with (architecture 08 §3).
- **Anything else** is restored as any run restores it. That covers a file not there, one whose content differs at any length, and a link. A file in the way meets the existing-file policy.

  > **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--a-resume-under-rename-looks-beside)).** Under rename the place a file would land, with a file in the way, is beside it, so a resume looks there too.

The policy is about somebody else's file. One holding exactly what the run would write is the run's own result, so even the policy that fails on any file found does not fail on it.

The receipt counts such a file restored, and says `found_in_place` (schema 8). The run's answer says how many there were.

### 2. Only when asked

A run not told it resumes cannot know that a file like the snapshot's is its own. It may be somebody's, kept on purpose, and the policy is there for them. So a resume is asked for, and without one a run does exactly what it did.

### 3. Where it lands

- **In direct mode**, the command line restores in place where `--output` points, so `restore --resume` lands where the run cut short landed, and names no run.
- **Through the service**, each run lands in a quarantine folder of its own (architecture 08 §3.1). A resume names the run it continues, as that folder is named: its sixteen lowercase hex digits, given as `--resume <run>`. The resumed run lands in that folder.
  - The folder must be there. A run named but never begun left nothing to continue, and making its folder now would be a run under a name nobody chose.
  - A name that is not a run's is refused before it reaches a path.
  - A run in place, or at the original location, resumes where any run lands, as in direct mode.
- **Either way, the resumed run is a run of its own.** It has its own receipt and its own store for what it moves aside, so nothing either run moved aside is in the other's way.

### 4. It is measured for what is left

A resume needs no room for a file already where it lands at the snapshot's length. The run checks each file's hash as it reaches it. The measure goes by length, which costs nothing to ask. A file of that length whose content differs is then landed under the policy, and for that file the estimate erred low. That is the one way this estimate errs low, and the alternative was hashing every file twice.

### 5. A resume is not planned, and not under rename

- **A plan of a resume is refused.** A plan is of a whole run. Saying which files a resume would find in place means hashing them, which is most of the resume's work.

  > **Superseded 2026-10 ([ADR-0120](0120-a-resume-is-planned.md)).** A resume is planned. The plan goes by length, as §4's measure does, so it hashes nothing: it says how many files lie where the run lands them at their length, and that the resume checks each by its hash. A plan of a resume is still not kept.
- **A resume under rename is refused.** The rename policy writes a copy beside a kept file under a name dated the day it was written ([ADR-0041](0041-guided-restore-and-peer-retrieval.md)). A resume cannot tell such copies from anybody's, and would write a second one beside each.

  > **Superseded 2026-10 ([Amendment 1](#amendment-1-2026-10--a-resume-under-rename-looks-beside)).** A resume under rename is run, and finds a copy beside under a name the restore writes there. It need not tell whose a copy is: one that holds exactly the snapshot's content is what the run would write.

### 6. Hard links

A hard-link group's first name found in place is the file its later names are made from, as one the run writes is ([ADR-0102](0102-a-restore-gives-a-hard-link-group-back-as-one-file.md)). A later name is made again as any run makes it, under the policy.

## Consequences

- A restore cut short costs, run again, what it had left to do: the files not landed, read once, and a local hash of each file that was.
- A disk the first run filled most of does not refuse the run that finishes it.
- A script resumes a run by running it again with `--resume`. Through the service, it reads the run's name from where the first run said it landed.

## What this does not do

- **Resume within a file.** A file cut short mid-write never landed, so it is restored whole.

  > **2026-10 ([ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md)).** Through the service, a run keeps the part of each file it was cut short in, and a resume takes it up as far as each segment proves against its object identifier, reading only the rest. In direct mode a file cut short is still restored whole.
- **Resume from the console.** The wizard runs a restore whole, and resuming one from there is not built.

  > **2026-10 ([ADR-0110](0110-the-console-resumes-a-restore-cut-short.md)).** The wizard offers to restore what is left of a run that failed or was stopped, naming the run its result names.
- **Find the run itself.** Through the service the run is named, as the folder it landed in is named. A run's answer says where that folder is, and the folder stays when the run is cut short before it answers.

  > **2026-10 ([ADR-0110](0110-the-console-resumes-a-restore-cut-short.md)).** A run's result names the run a resume of it gives (contract 1.68), so a client need not read it out of the folder's path. A run cut short before it answers is still resumed by its folder's name.

  > **2026-10 ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)).** The service names each run before it writes and keeps it until a run of it finishes, so a run cut short before it answers is named too, and said in a notice. `restore-resume` lists such runs and resumes one by its name alone, as it was asked (contract 1.71).

## Alternatives considered

- **Leave a file like the snapshot's on every run.** Rejected. A run not asked to resume cannot tell a file it landed from one a person kept, and the policy exists for the second.
- **Trust the earlier run's receipt.** Rejected. A run cut short by its machine stopping has no receipt, and a receipt says what was true when it was written, not what the disk holds now.
- **Compare lengths and times, not content.** Rejected. A restore is a claim about content (FR-RST-002), and a file of the right length is not the right file.
- **The service finds the run.** Rejected for now. The newest folder under the quarantine may be another snapshot's restore, and its files are somebody's too.

  > **2026-10 ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)).** The service finds no run among the folders. It names each run it began, before the run writes, and so knows which it began and what each was asked.

## Amendment 1 (2026-10) — a resume under rename looks beside

**Context.** Rename is the console wizard's default policy, and §5 refused a resume under it. A console that could resume would then resume only under overwrite. §5's reason was that a resume cannot tell a copy an earlier run wrote beside from anybody's. It need not. A copy that holds exactly the snapshot's content, under a name the restore writes beside, is what this run would write there, whoever wrote it.

**Decision.**
- **Under rename, with a file in the way,** a resume looks beside it as well as at it. That file differs from the snapshot's, or it would be found itself.
  - The copies it looks at are those named as the restore names one: the file's stem, ` (restored `, a date of any day, optionally a counter from 2 to 100 as the restore writes one, `)`, and the file's extension.
  - The first, in name order, that is a file holding the snapshot's content (§1's test) is left as it stands, its metadata applied again, and the receipt names it as `written_as` beside `found_in_place`.
  - A copy whose content differs is left too, and the restored copy is written beside it under a new name, as any run under rename writes one.
- **A folder is listed once a run** for this, so a folder of many files is not listed again for each.
- **The measure is unchanged.** It counts a copy found beside as still to write, so for such a file the estimate errs high, which is the side it errs on.
- **Under another policy** nothing is looked for beside a file, since nothing else writes there.

**Consequences.** The service no longer refuses a resume under rename. A run cut short under rename, resumed, leaves both the file in the way and the copy beside it as they stand, and writes no second copy.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A resumed restore leaves each file already holding the snapshot's content, by length and whole-file hash, as it stands, reads none of its content, applies its metadata again, and lands the rest; it is measured for what is left. Through the service it continues the run it names, in that run's folder (contract 1.67), and the receipt (schema 8) says what was found in place. A resume is not planned, and not under rename. |
| 2026-10 | Accepted (amended) | [Amendment 1](#amendment-1-2026-10--a-resume-under-rename-looks-beside): a resume under rename is run. With a file in the way it looks beside it, and leaves a copy there holding the snapshot's content under a name the restore writes beside, naming it in the receipt; §5's refusal is superseded. |
| 2026-10 | Accepted (amended) | [ADR-0110](0110-the-console-resumes-a-restore-cut-short.md) takes up the console resume this record left owed: a restore's result names its run (contract 1.68), the wizard offers to restore what is left of a run that failed or was stopped, and the command line says how. |
| 2026-10 | Accepted (amended) | [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md) takes up finding the run, which this record left to whoever read its folder: the service names each run before it writes, keeps it until a run of it finishes, says each one left unfinished, and resumes one by its name alone (contract 1.71, `restore-resume`). |
| 2026-10 | Amended (§5) | [ADR-0120](0120-a-resume-is-planned.md): a resume is planned, by length as §4 measures it, saying how many files the run landed, so §5's refusal of a plan is superseded; a plan of a resume is still not kept. |
| 2026-10 | Accepted (amended) | [ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md) takes up resuming within a file, which this record left undone: through the service a file cut short is resumed from the part its run had assembled, as far as the part proves. |

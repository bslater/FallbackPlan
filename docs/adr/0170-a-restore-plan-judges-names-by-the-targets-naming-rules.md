# ADR-0170 — A restore plan judges names by the target's naming rules

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: the plan names every name the target cannot make, before any byte moves; by Amendment 1, a name longer in bytes than a Linux target holds; by Amendment 2, two paths are a collision only where the target takes them for one; by Amendment 5, judged as the volume of each folder a restore writes to takes names, where it says), FR-RST-005 (amended: a restore on Windows refuses a name Windows would not keep, rather than reporting it restored under another; by Amendment 3, where the volume takes two of a snapshot's names for one, the second fails naming the first rather than moving it aside; by Amendment 4, the recovery tool refuses the second alike rather than replacing the first)
**Related:** [ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md) (the executor's rule for where a repository's path is written, which the recovery tool shares), the [prior-art review](../review/2026-08-prior-art-learnings.md)'s T-7

**Built:**
- The rule: `Domain/RestorePaths` states which names Windows keeps as they are written. It refuses a device's name with or without an extension, any of `< > : " / \ | ? *` or a control character, a trailing space or period, and a component of more than 255 UTF-16 units.
- The plan: `Restore/RestorePlan` gives the target profile a flag for Windows' naming rules, set by the local profile where it runs on Windows. The planner names, as a conflict, each item whose own name the executor would refuse on any target, and under Windows' rules each item whose name Windows would not keep.
- The executor: on Windows, `RestorePaths.TryResolve` refuses a component Windows would not keep, for the executor and the recovery tool alike.
- The tests:
  - `Domain.Tests/WindowsNamesTests` — which names Windows keeps; on Windows such a name is refused with a reason naming Windows, and on POSIX it is taken
  - `Repository.Tests/RestorePlanTests` — a snapshot of names a POSIX source wrote plans with each one Windows would refuse or alter named, and the one no target restores, while a POSIX target names only the latter; Windows' own names plan clean on a stated POSIX target

---

## Context

Names are captured as their source wrote them, and paths in the format are plain components with no separator. A snapshot is portable; a filesystem is not. A POSIX source can hold names that Windows refuses or alters:

- **A device's name.** `CON`, `aux.txt` or `LPT9.doc` is reserved for a device on Windows. Windows' own guidance is never to name a file so, and a write to such a path may reach the device.
- **A refused character.** Windows refuses `?`, `*`, `<`, `>`, `|`, `"` and control characters in a name.
- **A trailing space or period.** Windows strips it, so `notes.` lands on `notes`. Where both exist, one overwrites the other.
- **A long component.** Windows allows 255 UTF-16 units in one component; a POSIX volume counts bytes.

Only the executor judged names, and only by the rule every target shares: no separator, drive marker, NUL, `.` or `..` (ADR-0152). It judged them at execution, after the plan was approved, as an item refused partway through the run. Nothing judged a name by Windows' rules. On Windows, the path the executor built for `notes.` is normalised to `notes`, inside the root, so it passed the fence and was written there.

The [prior-art review](../review/2026-08-prior-art-learnings.md)'s T-7 noted that every restore test plans on the platform that captured. The degradations that matter appear only when the two platforms differ, and they must be surfaced in the plan **before any byte moves**, which the plan already promises for case collisions.

## Decision

### 1. One rule for the names Windows keeps

`RestorePaths.IsWindowsName` states the rule, beside the rule every target shares, in Domain. The recovery tool may not reference the restore project, and two copies of a naming rule drift apart (ADR-0152). It is judged on any platform.

### 2. The target says whether it names by Windows' rules

The target profile gains `WindowsNamingRules`. Like `CaseSensitive`, it is stated rather than read from the running platform. A plan for a Windows target can therefore be made, and tested, on any host. The local profile sets it where it runs on Windows.

### 3. The plan names what the target cannot make

The planner judges each item by its own last component; a folder's name is judged as the folder's item, so a bad folder is one conflict rather than one per file under it.

- **A name the executor refuses on every target** is a conflict on every target. Before, it surfaced only as a refused item at execution.
- **Under Windows' rules**, a name Windows would not keep is a conflict, with the reason.

Conflicts are the operator's call, as case collisions already are. The plan reports them; it does not rename or drop anything.

### 4. A restore on Windows refuses what Windows would not keep

On Windows, `TryResolve` refuses such a component with a reason that names Windows, so the item fails and is named in the receipt. The fence that keeps a path inside the root cannot catch this. Windows does not refuse these names: it writes them somewhere else, under a path the fence finds inside the root. On POSIX nothing changes; the names are legal there and restore as written.

## Consequences

- A restore of a POSIX snapshot onto Windows says, at plan time, every name that will not land as written, and why.
- No name is written on Windows under another name, so no file lands on another's path and a restore never reports a file restored that is not where it says.
- A restore planned and run on POSIX is unchanged, except that a name no target restores is now named in the plan as well as refused at execution.

## What this does not do

- **Restore the refused names under others.** A mapped name, such as an escaped character or a suffix, would restore the bytes under a name the source never had, and needs an operator's choice and a receipt that says so. Not built.
- **Judge a POSIX target by its own limits.** A POSIX volume's 255-byte component limit, and macOS's composed-name rules, are not judged. The plan's whole-path byte limit (`MaxPathBytes`) is unchanged.

  > **Built 2026-10 for Linux ([Amendment 1](#amendment-1-2026-10--a-name-longer-than-a-linux-target-holds)).** A target that counts a name in bytes states how many it holds, and the local profile states 255 on Linux. A name longer than that is a conflict. macOS's limit and its composed-name rules are still not judged.

  > **2026-10 ([Amendment 2](#amendment-2-2026-10--a-collision-is-named-only-where-the-target-takes-the-two-names-for-one)).** That macOS takes a name's composed and decomposed forms for one name is now stated, and two such names are a collision there. A volume that stores a name in another form than it was given, as HFS+ stores one decomposed, is still not judged, nor is macOS's limit.
- **Judge a destination by its volume rather than its platform.** A FAT or SMB volume mounted on POSIX may name by stricter rules than its host. The flag says what the platform does, and a caller that knows better can set it.

  > **2026-10 ([Amendment 3](#amendment-3-2026-10--a-run-never-moves-aside-what-it-restored-itself)).** A run no longer moves aside, replaces or fails unnamed over a file it restored itself where its volume takes two of the snapshot's names for one: the second fails, naming the first. The plan still judges by the platform.

  > **Built 2026-11 for case and normalisation ([Amendment 5](#amendment-5-2026-11--the-plan-reads-how-the-volume-it-writes-to-takes-names)).** The plan reads how the volume of each folder a restore writes to takes names, where the volume says, and judges collisions so. The other naming rules, Windows' and a name's byte limit, still follow the platform.

## Alternatives considered

- **Refuse such names on every platform.** Rejected. They are legal on POSIX, and a Linux snapshot restored to Linux must get its own names back.
- **Judge in the planner only, and leave the executor as it was.** Rejected. A plan is the operator's to approve, and a restore run without one, or past one, would still write `notes.` over `notes` on Windows.
- **Read the rules from the running platform.** Rejected, as for case sensitivity. A plan for another target could not be made, and the tests could judge only the host they ran on.

## Amendment 1 (2026-10) — a name longer than a Linux target holds

Windows counts a name in UTF-16 units, and Linux in bytes. A name in UTF-8 is never fewer bytes than units, and a character outside ASCII is two to four bytes, so a name Windows holds can be one Linux does not. Eighty-six three-byte characters are 86 units and 258 bytes. A snapshot of Windows names restored to Linux met that at execution: the platform refused the name, and the executor failed the file, named, with the platform's "too long". The plan, which promises to name before any byte moves each name the target cannot make, said nothing.

**Decision.**

- **The target profile gains `MaxNameBytes`:** the most bytes the target holds in one name, counted in UTF-8, or null where that is not known. Like the profile's other facts it is stated, so a plan for such a target can be made, and tested, anywhere.
- **The local profile states 255 on Linux,** the limit of the filesystems Linux commonly creates. On macOS it states nothing. A macOS volume's limit is not one this code can state for every volume, so a name there is not judged by one, rather than by a guess. On Windows the limit stays as §1 judges it, in UTF-16 units.
- **The plan judges each item's own name by it,** after the rule every target shares and Windows' rules. A name over it is a conflict whose reason gives the limit and the name's length. As §3 has it, the conflict is the operator's call, and the plan drops and renames nothing.
- **Execution is unchanged.** Linux refuses such a name itself, and the executor fails that file alone, named, while the rest restores.

**Built:** `Restore/RestorePlan`; `Repository.Tests/RestorePlanTests`:

- against a target that holds 255 bytes, a name of 86 three-byte characters is a conflict, and its reason gives 255 bytes and 258;
- one of 85 such characters, and one of 255 ASCII characters, is not;
- a target with no stated limit, or one that names by Windows' rules, judges none of them by bytes;
- the local profile states 255 on Linux and nothing elsewhere.

`Repository.Tests/RestoreLongNameTests`, on Linux: such a file is named in the plan. The run then fails it alone, restores the file beside it and leaves no spool.

**Not done.** A name on a volume mounted on Linux that holds fewer bytes, or more, is judged by Linux's common limit, since the profile states the platform's and not the volume's (the third bullet of "What this does not do"). The whole path is still not judged against the platform's path limit.

## Amendment 2 (2026-10) — a collision is named only where the target takes the two names for one

The plan named two paths as colliding on any case-insensitive target where they matched once each was turned to its composed Unicode form and lower-cased. That is the catalogue's fold ([ADR-0026](0026-phase-1-capture-shapes.md) §8). It is right for macOS, whose volumes take a name's composed and decomposed forms for one name. It is wrong for Windows:

- **NTFS compares names by case alone.** It keeps `café` with its accent composed into the letter apart from `café` with the accent a mark of its own after the `e`. A snapshot holding both, restored to Windows, planned with both named as a collision, a decision put to the operator that did not exist. The run then restored both, as they were.
- **A case-sensitive volume on macOS still takes the two forms for one.** No target could say so: a target stated case-sensitive was not judged at all.

**Decision.**

- **The target profile gains `NormalisationInsensitive`:** whether the target takes two names that differ only in their Unicode normalisation for one. Like `CaseSensitive` it is stated, so a plan for any target can be made, and tested, anywhere. The local profile states it on macOS.
- **The plan folds a path only as the target does.** Case is folded where the target is case-insensitive, compared as the executor compares paths there, by ordinal comparison ignoring case. Normalisation is folded, to the composed form, only where the target ignores it. A case-sensitive target that ignores normalisation is judged by normalisation alone.
- **The reason no longer says case alone.** Each item names the others it would land on, "which it takes for the same name".
- **Execution is unchanged.** On Windows and Linux both forms of a name land, each with its own content.

  > **2026-10 ([Amendment 3](#amendment-3-2026-10--a-run-never-moves-aside-what-it-restored-itself)).** Execution changes where a volume takes two of the snapshot's names for one: the second fails naming the first, which keeps what it landed.

**Built:** `Restore/RestorePlan`; `Repository.Tests/RestorePlanTests`:

- of a composed and a decomposed `café.txt` and of `Notes.txt` and `notes.txt`, a target that folds case alone, as Windows does, names only the case pair; one that folds both, as macOS does, names all four; a case-sensitive one that folds normalisation names only the two forms; Linux's names none;
- the local profile states it on macOS alone, and the volume the tests write on agrees: a name written composed is found decomposed on macOS and on no other platform;
- on Windows and Linux a run restores both forms of the name, each with its own content, and displaces nothing.

**Not done.** The flag follows the platform, as the others do, so a folder Linux makes case-insensitive, or a volume of another platform's kind mounted there, is judged as Linux (the third bullet of "What this does not do"). A volume that stores a name in another form than it was given, as HFS+ stores one decomposed, is not judged.

> **2026-10 ([Amendment 3](#amendment-3-2026-10--a-run-never-moves-aside-what-it-restored-itself)).** A run on such a volume no longer moves aside a file it restored itself: the second of the two names fails, naming the first. The plan still judges by the platform.

> **2026-11: the flag is now read from the volume, where it says ([Amendment 5](#amendment-5-2026-11--the-plan-reads-how-the-volume-it-writes-to-takes-names)).**

## Amendment 3 (2026-10) — a run never moves aside what it restored itself

Amendment 2 judges a collision by how the target folds names, and the target says what its platform does. A volume can fold where its platform does not: a FAT or exFAT drive or a share mounted on Linux, or a folder Linux makes case-insensitive. Where the plan does name a collision, the run still goes ahead, since conflicts are the operator's call ([architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)). Either way, two of the snapshot's names land on one file. The second found the first standing there, and met it with the policy for somebody else's file:

- **Under preserve, the default,** it moved the first into the run's displaced store and landed in its place. The receipt said both were restored, and one was not where it said.
- **Under overwrite,** it replaced the first, whose content was then nowhere.
- **Under fail,** it failed, saying a file was already there, without saying the file was the run's own.

**Decision.**

- **The run keeps each name it lands.** It keeps it by what a volume that folds names would take it for: its composed form, compared without case. `Domain/NamesLanded` holds them.
- **The second of two names the volume takes for one fails, naming the first.** This applies to a file, or a link, whose destination is occupied, where a name the run landed folds onto it and its folder lists no entry under its own name. The volume took the two names for one, and what stands there is the run's own. The item fails with "the target takes this name and X for one, and this run restored X there". No policy moves the first aside, replaces it or fails over it unnamed, and the first keeps what it landed. The run is not reported complete.
- **The listing decides, not the fold.** On a volume that keeps the two names apart, a file of somebody else's under the second name is listed under it, and the policy meets it as before.
- **Under rename the second lands beside the first,** as beside anyone's file, and both are kept.
- **The plan is unchanged.** It still judges by what the platform does.

**Built:** `Domain/NamesLanded`, `Restore/RestoreExecutor`:

> **2026-10 ([Amendment 4](#amendment-4-2026-10--the-recovery-tool-never-replaces-what-it-restored-itself)).** `NamesLanded` moved, unchanged, from the restore project into Domain, and its tests with it, so the recovery tool holds names by the same rule. It gained `TakenForOneAt`, which asks the disk the two questions the executor had asked itself.

- `Domain.Tests/NamesLandedTests`:
  - an occupied destination that a landed name folds onto, by case or by normalisation, and whose name its folder does not list, is taken for that name;
  - one listed under its own name, one unoccupied, and one no landed name folds onto are taken for nothing, and the last two ask for no listing;
  - a name is not taken for itself, the first name landed for a fold is the one named, and a name that is not well-formed Unicode is folded by case alone.
- `Repository.Tests/RestorePlanTests`: a snapshot holding `Notes.txt` and `notes.txt`, and a composed and a decomposed `café.txt`, is restored under preserve, overwrite and fail, and judged by the volume the test writes to.
  - Where the volume takes a pair for one, one lands with its own content, the other fails naming it, nothing is displaced, and the run fails. Windows' and macOS's volumes take the case pair for one; macOS's takes the other pair too.
  - Where the volume keeps them apart, as Linux's does, both land.
  - Under rename each lands with its own content.

**Not done.**

- **The plan does not judge the volume.** A collision a volume makes where its platform does not is still not named before the run; the run names it, item by item. Reading a volume's naming rules without writing to it, which a plan must not do, is the next record's.

  > **2026-11: taken up by [Amendment 5](#amendment-5-2026-11--the-plan-reads-how-the-volume-it-writes-to-takes-names).**
- **The recovery tool** replaces whatever stands at a name. On such a volume, the second of two names it takes for one replaces the first.

  > **2026-10: taken up by [Amendment 4](#amendment-4-2026-10--the-recovery-tool-never-replaces-what-it-restored-itself).**
- **A volume that stores a name in another form than it was given,** as HFS+ stores one decomposed, lists the landed name in the form the second asks for, and is not judged.

## Amendment 4 (2026-10) — the recovery tool never replaces what it restored itself

Amendment 3 left the recovery tool as it was. It clears whatever stands at a file's or a link's name before it writes there, as a rename replaces what it lands on. On an output whose volume takes two of a snapshot's names for one, the second of the two replaced the first. The report then counted both restored, though one name held the other's content.

**Decision.**

- **One rule for both writers.** `NamesLanded` moves, unchanged, from the restore project into Domain. The recovery tool may not reference the restore project, and a naming rule kept in two places drifts ([ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md)). It gains `TakenForOneAt`, which asks the disk the two questions the executor had asked itself: whether anything stands at the destination, and whether its folder lists an entry under its own name.
- **The tool keeps each name it writes** a file, a link or a hard link's later name under.
- **The second of two names the output takes for one is refused, naming the first.** Before writing a file or a link, the tool asks whether the destination is taken for a name it wrote. Where it is, the entry is refused with a note, "the output takes this name and X for one, and the tool restored X there", and counted failed. The first keeps what the tool wrote.
- **The executor asks through the same call,** and behaves as Amendment 3 has it.

**Built:** `Domain/NamesLanded`, `Recovery/RecoverySession`, `Restore/RestoreExecutor`:

- `Domain.Tests/NamesLandedTests` — the decision's cases, moved from the restore project's tests. On disk: where the volume finds the landed file under the other name, the destination is taken for it. Where it does not, nothing standing there, and somebody else's file listed under its own name, are each taken for nothing.
- `Repository.Tests/RecoveryNamesTests` — the recovery tool restores `Notes.txt` and `notes.txt`, and a composed and a decomposed `café.txt`, judged by the volume the test writes to. Where the volume takes a pair for one, one keeps its content and the other is refused naming it. Where it keeps them apart, as Linux's does, both are restored.

**Not done.** The plan still judges by the platform, and a volume that stores a name in another form than it was given is still not judged (Amendment 3's "Not done").

> **2026-11: the plan is taken up by [Amendment 5](#amendment-5-2026-11--the-plan-reads-how-the-volume-it-writes-to-takes-names).**

## Amendment 5 (2026-11) — the plan reads how the volume it writes to takes names

Amendments 2 to 4 judged a collision in the plan by what the target's platform does, and left the volume to the run. A volume can take names otherwise than its platform's commonly do:

- **On Linux,** a FAT or exFAT drive, or a share served over SMB or CIFS, folds case. A folder Linux casefolds folds case and the two forms of a name both.
- **On Windows,** a folder made case-sensitive keeps apart what Windows' volumes fold.
- **On macOS,** a volume made case-sensitive keeps case apart, though it still folds the two forms.

On such a volume the plan named a collision that would not happen, or missed one that would. The run met the volume truthfully, item by item, but only after the plan was approved.

**Decision.**

- **`Domain/VolumeNaming` reads how the volume a folder lies on takes names, without writing to it,** since a plan writes nothing:
  - macOS answers whether the volume distinguishes case (`pathconf`), and its volumes take the two forms of a name for one.
  - Windows answers whether the folder distinguishes case, a flag a folder made inside one inherits, and its volumes keep the two forms apart.
  - Linux answers what kind of filesystem the folder is on (`statfs`), and whether the folder is casefolded (`FS_IOC_GETFLAGS`).

  A folder not yet made is judged by the nearest one that is, which is where it would be made. A file in the way, or a platform that does not say, gives no answer.
- **The target is taken as the volumes of every folder a restore writes to take names.** It is case-sensitive only where every one is, and takes the two forms for one where any does. A volume that gives no answer is taken to name as the platform does.
- **Each plan made for a run reads them:** the command line's and the service's, for a run into a folder or to the original location. The plan is made again only where the volumes take names otherwise than the platform.
- **The run is unchanged.** It never reports the plan's conflicts, and it meets what the volume does item by item (Amendments 3 and 4).

**Built:** `Domain/VolumeNaming`, `Restore/RestorePlan`, `Cli/OperationGateway`, `Agent/ServiceCommandHandler`:

- `Domain.Tests/VolumeNamingTests`:
  - the volume the tests write on is judged as it behaves, on every platform CI runs;
  - a folder not yet made is judged by its nearest, and nothing is made;
  - a folder below a file is not judged;
  - Linux's kinds of filesystem, and the casefold flag, take names as stated;
  - a flow's own word answers in place of the platform.
- `Repository.Tests/RestorePlanTests` — plans for stated targets, written to folders whose volumes the flow states: a FAT drive on Linux folds case; a case-sensitive folder on Windows keeps it; a casefolded folder folds both; two volumes fold what either folds; and a volume that says nothing, or no folder at all, leaves the target as stated.
- `Cli.Tests/RestorePlanCommandTests`, `Hosts.Tests/RestoreHonestyServiceTests` — on Linux, whose own volume holds both names to back up, a plan to a folder whose volume folds case names the collision through the command line and through the service, and without that, names none.

**Not done.**

- **A share is judged by its protocol,** not by the server's own rule. A share whose server keeps case apart is judged as folding it.
- **NTFS mounted on Linux** is judged as keeping case apart, as Linux's drivers mount it by default.
- **A volume that is neither APFS nor HFS+ on macOS** is judged as taking the two forms for one, as macOS's own volumes do.
- **A volume that stores a name in another form than it was given,** as HFS+ stores one decomposed, is still not judged.
- **Windows' naming rules and a name's byte limit** still follow the platform.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A restore plan names every name the target cannot make, by a stated Windows naming rule, and a restore on Windows refuses one rather than writing it under another. No contract or format change. |
| 2026-10 | Amended | Amendment 1: a target that counts a name in bytes states how many it holds, 255 for the local profile on Linux, and the plan names a name longer than that before any byte moves. `Restore/RestorePlan`; `Repository.Tests/RestorePlanTests`; `Repository.Tests/RestoreLongNameTests` |
| 2026-10 | Amended | Amendment 2: the target states whether it takes a name's composed and decomposed forms for one, the local profile on macOS, and the plan names two paths as colliding only where the target folds them together, by case as the executor compares them and by normalisation only where the target ignores it. `Restore/RestorePlan`; `Repository.Tests/RestorePlanTests` |
| 2026-10 | Amended | Amendment 3: a run keeps each name it lands, and where its volume takes two of a snapshot's names for one, the second fails naming the first rather than moving it aside, replacing it or failing over it unnamed; under rename it lands beside. The plan is unchanged. `Domain/NamesLanded`, `Restore/RestoreExecutor`; `Domain.Tests/NamesLandedTests`, `Repository.Tests/RestorePlanTests` |
| 2026-10 | Amended | Amendment 4: `NamesLanded` moved, unchanged, into Domain with a call that asks the disk, and the recovery tool refuses the second of two names its output takes for one, naming the first, rather than replacing it. `Domain/NamesLanded`, `Recovery/RecoverySession`, `Restore/RestoreExecutor`; `Domain.Tests/NamesLandedTests`, `Repository.Tests/RecoveryNamesTests` |
| 2026-11 | Amended | Amendment 5: the plan reads how the volume of each folder a restore writes to takes names, without writing to it, and judges collisions so, case-sensitive only where every volume is and folding the two forms where any does; the run is unchanged. `Domain/VolumeNaming`, `Restore/RestorePlan`, `Cli/OperationGateway`, `Agent/ServiceCommandHandler`; `Domain.Tests/VolumeNamingTests`, `Repository.Tests/RestorePlanTests`, `Cli.Tests/RestorePlanCommandTests`, `Hosts.Tests/RestoreHonestyServiceTests` |

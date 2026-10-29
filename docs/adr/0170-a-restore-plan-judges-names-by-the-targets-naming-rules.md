# ADR-0170 — A restore plan judges names by the target's naming rules

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: the plan names every name the target cannot make, before any byte moves), FR-RST-005 (amended: a restore on Windows refuses a name Windows would not keep, rather than reporting it restored under another)
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
- **Judge a destination by its volume rather than its platform.** A FAT or SMB volume mounted on POSIX may name by stricter rules than its host. The flag says what the platform does, and a caller that knows better can set it.

## Alternatives considered

- **Refuse such names on every platform.** Rejected. They are legal on POSIX, and a Linux snapshot restored to Linux must get its own names back.
- **Judge in the planner only, and leave the executor as it was.** Rejected. A plan is the operator's to approve, and a restore run without one, or past one, would still write `notes.` over `notes` on Windows.
- **Read the rules from the running platform.** Rejected, as for case sensitivity. A plan for another target could not be made, and the tests could judge only the host they ran on.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A restore plan names every name the target cannot make, by a stated Windows naming rule, and a restore on Windows refuses one rather than writing it under another. No contract or format change. |

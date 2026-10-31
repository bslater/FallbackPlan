# ADR-0181 — A restore on Windows makes a symlink where the account may

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: a symlink comes back as a link on Windows where the account may make one); FR-RST-003 (amended: the plan says links will be skipped only where they will be)
**Related:** [ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md) (a link's own metadata, which Windows did not arise for), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) §4 and [ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md) (every path a restore writes is placed through no link out of it), [ADR-0155](0155-the-recovery-tool-makes-a-link-a-link-and-fails-only-the-entry-the-output-refuses.md) (the recovery tool makes links where they need no privilege), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (symbolic links: "preserve, privilege permitting" on Windows)

**Built:**
- The rule: `Domain/SymbolicLinks` says whether this process may make a link, writes a target with Windows' separators, and says whether a link is a folder's.
- The restore: `Restore/RestorePlan` takes the local profile's links from that answer, and `Restore/RestoreExecutor` makes each link a folder's or a file's.
- The tests:
  - `Domain.Tests/SymbolicLinksTests` — on every platform: a target is written with Windows' separators; a link Windows captured is a folder's by its bits whatever it names, and one captured elsewhere by what it names. Off Windows a link can always be made, and on Windows the answer is what making one answers.
  - `Repository.Tests/RestoreWindowsLinkTests` — the local profile makes links where the account may. On Windows a link captured elsewhere is made a link that resolves, with Windows' separators, and the plan no longer says links will be skipped; a link to a folder the restore makes is a folder's link, though the folder is made after it, and a link Windows captured is a folder's or a file's as its bits say.

---

## Context

Architecture 06's matrix has symbolic links preserved on Windows, privilege permitting. The restore's local profile on Windows said it could make no link, so the plan declared every link skipped and the executor skipped each, whatever the account could do. An account that holds the privilege to make links, or any account on a machine in developer mode, may make one, and an administrator usually holds the privilege.

Windows differs from Linux and macOS in two ways a restore meets:

- **A link is a folder's or a file's.** Windows makes a link to a folder apart from a link to a file, and a file's link to a folder does not open as a folder. Linux and macOS make one kind.
- **A link resolves only Windows' separators.** A link captured on Linux or macOS names its target with forward slashes, and Windows does not resolve a relative link written with them.

A link captured on Windows carries its attribute bits (specification 06 §4.1 key 10), which say whether it was made as a folder's. One captured elsewhere carries none.

## Decision

### 1. A link is made where the account may make one

Whether this process may make a link is asked once, by making one in the temporary folder. Linux and macOS always may. The local profile says it supports links where the answer is yes. The plan then declares links skipped only where they will be, and the executor makes each as it does on Linux and macOS, under the same existing-file policy.

### 2. A folder's link or a file's

On Windows a link is made as a folder's:

- where Windows captured it, as its captured attribute bits say, since it was one or the other whatever it names now;
- otherwise where what it names is a folder: one the plan makes, though it may not be made yet, or one that stands.

Anything else is made as a file's link.

### 3. Windows' separators

A target is written with each forward slash made Windows' separator. No Windows name may hold a forward slash, so each was a separator.

### 4. What a link is given

Nothing more than before. No call on Windows sets a link's own times, and its attribute bits and descriptor are written only onto files and folders, so a link's captured metadata is declared and listed as not applied, as ADR-0101 already said of a link it could not write onto.

## Consequences

- A tree with links restores on Windows as it does on Linux and macOS where the account may make links, and as before where it may not.
- A link made before what it names still points at it. A link may still point anywhere; every path the restore writes is still placed through no link out of it (ADR-0086 §4, ADR-0152), so a link carries no write out of the restore.
- A volume that keeps no links, as FAT does, refuses each at the write, and the item fails and says why.

## What this does not do

- **The recovery tool.** It still makes no link on Windows (ADR-0155). That is the next record's.
- **A link's own times on Windows.** They would need a call opened on the link itself, which nothing here makes.
- **A junction.** A directory junction captured on Windows comes back as a folder's symbolic link to the same target, not as a junction.

## Alternatives considered

- **Probe each destination's volume.** Rejected for now. The account decides whether a link may be made, and a volume that keeps none refuses at the write, which the item says.
- **Leave a POSIX target's separators as they are.** Rejected. The link would be made and resolve to nothing.
- **Decide a folder's link by what stands at the write alone.** Rejected. Links sort before what they name as often as after, so a link made before its folder would be a file's.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A restore on Windows makes a link where the account may, a folder's or a file's as its captured bits or what it names say, with Windows' separators; the plan says links will be skipped only where they will be. `Domain/SymbolicLinks`; `Restore/RestorePlan`; `Restore/RestoreExecutor`; `Domain.Tests/SymbolicLinksTests`; `Repository.Tests/RestoreWindowsLinkTests` |

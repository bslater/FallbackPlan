# ADR-0150 — The recovery tool gives what it restores its metadata back, by the executor's rule

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: the recovery tool writes back what the executor would, and counts what did not land), FR-DRL-001
**Related:** [ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md) (the writer moved into Domain so this tool could reach it), [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md), [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md), [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) and [ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md) (the rule), [ADR-0082](0082-the-recovery-tools-diagnostic-bundle.md) (the tool's bundle), [architecture 08 §5](../architecture/08-restore-and-recovery.md#5-emergency-recovery) (the recovery tool)

**Built:**
- The tool: `Recovery/RecoverySession` gives each file it restores its captured metadata once the file has landed, and each folder it made its own once nothing more will land in it, deepest folder first, through `Domain/MetadataWriter`. A folder already in the output keeps its own, so does the output folder, and nothing goes through a link. `Recovery/RecoveryHost` prints what did not land, and `Recovery/RecoveryBundle` carries it.
- The tests:
  - `Repository.Tests/RecoveryMetadataTests` — a file's times and permissions come back, and a folder's once its contents have landed, permissions that forbid writing included, and a folder's inside one captured without search permission; a folder already in the output and the output itself keep their own; an owner or group no account here has is counted, in the receipt's words; nothing goes through a link swapped in while the tool runs, in a folder's place or its parent's; and a run stopped part way still settles the folders it made
  - `Hosts.Tests/RecoveryHostTests` — a real backup's file and folder come back through the tool with their captured modification times, and the file with its permissions; a folder already in the output keeps its own, and the tool says on its error stream what was not applied
  - `Hosts.Tests/RecoveryBundleTests` — the bundle's restore entry counts each attribute not applied
  - `ArchitectureTests/DependencyRuleTests` — the recovery tool writes metadata through the shared writer, not a copy of it

---

## Context

The recovery tool is the last line of defence: it restores from the passphrase and the archive alone, with no service, no catalogue and no state ([architecture 08 §5](../architecture/08-restore-and-recovery.md#5-emergency-recovery)). It wrote content and nothing else. A file it restored carried the time the tool wrote it, the tool's default permissions and the account the tool ran as. Each of the records that built the executor's rule listed it among what that rule did not reach.

[ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md) moved the writer, its rules and the captured map into Domain, which the tool's closure already reaches through the format library. What was left was to use it.

The tool keeps no receipt. What it could not do is said as notes on its error stream and in its diagnostic bundle ([ADR-0082](0082-the-recovery-tools-diagnostic-bundle.md)). A note is about one entry that failed. Metadata alone does not fail an entry ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)), and on a machine that is not the one that captured it, a whole tree can miss the same attribute.

## Decision

### 1. The tool writes through the executor's writer

What the tool restores is given back what the executor would give it, by the same writer:
- **times:** modification and access times everywhere, and creation times where the platform sets them;
- **on a POSIX target:** owner and group where their names resolve and the account may give them, and permissions that keep a set-id bit only with its owner or group;
- **extended attributes**, where the target writes them.

It writes:
- for this platform, as the account it runs as;
- with no claim to have captured what it restores, so a Linux ACL that names accounts by number is withheld. The tool has no installation to compare the snapshot's device with.

A file's metadata goes on once its content has verified and landed, as the executor's does (architecture 08 §3). A refused write leaves that attribute not applied, and never fails the file.

### 2. A folder is held to ADR-0086

A folder the tool made gets its own metadata once nothing more will land in it, deepest folder first. A run that stops part way settles the folders it made too.

Immediately before a folder is given its metadata, the tool checks that it and every folder above it, down from the output, is still a folder and not a link. If anything has taken its place, nothing is applied.

A folder already in the output keeps its own, as it would through the executor ([ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) §3). So does the output folder itself. The person chose it, and the snapshot root's metadata would change who may reach it.

### 3. What did not land is counted, by attribute

The tool's report counts, for each captured attribute some restored file or folder did not get back, how many did not. It uses the receipt's words and order.

The terminal says it in one line on the error stream, after the notes:

```
metadata not applied: owner to 2 item(s), group to 1 item(s)
```

The bundle's restore entry carries the same counts. A folder already in the output, or one whose place was taken, counts every attribute captured with it. The output folder is not counted, because the person chose it and nothing was restored there.

The exit code is unchanged. Metadata does not fail a recovery.

## Consequences

- A tree the tool restores has its captured times, permissions and ownership back, where the target and the account allow them. A folder captured read-only comes back read-only, with its contents.
- A recovery onto another machine says, in one line, what of the tree's metadata that machine did not take.
- Every way of restoring now writes metadata back by one rule, and a canary fails if any of the three writes through a copy.

## What this does not do

- **Say which entries missed an attribute.** The tool counts. The executor's receipt is where each item lists what it did not get back.
- **Keep a file's metadata off a path through a planted link.** A file lands through any link that stands in the output or above it, and its metadata follows its content there. The tool checks each tree name is a plain component, but not, as the executor does, that the path stays in the output through links. Recovering into a new, empty folder keeps that out.

  > **2026-10 ([ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md)).** The tool now places every path by the executor's rule, so a file under a link out of the output is refused by name, and nothing, its metadata included, goes through the link.
- **Restore a symlink, a special file or a hard-link group as one file.** These are skipped or restored as separate files, as before. So no link's own metadata is written.

  > **2026-10 ([ADR-0155](0155-the-recovery-tool-makes-a-link-a-link-and-fails-only-the-entry-the-output-refuses.md)).** The tool now makes a symlink a link where links can be made without a privilege, and gives the link its own metadata onto the link. A special file and a hard-link group are as before.

  > **2026-10 ([ADR-0156](0156-the-recovery-tool-gives-a-hard-link-group-back-as-one-file-by-the-executors-rule.md)).** The tool now gives a hard-link group back as one file, by the executor's rule. A special file is as before.
- **Restore Windows attribute bits, security descriptors or alternate streams.** These are counted as not applied, as the executor lists them.

  > **2026-10 ([ADR-0174](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md)).** The executor now writes a file's alternate streams back on Windows. The tool still writes none, and counts them as not applied.

  > **2026-10 ([ADR-0174 Amendment 1](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md#amendment-1-2026-10--the-recovery-tool-writes-a-files-streams-back-by-the-same-rule)).** The tool now writes a file's alternate streams back on Windows by the executor's rule. One that does not land is noted, and counted not applied.

## Alternatives considered

- **A note per entry whose metadata did not wholly land.** Rejected. On a machine that is not the one that captured it, every file can miss the same owner, and the notes that say what failed would drown. The bundle already caps its notes, and a count says the same thing at every size.
- **Leave folders as the tool made them.** Rejected. A folder's times and permissions are as much what was captured as a file's. The executor's rule for when they can go on applies unchanged.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The recovery tool writes each restored file's metadata through the executor's writer, and each folder it made last and deepest first, never through a link, a run stopped part way included. A folder already in the output and the output itself keep their own, and the report, the terminal and the bundle count each attribute not applied. |
| 2026-10 | Accepted | [ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md): the tool places every path by the executor's rule, so the gap this record named for a link planted in the output is closed. |
| 2026-10 | Accepted | [ADR-0155](0155-the-recovery-tool-makes-a-link-a-link-and-fails-only-the-entry-the-output-refuses.md): the tool makes a symlink a link and writes the link's own metadata onto it, and a write the output refuses at an entry's name fails that entry alone. |
| 2026-10 | Accepted | [ADR-0156](0156-the-recovery-tool-gives-a-hard-link-group-back-as-one-file-by-the-executors-rule.md): the tool gives a hard-link group back as one file under each of its names, by the rule the executor holds to. |
| 2026-10 | Accepted | [ADR-0174](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md): the executor writes a file's alternate streams back on Windows; this tool still writes none. |
| 2026-10 | Accepted | [ADR-0174 Amendment 1](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md#amendment-1-2026-10--the-recovery-tool-writes-a-files-streams-back-by-the-same-rule): the tool writes a file's alternate streams back on Windows by the executor's rule. |

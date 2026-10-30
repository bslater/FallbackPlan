# ADR-0149 — The single-file restore writes back a file's metadata, by the executor's rule

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004 (amended: the command line's `restore-file` writes back what the executor would and names what did not land)
**Related:** [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md), [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md), [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) and [ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md) (the rule, each of which listed `restore-file` among what it did not reach), [architecture 11 §2](../architecture/11-solution-structure.md#2-dependency-rules) (what the recovery tool may reference)

**Built:**
- The writer: `Domain/MetadataWriter` writes an entry's captured metadata back by the rule `Domain/MetadataRules` states, for the target `Domain/MetadataTarget` describes. `Restore/RestoreExecutor` writes every item through it.
- What moved with it into Domain: `Domain/EntryMetadata`, the captured map, from the format library; `Domain/RestoreAccount`, the account a restore runs as; and `Domain/CapturedMetadata`, the flags naming each attribute.
- The single-file restore: `Cli/CliApplication`'s `restore-file` writes a file's metadata back once its content has verified, and names what did not land.
- The tests:
  - `Cli.Tests/InspectionCommandTests` — `restore-file` gives a backed-up file its captured modification time back, and its permissions where the target keeps POSIX ones
  - `ArchitectureTests/DependencyRuleTests` — the executor and the command line write metadata through the shared writer, not a copy of it

---

## Context

A restore through the service, or the command line's `restore`, goes through the executor. That writes a file's captured metadata back by one rule, built a record at a time: times ([ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md)), owner and group ([ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md)), a folder's own ([ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md)), extended attributes ([ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md)) and a symlink's own ([ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md)).

Two other ways of restoring wrote content and nothing else: the command line's `restore-file`, and the recovery tool. Each of those records listed them among what it did not reach.

The rule was a private method of the executor. It lived in the restore project beside the plan and the receipt. The recovery tool may not reference that project: its closure is the format library, crypto, packing and storage, so that it runs on a clean machine ([architecture 11 §2](../architecture/11-solution-structure.md#2-dependency-rules)).

## Decision

### 1. One writer, in Domain

These move into Domain:
- the writer;
- the rules it writes by: which extended attributes are withheld, what a mode keeps when its owner, group or ACL did not land, and when a link's permissions land;
- the names a receipt gives each attribute;
- the account a restore runs as;
- the captured metadata map itself.

Domain is where every primitive the service and the recovery tool share already lives: the platform calls the writer orders, and the rendering the recovery tool's bundle shares with the service's log. What the plan predicts stays with the plan.

The executor writes every item through the writer, for the target it was given, so what it writes is unchanged.

### 2. `restore-file` writes back once the content has verified

The single-file restore writes the file's captured metadata back by the same rule, after the whole-file hash has verified, as the executor does (architecture 08 §3). It writes as a file, since what it makes is a file whatever the manifest's kind.

It writes:
- for this platform, as the account it runs as;
- with no claim to have captured what it restores, so a Linux ACL that names accounts by number is withheld, as on any restore where that is not known.

It then names what did not land, in the receipt's words. A refused write leaves that attribute not applied and never undoes the content ([ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md)).

## Consequences

- A file restored with `restore-file` gets back what the executor would give it: its times, its permissions, its owner and group where the account may give them, and its extended attributes, each where the target takes it.
- The recovery tool can now reach the rule. Giving it the writer is a record of its own.

## What this does not do

- **Give the recovery tool metadata.** It still writes none.

  > **2026-10 ([ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md)).** The recovery tool now writes a file's and a folder's metadata back through this writer, folders last and deepest first, and counts what did not land.
- **Know that this installation captured the file.** `restore-file` withholds a Linux ACL that names accounts by number even on the machine that captured it, since it does not compare the snapshot's device with its own.

  > **2026-10 ([ADR-0154](0154-the-single-file-restore-knows-who-captured-the-snapshot-it-names.md)).** Given a snapshot, `restore-file` now compares the device it names with this installation's own, and writes such an ACL back where they agree. Given a manifest alone, it still withholds one.
- **Make a folder, a link or a hard-link group.** `restore-file` writes one regular file.

## Alternatives considered

- **Let the recovery tool reference the restore project.** Rejected. Its closure would take the engine, the catalogue and SQLite, which a clean machine must not need.
- **Give each tool its own copy of the writer.** Rejected. Two copies of one rule drift apart, and a canary now fails if the executor or the command line ever writes through one.
- **Move the writer into the format library, beside the captured map.** Rejected. The format library holds codecs and touches no file. Domain holds every call the writer orders.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The metadata writer, its rules, the account a restore runs as and the captured map move into Domain; the executor writes through the writer, and the command line's restore-file writes a file's metadata back once its content has verified and names what did not land. The recovery tool still writes none. |
| 2026-10 | Accepted | [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md): the recovery tool writes metadata back through the writer this record moved into Domain, and the canary holds it to that writer too. |
| 2026-10 | Accepted | [ADR-0154](0154-the-single-file-restore-knows-who-captured-the-snapshot-it-names.md): `restore-file` given a snapshot knows whether this installation captured it, and gives back an ACL that names accounts by number where it did. |
| 2026-10 | Accepted (related) | [ADR-0173 Amendment 1](0173-a-files-spool-is-named-within-any-limit-its-own-name-meets.md#amendment-1-2026-10--the-single-file-restore-lands-its-file-the-same-way): `restore-file` lands its content beside its output and moves it there once verified, before this record's metadata is written. `Cli/CliApplication` |
| 2026-10 | Accepted | [ADR-0174 Amendment 2](0174-a-restore-writes-a-files-alternate-streams-back-where-the-target-keeps-them.md#amendment-2-2026-10--the-single-file-restore-writes-a-files-streams-back-by-the-same-rule): `restore-file` also writes a file's alternate streams back on Windows, before the times this record writes. |

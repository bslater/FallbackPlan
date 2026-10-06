# ADR-0084 — A restore writes back the times it can set, and records what each write did

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004, FR-RST-003
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (the rule this extends, and what a restore says it does not write back), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (preserve, degrade and report, or refuse), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) (metadata after content), [ADR-0079](0079-sparse-restore.md) (why a restored file's helpers live in Domain)

**Built:**
- The times: `Domain/FileTimes` sets a creation time where the platform has a call that sets one and converts a captured time without throwing. `Restore/RestoreExecutor` writes each attribute on its own and records what landed. `Restore/RestoreMetadata` holds the rule the plan predicts by. `Restore/RestorePlan` gives the target profile its creation-time capability.
- The tests:
  - `Domain.Tests/FileTimesTests` — which platforms set a creation time, that setting one leaves the modification time alone, and an impossible time
  - `Repository.Tests/RestoreMetadataHonestyTests` — access and creation times read back as captured, a creation time the platform cannot set is listed whatever the profile claims, an impossible time is not applied and the run completes, and the plan declares only what the target cannot set
  - `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` — access times are no longer listed or declared through the service and the CLI

---

## Context

[ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) made a restore say which captured metadata it did not write back. A file got back its modification time everywhere and its permissions on a POSIX target. Everything else was named, per item, as not applied, and its "does not do" named the next step: creation and access times, then ownership.

Writing them back turned up three defects in the path that already existed.

- **A refused write failed the item after its content had landed.** Metadata was applied inside the same `try` as the move that lands the file, so an `IOException` from setting a time marked the item failed. Its content had arrived and verified. A recovery drill counts a failed item as a failed drill.
- **An impossible time ended the whole run.** A captured modification time went through `DateTimeOffset.FromUnixTimeMilliseconds`, which throws for anything past the year 9999. That throw was outside the per-item catch, so the run ended with no receipt. A manifest is untrusted input: a damaged or hostile repository could stop any restore that reached such a file.
- **The receipt recorded the rule, not the outcome.** Each item's list of what was not applied came from what the rule said the target applies. A write that failed would still have been reported as applied.

Two platform facts shape the rest. Linux has no call that sets a file's creation time. And .NET's `File.SetCreationTimeUtc` does not refuse where it cannot set one: on Linux it writes the modification time instead, and on macOS it does the same whenever a volume keeps no creation times. A restore that used it would overwrite the modification time it had just put back, and say nothing.

## Decision

### 1. What is written back, in what order

A file gets back, each where captured:

1. its permissions, where the target applies POSIX metadata (unchanged);
2. its modification time, everywhere (unchanged);
3. its access time, everywhere;
4. its creation time, where the target can set one.

The order is fixed by what each write disturbs. Permissions change no time, so they go first. The creation time goes last. On macOS, a modification time set earlier than the creation time makes the volume move the creation time back to it, and .NET then restores the one before. Written last, the captured creation time is the last word whichever of those holds. Every write still follows the content (architecture 08 §3).

> **2026-10 ([ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md)).** Ownership now goes first, ahead of the permissions, because changing a file's owner or group clears its set-id bits. The permissions that follow keep a set-id bit only where its owner or group landed.

### 2. A creation time only through a call that sets one

`Domain/FileTimes` sets a creation time on Windows through .NET, and on macOS through `setattrlist` with `ATTR_CMN_CRTIME` and `FSOPT_NOFOLLOW`, called directly. A volume that keeps no creation times answers with an error, and that is answered as not set. On Linux nothing is attempted. .NET's setter is never used on either Unix, because both of its fallbacks write the modification time.

`FileTimes` lives in Domain, beside `SparseFile`, for the reason [ADR-0079](0079-sparse-restore.md) gives: the engine's restore and the standalone recovery tool both write restored files, and Domain is the one assembly both may reach. The recovery tool applies no metadata today; when it does, it uses the same call.

### 3. Each attribute stands alone, and the receipt records what landed

Each attribute is written on its own. A write the platform refuses, with an `IOException` or `UnauthorizedAccessException`, leaves that attribute not applied, and the item keeps the outcome its content earned. The writes sit outside the landing's catch, so nothing about metadata can fail an item.

The executor returns what actually landed, and the receipt's `not_applied` is what was captured minus that. So it now names what the target did not get back, rather than what the rule expected it not to. A volume that refuses a creation time the platform supports is listed. So is a time no file can carry. Receipt schema 6 keeps its shape; only what its list means has tightened.

### 4. A time no file can carry is not applied

`FileTimes.FromUnixMilliseconds` answers null for a time past the last instant a file can carry, the end of the year 9999. The executor treats null as nothing to apply. That is the same answer as a time never captured, except that the receipt lists it, because it was captured.

### 5. The plan predicts by the rule

The plan cannot know what a write will do before it is made. It predicts by the target's profile, which gains `SupportsCreationTimes`, set from `FileTimes.CanSetCreationTime`. The executor attempts a creation time only where the profile allows one, so a profile and its run agree. Access times are never declared now, because every target sets them. Creation times are declared where the target cannot set them, and say so.

## Consequences

- A restored file's times match the captured file's on every platform, apart from the creation time on Linux. The plan says so before the run, and the receipt says so after.
- A restore no longer fails a file, or a drill, because a volume refused an attribute. Nor does a damaged manifest's impossible time end a run.
- The service and CLI tests anchored on access times as the attribute every file carries and no target applied. That is no longer true. They now anchor on an attribute still not written back, the owner on a POSIX host and the attribute bits on Windows, and they assert that access times are gone.
- Capture already reads atime and excludes it from the metadata digest ([architecture 06 §4.3](../architecture/06-filesystem-capture.md)). An unchanged file therefore keeps the access time from the version that first captured it, and a restore puts back that one.

## What this does not do

- **Ownership.** Owner and group are captured by name, and not yet written back. That is the next slice, which needs native calls to resolve a name and give a file away, and the privilege to do it.

  > **2026-10 ([ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md)).** Owner and group are now written back where their names resolve on the target and the restoring account may give them. The plan says, per file, which will not land and why.
- **A directory's own metadata.** The executor creates directories and never reads their captured metadata, so a restored directory has the times and mode it was created with. Its receipt item names none of it, which is a silent drop that architecture 06 §3 rules out. Writing a directory's metadata back has to wait until its children have landed, since each child moves the directory's modification time. That is owed.

  > **2026-10 ([ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md)).** A folder the restore makes now gets its own metadata back by this record's rule, once everything in it has landed, deepest folder first. A folder already at the destination keeps its own, and its receipt item says so.
- **A symlink's own metadata, extended attributes, Windows attribute bits, security descriptors and alternate streams.** These are still captured, declared and listed as before ([ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)).

  > **2026-10 ([ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md)).** Extended attributes are now written back on Linux and macOS, after ownership and before the permissions in §1's order. The rest of this item remains owed.
- **The recovery tool and the CLI's `restore-file`.** Both write content and no metadata, not even a modification time.

## Alternatives considered

- **Use .NET's `File.SetCreationTimeUtc` and verify by reading the times back.** Rejected. Its fallback writes the modification time, so a read-back would have to detect a write it did not ask for and undo it. Where the captured creation and modification times are equal, it could not tell that anything had happened. The direct call refuses instead, which is the answer wanted.
- **Keep reporting by the rule.** Rejected. The rule cannot see a volume's refusal or an impossible time, and a receipt that said "applied" for a write that failed would be the silent drop architecture 06 §3 rules out.
- **Fail the item when a write fails.** Rejected for the reason [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) rejected marking such an item degraded: the content landed and verified. An outcome that changed for metadata would stop distinguishing a file that is wrong from a file whose times are.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Access times are written back everywhere, and creation times on Windows and macOS through `Domain/FileTimes`. Each attribute is written on its own, a refused write is not applied rather than a failed item, and an impossible time is not applied rather than the end of the run. The receipt records what landed. |
| 2026-10 | Accepted | [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) writes ownership back, first in §1's order, and the permissions after it drop a set-id bit whose owner or group did not land. The receipt still records what each write did. |
| 2026-10 | Accepted | [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) holds a folder the restore makes to §1's rule and order, once everything in it has landed. The owed directory metadata is built. |
| 2026-10 | Accepted | [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) writes extended attributes back after ownership and before the permissions in §1's order, each on its own as §3 has it. |

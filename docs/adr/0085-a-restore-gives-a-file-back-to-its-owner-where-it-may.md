# ADR-0085 — A restore gives a file back to its owner where it may

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004, FR-RST-003
**Related:** [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) (the times, and the rule this extends), [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (what a restore says it does not write back), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (owner and group: preserve where the principal resolves), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) (metadata after content), [ADR-0079](0079-sparse-restore.md) (why a restored file's helpers live in Domain)

**Built:**
- The ownership: `Domain/FileOwnership` resolves a captured name on the target, gives a file to what it names, and knows the account this process runs as. `Restore/RestoreAccount` is that account on the target profile. `Restore/RestoreMetadata` decides each file's ownership, and keeps a set-id bit only with the owner or group it runs as. `Restore/RestoreExecutor` writes ownership first. `Restore/RestoreBlobSet` gives the plan each file's captured names and mode.
- The tests:
  - `Domain.Tests/FileOwnershipTests` — names resolve to the ids the system gives them, or to nothing; owner and group land apart; another account is refused without privilege and given with it; the process's account matches an independent read
  - `Repository.Tests/RestoreMetadataHonestyTests` — an owner and group the account may give are written back and not listed, one it may not give is listed while the file stays its own, a privileged restore gives a file away, a set-id bit survives the ownership write and is dropped where its owner or group did not land, and the plan says all of it per file
  - `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` — the account's own files come back as its own through the service and the CLI, and the plan declares no ownership for them
  - `TestSupport/FileOwner`, `TestSupport/PosixAccount` — an owner read and an account read kept apart from the product's interop; `TestSupport/PlatformFacts` gains the privileged counterpart of the unprivileged condition

---

## Context

[ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) wrote a file's times back and left ownership as the other half of the slice. A POSIX file's owner and group are captured by name (specification 06 §4.1 keys 5 and 6), because an id means nothing on another machine. Until now a restore listed both as not applied every time, even when the account running it was the owner.

Three facts shape the rest.

- **Only root gives files away.** Root, or a Linux process holding CAP_CHOWN, may give a file to any account and group. Any other process may keep a file as its own and give it a group it belongs to, and nothing more.
- **Changing ownership clears the set-id bits.** Giving a file to another owner or group clears its set-user-id bit, and its set-group-id bit where its group may execute it. Linux does so whoever makes the change, and macOS whenever the caller is not root. Permissions written before the ownership would be undone by it.
- **A set-id bit runs a file as its owner.** The executor wrote a captured mode whole. Restoring as root, it therefore landed every captured set-user-id file as a root-owned set-user-id file, whoever had owned it. Nothing about such a file looks wrong, and it is exactly the kind of file [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) reserves for refusal: wrong in a way the user could not detect, and granting more than the original did.

## Decision

### 1. A name is resolved on the target

`Domain/FileOwnership` looks a captured owner up with `getpwnam_r` and a group with `getgrnam_r`. A name no account or group has on the target resolves to nothing, and is not applied. The lookups are re-entrant, so a restore and a capture in one process share no static buffer. The id is read two pointers into the caller's entry, where it sits in `struct passwd` and `struct group` on Linux and macOS at either pointer width. Names are cached for a run, so a tree of one owner asks the system once.

Ownership by name means a name that belongs to a different person on the restoring machine gets the file. That is the choice specification 06 made, and it is the one a person restoring onto a rebuilt machine wants.

### 2. Who may give what

`Restore/RestoreAccount` is the account the restore runs as, on the target profile, read from the process: its user id, every group it is in, and whether it may give files away (root, or CAP_CHOWN read from `/proc/self/status`). An owner is given back where its name resolves and it is this account, or this account may give files away. A group is given back where its name resolves and this account is in it, or may give files away. Windows has no account here, because its owner lives in the security descriptor.

### 3. Owner and group are written apart, and first

Each is written with its own `lchown`, which never follows a link, so a refusal of the owner leaves the group the account could give. Ownership goes before the permissions, which go before the times ([ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) §1), because changing it clears the set-id bits. The executor attempts each wherever its name resolves, whatever the plan predicted, and the platform decides. A refusal is that attribute not applied, never the item failed.

### 4. A set-id bit only with its owner

A set-user-id bit is kept only where the owner it was captured under was given back, and a set-group-id bit only with its group. Otherwise the bit is dropped, the rest of the mode is written, and the receipt lists `posix_mode` beside the owner or group that did not land. Dropping the bit is the closest approximation that is not wrong, which is what architecture 06 §3 means by degrade and report.

### 5. The plan predicts per file, and says why

The plan's probe already decodes every manifest. Each file's facts now carry its captured owner, group and mode, and the plan resolves the names as the run will. Ownership is declared for two reasons, apart, because their remedies differ:

- a name that resolves to an account or group the restoring account may not give, which needs root or CAP_CHOWN, as the plan already said;
- a name that resolves to nothing on the target, which no privilege mends.

A set-id bit the run will drop is declared on a line of its own. The receipt summary's "needs root or CAP_CHOWN" goes only to an account without it, since one that has it was refused for another reason.

## Consequences

- A person restoring their own files gets them back as theirs, owner and group. A service running as root gives every file back to the owner its name resolves to.
- A plan over files restored by the account that owns them no longer lists ownership. The service and CLI tests had anchored on the owner as the attribute no target applied. They now anchor on a symlink, whose own metadata is still never written back.
- A set-user-id program captured from another account no longer lands as a set-user-id root program.
- The proof is split by privilege. The privileged half runs where the suite runs as root, a container; the unprivileged half runs on CI's runners. Neither place runs both.

## What this does not do

- **A directory's own ownership.** A directory's metadata is still neither written back nor named ([ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md)), and its owner with it.
- **A symlink's own owner.** `lchown` could set it. It is owed with the rest of a symlink's own metadata.
- **A Windows file's owner.** It lives in the security descriptor, which is captured and not applied.
- **Extended attributes**, POSIX ACLs among them, Windows attribute bits and alternate streams, which are still listed.
- **Whether a quarantine restore should keep set-id bits at all.** A restore running as root into the default quarantine folder recreates a root-owned set-user-id program faithfully, such as an old one from a historical snapshot, in a folder other accounts may reach. That predates this record, which neither makes it worse nor decides it.
- **The recovery tool and the CLI's `restore-file`**, which write no metadata.

## Alternatives considered

- **Predict by privilege alone.** Rejected. A person restoring their own files without privilege would be told their ownership will not be applied, when it will.
- **One `lchown` for owner and group together.** Rejected. A refused owner would take with it a group the account could have given.
- **Keep the set-id bit and report it.** Rejected. Reporting does not stop the file running as the restorer.
- **Refuse a set-id file whose ownership will not land.** Rejected. Its content is right, and dropping the bit leaves a file that is less than the original, not wrong. The plan says so before the run.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. Owner and group are written back on a POSIX target where their names resolve and the restoring account may give them, apart and before the permissions. A set-id bit is kept only with the owner or group it runs as. The plan predicts each file and says why ownership will not land. |

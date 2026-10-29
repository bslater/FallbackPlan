# ADR-0101 — A restore gives a symlink its own metadata back, onto the link itself

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004, FR-RST-003
**Related:** [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (the rule the plan and the receipt share, whose §5 left a link out because the platform's calls follow it), [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) (times, each write recorded as it landed), [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) (ownership, which goes first), [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) (a folder's metadata, and nothing applied through a link), [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) (extended attributes, each alone, never through a link), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (a link's own metadata is captured), [specification 06 §4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map) (the metadata map a link's manifest carries like a file's)

**Built:**
- The writes: `Domain/SymlinkMetadata` sets a link's own modification and access times on Linux and macOS through `utimensat` told not to follow it, and its permissions on macOS through `lchmod`. On Linux it sets no permissions: Linux gives every link `rwxrwxrwx` and has no call that changes them. `Restore/RestoreExecutor` gives a link it made the writes it gives a file, in the file's order, each by a call that changes the link itself. `Restore/RestoreMetadata` holds the rule: what a link gets on each target, the permissions Linux can carry on a link, the attributes Linux keeps on none, and the plan's lines, which count links beside files and folders.
- The tests:
  - `Domain.Tests/SymlinkMetadataTests` — a link's times land on the link and its target keeps its own; a link to nothing still takes them; one time alone leaves the other; on macOS a link's permissions land on the link and not its target; on Linux none are written and every link keeps `rwxrwxrwx`
  - `Repository.Tests/RestoreSymlinkMetadataTests` — a link gets its own times back and the file it points at keeps its own; a link pointing out of the restore is given its own times, owner and group, and the file outside keeps its permissions, times and owner; a link to nothing still gets its times; a link's owner and group come back where the account may give them, and its target's group is not overwritten through it; an owner that resolves to no account is listed; on Linux a link captured with `rwxrwxrwx` lists nothing and one captured with others lists them; on Linux a user attribute and an ACL on a link are listed with why; as root on Linux a trusted attribute lands on the link alone; on macOS a link's permissions and creation time land on the link; the plan counts a link on the lines files and folders use, adds the two lines that are a link's own, and leaves a link the target cannot make to the planner's line
  - `Repository.Tests/RestoreMetadataHonestyTests`, `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` — the link the honesty suites restore gets its times and owner back, through the engine, the service and the CLI's direct restore
  - `TestSupport/LinkStatus` — the oracle, a link's own status read without following it, because .NET reports a link's permissions from its target and has no call for its owner

---

## Context

Capture has always read a link's own metadata and never followed it: its times, mode, owner and group with `statx` or `lstat`, and its extended attributes with `llistxattr`. A restore made the link and wrote nothing onto it. [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) §5 said why: the calls that would set it follow the link. So every receipt listed every link's metadata as not applied, every plan carried one line for every link, and each record from ADR-0084 to ADR-0087 ended on the same owed line.

Four facts shape the rest.

- **A link may point anywhere.** Out of the restore, at a file the restore lands beside it, or at nothing. A write that followed it would stamp the link's captured times, owner or permissions onto whatever it points at. A restore runs as root to give files back to their owners, so that could be any file on the machine.
- **.NET follows a link for permissions, and promises nothing for times.** Its permission setter and getter act on what a link points at, and it has no call for a link's owner. Its time setters and getters act on the link itself on this runtime, measured, but no documentation promises that, so a restore cannot rest a security property on it.
- **Some calls never follow one.** `lchown` changes a link's owner and group, `lsetxattr` and macOS's `setxattr` with its no-follow flag write a link's attributes, and macOS's `setattrlist` with `FSOPT_NOFOLLOW` sets its creation time. A restore already used each of them, link-safe, for files and folders. `utimensat` with `AT_SYMLINK_NOFOLLOW` sets a link's own times on Linux and macOS, and macOS's `lchmod` its permissions.
- **Linux keeps less on a link.** It gives every link `rwxrwxrwx`, has no call that changes that, and consults none of it. It refuses a user-namespace attribute and an ACL on a link. Windows restores no links at all: its target profile says it cannot make one without elevation, and the plan reports every link skipped. So a link's Windows metadata never arises.

## Decision

### 1. Onto the link itself, never through it

A link the restore makes gets its captured metadata back, every write by a call that changes the link and never what it points at. Its times go through `utimensat` told not to follow a final link, on Linux and macOS, either time left as it is when none was captured. Its permissions go through `lchmod` on macOS. Its owner and group, extended attributes and creation time go through the calls a file's already use, which never follow one. Its creation time is set only on macOS, because the call that sets one on Windows follows a link. No .NET setter is used on a link: its permission setter follows one, and its time setters make no promise either way. A link to nothing gets its metadata back like any other.

### 2. The file's rule, in the file's order

A link is held to the rule a file is, in the order [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) and [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) fixed:
- the owner and group, each where its name resolves on the target and the restoring account may give it;
- the extended attributes, each alone, with the exceptions ADR-0087 makes and §4's;
- the permissions where the platform keeps a link's own (§3);
- the modification and access times;
- the creation time last, where the target can set one on a link.

A set-id bit on a link is held to ADR-0085's rule as a file's is, though nothing runs as a link. Each write is recorded as it landed ([ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md)). An attribute left is named in the item's detail, with why. Metadata alone changes no outcome.

### 3. Permissions where the platform keeps a link's own

macOS keeps a link's own permissions, and they are written. Linux gives every link `rwxrwxrwx`, has no call that changes them, and never consults them. So on Linux nothing is written. A link captured with `rwxrwxrwx`, which every link captured on Linux is, lands as captured, and its receipt lists nothing. A link captured with other permissions, as macOS makes them, cannot carry them: it lists `posix_mode`, and the plan says so first.

### 4. Linux keeps no user attribute and no ACL on a link

Linux refuses both on a link, so neither is attempted there. Each is left, named in the item's detail with why, and the plan declares it. A link captured on Linux carries neither, so this arises only for one captured elsewhere. A trusted or security attribute on a link is root's to write on Linux, as on a file.

### 5. The plan counts links beside files and folders

The plan's lines now count links where they count files and folders, as in "2 file(s), 1 folder(s) and 1 symlink(s)". It declares by the rule a link is now held to. Two lines are a link's own: permissions Linux cannot give a link (§3), and the attributes Linux keeps on none (§4). The line that spoke for every link is gone. A target that cannot make links reports them skipped on the planner's own line, as before, and their metadata goes unmentioned.

## Consequences

- A restored link carries its own captured times, owner, group and attributes on Linux and macOS, and on macOS its permissions and creation time. A restore on the machine that captured it lists nothing for it.
- A restore as root of a link that points at a system file leaves that file as it was. Nothing in the run writes through a link.
- Every plan over a tree with links shrinks by a line, and every receipt stops listing what links now get back.
- A plan's counts are now three-part where a link is counted, so a line's wording changed. Nothing parses it; the console shows it as written.
- The proof is split by privilege and platform, as ADR-0085's and ADR-0087's are. A container running as root proves a link given away and its trusted attribute written. CI's unprivileged runners prove the account's own groups. macOS's leg proves a link's own permissions and creation time.

## What this does not do

- **A handle-relative restore.** A link's metadata is written by path, as a file's and a folder's are. Between making the link and writing onto it, an account that may write in the restore's folder could put something else at that path. Every write is by a call that does not follow a link, so a substituted link is the one changed and nothing it points at. The window [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) records stands for this too.
- **Windows links.** Windows restores no links, so a link's Windows metadata is never written.
- **A link already at the destination.** The existing-file policy decides whether the run makes a link, as it does for a file. Metadata goes only onto a link the run made.
- **The recovery tool and the CLI's `restore-file`**, which write no metadata.

  > **2026-10 ([ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md)).** The CLI's `restore-file` now writes a file's metadata back through the writer the executor uses, and makes no symlink. The recovery tool still writes none.

  > **2026-10 ([ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md)).** The recovery tool now writes back a file's and a folder's metadata through the same writer. It still makes no symlink.

## Alternatives considered

- **Leave links as they were, and keep saying so.** Rejected. The calls that do not follow a link exist on both platforms that make links, and a restore already used most of them. One line a plan for every link said nothing a person could act on.
- **Set a link's times through .NET's setters, which act on the link itself on this runtime.** Rejected. That is the runtime's implementation, not a promise, and the property it would carry is that a restore as root writes through no link. A call that says so in its own flags cannot change under the restore.
- **Write Linux link permissions as captured and let the platform refuse.** Rejected. Linux has no call to refuse with. The permissions land only where they are those every link there has, which the receipt can say without a write.
- **Attempt a user attribute on a Linux link and list the refusal.** Rejected. The refusal is Linux's rule, not the volume's, so the plan can say it first, as ADR-0087 says what it withholds.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A link the restore makes gets its own times, owner, group, attributes and, on macOS, permissions and creation time, each by a call that changes the link itself. On Linux a link's permissions land only where they are those every link has, and a user attribute or ACL on a link is withheld and named. The plan counts links on the lines files and folders use. |
| 2026-10 | Accepted | [ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md): the command line's restore-file writes a file's metadata back through the writer the executor uses, and makes no symlink; the recovery tool still writes none. |
| 2026-10 | Accepted | [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md): the recovery tool writes a file's and a folder's metadata back through the writer the executor uses, and still makes no symlink. |

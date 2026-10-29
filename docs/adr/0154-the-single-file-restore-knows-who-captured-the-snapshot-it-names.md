# ADR-0154 — The single-file restore knows who captured the snapshot it names

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004
**Related:** [ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md) (which recorded this gap), [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) (the rule for an ACL that names accounts by number), [ADR-0020](0020-ed25519-signing-key-semantics.md) (a snapshot's device is attribution by claim), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification)

**Built:**
- The command line: `Cli/CliApplication`'s `restore-file` compares the device a snapshot it is given names with this installation's own. Where they agree, it writes a Linux ACL that names accounts by number back, as the executor does. Given a manifest alone, it still withholds one.
- The tests:
  - `Cli.Tests/InspectionCommandTests` — `restore-file` gives back an ACL that names accounts by number from a snapshot this installation captured, and withholds it, saying why, from one another installation captured and from a manifest named alone
  - `Cli.Tests/CliHarness` — a command run as another installation of the same repository, with a device of its own

---

## Context

A Linux ACL can name an account or a group by number, and a number means a different account on another machine. So the executor writes such an ACL back only where this installation captured the snapshot, which it judges by comparing the device the snapshot names with its own ([ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md)).

`restore-file` writes a file's metadata back through the same writer ([ADR-0149](0149-the-single-file-restore-writes-back-a-files-metadata-by-the-executors-rule.md)). It never claimed to have captured what it restored. So it withheld such an ACL even on the machine that captured it, and said so in the words *this installation did not capture the snapshot*. ADR-0149 recorded that.

`restore-file` takes either a file version's manifest or a snapshot, and restores the snapshot's first file. Given a snapshot, it already reads the snapshot's manifest, which names its device. A command-line session already knows this installation's device, as direct mode's restore does.

## Decision

Given a snapshot, `restore-file` judges that this installation captured it where the device the snapshot names is this installation's own, and writes through a target that says so. It then writes such an ACL back by the executor's rule.

Given a manifest alone, it still claims nothing. A file version names no device, and the same version may be held by snapshots of more than one. So such an ACL stays withheld, and the line naming what did not land says why.

The device a snapshot names is attribution by claim ([ADR-0020](0020-ed25519-signing-key-semantics.md)). The executor takes it the same way, and a member who would lie about it can already name any owner.

## Consequences

- On the machine that captured it, a file restored from a snapshot with `restore-file` gets its ACL back as the executor would give it.
- From another installation of the same repository, the ACL is withheld, and the line says that this installation did not capture the snapshot.

## What this does not do

- **Judge a manifest named alone.** It could be looked up among the snapshots that hold that version, but a version held by snapshots of two devices would still have no answer.
- **Give the recovery tool the judgement.** The recovery tool runs on a machine with no installation, so it claims to have captured nothing, and withholds such an ACL wherever it runs.

## Alternatives considered

- **Look the version up in the catalogue, and claim it where every snapshot holding it names this device.** Rejected for now. It adds a walk of the catalogue to a command an operator reaches for when something looks wrong, and it is a rule the executor does not have.
- **Write such an ACL back whenever the account restoring it is root.** Rejected. That an account may give a file to any number does not make the number mean the account it meant.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. `restore-file` given a snapshot compares the device it names with this installation's own, and writes an ACL that names accounts by number back where they agree. Given a manifest alone it still withholds one. |

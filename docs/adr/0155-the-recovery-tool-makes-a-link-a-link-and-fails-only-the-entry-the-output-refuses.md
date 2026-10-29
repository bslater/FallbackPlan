# ADR-0155 — The recovery tool makes a link a link, and fails only the entry the output refuses

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-004, FR-RST-005, FR-DRL-001
**Related:** [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) (which recorded that the tool makes no link), [ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md) (the rule every later path is placed by), [ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md) (a link's own metadata, onto the link), [architecture 08 §5](../architecture/08-restore-and-recovery.md#5-emergency-recovery)

**Built:**
- The tool: `Recovery/RecoverySession` makes a symlink a link, pointing where it pointed, wherever links can be made without a privilege. It replaces a file or a link standing at the link's name, and gives the link its own captured metadata onto the link through `Domain/MetadataWriter`. On Windows it still skips a link with a note.
- The containment: a folder, a file's spool or a file or link landing where the output will not take it fails that entry alone, with a note of its own kind, and the rest of the recovery continues.
- The tests:
  - `Repository.Tests/EndToEnd/RecoveryLinkTests` — a link is made pointing where it pointed; a link leading out of the output gets its own times back and nothing goes through it; a file at a link's name is replaced; a folder at a link's or a file's name fails that entry alone and is left as it was, and a file at a folder's name fails that folder with what it would have held; on Windows a link is skipped with a note

---

## Context

The executor makes a symlink a link where the target makes links, and gives it its own metadata onto the link, never through it ([ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md)). The recovery tool skipped every link with a note that making one was the full client's job. So a tree recovered from the passphrase alone came back without its links. [ADR-0150](0150-the-recovery-tool-writes-back-metadata-by-the-executors-rule.md) recorded that.

A link's target is the repository's text and may lead anywhere. Making such a link is safe only if nothing later is written through it. Since [ADR-0152](0152-the-recovery-tool-keeps-every-path-inside-its-output-by-the-executors-rule.md), the tool places every path by the executor's rule, which refuses a path through a link leading out of the output, a link it made itself included.

The tool also let any write the output refused end the whole recovery. A folder standing at a file's name made the rename into place throw, and nothing after that entry was restored. The executor contains such a fault to its item.

## Decision

### 1. A link is made a link

Where links can be made without a privilege, every platform but Windows, the tool makes a symlink a link, with the target text it was captured with. Whatever stands at the link's name, a file or a link, is replaced, as a file's rename replaces one. A folder standing there is not replaced.

The link's own captured metadata then goes onto the link through the executor's writer, never onto what it points at. What did not land is counted with the rest.

On Windows, the executor makes no link, and neither does the tool. It skips the link with its note, as before.

### 2. A write the output refuses fails its entry alone

The output's own calls are each contained to the entry they serve:
- making a folder;
- making a file's spool;
- renaming the spool into place;
- replacing what stands at a link's name and making the link.

Where the output refuses one, the entry fails with a note naming it and the platform's words, and the recovery continues. A folder that cannot be made fails with the subtree it would have held.

A store's fault is not contained. It is an I/O error too, but it says the archive cannot be read, not that one name cannot be written. It still ends the run, as it did.

## Consequences

- A tree recovered with the tool has its links back, each with its own times, and its owner where the account may give it.
- A link leading out of the output is made, and nothing is written through it: no later path, no metadata.
- One name the output will not give up costs that entry, not the recovery.

## What this does not do

- **Contain a fault while a file's content is written.** A full output fails the file being written and ends the run, as every file after it would fail the same way.
- **Restore a hard-link group as one file, or a special file.** These are still restored as separate files or skipped with a note.

  > **2026-10 ([ADR-0156](0156-the-recovery-tool-gives-a-hard-link-group-back-as-one-file-by-the-executors-rule.md)).** A hard-link group now comes back as one file, by the executor's rule.
- **Make a link on Windows.** That needs a privilege the executor does not ask for either.

## Alternatives considered

- **Skip a link whose target leads out of the output.** Rejected. The executor makes such a link, the target is what was captured, and the containment rule already keeps anything from being written through it.
- **Contain every I/O error, the store's included.** Rejected. A store that cannot be read fails every later entry too, and a run that ends says so once, with the cause.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The recovery tool makes a symlink a link where links can be made, replacing a file or link at its name and giving it its own metadata onto the link. A write the output refuses at an entry's name fails that entry alone, with a note, where it ended the run. |
| 2026-10 | Accepted | Noted by [ADR-0156](0156-the-recovery-tool-gives-a-hard-link-group-back-as-one-file-by-the-executors-rule.md): the tool now gives a hard-link group back as one file. |

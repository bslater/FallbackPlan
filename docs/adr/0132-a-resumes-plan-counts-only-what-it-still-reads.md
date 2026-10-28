# ADR-0132 — A resume's plan counts only what it still reads

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: a plan of a resume counts among what its run reads nothing the run already holds), FR-RST-008 (amended: the same of a resume planned through the service, its parts included)
**Related:** [ADR-0083 Amendment 2](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md#amendment-2-2026-10--a-plan-says-what-its-run-reads) (a plan says what its run reads), [ADR-0120](0120-a-resume-is-planned.md) (a resume is planned, measured where its run landed), [ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md) (a restore cut short resumes within a file, and the record that left its plan counting each file whole), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning)

**Built:**
- The probe: `Restore/RestoreBlobSet` counts among what a plan of a resume reads nothing of a file its run landed but the file's manifest, and nothing a part the run kept proves, told what is held by `Restore/RestoreHeld`. What is held is still probed for what is missing or archived.
- The proof: `Repository/RestorePart` walks a part by the rule a resume takes one up by, which `Repository/RestoreEngine` now shares, and proves a part without writing to it.
- The landed files: `Restore/RestoreSpace` names the files a resume will find landed, by the rule its measure counts them by.
- The plans: `Agent/ServiceCommandHandler` tells the probe what a resume's run landed and where it kept its parts; `Cli/OperationGateway`, in direct mode, what the run landed.
- The tests:
  - `Repository.Tests/RestoreWithinAFileTests` — a plan of a resume reads nothing a part proves, and only as far as it proves, nor opens a blob only for what is held; a part that proves all of a file leaves its manifest to read, as a file landed does; a hole a part holds leaves no segment unread; a file landed reads none of its streams; and a plan leaves a part as it found it
  - `Hosts.Tests/RestoreUnfinishedServiceTests` — a plan of a resume through the service reads nothing of a file its run landed, nor what its parts prove, and a part that does not prove saves nothing
  - `Cli.Tests/RestoreResumeCommandTests`, `Cli.Tests/RestoreOriginalDirectTests` — in direct mode, a plan of a resume reads nothing of a file its run landed at its length, a file at another length is read, and at several roots each root's file is found under its own label

---

## Context

A plan says what its run will read from where it restores from: each record a copy holds, once, and each blob's envelope once ([ADR-0083 Amendment 2](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md#amendment-2-2026-10--a-plan-says-what-its-run-reads)). A plan of a resume is measured where its run landed: a file found there at its length needs no room ([ADR-0120](0120-a-resume-is-planned.md)). What it read was counted as for a run afresh, every file's segments, the landed files' among them.

Since [ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md), a resume through the service takes up the part of each file its run was cut short in, and reads only the rest. A plan of a resume of a large file cut short near its end still said the run would read all of it. ADR-0129 recorded this as undone.

## Decision

### 1. A file its run landed is read for its manifest alone

A plan of a resume names the files the resume will find landed, by the rule its measure counts them by: at their length, where the resume lands them. Of those it counts only the manifest, which the resume reads to check each file's whole-file hash.

### 2. Through the service, a part is proved as the resume would take it up

Where the run kept a part of a file, the plan reads the part and proves it by the rule a resume takes one up by: each segment as far as its bytes derive its object identifier, each hole as far as it reads as zeroes, and nothing past the first piece that does not. It writes nothing to the part. What the part proves is not counted. The rule is written once, in the repository library, and the engine takes a part up by it too.

### 3. What is held is still probed

A file found landed at its length may still differ, and the resume then restores it, reading what it names. So whether a path is missing or archived is still asked of everything it names, as before.

## Consequences

- A plan of a resume says what the resume will read: less by each file its run landed, and by what each part it kept proves.
- A part that proves nothing saves nothing, so a plan never says the run reads less than it will for want of a proof.
- Where a file landed at its length differs, the resume reads it and the plan said it would not. A plan reads nothing of the destination but each file's length, as ADR-0120 has it.

## What this does not do

- **Hash what the run landed.** As ADR-0120 has it, the plan says what lies there at its length.
- **Credit a part in the room the run needs.** A resume's working space still counts its largest file whole ([ADR-0131](0131-the-service-measures-a-restores-working-space-where-its-parts-are-kept.md)).
- **Read parts in direct mode.** Direct mode keeps none (ADR-0129).

## Alternatives considered

- **Count a part by its length.** Rejected. A resume takes up only what a part proves, and a plan that counted more would say the run reads less than it does.
- **Hash each landed file to count it.** Rejected. The plan would read every landed file whole to save counting records, and a plan reads nothing of the destination beyond its length.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A plan of a resume counts nothing of a file its run landed but its manifest, and, through the service, nothing a part its run kept proves, proved by the rule the engine takes a part up by, now shared. What is held is still probed for what is missing or archived. No contract change. |

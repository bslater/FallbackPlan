# ADR-0162 — Every restore run the service left unfinished is let go at once

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: every run the service left unfinished is let go at once, each by its name)
**Related:** [ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md) (a run left unfinished is named, and let go by its name), [ADR-0133](0133-a-run-left-unfinished-says-what-its-parts-hold.md) (what its parts hold), [ADR-0158](0158-every-destination-a-restore-in-direct-mode-left-unfinished-is-let-go-at-once.md) (direct mode's parts let go at once, and the record that left the service's one run at a time)

**Built:**
- The verb: `Cli/CliApplication`'s `restore-resume --forget-all` asks, through `Cli/OperationGateway`, for every run the service left unfinished and then to let each go by its name, as `--forget` lets one go. It says each and how many, and names one the service will not let go with the service's words while the rest go.
- The tests:
  - `Cli.Tests/RestoreResumeVerbTests` — every run left unfinished is let go by its name, each said, and no passphrase is asked; none left is said; one the service will not let go is named with its words, the rest go, and the ask fails; beside a run, `--forget` or `--plan` it is refused; in direct mode no run is let go
  - `Hosts.Tests/RestoreUnfinishedServiceTests` — against a running service, the command line lets each run it left unfinished go, and that run's parts with it

---

## Context

The service names each restore it began and has not seen finish, and keeps the parts of the files the run was cut short in under its own state directory ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md), [ADR-0133](0133-a-run-left-unfinished-says-what-its-parts-hold.md)). A part is the plaintext of a backup's file. `restore-resume <run> --forget` lets one run go, and its parts with it.

A person who would resume none of them named each run in turn. Direct mode's parts went at once since [ADR-0158](0158-every-destination-a-restore-in-direct-mode-left-unfinished-is-let-go-at-once.md), which left the service's one run at a time.

## Decision

### 1. `--forget-all` lets every run go, each by its name

`restore-resume --forget-all` asks the service for every run it left unfinished, then asks it to let each go by its name, as `--forget` asks for one. The contract is unchanged, and no passphrase is asked, since letting a run go names none of the backup's files.

It says each run let go, in `--forget`'s words, and how many. Where none is left it says so, in the listing's words.

### 2. A run the service will not let go is named, and the rest still go

A run the service refuses, say one under way again, is named with the service's words. Every other run is still asked, and the command then fails.

### 3. It names no run

`--forget-all` beside a run, `--forget` or `--plan` is refused before the service is asked anything. In direct mode it is refused as every verb that names a run is, since only the service names one.

## Consequences

- A person who will resume none of the restores the service left unfinished lets every one go, and the plaintext its parts held, in one command.

## What this does not do

- **Let runs go by age.** Every one is let go, or one by its name.
- **Offer it in the console.** The console lets each run go beside it, as before.

## Alternatives considered

- **A contract command that lets every run go at once.** Rejected. The listing and the single let-go already say everything the person is asked, and a second way to let runs go would be a second way to get it wrong.
- **Stop at the first run the service refuses.** Rejected. A run under way again is the one the person did not mean, and the rest are still theirs to let go.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. restore-resume with --forget-all asks the service for every run it left unfinished and lets each go by its name, naming one it will not let go while the rest go. No contract change. |

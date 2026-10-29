# ADR-0142 — The parts a restore in direct mode keeps are listed and let go

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: each destination a restore in direct mode left unfinished is listed with what its run restores and what its parts hold, and its parts are let go by the name of their folder)
**Related:** [ADR-0135](0135-direct-mode-resumes-within-a-file.md) (direct mode keeps its parts by where a run lands, and the record that left listing and sweeping them undone), [ADR-0137](0137-a-run-in-direct-mode-left-unfinished-says-how-to-resume-it.md) (a run there that does not finish says what its parts keep, and the record that turned down keeping a record of each run for now), [ADR-0133](0133-a-run-left-unfinished-says-what-its-parts-hold.md) (the service's runs left unfinished say what their parts hold, the shape this listing takes), [ADR-0139](0139-a-kept-restore-receipt-is-let-go.md) (a kept receipt is let go from its state directory by its name, the shape letting parts go takes)

**Built:**
- The record: `Cli/OperationGateway`'s direct restore says, beside its parts, what the run restores and where, through `Cli/DirectRestoreParts`, which counts a folder's parts alone as what it holds.
- The listing: `Cli/DirectRestorePartsReport` writes each destination left unfinished, newest first, and lets one go by its folder's name.
- The verb: `Cli/CliApplication`'s `restore-parts --state`, with `--forget <name>`.
- The tests:
  - `Cli.Tests/RestoreWithinAFileDirectTests` — a run that does not finish is listed by its folder's name, with its snapshot, where it lands, the policy asked, when it began and what its parts hold, naming no file of the backup; a run there that finishes is listed no more; each destination left unfinished is listed, newest first; parts with no account of their run are listed as such; one destination's parts are let go by its name alone and every other's stay; a name no folder has is refused naming it, and what is not a folder's name is refused and lets nothing go; the run that does not finish says how to let its parts go; a state directory that is not there is refused
  - `Cli.Tests/RestoreOriginalDirectTests` — a set's run to the original location that does not finish is listed as the set's, landing where its files came from

---

## Context

A restore in direct mode keeps the parts of the files it assembles under the state directory, in a folder named by where it lands, until a run there finishes ([ADR-0135](0135-direct-mode-resumes-within-a-file.md)). A run that does not finish says how many bytes they keep, and where, as it ends ([ADR-0137](0137-a-run-in-direct-mode-left-unfinished-says-how-to-resume-it.md)). Nothing said so after. A person who decided not to resume had the backup's plaintext under the state directory, in a folder whose name is a digest, until they deleted it by hand. Once the terminal that printed it had gone, nothing said where it was. Both records left a listing undone, and ADR-0137 turned down keeping a record of each run until a listing wanted one.

## Decision

### 1. Each run says, beside its parts, what it restores and where

A run in direct mode writes a record into its folder before it assembles anything:
- the snapshot it restores;
- the set, where this state's configuration names one;
- where it lands, or that it lands each file where it was captured;
- the policy asked for a file in the way;
- when it began.

Each run there writes it again. It names none of the backup's files, as the service's record of a run left unfinished does not ([ADR-0115](0115-a-restore-cut-short-is-named-and-resumed-by-its-name-alone.md)). A run refused for room writes nothing, and a run that finishes lets the record go with the parts. What a folder holds is its parts alone, so the record is never counted among the backup's bytes.

### 2. `restore-parts` lists each destination left unfinished

`restore-parts --state <directory>` lists every folder of parts the state directory keeps, newest first. Each is listed by its folder's name, with what its run said of itself and what its parts hold. A folder kept before runs said anything of themselves is listed by what it holds, saying its run's account was not kept. The verb reads only the state directory, and asks no passphrase, as `restore-receipts` does not.

### 3. `--forget` lets one destination's parts go by that name

`restore-parts --state <directory> --forget <name>` removes that folder and says how much it held. Every other destination's stays.
- **What is not a folder's name**, thirty-two lowercase hex digits, is refused before it is joined to a path, and lets nothing go.
- **A name no folder has** is refused, naming it.

A run there that does not finish says the command that lets its parts go, after where they are kept.

## Consequences

- A person finds every destination direct mode left unfinished under a state directory, and what of the backup each still keeps, long after the run that left it.
- A person who will not resume lets that plaintext go by a name the listing and the run's own last line both give.
- A folder is named by where its run lands, so the name is the same for every run there. It is not a run's name: a resume in direct mode is still the same command run again with `--resume` ([ADR-0135](0135-direct-mode-resumes-within-a-file.md)).

## What this does not do

- **Resume by the name.** The listing says what each run restored and where. The resume is that restore again with `--resume`, naming the same paths, which the listing does not give because they are the backup's file names.
- **Tell a run under way from one left unfinished.** Direct mode keeps no lock on a destination. A run in another process is listed too, and letting its parts go while it runs costs it what it had assembled.

  > **2026-10 ([ADR-0143](0143-a-destination-takes-one-restore-in-direct-mode-at-a-time.md)).** A run holds its folder while it runs. The listing says a run is under way there, and `--forget` lets none of its parts go.
- **Sweep folders by age.** One destination at a time, by its name.

  > **2026-10 ([ADR-0158](0158-every-destination-a-restore-in-direct-mode-left-unfinished-is-let-go-at-once.md)).** `restore-parts --forget-all` lets every destination's parts go at once, passing over one with a run under way. None goes by age.

## Alternatives considered

- **Let parts go by naming the destination again, as the run was asked.** Rejected. A run to the original location of a set with several roots lands at all of them, and a destination typed another way names another folder. The folder's name is what both the listing and the run's last line give.
- **Keep the record in a file of its own beside the folders.** Rejected. Inside the folder it goes when the folder goes, however that happens.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A run in direct mode says beside its parts what it restores and where; restore-parts lists each destination left unfinished, newest first, with what its parts hold, and lets one go by its folder's name; a run that does not finish says how. No contract change. |
| 2026-10 | Accepted | [ADR-0143](0143-a-destination-takes-one-restore-in-direct-mode-at-a-time.md): a destination with a run under way is listed as such, and --forget refuses it. |
| 2026-10 | Accepted | Noted by [ADR-0158](0158-every-destination-a-restore-in-direct-mode-left-unfinished-is-let-go-at-once.md): every destination's parts are let go at once with --forget-all, which this record left one at a time. |

# ADR-0160 — A restore in direct mode judges where it lands through links

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: a run whose destination a link makes nest with one under way is refused, writing nothing)
**Related:** [ADR-0157](0157-a-restore-in-direct-mode-is-refused-a-destination-that-nests-with-one-under-way.md) (the judgement, and the record that left links undone), [ADR-0159](0159-a-restore-into-a-folder-that-is-itself-a-link-restores-what-lies-below-it.md) (the following of a path through its links), [ADR-0143](0143-a-destination-takes-one-restore-in-direct-mode-at-a-time.md) (a destination's folder of parts, named by its spelling)

**Built:**
- The judgement: `Cli/DirectRestoreParts` holds two folders to nest where they do as written, as before, or where each one's links lead, followed by `Domain/RestorePaths`.
- The tests:
  - `Cli.Tests/RestoreWithinAFileDirectTests` — on POSIX, a run through a link into a folder inside a destination under way is refused, writing nothing; so is a run into a folder inside where a link under way leads, a run through a link into a folder that holds one, and a run into a destination under way spelt through a link; a run through a link that leads beside a destination under way goes ahead, and a run into a folder inside one that a link leads out of is refused as before

---

## Context

A restore in direct mode is refused where a run under way lands in a folder that holds or lies inside one it lands in, since the two would write the same files ([ADR-0157](0157-a-restore-in-direct-mode-is-refused-a-destination-that-nests-with-one-under-way.md)). The judgement was made from the paths as written. Two destinations that only a link makes nest were judged apart, and ADR-0157 recorded that.

One destination spelt two ways, through a link and not, was not even held as one. A destination's folder of parts is named by its spelling ([ADR-0143](0143-a-destination-takes-one-restore-in-direct-mode-at-a-time.md)), so the two spellings held two folders, and nothing kept the two runs apart.

A path can now be followed through every link it passes ([ADR-0159](0159-a-restore-into-a-folder-that-is-itself-a-link-restores-what-lies-below-it.md)).

## Decision

### 1. Two folders nest as written or where their links lead

Two folders are held to nest where one holds or lies inside the other as written, as before, or once each is followed through its links. Each is followed when the run is judged, so a run under way whose record was written before links were judged is judged by where its folder leads now.

The refusal names both folders as written, the run under way's and the one asked, since those are what the person typed.

### 2. What nests as written still nests

A run into a folder inside a destination under way is refused even where a link inside the destination leads that folder elsewhere. The two runs may not write the same files, but the run under way could still meet the link's folder as it writes, and the judgement errs towards refusing.

## Consequences

- Two restores in direct mode into one place never run at once, however that place is spelt.
- A run through a link that leads beside a destination under way is not mistaken for one into it.

## What this does not do

- **Keep a run in direct mode from the service's runs.** The service keeps its parts apart and holds its own runs, as before.
- **Hold one destination by one folder of parts.** Two spellings still keep two folders, so a run left unfinished by one spelling is resumed by the same spelling.

## Alternatives considered

- **Name a destination's folder of parts by where it leads.** Rejected. A run left unfinished before would no longer find its parts under its own spelling, and a link changed after a run would move its parts out from under it.
- **Judge only where the links lead.** Rejected. A folder inside a destination as written is one the run under way may yet write through, wherever it now leads.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A restore in direct mode is refused where a run under way lands in a folder that holds or lies inside one it lands in, as written or once each is followed through its links, so a destination spelt through a link is one destination. |

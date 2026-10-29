# ADR-0157 — A restore in direct mode is refused a destination that nests with one under way

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-008 (amended: a run into a folder that holds or lies inside a destination under way is refused, writing nothing)
**Related:** [ADR-0143](0143-a-destination-takes-one-restore-in-direct-mode-at-a-time.md) (the hold, and the record that left nesting destinations apart), [ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md) (the record beside a destination's parts), [ADR-0034](0034-hub-and-spoke-destinations.md) (the lexical containment rule, which folds case)

**Built:**
- The hold: `Cli/DirectRestoreParts` takes a destination's folder under one claim of every destination's folder. It refuses a run where a run under way lands in a folder that holds or lies inside one this run lands in, and records what the run restores and every folder it lands in before letting the claim go. A run refused so leaves no folder it made.
- The run: `Cli/OperationGateway`'s direct restore says which destination under way its own nests with, and where.
- The tests:
  - `Cli.Tests/RestoreWithinAFileDirectTests` — a run into a folder inside a destination under way is refused before anything is written, its parts' folder included, and so is a run into a folder that holds one, and one judged by a record that names one destination alone; a run beside a destination under way goes ahead, though its name begins with the other's, and so does a run into a folder inside a destination left unfinished; a refused run keeps what a run before it left at its own destination, and holds it no more

---

## Context

A restore in direct mode holds a folder of parts named by where it lands, and a second run to the same place is refused while the first holds it ([ADR-0143](0143-a-destination-takes-one-restore-in-direct-mode-at-a-time.md)). A run to a folder inside another's destination, or to one that holds it, lands somewhere else by name, so it held a folder of its own. The two runs then wrote the same files, each under its own existing-file policy, and neither knew. ADR-0143 recorded that.

Each run already says, beside its parts, what it restores and where ([ADR-0142](0142-the-parts-a-restore-in-direct-mode-keeps-are-listed-and-let-go.md)). A run to where a set's files were captured lands in each set's root and said none of them.

## Decision

### 1. A run is judged against every run under way

A run takes its destination's folder as before. It then reads where each other run under way lands, from that run's record, and is refused where one of those folders holds or lies inside one it lands in. It is refused before it writes anything, with the folder of the run under way and its own.

A run refused so leaves nothing behind. It had taken a folder to be judged, so it lets that folder go if it made it, and `restore-parts` does not list it. A folder a run before it left at the same destination keeps its parts and its record, and the refused run holds it no more.

The judgement is lexical and folds case, as the rule that keeps a backup from capturing itself does ([ADR-0034](0034-hub-and-spoke-destinations.md)). The conservative direction refuses two spellings of one folder, never lets them through.

A run that ended holds nothing, whatever its record still says, so a destination left unfinished refuses nothing.

### 2. Every folder a run lands in is recorded

A run's record now names every folder it lands in. A run to where the files were captured names each set's root. A record written before names one destination at most, and is judged by that.

### 3. One claim covers the judgement

The judgement and the record are made under one claim of every destination's folder, held only while a run takes its own. Two runs starting at once therefore never both judge before either has recorded. A run that finds the claim taken waits for it a few seconds, since a judgement takes moments.

## Consequences

- Two restores in direct mode never write the same files at once through nesting destinations.
- A run beside another's destination is not mistaken for one inside it, since a folder's name that begins with another's is not a folder inside it.

## What this does not do

- **Keep a run in direct mode from the service's runs.** The service keeps its parts apart and holds its own runs, as before.
- **Judge through links.** The judgement is from the paths alone, so two destinations that one link makes nest are judged apart.

## Alternatives considered

- **One hold for every restore in direct mode.** Rejected. Two runs to unrelated folders need not wait for each other.
- **Hold each folder above the destination too.** Rejected. A run would then hold folders it never writes, and a run to one of them would be refused by a run below it only if the holds happened to meet.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. A restore in direct mode is refused where a run under way lands in a folder that holds or lies inside one it lands in, judged under one claim of every destination's folder, and its record names every folder it lands in. A run refused so leaves no folder it made. |

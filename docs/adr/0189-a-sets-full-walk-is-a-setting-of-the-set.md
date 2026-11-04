# ADR-0189 — A set's full walk is a setting of the set

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-012 (amended: a set is walked in full when its full walk falls due, on the schedule the set names for it or a day after the last where it names none, and its status says when it last was; Amendment 1: the console sets it and shows the last)
**Related:** [ADR-0188](0188-the-service-tells-a-backup-what-changed-since-the-sets-last.md) (the service tells a backup what changed since the set's last, and walks in full at least once a day), [ADR-0027 §1](0027-services-scheduling-status-telemetry.md#1-schedule-semantics-and-missed-runs) (a set's schedule and its grammar), [ADR-0035 §1](0035-destination-fitness.md#1-fitness-is-reported-not-enforced-at-load) (a defect in the file is not refused at load), [architecture 06 §4](../architecture/06-filesystem-capture.md#4-change-detection) (a reconciliation walk is mandatory, on a schedule)

**Built:**
- The setting: `Application/ClientConfiguration` carries a set's full walk, written only where it is set.
- The walk: `Agent/ChangeHints` asks the set's full walk whether it is due, from the last full walk.
- The surface: `Api/Results` carries the full walk on the set's descriptor and the last full walk on its status, at contract 1.82 (`Api/ContractVersion`); `Agent/ServiceCommandHandler` keeps the full walk where an edit is silent, clears it where it speaks empty, refuses one that does not read, and reports the last full walk.
- The command line: `Cli/CliApplication` prints the last full walk on the set's line of `status`.
- The tests: `Hosts.Tests/ChangeJournalRunTests` — a set walked in full every two hours walks in full two hours on; one walked in full daily at an hour walks in full at its first backup after the hour, and the full walk starts no backup; one that does not read walks every backup in full; the due check is a day by default, the schedule's answer otherwise, and due where it cannot be told; the status names the last full walk, and none for a set whose backups ask no journal. `Hosts.Tests/ConfigurationCommandTests` — an edit keeps, clears and refuses the full walk. `Application.Tests/ClientConfigurationTests` — the file keeps it under its name. `Api.Tests/ContractAdditiveFieldsTests`, `Api.Tests/ConfigurationContractTests` — the wire names, an older service's silence, and the version. `Cli.Tests/StatusFullWalkTokenTests` — the token.

---

## Context

ADR-0188 walks a set in full at least once a day, whatever the change journal says, and leaves the day fixed. The owner asked for the full walk to be a schedule the set's configuration defines, so a backup can always be relied on to be complete. A day suits a set backed up hourly. It is wasteful for a large set that changes little, and too long for a set whose owner wants the walk to be the night's work.

A set already has a schedule, in a grammar the scheduler, the console and the command line all speak: `every <n><unit>`, anchored to the last run, or `daily at HH:mm` on the wall clock (ADR-0027 §1).

## Decision

### 1. A set names its full walk in its schedule's grammar

A set's configuration may carry `full_walk`: `every 7d`, `every 6h`, `daily at 02:00`. A set that names none is walked in full a day after the last, as before (`every 1d`). The full walk is due when the schedule would say a run is due, from the last full walk in the frame the set's schedule is judged in. So `daily at 02:00` keeps the schedule's answer of one walk per night across an offset change.

### 2. It says which backup walks in full, and starts none

The first backup after the full walk falls due walks in full, told nothing of what changed. The set's schedule still says when backups run. A set backed up less often than its full walk walks in full every backup. A manual set walks in full at the first backup asked for after it falls due.

### 3. Where it cannot be told, it is due

A full walk that does not read is due, as is an interval too large to mean anything, and a clock behind the last full walk. It is not refused when the file loads, for the reason a schedule is not (ADR-0035 §1): a typo in one set's file should cost a full walk, not the set's backups.

### 4. The contract carries it, at 1.82

The set's descriptor gains `full_walk`, as the file spells it, and null where the set names none. On an upsert:

- null keeps what the set has, which is what every client before 1.82 sends;
- empty clears it back to a day; and
- one that does not read is refused with the defect named, and the file is left as it was.

A set's status gains `last_full_walk_at`, from what the set's last backup kept (ADR-0188 §2). It is null for a set never backed up. It is also null for a set whose backups ask no journal: every one of them walks in full, and the set's last completed backup already says when.

### 5. The command line shows it

The set's line of `status` ends `full-walk@<UTC minute>` where the service reports one.

## Consequences

- **A set can trade a longer wait for a missed change against a shorter backup.** A change the journal did not record waits at most until the full walk falls due and the set's next backup runs.
- **A full walk at night is a setting.** It does not start a backup, so a set backed up hourly walks in full in the hour after it.
- **Nothing changes for a set that names none,** nor on Linux and macOS, where every backup walks in full.

## What this does not do

- **Let the console set it or show it.** The set editor and the console's status are the next record.

  > **2026-11 ([Amendment 1](#amendment-1-2026-11--the-console-sets-the-full-walk-and-shows-the-last)).** The console's set editor sets it beside the schedule, and a set's card says when it was last walked in full.
- **Give the command line a way to set it.** The command line edits no set today. The configuration file and the console do.
- **Check it in a draft.** `validate_set_draft` does not take the full walk. The save refuses one that does not read.
- **Walk every backup in full by a word of its own.** `every 1m` comes close for a set backed up less often than a minute apart. A full backup asked for walks in full and reads every file again.

## Alternatives considered

- **A number of hours.** It cannot say "at night", which is when an owner wants the long walk. The schedule's grammar can, and a person already knows it from the set's schedule.
- **A full walk that starts a backup of its own.** The set's schedule owns when backups run, and two schedules starting backups would contend for one set. Marking which backup walks in full keeps one source of when.
- **Refuse a full walk that does not read when the file loads.** Every set would stop backing up over one set's typo. Walking in full costs only time.

## Amendment 1 (2026-11) — the console sets the full walk and shows the last

### Context

Contract 1.82 carried a set's full walk and its last full walk, and the command line printed the last. The console did neither. A person managing sets there could not see or change when a set is walked in full.

### Decision

- **The set editor sets it beside the schedule.** The schedule section offers the full walk in the schedule's own choices: daily (the default), every so many minutes, hours or days, or daily at a time. A form those choices cannot show checks none of them, and is kept as the set has it, as the schedule's own custom form is. The new-set wizard's last step takes it with the schedule.
- **The summary says it.** The schedule row reads "… · walked in full every 7 days", or "daily (the default)".
- **The editor says what it shows.** Its upsert always carries the full walk: the set's own, or empty for the default. It never sends null, which would keep whatever stands.
- **A set's card says when.** On the overview, the line with the set's next run gains "walked in full 3 h ago", with the time on hover. A set whose backups ask no journal shows nothing there.

### Consequences

- A set's full walk is set and read where its schedule is.
- An edit in the console cannot leave a full walk the person did not see in place.

### What this does not do

- **Check it before the save.** A full walk the person types that does not read is refused by the save, in the service's words, as an unreadable schedule would be at the next pass.
- **Show when the next full walk falls due.** The status carries the last one only.

**Built:** `Web.DomTests/FullWalkDomTests` — an existing set's summary says its full walk, and the schedule section changes it; putting it back sends the empty that clears it; an edit that leaves it alone sends what the set has; the wizard's last step takes it; a set's card says when it was last walked in full, and nothing where no journal is asked. `Web.Tests/StatusRelayNamesTests`, `Web.Tests/CommandRelayTests` — the names the console reads cross the relay.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first: the setting, the due check, contract 1.82 and the command line's status. |
| 2026-11 | Accepted | [Amendment 1](#amendment-1-2026-11--the-console-sets-the-full-walk-and-shows-the-last): the console sets the full walk beside the schedule and shows the last on a set's card. |

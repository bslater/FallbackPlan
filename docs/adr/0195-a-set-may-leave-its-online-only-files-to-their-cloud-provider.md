# ADR-0195 — A set may leave its online-only files to their cloud provider

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-015 (new: a set may leave its online-only files to their cloud provider. Its backups fetch none, leave a new or changed one out of the snapshot without failing it, and say how many they left. Its status says that it leaves them, its changes waiting count none it will leave, and a set that stops leaving them walks in full at its next backup)
**Related:** [ADR-0185](0185-a-run-stops-fetching-online-only-files-after-the-first-that-does-not-come.md) (a run stops fetching online-only files after the first that does not come, and its Amendment 3: a file not fetched keeps its version before), [ADR-0193](0193-a-sets-status-says-which-online-only-files-its-last-backup-could-not-fetch.md) (a set's status says which online-only files its last backup could not fetch, and its Amendment 1: behind, not partial), [ADR-0188](0188-the-service-tells-a-backup-what-changed-since-the-sets-last.md) (the service tells a backup what changed since the set's last), [ADR-0192](0192-a-sets-status-says-how-many-changed-files-its-next-backup-will-take.md) (a set's status says how many changed files its next backup will take)

**Built:**
- The setting: `Application/ClientConfiguration` keeps `leave_online_only` on a set, written only where it is chosen.
- The run: `Repository/SnapshotPublication` asks no provider for a file when its job says to leave them, and counts each it leaves.
- The count: `Repository/SourceComparer` counts none the next backup will leave.
- The full walk after: `Agent/ChangeHints` keeps, with the journal places, whether the set's snapshot may lack a file left, and walks a set that no longer leaves them in full. `Agent/BackupRunner` passes the choice to the run and says how many it left.
- The wire: `Api/Results` carries the choice on a set and in its status, at contract 1.89. `Agent/ServiceCommandHandler` keeps it where an edit says nothing of it.
- The clients: `Cli/CliApplication` prints `online-only:left-to-provider` on the set's status line. Direct mode's backup, in `Cli/OperationGateway`, says how many it left.
- The tests:
  - `Repository.Tests/OnlineOnlyCaptureTests` — a run that leaves them fetches none, leaves out the new and the changed, keeps the unchanged, and fails nothing; one only moved is taken without a fetch.
  - `Repository.Tests/ChangedFilesCountTests` — for a set that leaves them, none it will leave is counted, and none is to fetch.
  - `Application.Tests/ClientConfigurationTests` — the choice round-trips under its name and is not written where unset.
  - `Hosts.Tests/LeaveOnlineOnlyTests` — through the service: the backup fetches none, completes, and says how many it left; the set's status says it leaves them, and one that fetches them says nothing; a set that leaves them is told what changed, as any other is; its changes waiting count none it will leave; a set that stops leaving them walks in full and takes what it left, though the journal names nothing; the places kept say whether it left any, and older ones say not; the command line's status prints the token.
  - `Hosts.Tests/ConfigurationCommandTests` — an edit that says nothing of it keeps it, false lets it go, and the listing says it.
  - `Cli.Tests/BackupNotFetchedDirectTests` — direct mode's backup of a set that leaves them fetches none, says how many it left, and exits zero; one that fetches them says nothing of it.
  - `Cli.Tests/StatusLeavesOnlineOnlyTokenTests` — the token, and nothing for a set that fetches them.
  - `Api.Tests/ContractAdditiveFieldsTests` — the wire names at 1.89 and an older service's silence. `ConfigurationContractTests` pins 1.89.

---

## Context

An online-only file is one its cloud provider keeps off the device until it is asked for. A backup fetches it only where it is new or changed (ADR-0185). Where the provider does not deliver one, the run fetches no other, each changed file it passed keeps the version backed up before (ADR-0185 Amendment 3), and a backup whose every failure kept its version before is behind, not partial (ADR-0193 Amendment 1).

Some people keep files with a cloud provider on purpose, and do not want this machine fetching them:

- a library larger than the disk;
- a metered link;
- a provider that will not serve the account the service runs as.

For them every backup asks, waits, and reports what it could not fetch.

The owner asked whether such a file should be recorded as skipped. As a default, no. The cloud copy is not a backup: a deletion, an overwrite or ransomware syncs to it, and a skip says nothing needs attention. The owner chose to make it a choice of the set, said where it is made and where it holds.

## Decision

### 1. The set says so

A set may leave its online-only files to their cloud provider: `leave_online_only` on the set in the configuration file.

- **It is off by default.** A set that says nothing fetches them, as every set before it did.
- **It is written only where it is chosen.** A file written before it existed reads as a set that fetches them.

### 2. Its backups fetch none

A backup of such a set takes each online-only file as follows:

- **An unchanged one** keeps the version already backed up, as ever. It is never fetched.
- **One only moved, or changed only in its metadata,** is taken from the version before, without a fetch, as ever.
- **One new, or whose content changed,** is left out of the snapshot.
  - It is not a failure. The snapshot's error manifest does not name it, and the backup completes.
  - Its version before, where there is one, stays in the snapshots before this one until retention lets them go.

A file on the device is backed up as ever, whatever the set says.

### 3. A backup says how many it left

- **The service's backup** goes on in its detail: `; 2 online-only file(s) left to their cloud provider`. It says nothing where it left none.
- **Direct mode's backup** of a named set prints `left           2 online-only file(s) to their cloud provider, as the set asks`.

### 4. The changes waiting count none it will leave

A set's changes waiting are judged as its next backup will judge them (ADR-0192). For a set that leaves them, a file the backup will leave is not counted, and none is to fetch.

### 5. A set that stops leaving them walks in full

A backup told which folders changed walks only those (ADR-0188). A file left earlier, and not changed since, is in no folder the journal names. A set that stops leaving its online-only files would not take it until its full walk fell due.

So the service keeps, with the journal places, whether the set's last snapshot may lack a file it left:

- a backup that left any says so;
- one that walked only what the journal named carries on what the last said;
- one that walked in full and left none says not.

The next backup of a set that no longer leaves them, where the last says it may lack one, walks in full: `it left online-only files to their provider, and no longer does`. A record kept before it said so says not.

### 6. The wire and the clients

- **Contract 1.89:**
  - `backup_set` gains `leave_online_only`. An upsert that sends none keeps what the set has; true or false sets it. A listing says true or false.
  - A set's status gains `leaves_online_only`, true where the set leaves them.
  - Additive. A service before 1.89 fetches them, and its status says nothing of it.
- **The command line:** a set's status line prints `online-only:left-to-provider`.

## Consequences

- **A set that chose this backs up quietly.** Its backups complete, fail nothing for those files, and say how many they left. Its status says it leaves them.
- **Those files are kept only by their provider.** A file left that is new to the set is in no backup. The status says the set leaves them, so a person reading it is not told everything is protected.
- **A changed file is left out, not kept at the version before.** The latest snapshot holds a file only as it can say it is now. An earlier snapshot still holds the version before, until retention lets it go.
- **A backup's progress counts a file left as it counts a failed one:** its bytes where it ended, and not as a file backed up.

## What this does not do

- **Count how many files are left in all.** A backup told which folders changed sees only those, so what it left is what it left there, not all that are left. The status says the set leaves them, rather than give a count that would say less than is left.
- **Keep the version before of a changed file in the snapshot.** That is ADR-0185 Amendment 3's answer to a provider that fails, and it records a failure so that a restore can name the file (ADR-0194). A set that chose to leave a file has nothing failed to record.
- **Leave a file on the device.** A file its provider has put on the device is read like any other.
- **Offer the choice in the console.** The console's set editor and card are a later record's.

## Alternatives considered

- **Record a file left as skipped in the error manifest.** The error manifest says what the snapshot failed to capture. A skip by choice is not a failure, and recording it there would make every backup partial or behind again.
- **Keep the version before in the snapshot.** See above: it needs a failure for a restore to name it.
- **Give the status a count.** See above: a count from a backup told which folders changed says less than is left.
- **Walk in full when an edit turns the choice off.** A configuration file edited by hand would not do it. The places kept with each backup see every backup, however the set came to change.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first, at contract 1.89. |

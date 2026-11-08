# ADR-0193 — A set's status says which online-only files its last backup could not fetch

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-014 (new: a set's status says how many online-only files its last backup could not fetch, how many of them keep the version backed up before, the file whose fetch failed and why, and since when backups have been missing some); FR-SNP-014 (amended by [Amendment 1](#amendment-1-2026-11--a-backup-that-kept-every-earlier-version-is-behind-not-partial): a backup whose every failure is an online-only file that kept its version before completes, behind, and the set's status raises no PARTIAL warning for it); FR-SNP-014 (amended by [Amendment 2](#amendment-2-2026-11--the-hover-says-what-else-a-set-may-do): the console's hover of the line also says that the set's Other settings can leave online-only files to their cloud provider instead, and what that costs); FR-SNP-014 (amended by [Amendment 3](#amendment-3-2026-11--a-folder-not-listed-is-counted-and-a-backup-that-kept-it-is-behind): a set's status counts the folders its last backup could not list, how many of them keep what was backed up before, and names whichever of a file or a folder the provider failed first; a backup whose every failure kept what was backed up before, file or folder, is behind)
**Related:** [ADR-0185](0185-a-run-stops-fetching-online-only-files-after-the-first-that-does-not-come.md) (a run stops fetching online-only files after the first that does not come, and its Amendment 3: a file not fetched keeps its version before), [ADR-0192](0192-a-sets-status-says-how-many-changed-files-its-next-backup-will-take.md) (a set's status says how many changed files its next backup will take), [architecture 06 §1](../architecture/06-filesystem-capture.md#1-scanner) (an unreadable file is an entry in the error manifest, not a failed backup)

**Built:**
- The run: `Repository/SnapshotPublication` counts the online-only files it did not fetch, and how many kept the version before, with the file whose fetch failed and why. It says whether every failure kept the version before: behind (Amendment 1). It counts the folders it could not list too, and how many kept their tree, and names whichever failed first (Amendment 3).
- The record: `Agent/NotFetchedStore` keeps what each set's last backup did not fetch, with since when backups have been missing some; `Agent/BackupRunner` keeps it once the backup has published. It names that backup's snapshot and whether it was behind, and a backup behind completes rather than completing with failures (Amendment 1).
- The status: `Application/StatusModel` raises no PARTIAL warning for a latest snapshot behind (Amendment 1).
- Direct mode: `Cli/OperationGateway` says a backup behind is behind, and it exits zero (Amendment 1).
- The seam: `Agent/ServiceRuntime` gives a test harness the source every backup reads through.
- The wire: a set's status in `Api/Results` carries it, at contract 1.84, and the folders not listed at 1.90 (Amendment 3).
- The clients: `Cli/CliApplication` prints a set's `not-fetched:` token; the console's card says it at a glance and in words, and on hover what else a set may do (Amendment 2). `not-listed:` beside it counts the folders, and the card says them too (Amendment 3).
- The tests:
  - `Repository.Tests/OnlineOnlyCaptureTests` — a run says how many it did not fetch, how many of them kept the version before, and the file that failed and why; one that fetched every one says nothing. A run whose every failure kept the version before is behind, and one that failed nothing, failed a file on the device, or missed a file new to the set is not (Amendment 1). A run whose only failure is a folder that kept its tree is behind and names the folder; one with a folder that had no tree to keep is not; one that kept a file and a folder counts both and names the first to fail (Amendment 3).
  - `Repository.Tests/ApplicationServiceTests` — a latest snapshot behind raises no PARTIAL warning (Amendment 1).
  - `Hosts.Tests/NotFetchedStatusTests` — through a stand-in source wrapping the real one: the set's status says it, since the first backup that missed some, kept while each after it misses some, and cleared by one that fetches them all; the command line's status, asked of the running service, prints it on the set's line. A backup behind completes, says on how many in its detail, and leaves the set without a PARTIAL warning; one that missed a new file still completes with failures and warns; the record says behind only of its own snapshot, and an older record says not (Amendment 1). A backup whose only failure is a folder kept completes behind, the set warns no PARTIAL, and its status counts and names the folder; the record keeps the folders, and one kept before says nothing of them (Amendment 3).
  - `Cli.Tests/BackupNotFetchedDirectTests` — direct mode's backup behind says so and exits zero; one that missed a new file is partial and exits non-zero (Amendment 1). One whose only failure is a folder kept says it is behind on that folder (Amendment 3).
  - `Api.Tests/ContractAdditiveFieldsTests`, `Web.Tests/StatusRelayNamesTests` — the wire names at 1.84, the console's names, and an older service's silence; and the folders' at 1.90 (Amendment 3).
  - `Cli.Tests/StatusNotFetchedTokenTests` — `not-fetched:3`, and nothing where every one was fetched; `not-listed:2`, with no `not-fetched:` where only folders were missed (Amendment 3).
  - `Web.DomTests/NotFetchedDomTests` — the card's count at a glance and in words, each kept, none kept, the file and the provider's words on hover, and nothing where every one was fetched. The hover also says that the set's Other settings can leave online-only files to their provider instead, and what that costs (Amendment 2). Folders not listed are counted at a glance and in words, and the folder whose listing failed is named on hover (Amendment 3).

---

## Context

An online-only file is one its cloud provider keeps off the device until it is asked for. A backup fetches it only where it is new or changed. Where the provider does not deliver one, the run fetches no other (ADR-0185), and each changed file it passed keeps the version backed up before (ADR-0185 Amendment 3).

All of that is said in the snapshot's error manifest, one entry per file. Nothing a person looks at says how many there are, whether any is in no backup at all, or how long backups have been missing them. A provider signed out for a week looks, from the console, like a week of backups that finished with some failures.

## Decision

### 1. The run counts what it did not fetch

A run counts, as it passes them:

- **the online-only files it did not fetch:** the one whose fetch failed, and each it then did not ask for;
- **how many of them kept the version backed up before;**
- **the file whose fetch failed, and the provider's words.**

A run that fetched every one it asked for says nothing. A file gone or refused is the file's own state, not the provider's, and is not counted (ADR-0185 §3).

### 2. The service keeps it for the set, since the first that missed some

Once the backup has published, the service keeps what it did not fetch, one record per set in its state directory:

- **The time is the first backup that missed some.** It is kept while each backup after it misses some, whichever files they are.
- **A backup that fetches every one lets the record go.**
- **A backup that does not publish changes nothing.** Its snapshot does not exist, and the last one's record still holds.
- **A record not kept costs the status that word, nothing more.** It is logged.

### 3. The wire and the clients

- **Contract 1.84:** a set's status carries `not_fetched`: `files`, `kept_earlier`, `since`, `path` and `reason`. Additive. A service before 1.84 says nothing.
- **The command line:** a set's status line prints `not-fetched:3`.
- **The console:** a set's card says `3 not downloaded` at a glance. Opened, it says `3 online-only files were not downloaded by the last backup: 2 keep the version backed up before, 1 is not in it at all · first missed 3 h ago`. On hover it names the file whose download failed and the provider's words.

> **2026-11 ([Amendment 2](#amendment-2-2026-11--the-hover-says-what-else-a-set-may-do)).** The hover then says that the set's Other settings can leave online-only files to their cloud provider instead, and what that costs.

The console says *downloaded*, the word a person uses for a cloud provider's files. The records say *fetched*.

## Consequences

- **A person sees a provider that is not delivering,** how long it has been, and whether anything is in no backup at all, without opening a snapshot's failures.
- **A file not kept is named as such.** It is new to the set, or the backup was asked to walk in full and kept nothing earlier. Older backups may still hold it. The console says it is not in *that* backup, not that it is in none.
- **The count is the last backup's.** A file fetched since, or a provider that answers again, is not seen until the next backup runs.

## What this does not do

- **Change the set's protection state.** A backup that missed some still completes with failures, as before, and the status derives from that as it did.

  > **2026-11: revised ([Amendment 1](#amendment-1-2026-11--a-backup-that-kept-every-earlier-version-is-behind-not-partial)).** A backup whose every failure kept the version backed up before completes, behind, and the set's status raises no PARTIAL warning for it. The protection state still derives as it did.
- **Name every file.** The snapshot's failures name each one.
- **Say it of a file on the device that will not read.** That is not the provider's.
- **Fetch in the person's own session** (ADR-0185).

## Alternatives considered

- **Read it from the snapshot's error manifest.** The manifest is in the archive's blobs, not the catalogue, and a status is asked every five seconds. And it does not say since when.
- **Make it a warning on the set's status.** A warning moves the set's glance word and asks for an action. The action is the provider's, often only to sign in again, and the person may already know. A line that says what and since when is enough.

## Amendment 1 (2026-11) — a backup that kept every earlier version is behind, not partial

**Requirements:** FR-SNP-014 (amended: a backup whose every failure is an online-only file that kept its version before completes, behind, and the set's status raises no PARTIAL warning for it)

### Context

A backup that cannot fetch a changed online-only file keeps the version backed up before (ADR-0185 Amendment 3), records the failure, and the set's status says so (§3). The backup still completed with failures, and the status warned that the latest snapshot was PARTIAL. A provider away for a week made every backup look failed, though each held a version of every file it named.

Three cases were treated as one:

- a changed file the backup could not fetch, whose version before it kept;
- a file new to the set it could not fetch, which is in no backup at all;
- a file on the device that would not read, which is no provider's.

The owner chose to grade them by what is at risk.

### Decision

- **A backup whose every failure kept the version backed up before is behind.** It holds a version of every file it names, some older than the file is, and misses none.
  - Each such file records one failure, so a run is behind exactly when as many files kept their version before as it recorded failures.
  - A run that failed nothing is complete, not behind.
- **A backup behind completes.** It does not complete with failures. Its detail goes on: `; behind: 2 online-only file(s) not downloaded keep the version backed up before`.
- **The set's status raises no PARTIAL warning for a latest snapshot behind.** It still says what it is behind on, and since when (§3).
  - The service's record of what the last backup did not fetch names that backup's snapshot and whether it was behind.
  - A later snapshot, as another writer's, is not said to be behind. Nor does a record kept before it named one.
- **Direct mode's backup** says `status behind — 1 online-only file(s) not downloaded keep the version backed up before`, and exits zero.
- **Anything else is as before.** A backup that missed a file new to the set, or failed a file on the device, completes with failures, warns PARTIAL, and in direct mode exits non-zero.

### Consequences

- A person sees a backup that is behind as one that ran, with a line saying what it is behind on, and keeps the warning for a backup that holds no copy of something.
- The snapshot still records its failures, so the next backup asks again (ADR-0186). Its capture status is still partial, since that is the format's: the snapshot listing calls it partial, and its failures name each file.

### What this does not do

- **Call it behind in a snapshot's listing.** A snapshot's capture status is the format's own, and says only that its error manifest names something.
- **Say it in direct mode's status.** Direct mode keeps no record of what its backups did not fetch, and its status warns as before.

## Amendment 2 (2026-11) — the hover says what else a set may do

**Requirements:** FR-SNP-014 (amended: the console's hover of the line also says that the set's Other settings can leave online-only files to their cloud provider instead, and what that costs)

### Context

The line says what a provider failed to deliver. The remedy is often the provider's: to sign in again, or to serve the account the service runs as. Where that will not happen, a set may leave its online-only files to their provider instead (ADR-0195), and the console offers that among a set's Other settings (ADR-0195 Amendment 1).

The owner asked that cloud-synced files in a set's sources be handled as another well-known backup tool handles them, where skipping them is an option a person can find. Here the option was in place, but a person reading why files were missed had nothing to point them to it.

### Decision

**The hover says what else a set may do, and what it costs.** After the file and the provider's words it says: `The set's Other settings can leave online-only files to their cloud provider instead: none is downloaded, and one new or changed there is kept only by its provider.` The line and the count are unchanged.

### Consequences

- A person who reads why files were missed reads, in the same place, the choice that stops the backups asking, and what it gives up.

### What this does not do

- **Make the choice from the line.** It says where the setting is, and the set's editor makes it.
- **Say it on the command line.** The `not-fetched:` token is for a script to read.

## Amendment 3 (2026-11) — a folder not listed is counted, and a backup that kept it is behind

**Requirements:** FR-SNP-014 (amended: a set's status counts the folders its last backup could not list, how many of them keep what was backed up before, and names whichever of a file or a folder the provider failed first; a backup whose every failure kept what was backed up before, file or folder, is behind)

### Context

A folder whose listing its provider keeps, and whose listing does not come, keeps the tree backed up before it ([ADR-0185 Amendment 6](0185-a-run-stops-fetching-online-only-files-after-the-first-that-does-not-come.md#amendment-6-2026-11--a-folder-its-provider-holds-keeps-its-tree-when-its-listing-does-not-come)). The run records a failure for it. Nothing else said so: a backup whose only failure was such a folder completed with failures, the set warned PARTIAL, and the status said nothing of the folder. The same backup with a file kept in the folder's place was behind (Amendment 1).

### Decision

- **The run counts the folders it could not list** with the files it did not fetch, and how many of them kept their tree. A folder with none to keep is empty in the snapshot.
- **It names what the provider failed first,** a file's fetch or a folder's listing, and says which.
- **A backup whose every failure kept what was backed up before is behind,** whether each is a file that kept its version or a folder that kept its tree. Its detail says on what: `behind: 1 online-only folder(s) not listed keep what was backed up before`, and where it kept both, the files first and then the folders. Direct mode's status line says the same.
- **The wire, at contract 1.90:** a set's `not_fetched` record gains `folders`, `folders_kept_earlier` and `listing`. A record of files alone carries none of them, and an older service names files alone.
- **The command line** prints `not-listed:N` beside `not-fetched:N`, and no `not-fetched:` token where only folders were missed.
- **The console's card** counts both at a glance, `2 not downloaded, 3 not listed`. Opened, it says how many folders keep what was backed up before and how many are empty in that backup. On hover it names the folder whose listing failed, where that came first. It does not say that nothing was asked for after it, since a folder's listing does not stop the run asking.

  > **2026-11: no longer ([ADR-0185 Amendment 7](0185-a-run-stops-fetching-online-only-files-after-the-first-that-does-not-come.md#amendment-7-2026-11--the-first-provider-failure-stops-the-run-listing-and-fetching)).** A folder's listing that fails stops the run asking, and the hover says that no other online-only file or folder was asked for after it.

### Consequences

- A provider that keeps away from a folder's listing makes each backup behind, not partial, while the folder keeps its tree, as it does for a file.
- A record kept before the folders were counted says nothing of them, and reads as files alone.

### What this does not do

- **Stop the run asking after a listing that fails.** The folders and the files after it are still asked for (ADR-0185 Amendment 6). A later record may have the first failure of either kind stop both.

  > **2026-11: done ([ADR-0185 Amendment 7](0185-a-run-stops-fetching-online-only-files-after-the-first-that-does-not-come.md#amendment-7-2026-11--the-first-provider-failure-stops-the-run-listing-and-fetching)).** The first failure of either kind stops the run fetching files and listing folders, and the record counts each folder passed over.
- **Name a folder's files in a restore's plan.** The plan names files whose failure is their own (ADR-0194).

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first, at contract 1.84. |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--a-backup-that-kept-every-earlier-version-is-behind-not-partial): a backup whose every failure is an online-only file that kept its version before completes, behind, saying on how many in its detail; the set's status raises no PARTIAL warning for it; direct mode says so and exits zero; anything else completes with failures as before. |
| 2026-11 | Amended | [Amendment 2](#amendment-2-2026-11--the-hover-says-what-else-a-set-may-do): the console's hover of the not-fetched line also says that the set's Other settings can leave online-only files to their cloud provider instead, and that then none is downloaded and one new or changed there is kept only by its provider. |
| 2026-11 | Amended | [Amendment 3](#amendment-3-2026-11--a-folder-not-listed-is-counted-and-a-backup-that-kept-it-is-behind): the run counts the folders its provider holds that it could not list, and how many kept their tree, and names whichever of a file or a folder the provider failed first; a backup whose every failure kept what was backed up before, file or folder, is behind, and says on what; contract 1.90 carries the folders on a set's not-fetched record; the command line prints `not-listed:`, and the console counts and names them. `Repository/SnapshotPublication`; `Agent/NotFetchedStore`; `Agent/BackupRunner`; `Api/Results`; `Cli/CliApplication`; `Cli/OperationGateway`; `Repository.Tests/OnlineOnlyCaptureTests`; `Hosts.Tests/NotFetchedStatusTests`; `Cli.Tests/StatusNotFetchedTokenTests`; `Cli.Tests/BackupNotFetchedDirectTests`; `Api.Tests/ContractAdditiveFieldsTests`; `Web.Tests/StatusRelayNamesTests`; `Web.DomTests/NotFetchedDomTests` |
| 2026-11 | Accepted | Noted by [ADR-0185 Amendment 7](0185-a-run-stops-fetching-online-only-files-after-the-first-that-does-not-come.md#amendment-7-2026-11--the-first-provider-failure-stops-the-run-listing-and-fetching): the first failure of either kind stops the run asking; the folders it passes over are counted with those not listed, and the console's hover for a folder whose listing failed first says no other online-only file or folder was asked for after it. |

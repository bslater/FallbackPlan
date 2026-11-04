# ADR-0194 — A restore's plan names the files an earlier version stands in for

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-RST-003 (amended: a restore's plan names each file it restores as a version backed up before, standing in for one its backup could not fetch, with what the backup said of it); FR-RST-004 (amended by [Amendment 1](#amendment-1-2026-11--the-run-and-its-receipt-name-them-too): a restore's receipt names each file it restored as a version backed up before, with what the backup said of it, and says where the snapshot's failures would not read)
**Related:** [ADR-0185](0185-a-run-stops-fetching-online-only-files-after-the-first-that-does-not-come.md) (a run stops fetching online-only files after the first that does not come, and its Amendment 3: a file not fetched keeps its version before), [ADR-0193](0193-a-sets-status-says-which-online-only-files-its-last-backup-could-not-fetch.md) (a set's status says which online-only files its last backup could not fetch), [ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md) (a restore plan names what is held only in an archival tier), [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning) (restore planning)

**Built:**
- The probe: `Restore/RestoreBlobSet` reads the snapshot's manifest and its failures, and names each file the plan restores that the failures also name. For a run, which does not probe, it reads them alone and stamps the run's receipt with them (Amendment 1).
- The figures: `Restore/RestorePlanFigures` says, among the plan's degradations, where those will not read.
- The wire: a plan in `Api/Results` carries them, at contract 1.85; the service's plan and direct mode's carry them alike. A restore's result says how many it restored so, and which, at 1.87 (Amendment 1).
- The receipt: `Restore/RestoreExecutor` keeps, at schema 12, what the backup said of each such file on its item, and whether the failures would not read (Amendment 1).
- The seam: `Cli/OperationGateway` lets a test wrap the source a direct backup reads through, as the service's harness wraps the service's.
- The clients: `Cli/RestorePlanReport` prints them under an earlier line; the console's plan step says how many, each, and why. `Cli/OperationGateway` prints how many a run restored so, and which, through the service and in direct mode alike, and the console's result step says the same (Amendment 1).
- The tests:
  - `Repository.Tests/OnlineOnlyCaptureTests` — the probe names the file an earlier version stands in for, with what the backup said of it; no file new to the set, none on the device that would not read, and none outside what the plan restores; none for a backup that fetched every one; and, with a byte of the snapshot's failures or of its manifest flipped beneath the store, none named and the plan unable to name them. A receipt's item is stamped with what the backup said, no other item is, the receipt is schema 12 and writes neither field unset, and with the failures flipped none is stamped and the receipt says it could not name them (Amendment 1).
  - `Repository.Tests/RestorePlanTests` — where the snapshot's failures will not read, the plan says it cannot name them.
  - `Hosts.Tests/EarlierVersionPlanTests` — the service's plan names it, through a stand-in source wrapping the real one. The service's run says how many it restored so and which, its kept receipt names the file and why, and the file restored is the version backed up before (Amendment 1).
  - `Api.Tests/ContractAdditiveFieldsTests` — the wire names at 1.85, and an older service's silence; a restore's result's at 1.87 (Amendment 1).
  - `Cli.Tests/GatewayRestoreReportTests` — `restore --plan` prints each, and is still a plan that may run. `restore` prints how many it restored so and each, still succeeds, and prints nothing where there are none (Amendment 1).
  - `Cli.Tests/RestoreReceiptDirectTests` — direct mode's run prints how many and which, and its kept receipt names the file and why, and no other (Amendment 1).
  - `Cli.Tests/RestorePlanCommandTests` — direct mode's plan of a snapshot that kept an earlier version names it and why, and exits zero.
  - `Web.DomTests/RestoreWizardDomTests` — the plan step says which and why, and the typed word alone arms the restore. The result step says how many and which, and that the receipt names each, and nothing where none was (Amendment 1).

---

## Context

A backup that cannot fetch a changed online-only file from its cloud provider keeps the version backed up before at its name, and records the failure, saying what stands in its place (ADR-0185 Amendment 3). The newest snapshot then still restores the file, and retention never lets the last copy go.

A restore of that snapshot gives the earlier version back, and says nothing. The amendment left that to a later record: the run's failures say it, but a person restoring does not read a backup's failures first.

## Decision

### 1. The plan names each such file, from the snapshot's failures

A file that fails capture is in no tree, but for one whose version before was put in its place. So a file the snapshot's failures name, and its tree also holds, is one an earlier version stands in for.

- **The plan's probe reads the snapshot's manifest and its failures,** beside the manifests it already reads for the run.
- **It names each file the plan restores that the failures name,** with what the backup said of it: why it was not fetched, and when the version restored was last modified.
- **It names only what the plan restores.** A plan of part of a snapshot names only the files in that part.
- **Where the snapshot's manifest or its failures will not read,** the plan says it cannot name them, among its degradations, rather than naming none.

### 2. Nothing is refused for them

They restore. What they restore is older than the file was when its backup ran. The plan says so; it holds nothing back, and the person decides.

### 3. The wire and the clients

- **Contract 1.85:** `restore_plan` gains `earlier_versions`, each a `path` and a `detail`. Additive. A service before 1.85 names none.
- **The command line:** `restore --plan` prints `earlier: 1 file(s) restore the version backed up before, …` and each file and why under it. It does not make the plan fail.
- **The console:** the plan step says `1 file(s) will be restored as the version backed up before`, with each file and why, and the typed word alone arms the restore.

## Consequences

- **A person restoring learns which files are older than they were,** and why, before anything is written.
- **The plan reads two more records:** the snapshot's manifest and its failures, from the metadata blobs it already opens.
- **A file the backup failed for any other reason is not named,** because it is in no tree and so in no plan.

## What this does not do

- **Say it in the restore's receipt.** The plan says it before the run; the receipt is unchanged.

  > **2026-11: done ([Amendment 1](#amendment-1-2026-11--the-run-and-its-receipt-name-them-too)).** The run says how many files it restored as the version backed up before, and its receipt names each with what the backup said of it.
- **Say it of a file restored from an older snapshot than the one that put it in place.** That snapshot's own failures are read, and only those.
- **Name, in a resume's plan, which files are already landed.** A resume's plan names them as any plan does.

## Alternatives considered

- **Mark the version in the catalogue.** The catalogue would need a new column and every older catalogue a rebuild, to say what one error manifest already says.
- **Read the detail for its words.** The words are for a person and may change. A failure at a path the tree also holds says it whatever the words.
- **Refuse the run unless acknowledged,** as one that would open a file wider is. Nothing is lost or exposed: the newest version that was ever backed up is restored.

## Amendment 1 (2026-11) — the run and its receipt name them too

**Requirements:** FR-RST-004 (amended: a restore's receipt names each file it restored as a version backed up before, with what the backup said of it, and says where the snapshot's failures would not read)

### Context

The plan names each file it restores as a version backed up before, and the person decides before anything is written. The run that follows said nothing of them, and nor did its receipt. The receipt is what outlives the run: a person reads it back, through `restore-receipts` or the console, long after the plan has gone from the screen.

### Decision

- **The run reads the snapshot's manifest and its failures, as its plan did,** through the same store and other copies the run reads. It reads them before it writes anything, so a run stopped part-way still keeps a receipt that says so.
- **The receipt names each such file on its item,** as `earlier_version`, with what the backup said of it. Schema 12. An item whose version is its own carries nothing.
- **Where the snapshot's manifest or its failures will not read,** no item is named, and the receipt says `earlier_versions_unread`, as the plan said it could not name them. A store that will not answer means the same here; the run meets it file by file, as before.
- **Contract 1.87:** a restore's result gains `earlier_versions`, how many, and `earlier_versions_sample`, at most twenty `path — detail` lines. Additive. A service before 1.87 says none.
- **The command line** prints `1 file(s) were restored as the version backed up before, since their backup could not fetch the one on the device; the receipt names each:` and each under it, through the service and in direct mode alike.
- **The console's result step** says the same, with each file and why.

Nothing is refused for them, and the run's outcome does not change: they restored.

### Consequences

- **A receipt read back says which files are older than they were when their backup ran,** and why, as its plan did.
- **A run reads two records more,** the same two its plan read, before it writes.

### What this does not do

- **Count them in the listing of kept receipts.** `restore-receipts` and the console's list count what each run restored, skipped and failed; a receipt read whole names each earlier version.
- **Say it of a file restored from an older snapshot than the one that put it in place.** As in the plan, only the restored snapshot's own failures are read.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first, at contract 1.85. |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--the-run-and-its-receipt-name-them-too): a run reads the snapshot's failures as its plan did, before it writes; its receipt, at schema 12, names each file it restored as the version backed up before, with what the backup said, or says the failures would not read; its result says how many and which, at contract 1.87, and the command line and the console say so. |

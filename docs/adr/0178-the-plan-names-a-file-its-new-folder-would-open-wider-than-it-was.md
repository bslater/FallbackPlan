# ADR-0178 — The plan names a file its new folder would open wider than it was

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended: the plan names the files whose security descriptor will not be written and whose folder would give an account access their own did not, and the accounts that gain; amended by [Amendment 1](#amendment-1-2026-10--a-run-that-would-open-a-file-wider-is-refused-unless-acknowledged): a run with such files is refused before anything is written unless it acknowledges wider access, and the plan counts them; amended by [Amendment 2](#amendment-2-2026-10--the-console-asks-for-the-acknowledgement): the console asks the person for that acknowledgement; amended by [Amendment 3](#amendment-3-2026-10--a-file-is-judged-by-the-folder-it-lands-in): a file is judged by the folder it lands in, through the folders the restore makes, and the folders its access comes from are named; amended by [Amendment 4](#amendment-4-2026-10--a-folder-the-restore-makes-is-judged-too): a folder the restore makes is judged too, counted apart, and its run refused alike)
**Related:** [ADR-0176](0176-a-restore-writes-back-a-windows-security-descriptor-whose-accounts-the-machine-knows.md) (a descriptor is written only where the machine knows every account it names, and is otherwise declared), [ADR-0083](0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md) (the plan and the receipt say what a target will not get back, and a run that will not fit is refused unless a person goes on), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (refuse is reserved for a descriptor that cannot be applied where the default would grant broader access)

**Built:**
- The reading: `Domain/WindowsSecurityDescriptor` reads each DACL entry that allows or denies one account, with its mask and flags, and says when the DACL is null. `Domain/WindowsAccess` reads the access a DACL gives each account, the access a file made in a folder takes from it, and which accounts gain. On Windows it reads that from the nearest folder that stands.
- The plan: `Domain/MetadataRules` gives each item's facts the access its captured DACL gave, through `Restore/RestoreBlobSet`. `Restore/RestorePlan` carries the call that reads a folder's inherited access. `Restore/RestoreMetadata` holds the two against each other for a file whose descriptor names an account the machine does not know, and maps a restore's slices to the folders their items land in. `Restore/RestorePlanFigures`, `Agent/ServiceCommandHandler` and `Cli/OperationGateway` pass those folders, so the service and direct mode say it alike.
- The tests:
  - `Domain.Tests/WindowsAccessTests` — on every platform, each allowing and denying entry is read from bytes with its mask and flags. The tests also hold:
    - the access a DACL gives, denies taken off, inherit-only entries apart and generic rights mapped;
    - a null DACL giving everyone everything, and no DACL saying nothing;
    - what a file inherits, the creator's entries apart;
    - which accounts gain, synchronising apart.

    On Windows a real folder's inherited access is read, through a path not yet made.
  - `Repository.Tests/RestoreMetadataHonestyTests` — of three files, only the one kept to an unknown account and the system is declared, naming the accounts that gain and the folder. Their folder, kept the same way, is not. A plan told no destinations, or whose folder will not read, says nothing. A restore's slices map their items to their own folders.
  - `Hosts.Tests/RestoreHonestyServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` — on Windows, the service and direct mode each plan the restore of a real file kept to an unknown account into a folder that shares with Users, and name it.
- The refusal ([Amendment 1](#amendment-1-2026-10--a-run-that-would-open-a-file-wider-is-refused-unless-acknowledged)): `Restore/RestoreMetadata` counts the files a folder would open wider, and for a run reads each one's manifest to count them, a blob at a time, only where a folder says what a file takes. `Agent/ServiceCommandHandler` and `Cli/OperationGateway` refuse such a run before anything is written unless it acknowledges wider access, and count the files in the plan. `Cli/CliApplication` takes `--acknowledge-wider-access` on `restore` and `restore-resume`, and `Cli/RestorePlanReport` fails a plan that counts any. The tests:
  - `Repository.Tests/RestoreMetadataHonestyTests` — the count says what the plan's line says; a run reads each manifest to say the same, within NFR-PERF-009's budget of a request a blob, and reads nothing where no folder says what a file takes.
  - `Hosts.Tests/RestoreHonestyServiceTests`, `Hosts.Tests/RestoreUnfinishedServiceTests`, `Cli.Tests/RestoreHonestyCommandTests` — on Windows, the service, a resume and direct mode each refuse a run of a real file kept to an unknown account into a folder that shares with Users, writing nothing, and run it when acknowledged.
  - `Cli.Tests/GatewayRestoreReportTests`, `Cli.Tests/RestoreResumeVerbTests`, `Api.Tests/ConfigurationContractTests` — the acknowledgement crosses only when told, under contract 1.80's names, and a plan that counts such files fails, saying how to go on.
- The console ([Amendment 2](#amendment-2-2026-10--the-console-asks-for-the-acknowledgement)): the restore wizard says how many files a plan counts, and arms the run only once the person ticks the acknowledgement, for a run, a kept plan's run and either resume. `Web.DomTests/RestoreWizardDomTests`, `Web.DomTests/RestoreUnfinishedDomTests` and `Web.DomTests/RestoreKeptPlanDomTests` hold each, and that a plan short of room and opening files wider needs both choices.

---

## Context

A restore on Windows writes a file's captured security descriptor back only where the machine knows every account it names ([ADR-0176](0176-a-restore-writes-back-a-windows-security-descriptor-whose-accounts-the-machine-knows.md)). A local account's SID names nobody on another machine, or on the same one rebuilt. So a file restored there is usually one whose descriptor is not written, and it takes the access of the folder it lands in. The plan said the descriptor would not be applied, and nothing about what the file would be open to instead.

Architecture 06 §3 reserves refusal for exactly this case: a file whose descriptor cannot be applied, landing where the default would grant broader access than the original. A file kept private on the machine that captured it, restored into a folder that shares with every user, is open to people it never was, and nothing on screen says so. Before a restore can refuse that, the plan has to know which files they are, and what "broader" means.

## Decision

### 1. Access is read from the DACL's own entries

The access a DACL gives an account is what its allowing entries grant the account, less what its denying entries take, read as a file's rights.
- An entry that governs only what is made in a folder (inherit-only) is not the entry's own access.
- Generic rights are read as Windows gives them to a file.
- A null DACL gives everyone everything.
- A descriptor captured with no DACL says nothing, and nothing is judged of it.
- Only entries that allow or deny one account are read. An object entry or a conditional one is passed over.

The reading is per account, not per person: a right an account holds through a group it is in is that group's.

### 2. A file made in a folder takes the folder's file-inheriting entries

What a file made in a folder takes is each entry the folder marks to pass to files, wherever the folder got it. The creator's entries are left out: they become the restoring account's, which already holds what it restores. The folder read is the nearest one that stands at or above where the file lands. A restore makes the folders below it, each taking what passes down, and writes their own descriptors only after their files have landed, without passing them down again.

> **2026-10 ([Amendment 3](#amendment-3-2026-10--a-file-is-judged-by-the-folder-it-lands-in)).** Built as said only now. Each file was held against the folder its slice writes into, not the one it lands in, and what the folder that stands gives was read as if every file were made in it. A file is now judged where it lands, and an entry marked not to propagate stops at the first folder the restore makes.

### 3. Wider is a right an account gains

A file's folder opens it wider where some account gets a right from the folder that the file's own DACL did not give that account. Synchronising alone is not counted, since every read grants it.

### 4. The plan names those files, the folders and the accounts

For a file whose descriptor names an account the machine does not know, the plan holds what its folder gives against what its own DACL gave. It declares those opened wider on a line of their own, `wider-access`, beside the line that already says their descriptors will not be applied. The new line names the folders and every account that gains. The plan finds each item's folder from the slices the service and direct mode already measure for room, and reads each folder once.

> **2026-10 ([Amendment 3](#amendment-3-2026-10--a-file-is-judged-by-the-folder-it-lands-in)).** Each item's folder is now its own, resolved under its slice's folder as the executor resolves it. The line names the folders the access comes from: for each file, the nearest that stands.

A file whose owner is refused still gets its DACL (ADR-0176 Amendment 1), so it is not judged. Nor is a file whose descriptor is written.

## Consequences

- A restore of a private file to a rebuilt machine, into a folder that shares with Users, says so before it runs, and names Users.
- A file whose captured DACL already gave what the folder gives is not named, though its descriptor is still not written.
- The refusal itself, and the acknowledgement that goes past it, are not built here. This record gives them something to refuse on.

  > **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--a-run-that-would-open-a-file-wider-is-refused-unless-acknowledged)).** Built: a run with such a file is refused before anything is written, unless it acknowledges wider access.

## What this does not do

- **Refuse.** The plan names the files. The run still restores them, as before. Making the run refuse unless a person acknowledges it is architecture 06 §3's other half.

  > **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--a-run-that-would-open-a-file-wider-is-refused-unless-acknowledged)).** The run refuses now, before anything is written, unless it acknowledges wider access.
- **Judge a folder.** A folder the restore makes takes container-inheriting entries, not file ones, and its own access is not compared.

  > **Built 2026-10 ([Amendment 4](#amendment-4-2026-10--a-folder-the-restore-makes-is-judged-too)).** A folder the restore makes is held against what passes to it as its own, counted apart from files, and its run refused alike.
- **Judge a descriptor that does not read.** With no DACL to compare, nothing is said beyond the line that already declares it.
- **Follow group membership.** An account that gains access through a group is counted only where the group itself gains.
- **Judge a target it cannot read.** Off Windows, or where the folder's DACL will not read, nothing is said.

## Alternatives considered

- **Name every file whose descriptor is not written.** Rejected. That is the existing line, and it says nothing about whether the file ends up more open. A private file restored into an equally private folder loses nothing anyone could see.
- **Judge by whether the captured DACL was protected.** Rejected. Protection says the file did not inherit, not that its folder would give more. A protected DACL that granted Users read loses nothing to a folder that grants Users read.
- **Re-read the access the file actually gets after the run.** Rejected for the plan. The point is to say it before anything moves. A receipt that reads back what landed is a separate check, not this one.

## Amendment 1 (2026-10) — a run that would open a file wider is refused unless acknowledged

### Context

The plan names the files a restore would open wider (§4), and the run restored them all the same. Architecture 06 §3 reserves refusal for exactly these files, and asks for explicit acknowledgement. A person who ran without reading the plan, or through a client that never showed it, got a file open to accounts it never was. Nothing said so but a line they may not have seen.

### Decision

1. **A run judges what its plan judges.** Before anything is written, a run holds each file against the folder it lands in, by §§1–4. A run is asked without its plan's facts, so it reads each file's manifest for this.
   - It reads them a wave at a time, so a blob that holds many is read once.
   - It reads only a file whose folder says what a file made there takes. Off Windows no folder says, and nothing more is read.
   - A manifest that will not read is passed over. The run fails that file, and opens nothing wider.
2. **Such a run is refused.** A run with any such file is refused before anything is written, and leaves no run to resume. The refusal names the files, the folders and the accounts that gain, in the plan's words. It is judged before the run's room is measured.
3. **A person may go on.** A run that acknowledges wider access runs as before, and reads nothing more.
   - The service takes `acknowledge_wider_access` on `run_restore` and `resume_restore` (contract 1.80).
   - The command line takes `--acknowledge-wider-access` on `restore` and `restore-resume`. Direct mode refuses and goes on the same way.
   - A resume asks again. The run's acknowledgement is not kept with it.
4. **The plan counts them.** The plan says how many such files there are, as `opened_wider` (contract 1.80), so a client can ask for the acknowledgement its run needs. The command line's plan exits non-zero where there are any, as it does where the run would not fit.

### Consequences

- A restore of a private file into a folder that shares with Users stops before it writes anything, naming Users, through the service and direct mode alike.
- On Windows a run reads each file's manifest once more before it writes, a blob at a time. A run that acknowledges wider access reads nothing more, and off Windows no run does.
- The run and its plan judge the same folder: the one the run is told to write to, read where it stands. A quarantined run's own folder, made below it, takes what that folder passes down.

  > **2026-10 ([Amendment 3](#amendment-3-2026-10--a-file-is-judged-by-the-folder-it-lands-in)).** Each file is now held against the folder it lands in below that one, through the folders the run makes.

### What this does not do

- **Ask in the console.** The console shows the plan's line, and sends no acknowledgement. A run it asks is refused, naming the files, until its wizard gives the person a way to go on. That is the next record's.

  > **Amended 2026-10 ([Amendment 2](#amendment-2-2026-10--the-console-asks-for-the-acknowledgement)).** Built: the console's wizard asks, and sends the acknowledgement the person gives.
- **Keep the acknowledgement.** A resume, and a later run of the same plan, ask for it again.
- **Judge more than §4 does.** A folder, a descriptor that does not read and a group's members are left as §4 leaves them.

  > **2026-10 ([Amendment 4](#amendment-4-2026-10--a-folder-the-restore-makes-is-judged-too)).** A folder is judged now, and a run is refused for one.

## Amendment 2 (2026-10) — the console asks for the acknowledgement

### Context

Amendment 1 had the service refuse a run that a folder would open a file wider in, unless the run acknowledges it, and gave the command line a flag to say so. The console sent no acknowledgement. A person restoring through it saw the plan's line and then a refusal, with no way to go on short of choosing another folder.

### Decision

The restore wizard asks, as it asks for room the plan says the run lacks.
- Where a plan counts files a folder would open wider (`opened_wider`), the wizard says how many, and that nothing will be written unless the person chooses to restore anyway.
- It offers a checkbox to go on. The run is armed only once it is ticked, beside the typed word where the run takes one.
- Where the plan is also short of room, both choices are needed, each its own.
- The run, a kept plan's run, a resume of a run left unfinished and a resume a result offers each send the person's choice. A resume asks again, since the run's acknowledgement is not kept with it.
- A resume whose plan could not be made offers the checkbox unasked, as it offers to go on for room, since its run may still be refused.

### Consequences

- A person restoring through the console can go on past the refusal, having been told what it is for, and nothing is sent that they did not choose.
- A kept plan's run is held to the plan made now. Files a folder would open wider are asked about whatever the kept plan said, since the run is refused for them either way.

### What this does not do

- **Name the accounts on the checkbox.** The plan's line above it names them. The kept plan's view shows its summary, not the lines.

## Amendment 3 (2026-10) — a file is judged by the folder it lands in

### Context

§2 has the folder read be the nearest one that stands at or above where a file lands, each folder the restore makes taking what passes down. What was built read the folder a slice writes into, for every file in it. A file restored in place, into a folder below that one that stands with access of its own, was judged by the root's access instead. A file landing in a folder that shares more than the root was not named, and the run that opened it wider was not refused. A file landing in a folder that keeps to fewer accounts could be named, and its run refused, for access it would not get. And what passes down was read as though every file were made in the folder that stands. An entry marked not to propagate, which Windows gives that folder's own files and no others, was counted for files below folders the restore makes.

### Decision

- **Each item's folder is its own.** `RestoreMetadata.DestinationsOf` resolves each item's path under its slice's folder by the rule the executor writes by, and gives the folder it lands in. A path the executor would refuse lands nowhere, and is not judged.
- **What passes down is read as Windows passes it.** `WindowsAccess.InheritedByAFile` takes how many folders the restore makes between the folder that stands and the file.
  - Each made folder takes an entry that passes to folders as its own, and passes it on.
  - It keeps an entry that passes to files alone for the files below it.
  - An entry marked not to propagate stops at the first.
  - A creator's entry still gives a file nothing.
- **The folder named is the one the access comes from.** `WindowsAccess.InheritedByAFileAt` gives the access with the folder it read (`InheritedAccess`). The `wider-access` line and the refusal name those folders, each once, not every folder the files land in. A restore of a whole tree into a new place names the folder that stands above it.
- **The plan, a run and a resume judge alike**, since each maps its slices through `DestinationsOf`.

### Consequences

- A file restored in place into a folder that stands is judged by that folder's own access. One a shared folder below the root would open wider is named, and its run refused unless acknowledged. One a private folder would not open wider is not.
- Each folder a run's files land in is asked about once. A run into many folders reads the folder that stands above them once for each.

### What this does not do

- **Judge a folder.** A folder the restore makes is still not held against its own captured access, as §4 leaves it.

  > **Built 2026-10 ([Amendment 4](#amendment-4-2026-10--a-folder-the-restore-makes-is-judged-too)).** It is now.
- **Read a folder that stands once a run.** It is read once for each folder a file lands in below it.

**Built:** `Domain/WindowsAccess`; `Restore/RestoreMetadata`; `Restore/RestorePlan`:

- `Domain.Tests/WindowsAccessTests`:
  - on every platform, a file made below folders a restore makes takes what passes down them: an entry that passes to folders and files reaches it, and so does one that passes to files alone. One that passes to folders alone does not, nor one marked not to propagate, nor a creator's;
  - on Windows, the access comes with the folder it was read from. An entry marked not to propagate reaches a file made in that folder, and not one made below a folder the restore makes. A folder that stands below another is the one read.
- `Repository.Tests/RestoreMetadataHonestyTests`, on every platform:
  - each item lands in its own folder under its slice's, and one whose path would be refused lands nowhere;
  - of two files kept to an unknown account, the one whose access comes from a folder that shares is named, with that folder. The one landing in a folder that keeps to the system is not.

The Windows cases run on CI's Windows job; elsewhere they are reported as skipped.

## Amendment 4 (2026-10) — a folder the restore makes is judged too

### Context

Architecture 06 §3 reserves refusal for a descriptor that cannot be applied where what the item takes instead would grant broader access. §4 judged files alone. A folder kept to an account the machine does not know is made by the restore and takes what passes to it from the folder above, and its descriptor is not written. Restored into a folder that shares with Users, it was opened to Users, and listing its files was too, unnamed and unrefused.

### Decision

- **A folder takes what passes to folders.** `WindowsAccess.InheritedByAFolder` reads it as Windows passes it, through the folders the restore makes between the folder that stands and this one, as Amendment 3 reads a file's. An entry that passes to folders is the folder's own. One that passes to files alone is not, nor is a creator's.
- **Only a folder the restore makes is judged.** `WindowsAccess.InheritedByAFolderAt` is asked about the folder's own path. A folder that stands there keeps its own descriptor, as [ADR-0086](0086-a-restore-gives-a-folder-its-own-metadata-back-last.md) has it, so nothing is said of it. `InheritedFolderAccessAt` joins `Restore/RestorePlan`'s profile beside the file's call.
- **It is judged as a file is.** A folder whose descriptor names an account the machine does not know is held against what it takes, by §3. The `wider-access` line counts such folders apart from files, and names the folders their access comes from and the accounts that gain.
- **A run is refused for it alike.** A run reads each judged folder's own record, as it reads a file's manifest, a wave at a time. A run with such a folder is refused before anything is written unless it acknowledges wider access.
- **The plan counts them apart.** `restore_plan` gains `opened_wider_folders` (contract 1.81), and `opened_wider` still counts files alone. The command line's plan names both, and fails where either is counted.

### Consequences

- A folder kept to a stranger, restored into a folder that shares with Users, is named and stops the run, as a file would.
- A plan or a run that judges folders reads each made folder's place once more, and a run reads each judged folder's own record.

### What this does not do

- **Ask in the console.** The console's wizard asks for the acknowledgement where `opened_wider` counts files. A run with only such folders is refused through it, naming them, until it reads `opened_wider_folders` too.
- **Judge a folder that stands.** It keeps its own descriptor, so nothing it gives is in question.

**Built:** `Domain/WindowsAccess`; `Restore/RestoreMetadata`; `Restore/RestorePlan`; `Agent/ServiceCommandHandler`; `Cli/OperationGateway`; `Cli/RestorePlanReport`:

- `Domain.Tests/WindowsAccessTests`:
  - on every platform, a folder made takes as its own what passes to folders, and not what passes to files alone or a creator's. Below a made folder, an entry marked not to propagate is gone;
  - on Windows, a folder that stands is nothing to judge, and one made below reads the folder above it.
- `Repository.Tests/RestoreMetadataHonestyTests`, on every platform:
  - of two folders kept to an unknown account, the one whose own access gave less than what passes to it is counted apart from files, with the folder its access comes from. The line says what the count says. A folder that stands is not judged;
  - a run reads each judged folder's own record to say the same.
- `Api.Tests/ConfigurationContractTests`: `opened_wider_folders` crosses at 1.81, and an older service's plan says nothing of folders.
- `Cli.Tests/GatewayRestoreReportTests`: a plan that counts only such folders fails, naming them and how to go on.
- `Hosts.Tests/RestoreHonestyServiceTests`, on Windows: a real folder kept to an unknown account, restored into a folder that shares with Users, is counted. Its run is refused and writes nothing, then restores once acknowledged.
- `Cli.Tests/RestoreHonestyCommandTests`, on Windows: in direct mode the command line's plan counts such a folder apart from files, and fails.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first. The plan reads each captured DACL's access per account, and the access a file would take from the folder it lands in. For a file whose descriptor will not be written, it names those opened wider, the folders and the accounts that gain, through the service and direct mode alike. `Domain/WindowsAccess`; `Domain/WindowsSecurityDescriptor`; `Domain/MetadataRules`; `Restore/RestoreMetadata`; `Restore/RestorePlan`; `Restore/RestoreBlobSet`; `Domain.Tests/WindowsAccessTests`; `Repository.Tests/RestoreMetadataHonestyTests`; `Hosts.Tests/RestoreHonestyServiceTests`; `Cli.Tests/RestoreHonestyCommandTests` |
| 2026-10 | Amended | [Amendment 1](#amendment-1-2026-10--a-run-that-would-open-a-file-wider-is-refused-unless-acknowledged): a run that a folder would open a file wider in is refused before anything is written, naming the files, the folders and the accounts, unless it acknowledges wider access, through the service, direct mode and a resume. The run reads each file's manifest for it, a blob at a time, only where a folder says what a file takes, and the plan counts the files (contract 1.80). `Restore/RestoreMetadata`; `Agent/ServiceCommandHandler`; `Cli/OperationGateway`; `Cli/CliApplication`; `Cli/RestorePlanReport`; `Api/Commands`; `Api/Results`; `Api/ContractVersion`; `Repository.Tests/RestoreMetadataHonestyTests`; `Hosts.Tests/RestoreHonestyServiceTests`; `Hosts.Tests/RestoreUnfinishedServiceTests`; `Cli.Tests/RestoreHonestyCommandTests`; `Cli.Tests/GatewayRestoreReportTests`; `Cli.Tests/RestoreResumeVerbTests`; `Api.Tests/ConfigurationContractTests` |
| 2026-10 | Amended | [Amendment 2](#amendment-2-2026-10--the-console-asks-for-the-acknowledgement): the console's restore wizard says how many files a plan counts a folder would open wider, and arms the run only once the person ticks the acknowledgement, which a run, a kept plan's run and either resume send; a plan short of room as well needs both choices. `Web/wwwroot`; `Web.DomTests/RestoreWizardDomTests`; `Web.DomTests/RestoreUnfinishedDomTests`; `Web.DomTests/RestoreKeptPlanDomTests` |
| 2026-10 | Amended | [Amendment 3](#amendment-3-2026-10--a-file-is-judged-by-the-folder-it-lands-in): a file is judged by the folder it lands in, resolved under its slice's folder as the executor resolves it, and by what passes down to it through the folders the restore makes, an entry marked not to propagate stopping at the first; the plan's line and the refusal name the folders the access comes from. `Domain/WindowsAccess`; `Restore/RestoreMetadata`; `Domain.Tests/WindowsAccessTests`; `Repository.Tests/RestoreMetadataHonestyTests` |
| 2026-10 | Amended | [Amendment 4](#amendment-4-2026-10--a-folder-the-restore-makes-is-judged-too): a folder the restore makes, whose descriptor names an account the machine does not know, is held against what passes to it as its own, counted apart from files (`opened_wider_folders`, contract 1.81), and its run refused alike unless acknowledged; a folder that stands is not judged. `Domain/WindowsAccess`; `Restore/RestoreMetadata`; `Cli/RestorePlanReport`; `Domain.Tests/WindowsAccessTests`; `Repository.Tests/RestoreMetadataHonestyTests`; `Api.Tests/ConfigurationContractTests`; `Cli.Tests/GatewayRestoreReportTests`; `Hosts.Tests/RestoreHonestyServiceTests`; `Cli.Tests/RestoreHonestyCommandTests` |

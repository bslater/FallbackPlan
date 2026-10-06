# ADR-0083 — A restore says whether it fits, and what it will not write back

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-RST-003, FR-RST-004
**Related:** [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning) (the plan's free-space assessment, privileges and unpreservable metadata), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) ("reports every skipped or degraded attribute"), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (no silent drop), [ADR-0079](0079-sparse-restore.md) (a hole is not written), [ADR-0068](0068-the-catalogue-directed-restore-read.md) (what a run may read), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the plan, the slices, the wizard)

**Built:**
- The measure: `Restore/RestoreSpace`, asking the platform through a `RestoreSpaceProbe`. The service's probe is on `Agent/ServiceRuntime`, the CLI's in `Cli/OperationGateway`.
- The rule for metadata: `Restore/RestoreMetadata`. The plan probe, `Restore/RestoreBlobSet`, reports what each file writes and carries, and `Restore/RestoreExecutor` records receipt schema 6.
- The surfaces: `Agent/ServiceCommandHandler` plans and runs; `Api/Commands`, `Api/Results` and `Api/ContractVersion` carry contract 1.53; `Cli/CliApplication` takes `--ignore-free-space`; and the console's restore wizard shows the room and arms a short run only by choice.
- The tests:
  - `Repository.Tests/RestoreSpaceTests` — the measure
  - `Repository.Tests/RestoreMetadataHonestyTests` — the rule, the declarations and receipt schema 6
  - `Repository.Tests/RestoreBreadthTests` — the receipt pinned whole at schema 6
  - `Hosts.Tests/RestoreHonestyServiceTests` — the plan, the refusal, the override and the summary through the service
  - `Api.Tests/ConfigurationContractTests` — contract 1.53's wire names
  - `Cli.Tests/RestoreHonestyCommandTests` — the direct restore's refusal, its override and its summary
  - `Web.DomTests/RestoreWizardDomTests` — the wizard

---

## Context

FR-RST-003 asks the plan to report free space and required privileges, and to report a space shortfall before any byte is written. FR-RST-004 asks the receipt to list every degraded attribute. Architecture 08 §2 lists the free-space assessment and the metadata a target cannot preserve among what a plan contains, and its Built line claimed §2 whole. Neither had been built.

**Space.** A plan reported one figure, the sum of its files' logical lengths. Nothing compared it with the disk the restore would write to. A restore that could not fit started anyway and failed file by file once the disk filled. A full disk does not only fail the restore: it fails whatever else on the machine needs to write, the service's own receipt among them. The figure was also wrong in both directions. It ignored that a sparse file restores sparse ([ADR-0079](0079-sparse-restore.md)), so a 98 MiB disk image holding 2 MiB of data counted as 98 MiB. And it ignored that the engine holds each file in the system's temporary directory until its whole-file hash verifies, so the largest file needs room there as well. Where that directory shares the volume, the file needs room twice.

**Metadata.** Capture records creation and access times, ownership by name, a Windows security descriptor, extended attributes, alternate streams and the platform's attribute bits. The executor writes back a file's modification time everywhere and its POSIX mode on a POSIX target, and nothing else. The plan declared POSIX metadata on a target that applies none, symlinks on a target that cannot create them, special files, and alternate streams. It said nothing of the rest. The receipt said "restored". Architecture 06 §3 allows three answers for each attribute on each target — preserve, degrade and report, or refuse — and rules out the fourth, a silent drop. Restores were dropping silently.

## Decision

### 1. What a run needs

A file needs the bytes restoring it writes. Those are its segments, because a hole is skipped rather than written, rounded up to whole 4 KiB clusters, the default unit of NTFS, APFS and ext4 alike. A directory the run creates needs a cluster. A symlink, a special file and anything else not materialised as a file need nothing measurable. The engine's working copy needs room for the largest file the run writes.

A run in place is credited only what its existing-file policy frees:

- **Overwrite** frees each file once its replacement lands, so the run needs its growth, plus the largest replacement in flight, because old and new coexist until the move.
- **Move aside** stays on the volume, and **keep both** keeps both, so neither frees anything.
- **Fail** writes nothing over a file that is there.

A quarantined run lands in a directory of its own, where nothing is in the way.

### 2. Where it needs it

The needs are summed per volume. Two folders on one volume share its free space, so the slices of an original-location restore are measured together, or each would fit and both would not. The engine's working directory joins the volume it shares, or is a need of its own when it shares none, labelled as working space. A directory not yet created is asked about at its nearest existing ancestor. The service asks volume identity through the probe placement uses ([ADR-0051](0051-local-destination-placement.md)), so the two agree on what shares a drive. It asks free space through the probe the destination floor uses, so one test seam answers for both.

A platform that will not say what is free is never short. The check exists to stop a disk filling, not to stop a restore because a platform would not answer — the posture the destination floor took first (FR-DEST-010).

### 3. The plan measures exactly; the run measures cheaply first

The plan's probe already decodes every file's manifest, to find the segments the store is missing. So it measures each file's written bytes exactly, at no extra cost. Told where the run would write, the plan answers the room each volume needs against what is free there. A shortfall is also a conflict line, where a client that predates the figure already looks. Without a folder named there is nothing to measure against, and it answers as before, plus what the files write.

The run measures from the catalogue's logical lengths, which cost nothing. Only when those say it is short does it read every manifest and measure again. A run that fits by logical length has its answer without a read, so the check costs nothing against NFR-PERF-009's budget unless the disk is nearly full. The whole run is measured before its first slice writes anything, including the folder it would create.

### 4. A run that will not fit is refused, and a person may go on

A short run is refused before anything is written. The refusal names the space needed, the space free and the folder, for each short volume. Through the service the refusal is a `Refused` error. Through the CLI's direct restore it is a failure that names `--ignore-free-space`. In the console's wizard the plan shows the room on each volume, and a plan that will not fit arms the restore only once the person ticks "restore anyway".

The override exists because the estimate errs high and some volumes hold more than they are asked to. A volume that compresses what it stores — ZFS and Btrfs commonly do, and NTFS can — may hold the restore comfortably. A restore is what a person runs on a bad day, and refusing one that would have fitted is its own harm. So the check refuses by default and gives way to a person who says so.

### 5. One rule for metadata, said twice

`RestoreMetadata` holds the executor's own rule: a file's modification time everywhere, and its permissions where the target applies POSIX metadata. Nothing else is written back yet. A symlink is created with none of its own metadata, because the platform calls that would set it follow the link. Alternate streams are never applied, whatever a profile claims, because the executor writes none.

The plan and the receipt both read that one rule, so they cannot disagree about what a target gets back:

- **The plan** declares each attribute the tree carries and the target will not get back, with how many files carry it, and only when some do — ownership, security descriptors, extended attributes, creation times, access times, file attributes, and a symlink's own metadata. For ownership it names what applying it needs: root, or CAP_CHOWN. It does not repeat what the planner already says: on a target that applies no POSIX metadata, the planner's line covers permissions and ownership.
- **The receipt**, at schema 6, names per landed item each captured attribute not applied, in the format's order and words: `created_at`, `owner` and so on. An item's outcome does not change for metadata alone. Its content landed and verified, so it is restored; what it lacks is said beside it, as the plan said it would be. Alternate streams stay the exception they were: a file missing a stream is not the captured file, so it is degraded.
- **The run's answer** summarises the receipt, one line an attribute with how many items it was left off.

## Consequences

- A restore that will not fit stops before it starts, and says how far short it is and where. One that fits costs what it cost.
- Every plan over real files now lists access times, because every platform captures them and no target applies them, and most list creation times and ownership. That is the truth, and it is the next slice's work: these are cheap to write back.
- The receipt gained a field, so its schema moved to 6, and the golden fixture moved with it. The fixture's file now carries only a modification time, which every target applies, so it stays one document on every platform.
- `plan_restore` takes the run's shape, so a client that wants the room measured sends what it will run. The console does. The CLI has no plan verb, and its restore measures itself.
- The CLI's direct restore gained a flow-scoped test seam, `DirectGateway.AvailableBytesInFlow`, the pattern of `FanOut.ReadBackBudget`.

## What this does not do

- It does not write back any attribute it did not before. Creation and access times, ownership where the principal resolves and the restore may set it, extended attributes, security descriptors and attribute bits are the next slice.

  > **2026-10 ([ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md)).** Access times are now written back everywhere, and creation times where the platform has a call that sets one. The receipt's list records what each write did rather than what §5's rule expected. Ownership and the rest remain owed.

  > **2026-10 ([ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md)).** Ownership is now written back where the names resolve and the restoring account may give them. The rest remains owed.

  > **2026-10 ([ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md)).** Extended attributes are now written back on Linux and macOS where the account and the volume allow them. Security descriptors and attribute bits remain owed.
- It does not recreate hard links. A hard-link group restores as separate files, as it did, and neither the plan nor the receipt says so yet.
- It does not refuse for a security descriptor that cannot be applied. Architecture 06 §3 reserves that refusal for a descriptor whose absence would grant broader access, and that needs a descriptor to apply first.
- It does not estimate the physical transfer size, which differs from the logical size when a store lacks range reads. It does not export a plan or resume one. And it does not report an object held only in an archival tier. All three are architecture 08 §2's, and all three stay owed.
- It does not measure the standalone recovery tool's restore, which has its own engine and writes where a person points it.
- It does not remove the engine's double write. Each file is written once into the working copy and once where it lands, which is why the working directory needs room for the largest file. A restore that verified into the file it lands would need no working space at all, and would read and write half as much. That is a change to the verified write path and is not made here.
- It does not account for filesystem metadata beyond a directory's cluster, nor for allocation units other than 4 KiB. The error is a cluster per file at most, and errs high.

## Alternatives considered

- **Refuse without an override.** Rejected: on a compressing volume the estimate refuses restores that fit, and a person on their worst day would have no way through but to free space they did not need to free.
- **Warn and never refuse.** Rejected: FR-RST-003's acceptance is a shortfall reported before any byte is written. A warning the person never sees, from a CLI or a script, leaves the disk to fill just as before.
- **Read every manifest before every run.** Rejected: it would cost a request a file on every restore to answer a question the catalogue answers for nearly all of them. The run reads manifests only when the cheap figure says it is short.
- **Move the engine's working copy onto the volume the restore writes to.** Rejected for this slice: one volume would mean one figure, but on a spinning disk every file would then be written, read back and written again on the same spindle. The working space is measured where it is instead.
- **Mark a file degraded when any captured attribute is not applied.** Rejected: nearly every restored file would be degraded, because access times alone are never applied, and the outcome would stop distinguishing a file whose content is not the captured file from one whose times are not.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first: the space measure per volume with the engine's working copy and the existing-file policy's credit, the plan's exact figure and the run's cheap one, the refusal and its override (contract 1.53), the metadata rule, the plan's counted declarations and receipt schema 6, through the service, the CLI and the console. |
| 2026-10 | Accepted | [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) writes back access times everywhere and creation times where the platform can set them. The receipt's not_applied list keeps receipt schema 6's shape but now records each write's outcome, not §5's rule. The rule still drives the plan. |
| 2026-10 | Accepted | [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) writes ownership back where the names resolve and the account may give them. §5's plan now names two reasons ownership will not land, privilege and a name that resolves to nothing, and a set-id bit it will drop. |
| 2026-10 | Accepted | [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) writes extended attributes back where the account and the volume allow them. §5's plan gains a line for each kind the rule withholds item by item: ACLs naming accounts by number elsewhere, every ACL on macOS, and the namespaces only root writes. |

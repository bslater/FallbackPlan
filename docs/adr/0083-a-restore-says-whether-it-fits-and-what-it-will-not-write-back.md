# ADR-0083 — A restore says whether it fits, and what it will not write back

**Status:** Accepted (amended 2026-10: [Amendment 1](#amendment-1-2026-10--an-empty-security-descriptor-is-no-descriptor), [Amendment 2](#amendment-2-2026-10--a-plan-says-what-its-run-reads))
**Date:** 2026-10
**Requirements:** FR-RST-003 (amended by [Amendment 2](#amendment-2-2026-10--a-plan-says-what-its-run-reads): a plan says what its run reads), FR-RST-004
**Related:** [architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning) (the plan's free-space assessment, privileges and unpreservable metadata), [architecture 08 §3](../architecture/08-restore-and-recovery.md#3-restore-verification) ("reports every skipped or degraded attribute"), [architecture 06 §3](../architecture/06-filesystem-capture.md#3-metadata-matrix) (no silent drop), [ADR-0079](0079-sparse-restore.md) (a hole is not written), [ADR-0068](0068-the-catalogue-directed-restore-read.md) (what a run may read), [ADR-0041](0041-guided-restore-and-peer-retrieval.md) (the plan, the slices, the wizard)

**Built:**
- The measure: `Restore/RestoreSpace`, asking the platform through a `RestoreSpaceProbe`. The service's probe is on `Agent/ServiceRuntime`, the CLI's in `Cli/OperationGateway`.
- The rule for metadata: `Restore/RestoreMetadata`. The plan probe, `Restore/RestoreBlobSet`, reports what each file writes and carries, and `Restore/RestoreExecutor` records receipt schema 6.
- The surfaces: `Agent/ServiceCommandHandler` plans and runs; `Api/Commands`, `Api/Results` and `Api/ContractVersion` carry contract 1.53; `Cli/CliApplication` takes `--ignore-free-space`; and the console's restore wizard shows the room and arms a short run only by choice.
- The read ([Amendment 2](#amendment-2-2026-10--a-plan-says-what-its-run-reads)): `Restore/RestoreBlobSet` adds up what the run will read, `Agent/ServiceCommandHandler` sends it as contract 1.64's `read_bytes`, and the console's restore wizard shows it beside what the run writes.
- The tests:
  - `Repository.Tests/RestoreSpaceTests` — the measure
  - `Repository.Tests/RestoreMetadataHonestyTests` — the rule, the declarations and receipt schema 6
  - `Repository.Tests/RestoreBreadthTests` — the receipt pinned whole at schema 6
  - `Hosts.Tests/RestoreHonestyServiceTests` — the plan, the refusal, the override and the summary through the service
  - `Api.Tests/ConfigurationContractTests` — contract 1.53's wire names
  - `Cli.Tests/RestoreHonestyCommandTests` — the direct restore's refusal, its override and its summary
  - `Web.DomTests/RestoreWizardDomTests` — the wizard, and what the run will read where the service says it
  - `Repository.Tests/RestoreReadTests` — what the run will read: exactly each record once as the reader sizes it and each blob's envelope once; less than it writes for content that compresses or repeats or keeps holes, a little more for content that does none of those, and nothing of what no copy holds; and for a whole tree read as a run reads it, no less than the run reads and not 1% more, or 0.3% for one file across many blobs
  - `Api.Tests/ContractAdditiveFieldsTests` — contract 1.64's wire name, and a pre-1.64 plan reading as not saying

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

> **2026-10 ([ADR-0131](0131-the-service-measures-a-restores-working-space-where-its-parts-are-kept.md)).** Through the service, the engine's working directory is the folder its runs keep their parts in, under the state directory ([ADR-0129](0129-a-restore-cut-short-resumes-within-a-file.md)), and the working space is measured there. Direct mode still measures the system's temporary folder, where its engine spools.

A platform that will not say what is free is never short. The check exists to stop a disk filling, not to stop a restore because a platform would not answer — the posture the destination floor took first (FR-DEST-010).

### 3. The plan measures exactly; the run measures cheaply first

The plan's probe already decodes every file's manifest, to find the segments the store is missing. So it measures each file's written bytes exactly, at no extra cost. Told where the run would write, the plan answers the room each volume needs against what is free there. A shortfall is also a conflict line, where a client that predates the figure already looks. Without a folder named there is nothing to measure against, and it answers as before, plus what the files write.

The run measures from the catalogue's logical lengths, which cost nothing. Only when those say it is short does it read every manifest and measure again. A run that fits by logical length has its answer without a read, so the check costs nothing against NFR-PERF-009's budget unless the disk is nearly full. The whole run is measured before its first slice writes anything, including the folder it would create.

### 4. A run that will not fit is refused, and a person may go on

A short run is refused before anything is written. The refusal names the space needed, the space free and the folder, for each short volume. Through the service the refusal is a `Refused` error. Through the CLI's direct restore it is a failure that names `--ignore-free-space`. In the console's wizard the plan shows the room on each volume, and a plan that will not fit arms the restore only once the person ticks "restore anyway".

The override exists because the estimate errs high and some volumes hold more than they are asked to. A volume that compresses what it stores — ZFS and Btrfs commonly do, and NTFS can — may hold the restore comfortably. A restore is what a person runs on a bad day, and refusing one that would have fitted is its own harm. So the check refuses by default and gives way to a person who says so.

### 5. One rule for metadata, said twice

`RestoreMetadata` holds the executor's own rule: a file's modification time everywhere, and its permissions where the target applies POSIX metadata. Nothing else is written back yet. A symlink is created with none of its own metadata, because the platform calls that would set it follow the link. Alternate streams are never applied, whatever a profile claims, because the executor writes none.

> **2026-10 ([ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md)).** A symlink the restore makes now gets its own metadata back by the rule a file does, each write by a call that changes the link itself and never what it points at. The plan counts links on the lines files and folders use, and its one line for every link's metadata is gone.

The plan and the receipt both read that one rule, so they cannot disagree about what a target gets back:

- **The plan** declares each attribute the tree carries and the target will not get back, with how many files carry it, and only when some do — ownership, security descriptors, extended attributes, creation times, access times, file attributes, and a symlink's own metadata. For ownership it names what applying it needs: root, or CAP_CHOWN. It does not repeat what the planner already says: on a target that applies no POSIX metadata, the planner's line covers permissions and ownership.
  > **2026-10 ([Amendment 1](#amendment-1-2026-10--an-empty-security-descriptor-is-no-descriptor)).** The security-descriptor line had been declared on every plan over files captured off Windows, which never had one. An empty descriptor is now read as none.
- **The receipt**, at schema 6 (schema 7 since [ADR-0102](0102-a-restore-gives-a-hard-link-group-back-as-one-file.md), which adds `linked_to`, and 8 since [ADR-0109](0109-a-restore-cut-short-is-resumed.md), which adds `found_in_place`), names per landed item each captured attribute not applied, in the format's order and words: `created_at`, `owner` and so on. An item's outcome does not change for metadata alone. Its content landed and verified, so it is restored; what it lacks is said beside it, as the plan said it would be. Alternate streams stay the exception they were: a file missing a stream is not the captured file, so it is degraded.
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

  > **2026-10 ([ADR-0175](0175-a-restore-writes-back-the-windows-attribute-bits-it-may.md)).** A file's and a folder's Windows attribute bits are now written back on Windows: read-only, hidden, system, archive and not-content-indexed, as the last write. A bit no restore sets, as compressed or encrypted, is named.

  > **2026-10 ([ADR-0176](0176-a-restore-writes-back-a-windows-security-descriptor-whose-accounts-the-machine-knows.md)).** A file's and a folder's Windows security descriptor is now written back on Windows, after the times and before the attribute bits, where the machine knows every account it names. One that names an account it does not know is named, and the file keeps its folder's access.

- It does not recreate hard links. A hard-link group restores as separate files, as it did, and neither the plan nor the receipt says so yet.

  > **2026-10 ([ADR-0102](0102-a-restore-gives-a-hard-link-group-back-as-one-file.md)).** Built: a group's later names are links to the file its first name landed as, where they agree with it, and a name restored as a file of its own says why. The plan measures a group once, and the receipt moves to schema 7 for `linked_to`.
- It does not refuse for a security descriptor that cannot be applied. Architecture 06 §3 reserves that refusal for a descriptor whose absence would grant broader access, and that needs a descriptor to apply first.

  > **2026-10 ([ADR-0176](0176-a-restore-writes-back-a-windows-security-descriptor-whose-accounts-the-machine-knows.md)).** A descriptor is now applied where the machine knows every account it names. One that is not is declared and degraded; the refusal is still not made.

  > **2026-10 ([ADR-0178](0178-the-plan-names-a-file-its-new-folder-would-open-wider-than-it-was.md)).** The plan now names the files whose descriptor is not written and whose folder would give an account access their own did not. The refusal is still not made.

- It does not estimate the physical transfer size, which differs from the logical size when a store lacks range reads. It does not export a plan or resume one. And it does not report an object held only in an archival tier. All three are architecture 08 §2's, and all three stay owed.

  > **Amended 2026-10 ([Amendment 2](#amendment-2-2026-10--a-plan-says-what-its-run-reads)).** The physical transfer size is estimated: a plan says what its run will read. Export, resume and archival-tier reporting stay owed.

  > **2026-10 ([ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md)).** An object held only in an archival tier is reported: the plan names what every copy holds archived, apart from what is missing. Export and resume stay owed.
- It does not measure the standalone recovery tool's restore, which has its own engine and writes where a person points it.
- It does not remove the engine's double write. Each file is written once into the working copy and once where it lands, which is why the working directory needs room for the largest file. A restore that verified into the file it lands would need no working space at all, and would read and write half as much. That is a change to the verified write path and is not made here.
- It does not account for filesystem metadata beyond a directory's cluster, nor for allocation units other than 4 KiB. The error is a cluster per file at most, and errs high.

## Alternatives considered

- **Refuse without an override.** Rejected: on a compressing volume the estimate refuses restores that fit, and a person on their worst day would have no way through but to free space they did not need to free.
- **Warn and never refuse.** Rejected: FR-RST-003's acceptance is a shortfall reported before any byte is written. A warning the person never sees, from a CLI or a script, leaves the disk to fill just as before.
- **Read every manifest before every run.** Rejected: it would cost a request a file on every restore to answer a question the catalogue answers for nearly all of them. The run reads manifests only when the cheap figure says it is short.
- **Move the engine's working copy onto the volume the restore writes to.** Rejected for this slice: one volume would mean one figure, but on a spinning disk every file would then be written, read back and written again on the same spindle. The working space is measured where it is instead.
- **Mark a file degraded when any captured attribute is not applied.** Rejected: nearly every restored file would be degraded, because access times alone are never applied, and the outcome would stop distinguishing a file whose content is not the captured file from one whose times are not.

## Amendment 1 (2026-10) — an empty security descriptor is no descriptor

A restore's receipt on Linux listed `security_descriptor` as not applied on every item, and its plan declared descriptors for files that never had one. The cause was capture. It typed the descriptor's conditional as a byte array, so where no descriptor was read, the null array became an empty value rather than none. Every entry captured off Windows, and every one captured on Windows with descriptor capture turned off, recorded key 7 of its metadata map as an empty byte string. [Specification 06 §4.1](../../specifications/repository-format/06-manifests.md#41-metadata-map) defines key 7 as a descriptor in self-relative form, and the smallest of those is twenty bytes. No test exercised a descriptor on any platform.

- **Capture records none.** Where no descriptor is read, an entry carries none, and a manifest written for it omits key 7. A descriptor Windows answers as empty is none too.
- **A restore reads an empty descriptor as none.** A manifest already written keeps its empty key until its file changes, so §5's rule counts a descriptor only when it holds bytes. The plan declares nothing for an empty one, and the receipt lists nothing.
- **The metadata digest does not tell none from empty.** The digest decides whether an unchanged file's manifest is reused verbatim (NFR-PERF-003), and every digest a catalogue holds was computed with the empty key. Told apart, every file captured off Windows would look changed once, and the next backup would fetch and rewrite its manifest. So none digests as empty, and the next backup reuses what it reused before. A real descriptor still changes the digest.

Two alternatives were weighed. Fixing capture alone was rejected for the rewrite it would cost every installation once. Treating empty as none only at restore was rejected because it would leave every new manifest carrying a key the specification does not allow. The manifest codec still reads an empty key 7, which every repository written so far holds.

## Amendment 2 (2026-10) — a plan says what its run reads

[Architecture 08 §2](../architecture/08-restore-and-recovery.md#2-restore-planning) asks a plan for an estimated logical and physical transfer size, and this record left the physical one owed. It described the two as differing when a store lacks range reads. No store the product admits lacks them now, because `Repository/StoreAdmission` refuses one by name. The two still differ, often greatly, for other reasons:
- a store holds content compressed;
- it holds content once, however many files repeat it;
- it holds nothing where a file keeps a hole;
- it holds every record sealed.

On a store that bills for what is read, the physical figure is what a restore costs, and the plan said only what the files write.

### 1 The figure is what the run asks the store for

The plan probe already resolves the location of every record a run needs, to name what no copy holds. It now adds up what the run will ask for:
- each record a copy holds, once: every item's manifest, and a file's segments and streams;
- each at the length the reader sizes a record's read by;
- the envelope of each blob they sit in, once.

A record no copy holds is not counted, because the run will not read it.

### 2 It crosses as `read_bytes` (contract 1.64)

The plan result gains `read_bytes` beside `write_bytes`. A service before 1.64 sends none, which a client reads as not said, never as nothing to read. The console's restore wizard shows the figure beside what the run writes, and only when the service says it.

### 3 What the figure is held to

A whole tree read the way a run reads it takes no more than the figure, and not 1% less. That is from the catalogue's locations, through the reader's coalescing. The suite's tree read 0.2% under it.

The reader sizes a record's read by the longest framing any container uses, which errs high by up to 80 bytes for each metadata record. The figure counts each envelope at the longest any blob has, which errs high by 80 bytes for a blob whose envelope carries no sealed key. A file spread across many blobs read 0.11% under the figure. The reader may also read further than the figure, in two ways:
- it bridges a short gap between two records it wants;
- it reads a record again whose run it evicted before using it.

A restore of part of a tree can meet the first, and a tree larger than the read budget the second. Neither is counted, so the figure is an estimate and is called one.

### What this amendment does not do

- **Say in the receipt what the run read.** The figure is the plan's, given before the person decides.
- **Count the plan's own reads.** The probe reads each manifest to find what it names, before anything is decided.
- **Export a plan, resume one, or report an object held only in an archival tier.** These stay owed, as architecture 08 §2's.

  > **2026-10 ([ADR-0107](0107-the-command-line-plans-a-restore-on-its-own.md)).** The command line now prints a plan on its own, through either path, and exits non-zero unless the run would work. A plan is still not exported to a file, and a restore is not resumed.

  > **2026-10 ([ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md)).** A plan is kept in a file with `restore --export`. A restore is still not resumed.

  > **2026-10 ([ADR-0109](0109-a-restore-cut-short-is-resumed.md)).** A restore cut short is resumed with `restore --resume`: what it landed is left as it stands and the rest restored, and the run is measured for what is left. Nothing this bullet left owed remains.

### Alternatives considered

- **Reproduce the reader's coalescing to predict its bridges.** Rejected. The waves a run reads in depend on the reader's budgets and its order, and a figure that copied them would go wrong whenever they changed. The bound is stated instead.
- **Count each blob a run touches whole.** Rejected. The reader reads ranges. For a tree restored in part, a whole-blob figure would overstate what is paid for by every stretch of a blob the run never asks for.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first: the space measure per volume with the engine's working copy and the existing-file policy's credit, the plan's exact figure and the run's cheap one, the refusal and its override (contract 1.53), the metadata rule, the plan's counted declarations and receipt schema 6, through the service, the CLI and the console. |
| 2026-10 | Accepted | [ADR-0084](0084-a-restore-writes-back-the-times-it-can-set.md) writes back access times everywhere and creation times where the platform can set them. The receipt's not_applied list keeps receipt schema 6's shape but now records each write's outcome, not §5's rule. The rule still drives the plan. |
| 2026-10 | Accepted | [ADR-0085](0085-a-restore-gives-a-file-back-to-its-owner-where-it-may.md) writes ownership back where the names resolve and the account may give them. §5's plan now names two reasons ownership will not land, privilege and a name that resolves to nothing, and a set-id bit it will drop. |
| 2026-10 | Accepted | [ADR-0087](0087-a-restore-writes-back-the-extended-attributes-it-may.md) writes extended attributes back where the account and the volume allow them. §5's plan gains a line for each kind the rule withholds item by item: ACLs naming accounts by number elsewhere, every ACL on macOS, and the namespaces only root writes. |
| 2026-10 | Accepted (amended) | [Amendment 1](#amendment-1-2026-10--an-empty-security-descriptor-is-no-descriptor): an empty security descriptor, which capture had recorded for every entry it read none for, is no descriptor. Capture records none, a restore reads an empty one as none, and the metadata digest does not tell them apart, so no manifest is rewritten for it. |
| 2026-10 | Accepted | [ADR-0101](0101-a-restore-gives-a-symlink-its-own-metadata-back.md) gives a symlink its own metadata back, onto the link itself. §5's plan counts links beside files and folders, and loses its one line for every link. |
| 2026-10 | Accepted | [ADR-0102](0102-a-restore-gives-a-hard-link-group-back-as-one-file.md) gives a hard-link group back as one file. §5's receipt moves to schema 7 for `linked_to`, and §2's measure counts a group once. |
| 2026-10 | Accepted (amended) | [Amendment 2](#amendment-2-2026-10--a-plan-says-what-its-run-reads): a plan says what its run will read, each record a copy holds once as the reader sizes it and each blob's envelope once, as contract 1.64's `read_bytes`, shown by the console's wizard. A whole tree read as a run reads it takes no more and not 1% less (`Restore/RestoreBlobSet`; `Repository.Tests/RestoreReadTests`). |
| 2026-10 | Accepted | [ADR-0103](0103-a-restore-plan-names-what-is-held-only-in-an-archival-tier.md) names in the plan what every copy holds only in an archival tier, apart from the missing objects §2 already named (contract 1.65). Of what this record left owed, plan export and resume remain. |
| 2026-10 | Accepted | [ADR-0107](0107-the-command-line-plans-a-restore-on-its-own.md): `restore --plan` prints this record's plan from the command line, made by the service or in direct mode alike, and restores nothing; export to a file and resume stay owed. |
| 2026-10 | Accepted | [ADR-0108](0108-a-restores-plan-can-be-kept-in-a-file.md): the command line keeps this record's plan in a file as JSON with `restore --export`; resume stays owed. |
| 2026-10 | Accepted | [ADR-0109](0109-a-restore-cut-short-is-resumed.md) resumes a restore cut short, the last of what this record left owed. §2's measure needs nothing for a file a resume finds already where it lands at its length, and §5's receipt moves to schema 8 for `found_in_place`. |
| 2026-10 | Accepted | [ADR-0131](0131-the-service-measures-a-restores-working-space-where-its-parts-are-kept.md) measures §2's working directory, through the service, where the runs keep their parts: the folder every file a run there restores is assembled in since ADR-0129. |
| 2026-10 | Accepted | [ADR-0132](0132-a-resumes-plan-counts-only-what-it-still-reads.md): Amendment 2's figure, for a plan of a resume, counts only what the resume still reads: nothing of a file its run landed but the manifest, and through the service nothing a part its run kept proves. |
| 2026-10 | Accepted | [ADR-0136](0136-a-resumes-working-space-credits-what-its-parts-hold.md): §2's working directory, for a resume, needs only what the run's parts do not yet hold of each file. The folder a file lands in still needs all of it. |
| 2026-10 | Accepted | [ADR-0138](0138-the-working-space-is-said-as-room-to-assemble-files-one-at-a-time.md): §2's working-directory need is said as room to assemble the run's files one at a time, not for its largest file. The figure is as it was. |
| 2026-10 | Accepted | [ADR-0175](0175-a-restore-writes-back-the-windows-attribute-bits-it-may.md): a file's and a folder's Windows attribute bits are written back on Windows. |
| 2026-10 | Accepted | [ADR-0176](0176-a-restore-writes-back-a-windows-security-descriptor-whose-accounts-the-machine-knows.md): a file's and a folder's Windows security descriptor is written back on Windows where the machine knows every account it names. |
| 2026-10 | Accepted | [ADR-0178](0178-the-plan-names-a-file-its-new-folder-would-open-wider-than-it-was.md): the plan names a file whose descriptor is not written and whose folder would open it wider; the refusal this record does not make is still not made. |

# ADR-0185 — A run stops fetching online-only files after the first that does not come

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-009 (new: an online-only file is read only when it is new or changed; once the provider fails to deliver one, the run fetches no other, names each it passes with the file whose fetch failed, and the next backup fetches it again); NFR-PERF-003 (unchanged: an unchanged online-only file is never fetched); FR-SNP-009 (amended by [Amendment 1](#amendment-1-2026-11--a-providers-bits-are-not-kept): a provider's bits are not kept with a file or a folder, so going between online-only and on the device is no change, and a restore names none of them); FR-SNP-009 (amended again by [Amendment 2](#amendment-2-2026-11--a-file-the-run-fetched-is-freed-again): a file the run fetched, and read whole as the scan found it, is freed from the device again, unless the person pinned it, and a refusal fails nothing); FR-SNP-009 (amended by [Amendment 3](#amendment-3-2026-11--a-file-not-fetched-keeps-its-version-before): a changed online-only file the run did not fetch keeps the version backed up before it, at its name, and the failure says so); FR-SNP-009 (amended by [Amendment 4](#amendment-4-2026-11--a-scan-sees-placeholders-as-they-are): on Windows a scan sees a cloud provider's placeholders as they are, even where the system would disguise them from the process); FR-SNP-009 (amended by [Amendment 5](#amendment-5-2026-11--macos-names-an-online-only-file-by-its-flags): on macOS a file whose flags say it is dataless is online-only, and the walk does not open it); FR-SNP-009 (amended by [Amendment 6](#amendment-6-2026-11--a-folder-its-provider-holds-keeps-its-tree-when-its-listing-does-not-come): a folder its provider holds, whose listing does not come, keeps the tree backed up before it, and the failure says so); FR-SNP-009 (amended by [Amendment 7](#amendment-7-2026-11--the-first-provider-failure-stops-the-run-listing-and-fetching): the first failure of either kind, a file's fetch or a folder's listing, stops the run asking its provider for any other file or folder, and each it passes is named with the first)
**Related:** [architecture 06 §1](../architecture/06-filesystem-capture.md#1-scanner) (an unreadable file is an entry in the error manifest, not a failed backup), [ADR-0026](0026-phase-1-capture-shapes.md) (the capture failure reasons), [ADR-0183](0183-capture-reads-a-reparse-point-as-a-link-only-where-its-tag-names-one.md) (a file a filter keeps is read through the filter), [ADR-0186](0186-a-run-reuses-a-folder-it-was-told-did-not-change.md) (a folder taken as the prior snapshot held it)

**Built:**
- The rule: `Domain/MetadataRules` says which Windows attribute bits make a file online-only, and on macOS the dataless flag (Amendment 5).
- The scan: `Filesystem.Local/LocalFileSystemSource` names an online-only file on the entry it builds. On macOS it names a dataless file from its flags and does not open it (Amendment 5).
- The run: `Repository/SnapshotPublication` stops fetching after the first online-only file that does not come, and names each it passes.
- The unlisted folder: `Filesystem.Local/LocalFileSystemSource` lists a folder its provider holds before it enters it, and names one whose listing does not come unlisted. `Repository/SnapshotPublication` keeps that folder's tree from the prior snapshot, through `Repository/FolderReuse`, and `Repository/SourceComparer` counts it a failure with none of its files deleted (Amendment 6).
- The stop: `Repository/ProviderFailures` holds what a provider failed first in a run, for the walk that counts and the walk that captures alike. `Filesystem/ScanModel` lets the run tell the walk so, and after that `Filesystem.Local/LocalFileSystemSource` lists no folder a provider holds and names each unasked, which `Filesystem/MultiRootScan` puts under its label. `Repository/SnapshotPublication` keeps each such folder's tree and fetches no file after a failure of either kind (Amendment 7).
- The view: `Filesystem.Local/WindowsInterop` has the process see a cloud provider's placeholders as they are, and `Filesystem.Local/LocalFileSystemSource` asks it before a scan reads any attribute (Amendment 4).
- The tests:
  - `Repository.Tests/OnlineOnlyCaptureTests` — through a stand-in source, on every platform: a fetch that fails before its first byte or part-way stops the run opening any other online-only file, each named with the file that failed and why, while a file on the device is still backed up; a file gone, refused, or on the device that will not read stops nothing; the next backup fetches what was not fetched; an unchanged online-only file is not opened again. A changed one not fetched keeps the version backed up before it, asked for again by each backup and fetched once the provider answers (Amendment 3). A folder its provider holds, whose listing does not come, keeps the tree backed up before it, in a full walk and in a run told it changed, and in a set of several folders under its folder's label; one new to the set has none to keep; the next backup lists it again; and a comparison names none of its files deleted (Amendment 6). A folder whose listing does not come is asked for once in a run, while it counts; the capture lists no folder its provider holds after it and fetches no file, each kept and named with the first failure, and a file whose fetch fails stops the capture listing too; a folder whose listing fails only as the run captures stops it there; in a set of several folders a folder not asked is kept under its label; and one that failed while the run counted, gone when it captures, leaves nothing to name (Amendment 7).
  - `Domain.Tests/OnlineOnlyAttributesTests` — offline, recall on open and recall on data access each make a file online-only; pinned, unpinned and a reparse point do not. On macOS the dataless flag does, and it is not a flag a file keeps (Amendment 5).
  - `Filesystem.Tests/LocalScanTests` — an ordinary file is not online-only, and on Windows a file carrying the offline bit is. On Windows a scan in a process told to disguise placeholders leaves it seeing them as they are (Amendment 4). On Linux and macOS a file whose stat says it is dataless is online-only, and the walk leaves it closed (Amendment 5). A folder marked dataless, or on Windows carrying the offline bit, whose listing fails is named unlisted and not entered; an ordinary folder, and a provider's folder the account may not list, are entered and fail as before (Amendment 6). Once the run says a provider failed earlier, a folder it holds is not asked for its listing and is named unasked, while an ordinary folder is listed as ever (Amendment 7).

---

## Context

A cloud provider that syncs a folder can leave a file's content off the device and keep only a placeholder: an **online-only file**. On Windows the placeholder's attributes say so, with recall on data access, recall on open or offline. Reading it asks the provider to fetch the content, and the read waits while it does.

Capture reads a file only when it is new or changed (NFR-PERF-003), judged by identity, size and modification time. A placeholder keeps those as the provider's copy has them, so an unchanged online-only file is never read and costs nothing.

A new or changed one is read, and that read is a fetch. Where the provider does not deliver, the platform fails the read only once its own time-out has passed. That can happen because the provider is not running, is signed out or offline, or does not serve the account the service runs as. A report from the field measured about two minutes per file. Each such file then failed alone, as an unreadable file does (ADR-0026), after its own time-out.

So a set reconfigured to take in a folder of 89 online-only files ran for about three hours to end with 89 gaps. All that time its progress stood at the last file.

## Decision

### 1. The scan names an online-only file

On Windows a file is online-only where its attributes carry offline, recall on open or recall on data access. Pinned and unpinned say only what the person wants kept on the device. A reparse point says only that a filter keeps the file (ADR-0183). Neither makes a file online-only.

> **2026-11 ([Amendment 4](#amendment-4-2026-11--a-scan-sees-placeholders-as-they-are)).** The scan reads these attributes as they are. Windows can disguise placeholders from a process, as ordinary files without them, so on Windows a scan first asks to see placeholders as they are.

> **2026-11 ([Amendment 5](#amendment-5-2026-11--macos-names-an-online-only-file-by-its-flags)).** On macOS a file is online-only where its flags say it is dataless, and the walk names it without opening it.

### 2. The first fetch that fails stops the run fetching

An online-only file fails its fetch where opening or reading it raises an I/O fault, before its first byte or part-way. Its failure reads `online-only, and fetching it failed: …`, with the platform's words, and the run logs one warning.

From then on the run opens no online-only file. Each it would have read is an entry in the error manifest, reason I/O error, which reads `online-only, and not fetched, since fetching <path> failed earlier in this run: …`. That names the file whose fetch failed and the platform's words for it.

> **2026-11 ([Amendment 7](#amendment-7-2026-11--the-first-provider-failure-stops-the-run-listing-and-fetching)).** A folder's listing that does not come stops the run as a fetch does. After the first failure of either kind, the run fetches no online-only file and lists no folder its provider holds.

### 3. What is not the provider failing stops nothing

A file gone since the scan, or one the account may not read, is the file's own state. A file on the device that will not read is the device's. Each fails alone, as before, and the run goes on fetching.

### 4. The next backup fetches again

A file not fetched is in no snapshot this run publishes, so the next run finds no prior version for it and reads it. The stop belongs to the run that met the failure. A later run stops only if its own fetch fails.

## Consequences

- **A run that meets a provider that does not deliver ends after about one time-out,** not one per file, and names every file it passed.

  > **2026-11 ([ADR-0193](0193-a-sets-status-says-which-online-only-files-its-last-backup-could-not-fetch.md)).** The run names them in its error manifest, and now also counts them: a set's status says how many its last backup did not fetch, how many keep the version before, the file that failed and why, and since when backups have been missing some.
- **One failed fetch stops every online-only file in the run,** whichever provider keeps it, because the run cannot tell one provider's placeholders from another's. A passing fault, such as a network drop, defers the rest to the next backup. Nothing is lost: each is named and fetched again.
- **A changed online-only file not fetched is absent from this run's snapshot,** as any file that fails capture is. Its earlier versions stay in earlier snapshots.

  > **2026-11 ([Amendment 3](#amendment-3-2026-11--a-file-not-fetched-keeps-its-version-before)).** It keeps the version backed up before it, where that version is at its name, so the newest snapshot still restores it and retention cannot let the last copy go.
- **An unchanged online-only file is still never fetched** (NFR-PERF-003). A change to its metadata alone is still recorded from the prior version's manifest, with no fetch.

## What this does not do

- **Fetch in the person's own session.** The service, which runs as the system account by default, asks as any program does. Whether a provider serves that account is the provider's choice.
- **Put a fetched file back.** A file the run fetched stays on the device as the provider leaves it. The owner has chosen that a backup should put back a file it fetched, and that is a later record.

  > **2026-11: done ([Amendment 2](#amendment-2-2026-11--a-file-the-run-fetched-is-freed-again)).** A file the run fetched and read whole is freed from the device again, unless the person pinned it.
- **Leave the provider's state out of a file's metadata.** A file's attributes carry the provider's bits. A change between online-only and on the device is therefore a metadata change, recorded with no fetch, and a restore names those bits as not applied.

  > **2026-11: done ([Amendment 1](#amendment-1-2026-11--a-providers-bits-are-not-kept)).** The provider's bits are no longer kept, so going between online-only and on the device is no change, and a restore names none of them.
- **Say before a run how much it must fetch.**

  > **2026-11: done ([ADR-0192 Amendment 2](0192-a-sets-status-says-how-many-changed-files-its-next-backup-will-take.md#amendment-2-2026-11--the-status-says-how-much-the-next-backup-must-fetch)).** A set's status says how many of the changed files its next backup will take are online-only ones it must fetch, and their bytes.
- **Name an online-only file on macOS.** There, the walk opens each file it classifies, which may itself fetch one. Linux has no such marker.

  > **2026-11: done ([Amendment 5](#amendment-5-2026-11--macos-names-an-online-only-file-by-its-flags)).** A file whose flags say it is dataless is online-only, and the walk does not open it.

## Alternatives considered

- **Leave online-only files out.** Comparable tools offer this as an option. It never fetches, but it also never backs up a file the person did not keep on the device, and the owner chose to back them up.

  > **2026-11: taken up as a choice of the set ([ADR-0195](0195-a-set-may-leave-its-online-only-files-to-their-cloud-provider.md)).** Backing them up stays the default. A set may say it leaves them to their provider: its backups fetch none, and a file new or changed is left out of the snapshot without failing it.
- **A shorter time-out of our own.** The platform owns the wait. A read cannot be cut short below it without abandoning a thread mid-call.
- **Stop per provider rather than per run.** Telling providers apart needs each provider's interface. The run stopping whole is simpler and loses nothing.
- **Stop after several failures.** One failure is the evidence there is, and each further one costs a full time-out.

## Amendment 1 (2026-11) — a provider's bits are not kept

**Requirements:** FR-SNP-009 (amended: a provider's bits are not kept with a file or a folder, so going between online-only and on the device is no change, and a restore names none of them)

### Context

A file's attributes, captured whole on Windows, carried five bits a cloud provider keeps on the entry: offline, recall on open and recall on data access, which say whether the content is on the device, and pinned and unpinned, which say whether the person wants it kept there.

They change whenever the provider fetches a file, frees it, or the person asks it kept. Each change made the file's metadata differ from its prior version's. The next backup then wrote a new version from the prior manifest. That cost no fetch, but every such version said nothing about the file. A folder's tree changed the same way, and so did every tree above it. A restore then named the bits it could not set, offline among them, as not applied, though no restore could set them and nothing was lost.

### Decision

**A backup keeps none of the five.** Publication drops them from a file's and a folder's metadata before anything is built from it: the manifest, the tree, and the digest an incremental compares. Going between online-only and on the device, or being pinned or unpinned, is therefore no change, and the prior version stands.

**A restore names none of them.** They are left out of the bits a restore names as not given back, so a backup taken before this record, which kept them, restores with no such note.

Whether a file is online-only is still read from its attributes at the scan, before they are dropped, so the run's stop (Decision 2) is unchanged.

### Consequences

- A file the provider fetches or frees, or the person pins, keeps its version and costs nothing. A folder keeps its tree.
- The first backup after this record writes a new version of each file and folder whose metadata kept one of the bits. That is one manifest or tree each, with no fetch, and it happens once.
- A restore never gives a file the provider's state, which only the provider can, and no longer says so.

## Amendment 2 (2026-11) — a file the run fetched is freed again

**Requirements:** FR-SNP-009 (amended: a file the run fetched, and read whole as the scan found it, is freed from the device again, unless the person pinned it, and a refusal fails nothing)

### Context

A run that fetches an online-only file leaves it on the device. The provider may free it again later, when space runs short, but until then it takes the room its content needs. A backup that takes in a folder of such files therefore fills the disk with what the person had kept off it. Every changed file fetched later adds to that.

The owner chose that a backup should leave such a file as it found it.

### Decision

**A file the run fetched is unpinned once it is read whole.** On Windows the run asks the cloud files platform to unpin the file. Any program may make that request, through a handle that can read the file's data. The platform then has the provider free the content, in its own time.

**Only what the run itself fetched, read whole as the scan found it.**
- The file was online-only at the scan, so reading it was the fetch.
- The read finished as the scan found it: same identity, same length, same modification time.
- A file changed while it was read, or replaced at its name, is left alone. Freeing it would act on something the run did not read whole.
- A file not fetched is left as it is: one unchanged since the last backup, one the provider failed to deliver, and a file already on the device.

**A file the person pinned is never asked.** Pinned says they want it kept on the device. A file pinned between the scan and the read keeps that wish.

**A refusal fails nothing.** The backup has the file whole. That the device keeps it as well is the provider's to settle, and it costs only space. The run logs the refusal, with the platform's words, and goes on.

### Consequences

- A backup asks for an online-only file it fetched to be freed as soon as it has read it. The disk keeps the file only until its provider frees it, not until the person does.
- A later backup of an unchanged file fetches nothing, so it frees nothing. Whatever the person did with the file since is theirs.
- On Linux and macOS nothing is freed, since nothing there is named online-only.

  > **2026-11 ([Amendment 5](#amendment-5-2026-11--macos-names-an-online-only-file-by-its-flags)).** macOS now names a dataless file online-only, and frees none: it evicts a file only through its provider's own interface.

### What this does not do

- **Wait for the provider to free the file.** The platform frees it in its own time, and a run does not check that it did.
- **Show a refusal in the console.** It is in the log.

## Amendment 3 (2026-11) — a file not fetched keeps its version before

**Requirements:** FR-SNP-009 (amended: a changed online-only file the run did not fetch keeps the version backed up before it, at its name, and the failure says so)

### Context

A changed online-only file that a run did not fetch is left out of its snapshot (Consequences). Its earlier versions are only in earlier snapshots, and retention expires those by their own rules. Keeping a deleted file restorable for some days (ADR-0094) does not help for long: it counts from the first snapshot the file is missing from.

A provider can stay away for weeks. That happens when the person stays signed out, or the provider does not serve the account the service runs as. In that time every backup leaves the file out, and once the last snapshot holding it expires, the backup no longer has the file at all, though the person still does.

The owner chose that such a file keep the version backed up before it. The owner kept this to files a provider holds. Their content is safe where the provider keeps it, so keeping the older copy hides nothing the person could have fixed.

### Decision

**A changed online-only file the run did not fetch keeps the version backed up before it.** This covers the file whose fetch failed and every one the run passed after it (Decision 2). The snapshot's tree names the prior snapshot's version, unchanged, and a restore of the newest snapshot gives that version back.

**Only a version at the file's own name.** A version found only under another name, because the file moved as well as changed, is not put here. A manifest states its own name, and rewriting it here would claim the earlier content was modified when this one was. That file is left out, as before.

**The failure is still recorded.** It is the same entry in the error manifest, with what stands in its place added: `…; the version backed up earlier, last modified <time> UTC, stands in its place`. The person sees it among the run's failures, and the run still completes with failures.

> **2026-11 ([ADR-0197 Amendment 1](0197-format-7-a-failure-says-what-stands-in-its-place.md#amendment-1-2026-11--the-backup-writes-the-key-and-a-restore-reads-it)).** From format 7 the failure also says so in key 4, so a restore names the file without asking an earlier snapshot.

**The next backup asks again.** Two things make sure of it.
- The folder holding a failure is walked even where the change journal names nothing (ADR-0186).
- The version kept carries the modification time it had when it was backed up, so the file is seen as changed.

While the provider stays away each backup keeps the same version. The first backup it answers fetches the file.

**A file new to the set has nothing to keep** and is left out, as before. **A file on the device that will not read** is not the provider's, and is left out as before.

### Consequences

- The newest snapshot holds every online-only file it held before, however long the provider stays away. Retention never sees one go.
- A snapshot can now name a failure at a path its tree also holds. The tree then holds an earlier version, carried from the snapshot before ([specification 06 §8](../../specifications/repository-format/06-manifests.md#8-error-manifest)).
- A restore of the file gives the earlier version back, as the failure says, and does not say so itself.

### What this does not do

- **Say at restore that a file is older than its source was.** The run's failures say it. A restore plan that names such a file, read from the snapshot's error manifest, is a later record.

  > **2026-11: done ([ADR-0194](0194-a-restores-plan-names-the-files-an-earlier-version-stands-in-for.md)).** A restore's plan names each file it restores as a version backed up before, read from the snapshot's failures, with what the backup said of it.
- **Keep a version for a file that moved and changed.**
- **Keep a version for any other file that will not read.** The owner kept this to online-only files.

## Amendment 4 (2026-11) — a scan sees placeholders as they are

**Requirements:** FR-SNP-009 (amended: on Windows a scan sees a cloud provider's placeholders as they are, even where the system would disguise them from the process)

### Context

Decision 1 names a file online-only by its attributes. Since Windows 10 1803 the system can show a process a disguised view of a cloud provider's placeholders: ordinary files, without the attributes that say their content is online only. It shows that view to a process it judges not to understand placeholders, and a process can ask for either view for itself.

A scan shown the disguised view names no file online-only. It reads each new or changed one, which downloads it, and Decision 2 never stops the run. A set that leaves its online-only files to their provider (ADR-0195) downloads them too, since it leaves only the files the scan names.

The owner asked that cloud-synced files in a set's sources be handled as another well-known backup tool handles them. That tool asks to see placeholders as they are before it reads its sources. This product named online-only files and left them to their provider already; it did not ask for the view.

### Decision

**On Windows a scan asks to see placeholders as they are, before it reads any attribute.** It sets the process's placeholder mode to expose them. The mode is the whole process's, so a scan after the first changes nothing. Every read of a source file follows a scan in the same process, so the reads see what the scan saw. Before Windows 10 1803 the call does not exist and is skipped, since nothing there disguises a placeholder.

### Consequences

- An online-only file is named whatever view the process was first given. A set that fetches them stops after the first that does not come, and a set that leaves them downloads none.
- The rest of the service sees placeholders as they are too. Capture already reads a placeholder's reparse point as a file a filter keeps (ADR-0183), which is the view it was written for.

### What this does not do

- **Leave online-only files out as though they were not there.** The other tool can skip them. A backup that skipped them would take each as deleted, and its earlier versions would expire. ADR-0195 chose that a set may leave them to their provider and still hold what it had.
- **Leave out by name the files a provider keeps for itself.** Those names are one provider's, and a set's exclusions can name them.
- **Run against a real provider.** No runner has one. The test sets the disguised view itself and sees a scan undo it. That the disguised view hides the attributes is the platform's documented behavior, not shown here.

## Amendment 5 (2026-11) — macOS names an online-only file by its flags

**Requirements:** FR-SNP-009 (amended: on macOS a file whose flags say it is dataless is online-only, and the walk does not open it)

### Context

macOS keeps a cloud provider's file on the volume as a dataless file. Its name, size and times are there, its content is not, and its flags carry the dataless flag. Reading it asks the provider for the content, as on Windows, and opening it can too.

The walk opens each regular file it names, to read its content, extended attributes and holes from the one object its stat described. So on macOS every dataless file the walk met was opened, changed or not, and none was named online-only. A provider that did not deliver cost each read its own wait (Context). A set that leaves online-only files to their provider (ADR-0195) left none on macOS, since it leaves only what the scan names.

### Decision

**A file whose flags carry the dataless flag is online-only.** The rule is in the domain, beside the Windows bits. It is not a flag a file keeps: like the Windows bits (Amendment 1), it says how the provider holds the file, not what the file is.

**The walk does not open it.** It names the file from its stat and leaves it closed, reading its extended attributes by its path. The hole probe, which opens a file too, is skipped for an online-only file on either platform, since none of its content is on the device.

**From there it is like any other online-only file.** The run reads one only when it is new or changed, and then by its path, which asks the provider. It fetches no other after the first that does not come (Decision 2), a changed one not fetched keeps its version before (Amendment 3), and none is read where the set leaves them (ADR-0195).

### Consequences

- An unchanged dataless file costs its provider no request.
- A changed one is read by its path, not from a handle the walk took. The read is still held to the stat the walk took, by path, as on Windows.

### What this does not do

- **Free a fetched file again on macOS.** Amendment 2 unpins a file on Windows. macOS evicts one only through its provider's own interface, which a later record may take.
- **Leave a dataless folder unlisted.** A provider can keep a folder's listing too, and the walk lists every folder it enters, which asks for it.

  > **2026-11: in part ([Amendment 6](#amendment-6-2026-11--a-folder-its-provider-holds-keeps-its-tree-when-its-listing-does-not-come)).** The walk still lists such a folder. Where the listing does not come, the folder keeps the tree backed up before it.
- **Stop a read from fetching.** macOS lets a process refuse to fetch a dataless file. The run reads a changed file to back it up, and that read is the fetch it means.
- **Run against a real provider.** No runner has one. The test marks a stat dataless and sees the walk name the file and leave it closed.

## Amendment 6 (2026-11) — a folder its provider holds keeps its tree when its listing does not come

**Requirements:** FR-SNP-009 (amended: a folder its provider holds, whose listing does not come, keeps the tree backed up before it, and the failure says so)

### Context

A provider can keep a folder's listing as it keeps a file's content (Amendment 5). macOS marks such a folder dataless. Windows gives it the bits it gives an online-only file. Listing the folder asks the provider for its names.

Where the provider did not answer, the walk entered the folder, failed its listing, and the snapshot held the folder empty. The newest snapshot lost every file in it, though the person still had them at the provider. Amendment 3 keeps the version before for a file the run did not fetch, because a provider can stay away for weeks; a folder's files were not kept so.

### Decision

**A folder its provider holds is listed before the walk enters it.** That is a folder whose flags say it is dataless, on macOS, or whose attributes carry offline, recall on open or recall on data access (Decision 1), on Windows. Where it lists, the walk descends with the names it has.

**One whose listing does not come keeps the tree backed up before it.** That is a listing that raises an I/O fault. The walk does not enter the folder, and the snapshot holds the tree the prior snapshot held there, as a folder the change journal did not name is taken (ADR-0186). Every file below it restores as it was backed up.

**The failure is still recorded,** at the folder's path, reason I/O error: `online-only, and listing it failed: …; the folder as backed up earlier stands in its place`. The run completes with failures.

> **2026-11 ([ADR-0197 Amendment 1](0197-format-7-a-failure-says-what-stands-in-its-place.md#amendment-1-2026-11--the-backup-writes-the-key-and-a-restore-reads-it)).** From format 7 a folder that kept its tree says so in key 4 as well.

**A folder the prior snapshot does not hold has nothing to keep.** It is in the snapshot as before, empty, and its failure reads `online-only, and listing it failed: …`. **A folder the account may not list** is the account's (Decision 3), and is entered and fails as before.

**The next backup lists it again.** A run told what changed walks a folder holding a failure (ADR-0186), and a full walk lists every folder.

**A run that walks everything reads the prior snapshot's folders only when it meets such a folder.** It reads them once, then. A folder kept this way counts among the folders the run did not walk.

**A comparison counts the folder a failure** and names none of its files deleted, since the backup keeps them.

### Consequences

- The newest snapshot holds every file a provider's folder held before, however long the provider stays away from its listing.
- A file changed in such a folder while it was not listed is taken by the first backup that lists it, since its version kept carries the time it was backed up.
- A backup that kept such a folder is completed with failures, not behind (ADR-0193 Amendment 1): its failure is no file's.

  > **2026-11: no longer ([ADR-0193 Amendment 3](0193-a-sets-status-says-which-online-only-files-its-last-backup-could-not-fetch.md#amendment-3-2026-11--a-folder-not-listed-is-counted-and-a-backup-that-kept-it-is-behind)).** A backup whose every failure kept what was backed up before, file or folder, is behind.

### What this does not do

- **Stop listing after the first that does not come.** Each such folder is asked for its listing. Decision 2 stops the run fetching files, not listing folders.

  > **2026-11: done ([Amendment 7](#amendment-7-2026-11--the-first-provider-failure-stops-the-run-listing-and-fetching)).** The first failure, a folder's listing or a file's fetch, stops the run listing the folders a provider holds and fetching its files.
- **Leave such a folder unlisted where the set leaves its online-only files** (ADR-0195). A listing asks for names, not content, and the set's unchanged files are kept only by listing them.
- **Count the folder in the set's status.** The count of files not fetched (ADR-0193) names files; a folder kept this way is among the run's failures.

  > **2026-11: done ([ADR-0193 Amendment 3](0193-a-sets-status-says-which-online-only-files-its-last-backup-could-not-fetch.md#amendment-3-2026-11--a-folder-not-listed-is-counted-and-a-backup-that-kept-it-is-behind)).** The set's status counts the folders its last backup could not list, and how many kept their tree.
- **Name the folder's files in a restore's plan.** The plan names files whose failure is their own (ADR-0194); a folder's failure names the folder.

  > **2026-11: done ([ADR-0194 Amendment 3](0194-a-restores-plan-names-the-files-an-earlier-version-stands-in-for.md#amendment-3-2026-11--an-earlier-version-is-one-an-earlier-snapshot-holds)).** The plan names each file it restores below a folder kept from before, with what the backup said of the folder.
- **Run against a real provider.** No runner has one. The tests mark a folder dataless, or set the offline bit on Windows, and refuse its listing.

## Amendment 7 (2026-11) — the first provider failure stops the run listing and fetching

**Requirements:** FR-SNP-009 (amended: the first failure of either kind, a file's fetch or a folder's listing, stops the run asking its provider for any other file or folder, and each it passes is named with the first)

### Context

Decision 2 stops the run fetching once one online-only file does not come, since each would wait out the same time-out. Amendment 6 lists each folder a provider holds before the walk enters it, and nothing stopped those listings. A run asked for the listing of every such folder after one failed, and fetched files after a listing failed. A tree with many such folders could spend most of a run being told what it was told the first time.

### Decision

**The first failure of either kind stops both.** Once a file's fetch or a folder's listing has failed, the run fetches no other online-only file and lists no other folder its provider holds.

**The run holds what failed first, once.** The walk that counts the run and the walk that captures it share it, so a folder whose listing failed while the run counted is not asked for again while it captures.

**The walk is told, and decides nothing itself.** As it meets a folder a provider holds, the walk asks whether a provider failed earlier in the run, as it asks whether a folder is unchanged (ADR-0186). Where one did, it does not list the folder and names it unasked. The run answers between events, so the walk learns of a failure before it meets the next folder.

**A folder passed over is kept as one not listed is** (Amendment 6). It keeps the tree backed up before it, or is empty where there is none. Its failure, reason I/O error, names what failed first: `online-only, and not listed, since listing <path> failed earlier in this run: …`, or `since fetching <path>`, then `; the folder as backed up earlier stands in its place` where it kept its tree. The folder whose listing failed while the run counted reads as its own failure, `online-only, and listing it failed: …`.

**A file passed over after a listing failed** reads `online-only, and not fetched, since listing <path> failed earlier in this run: …`, and keeps the version backed up before it (Amendment 3).

**The set's status** counts the folders passed over with those not listed, and names the first failure ([ADR-0193 Amendment 3](0193-a-sets-status-says-which-online-only-files-its-last-backup-could-not-fetch.md#amendment-3-2026-11--a-folder-not-listed-is-counted-and-a-backup-that-kept-it-is-behind)). It names that failure only beside a file or folder the run missed: a folder that failed while the run counted and is gone when it captures leaves nothing to name.

**The console's hover** for a folder whose listing failed first says that no other online-only file or folder was asked for after it. The hover for a file's download keeps its words, which hold for a service before this amendment too: that one went on listing folders after a download failed.

### Consequences

- A provider that is away costs a run one time-out, not one for each folder and file it holds.
- A folder whose listing came while the run counted is not listed again once a file's fetch has failed. It keeps the tree backed up before it, though its provider answered minutes earlier, and the next backup asks again.
- A listing that fails while the run counts stops the capture fetching any file, including those the walk meets before that folder.

### What this does not do

- **Stop a comparison asking.** A comparison lists each folder a provider holds. It keeps nothing, and passes over nothing.
- **Leave such a folder unlisted where the set leaves its online-only files** (ADR-0195), as Amendment 6 says.
- **Run against a real provider.** No runner has one. The tests mark folders dataless and refuse a listing, and play the run's part between events.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-11 | Accepted | Built in one slice, tests first. On Windows a file whose attributes carry offline, recall on open or recall on data access is online-only. The first such file whose fetch fails stops the run opening any other: each is an error-manifest entry naming the file that failed and why, and the next backup fetches it again. A file gone, refused, or on the device that will not read stops nothing. `Domain/MetadataRules`; `Filesystem.Local/LocalFileSystemSource`; `Repository/SnapshotPublication`; `Repository.Tests/OnlineOnlyCaptureTests`; `Domain.Tests/OnlineOnlyAttributesTests`; `Filesystem.Tests/LocalScanTests` |
| 2026-11 | Amended | [Amendment 1](#amendment-1-2026-11--a-providers-bits-are-not-kept): a backup keeps none of the five bits a cloud provider keeps on an entry (offline, recall on open, recall on data access, pinned, unpinned). Publication drops them before a manifest, tree or digest is made, so going between online-only and on the device is no change, and a restore names none of them. `Domain/MetadataRules`; `Repository/SnapshotPublication`; `Repository.Tests/OnlineOnlyCaptureTests`; `Domain.Tests/OnlineOnlyAttributesTests`; `Domain.Tests/FileAttributesWriteBackTests` |
| 2026-11 | Amended | [Amendment 2](#amendment-2-2026-11--a-file-the-run-fetched-is-freed-again): a file the run fetched, and read whole as the scan found it, is unpinned so its provider frees it from the device again. A file changed or replaced while read, one not fetched, and one the person pinned are left alone. A refusal is logged and fails nothing. `Filesystem/ScanModel`; `Filesystem.Local/LocalFileSystemSource`; `Filesystem.Local/WindowsInterop`; `Agent/PacedFileSystemSource`; `Domain/MetadataRules`; `Repository/SnapshotPublication`; `Repository.Tests/OnlineOnlyCaptureTests`; `Domain.Tests/OnlineOnlyAttributesTests`; `Filesystem.Tests/LocalScanTests`; `Hosts.Tests/PacedFileSystemSourceTests` |
| 2026-11 | Amended | [Amendment 3](#amendment-3-2026-11--a-file-not-fetched-keeps-its-version-before): a changed online-only file the run did not fetch keeps the version backed up before it, at its name; the failure is still recorded, with what stands in its place, so the next backup walks its folder and asks again. A file new to the set, one found only under another name, and a file on the device that will not read keep nothing. `Repository/SnapshotPublication`; `Repository.Tests/OnlineOnlyCaptureTests` |
| 2026-11 | Accepted | Noted by [ADR-0193](0193-a-sets-status-says-which-online-only-files-its-last-backup-could-not-fetch.md): a set's status says how many online-only files its last backup did not fetch, how many keep the version before, the file that failed and why, and since when. |
| 2026-11 | Accepted | Noted by [ADR-0194](0194-a-restores-plan-names-the-files-an-earlier-version-stands-in-for.md): a restore's plan names each file it restores as a version backed up before, with what the backup said of it. |
| 2026-11 | Accepted | Noted by [ADR-0192 Amendment 2](0192-a-sets-status-says-how-many-changed-files-its-next-backup-will-take.md#amendment-2-2026-11--the-status-says-how-much-the-next-backup-must-fetch): a set's status says how many online-only files its next backup must fetch, and their bytes. |
| 2026-11 | Accepted | Noted by [ADR-0195](0195-a-set-may-leave-its-online-only-files-to-their-cloud-provider.md): a set may leave its online-only files to their provider, the alternative set aside here, by its own choice. Its backups fetch none, and a file new or changed is left out without failing it. |
| 2026-11 | Amended | [Amendment 4](#amendment-4-2026-11--a-scan-sees-placeholders-as-they-are): on Windows a scan sets the process's placeholder mode to expose a cloud provider's placeholders before it reads any attribute, so a process shown them disguised still names its online-only files; before Windows 10 1803 the call is skipped. `Filesystem.Local/WindowsInterop`; `Filesystem.Local/LocalFileSystemSource`; `Filesystem.Tests/LocalScanTests` |
| 2026-11 | Amended | [Amendment 5](#amendment-5-2026-11--macos-names-an-online-only-file-by-its-flags): on macOS a file whose flags carry the dataless flag is online-only; the walk names it from its stat and does not open it, and the hole probe is skipped for an online-only file. Nothing is freed on macOS. `Domain/MetadataRules`; `Filesystem.Local/PosixInterop`; `Filesystem.Local/LocalFileSystemSource`; `Domain.Tests/OnlineOnlyAttributesTests`; `Filesystem.Tests/LocalScanTests` |
| 2026-11 | Amended | [Amendment 6](#amendment-6-2026-11--a-folder-its-provider-holds-keeps-its-tree-when-its-listing-does-not-come): a folder its provider holds (dataless on macOS; offline, recall on open or recall on data access on Windows) is listed before it is entered; where the listing raises an I/O fault, the folder keeps the tree the prior snapshot held for it and the failure says so, while a folder that snapshot does not hold is empty as before; a refusal is the account's and fails as before; a full walk reads the prior snapshot's folders only on meeting such a folder; a comparison names none of its files deleted. `Filesystem.Local/LocalFileSystemSource`; `Repository/SnapshotPublication`; `Repository/FolderReuse`; `Repository/SourceComparer`; `Filesystem.Tests/LocalScanTests`; `Repository.Tests/OnlineOnlyCaptureTests` |
| 2026-11 | Amended | [Amendment 7](#amendment-7-2026-11--the-first-provider-failure-stops-the-run-listing-and-fetching): the first failure of either kind, a file's fetch or a folder's listing, stops the run fetching online-only files and listing the folders a provider holds; the walk that counts and the walk that captures share what failed first, and the walk is told it and names each folder it passes unasked; each folder passed over keeps its tree, and each failure names the first. `Repository/ProviderFailures`; `Filesystem/ScanModel`; `Filesystem/MultiRootScan`; `Filesystem.Local/LocalFileSystemSource`; `Repository/SnapshotPublication`; `Filesystem.Tests/LocalScanTests`; `Repository.Tests/OnlineOnlyCaptureTests` |
| 2026-11 | Accepted (related) | [ADR-0197 Amendment 1](0197-format-7-a-failure-says-what-stands-in-its-place.md#amendment-1-2026-11--the-backup-writes-the-key-and-a-restore-reads-it): from format 7 the failure of a file that kept its version, or a folder that kept its tree, says so in key 4. |

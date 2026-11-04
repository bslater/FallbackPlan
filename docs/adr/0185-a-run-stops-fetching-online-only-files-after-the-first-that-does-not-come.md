# ADR-0185 — A run stops fetching online-only files after the first that does not come

**Status:** Accepted
**Date:** 2026-11
**Requirements:** FR-SNP-009 (new: an online-only file is read only when it is new or changed; once the provider fails to deliver one, the run fetches no other, names each it passes with the file whose fetch failed, and the next backup fetches it again); NFR-PERF-003 (unchanged: an unchanged online-only file is never fetched); FR-SNP-009 (amended by [Amendment 1](#amendment-1-2026-11--a-providers-bits-are-not-kept): a provider's bits are not kept with a file or a folder, so going between online-only and on the device is no change, and a restore names none of them); FR-SNP-009 (amended again by [Amendment 2](#amendment-2-2026-11--a-file-the-run-fetched-is-freed-again): a file the run fetched, and read whole as the scan found it, is freed from the device again, unless the person pinned it, and a refusal fails nothing); FR-SNP-009 (amended by [Amendment 3](#amendment-3-2026-11--a-file-not-fetched-keeps-its-version-before): a changed online-only file the run did not fetch keeps the version backed up before it, at its name, and the failure says so)
**Related:** [architecture 06 §1](../architecture/06-filesystem-capture.md#1-scanner) (an unreadable file is an entry in the error manifest, not a failed backup), [ADR-0026](0026-phase-1-capture-shapes.md) (the capture failure reasons), [ADR-0183](0183-capture-reads-a-reparse-point-as-a-link-only-where-its-tag-names-one.md) (a file a filter keeps is read through the filter)

**Built:**
- The rule: `Domain/MetadataRules` says which Windows attribute bits make a file online-only.
- The scan: `Filesystem.Local/LocalFileSystemSource` names an online-only file on the entry it builds.
- The run: `Repository/SnapshotPublication` stops fetching after the first online-only file that does not come, and names each it passes.
- The tests:
  - `Repository.Tests/OnlineOnlyCaptureTests` — through a stand-in source, on every platform: a fetch that fails before its first byte or part-way stops the run opening any other online-only file, each named with the file that failed and why, while a file on the device is still backed up; a file gone, refused, or on the device that will not read stops nothing; the next backup fetches what was not fetched; an unchanged online-only file is not opened again. A changed one not fetched keeps the version backed up before it, asked for again by each backup and fetched once the provider answers (Amendment 3).
  - `Domain.Tests/OnlineOnlyAttributesTests` — offline, recall on open and recall on data access each make a file online-only; pinned, unpinned and a reparse point do not.
  - `Filesystem.Tests/LocalScanTests` — an ordinary file is not online-only, and on Windows a file carrying the offline bit is.

---

## Context

A cloud provider that syncs a folder can leave a file's content off the device and keep only a placeholder: an **online-only file**. On Windows the placeholder's attributes say so, with recall on data access, recall on open or offline. Reading it asks the provider to fetch the content, and the read waits while it does.

Capture reads a file only when it is new or changed (NFR-PERF-003), judged by identity, size and modification time. A placeholder keeps those as the provider's copy has them, so an unchanged online-only file is never read and costs nothing.

A new or changed one is read, and that read is a fetch. Where the provider does not deliver, the platform fails the read only once its own time-out has passed. That can happen because the provider is not running, is signed out or offline, or does not serve the account the service runs as. A report from the field measured about two minutes per file. Each such file then failed alone, as an unreadable file does (ADR-0026), after its own time-out.

So a set reconfigured to take in a folder of 89 online-only files ran for about three hours to end with 89 gaps. All that time its progress stood at the last file.

## Decision

### 1. The scan names an online-only file

On Windows a file is online-only where its attributes carry offline, recall on open or recall on data access. Pinned and unpinned say only what the person wants kept on the device. A reparse point says only that a filter keeps the file (ADR-0183). Neither makes a file online-only.

### 2. The first fetch that fails stops the run fetching

An online-only file fails its fetch where opening or reading it raises an I/O fault, before its first byte or part-way. Its failure reads `online-only, and fetching it failed: …`, with the platform's words, and the run logs one warning.

From then on the run opens no online-only file. Each it would have read is an entry in the error manifest, reason I/O error, which reads `online-only, and not fetched, since fetching <path> failed earlier in this run: …`. That names the file whose fetch failed and the platform's words for it.

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

## Alternatives considered

- **Leave online-only files out.** Comparable tools offer this as an option. It never fetches, but it also never backs up a file the person did not keep on the device, and the owner chose to back them up.
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

# ADR-0095 — The staging archive is swept, and heals from its destinations

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-VER-009 (new); FR-VER-002 (every blob a sweep reads is bound to its key); FR-VER-003 (the staging archive's circuit is reported); FR-VER-007 (a length that differs because the staging archive's copy is unsound is the staging archive's finding); FR-RST-007 (what a restore reads around in the staging archive is kept for repair); keeps FR-VER-005
**Related:** [ADR-0035](0035-destination-fitness.md) §4 and Amendments 1–3 (the destination sweep and its repair; §4 amended here), [ADR-0075](0075-a-restore-reads-around-damage.md) §7 (amended here), [ADR-0034](0034-hub-and-spoke-destinations.md) §6 (the staging trim), [ADR-0076](0076-damage-is-traced-to-what-needs-it.md) (what damage reaches), [ADR-0089](0089-a-backups-file-names-need-the-passphrase.md) Amendment 1 (a notice's names), [architecture 09 §5.5](../architecture/09-replication-and-peers.md#55-re-reading-what-is-held-and-repairing-it), [command-contract](../../specifications/command-contract/README.md)

**Built:**
- The proof: `Repository/VerifyEngine` binds a blob to its key at any level, `Repository/ReplicaRepair` proves a copy through it, and `Repository/ReplicaSweep` binds every blob it reads and names the source copies a length comparison proved unsound.
- The sweep: `Agent/StagingSweepJob`, on the transfer lane under the set's gate and queued by `Agent/Scheduler` ahead of the set's destination sweeps; its record, `Application/StagingSweepStore` (`staging-sweeps.json`); its repair, `Agent/ReplicaRepairer` drawing on the set's destinations.
- The other finders: `Agent/ReplicaSweepJob` repairs the staging archive's copy its length comparison proved unsound, under the gate it holds, and the restore's read-around in `Agent/ServiceCommandHandler` records what it found there.
- The report: `Application/StatusModel` (outstanding staging damage), contract 1.63's `staging_sweep` (`Api/Results`, `Api/ContractVersion`), `Cli/CliApplication`'s `staging-sweep:` token on the set's line, and the console's set card.
- The tests:
  - `Repository.Tests/ReplicaSweepTests` — the digest passes a sound blob under another blob's key and the key does not, with no source to compare against; a length that differs because the source's copy is damaged is the source's finding, one whose source copy will not read is nobody's, and two sound blobs under one identity are neither's
  - `Hosts.Tests/StagingSweepTests` — a rotted staging blob and a sound one under another's key are replaced from a destination's copy, and the notice says from where; with no sound copy the blob is left where it is, named, and the set degraded; the damage is tried again under back-off and repaired once a copy is sound; damage still standing at the next circuit is not said again; the circuit closes over consecutive passes and rests a week; a direct-ship set has nothing swept and reports nothing; a blob that will not read stalls under back-off and three stalls are said; a destination's length comparison that finds staging's copy wrong repairs staging and fails nothing; a repair cut short between its delete and its put is refilled, and a key no repair emptied is let go; a repair whose put the staging archive refused is finished by a later try, though the try repaired more after it; the status reports the circuit
  - `Hosts.Tests/RestoreReadAroundTests` — staging damage a restore reads around is kept, and the next pass repairs it without a second notice
  - `Application.Tests/StagingSweepStoreTests` — the circuit, the stall, the damage and its back-off, and every key under repair survive a restart; a key resolved is no longer owed back; the file is versioned and set aside when unreadable or newer
  - `Application.Tests/StagingDamageStatusTests` — outstanding staging damage degrades a set every destination holds in sync, and says how much
  - `Api.Tests/ContractAdditiveFieldsTests`, `Api.Tests/ConfigurationContractTests` — the wire name and its pre-1.63 default, and the version pin
  - `Cli.Tests/StatusSweepTokenTests`, `Web.Tests/ConsoleDestinationCardTests`, `Web.Tests/StatusRelayNamesTests`, `Web.DomTests/ConsoleViewsDomTests` — the CLI's token, the console's line, and the names the console reads

---

## Context

A staging set's staging archive is the copy every destination is filled from, and nothing read it back. Damage there was found only by accident, and kept nowhere:

- `check` and `verify` read it and printed what they found.
- A restore that read around it raised a notice saying nothing would repair it ([ADR-0075](0075-a-restore-reads-around-damage.md) §7).
- A blob whose footer would not open held back every retention pass of the set.
- A destination's deep sweep compared each blob's length with staging's and blamed the destination for any difference, whichever copy was wrong. Its repair then replaced the destination's sound copy from another destination where one existed, or held the destination failed where none did, and the next circuit blamed it again.

Phase 4's criterion *corruption healed from another replica* was met for local paths and object stores ([ADR-0035](0035-destination-fitness.md) Amendment 1), but not for the staging archive.

Three facts shaped the design.

- **The staging archive has nothing beside it.** The destination sweep caught a sound blob stored under another blob's key by its length differing from the source's (ADR-0035 §4). With no source, length cannot catch it.
- **A staging blob that is missing has usually been let go deliberately.** The staging trim deletes history from `blobs/data/` once every destination entitled to it verifiably holds it (ADR-0034 §6), and the collector deletes what nothing needs. Neither leaves a tombstone the sweep could read.
- **A backup takes no gate.** Every other writer of staging (sync, retention, deletion, the destination sweep) waits on the set's gate, but a backup does not. It may reference a blob it probed present a moment before a repair deleted it.

## Decision

1. **The staging archive is swept.** A staging set's staging archive is read in full as a destination is: in segments of 64 blobs or 4 GiB, a circuit carried on every pass until it closes, then resting a week. The cadence is fixed, because the archive is on a disk this service owns and reading it costs no one else anything. Nothing paces it. The segment runs on the transfer lane under the set's gate, queued ahead of the set's destination sweeps, which compare against it. A direct-ship set has no staging archive and nothing is swept for it.

2. **Every blob a sweep reads is bound to its key.** A blob passes when it hashes to the digest sealed in its footer *and* its envelope names the blob its key derives from. This was the repair's proof (ADR-0035 Amendment 1), and it now runs inside the sweep, from the envelope the first read already opens. It is the staging sweep's only witness to a sound blob stored under another blob's key. The destination sweep binds keys too, so a swap of equal length no longer passes there either. **This amends ADR-0035 §4**, which said the length comparison was the only thing that caught a swap.

3. **A length that differs is judged by proving the source's copy.** A destination's blob that passes both checks is the blob, so a staging copy of another length is something else. The source's copy is proven where it sits:
   - **Unsound:** the finding is the staging archive's. The destination sweep repairs it at once, under the gate it holds, and fails nothing at the destination.
   - **Unreadable:** the finding is nobody's.
   - **Sound:** also nobody's. Two sound blobs under one identity can only come from a writer whose counter went back, and replacing either would lose the one replaced. Before this, the destination's copy would have been replaced from staging's.

4. **Damage is recorded before it is repaired.** It goes on the set's record before anything is done about it, so a repair cut short leaves it for the next try. Every destination of the set is a source, nearest first: local paths by priority, then object stores, then peers. None of them holds the damage being repaired. Each source copy is staged under the spool and proven first. Only then is the damaged blob deleted and the copy put, and the copy is proven again where it landed. With no sound copy, the blob stays where it is, because every record the damage missed still restores. It stays named, and the set reads degraded, its warning saying how many objects are outstanding.

5. **Damage no copy could replace waits a back-off between tries.** Every try reads the destinations it asks, and at an object store each read is a request its provider may charge for. So a try that leaves damage standing waits the sync's back-off before the next, capped at an hour. Damage nothing has tried yet makes all of it due at once.

6. **A repair cut short is finished.** Each key a repair is replacing is recorded before anything is touched and cleared once its repair returns. A key so recorded, found empty on a later try, is refilled, because a backup running beside the repair may reference it. Every such key is kept, not only the last: a try carries on past a repair the staging archive refused, and the repairs after it are recorded beside it. Any other key the staging archive no longer holds was let go by the trim or the collector. It is not owed, and history does not come back.

7. **Each finding is said once, by what found it.**
   - **The circuit and a destination's sweep** raise `staging-damaged:{set}`. It names what was found, what replaced it and from where, and what nothing could. It counts the files and snapshots the standing damage reaches, with their names kept behind the passphrase. A later repair does not withdraw it, for ADR-0035 Amendment 1's reason: a disk that altered a backup once is the person's to hear about.
   - **A restore's read-around** raises its own notice and records the keys. The next pass repairs them without a second notice.
   - **A stall:** a segment stopped at a blob that will not read waits the back-off, as a destination's does. Three in a row raise `staging-sweep-stalled:{set}`, which the segment that reads past it withdraws.

8. **The circuit is reported where the status is.** Contract 1.63 adds `staging_sweep` to a set's status, in the words of a destination row's `deep_sweep`. The CLI's set line carries `staging-sweep:ok@…`, and the console's set card says when the staging archive was last read back in full.

## Consequences

- **FR-VER-009 is met, and Phase 4's healing criterion is met for the staging archive.** A peer's replica is still not repaired from here: replacing an object there needs a write the retrieval session does not offer (ADR-0035 Amendment 2). Since [ADR-0100](0100-a-peer-replaces-what-its-own-check-finds-damaged.md) it is, over a repair session of its own.
- **A staging archive is read in full once a week.** A segment holds the transfer worker for as long as it reads four gibibytes off a local disk. The first circuit begins on the first pass after an upgrade, as a destination's did when its sweep arrived, and a shut background window holds it as it holds every transfer.
- **A destination's swap of equal length is now found.** Its finding names the different blob rather than a length.
- **A staging copy is no longer blamed on a destination.** Destination damage a staging copy caused, and the circuit-by-circuit repair it triggered, stop.
- **Damage that no destination holds sound is still lost where it is.** It is now said, counted against the set, and tried again until a sound copy appears.
- **On Windows a repair can meet an open read.** A backup's range read holds the blob without delete sharing, so the delete fails and the try waits its back-off.

## Alternatives considered

- **A configurable cadence.** Deferred. Nothing in the configuration names the staging archive, and a week matches a local path's default.
- **Repairing during the restore.** Rejected. A restore holds no gate, so a sync or a trim could change staging under the repair.
- **Refilling every key staging no longer holds.** Rejected. It would undo the trim and bring back what the collector condemned.
- **Leaving the destination sweep unbound, with length standing in.** Rejected. A swap of equal length passes, and a damaged source would stay invisible from the replica's side.
- **Blaming the destination when both copies prove sound.** Rejected. It replaces a sound blob.
- **A row for the staging archive on the destination ledger.** Rejected. Every reader of the ledger takes a row for a place the set is copied to.

## What this record does not do

- **Repair a peer's replica.** Still held until its owner removes the objects (FR-VER-008).

  > **Built 2026-10 ([ADR-0100](0100-a-peer-replaces-what-its-own-check-finds-damaged.md)):** a peer whose build takes a repair is offered a copy proven sound here, and takes it where its own check of its copy finds that copy damaged.
- **Read the staging archive on demand.** `verify-destination` reads destinations, and `check` still prints what it finds.
- **Make the staging archive's cadence configurable.**

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built tests first: the key binding in `Repository/VerifyEngine`, the sweep's source proof in `Repository/ReplicaSweep`, `Agent/StagingSweepJob` and its record in `Application/StagingSweepStore`, the repair through `Agent/ReplicaRepairer`, the destination sweep's and the restore's findings handed to it, outstanding damage in `Application/StatusModel`, and contract 1.63's staging sweep with the CLI and the console. Amends [ADR-0035](0035-destination-fitness.md) §4 and [ADR-0075](0075-a-restore-reads-around-damage.md) §7 |
| 2026-10 | Amended (related) | [ADR-0100](0100-a-peer-replaces-what-its-own-check-finds-damaged.md) repairs a peer's replica, which this record listed as not done: from a copy here proven sound, taken by the peer only where its own check of its copy agrees the copy is damaged. Phase 4's healing criterion is met for every destination kind. `Agent/PeerRepairTarget`; `Hosts.Tests/PeerDeepSweepTests` |

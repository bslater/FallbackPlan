# ADR-0094 — A deleted file stays restorable for a declared time

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-GC-014 (new); FR-GC-010 (a destination's override carries its own duration); FR-DR-006 (the duration is recorded with the set's own policy); keeps FR-GC-001, FR-GC-009, FR-GC-012 and FR-GC-013
**Related:** [architecture 07 §2](../architecture/07-retention-and-gc.md#2-retention-policy) (the rule it names and leaves unbuilt), [ADR-0009](0009-garbage-collection-safety.md) (what retention may and may not do), [ADR-0034](0034-hub-and-spoke-destinations.md) (per-destination policies), [ADR-0037](0037-configuration-over-the-command-contract.md) (configuration over the contract), [ADR-0061](0061-adopt-a-destinations-archives.md) Amendment 1 (the set's own retention recorded), [ADR-0078](0078-implausible-capture-times.md) (the times the rules can rely on), [ADR-0080](0080-a-person-deletes-a-snapshot.md) (a person's deletion), [ADR-0014](0014-format-versioning-and-stability.md) (no forward compatibility before the freeze), [ADR-0071](0071-recovering-operation-after-total-loss.md) (the set-configuration object it proposed, superseded and left as it is)

**Built:**
- The rule: `Retention/RetentionPlanner` keeps the snapshots either side of a lost path and names the pairs it reads; `Retention/DeletedFileSurvey` compares their sealed trees. `Retention/RetentionRunner`, `Retention/DestinationConvergence` and `Retention/StagingTrim` hand the answers to every plan that reads a policy.
- The configuration: `Application/DestinationConfiguration` (`keep_deleted_days`), `Application/ClientConfiguration` (schema 11).
- The record: `Repository.Format/Manifests/PolicyManifest` (retention inner key 6), `Agent/RecordedRetentionMapping`, and adoption's report in `Agent/ServiceCommandHandler.Adoption.cs`.
- The contract and the service: `Api/Results` and `Api/ContractVersion` (contract 1.62), the upsert in `Agent/ServiceCommandHandler`, and `Agent/DiagnosticBundle`.
- The console: the set editor's retention step and each destination's override take the duration, and the summary says it.
- The tests:
  - `Retention.Tests/DeletedFileHistoryTests` — the rule over facts: both snapshots kept until the duration since the later one's capture, and released together at that instant; no drift once a neighbour expires; a snapshot either side of two deletions carries both reasons; a duration alone deletes and adds nothing; a pair with no answer counts as a deletion; a person's deletion wins; implausible and requested snapshots are nobody's neighbour; each destination's own duration; the report's words in the Gregorian calendar whatever the culture's
  - `Retention.Tests/DeletedFileSurveyTests` — over a real archive, read without the passphrase's authority: an edit and an addition lose nothing; a file deleted at the top, three folders down, from the last part of a folder split across manifests, or replaced by a folder each count; a tree that will not read counts as lost; a dry-run pass keeps the two snapshots with their reasons and lets them go; the gate holds them for a destination whose own duration keeps them, and the direct-ship spare owes them to it and not the snapshot between; a destination converged under its own duration as each pass fans out keeps them and drops the snapshot between
  - `Application.Tests/DeletedFileRetentionConfigurationTests` — the field round-trips at the set and in an override, is written only when declared, is refused at zero or below, and a schema-10 file loads unchanged
  - `Repository.Tests/Format/ManifestCodecTests` — inner key 6 round-trips, a policy with no duration encodes as before, and a seventh key is refused
  - `Api.Tests/ContractAdditiveFieldsTests`, `Api.Tests/ConfigurationContractTests` — the wire name and its pre-1.62 default, a lone zero as the empty policy, and the version pin
  - `Hosts.Tests/ConfigurationCommandTests` — an older client's edit keeps the duration at the set and in an override, and zero clears it
  - `Hosts.Tests/DestinationAdoptionTests` — adoption brings the duration back and says it in words
  - `Hosts.Tests/DiagnosticBundleTests` — the bundle's configuration carries it
  - `Web.DomTests/NewSetWizardDomTests` — the wizard sends it; an existing set's summary says it, and an emptied field, at the set or in an override, sends the zero that clears it

---

## Context

Architecture 07 §2 lists a rule nothing implemented: *keep deleted-file history for a separate, independently configured duration*. Its reason is that people reason about two questions apart. "How far back can I go?" is about snapshot age. "Can I still get the file I deleted last spring?" is about how long the last version of a deleted file survives. Until now a deleted file was kept for exactly as long as some kept snapshot still held it, which the snapshot rules decide. A set keeping seven daily snapshots and nothing else forgets a deleted file a week after it goes. Phase 4's exit criterion *deleted content retained per policy* was unmet for that reason.

Three facts shaped the design.

- **A restore reads snapshots.** Every surface browses a snapshot's tree and restores from it. A file version kept on its own, reachable from no kept snapshot, could not be found by any of them. StagingMark's comment had anticipated walking version lineage to keep such versions, which would have needed a restore path to match.
- **The catalogue is a cache.** What retention keeps never hangs off it (architecture 07 §3). The archive's sealed trees are the authority, and the service holds the keys that read them, write-only sets included.
- **Snapshots expire one pass at a time.** A rule reading neighbouring snapshots sees different neighbours after each pass expires something. A rule that measured from a neighbour that can expire would move its own deadline.

## Decision

1. **The rule.** A set's retention, and a destination's override of it, may declare `keep_deleted_days`. Retention reads the snapshots it already reads in order, newest first: the ones whose capture times can be relied on (FR-GC-012) and that nobody asked to delete (FR-GC-013). For each adjacent pair, when the newer snapshot lost a path the older holds, both are kept until the duration has run since the newer one was captured. The older is the **holder**: it has the last version of what went. The newer **dates** the deletion: it is the first snapshot without it. The dry-run report says `deleted files until 2026-10-21` on the holder and adds `(first without them)` on the other, in the Gregorian calendar whatever the process culture's own calendar is.

2. **The first snapshot without the file is kept too.** Were it let go, the holder's next neighbour would be a later snapshot. The deletion would then read as later each time a neighbour expired, and the holder would never go. Kept, it holds the deletion's date where it was found, and the two are released together.

3. **What losing a path means.** The newer snapshot has nothing at a path the older holds, at any depth, or has something of another kind there: a folder where a file was, or the reverse. Any path counts: a file, a link, a folder, the old name of a rename, a path the rules stopped capturing. Names compare as the bytes recorded, so a rename that changes only case counts. An edit or an addition loses nothing. A subtree whose object is the same on both sides is not read. A tree that will not read or decode counts as lost, and so does a pair the comparison has no answer for, because what they held cannot be known.

4. **It only ever keeps more.** A policy with no rule that expires anything already keeps every snapshot, so a duration alone deletes nothing and adds nothing to the report; it is not a rule for `HasRules`. A person's deletion still wins: a requested snapshot is no one's neighbour, and its neighbours are judged as they will be once it has gone. An implausible snapshot is nobody's neighbour either, since its own flag keeps it. A partial capture missing a file it could not read counts as having lost it.

5. **Whole snapshots, not file versions.** The rule keeps the snapshot holding the last version, and everything in it stays with it. A deleted file's earlier versions are kept only by the snapshot rules. No version manifest outlives every snapshot that reaches it, so version lineage is still never walked (StagingMark's comment now says so).

6. **Computed once, for every plan that reads a policy.** A pass compares the pairs the longest duration in force reads, across the set's own policy and every destination's effective one, and hands one answer to each: the staging selection, each destination's keep-set (the gate's and the direct-ship spare's), each destination's convergence, and the trim's entitlements. A pass that only carries out a person's deletion compares nothing. Trees are named by their content, so a tree read for one pair answers for any other; the comparison carries the trees of the snapshot two pairs share and lets the rest go, holding two snapshots' changed folders rather than the archive's. Without a duration in force nothing is read, and the direct-ship spare's common case still costs nothing.

7. **Where it is declared.** Schema 11 adds the field to a set's retention and a destination's override, written only when declared and refused at zero or below. The policy manifest records the set's own duration as retention inner key 6 of key 13 ([specification 06 §7](../../specifications/repository-format/06-manifests.md)), so a set adopted from its archive keeps deleted files as long as it did; a destination's override is still not recorded (FR-DEST-006). A reader that predates the key refuses a policy manifest carrying it, which is acceptable before the format freeze ([ADR-0014](0014-format-versioning-and-stability.md)), and a policy without one encodes as before. Contract 1.62 adds `keep_deleted_days` to `RetentionPolicyDescriptor`. Null keeps what stands, at the set and in each override, because a client before 1.62 cannot see the field to send it back. Zero clears it, and a descriptor holding only a zero is still the empty "no policy". The console's set editor and each destination's override take the duration and send zero for an emptied field. The adoption report says it in words, and the diagnostic bundle carries it. The set-configuration object [ADR-0071](0071-recovering-operation-after-total-loss.md) proposed, superseded and written by nothing, is left as it is.

## Consequences

- **FR-GC-014 is met, and so is Phase 4's criterion.** A file deleted from the sources stays restorable from the snapshot holding its last version until the declared duration since the deletion was first seen.
- **The promise is at least the duration.** It runs from the first snapshot without the file, so a file deleted just after a backup is kept a little longer than declared, never less.
- **A set that deletes files all the time keeps nearly every snapshot.** Every pair that lost any path keeps both snapshots, so a folder of temporary files or a cache under a set's roots keeps every snapshot inside the duration. An exclude rule for that folder is the remedy, and the dry run says which deletions keep what.
- **A pass reads more when a duration is in force.** It reads each changed folder's trees once for every pair inside the duration, which is no more than the mark already reads for the snapshots it keeps.
- **An older build cannot adopt an archive recording a duration.** It refuses the policy manifest by name rather than misreading it.

## Alternatives considered

- **Reading deletions from the catalogue.** Rejected. The catalogue is a cache, rebuilt from the archive, and what retention keeps never hangs off a cache.
- **Keeping file versions by walking their lineage.** Rejected. A version no kept snapshot reaches cannot be found by any surface, and a restore path to reach them would be a second way of restoring.
- **Keeping only the holder.** Rejected for decision 2's drift: the deadline would move each time a neighbour expired.
- **Measuring from the holder's capture time.** Rejected. The file was still there when the holder was captured, so the promise would fall short of the duration by the gap between the two snapshots.
- **Treating a partial capture's missing paths as no deletion.** Rejected. The comparison cannot tell a file the run could not read from one that was deleted, and the answer that keeps more is the safe one.
- **A deletion journal written at capture time.** Rejected. It would be a new object in the format, archives already written would not have it, and the trees already say what each snapshot holds.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built end to end: the rule in `Retention/RetentionPlanner` over answers `Retention/DeletedFileSurvey` reads from the sealed trees, handed to every plan through `Retention/RetentionRunner`, `Retention/DestinationConvergence` and `Retention/StagingTrim`; the field in `Application/DestinationConfiguration` (schema 11), recorded as policy-manifest retention key 6, carried by contract 1.62 with null keeping what stands, and taken by the console's set editor; [ADR-0061](0061-adopt-a-destinations-archives.md) Amendment 1 and [architecture 07 §2](../architecture/07-retention-and-gc.md#2-retention-policy) amended |

# ADR-0080 — A person deletes a snapshot, from staging and every copy

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-GC-013, FR-GC-008
**Related:** [ADR-0055](0055-reclaim-authority.md) (the reclaim grant, one per set since Amendment 3), [ADR-0009](0009-garbage-collection-safety.md) (the grace a tombstone waits out), [ADR-0011 Amendment 2](0011-commit-versus-replication-semantics.md#amendment-2--retention-must-not-outrun-replication) (the replication gate), [ADR-0034 §6](0034-hub-and-spoke-destinations.md#6-the-costs-accepted) (a copy drops only what staging still lists), [ADR-0059](0059-session-bound-deletion-authority.md) and [ADR-0063](0063-deletion-receipts.md) (the peer instruction and its receipt), [ADR-0078](0078-implausible-capture-times.md) (which kept a misdated snapshot until a person could delete one), [ADR-0022 Decision 6](0022-standalone-metadata-records-and-index-identifiers.md#decision-6--inner-shapes-left-unspecified) (the audit record's parameters), [specification 11 §3.3](../../specifications/repository-format/11-lifecycle-objects.md#33-a-persons-request), [specification 08 §6](../../specifications/repository-format/08-journal.md#6-audit-record), [architecture 07 §2.2](../architecture/07-retention-and-gc.md#22-a-persons-deletion)

**Built:**
- The request and the one rule it does not override: `Retention/SnapshotDeletion`. The reason it writes: `Repository.Format/Manifests/Tombstone`, with a conformance vector for every reason in `specifications/repository-format/conformance/vectors/tombstones.json`.
- Every plan reading it. `Retention/StagingMark` marks the survey, `Retention/RetentionPlanner` expires what was requested, and `Retention/ReplicationGate` holds it for the copies. `Retention/RetentionRunner` and `Retention/StagingSweep` carry out requests alone in the command's passes.
- Every copy converging: `Agent/FanOut` and `Agent/ReplicationInitiator`, with `converged_sequence` in `Application/DestinationSyncStore` (ledger schema 9).
- The command:
  - `delete_snapshots` in `Agent/ServiceCommandHandler`, contract 1.51 (`Api/Commands`, `Api/Results`, `Api/ContractVersion`);
  - who asked, stamped by `Agent/AuthenticatingService`;
  - the audit record, in `Repository.Index/Journal/JournalRecordCodec`;
  - the catalogue forgetting what went, in `Repository.Catalogue/Catalogue`.
- The surfaces: `Cli/CliApplication` and `Cli/OperationGateway`. The console's dialog is in `wwwroot/app.js` and its endpoint in `Web/WebConsoleHost`.
- The tests:
  - `Retention.Tests/SnapshotDeletionPlanTests`
  - `Retention.Tests/SnapshotDeletionCycleTests`
  - `Retention.Tests/SnapshotDeletionFanOutTests`
  - `Retention.Tests/SnapshotDeletionPeerTests`
  - `Retention.Tests/SnapshotDeletionServiceTests`
  - `Repository.Tests/TombstoneCodecTests`, `Repository.ConformanceTests/TombstoneConformanceTests` and `Repository.Tests/JournalTests`
  - `Application.Tests/DestinationSyncStoreTests` and `Repository.Tests/CatalogueReachTests`
  - `Api.Tests/ConfigurationContractTests`, `Hosts.Tests/AuthenticationGateTests`, `Hosts.Tests/ClientModeTests` and `Cli.Tests/CommandTests`
  - `Web.Tests/SnapshotDeletionCeremonyTests` and `Web.DomTests/SnapshotDeletionDomTests`

---

## Context

Retention was the only way anything left an archive. A person who found a snapshot holding what it never should, such as a key file, or a folder of someone else's photographs, could not remove it. Retention keeps what its rules keep. Since ADR-0078 it also keeps a misdated snapshot whatever the rules say, and that record named a deletion verb as the way out and said there was none.

Three facts of the built collector decide the shape of one:

- **A decision taken once is undone by the next plan.** A tombstone is revalidated against a fresh plan before anything is deleted ([specification 11 §3.2](../../specifications/repository-format/11-lifecycle-objects.md#32-what-a-collector-must-do-before-deleting) step 3). A snapshot the policy keeps comes back "protected again" and stays. A person's deletion of such a snapshot has to be a fact every later plan reads, not one pass's decision.
- **A copy keeps what staging no longer lists.** A converge drops at a destination only a key staging still lists, because a key staging no longer lists may be the only copy left of something the trim removed (ADR-0034 §6). Once staging has deleted a snapshot, nothing will ever remove it from a copy that still holds it.
- **Some copies never drop anything.** A destination or peer with no retention rules gets the whole copy and is never converged. A deletion at staging alone would leave every such copy holding the snapshot for ever.

The owner settled four questions before any test was written:

- the deletion reaches staging and every copy, local and peer;
- it overrides the windows, the min-generations floor and the implausible-time flag, and refuses only to leave a set without a complete snapshot;
- the record is a new tombstone reason in the format's closed vocabulary;
- the journal's audit record names the person who asked.

## Decision

### 1. The verb, and what it refuses

`delete_snapshots` (contract 1.51) names one set and the snapshots to delete, by the ids `list_snapshots` gives.

- **Without `apply`** it is a dry run, and needs no grant. It answers with each snapshot named and the destinations a deletion of it waits on.
- **With `apply`** it takes the set's reclaim grant. The grant is derived where the passphrase was typed, and proved against the archive's reclaim public key as an applied retention pass's grants are (ADR-0055 Amendment 3).

Each of these is refused by name before anything is written:

- a set with no archive here, and a snapshot id its archive does not hold;
- a request that would leave the set nothing to restore from:
  - a set that has a complete snapshot keeps one, and a partial capture cannot stand in for it, as the floor's rule says;
  - a set none of whose snapshots is complete keeps its last snapshot of any kind, because a source that always holds a file it cannot read makes every capture partial;
  - a snapshot already requested counts as gone;
- a set whose gate a sync holds. The deletion and the sync must not interleave, and the writer lane must not wait behind a sync that may run for hours;
- an apply without the set's grant, or with one that does not prove out;
- a repository whose credential carries no reclaim public key. Every later pass must verify the request without a grant (§3), and such a credential gives it nothing to verify against.

### 2. The request is a tombstone

The request is a tombstone of the snapshot's manifest with reason 5, *requested* (specification 11 §3.3), signed under the reclaim key like every tombstone. It is the request itself, not a note of one, and it is the only durable record that the snapshot is to go.

- **It names a snapshot manifest and nothing else.** What only the snapshot held is condemned afterwards, as *unreferenced*, by the ordinary analysis. A request that named content directly would let whoever wrote one remove a file from a snapshot that is still kept.
- **It replaces any tombstone already at the manifest's key.** An expiry's *unreferenced* tombstone is a weaker claim about the same object.
- **Its grace is the next publication past it** (ADR-0009, [specification 11 §3.1](../../specifications/repository-format/11-lifecycle-objects.md#31-the-grace-period-is-counted-in-generations-not-in-time)), and the audit record (§6) is that publication.
- **A build older than this one refuses a tombstone of reason 5** and deletes nothing for it. A request made here waits, untouched, through a downgrade.

### 3. Every plan reads it

The survey every plan starts from reads the standing requests and marks each requested snapshot's fact. It needs no grant to do so. It verifies each request against the reclaim public key the write credential carries, so a scheduled sync honours a request without the passphrase. A request that does not verify changes nothing anyone keeps, and the sweep reports it as a security finding, as it reports any tombstone that will not verify.

The planner expires a requested snapshot whatever the windows, the min-generations floor and the implausible-time flag would keep. It takes no floor place and represents no bucket, because it is going, and the yardstick ADR-0078 judges capture times by is drawn only from the snapshots that stand.

Every selection built from the survey reads the same mark:

- each destination's keep-set;
- the replication gate;
- the staging trim;
- the listing.

### 4. Staging lets go only after every copy has

**A destination with no rules converges too.** While a request stands, a local destination or peer with no retention rules converges under a keep-set of everything but the requested snapshots, instead of receiving the whole copy. A destination added after the request is never sent the snapshot.

**The ledger records the converge.** The sync ledger (schema 9) gains `converged_sequence`: the staging journal's head when the last sync that converged the pair began, read before the keep-set is computed. It is monotonic, and zero until a converge has run with a request pending.

**The gate holds the snapshot until every declared destination of the set has converged since the request**, which means its `converged_sequence` has reached the request's generation.

- **A destination with no ledger row counts as never converged.** A missing row is not a missing copy. A renamed destination starts a new row under its new name, and a ledger that could not be read is set aside and starts empty, while the drives themselves keep what they were given.
- **A destination's own policy does not excuse it.**
- **The hold has no deferral bound.** Letting go early leaves a copy holding objects staging no longer lists, which nothing will ever remove (ADR-0034 §6). A destination that never returns holds the deletion until it is removed from the set.

**A peer drops only under the grant.** A scheduled sync to a peer holds no authority to delete (ADR-0055 Amendment 2). While a request stands, it sends everything but the requested snapshots and instructs no drop. The snapshot reaches no peer that lacked it, and the pair is not recorded as converged. The command's own converge (§5) instructs the drop under the grant and files the peer's deletion receipt (ADR-0063).

### 5. One command carries it through

An apply runs on the writer lane, under the set gate, in this order. Each step needs the one before.

1. **The request.** The tombstones are written, and then the audit record (§6), which is the publication their grace waits for.
2. **Every copy converges.** Each declared destination of the set is converged under the grant: a local path by the fan-out's own copy, a peer by its instruction and receipt. The ledger records where each began.
3. **A pass that carries out requests and nothing else.** The set's policy is set aside for this pass, so nothing it would expire is touched: that is a retention run's to do, when someone asks for one. The pass deletes each requested snapshot every copy has let go, and condemns what only those snapshots held.
4. **A second pass, after a pass-closing audit record** (action 3, gc-pass). That record is the publication the condemned content's grace waits for. The second pass therefore removes it in the same command, rather than at a retention run nobody may ever start.

The catalogue forgets a snapshot the command deleted, so nothing lists it, offers to restore it or traces damage to it. A retention run that sweeps a requested snapshot forgets it too.

Each snapshot is answered *deleted*, or *pending* with the destinations it waits on. Asking again finishes a pending one. The request stands, so a second request for the same snapshot is a resume and writes no new tombstone.

### 6. The journal says who asked

The command writes the journal's audit record (specification 08 §6) with action 2, bulk-snapshot-deletion:

- its parameter 1 lists the snapshot ids;
- `objects_affected` is their count;
- `actor` is the account signed in when the request was made, or `cli` where none was.

Who asked is not on the wire. The connection's authentication gate stamps it from the session, so a client cannot claim to be someone else. Journal records are sealed. Specification 11 §4's audit periods, which carry no names, are unchanged.

### 7. The surfaces

- **`list_snapshots`** carries `deletion_pending` on a requested snapshot. It names the destinations the deletion still waits on, and is empty once every copy has let the snapshot go and staging awaits the next pass.
- **`fallbackplan delete-snapshots --set <name or id> <snapshot>... [--apply --passphrase-env VAR]`** runs against a running service, deriving the grant as an applied retention pass does. In direct mode it refuses with directions, because only the hub knows the copies.
- **The console's snapshot rows** gain **Delete…**. The dialog opens on the dry run, then asks for the typed word and the passphrase. It posts to the console's own endpoint, `/api/delete-snapshots`, which derives the one set's grant and sends the service only the sealed envelope.

## Consequences

**Positive**

- A snapshot that should never have been taken can be removed from every copy by the person who has the passphrase, and the journal says who did it.
- A misdated snapshot ADR-0078 keeps indefinitely now has a way out.
- No copy is left holding a deleted snapshot without someone being told which copy it is.

**Negative**

- **An unreachable destination holds the deletion indefinitely**, named in the outcome and the listing, until it is reached or removed from the set. That includes a destination no copy has ever reached, because the ledger cannot vouch that it holds nothing. When it is reached, the converge that clears it is trivial.
- **A deletion that waits is finished by asking again.** The scheduled sync converges a returning destination, but only the command, or a retention run, takes the snapshot out of staging.
- **Older builds refuse the new tombstone reason.** Once a request exists, a build that predates it refuses that tombstone and deletes nothing for it.

**Neutral**

- The ledger's schema moves to 9. A file of schema 8 reads with `converged_sequence` at zero, which holds a request until a converge: the reading that cannot leave a copy behind.

## What this does not do

- **Content another snapshot shares stays.** Deduplication means a file in the deleted snapshot that a kept snapshot also holds is not removed. Only what the deleted snapshots alone held goes.
- **A request cannot be withdrawn.** Once made, it stands until the snapshot is gone.
- **The agent has no verb for it.** `fallbackplan` reaches the service, and the agent's own verbs do not include it.
- **It does not survive key rotation as built.** A request is verified against the credential's reclaim public key, which is generation zero's. Nothing rotates reclaim keys today. A build that does will need the public key per generation.
- **A credential that predates the reclaim key's public half is refused**, rather than given a request no scheduled pass could verify. Re-provisioning gives it one.
- **A retention-expired snapshot keeps its catalogue row**, as it did before. Only a requested snapshot is forgotten when it goes.

## Alternatives considered

**A separate signed request object, apart from the tombstone.** Rejected by the owner. It would be a second durable record of one decision, and every reader of tombstones would still need to know to look for it.

**Deleting the manifest at once, without grace.** Rejected. Specification 11 §3.2 makes the grace a collector's obligation, and a concurrent reader may be walking the snapshot.

**A deferral bound, after which staging lets go of a requested snapshot whatever the copies have done.** Rejected. A copy that returned after the bound would keep the snapshot for ever, because staging would no longer list its keys.

**Letting staging go first, and having each copy drop on the tombstone's word.** Rejected for the same reason. By then the snapshot's own content would be unknown to the copier, and kept as an orphan.

**A ledger fact that no copy was ever written to a destination, so that a destination never reached would not hold a deletion.** Rejected. It would rest on the same row history a rename or a set-aside ledger loses, and a deletion must not let go of a copy on the strength of a record that may have been lost.

**Applying the set's policy in the same command.** Rejected. A person asking to delete one snapshot should not find that the command expired others too.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted | Built in one slice, tests first: the reason and its vectors, the survey's mark and the planner's override, the gate's hold and the ledger's `converged_sequence`, the converge of every copy, the command with its two passes and its audit record, contract 1.51, the CLI verb and the console's dialog. |

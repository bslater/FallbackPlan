# ADR-0047 — Recovering operation after total loss: the repository carries the set's shape, sealed

**Status:** Proposed
**Date:** 2026-08
**Requirements:** FR-DR-006, FR-DR-007, FR-DR-008, FR-DR-009, FR-DEST-006, NFR-SEC-009
**Related:** [ADR-0009](0009-garbage-collection-safety.md), [ADR-0013](0013-recovery-kit.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0040](0040-multi-root-backup-sets.md), [ADR-0042](0042-write-only-repositories.md), [ADR-0046](0046-replica-claim-after-total-loss.md), [format 11 §5](../../specifications/repository-format/11-lifecycle-objects.md#5-set-configuration-object)

---

## Context

[ADR-0046](0046-replica-claim-after-total-loss.md) lets a machine rebuilt from bare
metal reach a peer-held replica and read its history back. It stops exactly
there, and says so: *"A claim recovers **data**. It does not recover
**operation**."*

That leaves the user in the position
[architecture 08 §6](../architecture/08-restore-and-recovery.md#6-what-must-survive-a-clean-machine)
already names as worse than ignorance — files restored, protection gone, and
nothing on screen distinguishing the two. Their machine can read every version
of every file it ever kept and cannot tell itself what it was supposed to be
backing up tomorrow.

The gap is narrow and specific. Tracing what survives a rebuild:

| Fact | Where it survives |
|------|-------------------|
| Backup set ids | The replica's snapshot manifests; the claim already answers them (FR-DR-004) |
| Capture rules, segmentation, compression, dedup domain | The policy manifest, keys 1–9 |
| Root **labels** | The snapshot tree's top-level names — [ADR-0040](0040-multi-root-backup-sets.md) persists them and never derives them on read |
| Repository id, KDF parameters | The recovery kit, and the replica's own descriptor |
| Destination addresses | The recovery kit ([ADR-0013](0013-recovery-kit.md)) |
| Root **paths**, schedule, retention policy, set name | **Nowhere.** `config.json`, which died with the machine |

The last row is the whole problem. A schedule and a retention policy are not
recoverable by inference, and asking a person to recall the retention policy
that governs deletion of their own history is asking them to guess at
something consequential.

## Decision

1. **The repository carries the set's operational shape**, in a
   **set-configuration object** — object type `0x10`, at
   `/config/<backup-set-id>/<recorded-at>/<config-id>`, specified in
   [format 11 §5](../../specifications/repository-format/11-lifecycle-objects.md#5-set-configuration-object).
   It records the set name, its root labels **and the paths they had**, the
   include and exclude rules, the schedule, and the retention policy.

2. **Its payload is sealed to an asymmetric recipient, so only the passphrase
   opens it.** The record's outer framing is an ordinary standalone metadata
   record, so a writer can locate, order and replace these objects during
   normal operation. The configuration inside is sealed again.

   The second layer is the load-bearing one. A format-v2 repository grants its
   own service the entire structure plane by design
   ([ADR-0042](0042-write-only-repositories.md)), so a single-layer record
   would hand a compromised write-only hub the user's folder layout, schedule
   and rules — the exact class of thing v2 exists to withhold. Sealed, the hub
   writes an envelope it can never open, precisely as it already does for file
   contents.

   The recipient key exists in both formats already, so nothing new is
   distributed: v2 seals to the descriptor's `fbp/seal/v2` public key; v1
   derives an X25519 recipient from the master key under `"fbp/recovery/v1"`
   ([format 03 §4](../../specifications/repository-format/03-keys.md#4-derived-keys)).
   A recovering v1 device walks passphrase → KEK → `/keys/` → master key, and
   `/keys/` is servable within an authorised replica
   ([peer-protocol 07 §4](../../specifications/peer-protocol/07-retrieval.md#4-authorization)).

3. **Destinations are excluded, and FR-DEST-006 is not amended.** No name, no
   kind, no path, no endpoint, no fingerprint, no quota.

   Sealing defeats today's reader, and that is not sufficient reason to relax
   this. [ADR-0034 §5](0034-hub-and-spoke-destinations.md) keeps the
   destination list local as a privacy statement — the configuration *"names
   who stores your backups and where"* — and the repository is held **by** the
   very destinations it would name. Defence in depth is the point: a future
   weakness in the sealing scheme, or a compromised passphrase, must not
   additionally surrender the household's network of peers from repository
   bytes alone. Destinations come from the recovery kit, which the user holds
   and no peer does.

4. **It is a lifecycle object, not a snapshot — and the alternative is
   actively unsafe.** The natural implementation is to publish a small
   snapshot carrying the new configuration whenever it changes. Retention
   selects by bucketing snapshots in time and keeping the newest per bucket
   ([architecture 07 §2](../architecture/07-retention-and-gc.md#2-retention-policy)),
   and a snapshot fact carries no kind. **A configuration snapshot published
   later the same day would be the newest in that day's bucket, and would
   expire the day's real backup.**

   Making that safe means teaching a new exception to the one component whose
   bugs delete data. A separate namespace avoids the interaction rather than
   surviving it. It is also the more honest model: a snapshot is a
   point-in-time claim about data, and a configuration edit looked at no data.

5. **It is written on publication and on configuration change.** A schedule
   edited between backups is otherwise lost with the machine, which would
   leave the recovered value stale in exactly the cases where someone had
   recently thought about it.

6. **The newest object per set is a collection root.** Nothing references
   these objects, so a reachability walk alone would collect all of them and
   silently disarm recovery of operation — invisibly, because nothing else
   reads them during ordinary running. Recorded as
   [ADR-0009 Amendment 6](0009-garbage-collection-safety.md).

7. **It is signed, and the signature has a stated adversary.** Ed25519 under
   the repository signing key, the same construction as a snapshot manifest.
   A configuration object names a retention policy and a retention policy
   names what gets deleted, so a machine adopting a forged one could be
   induced to age away its own history. The realistic adversary is **the
   destination holding the replica**, which has no repository key at all and
   therefore cannot forge one.

   It does not defend against a compromised repository member, which holds the
   signing key by construction — for v2 the signing sub-root is inside the
   write bundle. That residue is answered by procedure: the recovered
   configuration, the retention policy above all, is presented for
   confirmation before it takes effect (decision 9).

8. **Staging re-adopts the same repository identity.** The recovered hub does
   not create a new repository. It pulls the descriptor and `/keys/` back from
   the claimed replica, writes them to a fresh staging path, and opens it
   normally. Three existing properties make this cheap rather than heroic:
   empty staging is already a supported state
   ([ADR-0034 §6](0034-hub-and-spoke-destinations.md)); fan-out already sends
   only what the destination's inventory lacks, so history does not re-cross
   the wire; and `LocalState` already mints a fresh `writer_id` when its state
   file is absent, making the recovered machine a new writer with a clean
   gapless sequence space — which is what [T-18](../threat-model.md#t-18-writer-identity-cloning)
   wants, and the opposite of re-using an id whose sequence file was lost.

9. **The reconstruction is guided, and refuses to guess paths.** The recovered
   configuration is presented for confirmation, pre-filled, with each root's
   old path shown and flagged where it does not exist here. The path is a
   hint, never an instruction: the new machine's layout may legitimately
   differ, and silently capturing the wrong tree under a name that says
   otherwise is worse than asking.

## Consequences

- **Recovery of operation becomes a supported path**, and architecture 08 §7.4
  changes from a statement of what is not recovered into a description of what
  is. What still needs a human is bounded and stated: confirming paths, and
  supplying destinations from the kit.
- **A user who has lost their kit as well as their machine recovers data but
  not destinations.** They can restore from any peer that still holds them and
  can be re-paired; they cannot enumerate where their other copies live. This
  is the accepted cost of decision 3, and it is the reason FR-KIT-004 makes
  kit generation a gate on setup completing.
- **The first backup after recovery may re-upload everything, and only for
  v2.** A fresh `writer_id` means previously written segments belong to
  another writer. Under the default `repository` trust domain reuse still
  works; under `device` — which [ADR-0042](0042-write-only-repositories.md)
  forces for write-only repositories, because they cannot read another
  writer's segments to verify reuse — the whole source is re-uploaded. On a
  domestic uplink that is days, and it belongs in the recovery summary the
  user reads, not in a footnote. It joins the other v2 disaster-recovery
  question in [Q23](../open-questions.md#q23--arming-disaster-recovery-on-a-write-only-repository).
- **Every publication writes one more small object**, and every configuration
  edit writes one outside a publication — new traffic on a path that
  previously moved only when data changed. The object is a few hundred bytes;
  the fan-out cost is one object per edit.
- **The collector gains a rule it must not get wrong.** A stated root is
  simpler than a reachability inference, but it is still a rule whose failure
  is silent until a disaster, which is why it is written into ADR-0009 rather
  than left to the collector's implementation.
- **Repositories written before this revision have no configuration object.**
  A reader tolerates their absence; such a set recovers data and reconstructs
  operation by hand. Nothing migrates silently.

## Alternatives considered

**Extend the policy manifest instead of adding an object type.** Genuinely
attractive, and the first design: the policy manifest already exists to answer
*"what settings produced this?"* — its own doc comment says *"years after the
configuration file is gone"* — it is already written on every publication, and
it is already covered transitively by the snapshot signature. Rejected for two
reasons. It is a metadata-class record, so a write-only hub reads it, which
defeats decision 2 outright. And it is written *per snapshot*, so a
configuration edit between backups could only be captured by publishing a
snapshot, which decision 4 shows to be unsafe.

**Put the operational configuration in the recovery kit.** No repository
change, no per-snapshot cost, and the kit already carries destinations.
Rejected because the kit goes stale the moment a schedule or a rule changes —
FR-KIT-005 has a staleness concept for exactly this reason — and a printed
page is the worst possible home for something that changes. The split that
survives is by *volatility and by holder*: the kit carries what is stable and
belongs to the user (destinations), the repository carries what changes and
describes the source.

**Include destinations, since the envelope is sealed.** Considered seriously,
because it would let a user who lost their kit rediscover where their copies
live — a real recovery benefit. Rejected on defence in depth, per decision 3.
The asymmetry is deliberate: losing the kit costs knowledge of your peers;
relaxing this would mean a single passphrase compromise reveals them.

**Publish a tagged snapshot and teach retention to skip it.** Smaller
specification surface, larger blast radius: it puts new logic in the code path
that decides what gets deleted, to solve a problem a separate namespace does
not have.

**Re-materialise the full staging archive from the peer.** Byte-for-byte
continuity, and local restores work immediately. Rejected: it transfers the
entire backup before the first new backup can run — potentially days — for
data the peer already holds safely, and ADR-0034 §6 already accepts a trimmed
staging archive as a normal state.

**Start a new repository for the recovered set.** Simplest to build. Rejected:
the peer would hold two repositories for one set, history would split across
them, restores would have to know which era they wanted, and quota would be
consumed twice.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written as the second half of [ADR-0046](0046-replica-claim-after-total-loss.md), which recovered data and left operation unrecovered by its own admission. Decision fixed by the user: the metadata is stored at the destination sealed to a public key, undecryptable without the private half, and the recovered instance pulls it and opens it with the full passphrase. Destinations held back from the envelope, and the configuration-change publication kept out of the snapshot namespace after the retention-bucket hazard was found |

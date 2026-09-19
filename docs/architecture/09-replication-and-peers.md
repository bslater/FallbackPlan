# 09 — Replication and peers

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §8.3–8.4, §16.2 · **Resolves:** [H6](../review/2026-08-architecture-review.md#h6--independently-verified-trusts-the-destination-to-report-on-itself), [C5](../review/2026-08-architecture-review.md#c5--snapshot-commit-is-defined-so-that-one-offline-destination-stalls-all-protection)

**Built:** A transfer cut inside an object resumes where it stopped (§1.2, [ADR-0057](../adr/0057-resumable-object-transfer.md)). What a pass costs is bounded by what changed (§1.1, [ADR-0056](../adr/0056-incremental-reconciliation.md)): phase-scoped listings, a gate that skips a pair the last pass left level, and a reading-through that comes due on its own cadence. Identity, pairing and the session layer built and carried over a real TLS socket; the object exchange (§1) built for the whole-repository scope ([peer-protocol 03](../../specifications/peer-protocol/03-replication.md)); quotas and their distinct exhaustion reporting (§6) built ([peer-protocol 05](../../specifications/peer-protocol/05-quotas.md)); destination verification (§5) built ([peer-protocol 04](../../specifications/peer-protocol/04-verification.md)): every sync challenges a bounded sample with the newest snapshot always included, local-path replicas answer to direct read-back, and a failed proof is a durable finding, and for a direct-ship set the challenge's ground truth and the verifier's reads run **through the ship sink** against destination-held objects ([ADR-0046](../adr/0046-direct-to-destination-publication.md)); the direct write path of §4.1 is built as the default for new local-path sets (`direct_ship`, contract 1.23) and serves peer destinations too since the write adapter of §4.2 ([ADR-0058](../adr/0058-peer-write-adapter.md)), which a peer-only set may choose and does not get by default; a retention instruction is signed over the session it is sent in and required by any spoke holding the repository's reclaim key, never by a negotiated feature ([ADR-0059](../adr/0059-session-bound-deletion-authority.md)); and a rebuilt machine that has claimed its replica adopts it back under its original ids over the retrieval session, the owner inventory naming what it may adopt ([ADR-0061](../adr/0061-adopt-a-destinations-archives.md)); and a destination is the witness for a whole state directory rolled back — a local path by listing its journal head for this writer, a peer from the inventory every push already declares before it filters or drops anything — the sequence moved past it, nothing deleted there, and the set healed from it — a direct-ship set's metadata and catalogue, a staging set's content as well, bounded by the history it lacks — over the retrieval session for a peer, one chunk at a time ([ADR-0062](../adr/0062-the-destination-is-the-rollback-witness.md) and its Amendments 1 and 2); a write-only set's sealed data plane is proved at a destination by the digest tier, and the ledger says which tier proved what (contract 1.32); a peer is drilled on a cadence its source's operator states, under a byte cap ([ADR-0054 Amendment 3](../adr/0054-scheduled-restore-drills.md#amendment-3--a-peer-is-drilled-on-a-stated-cadence-and-under-a-byte-cap-2026-09)); and a push ends with the peer's signed replication receipt — what it committed and what it holds afterwards — verified by the commander, filed by both, and the one record on which the ledger counts a peer complete, as the peer's attestation and never as possession ([ADR-0064](../adr/0064-replication-receipts.md)); and a format-3 set's sealed blobs at a peer are proved by **one leaf** of the Merkle commitment its index publishes, checked against the signed root, instead of the blob crossing the wire — the leaf's bytes being what a cached path cannot supply ([ADR-0065](../adr/0065-merkle-commitment-and-chunk-possession.md), contract 1.34) — see [implementation status](../implementation-status.md).

---

## 1. What replication moves

Peers exchange immutable repository objects — blobs, manifests, snapshots, and index generations. They never reconcile live folders, and there is no notion of a single "current" global file state. This is the distinction from a file synchroniser set out in [`00-overview.md` §5.2](00-overview.md#52-peer-synchronisation-protocols).

Scope note ([ADR-0046](../adr/0046-direct-to-destination-publication.md)):
this peer exchange is the write path for **staging sets'** peer destinations
and the catch-up path generally. A **direct-ship** set's captures write
through the ship sink (§4.1), which does not yet serve peer destinations —
a peer on a direct-ship set is a stated `NotSupported` in the sync ledger,
never a silent skip, until the peer write adapter lands.

Exchange sequence:

1. repository identity and compatible format capabilities;
2. authorised snapshot scopes;
3. generation summaries and compact object-set filters;
4. missing snapshot declarations;
5. missing index generations;
6. missing blobs or blob ranges;
7. verification receipts.

Steps 3–6 are ordered so the cheapest discovery happens first: a filter exchange establishes most of what is missing without enumerating anything.

**Built so far (peer-protocol 03, first slice).** The exchange runs for the widest scope — the source offers a whole repository, the destination declares the object keys it holds as an explicit inventory, and the source streams the rest, each object committed whole under a create-if-absent write so a re-run resumes with no checkpoint. Three decisions this section left open were settled there rather than in the architecture, because they are encoding and placement, not behaviour: the destination keeps each source's replica in a store it names locally by repository id (a storage path never crosses the wire, §3); an object commits atomically, so resumption is a property of the exchange rather than a negotiated position; and step 3's compact object-set filter is an *optional negotiated feature* layered over the explicit inventory, so a v1 implementation is complete without it. Snapshot scoping (steps 2, 4, 5) and the filter are a later slice; quotas (§6) are built per [peer-protocol 05](../../specifications/peer-protocol/05-quotas.md); verification (§5) follows.

### 1.1 What a pass costs

A pass is a diff, and until [ADR-0056](../adr/0056-incremental-reconciliation.md)
it re-derived the diff from scratch every time: the source's whole namespace
listed once per dependency phase, the destination's whole inventory held in
memory beside it. That cost the archive's object count multiplied by the number
of dependency classes, whether or not anything had changed, which on the poll
cadence is the wrong constant to be multiplying by anything.

Three rules now bound it, and the order matters because each one only makes
sense given the one before:

1. **A phase lists under its own prefix.** A dependency phase *is* a prefix,
   and the destination's inventory for a phase is released when the phase ends
   — so a pass's resident key set is the largest phase rather than the
   archive.
2. **A pass decides before it reads.** The sync ledger records the publication
   sequence a destination provably holds and when its inventory was last read
   through. A pair with nothing published since, an unmoved keep-set and an
   unexpired reading-through is answered from those records: the pass carries
   nothing and lists nothing.
3. **A reading-through comes due.** The watermark describes what the *source*
   published and can say nothing about what the destination still holds, so
   the skip has a shelf life — a day — after which both inventories are read
   through whether or not anything has happened.

Rule 3 is the one that keeps rule 2 honest, and it is not alone: destination
verification (§5) reads bytes at the destination on its own cadence and the
scheduled drill ([ADR-0054](../adr/0054-scheduled-restore-drills.md)) restores
a file from it. A pair that fails either stops claiming to be level, and a pair
that is not level is never skipped.

### 1.2 What an interruption costs

A cut transfer costs its tail, not the object
([ADR-0057](../adr/0057-resumable-object-transfer.md)). The destination stages
an incoming object outside its replica store, keyed by the object it belongs
to; if the link dies the bytes stay, and on the next session the destination
declares what it part holds along with a digest of exactly those bytes.

The **source** decides whether to begin there. It hashes its own prefix of the
same object and compares; on a match it sends the remainder, and on any
disagreement it sends the object whole. The destination cannot make that check
itself — it holds no repository keys, and a store key is a keyed rendering of
an identifier rather than of the bytes — so the side that has the object is
the side that decides, and a peer cannot talk a source into skipping bytes it
has not proved it holds.

Everything §1's atomicity rests on is unchanged: the destination still commits
whole or not at all, under a create-if-absent write, and staged bytes are in no
store, answer no read, appear in no inventory and cannot satisfy a possession
challenge (§5). They are counted against the peer's quota, because they are
real disk it is costing its host, and swept when nothing can resume them.

A local-path destination has none of this and needs none: a copy cut there
re-reads local disk, where the restart costs seconds.

## 2. Transport

Specified in [`specifications/peer-protocol/02`](../../specifications/peer-protocol/02-session.md).

Direct QUIC or TLS · mutually authenticated peer identity · relay fallback where the relay cannot decrypt · restart from verified ranges · dynamic concurrency · bandwidth schedules · source selection across multiple replicas · end-to-end content verification · optional local-network preference · fairness between backup sets.

Fairness matters more than it sounds: without it, one large backup set starves every other set on the same link indefinitely, and the user sees a set that has simply stopped making progress with no explanation.

**TLS carries the session; it does not establish who is on it.** Both sides present a certificate generated for that connection and discarded with it, and neither makes a trust decision about it. Identity is proved inside the protocol, by each side signing a transcript that binds its pinned Ed25519 key to the certificates this connection actually used — so a man in the middle, who must terminate TLS with certificates of its own, cannot produce or relay a proof that verifies. The original design put this in the transport with RFC 7250 raw public keys; that is unreachable on the platform, and [ADR-0030 Amendment 1](../adr/0030-peer-identity-and-pairing.md#amendment-1-2026-08--authentication-moves-out-of-tls) records the move and why the guarantee survives it.

A consequence worth stating where an architect will look for it: **a completed handshake is not an authenticated peer**, and the session's states are named so that no code can quietly assume otherwise ([02 §2](../../specifications/peer-protocol/02-session.md#2-session-states)).

### 2.1 Version skew

Paired peers may run different agent versions. The protocol negotiates a common feature set at connection time; a peer that cannot satisfy the other's **required** features refuses the connection with a clear reason rather than proceeding into an undefined state mid-transfer (NFR-COMP-006).

Repository format compatibility is negotiated separately from protocol compatibility, because they version independently ([ADR-0014](../adr/0014-format-versioning-and-stability.md)).

## 3. Pairing

Specified in [`specifications/peer-protocol/01`](../../specifications/peer-protocol/01-identity-and-pairing.md); the decisions behind it in [ADR-0030](../adr/0030-peer-identity-and-pairing.md). The properties this section fixes are the ones that specification is written to satisfy:

- Both sides see the same short authentication string — six base32 characters, per [01 §2.3](../../specifications/peer-protocol/01-identity-and-pairing.md) — and both must approve. An earlier draft specified words from a wordlist and shipped no wordlist; the characters say what they mean and need nothing carried.
- Identity is pinned on approval; a changed identity is a hard failure requiring explicit re-approval, not a prompt that can be clicked through.
- Direct connection is negotiated first; relay is a fallback and is reported as such.
- The **destination** sets quota, storage path, schedule window, and retention floor. These are its terms: a source may operate under narrower ones of its own choosing and can never ask for more generous. The storage path is deliberately not on the wire at all — a source that knew it would be a source that could name it.
- A source never receives unrestricted filesystem access to a destination — it speaks the repository protocol ([`05-storage-providers.md` §4.2](05-storage-providers.md#42-fallbackplan-peer)).
- A destination cannot read source content. Holding blobs conveys no ability to decrypt them.
- **The direction of storage is part of what is approved.** The ceremony's offer proposes which side stores for which — one way, the other, or both — and the acceptance confirms it inside the authenticated transcript, so the two grants cannot disagree about who lends the disk ([ADR-0030 Amendment 2](../adr/0030-peer-identity-and-pairing.md#amendment-2-2026-08--the-pairing-lifecycle-completes-roles-on-the-wire-endings-announced-terms-enforced)).

That pair of properties above — protocol-only access, ciphertext-only holding — is what makes "back up to a friend's computer" a reasonable thing to ask of a friend. Neither party has to trust the other with anything.

### 3.1 Ending a peering

Either side may end a peering unilaterally, at any time, for any reason — revocation is a local act and no protocol round-trip is ever a precondition for it ([peer-protocol 01 §3](../../specifications/peer-protocol/01-identity-and-pairing.md)). What the ending must not be is *silent*. The ender sends a best-effort **termination notice** when the peer is reachable (feature-gated, so an older peer is simply not sent what it cannot parse); a peer that was unreachable learns the same thing from the `Revoked` refusal at its next dial. Both paths produce a **durable notice** the user sees until acknowledged: the hub that lost a destination is told to reconfigure the sets that counted on it, and the spoke left holding a departed hub's ciphertext is told the data is now its own to evict, after a stated grace period. Eviction is the storing side's own decision on its own timetable — the notice creates awareness, never an obligation.

## 4. Durability policy

A backup set declares one or more named destinations, and its policy is evaluated over per-destination replication state ([`04-concurrency-and-publication.md` §6](04-concurrency-and-publication.md#6-commit-versus-replication), [ADR-0034](../adr/0034-hub-and-spoke-destinations.md)). **None of the destinations has to be local.** For a staging set, publication lands in the set's staging archive on the hub — that is what makes its capture unconditional — but staging is a cache the hub manages, not a destination a policy may count. For a direct-ship set (§4.1) publication lands at the destinations themselves, and "captured" means committed to at least one:

```text
Snapshot captured when:
  - staging set:     committed to the set's staging archive
  - direct-ship set: committed to at least one destination

Snapshot protected when:
  - at least one destination outside the source's failure domain: durable

Snapshot policy-compliant when:
  - every destination the set's policy requires: durable

Snapshot healthy when:
  - a local-path destination: verified within 7 days,  and
  - a peer destination:       verified within 30 days, and
  - a cloud destination:      durable within 24 hours (reserved; no cloud kind is served)
```

The verification bounds are no longer an illustration. They are the values the
status derivation compares each destination's proof against
([ADR-0035 §7](../adr/0035-destination-fitness.md)), and the local bound is
chosen against the deep sweep's weekly default cadence rather than in the
abstract — a bound shorter than the cadence meant to satisfy it would be
permanently unmet.

Passing the bound produces a **warning and no change of state**. An old proof
is still a proof, and demoting a set over its age would say the data is at risk
when what is true is that nobody has looked lately. The warning is worth
reading because it fires only where nothing else is already complaining: an
unproven, sequence-stale or knowingly-unprovable destination each earns its own
line naming that situation. It is checked for every destination, including
those inside the source's failure domain that can never earn `protected` — a
local path's proof is what licenses reclaiming a last copy (the staging trim
for staging sets, per-destination convergence for direct-ship ones), so an
overdue proof there quietly stops space being reclaimed.

Two other fitness facts join the policy at the same place, on the same terms —
reported, never enforced by refusing a configuration
([ADR-0035](../adr/0035-destination-fitness.md)):

- **Admission.** A destination's declared address is checked for the defects
  findable without touching the world, and can be probed on demand to confirm
  it would accept a backup before one has ever been sent there.
- **Capacity.** A quota-bound peer reports its remaining headroom, and a source
  warns below a tenth of the loan. A local copy does not begin when the
  destination volume is under the floor the hub leaves for the machine that
  owns it.

For a staging set, because commit is to staging and replication is per destination, a destination that is offline delays *policy compliance* without blocking *capture*; the status display can say "captured, waiting on the offsite copy" — a true statement the original design could not make, because it would have had no snapshot to report at all. For a direct-ship set one offline destination still does not block a run — the run writes to the reachable ones and catch-up heals the rest — but with **no** reachable in-scope destination the capture refuses as a stated recoverable failure: [ADR-0046 §4](../adr/0046-direct-to-destination-publication.md) consciously gives up capture-unconditionally in exchange for holding no local copy.

`protected` deliberately requires a destination outside the source's failure domain, so that a repository directory sharing a disk with the source data never reads as safe — and the staging archive, which shares the source's domain by construction, never counts at all (a direct-ship set holds no such archive; its same-domain *destinations* are what can never earn `protected`) ([ADR-0018 Amendment 1](../adr/0018-replica-failure-domains.md#amendment-1-2026-08--the-domain-is-declared-per-configured-destination)). Domains and rationale in [`04-concurrency-and-publication.md` §6.4](04-concurrency-and-publication.md#64-protected-requires-an-independent-failure-domain).

**The destination is also the rollback witness** ([ADR-0062](../adr/0062-the-destination-is-the-rollback-witness.md)). A direct-ship set's metadata plane lives in the state directory, so a state directory restored from an older copy rolls the catalogue, the sequence file, the sync ledger and the metadata store back together and no local witness can notice. The destination did not roll back, and its journal keys carry the writer's sequence in the clear. Every fan-out pass to a local-path destination reads that head for this writer and offers it to the writer sequence, which only ever rises: a destination attesting a number the writer has not yet allocated is a rollback of the allocation state and nothing else. The detecting pass computes no keep-set, so it converges nothing and deletes nothing at the destination — the harm it exists to prevent is a pass that trims the destination to the keep-set its rolled-back metadata can see — raises a notice that is never auto-resolved, and for a direct-ship set copies the destination's metadata back, rebuilds the catalogue in place and moves the writer past what the healed archive attests. A staging set is healed too — the content its missing snapshots need and their metadata, bounded by that closure — and a peer is witnessed from the inventory every push declares and healed over the retrieval session ([Amendment 1](../adr/0062-the-destination-is-the-rollback-witness.md#amendment-1--the-peer-is-a-witness-too-from-the-inventory-it-already-declares-2026-09), [Amendment 2](../adr/0062-the-destination-is-the-rollback-witness.md#amendment-2--a-staging-set-is-healed-too-bounded-by-the-history-it-lacks-2026-09)).

### 4.1 The direct write path

A set flagged `direct_ship` ([ADR-0046](../adr/0046-direct-to-destination-publication.md))
publishes through the **ship sink** — an `IObjectStore` the publication
pipeline writes exactly as it wrote the staging store, with routing by key:
`blobs/` objects go to the set's in-scope destinations and never to local
disk; every other object (descriptor, keys, journal, index, snapshots,
hints) goes to the local **metadata store** (`<state>/sets/<setId>/`) *and*
the destinations. Each destination therefore holds a whole, independently
restorable repository — ADR-0034 §2's invariant, kept without the staging
archive. Reads route to whoever holds the bytes: metadata answers locally; a
blob read is answered by the first destination holding the key in
**priority order**; a blob listing is the **union** across destinations.
The union is sufficient because a capture refuses to run with no reachable
destination, so every committed snapshot's closure exists at at least one
destination — which is also what lets a sibling seed a destination that
missed a run.

**Run scope.** A run writes to the set's defect-free, reachable destinations
that hold a **baseline** — or all reachable ones when the set
has never captured, because that first capture is every destination's full
backup. A baseline-less destination on a set with history is *skipped* by
the run (an incremental would hand it a snapshot without its closure) and
**seeded by catch-up instead**: the existing exchange, running through the
sink, copying from whichever sibling holds each object. The sync ledger
(schema 2, [ADR-0047 §6](../adr/0047-backup-pool-and-priorities.md)) carries
the facts this turns on — `baseline_completed_at`, `needs_full`,
`baseline_snapshot_id`, `last_reconciled_at` — and contract 1.19 surfaces
the first two on every status row.

**Failure mid-run.** A destination that fails during a run is dropped and
named in the ledger and the log (event 3758); its replica is
lagging-but-valid — a journal intent nothing retired, exactly an
interrupted copy's state — healed by the next catch-up. Only the last
destination failing fails the run, through the pipeline's ordinary
interruption safety. Fan-out and this section's peer exchange survive as
the catch-up and seeding pump; they are no longer the write path for these
sets.

### 4.2 Shipping to a peer

A peer destination is a shipment like any other
([ADR-0058](../adr/0058-peer-write-adapter.md)): `PeerShipStore` presents one
live replication push session (§1) as an `IObjectStore`. The offer and the
destination's inventory are exchanged when the run resolves its targets, each
put writes a `ReplicationObject` and its chunks, and the run's books close
with the completion and its acknowledgement — whose count must equal what the
run sent, or the destination is recorded failed however the run itself ended.
The acknowledgement also carries the peer's replication receipt
([ADR-0064](../adr/0064-replication-receipts.md)), and the run verifies it
through the same seam the sync pass uses, files its copy and counts the pair
on its strength. That the run does it rather than waiting for a later pass is
the point: a source cannot cheaply list a peer's replica, so the
acknowledgement closing the run is the only measurement that shipment gets,
and it is worth most now — the capture has just arrived. A rejected receipt is
a notice and never a refusal; a peer that sends none leaves the pair uncounted,
as every peer was before receipts.
The inventory answers "already there" without touching the wire; every wire
failure reaches the sink as an `IOException`, so a peer is dropped, named and
healed by exactly the rule a full disk is.

The session is held open rather than the run being spooled and pushed
afterwards, because a spool of the run is a local copy of the backup, which is
what the direct write path exists to remove. It deliberately does **not**
negotiate `partial-object-resume` (§1.2): a run holds no object it could
resume, so a prefix staged for it would be charged to the peer's quota for a
week awaiting a second half that never comes.

Reads travel a second retrieval session (§7 of
[peer-protocol 07](../../specifications/peer-protocol/07-retrieval.md)),
dialled only when bytes are actually wanted, and a key the inventory never
listed is answered absent without dialling. Outside a run the sink does not
resolve peers at all; a peer's replica is read back on the restore-source
path. **A set whose content lives only at a peer therefore has no independent
copy to verify against, and the pass says so rather than stamping a proof
drawn from the metadata it happens to keep locally** — see §5.4.

## 5. Destination verification

### 5.1 The problem with asking

"When was the last independently verified recoverable snapshot?" is the product's central promise ([`00-overview.md` §2](00-overview.md#2-product-promise)). It is also the one status a destination can fabricate for free.

A peer that lost the data to a failed disk, deleted it to reclaim space, or is running buggy software can answer "verified" while holding nothing. The obvious implementations do not help: asking it to hash a blob lets it cache the answer from the first challenge and reuse it forever; asking for the whole blob back defeats the purpose of not transferring it.

### 5.2 Keyed random-range challenge

The verifier selects a blob, a random byte range within it, and a fresh nonce, then requests:

```text
response = MAC(challenge_key, nonce ‖ blob_id ‖ range ‖ bytes_at_range)
```

The verifier recomputes the expected value from its own copy, or from another replica holding the same blob.

Because the nonce is fresh and the range unpredictable, the response cannot be precomputed, cached, or replayed. Producing it requires actually holding those bytes at that moment. The bandwidth cost is one MAC per challenge rather than one blob.

### 5.3 Sampling policy

Full verification of every blob on every cycle is prohibitive; verifying nothing is what we are fixing. The policy is therefore a coverage-versus-cost trade with the trade made visible:

- a bounded random sample per verification interval;
- weighted towards blobs longest since their last successful challenge;
- always covering the objects a *recent* snapshot depends on, so the newest recovery point is the best-verified one;
- full verification available on demand and before a recovery drill.

Status reports **coverage and challenge age**, not a boolean. "Verified" with no indication of how much was checked or how long ago is the kind of green light §23 of the original proposal warned about under "consumer UI hides degraded state".

### 5.4 What this does not prove

A challenge proves the destination holds those bytes **now**. It does not prove it will return them when asked to restore — a destination can pass every challenge and then refuse or fail at restore time. Nothing short of an actual restore proves that, which is why recovery drills exist ([`08-restore-and-recovery.md` §4.4](08-restore-and-recovery.md#44-lifecycle)).

It also cannot be issued at all without an independent copy to judge the answer against. The expected proof is computed from this side's own bytes, so a direct-ship set whose only destination is a peer (§4.2) can challenge its metadata plane and nothing else — and a stamp drawn from that population would report a proven replica while the part a restore needs went unexamined. Such a pass therefore challenges nothing and **reads the replica back instead**: it opens a sample of the blobs the spoke declared holding, over the retrieval session of §4.2, and authenticates a record inside each under the repository's own key — which the peer has never held, so no second copy is involved. The sample comes from the spoke's own declaration, which is a closed loop rather than the examined party choosing the questions: that declaration is also the push's diff, so a key omitted to avoid being asked about is a key the same session re-ships. A peer that will not serve the read-back leaves the content unchecked, and the pass says so durably rather than stamping anything ([ADR-0058](../adr/0058-peer-write-adapter.md) §8).

The tag proof stops at the container on a write-only set, whose records are sealed to a key the service does not hold. Those are proved by the **digest tier**: the whole blob short of its locator hashed at the destination and compared with the digest the writer signed into the index ([07 §2.2](../../specifications/repository-format/07-index.md)), which a catalogue rebuilt from the index plane alone still holds. The tier reads whole blobs, so it is bounded in bytes per pass and a blob above the budget is left unproved and said rather than looped; the ledger and the status matrix carry how many objects each tier proved (contract 1.32). Over a peer, a format-2 blob is read back the same way, costing the peer's link the blob, under a smaller per-pass budget and on a cursor that walks the peer's declared inventory so passes add up. A digest challenge in which the peer hashes its own copy was considered and refused: the source holds only the flat digest and could check nothing keyed to a nonce, so the answer would be a claim the peer could have cached at receipt.

At format 3 the index carries more, and the challenge gets cheaper without getting weaker. A format-3 delta publishes a **Merkle root** beside the flat digest — an RFC 6962 tree over one-mebibyte chunks of the same preimage, with the preimage's length bound into the root ([ADR-0065](../adr/0065-merkle-commitment-and-chunk-possession.md), [07 §2.3](../../specifications/repository-format/07-index.md#23-covered-blob-merkle-roots)) — so the source can ask for **one leaf and its authentication path** instead of the blob. What makes that a proof rather than the refused self-report is that the answer carries the leaf's *bytes*: a path is public arithmetic over hashes the peer may freely cache, while the chunk it commits to cannot be produced without being held. The leaf is drawn at random, an inability to produce one for a blob the peer itself declared holding is a finding, and the tree's size comes from the length the peer declares — which the bound root makes fail closed rather than let an understated copy exempt its last leaf. A peer that does not offer the feature is read back whole, as before: its absence costs the destination bandwidth, never the source a check. A chunk proof **samples** the blob, so it is counted apart from the whole-blob tier and never summed with it (contract 1.34).

Recorded in [`../threat-model.md`](../threat-model.md#t-8-destination-withholding-data).

## 6. Quotas and exhaustion

A destination enforces its quota. When a source reaches it:

- the transfer stops cleanly at a blob boundary — never mid-blob, and never leaving a partial object visible;
- the source is told **why**, distinguishing quota exhaustion from disk-full and from a transient error, because the three call for entirely different user actions;
- previously durable snapshots at that destination are unaffected;
- the backup set reports `degraded` for that destination while continuing to protect locally;
- retention at the destination proceeds under its own floor, which may in time free space.

Disk-full on the *destination's* underlying store is reported distinctly from quota exhaustion. Quota is a policy the destination chose; disk-full is a fault it needs to fix.

## 7. Relay

A relay forwards encrypted traffic between peers that cannot connect directly. It:

- cannot decrypt content or metadata;
- learns which device identities are communicating, and how much — traffic analysis is the residual exposure and is recorded in [`../threat-model.md`](../threat-model.md#t-13-relay-traffic-analysis);
- is optional, self-hostable, and reported in the connection path so a user always knows whether they are relayed;
- applies quotas and rate limits, since an open relay is otherwise an abuse vector.

Relay use is never silent. A user paying for metered bandwidth, or expecting LAN-speed transfer, needs to know when traffic is going the long way round.

---

**Previous:** [08 — Restore and recovery](08-restore-and-recovery.md) · **Next:** [10 — Observability](10-observability.md)

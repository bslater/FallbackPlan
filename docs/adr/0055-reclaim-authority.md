# ADR-0055 — The key that deletes is not the key that publishes

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-GC-007, FR-GC-008, FR-WOR-003, NFR-SEC-005, NFR-SEC-009
**Related:** [ADR-0009](0009-garbage-collection-safety.md), [ADR-0020](0020-ed25519-signing-key-semantics.md), [ADR-0042](0042-write-only-repositories.md), [ADR-0053](0053-peer-claim-and-configuration-recovery.md), [repository-format 11 §3](../../specifications/repository-format/11-lifecycle-objects.md#3-tombstone), [peer-protocol 06](../../specifications/peer-protocol/06-retention.md), [T-6](../threat-model.md#t-6-deletion-by-compromised-store-credentials)

---

## Context

An always-on backup service is a high-value ransomware target, and the one
thing an attacker wants from it is not the plaintext — it is the ability to
make the backups go away before the ransom note lands. The threat model names
this as [T-6](../threat-model.md#t-6-deletion-by-compromised-store-credentials)
and points at FR-GC-008, which asks that "retention reduction and bulk
snapshot deletion shall require stronger authorisation than ordinary backup".
That requirement has been carried for four phases with no record behind it and
no test in front of it.

The 2026-09 architecture review put the finding precisely: *a compromised
write-only service may legitimately possess the key needed to publish new
backup metadata; it should not therefore automatically possess the authority
to reduce retention or authorise bulk deletion.*

Today it does. One Ed25519 key per generation signs everything the repository
authenticates, and the two kinds of thing it signs could not be more
different:

- **Appends** — journal records, index publications, snapshot manifests. These
  add. A forged one is noise a reader can reject and a collector will never
  act on destructively.
- **A deletion** — a tombstone. [Specification 11 §3](../../specifications/repository-format/11-lifecycle-objects.md#3-tombstone)
  is explicit that its signature *is* the authorisation: "a reader MUST verify
  it before treating the tombstone as authorisation, and MUST treat a
  tombstone that fails verification as a **security finding** … an unsigned or
  forged tombstone is an attempt to have someone else delete data."

That sentence is right about what a tombstone is and wrong about which key
should be able to write one. Under [ADR-0042](0042-write-only-repositories.md)
a write-only service is deliberately given a write credential and deliberately
denied the content keys — the whole design exists so that a compromised
service cannot read the backups. It can, at present, delete them.

On the peer wire the gap is wider still: a `RetentionOffer`
([peer-protocol 06 §4.1](../../specifications/peer-protocol/06-retention.md#41-retentionoffer))
carries a repository id and a page of keys to delete, and nothing else. The
session's grant is the only authority, so the identity entitled to push
objects is the identity entitled to command erasure. The spoke's retention
floor bounds the damage and does not stop it.

## Decision

### 1 A second derivation domain: the reclaim key

An Ed25519 key per generation, derived off the master key on a domain of its
own, beside the signing key rather than replacing it:

```text
reclaim_seed = HKDF-Expand(master_key, "fbp/reclaim/v1" ‖ u32(g), 32)
```

Interpreted as an RFC 8032 §5.1.5 private-key seed, exactly as
[ADR-0020 §1](0020-ed25519-signing-key-semantics.md) fixed for the signing
key — one interpretation for both keys, because two would be a second chance
to get clamping wrong.

**Tombstones are signed under it. Publications stay under the signing key.**
The split follows what the objects mean, not where they live: everything that
adds keeps the append authority, and the one object that authorises removal
gets its own.

*Reclaim* is not a new word here. FR-VER-006 already speaks of a destination
licensing "reclaiming the source's last local copy", and
[architecture 07 §1](../architecture/07-retention-and-gc.md#1-retention-selects-collection-deletes)
already separates selecting from deleting. This names the authority for the
half that deletes.

### 2 A write-only service is not provisioned with it

[`WriteOnlyDerivation`](../../src/FallbackPlan.Repository.Crypto/WriteOnlyDerivation.cs)
expands a signing sub-root into the write credential. It does **not** expand a
reclaim sub-root, and the omission is the decision: a v2 service can publish
for ever and cannot author a deletion, because it does not hold and cannot
derive the key that would authorise one.

This is the case the review names and the only case where the split is worth
anything by itself.

### 3 An ordinary v1 service gains nothing, and the record says so

> **Amended 2026-09.** There is no such service any more: format 1 was
> withdrawn ([ADR-0014 Amendment 1](0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)), so every service holds a write credential
> that deliberately carries no reclaim domain, and the limit this section
> states plainly has closed. The split now defends every repository. The
> retention floor stays the safeguard that holds against a compromised
> *grant* — a run's authority is still a run's — which is why
> [architecture 07 §5](../architecture/07-retention-and-gc.md#5-destructive-change-safeguards)
> still calls it the most valuable of its measures.

A v1 service holds the master key. It can derive the reclaim key as easily as
the signing key, so against a fully compromised v1 service this decision
defends nothing at all.

Stated here rather than left for a reader to discover, because a security
record that lets someone believe they are protected when they are not is worse
than no record. For the v1 shape the destination-side retention floor remains
the only safeguard that holds — exactly as
[architecture 07 §5](../architecture/07-retention-and-gc.md#5-destructive-change-safeguards)
already says, and the reason that section calls the floor the most valuable of
its measures.

The reclaim key's value for a v1 repository is different and smaller: it makes
the *peer* instruction of §5 signable by something narrower than "whoever
holds the master key", and it gives a later administrative ceremony a key to
hang on.

### 4 The split is announced by a required repository feature

A repository advertising the feature `reclaim-authority` in its descriptor's
**required** set has its tombstones signed under the reclaim key; one without
it keeps the old semantics and verifies them under the signing key. Readers
and collectors follow the descriptor, never a per-object hint.

**Not** a format version bump: the tombstone's `8: bytes[64] signature` is
opaque to the framing, so which key produced it changes no structure and
format v1 stays frozen with its conformance vectors intact.

**And not** the tombstone's own `1: u16 schema version`, which would have been
the obvious move and is the wrong one. A per-object version is a per-object
choice, and the attacker chooses: write a schema-1 tombstone and the weaker
key is back. A repository-level required feature cannot be downgraded by the
object it governs, which is the entire property being bought.

Required rather than optional has a cost worth naming. Every reader must
declare the feature or refuse the repository, including
`FallbackPlan.Recovery`, which never reads a tombstone and would otherwise
refuse to open an archive it is perfectly able to restore. It declares it.
Optional was considered and rejected for the reason the whole section turns
on: an optional feature lets an old collector proceed and accept signing-key
tombstones, which is precisely the downgrade the feature exists to prevent.

### 5 The peer instruction carries a signature, and the spoke can check it

`RetentionOffer` gains a signature over the canonical encoding of its
drop-list under the reclaim key, behind a negotiated feature
(`signed-retention`). A spoke that offers the feature MUST refuse an
instruction that carries no signature or a signature it cannot verify,
refusing the whole instruction and deleting nothing — the same total refusal
the floor check already performs, for the same reason.

> **Amended (2026-09): the feature was the wrong gate, and the signature was
> too narrow.** [ADR-0059](0059-session-bound-deletion-authority.md) replaces
> both halves of the paragraph above. *Behind a negotiated feature* was a hole:
> negotiation is an intersection of what the two sides offer and a listener
> cannot require one, so a source that omitted `signed-retention` from its
> hello had an **unsigned** drop-list obeyed — forgery, needing no reclaim key
> at all. The gate is now the reclaim public key the spoke recorded at first
> attribution, which no session can withdraw. And *over the canonical encoding
> of its drop-list* was too narrow: those bytes say who authorised the
> instruction and never when, so a recorded page replayed into a later session
> verified. The signature now covers the session identifier as well. §4's own
> reasoning — that a per-object choice is the attacker's choice — is what this
> record failed to carry to the wire.

A spoke holds no repository keys by design, so it can only verify against a
**published public key**. The reclaim public key is recorded beside the
attribution, at the first `ReplicationOffer` for a repository, which is where
[05 §2](../../specifications/peer-protocol/05-quotas.md#2-ownership) already
establishes the durable fact that a repository is this peer's. That carrier is
the same one [ADR-0053](0053-peer-claim-and-configuration-recovery.md) decided
for the claim public key and did not build; building it here lands that half
of ADR-0053 as well, and the two keys ride one mechanism rather than two.

This narrows [ADR-0020 §3](0020-ed25519-signing-key-semantics.md), which held
that nothing stores a public key because any reader entitled to verify can
derive it. That reasoning is sound **inside the key boundary** and does not
reach a keyless destination. ADR-0020 is amended accordingly rather than
contradicted silently.

### 6 A write-only set still collects, under a grant that does not outlive the run

Taking the reclaim key away from a v2 service would stop its retention dead,
and retention is not theoretical there: the collector is format-agnostic
today, so a write-only set collects right now using the write credential's
signing key. Removing that without a replacement would let a v2 repository
grow without bound — a regression wearing a security improvement's clothes.

So a collection run on a write-only set requires a **reclaim grant**: the
derived reclaim scalar, sealed end-to-end to the service's published recipient
key and rendered as hex — the one shape NFR-SEC-009 permits key material to
take on the contract, and the shape
[ADR-0042 §5](0042-write-only-repositories.md)'s restore grant already uses.
It is opened only inside the service, held only for that run, and zeroed with
it.

That is the review's "retention/collector capability, preferably with
short-lived credentials", and it changes the meaning of a compromise: a
service taken between runs holds nothing that can author a deletion, and a
service taken during one holds an authority that expires with the job it was
granted for.

> **Amended (2026-09): the grant also carries the peer instruction.** As
> shipped, this section covered the local collection and §5 the wire, and
> nothing said which run a write-only set's *peer* instruction rode. It rode
> the scheduled sync, unsigned — and a spoke holding the recorded reclaim key
> refuses that whole, on every pass, for ever. See
> [Amendment 2](#amendment-2-2026-09--a-write-only-sets-peers-converge-under-the-grant).

## Amendment 2 (2026-09) — a write-only set's peers converge under the grant

Found by moving the peer retention fixtures onto a set-up installation
(slice 12A). `FanOut`'s reclaim signer was null for a write-only set — §2
says it cannot derive the key — so the scheduled sync sent its
`RetentionOffer` unsigned, and the spoke, holding the reclaim public key
every write-only offer publishes (§5), refused it whole under the gate
[ADR-0059](0059-session-bound-deletion-authority.md) moved there. The pass
recorded a terms refusal, the replica kept everything, and nothing on the
status surface said why. The 12A note that called the fan-out's signer
"always null" once format v1 went had read that as acceptable; it is not.

**Decision.** Peer convergence is a deletion the destination performs on the
source's instruction, so for a write-only set it happens under the same
grant its local collection does — during a `retention --apply` run that
carries one — and never during a scheduled sync, which holds no authority to
delete:

- A scheduled sync of a write-only set under retention rules sends **no**
  instruction: it pushes the whole copy, records the pair `InSync`, and
  raises a notice naming the grant it is waiting on. An unsigned page is
  never sent to be refused.
- The retention command's apply path, grant in hand, runs the peer
  convergence for every peer destination of the set with rules —
  `FanOut.ConvergePeersAsync`, pages signed by a signer closed over the
  grant — inside the run, under the set gate, before the grant is zeroed.
  The report names each peer's outcome, and the notice resolves.
- An ordinary v1 set is unchanged: it derives the key and its scheduled
  sync keeps instructing as before (§3).

**Cost stated.** A write-only set's peers age only when someone applies
retention with the passphrase — a console, or
`fallbackplan-agent retention --apply --passphrase-env`. Between runs the
replica grows by what the source publishes, bounded by the peer's quota.
That is the shape §6 already accepted for the local plane, now said for the
wire. `Retention.Tests/PeerRetentionTests` holds it: the scheduled passes
leave the spoke holding everything and say so; the granted run converges it,
is refused whole below the floor, and is refused whole under a recorded key
that is not the repository's.

## Consequences

**Positive**

- The write-only shape now means what it has always claimed: a service that
  can be compromised without the backups being destroyable by the attacker who
  compromised it.
- FR-GC-008 gains a mechanism, a record and a test after four phases of being
  a sentence.
- A peer's deletion instruction is authorised by something narrower than "the
  peer we paired with", for the first time.
- The attribution key carrier that ADR-0053's claim ceremony needs gets built.

**Negative**

- Retention on a write-only set becomes a granted operation rather than an
  autonomous one. That is the point, and it is still a new way for retention
  to stall — a set whose grants stop arriving stops collecting, and the status
  surface has to say so rather than looking healthy.
- A required feature is a compatibility cliff by construction: every reader
  must declare it. The cost is paid once and `eng/recovery-drill.sh` is what
  catches a reader that was missed.
- One more derived key in the hierarchy, and one more thing for a future
  rotation to carry.
- Against a fully compromised v1 service the decision buys nothing (§3).
- **[added 2026-09]** A write-only set's peer replicas converge only under a
  granted run, never on the schedule — the local plane's cost, now on the
  wire too ([Amendment 2](#amendment-2-2026-09--a-write-only-sets-peers-converge-under-the-grant)).
- **[added 2026-09]** The peer signature as this record shipped it covered one
  page and not the session, so an instruction captured inside an authenticated
  session could be replayed into a later one. It was stated in the
  specification and the requirement row and not here, which is the wrong place
  for a record's own limit to live. Closed by
  [ADR-0059](0059-session-bound-deletion-authority.md).

## Alternatives considered

**Bump the tombstone's schema version instead of adding a repository feature.**
Smaller and self-describing, and it hands the attacker the choice: a schema-1
tombstone opts back into the weaker key. Rejected in §4.

**A separate operator secret rather than a derived key.** Strictly stronger —
even a v1 service could not author deletions — and it introduces a second
secret that can be lost, whose loss would mean retention could never be
reduced again. Rejected as a worse trade for a household product, where the
number of things a person must not lose is itself a design constraint.

**Keep the destructive key only in the recovery kit.** Ties destructive
authority to the artefact already treated as the crown jewel, and makes
routine retention an operator ceremony on every set rather than only on
write-only ones. Rejected as disproportionate; the grant of §6 gets most of
the property without making the ordinary case manual.

**Enforce it only at the destination, with floors and object lock.** The
review's other half, and not an alternative — floors already exist and hold
where this does not (§3). Provider object lock waits on a second storage
provider ([ADR-0012](0012-storage-provider-contract.md)), which is why this
record scopes itself to the repository plane and the peer wire and says
nothing about cloud IAM.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | In response to the 2026-09 architecture review's R4, the last of its P0 findings. Narrows [ADR-0020 §3](0020-ed25519-signing-key-semantics.md) for keyless destinations and gives FR-GC-008 its first mechanism |
| 2026-09 | Amended (gate and scope) | Amendment at §5: the peer half's requirement to sign was gated on the negotiated `signed-retention` feature, which the party it defends against decides whether to offer — so an unsigned, freshly composed drop-list was obeyed by a spoke that had simply been told not to ask. §4 of this record had already rejected exactly that shape of gate on the repository plane. [ADR-0059](0059-session-bound-deletion-authority.md) moves the gate to the reclaim public key the spoke recorded, extends the signature to cover the session identifier, and states the general rule in [02 §6](../../specifications/peer-protocol/02-session.md#6-feature-negotiation). `Hosts.Tests/PeerRetentionReplayTests` holds both |
| 2026-09 | Amended | §3's limit closed: format 1 withdrawn ([ADR-0014 Amendment 1](0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)), so no service derives the reclaim key and the split defends every repository |
| 2026-09 | Amended (audit record on the peer plane) | [ADR-0063](0063-deletion-receipts.md) gives FR-GC-008's audit half its peer-plane artefact: the destination's deletion receipt under its own device key, verified by the commander against the instruction §5 signs and filed by both parties. `Hosts.Tests/PeerRetentionReplayTests`, `Retention.Tests/PeerRetentionTests` |
| 2026-09 | Amended (the peer instruction rides the grant) | [Amendment 2](#amendment-2-2026-09--a-write-only-sets-peers-converge-under-the-grant): a write-only set's scheduled sync sent its peer instruction unsigned and was refused whole on every pass. The instruction now rides the granted retention run, and the scheduled sync pushes whole copies and names the grant it waits on. `Retention.Tests/PeerRetentionTests` |

# ADR-0053 — A rebuilt machine claims its peer replica, and its backup set's shape survives with it

**Status:** Proposed
**Date:** 2026-09
**Requirements:** FR-REP-001, FR-KIT-006, FR-DEST-006, NFR-OPS-005
**Related:** [ADR-0013](0013-recovery-kit.md), [ADR-0020](0020-ed25519-signing-key-semantics.md), [ADR-0030](0030-peer-identity-and-pairing.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0042](0042-write-only-repositories.md), [peer-protocol 05 §2](../../specifications/peer-protocol/05-quotas.md#2-ownership), [peer-protocol 07 §4](../../specifications/peer-protocol/07-retrieval.md)

---

## Context

The product's claim is that recovery is the product. A machine dies; a new one
is bought; the backups come home. The 2026-09 architecture review put that
claim to the case it is weakest in — a set whose only destination is a peer —
and found two separate reasons it does not hold. Neither is a bug. Both are
places where a decision was never taken.

**A peer will not give a rebuilt machine its own replica.** Attribution is
keyed by repository to a *pinned peer identity*
([05 §2](../../specifications/peer-protocol/05-quotas.md#2-ownership)), and
`ReplicationResponder` refuses an offer for a repository attributed elsewhere.
Retrieval is gated the same way
([07 §4](../../specifications/peer-protocol/07-retrieval.md)), and refuses
"someone else's replica" and "one never stored here" identically, so the
rebuilt machine cannot even tell which it is being told. The specification is
explicit that this survives re-pairing: *"Re-pairing does not transfer
attributions; they are keyed by repository, not by grant."*

That rule is right. It is what stops one household's archive being counted
against another's quota, and what stops a stranger who pairs with your peer
from asking for your repository by id. It simply has no exit: a device
keypair is per-installation, a rebuilt machine has a new one, and nothing in
the protocol lets the same *person* prove they are the same *owner*.

The existing drills do not catch this because they preserve the state
directory (`Hosts.Tests/AlternateSiteTests`), which is exactly the thing a
dead machine does not preserve.

**And the set's shape does not travel at all.** Name, roots, schedule,
retention and destinations live only in `config.json`. The repository carries
the *capture* settings — the policy manifest records segmentation,
compression, encryption, dedup domain and the include/exclude rules
([06 §7](../../specifications/repository-format/06-manifests.md#7-policy-manifest))
— but nothing says what the set was called, which directories it watched, how
often, how long it kept things, or where else it shipped. A recovered
repository restores every byte and cannot say what it was for.

The second half is the more general failure: it costs the same whether the
destination is a peer, a drive, or a cloud bucket, and it is felt by anyone who
recovers anything.

## Decision

### 1 A claim is a signature the peer can check without holding a key

At the first `ReplicationOffer` for a repository, the source presents a
**claim public key**, and the destination records it beside the attribution.
Later, a machine holding the repository passphrase can present a signature
over a destination-issued nonce and have the attribution re-pointed at its new
device identity.

The claim key is derived from the repository's key hierarchy, on a domain of
its own, so it is available to anyone who can open the repository and to
nobody else:

```text
claim_key = Ed25519 from HKDF-Expand(master_key, "fbp/claim/v1" ‖ repository_id)
```

The destination stores the **public** half. That is what makes this workable
at all: a peer holds no repository keys by design, so it cannot verify a
signature made with the repository's ordinary signing key
([ADR-0020](0020-ed25519-signing-key-semantics.md) — verification derives from
the hierarchy, which is inside the key boundary). A key published in the clear
at attribution time is the one thing a keyless host can check later.

**What this authorises, stated plainly.** Whoever holds the repository
passphrase can take the replica's attribution. That is the correct boundary
and not a weakening: they can already decrypt every byte of it. The claim
grants no new power over the data — only over who the destination will hand it
to and whose quota it counts against.

### 2 The claim ceremony

1. The claimant pairs with the destination as an ordinary new peer
   ([ADR-0030](0030-peer-identity-and-pairing.md)); pairing establishes who is
   speaking and nothing more.
2. The claimant sends `ReplicationClaim { repository_id }`.
3. The destination answers with a fresh random nonce, or refuses identically
   to [07 §4](../../specifications/peer-protocol/07-retrieval.md) when it holds
   no such replica — the refusal must not confirm that a repository is stored
   here, which is the same reconnaissance rule as retrieval.
4. The claimant returns a signature over `repository_id ‖ nonce ‖
   claimant_fingerprint` under the claim key.
5. The destination verifies against the stored claim public key and, on
   success, re-points the attribution at the claimant's fingerprint. The old
   attribution is replaced, not duplicated: one repository, one owner here.

The nonce is per-claim and single-use, so a recorded exchange replays into
nothing. Binding the claimant's fingerprint into the signed material is what
stops an observer re-using a captured signature to point the replica at
themselves.

### 3 A replica attributed before this exists is claimed by a person

A destination running an older build, or one that accepted a repository before
the claim key was published, holds an attribution with no key to check against.
Those are **not** claimable cryptographically, and inventing a weaker proof for
them would put the weakest path in front of every attacker.

They are claimable by the destination's operator, out of band: a stated verb
that re-points an attribution, with the repository named, requiring the
operator's own authority on their own machine. A recovery that needs a phone
call to a friend is a poor recovery; a recovery that is impossible is worse,
and this is the one case where the poor one is all that is available.

### 4 The set's shape travels in the recovery kit

Name, roots, schedule, retention, and the destinations it shipped to go into
the recovery kit ([ADR-0013](0013-recovery-kit.md)), which is already the
artefact that exists to make clean-machine recovery possible and already
carries destination locators. A kit rebuilt after a configuration change
carries the new shape; the console already offers that rebuild.

**Not into the repository**, and the reason is a rule this record will not
bend. [FR-DEST-006](../requirements/functional.md) keeps destination addresses
out of the repository plane so that a store learns nothing about where else its
tenant keeps copies. Publishing the set's destination list into the repository
would put that map into every replica — including a replica held by the very
peer the list describes. The kit is an artefact the *user* holds, off the
machine and off every store, which is the only place this can safely live.

The consequence is honest and belongs on the recovery screen: **a kit is only
as current as its last rebuild.** A set reconfigured after a kit was printed
recovers under the old shape, and the recovery must say when the kit was
issued rather than presenting stale configuration as fact.

### 5 What is built now, and what is not

**Nothing here is built yet, and the attempt found why.** Decision 4 looked
free — the kit is a local artefact, no wire protocol to bump — and it is not,
for two reasons discovered by trying it.

The kit that a service builds is an **installation** kit, by ADR-0042's
2026-08 amendment: setup provisions the installation rather than the first
set, and it is `BuildForInstallation` that the console, the agent and the
restore gate all call. The per-repository builder has exactly one production
caller, the CLI's `kit` verb, which is pointed at a repository path and has no
configured set to read a shape from. So the set's shape has no producer on the
repository kit at all; carrying it means the **installation** kit carrying one
shape per set, which is a larger change to a printed artefact than this record
scoped.

And the kit's version number cannot currently grow, because it means two
things. `IsInstallationKit` was a threshold — version 2 or above — so the next
repository-kit version would have read as an installation kit and been parsed
for fields it does not carry. That test is now an equality, which is the one
change this record lands: it is a latent trap in the artefact every recovery
depends on, it blocks any future kit field rather than only this one, and it
costs a predicate.

**What the next slice has to decide before it writes any code:** whether the
installation kit carries an array of set shapes, and whether the kit's shape
discriminator becomes a field of its own rather than a version number doing
two jobs. The second is the prerequisite for the first.

Decisions 1–3 need a new peer-protocol message, a new derivation, and a durable
field on the attribution ledger: a protocol version bump with its own drill.

The record exists now anyway, because the drill that would have caught the
claim gap preserves the state directory, and every future drill written before
the decision would preserve it too. Naming the ceremony is what lets the drill
be written against something.

## Consequences

**Positive**

- A peer-only set stops being a set that cannot be recovered, which is the
  shape the product recommends to anyone without a second drive.
- The claim's authority is exactly the passphrase's, so it introduces no new
  secret to lose and no new thing to store.
- A recovered repository can say what it was for, on every destination kind
  rather than only for peers.

**Negative**

- A destination now stores one more durable fact per attribution, and a
  destination that has never seen a claim key must fall back to its operator.
- The kit gains state that goes stale. A kit is a snapshot of configuration as
  well as of key material now, and the second ages faster than the first.
- Losing the passphrase now loses the claim as well as the plaintext. That was
  already total loss; it is stated because the claim key makes it look like a
  separate capability and it is not.

## Alternatives considered

**Let re-pairing transfer attributions.** The obvious move, and it hands any
repository to anyone who can pair with the destination and name an id.
Attribution exists to stop precisely that. Rejected.

**Prove the claim by producing bytes the destination holds.** Elegant for a
possession check and useless here: the claimant has lost the data, which is
why it is claiming.

**Put the set's shape in the repository.** Simplest to keep current, since
every publication would refresh it, and it puts each destination's address
list inside every replica — including the replica held by a peer on that list.
FR-DEST-006 forbids it, correctly. Rejected.

**Leave configuration recovery to the operator's memory.** What happens today.
It works for one set with one root and fails for the person who had eleven, in
the week they are least able to reconstruct them.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Proposed | In response to the 2026-09 architecture review's R2. Nothing is built: decisions 1–3 need a peer-protocol message, a new derivation and a ledger field; decision 4 was attempted and found to need the *installation* kit to carry a shape per set, because the per-repository builder has one caller and no configuration to read. The attempt did land one fix — `Repository.Format/RecoveryKit` — where the kit's version number doubled as its shape discriminator and would have misread the next version as an installation kit |

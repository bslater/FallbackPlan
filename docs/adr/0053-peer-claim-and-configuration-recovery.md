# ADR-0053 — A rebuilt machine claims its peer replica, and its backup set's shape survives with it

**Status:** Amended (2026-09) — decisions 1–3 built, decision 4 will not be done; see [Amendment 1](#amendment-1-2026-09--the-claim-key-is-the-installations-and-the-ceremony-is-one-message) and [Amendment 2](#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else)
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

> **Amended (2026-09): a claimant cannot reach that key.** Building the
> ceremony found the derivation above unreachable at the moment it is wanted.
> A machine claiming a replica has lost the repository, and what it holds is an
> **installation** kit — which carries no repository id and no key object at
> all, because every key re-derives from the passphrase and the kit's public
> salt. There is no master key to expand from until the repository is open, and
> the repository is what is being claimed. The claim key is therefore derived
> from the **installation**: `"fbp/claim/v2"` off the root
> `Argon2id(passphrase, installation salt, params)`, with `"fbp/claim/v1"` off
> the master key kept for a claimant holding a format-v1 per-repository kit.
> The destination neither knows nor cares which produced the public key it
> recorded. See [Amendment 1](#amendment-1-2026-09--the-claim-key-is-the-installations-and-the-ceremony-is-one-message).

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

> **Amended (2026-09): steps 2–4 collapse into one message, and the claim
> names no repository.** Two changes, both found by building it.
>
> The nonce round trip is unnecessary. [ADR-0059](0059-session-bound-deletion-authority.md)
> surfaced the **session identifier**
> ([02 §3.5](../../specifications/peer-protocol/02-session.md)), which is
> already fresh per connection, already derived from both sides' contributions
> including both TLS keys, and already known to both ends before the claim —
> so signing over it buys the same freshness for one message instead of three.
>
> And step 2's `repository_id` cannot be supplied. The claimant holds an
> installation kit, which names no repository; nor can it ask, because the
> owner-inventory path is itself gated on attribution and answers a fresh
> device identity with an empty page. So the **claim public key is the
> selector**: the destination re-attributes every repository it recorded that
> key against — which for one installation may be several at once — and its
> answer says which. The signed material is
> `"fbp-peer-v1:replica-claim" ‖ session_id ‖ claimant_fingerprint`.
> The identical-refusal rule of step 3 is unchanged and is what
> `Agent/ClaimResponder` implements.

> **Amended again (2026-09): the ceremony is two phases, because the
> claimant holds the passphrase and nothing else.** With the recovery kit
> withdrawn ([Amendment 2](#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else)), the claimant has no salt to derive the claim
> key with — the salt is inside the replica, behind the attribution gate it
> is trying to pass. So the destination speaks first: an empty
> `ReplicationClaimOpen` is answered by `ReplicationClaimParameters`, the
> distinct KDF salt-and-cost pairs behind every claimable replica here, and
> the claimant sends one `ReplicationClaim` entry per pair. Signed material,
> selector and refusal rule are as the paragraph above says.

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

> **Closed as will-not-do (2026-09).** There is no recovery kit to carry it:
> the passphrase is the whole recovery credential ([Amendment 2](#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else)). The
> set is re-declared after a rebuild, and the flow the owner's recovery model
> implies — add an existing destination, discover its archives by descriptor,
> adopt them under their original ids — is the named follow-up, not this
> record's. **Built as [ADR-0061](0061-adopt-a-destinations-archives.md):**
> the shape travels in the archive itself (policy-manifest keys 10–12), and a
> rebuilt machine adopts a claimed replica back under its original ids over
> the retrieval session.

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

## Amendment 1 (2026-09) — the claim key is the installation's, and the ceremony is one message

Decisions 1–3 are built. Building them changed two of them, and the changes
are recorded here rather than edited silently into the decisions above.

### The claim key is derived from the installation

§1's `HKDF-Expand(master_key, "fbp/claim/v1" ‖ repository_id)` is unreachable
by the party that needs it. A claimant has lost the repository — that is what
it is claiming — and what it still holds is an installation kit, which carries
a KDF salt, Argon2 parameters and a sealing public key, and no repository id
and no key object at all. `Recovery/RecoverySession` says so in as many words:
an installation kit "names no repository", and the path that opens one needs
the archive.

So the authority is the **installation's**, which is already the product's
model: [ADR-0044](0044-first-run-setup.md)'s one passphrase stamps every
archive, an installation kit opens every archive that passphrase wrote, and
`ServiceRuntime.OpenFromInstallationAsync` creates every new set from the
installation credential. "Whoever holds this installation's kit and passphrase"
and "whoever can open this repository" name the same person.

| Claimant holds | Root | Domain |
|---|---|---|
| An installation kit (the provisioned default) | `WriteOnlyDerivation`'s installation root | `fbp/claim/v2` |
| A per-repository kit (format v1) | the repository master key | `fbp/claim/v1` |

> **Amended 2026-09.** The second row is gone with format 1
> ([ADR-0014 Amendment 1](0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)): `fbp/claim/v2` off the installation root is the only
> claim key, and a format-1 kit is refused by name rather than yielding
> one. The single-entry-point remark below now describes one shape.

`Repository/RecoveryKitClaim` answers both through one entry point, because
the person holding the kit should not have to know which kind they were given
and the destination cannot tell either — it recorded a public key and nothing
about where it came from.

> **Amended 2026-09.** That class is gone with the kit. The claimant derives
> in the `claim` verb itself, from the passphrase and each salt the
> destination serves ([Amendment 2](#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else)).

**The key takes no generation**, alone among the derived keys, and the reason
is its carrier rather than its cryptography. `Application/ReplicaOwnerStore`
records it at first attribution and never replaces it — the same
irreplaceability the reclaim key relies on under
[ADR-0055](0055-reclaim-authority.md) §5 — so a key that turned over with the
generation would go stale on the first rotation with no way to tell the peer.
It could not be generational anyway: an installation root knows nothing of any
one repository's generations.

**The private half is withheld from the write credential**, exactly as the
reclaim seed is, and from every grant besides. A service that could author a
claim could re-point a replica's attribution to a machine of its choosing; the
decision of which machine a peer hands the backups back to belongs to the
person holding the passphrase and the kit.

### The ceremony is one message and its answer

§2's nonce round trip existed only for freshness, and
[ADR-0059](0059-session-bound-deletion-authority.md) surfaced something
fresher: the session identifier
([02 §3.5](../../specifications/peer-protocol/02-session.md)) is per
connection, derived from both sides' contributions including both TLS keys,
and known to both ends before the claim.

And the claim names no repository, because the claimant holds no repository
id. The claim public key is the selector: `Agent/ClaimResponder` re-attributes
every repository recorded against it and names them in
`ReplicationClaimAccepted`, which is also the list the claimant could not have
known to ask for. `ReplicaOwnerStore.Reattribute` is the first writer in that
ledger that changes a fingerprint, and the one place the "already stored here
for another peer" rule is set aside — on proof the ledger itself cannot check,
since it holds no cryptography.

Gating on the negotiated `replica-claim` feature is safe in a way it was not
for `signed-retention` ([ADR-0059](0059-session-bound-deletion-authority.md)):
withholding it can only make the destination refuse, so a party that omits it
is asking for less authority rather than more. A destination that does not
offer it refuses by name, so the half of the pair that needs updating is
identified rather than guessed at.

### What is still not built, said plainly

**§3's operator re-attribution is not built.** A replica attributed before the
claim key existed has no key to check against. It self-heals the moment an
updated source makes one more offer, because `TryAttribute` fills an absence —
but if the machine died before that offer, there is nothing, and §3's
out-of-band verb is the only answer. `docs/proof-obligations.md` carries that
limit rather than the record implying otherwise.

**§4 is untouched**, and §5 still says why: the kit a service builds is an
installation kit, and carrying one shape per set is a larger change to a
printed artefact than this record scoped. The decision above does not reopen
it — the claim needs nothing new in the kit, only the salt and parameters an
installation kit already carries.

**A passphrase change would invalidate every recorded claim key.** Nothing in
the product rotates a passphrase today, so this costs nothing now. It is a
constraint on whoever adds one: rotation must re-publish the claim key, or
claims made before it stop verifying.

## Amendment 2 (2026-09) — the claim takes the passphrase and nothing else

Slice 12's decision is that the passphrase is the recovery credential and the
recovery kit is withdrawn ([ADR-0014 Amendment 1](0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)
took format 1 first; the kit follows). Amendment 1's ceremony rested on the kit
in one place: the claimant derived its claim key from the passphrase and *the
kit's* salt. Without a kit, a rebuilt machine holds a passphrase and nothing
else, and the salt it needs is inside the replica — behind the very
attribution gate the claim exists to pass.

### The destination serves the derivations

The ceremony becomes two phases in one session
([03 §6](../../specifications/peer-protocol/03-replication.md#6-the-claim)):

1. The claimant sends an empty `ReplicationClaimOpen`.
2. The destination answers `ReplicationClaimParameters`: the **distinct**
   `(salt, Argon2id costs)` pairs behind every replica it holds whose
   attribution carries a claim public key, read from each replica's own
   descriptor, sorted by salt, at most 16.
3. The claimant derives one claim key per pair — Argon2id runs after the
   dial, because the inputs came over it — and sends one `ReplicationClaim`
   with one entry per pair, all over the same session-bound bytes.
4. The destination verifies every entry, collects every match, and only then
   decides: re-attribute all matches and answer `ReplicationClaimAccepted`,
   or refuse identically for "nothing matched" and "a signature is wrong".

The `replica-claim` feature is **redefined, not versioned**: nothing outside
this repository ever spoke the one-message shape, which was unreleased. A
claim arriving as the first payload frame is refused as `malformed`, so a
peer speaking the old shape learns that rather than being told its key is
unknown.

### What this hands a paired peer, for the record

To a peer that is **paired** — and only such a peer, since the parameters are
a payload of an authenticated session — the destination reveals how many
installations have claimable replicas here, their public salts and their KDF
costs. That is exactly what such a peer could learn by holding any one of
those replicas: a descriptor is served unencrypted within an authorised
retrieval. It does **not** reveal repository ids, sealing public keys, or
which fingerprint each pair belongs to. A salt without a verifier beside it is
no offline oracle for the passphrase; the only oracle is the claim, one per
session, refusing identically.

The cost, stated: at a peer, a wrong passphrase reads as *no replica here is
claimable*, not *wrong passphrase*. Nothing on the claimant's side can tell
the two apart, and the destination must not. A claimant can only be told the
latter with one of its own archives mounted, where the sealing public key in
the descriptor verifies the passphrase offline.

The reader caps what the destination may ask for — memory to 1 GiB,
iterations and parallelism to 64 — and refuses an entry outside them as
`malformed` rather than clamping it. The claimant runs Argon2id on parameters
the destination chose, and a destination naming a terabyte of memory would be
naming a denial of service.

### Alternatives considered here

**Serve `repository-format` through retrieval to a paired peer**, so the
claimant reads each descriptor itself. Rejected: the descriptor carries the
sealing public key, which is a real offline wrong-passphrase verifier, and it
would hand every tenant's to a non-storing pair.

**A constant-salt claim key**, derived from the passphrase alone so no salt
is needed. Rejected: a product-wide precomputation target for the one key that
re-points ownership.

**Keep a kit for the claim alone.** Rejected by the owner's decision: one
credential, and the product does not ask a person to keep an artefact whose
only purpose is to carry sixteen public bytes the peer already holds.

### Decision 4 closes

The set's shape was to travel in the kit. There is no kit; the set is
re-declared after a rebuild, and the flow that would make that cheap — add an
existing destination, discover its archives by descriptor, adopt them under
their original ids with the passphrase — is the named follow-up, since built
as [ADR-0061](0061-adopt-a-destinations-archives.md). §4 is
**will not do** as written, and the *Negative* consequence about a kit that
goes stale goes with it.

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
- ~~The kit gains state that goes stale.~~ Closed with §4
  ([Amendment 2](#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else)): there is no kit.
- A paired peer learns the salts and KDF costs of every claimable installation
  here, and a wrong passphrase at a peer reads as "nothing claimable"
  ([Amendment 2](#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else)).
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
| 2026-09 | Amended | The intent of §4 is met otherwise: the set's shape travels in the archive's policy manifest and a rebuilt machine adopts a claimed replica back under its original ids ([ADR-0061](0061-adopt-a-destinations-archives.md)); `Hosts.Tests/PeerAdoptionTests` runs the drill after the claim. §3's operator re-attribution remains unbuilt |
| 2026-09 | Amended (passphrase only) | [Amendment 2](#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else): the kit is withdrawn, so the claimant holds the passphrase and nothing else. The ceremony is two phases in one session — `ReplicationClaimOpen`, `ReplicationClaimParameters` (the destination serves the distinct KDF salts and costs behind its claimable replicas), a multi-entry `ReplicationClaim`, `ReplicationClaimAccepted` — with `replica-claim` redefined rather than versioned. `Protocol/PeerReplicationMessages`, `Agent/ClaimResponder` and `Cli/CliApplication` carry it; `Hosts.Tests/PeerClaimTests` runs the drill with the state directory destroyed. §4 closes as will-not-do |
| 2026-09 | Amended | The `fbp/claim/v1` root went with format 1 ([ADR-0014 Amendment 1](0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)); the installation's claim key is the only one |
| 2026-09 | Amended (derivation and ceremony) | Decisions 1–3 built. [Amendment 1](#amendment-1-2026-09--the-claim-key-is-the-installations-and-the-ceremony-is-one-message) records two changes the attempt forced: §1's repository-derived claim key is unreachable by a claimant that has lost the repository, so the key is derived from the **installation** (`fbp/claim/v2`, with `fbp/claim/v1` for a format-v1 kit); and §2's nonce round trip is replaced by the session identifier from [ADR-0059](0059-session-bound-deletion-authority.md), with the claim naming no repository because the claimant holds no repository id. `Protocol/PeerReplicationMessages`, `Agent/ClaimResponder`, `Repository/RecoveryKitClaim` and `Application/ReplicaOwnerStore` carry it; `Hosts.Tests/PeerClaimTests` runs the drill. §3's operator re-attribution and §4 remain unbuilt |
| 2026-09 | Proposed | In response to the 2026-09 architecture review's R2. Nothing is built: decisions 1–3 need a peer-protocol message, a new derivation and a ledger field; decision 4 was attempted and found to need the *installation* kit to carry a shape per set, because the per-repository builder has one caller and no configuration to read. The attempt did land one fix — `Repository.Format/RecoveryKit` — where the kit's version number doubled as its shape discriminator and would have misread the next version as an installation kit |

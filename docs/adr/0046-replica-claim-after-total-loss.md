# ADR-0046 — Disaster recovery from a peer: the passphrase claims the replica, and the device identity does not

**Status:** Proposed
**Date:** 2026-08
**Requirements:** FR-DR-001, FR-DR-002, FR-DR-003, FR-DR-004, FR-DR-005, NFR-SEC-009
**Related:** [ADR-0010](0010-local-store-separation.md), [ADR-0013](0013-recovery-kit.md), [ADR-0030](0030-peer-identity-and-pairing.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0041](0041-guided-restore-and-peer-retrieval.md), [ADR-0042](0042-write-only-repositories.md), [peer-protocol 05](../../specifications/peer-protocol/05-quotas.md), [peer-protocol 07](../../specifications/peer-protocol/07-retrieval.md)

---

## Context

The disaster this project exists for is the one where the machine is gone.
Not a lost staging archive, not a dead disk in an array — the whole
computer: ransomware that reached everything writable, a theft, a fire, a
rebuild from bare metal after malware nobody wants to try to clean. The
data survives because a friend's computer holds a replica. The question is
what the rebuilt machine has to do to get it back.

Today it cannot get it back at all, and the reason is worth stating
carefully, because every individual piece of it is correct.

**Durable local state is not a cache and does not come back.**
[Architecture 00 §Principles](../architecture/00-overview.md) already
carves out the exception: caches are disposable, but *device identity and
pairing grants are not* — they cannot be rebuilt from repository contents.
`PeerKeypairStore` says the same thing at the point of use: losing
`peer.key` loses the identity and every pairing. That is deliberate.
[ADR-0010](0010-local-store-separation.md) and
[ADR-0013](0013-recovery-kit.md) both keep the device private key out of
the recovery kit precisely so that a kit left in a drawer, photographed, or
posted to a friend cannot impersonate the device to its destinations. Both
records then say what happens instead: *a recovering device establishes a
new identity and is re-authorised*.

Establishing the new identity works. Re-pairing by invite works. It is
everything after that which does not:

1. **The replica is attributed to a fingerprint that no longer exists.**
   [Peer-protocol 05 §2](../../specifications/peer-protocol/05-quotas.md)
   records an attribution — repository id → peer identity — the first time
   a destination accepts an offer, and states plainly that *re-pairing does
   not transfer attributions; they are keyed by repository, not by grant*.
   The retrieval responder gates every open on that ledger.
2. **The owner inventory answers nothing.** [Peer-protocol 07
   §3.5](../../specifications/peer-protocol/07-retrieval.md) exists for
   exactly this shape of trouble — *how a hub that lost its staging learns
   what to ask for* — but it answers for the **dialling** identity. A
   rebuilt machine asks and is told, truthfully under the current rules,
   that the destination holds nothing of its.
3. **The backup set id is gone with the configuration.** A candidate
   replica is kept only if it holds a snapshot carrying the named set's
   `backup_set_id`. The kit carries the repository id and the destination
   addresses; it has never carried set ids. So even a replica the machine
   could reach would be rejected as some other set's.

Wall 1 is not a defect to be removed. The same attribution check is what
stops one household's peer aging or deleting another household's replica
([peer-protocol 06 §3](../../specifications/peer-protocol/06-retention.md)),
and what keeps one household's bytes off another's quota. Whatever opens
the disaster-recovery path must leave that standing.

So the question is narrow: **what, surviving a bare-metal rebuild, should
be allowed to prove that this machine is the same household as the one
whose replica the friend is holding?**

The device key is gone by design. The kit alone cannot be the answer — a
kit is a printable page, and treating possession of it as authority to
redirect a replica is the impersonation risk ADR-0010 refused. What is
left is the passphrase, and on inspection it is not a compromise but the
correct answer: **whoever holds the passphrase can already decrypt every
byte of that replica.** Gating recovery on a credential the recovery model
deliberately destroys protects nothing, while costing the user their data.

## Decision

1. **The passphrase is the claim authority.** A device that can prove it
   holds the repository's passphrase may claim that repository's replica at
   a destination, re-pointing the attribution to its current peer identity.
   Nothing else grants a claim: not the kit alone, not a prior pairing, not
   an operator's assertion at either end.

2. **The credential is derived, never stored, and never transmitted.**
   Both repository formats already reduce the passphrase to an Argon2id
   root over the descriptor's public salt and parameters — v1 calls it the
   KEK ([format 03 §2](../../specifications/repository-format/03-keys.md)),
   v2 calls it the root ([ADR-0042](0042-write-only-repositories.md)
   Decision 1) — and the two use the same derivation code. The claim
   keypair expands from that root:

   ```text
   claim_root   = Argon2id(passphrase, descriptor.kdf_salt, descriptor.kdf_parameters)
   claim_seed   = HKDF-Expand(claim_root, "fbp/peer-claim/v1" ‖ claim_token, L = 32)
   claim_public = Ed25519 public key of claim_seed
   ```

   The seed is an Ed25519 private-key seed in the sense of RFC 8032 §5.1.5,
   the same interpretation [ADR-0020](0020-ed25519-signing-key-semantics.md)
   fixed for the repository signing key. The passphrase itself never
   crosses the wire or the command contract, and neither does the root.

3. **The destination holds a token unique to itself, and validates by
   comparing the generated public key.** On accepting a repository for the
   first time — the same moment [peer-protocol 05
   §2](../../specifications/peer-protocol/05-quotas.md) already records an
   attribution — the destination mints a random 16-byte `claim_token` for
   that replica and sends it. The source derives `claim_public` and returns
   it. The destination stores both beside the attribution.

   The token is **not a secret**. It is a salt, and its whole job is to
   make the derived keypair different at every destination, so that a proof
   produced at one friend's machine is worthless at another's. Two friends
   holding replicas of the same repository hold different tokens and
   therefore validate against different public keys.

4. **A claim is a signature over a channel-bound challenge, checked against
   the stored public key.** The claimant dials a retrieval session — already
   mutually authenticated and bound to the TLS channel by [peer-protocol 02
   §3](../../specifications/peer-protocol/02-session.md) — and asks to
   claim. For each replica the dialling identity does not already own, the
   destination sends that replica's `claim_token` and a fresh nonce. The
   claimant derives the keypair and signs the nonce together with the
   session transcript hash and its own fingerprint. The destination
   verifies against the `claim_public` it stored, and on success re-points
   the attribution.

   Equality of a derived public key is the verifier, not decryption of
   anything: the same posture [ADR-0042](0042-write-only-repositories.md)
   took for the wrong-passphrase check, and for the same reason — it offers
   no oracle beyond equality.

5. **A refused claim says nothing about what is there.** A signature that
   does not verify, and a replica that does not exist, refuse identically
   and in one message, extending [peer-protocol 07
   §4](../../specifications/peer-protocol/07-retrieval.md)'s existing rule
   that which of the two it was is reconnaissance the requester is not
   owed. A claim attempt is therefore not a probe for what a peer holds.

6. **The claim returns the set ids, because wall 3 is otherwise fatal.**
   The response names, for each claimed repository, the `backup_set_id`s
   its snapshots carry. This is the one piece of the lost configuration
   that cannot be reconstructed from the kit and without which the restore
   path rejects every candidate. It is not sensitive: the claimant has just
   proved it can decrypt the whole repository.

7. **Reading is unattended; deleting is not.** A claimed replica is
   readable immediately — an unattended recovery path is the entire point,
   and a friend who is asleep, travelling or unreachable must not be able
   to stall a disaster recovery. But the destination raises a durable
   notice, and **refuses retention instructions from the claiming identity
   until that notice is acknowledged** ([peer-protocol 06
   §3](../../specifications/peer-protocol/06-retention.md)).

   This is the asymmetry the malware case demands. An attacker who has
   stolen the passphrase has gained nothing they did not already have —
   the passphrase decrypts the data wherever they read it — so gating
   *reading* on a human buys no security and costs recoveries. Destroying
   the last surviving copy is a different act with a different blast
   radius, and it waits for the person who owns the disk.

8. **Nothing in the repository format changes.** The claim key derives from
   a root the format already defines, and lives entirely in the
   destination's attribution ledger. No new object, no new descriptor
   field, no new key namespace, and no bearing on the format v1 freeze
   gate. [Format 03 §4](../../specifications/repository-format/03-keys.md)
   gains a pointer to this record and nothing normative.

## Consequences

- **The bare-metal recovery becomes a supported, testable path** rather
  than a scenario that reads as covered and is not. Its drill is the
  disaster-recovery sibling of [ADR-0013](0013-recovery-kit.md)'s kit
  drill: destroy the state directory *and* the archives, keep the kit and
  the passphrase, and get the data back over the wire.
- **The passphrase's blast radius is now stated honestly.** It was always
  true that the passphrase decrypts everything; it is now also true that it
  redirects a replica's attribution. The threat model records this, and
  decision 7 is what bounds it.
- **Replicas stored before this ceremony cannot be claimed.** They carry no
  token and no public key, because the destination never asked for one.
  Such a claim is refused with that stated as the reason, so the operator
  learns the remedy — one successful session under the old identity
  registers the credential — rather than reading it as a wrong passphrase.
  Nothing migrates silently.
- **A write-only (v2) set cannot arm itself unattended, and this is a real
  limitation rather than a detail.** Registration needs the Argon2id root, and
  a provisioned v2 service deliberately holds only the one-way write bundle
  derived from it ([ADR-0042](0042-write-only-repositories.md) Decision 1) — so
  an ordinary v2 backup cannot register the credential its own disaster
  recovery would need. Decision 2's choice of the root as the derivation base
  is what makes one claim path serve both formats, and it is also what creates
  this gap; the alternatives are weighed in
  [Q23](../open-questions.md#q23--arming-disaster-recovery-on-a-write-only-repository)
  and one of them must be picked before FR-DR-001 can be claimed for v2. v1 is
  unaffected. The failure is at least loud: an unregistered replica refuses a
  claim by saying it predates the ceremony, never by implying a wrong
  passphrase.
- **A destination stores two more small fields per replica.** The ledger
  keeps its existing posture for damage: a corrupt file is set aside and
  the ledger starts refillable, which now costs a re-registration on the
  next session rather than only a re-attribution.
- **Claim is gated behind a negotiated feature**, so a destination running
  an older build is never sent a frame it would refuse as unknown, and a
  claimant learns "this peer cannot do that yet" as a capability rather
  than a failure.
- **An acknowledged-claim interlock is new operator surface.** A friend who
  never acknowledges leaves the claiming hub unable to age its replica
  there; that shows as a destination not converging, with the notice naming
  why. Preferred to the alternative, which is a stolen passphrase deleting
  a household's last copy unattended.

## Alternatives considered

**Put the device identity in the recovery kit.** Rejected in
[ADR-0010](0010-local-store-separation.md) and rejected again here, for
the reason given there: a stolen kit could then impersonate the device to
its destinations. It would also make the kit a bearer credential for
redirecting replicas, which is strictly worse than making the passphrase
one, because the kit is designed to be printed.

**Transfer attributions on re-pairing.** The obvious shortcut, and unsafe:
it would make "pair with me" sufficient to inherit whatever the destination
held for any identity it had previously trusted, and it collapses the
distinction [peer-protocol 05 §2](../../specifications/peer-protocol/05-quotas.md)
draws between a grant and a repository. It also cannot work in the case that
matters — the rebuilt machine is a *new* identity, with no prior grant to
inherit from.

**Have the friend approve the claim manually.** Sound, and genuinely
simpler — no derivation, no new credential. Rejected as the primary path
because it makes every disaster recovery block on a second household being
awake and available, which is exactly when a person is least able to wait.
It survives in decision 7 for the destructive half, where the delay is
worth its cost.

**Derive the claim key from the repository master key instead of the
Argon2 root.** Workable for v1 and impossible for v2, whose service holds
a write bundle and no master key at all. The Argon2 root is the one thing
both formats have, and deriving from it makes a single claim path serve
both — which is also why the label carries no format-version suffix.

**Reuse the repository signing key as the claim key.** Rejected: it is the
key that signs snapshot manifests, and reusing a document-signing key as a
network authentication key is the cross-protocol reuse
[ADR-0005](0005-aead-suite-and-nonce-construction.md)'s domain separation
exists to prevent. A separate HKDF label costs nothing and keeps a
compromise of one from reaching the other.

**Have the destination verify a manifest signature instead of storing a
public key.** Attractive — it would need no registration step and would
work for replicas already stored. It does not work: the destination holds
ciphertext, snapshot manifests are sealed, and format v1 stores no signing
public key anywhere precisely because any holder of the master key computes
it themselves. The destination is not such a holder, and must not become
one.

**Skip the per-destination token and derive one claim key per repository.**
Simpler to specify, and it would let a proof captured at one destination be
replayed at every other destination holding that repository. The token
costs 16 bytes and removes that entirely.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written after a coverage review found the bare-metal case uncovered: the existing peer drills destroy the staging archive but keep the state directory, so no test had ever exercised a recovery in which the device identity itself was lost. Decision fixed by the user: the passphrase claims the destination, validated by comparing the generated public key, against a token the destination holds that is unique to it |

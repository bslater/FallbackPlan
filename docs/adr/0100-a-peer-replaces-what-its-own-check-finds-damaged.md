# ADR-0100 — A peer replaces what its own check finds damaged

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-VER-008 (amended: what the sweep finds at a peer is replaced from a copy here proven sound, where the peer's own check of its copy agrees the copy is damaged, and proven again where it lands; what the peer will not take is held as before); FR-VER-005 (the finding still fails the pair, and its notice says what was replaced and what remains)
**Related:** [ADR-0035](0035-destination-fitness.md) Amendments 1 and 2, whose repair this carries to a peer and whose "does not do" it discharges; [ADR-0058](0058-peer-write-adapter.md), whose refusal to carry writes on the retrieval session this keeps; [ADR-0055](0055-reclaim-authority.md), the authority a deletion at a peer needs and a repair does not; [ADR-0095](0095-the-staging-archive-heals.md), which heals the staging archive the other way; [specification 05 §4](../../specifications/repository-format/05-blob.md#4-footer-locator); [peer-protocol 08](../../specifications/peer-protocol/08-repair.md)

**Built:**
- The check: `Repository.Packing/BlobLocatorCheck`, the keyless check of a whole blob against its own locator.
- The peer's side: `Agent/RepairResponder`, served from `Agent/RemoteServiceListener` when a session opens with a repair offer and the feature is in effect.
- The wire: `Protocol/PeerRepairMessages` (`RepairOffer` 280 and `RepairVerdict` 281), the replica-repair feature in `Protocol/PeerSessionNegotiation`, and the state gate in `Protocol/PeerAuthenticator`.
- The source's side: `Agent/PeerRepairClient` and `Agent/PeerRepairTarget`, through `Repository/ReplicaRepair`'s repair target and `Agent/ReplicaRepairer`'s peer variant; the sweep repairs at once (`Agent/ReplicaSweepJob`), and each sync of a pair still holding damage offers it again (`Agent/FanOut`).
- The tests:
  - `Protocol.Tests/RepairMessageTests`: the messages, their numbers and verdict codes, the state gate and the feature
  - `Repository.Tests/EndToEnd/BlobLocatorCheckTests`: what the check passes and fails, and what it cannot see
  - `Repository.Tests/EndToEnd/ReplicaRepairTests`: a repair through a target that may refuse, report the key gone, or not prove where it landed
  - `Hosts.Tests/PeerRepairTests`: the peer's rule over the wire, verdict by verdict
  - `Hosts.Tests/PeerDeepSweepTests`: rot at a peer replaced at once and the pair in sync after the next sync; a well-formed blob under another's key refused and held; damage held while nothing here was sound replaced by a later sync; and a peer whose build takes no repair holding what is found, as before

---

## Context

The deep sweep has read a peer's replica back over the retrieval session since [ADR-0035 Amendment 2](0035-destination-fitness.md#amendment-2-2026-09--a-peer-is-swept-on-a-stated-cadence-and-what-is-found-there-is-held). What it found there was held and not repaired. The notice named the objects and the one remedy that existed: the peer's owner removes them, and the next push sends them again whole. The amendment said why: replacing an object at a peer "needs a write the protocol does not have, under an authority a write-only service does not hold unattended".

Both halves of that were true. The retrieval session reads, and its responder holds no write path ([ADR-0058](0058-peer-write-adapter.md)'s alternatives). A push only creates, so a key the peer holds is never sent again. A deletion at a peer is the one write that removes an object, and it needs the reclaim authority ([ADR-0055](0055-reclaim-authority.md), [ADR-0059](0059-session-bound-deletion-authority.md)), which a write-only service holds only under a grant a person gives it for a retention run.

The authority exists because the peer cannot tell a legitimate deletion from a destructive one. It holds no key, so it cannot tell which blob a key names, and it takes nothing on the source's word: a hub taken over must not be able to empty its peers. So a repair could only be safe if the peer could judge it without trusting the source. Phase 4's last unmet exit criterion was this: "corruption healed from another replica", met for local paths, stores and the staging archive, and for a peer "held until its owner removes it".

## Decision

### 1 The peer replaces a blob only on evidence it holds itself

A blob ends in a sixteen-byte locator: the offset of its footer, and the first eight bytes of the SHA-256 of everything before the locator ([specification 05 §4](../../specifications/repository-format/05-blob.md#4-footer-locator)). That is unkeyed and truncated, so it proves nothing about which blob the bytes are. But it does tell whether the bytes have changed since they were sealed, and a holder with no key can check it. This is the **locator check**.

The peer replaces its copy of a key only when all three hold:
1. its own copy fails the locator check;
2. the replacement is the length of its copy, and passes the locator check;
3. the replacement's locator is the locator its damaged copy still states.

The third condition is what makes the rule safe. Rot that lands anywhere but the last sixteen bytes leaves the locator as it was sealed, so the locator still names the bytes that were written. A replacement that passes the check and states that locator is those bytes, unless someone found a second preimage of a 64-bit SHA-256 prefix, at the same length and footer offset. A source that is buggy or taken over can therefore put back only what was sealed under the key, and only where it is no longer there:
- a sound copy is never touched, because it passes the check;
- a damaged copy is never replaced with anything else, because the locator binds the replacement;
- [INV-BLOB-001](../../specifications/repository-format/05-blob.md#51-blob-immutability--inv-blob-001) holds, as at a local path. What is put back is what was written under the key; the storage changed it, and the repair changes it back.

So a repair needs no authority the pairing does not already give. That is the whole of the security argument, and it is the peer's to make, from its own copy and the bytes it receives. The feature that gates the exchange decides nothing: it says only that the peer understands the messages, which [02 §6](../../specifications/peer-protocol/02-session.md#6-feature-negotiation) permits of a gate whose absence can only mean less change at the destination.

### 2 Its own exchange, the owner's, gated by `replica-repair`

The exchange is [peer-protocol 08](../../specifications/peer-protocol/08-repair.md). A session opens with a `RepairOffer` naming the repository, the key and the length, and is served only for the replica's owner, as retrieval is. A repository another peer stores there and one never stored there refuse identically.

The peer answers each offer with a `RepairVerdict` reached from its own copy:
- **send**: its copy fails the check and is the offered length;
- **intact**: its copy passes the check;
- **not held**: it holds nothing under the key;
- **length differs**: its copy is not the offered length.

Only on send do the bytes cross, as a replication object and its chunks ([03 §3.3](../../specifications/peer-protocol/03-replication.md)). The peer stages them, checks them, and answers again: **replaced**, or **unmatched** when they fail the check or state another locator. It replaces by deleting its copy and creating the staged bytes under the key. A cut between the two leaves the key absent, which the next push fills if the owner is still owed it.

A repair never creates an object, so it never moves the quota ([05](../../specifications/peer-protocol/05-quotas.md)). It names only blobs: nothing else a replica holds has a check a keyless holder could make of a replacement.

It is its own session for two reasons:
- not part of retrieval, because retrieval's argument is that it holds no write path ([ADR-0058](0058-peer-write-adapter.md));
- not a step after a push's acknowledgement, because the sweep that finds the damage reads over retrieval and has no push to ride. An empty push to reach it would issue a replication receipt for nothing.

### 3 Proven here first, offered, and proven again where it landed

The source repairs as [ADR-0035 Amendment 1](0035-destination-fitness.md#amendment-1-2026-09--a-circuit-is-carried-to-its-end-and-damage-is-repaired) repairs a local path:
- **Proven first.** A copy is staged under the spool and proven sound: its whole blob hashes to its sealed digest, and its envelope names the blob its key derives from.
- **Offered.** Only then is the peer offered it, and it is sent only on send.
- **Proven again.** What the peer took is read back whole over the retrieval session and proven again before the key leaves the ledger. The peer's verdict is a claim, and only the read-back is a proof ([04 §5.1](../../specifications/peer-protocol/04-verification.md#51-what-a-source-with-no-copy-of-its-own-can-prove)).

The copies a peer is repaired from are those this installation reads without dialling a peer: the staging archive, and the set's local paths and stores. A finding at one peer is no reason to put a session on another's link. And a refusal, which the peer's own copy decides, would spend that link again on every sync that offered the repair. A target's refusal ends the repair of that key: every other source's copy, once proven, is the same bytes, and would be refused for the same reason.

### 4 The sweep repairs at once; each sync offers what remains

A segment that finds damage at a peer whose build takes a repair repairs it then, over the retrieval session it is reading through, as a local path's segment does. The finding still fails the pair (FR-VER-005), and the next sync finds nothing held and returns it to in sync. The notice says what was replaced, from where, and that it was proven where it landed.

Each sync of a pair still holding damage offers it again. That covers damage found when no copy here was sound, damage a restore read around ([ADR-0075](0075-a-restore-reads-around-damage.md)), and damage found while the peer's build took no repair. The order keeps a refusal cheap: the copy is staged from a local source, offered, and read back only if taken. A key the peer refuses costs its link the offer and nothing more, under the sync's back-off.

### 5 What the peer will not take is held, as before

A copy the peer's check finds intact is a well-formed blob other than the one sealed under the key, which only the repository's keys can tell. A copy of another length has lost the place its locator was, and one whose locator rotted names a blob no replacement will match. The peer takes neither. Damage with no sound copy here cannot be offered. And a peer whose build takes no repair is never asked: the source requires the feature of the repair session, so an older build is refused at the hello and never sent a message it would refuse as unknown.

All of that stays on the ledger, holds the pair failed, and is named with the remedy [ADR-0035 Amendment 2](0035-destination-fitness.md#amendment-2-2026-09--a-peer-is-swept-on-a-stated-cadence-and-what-is-found-there-is-held) gave: the peer's owner removes the objects, and the next push sends them again whole. The notice now says why the repair did not happen, in the peer's own terms.

## Consequences

**Positive.**
- Rot at a peer heals without its owner doing anything, from a copy proven sound here and proven again where it lands. Phase 4's exit criterion, "corruption healed from another replica", is met for every destination kind.
- The peer needs no key and takes nothing on trust. A source taken over can do no more through a repair than put back the bytes that were sealed, where they are no longer there.
- A refusal is cheap and says why. A swapped blob, a truncated copy and a peer that predates the feature each read differently on the notice.

**Negative.**
- The locator binds a replacement to 64 bits of a digest. A party that can find a second preimage of a 64-bit SHA-256 prefix, at the right length and footer offset, could replace a damaged blob with bytes of its choosing at a peer. It could not touch a sound one, and it gains nothing a damaged blob had not already lost. The flat digest a writer signs into the index is the full 256 bits, but a peer cannot read the index.
- A copy that changed length, or whose last sixteen bytes rotted, is not repaired: nothing left on it names the blob a replacement would have to be. Its remedy is still the owner's removal.
- A blob replaced and then not readable back over the retrieval session stays on the ledger, though the peer's copy may now be sound. The next segment that reads it clears it, so at a peer with no cadence that takes a person's verify-destination.

**Neutral.**
- No repository format change, and no change to the client↔service contract. The peer protocol keeps version 1 and gains a feature and two message types, which is the mechanism [02 §6](../../specifications/peer-protocol/02-session.md#6-feature-negotiation) provides for exactly this.

## Alternatives considered

**Repair under the reclaim authority.** A signed deletion, then the push that recreates the key, would use the authority a peer already checks. But a write-only service holds that key only under a grant a person gives for a retention run. So a peer's damage would wait for a person, and a hub taken over while holding a grant could delete anything rather than repair one thing. The locator check needs no authority because it can authorise nothing destructive.

**Replace on the peer's check alone, without binding the locator.** Simpler, and it would also repair a copy whose locator rotted. But a source could then replace a damaged blob with any self-consistent blob. The damaged blob's surviving records, each still authenticating on its own, would be lost to a replacement the peer had no way to refuse.

**Let the peer hash its copy and answer the digest.** A digest a peer computes of its own copy is a claim ([ADR-0058](0058-peer-write-adapter.md) §8), and the source holds nothing that would make it more. The repair's proof stays the read-back.

**Carry the write on the retrieval session.** One session for the find, the write and the proof. Refused for the reason ADR-0058 gives: retrieval's security argument is that it holds no write path.

## What this record does not do

- **Repair a copy whose length changed, or whose locator rotted.** See the second negative consequence.
- **Repair anything but a blob.** The sweep reads blobs. Nothing else a replica holds carries a check a keyless holder could make of a replacement.
- **Repair a peer from another peer.** A copy here, or the owner's removal.

## Status history

| Date | Status | Note |
|---|---|---|
| 2026-10 | Accepted; built | A peer replaces a blob only where its own copy fails the locator check, with bytes of its length that pass it and state the locator its copy states, over a repair session of its own gated by the replica-repair feature and served only for the owner (`Agent/RepairResponder`, `Repository.Packing/BlobLocatorCheck`, `Protocol/PeerRepairMessages`); the source proves its copy first, offers it, and proves it again over the retrieval session where it landed, at once in the sweep and again at each sync while damage is held (`Agent/PeerRepairTarget`, `Agent/ReplicaSweepJob`, `Agent/FanOut`). Held by `Hosts.Tests/PeerRepairTests`, `Hosts.Tests/PeerDeepSweepTests`, `Repository.Tests/EndToEnd/BlobLocatorCheckTests`, `Repository.Tests/EndToEnd/ReplicaRepairTests` and `Protocol.Tests/RepairMessageTests` |

# FallbackPlan peer protocol — specification

**Protocol version:** 1 (draft) · **Status:** all seven documents written; see [Documents](#documents) · **Implemented:** 01, 02, 03's base object exchange, 04's verification challenges, 05's quota enforcement and 06's retention instructions, over a real TLS socket

---

## What this is

The normative wire protocol two FallbackPlan devices speak to replicate a repository between them, and the ceremony by which they first come to trust one another.

It is written to the same standard as the [repository format](../repository-format/README.md): implementable by someone who has never read the project's architecture documents, in a language other than C#, without access to the reference implementation.

## What this is not

**It is not the repository format.** The protocol moves objects whose bytes that specification defines; it never reinterprets them. A peer that has never parsed a blob can still forward one.

**It is not a synchronisation protocol.** Peers exchange immutable objects and never reconcile live folders. There is no "current" global file state to converge on. → [`09-replication-and-peers.md` §1](../../docs/architecture/09-replication-and-peers.md#1-what-replication-moves)

## Authority

| Question | Authority |
|----------|-----------|
| What bytes cross the wire | **This specification** |
| What bytes are stored | [repository format](../repository-format/README.md) |
| Why it is that way | [`docs/architecture/09`](../../docs/architecture/09-replication-and-peers.md), [ADR-0030](../../docs/adr/0030-peer-identity-and-pairing.md) |
| What the system must do | [`docs/requirements/`](../../docs/requirements/) |

## Documents

All eight documents are now written; the table says which are also implemented. The set spent its first months deliberately incomplete, with the missing parts stated here rather than discovered by an implementer halfway through — the alternative, drafting the remainder thinly so the set *looked* whole, is how a specification becomes something nobody can build from.

| # | Document | Covers | Status |
|---|----------|--------|--------|
| — | [Conventions](00-conventions.md) | What is inherited from the repository format, and what differs | Written |
| 01 | [Identity and pairing](01-identity-and-pairing.md) | Peer keypairs, the pairing ceremony with negotiated storage roles, pinning, grants and terms | Written; implemented |
| 02 | [Session](02-session.md) | Transport, handshake, feature negotiation, framing, errors | Written; implemented |
| 03 | [Replication](03-replication.md) | The object exchange: scope, have/want, ranged transfer, resumption | Written; base exchange implemented |
| 04 | [Verification](04-verification.md) | The keyed random-range challenge and its sampling policy | Written; implemented |
| 05 | [Quotas](05-quotas.md) | Exhaustion, disk-full, and their distinct reporting | Written; implemented |
| 06 | [Retention instructions](06-retention.md) | Hub-planned aging of a peer replica, floor-bounded | Written; implemented |
| 07 | [Retrieval](07-retrieval.md) | An owner reading its own replica back: listing, ranged reads, the owner inventory | Written; implemented |

Documents 01 and 02 are implemented in full and run over a real TLS 1.3 socket, in `FallbackPlan.Protocol`: the keypair and its durable device key, the pairing ceremony (key agreement, transcript, short authentication string, confirmation signature and the four messages that carry them), grants (01 §3) and terms (01 §4), and the whole session layer of 02 — the four-state machine, channel-bound authentication, framing with its pre-allocation bounds, version selection and feature negotiation, and the coarse refusal codes. `FallbackPlan.Protocol.Tests` exercises all of it over loopback TCP, including the man-in-the-middle relay that channel binding defeats; `FallbackPlan.Hosts.Tests` performs the pairing ceremony between two real operating-system processes.

Documents 01 and 02 are the two that [ADR-0028 §5](../../docs/adr/0028-service-boundary-and-deployment-topologies.md)'s remote binding was blocked on: a console pairs and opens a session by the same rules a peer does, and carries a different payload over it. They were written first for that reason, and that binding now exists — a paired console reaches the service over the wire, an unpaired one is refused.

Document 03 is now written, and its base object exchange is implemented: a source pushes a repository's objects to a paired destination over an Open session, the destination stores the ciphertext it cannot read, and the transfer is resumable because each object commits whole or not at all. `FallbackPlan.Hosts.Tests` proves it end to end over loopback — a source's objects mirror to a destination byte for byte, and the standalone recovery tool restores the original files from the replica. What 03 defers to a later slice is the optimization, not the mechanism: a compact object-set filter (an optional negotiated feature) in place of the explicit inventory, and snapshot-scoped replication in place of the whole-repository scope.

Document 07 is now written and implemented (ADR-0041): an owner opens its own replica over the session — attribution-authorised, read-only, ciphertext both ways — lists and range-reads it, and a hub that lost its staging archive restores from a peer over the wire alone; `FallbackPlan.Hosts.Tests` proves that drill end to end between two live services.

Document 05 is now written and its enforcement implemented: a destination attributes each replica to the peer that offered it, refuses `terms_refused` at the object boundary when the peer's quota would be crossed, refuses `storage_exhausted` when its own storage fails, and announces its current terms in every hello so a source learns a narrowing before the first refusal. Document 04 is written and implemented: challenges ride the replication session after the acknowledgement, a tampered byte at the destination fails the proof and is recorded durably at the source, and the same fact is earned by local-path replicas through direct read-back — both destination kinds answer to bytes, not to their word.

## Requirement language

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHALL NOT**, **SHOULD**, **SHOULD NOT**, **RECOMMENDED**, **MAY** and **OPTIONAL** are to be interpreted as described in [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119) and [RFC 8174](https://www.rfc-editor.org/rfc/rfc8174), when and only when they appear in capitals.

## Design constraints a reader should know up front

**Neither party trusts the other.** A source does not trust a destination to hold what it claims to hold — that is what [09 §5](../../docs/architecture/09-replication-and-peers.md#5-destination-verification)'s challenge exists for. A destination does not trust a source not to send it malformed input — every length on this wire is bounded before allocation, exactly as in the repository format ([00 §8](../repository-format/00-conventions.md#8-lengths-and-limits)). → [`threat-model.md` T-7](../../docs/threat-model.md#t-7-malicious-or-malformed-protocol-input)

**A destination is not a repository member.** It holds blobs it cannot decrypt and has no key material. Every message in this protocol is designed so that serving it requires no ability to read what is being served.

**The identity is the key.** There is no certificate authority, no name to validate, and no notion of a peer's identity separate from its public key ([ADR-0030](../../docs/adr/0030-peer-identity-and-pairing.md)).

**Refuse, never guess.** As in the repository format ([00 §10](../repository-format/00-conventions.md#10-error-handling-posture)), the required response to anything unexpected is to refuse with a stated reason and close. There is no lenient path.

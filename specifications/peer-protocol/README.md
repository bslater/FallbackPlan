# FallbackPlan peer protocol — specification

**Protocol version:** 1 (draft) · **Status:** incomplete — verification and quotas unwritten; see [Documents](#documents) · **Implemented:** 01, 02, and 03's base object exchange, over a real TLS socket

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

This set is **incomplete, and the table says which parts**. A missing document is stated here rather than discovered by an implementer halfway through, because the alternative — drafting the remaining three thinly so the set *looks* whole — is how a specification becomes something nobody can build from.

| # | Document | Covers | Status |
|---|----------|--------|--------|
| — | [Conventions](00-conventions.md) | What is inherited from the repository format, and what differs | Written |
| 01 | [Identity and pairing](01-identity-and-pairing.md) | Peer keypairs, the pairing ceremony with negotiated storage roles, pinning, grants and terms | Written; implemented |
| 02 | [Session](02-session.md) | Transport, handshake, feature negotiation, framing, errors | Written; implemented |
| 03 | [Replication](03-replication.md) | The object exchange: scope, have/want, ranged transfer, resumption | Written; base exchange implemented |
| 04 | Verification | The keyed random-range challenge and its sampling policy | **Not written** |
| 05 | Quotas | Exhaustion, disk-full, and their distinct reporting | **Not written** |

Documents 01 and 02 are implemented in full and run over a real TLS 1.3 socket, in `FallbackPlan.Protocol`: the keypair and its durable device key, the pairing ceremony (key agreement, transcript, short authentication string, confirmation signature and the four messages that carry them), grants (01 §3) and terms (01 §4), and the whole session layer of 02 — the four-state machine, channel-bound authentication, framing with its pre-allocation bounds, version selection and feature negotiation, and the coarse refusal codes. `FallbackPlan.Protocol.Tests` exercises all of it over loopback TCP, including the man-in-the-middle relay that channel binding defeats; `FallbackPlan.Hosts.Tests` performs the pairing ceremony between two real operating-system processes.

Documents 01 and 02 are the two that [ADR-0028 §5](../../docs/adr/0028-service-boundary-and-deployment-topologies.md)'s remote binding was blocked on: a console pairs and opens a session by the same rules a peer does, and carries a different payload over it. They were written first for that reason, and that binding now exists — a paired console reaches the service over the wire, an unpaired one is refused.

Document 03 is now written, and its base object exchange is implemented: a source pushes a repository's objects to a paired destination over an Open session, the destination stores the ciphertext it cannot read, and the transfer is resumable because each object commits whole or not at all. `FallbackPlan.Hosts.Tests` proves it end to end over loopback — a source's objects mirror to a destination byte for byte, and the standalone recovery tool restores the original files from the replica. What 03 defers to a later slice is the optimization, not the mechanism: a compact object-set filter (an optional negotiated feature) in place of the explicit inventory, and snapshot-scoped replication in place of the whole-repository scope.

Documents 04–05 have their behaviour fixed in architecture already — [09 §5](../../docs/architecture/09-replication-and-peers.md#5-destination-verification) gives the challenge construction and the reasoning behind it, [09 §6](../../docs/architecture/09-replication-and-peers.md#6-quotas-and-exhaustion) gives the exhaustion semantics — so what is missing is the wire encoding, not the design.

## Requirement language

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHALL NOT**, **SHOULD**, **SHOULD NOT**, **RECOMMENDED**, **MAY** and **OPTIONAL** are to be interpreted as described in [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119) and [RFC 8174](https://www.rfc-editor.org/rfc/rfc8174), when and only when they appear in capitals.

## Design constraints a reader should know up front

**Neither party trusts the other.** A source does not trust a destination to hold what it claims to hold — that is what [09 §5](../../docs/architecture/09-replication-and-peers.md#5-destination-verification)'s challenge exists for. A destination does not trust a source not to send it malformed input — every length on this wire is bounded before allocation, exactly as in the repository format ([00 §8](../repository-format/00-conventions.md#8-lengths-and-limits)). → [`threat-model.md` T-7](../../docs/threat-model.md#t-7-malicious-or-malformed-protocol-input)

**A destination is not a repository member.** It holds blobs it cannot decrypt and has no key material. Every message in this protocol is designed so that serving it requires no ability to read what is being served.

**The identity is the key.** There is no certificate authority, no name to validate, and no notion of a peer's identity separate from its public key ([ADR-0030](../../docs/adr/0030-peer-identity-and-pairing.md)).

**Refuse, never guess.** As in the repository format ([00 §10](../repository-format/00-conventions.md#10-error-handling-posture)), the required response to anything unexpected is to refuse with a stated reason and close. There is no lenient path.

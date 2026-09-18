# ADR-0059 — A deletion instruction is bound to the session it was authorised in, and no session may waive the requirement

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-GC-007, FR-GC-008
**Related:** [ADR-0020](0020-ed25519-signing-key-semantics.md), [ADR-0030](0030-peer-identity-and-pairing.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0053](0053-peer-claim-and-configuration-recovery.md), [ADR-0055](0055-reclaim-authority.md)

---

## Context

[ADR-0055](0055-reclaim-authority.md) §5 gave the peer retention instruction a
signature under the repository's reclaim key, so that a destination holding no
repository keys could tell a deletion authorised by the reclaim authority from
one sent by whoever merely held the session. It closed forgery of a page and
editing of a page, and it recorded — in the specification, in `FR-GC-008`'s
*"Not yet met"* clause, and in the proof-obligation row — that it did not close
**replay**: the signature covers the page's own bytes, which say who authorised
an instruction and never when.

Writing the test for that found something larger, in the same code.

**The requirement to sign was itself waivable by the party it defends against.**
`signed-retention` is a negotiated feature; negotiation is an intersection of
what the two sides offer; and a listener cannot require a feature — the accept
path passes none and no overload would let it. So whether signatures were in
force was decided by *both* sides offering, and the responder's check opened
with an early return when the feature was absent. A source that simply omitted
it from its hello had an **unsigned, freshly composed** drop-list obeyed.

That is forgery rather than replay — an arbitrary list instead of one the
reclaim authority once approved — and it needs no captured page and no reclaim
key, only the peer device key, which is exactly what a compromised write-only
service holds. It is the hole the whole reclaim split exists to close, reached
by declining to participate in it.

ADR-0055 had already made this argument against itself, on the repository
plane. Its §4 chose a *required* descriptor feature over a per-object tombstone
schema version because "a per-object version is a per-object choice, and the
attacker chooses". An optional negotiated feature is a per-session choice. The
reasoning simply was not carried to the wire.

## Decision

### 1 A feature may never be the sole gate on a check that defends one side against the other

Stated in [02 §6](../../specifications/peer-protocol/02-session.md#6-feature-negotiation)
as a rule of the protocol rather than a fix to one message, because it is not
about retention. The intersection is computed from both hellos, so conditioning
such a check on a feature hands the decision to the party being checked.

Features remain right for what the other side can **understand** — which is
what every other feature in the registry is about, and what makes them the
wrong tool for what the other side is **allowed**.

### 2 A spoke enforces on the reclaim key it recorded

The durable fact already exists and is written by the spoke for itself: the
reclaim public key recorded when the repository was first attributed to this
peer ([05 §2](../../specifications/peer-protocol/05-quotas.md#2-ownership),
ADR-0055 §5). Holding one means this repository's owner published a reclaim
authority, and no later session can take that back.

So: **if the spoke holds a key, it requires a signature.** `signed-retention`
survives as an announcement — it still tells a commander at the hello what it
will be held to, so a repository publishing no reclaim key learns before the
objects cross rather than after — and it decides nothing. The responder no
longer takes it as an argument at all, because a flag that once was
load-bearing and is not any more will be read as though it still were.

A peering established before the key existed records none, has nothing to check
against, and is unaffected. That is the whole of the compatibility surface and
it closes by itself: the key is published on the first offer a current build
sends.

### 3 The signature covers the session

`RetentionOffer`'s signed bytes gain the 32-byte session identifier of
[02 §3.5](../../specifications/peer-protocol/02-session.md#35-the-session-identifier),
appended last and fixed-length — so a bound page is the unbound one plus a tail
that is always 32 bytes or none, and neither encoding can be read as the other.

The identifier costs nothing on the wire. Every session already builds the
context of [02 §3.2](../../specifications/peer-protocol/02-session.md#32-the-bound-transcript)
— a binding version, both identities, both TLS certificate hashes, both fresh
nonces — to authenticate with. It was computed, consumed and discarded; the
plan for this work, and the specification, both said this revision carried no
session-unique material, and both were wrong.

It is derived under a **role-neutral** label, where the transcript deliberately
uses two role-specific ones: a proof must not be reflectable, and an identifier
must be reachable by both ends alike. The separate label is what keeps the two
constructions from meeting.

### 4 A commander asks what the spoke can check; a spoke asks what it said

These are two different questions and answering them from one place is what
made decision 1 necessary.

A **commander** binds when the session negotiated `session-bound-retention`.
A household updates one machine before the other, so a current commander will
meet an older spoke, and signing an encoding that spoke cannot verify would
refuse every page of an instruction meant for it. This is the legitimate use of
a feature: what the other side understands.

A **spoke** requires whichever form **it offered**, never what the intersection
says. This was wrong in the first attempt and the compatibility test caught it:
reading the requirement out of the intersection puts half the decision back in
the source's hands, which is decision 1's mistake one layer down. What this
build offers is a fact about this build.

A spoke MUST NOT accept both forms. Accepting both is accepting the replayable
one, since nothing stops a replay presenting itself as the older encoding.

### 5 The older commander is refused, and that is the right trade

An older commander signs unbound; a current spoke requires bound; the
instruction is refused by name until the commander is upgraded. It costs a
deletion not made — retention stalls, the replica keeps more than its policy
wants — and never a backup not taken, because replication itself is untouched.

Between "a deletion I cannot bind to now" and "a replica that grows until
somebody upgrades the other machine", the second is the failure a backup
product should choose.

### 6 No page ordinal

Considered and rejected. It would close a page replayed within its own session,
a page reordered, and a page dropped — and none of those is reachable. The
pages ride one TLS stream, so no outside party can reorder or drop one; the
responder admits at most one retention exchange per session; and duplicate keys
inside one exchange delete idempotently. The only party who can omit a page is
the commander, which could equally have composed a shorter list.

It would have cost a new map key, per-session state, and a new way for a
legitimate exchange to be refused, to defend against nobody.

## Consequences

**Positive**

- The reclaim authority means what ADR-0055 said it meant: a service that may
  publish for ever cannot delete, and cannot arrange to be excused from proving
  it.
- A recorded instruction is worthless after its session ends, so an attacker
  who captures one has captured a transcript rather than a capability.
- The session identifier is now available to anything else that needs to say
  *this session* — [ADR-0053](0053-peer-claim-and-configuration-recovery.md)'s
  claim ceremony wants a fresh nonce for exactly this reason and can use this
  instead of inventing one.

**Negative**

- An older commander's retention is refused by a current spoke until it is
  upgraded (decision 5). The refusal is loud and names the remedy; it is still
  a version skew that stops something working.
- A spoke whose attribution predates the reclaim key still accepts unsigned
  instructions, and nothing upgrades it but a fresh offer from a current build.
  It is the same compatibility surface ADR-0055 opened, not a new one.
- One more thing that must stay in step between the two ends, with a failure
  mode — a signature that does not verify — whose message has to carry the
  diagnosis, because the bytes cannot.

**Unchanged, and still not met**

> **Closed by [ADR-0063](0063-deletion-receipts.md) (2026-09).** The artefact
> this paragraph described now exists: a deletion receipt signed under the
> destination's device key, carried in the `RetentionAck`, verified by the
> commander against the instruction it sent and filed by both parties, with
> `receipts` as its reader. The paragraph stands as written for what this
> record decided and left open.

`FR-GC-008` promises signed **audit records**, and a destination still keeps no
signed record of what it deleted — only the count it acknowledges. A receipt
would have to be signed under the destination's own device key, since it holds
no repository keys, which is a different kind of artefact with its own
lifetime and its own reader. It stays in the requirement as not met; this
record does not absorb it.

## Alternatives considered

**A TLS exporter (RFC 5705).** The textbook channel binding, and .NET's
`SslStream` exposes no keying-material exporter, so it is unavailable rather
than rejected. It would also buy less than the transcript does: the context
already names both permanent peer identities, which an exporter does not.

**Bind to the destination's nonce alone.** Enough for freshness — the attacker
is the initiator and cannot influence the responder's fresh bytes — and fewer
moving parts. Rejected because a bare nonce appended to signed bytes is an
unlabelled blob that the next reader cannot tell from a digest or a counter,
and will eventually "simplify". A named derivation with a docstring, sitting
beside the transcript where the role-reflection reasoning already lives, costs
one hash per session and rules out cross-identity replay as a by-product.

**A timestamp and a window.** No new state, and it puts a clock in the trust
path: two households whose clocks disagree stop replicating, and an attacker
with a captured page waits for the window rather than being refused.

**A per-instruction nonce from the spoke.** A round trip per instruction, which
is what ADR-0053's claim ceremony does — correctly, because a claim happens
once. A retention pass sends many pages and already has a fresher binding to
hand.

**A sticky per-grant flag** recording that a peer was once seen offering
`signed-retention`, refusing a later session that omits it. The HSTS shape, and
it survives a device-key compromise. Rejected because the attribution already
carries a stronger fact — the key itself — so the flag would be a second,
weaker record of the same thing, with a first-use window the key does not have.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Amended (audit half closed) | The half this record left as "still not met" — a destination's signed record of what it deleted — is built by [ADR-0063](0063-deletion-receipts.md) as the deletion receipt, carried in the acknowledgement this record's signature already bound to the session; the scoped note at that paragraph points there. `Hosts.Tests/PeerRetentionReplayTests` holds the receipt off the same listener it holds the replay refusal off |
| 2026-09 | Accepted | Closes the replay window [ADR-0055](0055-reclaim-authority.md) §5 recorded, and the larger downgrade found while closing it. Built: `Protocol/SessionBinding` derives the session identifier, `Protocol/PeerAuthenticator` and `Protocol/PeerSessionDriver` surface it as `PeerSession.Binding`, `Protocol/PeerReplicationMessages` signs over it, `Protocol/PeerSessionNegotiation` carries the feature, `Agent/ReplicationResponder` enforces on the recorded reclaim key rather than on the hello, `Agent/RemoteServiceListener` decides the requirement from what it offered, and `Agent/FanOut` binds only where the spoke can check it. Held by `Hosts.Tests/PeerRetentionReplayTests`, `Protocol.Tests/PeerWireTests` and `Protocol.Tests/ReplicationMessageTests` |

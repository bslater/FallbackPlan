# 01 — Identity and pairing

**Normative.** Rationale in [ADR-0030](../../docs/adr/0030-peer-identity-and-pairing.md) and [architecture 09 §3](../../docs/architecture/09-replication-and-peers.md#3-pairing).

---

## 1 Peer identity

A device MUST hold exactly one long-lived **peer keypair**, generated on first use with a CSPRNG:

| Part | Type | Storage |
|------|------|---------|
| Peer signing key | Ed25519 private key, `bytes[32]` | Durable local state, service-account confined |
| **Peer identity** | Ed25519 public key, `bytes[32]` | Durable local state; sent in the clear |

The public key **is** the identity. A peer is never identified by a name, an address, or a `device_id`; those are labels displayed beside the key and carry no authority. → [ADR-0030 §1](../../docs/adr/0030-peer-identity-and-pairing.md#1-a-peer-is-a-public-key-held-per-device-unrelated-to-the-repository)

The peer keypair MUST NOT be derived from, or derivable from, the repository master key or any key in the repository hierarchy. It is generated independently and is unrelated to repository membership: a destination holds one without holding any repository key at all.

**Peer fingerprint.** Where a key must be shown to a human or used as a map key, it is rendered as the base32 form ([00 §6](../repository-format/00-conventions.md#6-object-identifiers-in-paths)) of

```text
fingerprint = SHA-256(peer_identity)[0..16]
```

— 16 bytes, 26 base32 characters. The fingerprint is a display and indexing convenience; **verification is always against the full key**, never the fingerprint.

## 2 The pairing ceremony

Pairing runs over a channel neither side trusts yet, and is authenticated by the two humans rather than by any shared key. It has four messages and two approvals.

### 2.1 Roles

The side that initiates is the **offerer**; the side that accepts is the **responder**. These wire roles decide message order only. Neither confers authority, and the *destination* sets terms (§4) regardless of which side initiated.

Distinct from the wire roles are the **storage roles** each side will record in its grant (§3): who stores for whom. Each side declares, inside the ceremony, the role it will record for the other (key 7 below), both declarations enter the transcript (§2.3), and both humans therefore approve them with everything else — the two grants cannot silently disagree about which of them lends the disk ([ADR-0030 Amendment 2](../../docs/adr/0030-peer-identity-and-pairing.md#amendment-2-2026-08--the-pairing-lifecycle-completes-roles-on-the-wire-endings-announced-terms-enforced)). A message that omits key 7 MUST be refused as malformed: it comes from a build predating this rule, and pairing with it would record a role only one side saw.

### 2.2 Messages

Each message is a CBOR map carried in one frame ([02 §7](02-session.md#7-framing)), bounded by the pairing-message limit of [00 §2.3](00-conventions.md#23-limits-are-the-protocols-own).

**`PairOffer`** (offerer → responder)

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `bytes[32]` | Offerer peer identity (Ed25519 public key) |
| 2 | `bytes[32]` | Offerer ephemeral X25519 public key |
| 3 | `bytes[32]` | Offerer nonce, fresh from a CSPRNG |
| 4 | `text` | Offerer label, for display (≤ 256 bytes) |
| 5 | `u16` | Highest protocol version the offerer speaks |
| 7 | `u8` | Storage role the offerer will record for the responder (§3 vocabulary: 1, 2 or 3) |

**`PairAccept`** (responder → offerer)

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `bytes[32]` | Responder peer identity |
| 2 | `bytes[32]` | Responder ephemeral X25519 public key |
| 3 | `bytes[32]` | Responder nonce, fresh from a CSPRNG |
| 4 | `text` | Responder label |
| 5 | `u16` | Protocol version selected, ≤ the offerer's |
| 6 | `map` | Terms, per §4 — present when the responder is the destination |
| 7 | `u8` | Storage role the responder will record for the offerer (§3 vocabulary) |

**`PairConfirm`** (either → other, after that side's human approves)

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `bytes[64]` | Ed25519 signature over the transcript (§2.4) |

**`PairRefuse`** (either → other)

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u16` | Reason code ([02 §8](02-session.md#8-errors-and-refusal)) |
| 2 | `text` | Reason, for a human |

### 2.3 Deriving the short authentication string

Both sides compute the X25519 shared secret and then:

```text
transcript = offerer_identity   ‖ responder_identity
           ‖ offerer_ephemeral  ‖ responder_ephemeral
           ‖ offerer_nonce      ‖ responder_nonce
           ‖ u16(protocol_version)
           ‖ u8(offerer_declared_role) ‖ u8(responder_declared_role)

sas_key = HKDF-SHA-256(
              salt    = offerer_nonce ‖ responder_nonce,
              ikm     = x25519_shared_secret,
              info    = "fbp-peer-v1:sas" ‖ transcript)

sas = first 30 bits of sas_key
```

The 30 bits are rendered as **six lowercase base32 characters** ([00 §6](../repository-format/00-conventions.md#6-object-identifiers-in-paths)) — six characters carry exactly 30 bits, with no padding and no remainder — displayed in two groups of three. A QR code carrying the transcript hash MAY be offered alongside, for devices that can scan.

Earlier drafts — and [ADR-0028](../../docs/adr/0028-service-boundary-and-deployment-topologies.md), which predates this document — described this as "short authentication words". A word rendering is **deferred**, and deliberately: words are easier for two people to read aloud, but a word list only helps if both implementations use the *same* list, which means this specification would have to carry 1 024 words normatively. That is a large thing to mandate for a display convenience, and getting it wrong — near-homophones, shared prefixes — makes comparison worse rather than better. Base32 is already required of every implementer for identifiers, so it costs nothing to obtain and is unambiguous under case folding.

Both long-lived identities and both nonces are inside the derivation, so two concurrent sessions relayed by an attacker produce different strings on each side. **The comparison is what defeats the relay**; the ceremony exists to make a human actually perform it. The declared storage roles are inside it for the same reason: an intermediary that altered who-stores-for-whom would alter the string the humans compare.

### 2.4 Approval and pinning

1. Each side displays its `sas` and the other side's label and fingerprint.
2. Each human confirms the strings match, **on their own device**.
3. On confirmation, each side sends `PairConfirm` carrying an Ed25519 signature by its peer signing key over `"fbp-peer-v1:confirm" ‖ transcript`.
4. A side that receives a valid `PairConfirm` **and** has its own human's approval writes the grant (§3).

A `PairConfirm` whose signature does not verify against the identity in the offer or accept MUST cause immediate refusal with the session closed. A confirm arriving before local approval MUST be held, not auto-approved; if local approval never comes, the pairing is refused.

Both approvals are required. A design where one side can pair unilaterally is a design where a device can be enrolled into someone else's estate without its owner acting.

### 2.5 Rejecting a changed identity

Once pinned, a peer identity is fixed. On any later session, a peer presenting a key that differs from the pinned one MUST be refused with reason `identity_changed`, the session closed, and the event surfaced to the operator.

A conforming implementation MUST NOT offer a control that accepts the new key in place of the old as part of that refusal. Re-pairing MUST require the operator to remove the existing grant deliberately, as a separate act. → [architecture 09 §3](../../docs/architecture/09-replication-and-peers.md#3-pairing)

This is the one place this specification constrains a user interface, and it does so because "your friend's key changed — continue?" is a question no user has ever answered correctly under time pressure.

### 2.6 Why thirty bits

An attacker relaying between two sessions must make both strings match, and cannot influence the derivation except by choosing its own ephemeral share and nonce. Thirty bits puts that at 2⁻³⁰ per attempt, and an attempt costs a full ceremony with two humans watching it fail.

The number is chosen against *retries*, not against a single attempt. Twenty bits would still be 2⁻²⁰ once — but a failed pairing looks exactly like flaky software, so the humans try again, and an attacker who can provoke enough retries gets enough attempts. Thirty bits keeps that out of reach while staying short enough that someone will actually compare all six characters rather than the first two and the last one.

## 3 Grants

A successful pairing writes a **grant** on each side, in durable local state ([NFR-REL-007](../../docs/requirements/non-functional.md)):

| Field | Type | Meaning |
|-------|------|---------|
| `peer_identity` | `bytes[32]` | The pinned key |
| `label` | `text` | Human-chosen, freely editable, no authority |
| `role` | `u8` | 1 = this peer may store objects here; 2 = this peer stores objects for us; 3 = both |
| `terms` | map | §4, as set by the destination |
| `paired_at` | `u64` | Unix milliseconds, informational |

Grants are **revocable at either side, unilaterally, at any time**. Revocation takes effect on the next session; it does not reach across the network to delete anything already stored, and this specification does not pretend otherwise. What a destination does with objects it already holds after revoking is its own policy.

Revoking a grant leaves a **tombstone**: the revoked identity's fingerprint, retained after the grant itself is gone. The tombstone is what lets a later session from that peer be refused `revoked` rather than `not_paired` ([02 §8](02-session.md#8-errors-and-refusal)) — the difference between "the peering was ended" and "you were never here", which call for different operator action. Re-pairing is the deliberate second act [§2.5](#25-rejecting-a-changed-identity) requires, and it clears the tombstone; the list grows only with deliberate endings, never with strangers.

### 3.1 Ending a peering

A side ending a peering SHOULD announce it before revoking, while its grant still authenticates a session — a peering that simply goes silent is indistinguishable from an outage, and the human on the other side deserves the distinction ([FR-DEST-008](../../docs/requirements/functional.md#destinations-and-fan-out), [ADR-0030 Amendment 2](../../docs/adr/0030-peer-identity-and-pairing.md#amendment-2-2026-08--the-pairing-lifecycle-completes-roles-on-the-wire-endings-announced-terms-enforced)).

**`PeeringTermination`** (type 10, [02 §7](02-session.md#7-framing)):

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `text` | Reason, for a human (≤ 256 bytes) |
| 2 | `u16` | Suggested grace, days — 0 for no suggestion |

The message is gated by the **`termination-notice`** feature ([02 §6](02-session.md#6-feature-negotiation)): a side MUST NOT send it unless the feature is in the session's intersection, because an ungated build answers an unknown type with `message_unknown` and the announcement would end in a refusal instead of a notice. Against a peer that does not offer the feature, the sender revokes locally and lets the fallback below carry the fact.

It is sent in the `Open` state, in place of the payload exchange the session would otherwise carry, and it is the last message of the session. The receiver MUST record a **durable notice** that survives restarts and is surfaced until a human acknowledges it, and MUST revoke its own grant for the sender. Objects it already stores for the ended peering remain its own to keep or evict on its own timetable — the grace in key 2 is the sender's suggestion, not a protocol obligation, and nothing here enforces it.

Delivery is **best effort**. A peer that is unreachable, or that never offers a session again, is never told directly; it learns at its next dial, when the tombstone above turns its authentication into a `revoked` refusal. A dialler refused `revoked` MUST record the same durable notice the announcement would have produced — the refusal is the fallback delivery of the termination, not merely an error.

## 4 Terms

Terms are set by the **destination** — the side that will store objects — and travel with the grant:

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u64` | Quota, bytes |
| 2 | `text` | Schedule window, in the [ADR-0027](../../docs/adr/0027-services-scheduling-status-telemetry.md) expression language, or empty for "any time" |
| 3 | `u32` | Retention floor, generations the destination will keep regardless of source policy |

A source MUST NOT request terms more generous than those offered. A source MAY operate under narrower ones of its own choosing.

When a destination changes its terms, it sends the new ones at the next session's hello. The source continues under them. Where the new terms are **narrower** than those the source is relying on, the source MUST report the affected backup set as `degraded` for that destination rather than silently failing to replicate — an unexplained stall is the failure mode [architecture 09 §2](../../docs/architecture/09-replication-and-peers.md#2-transport) calls out for fairness and it applies here too.

The storage path is deliberately **not** on the wire. It is the destination's business, and a source that knew it would be a source that could name it. → [architecture 09 §3](../../docs/architecture/09-replication-and-peers.md#3-pairing)

---

**Previous:** [00 — Conventions](00-conventions.md) · **Next:** [02 — Session](02-session.md)

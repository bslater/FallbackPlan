# 02 — Session

**Normative.** Rationale in [ADR-0030](../../docs/adr/0030-peer-identity-and-pairing.md) and [architecture 09 §2](../../docs/architecture/09-replication-and-peers.md#2-transport).

---

## 1 Transport

A session runs over **TLS 1.3** ([RFC 8446](https://www.rfc-editor.org/rfc/rfc8446)) over TCP, or over **QUIC** ([RFC 9000](https://www.rfc-editor.org/rfc/rfc9000)) with the same TLS 1.3 handshake. Whether a session is direct or relayed MUST be reported to the operator either way ([architecture 09 §7](../../docs/architecture/09-replication-and-peers.md#7-relay)).

**TLS provides confidentiality, integrity and key exchange. It does not provide peer identity here.** Authentication is the protocol's own, in §3.

Each side MUST present a **self-signed certificate generated for that connection alone**. Such a certificate:

- MUST be generated afresh for every connection;
- MUST NOT be persisted;
- MUST NOT be reused across connections;
- MUST carry a key the peer identity of [01 §1](01-identity-and-pairing.md#1-peer-identity) is *not* derived from and has no relationship to;
- SHOULD be destroyed once the connection ends.

Both sides request and present one; the client certificate message is not optional. Neither side makes any trust decision about the certificate it receives — no chain is built, no name is checked, no authority is consulted, and there is no certificate this design would reject on the strength of who issued it. **A certificate here is a container for an ephemeral key and nothing more.** Its only durable role is the hash of its public key, which §3 binds the authentication proof to.

**No trust decision occurs during the TLS handshake.** An implementation MUST NOT treat a completed handshake as evidence of identity. → [`threat-model.md` T-7](../../docs/threat-model.md#t-7-malicious-or-malformed-protocol-input)

**Pairing is the exception.** The ceremony of [01 §2](01-identity-and-pairing.md#2-the-pairing-ceremony) runs before any key is pinned, so it cannot pin one and cannot authenticate under §3 either. It runs over the same encrypted, unauthenticated channel, and the ceremony's short authentication string is what authenticates it — a man in the middle necessarily runs two different key agreements, so the two humans read different strings. A conforming implementation MUST NOT reuse a connection that has only completed pairing for anything but pairing.

### 1.1 Why not raw public keys

Earlier drafts of this document required TLS 1.3 carrying **raw public keys** ([RFC 7250](https://www.rfc-editor.org/rfc/rfc7250)), with X.509 prohibited outright. That is the mechanism this design wants: the pinned key *is* the expected identity, and a certificate is a container for a check that is already exact.

It is not available. The reference platform exposes TLS through an API that is certificate-shaped throughout, with no way to supply or validate a bare public key; it also exposes no keying-material exporter ([RFC 5705](https://www.rfc-editor.org/rfc/rfc5705), [RFC 8446 §7.5](https://www.rfc-editor.org/rfc/rfc8446#section-7.5)), which is the usual second choice for binding an application protocol to its channel. Embedding an Ed25519 identity in a self-signed certificate fails for a third reason: the platform has no Ed25519 certificate support to build or consume one with, and one major TLS provider does not accept such certificates at all. Replacing the platform TLS stack would solve it and is refused on blast radius ([ADR-0019](../../docs/adr/0019-third-party-dependency-policy.md)).

**So the objective is not to implement RFC 7250. It is to preserve the guarantees RFC 7250 was chosen for**, using mechanisms the platform supports. §3 does that: the pinned Ed25519 key still authenticates the channel, and X.509 is still trusted for nothing. What moved is *where* the check happens, not what it establishes.

Should the platform later gain raw public keys, a keying-material exporter, or Ed25519 certificates, this protocol MAY adopt them to strengthen the binding. The application-layer proof of §3 remains the authoritative source of peer identity regardless, so such a change would not alter what a session means. → [ADR-0030 §4](../../docs/adr/0030-peer-identity-and-pairing.md#4-the-pinned-key-authenticates-the-channel)

## 2 Session states

A session advances through these states and MUST NOT skip one:

| State | Reached by | What it permits |
|-------|-----------|-----------------|
| `Connected` | TCP or QUIC connection established | Nothing but a TLS handshake |
| `Encrypted` | TLS 1.3 handshake completed | `SessionAuth`, `SessionAuthProof`, `SessionRefuse`, and the pairing ceremony — nothing else |
| `Authenticated` | §3 satisfied in both directions | `SessionHello`, `SessionAccept`, `SessionRefuse` |
| `Open` | §4 completed | The payload documents ([03–05](README.md#documents)) |

A message that is not permitted in the current state MUST be refused as `malformed`, and the session closed.

The states are named and enforced rather than left implicit because `Encrypted` is the state that looks finished and is not. An implementation that treats a completed handshake as an authenticated session has a stranger inside the protocol, and every check after that point is being applied to input it has no reason to believe. → NFR-SEC-009

## 3 Authentication

Immediately on reaching `Encrypted`, each side proves possession of the private half of its peer identity, over a transcript that binds that permanent identity to *this* TLS connection.

The side that opened the connection is the **initiator**; the side that accepted it is the **responder**. Each knows which it is without asking.

### 3.1 Messages

Each side sends `SessionAuth` at once, without waiting for the peer's. On receiving the peer's, each side sends `SessionAuthProof`. Authentication therefore costs one round trip.

**`SessionAuth`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `bytes[32]` | This side's Ed25519 peer identity ([01 §1](01-identity-and-pairing.md#1-peer-identity)) |
| 2 | `bytes[32]` | A nonce, freshly random for this connection |

**`SessionAuthProof`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `bytes[64]` | Ed25519 signature over §3.2 |

### 3.2 The bound transcript

Both sides construct the same byte string, which is fixed-length by construction:

```text
context = u16(binding_version)
        ‖ initiator_identity[32]  ‖ responder_identity[32]
        ‖ initiator_spki_hash[32] ‖ responder_spki_hash[32]
        ‖ initiator_nonce[32]     ‖ responder_nonce[32]
```

where `spki_hash` is `SHA-256` over the DER-encoded `SubjectPublicKeyInfo` of that side's TLS certificate, and `binding_version` is `1`.

Each side signs its **role-specific label** followed by that context:

```text
initiator_proof = Ed25519-Sign(initiator_key, "fbp-peer-v1:tls-binding:initiator" ‖ context)
responder_proof = Ed25519-Sign(responder_key, "fbp-peer-v1:tls-binding:responder" ‖ context)
```

The two labels are the role separation. Without it, an attacker could open a connection back to a peer and reflect that peer's own proof at it, and the peer would verify its own signature and be satisfied. → [00 §4](00-conventions.md#4-domain-separation)

`binding_version` is the version of *this construction*, not the negotiated protocol version of §5 — which is not yet known, because §4 has not run. Authentication precedes negotiation deliberately: a stranger has no business learning what features a device supports or what terms it offers.

### 3.3 What each side must check

A side MUST NOT enter `Authenticated` until **all** of the following hold for the peer:

1. the presented identity is byte-for-byte the key pinned in the grant ([01 §2.5](01-identity-and-pairing.md#25-rejecting-a-changed-identity)) — refuse `identity_changed` where the presented key differs from one this side was expecting, and `not_paired` where no grant exists;
2. the signature verifies against that identity over the transcript of §3.2 built with the roles as they actually are;
3. the transcript's `spki_hash` values are those of the certificates actually exchanged on **this** connection.

A failure of (2) or (3) is refused as `authentication_failed`. The two are not distinguished on the wire: both mean the peer did not prove what it claimed, and which check caught it is not a stranger's business ([§8](#8-errors-and-refusal)).

**Only a side that was expecting a particular peer can report `identity_changed`.** An initiator dialled a pinned identity and can say that something else answered — which is the case [01 §2.5](01-identity-and-pairing.md#25-rejecting-a-changed-identity) exists for. A responder taking an inbound connection expected nobody in particular, so an unrecognised key there is `not_paired` and MUST NOT be reported as anything else. Distinguishing them would require knowing which pairing a stranger *meant* to be, and the only thing a stranger has offered is the key that is wrong.

**`revoked` requires a retained revocation record.** Revocation removes a grant, and a side that kept no record of the removal would honestly know only `not_paired`. [01 §3](01-identity-and-pairing.md#3-grants) therefore keeps a tombstone — the revoked identity's fingerprint, nothing more — because the two refusals call for different operator action, and because `revoked` is the fallback delivery of a termination the peer may never have heard announced ([01 §3.1](01-identity-and-pairing.md#31-ending-a-peering)). Re-pairing clears the tombstone, so the list grows only with deliberate endings.

### 3.4 Why this holds

Suppose an attacker terminates TLS in the middle, so that there are two connections and two certificate pairs:

```text
peer A  ⟷  attacker  ⟷  peer B
```

The certificate A sees is the attacker's, not B's, so the `spki_hash` values in the transcript A verifies are not the ones B signed. The attacker cannot relay B's proof to A, because it is a signature over a different byte string; and it cannot produce the right one, because that needs B's private key. Both sides refuse. This is the property RFC 7250 was chosen for, obtained without it.

The freshness that makes each connection's transcript unique comes from two independent places: the ephemeral certificate keypair, which an attacker cannot use without its private key, and the nonces. The nonces are carried because §1's "never reuse a certificate" is a rule a peer **cannot verify about the other side** — it would have to remember every certificate it had ever seen. A fresh nonce from each side makes the transcript unique whether or not the peer honoured that rule, and costs nothing, since both `SessionAuth` messages are sent without waiting.

## 4 Session establishment

In `Authenticated`:

1. each side sends `SessionHello`;
2. each side either sends `SessionAccept` or refuses;
3. the session is `Open`, and payload documents ([03–05](README.md#documents)) apply.

**`SessionHello`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u16` | Lowest protocol version supported |
| 2 | `u16` | Highest protocol version supported |
| 3 | `array of text` | Feature identifiers offered, ≤ 256 ([00 §2.3](00-conventions.md#23-limits-are-the-protocols-own)) |
| 4 | `array of text` | Feature identifiers **required** of the peer |
| 5 | `text` | Agent version, for display and diagnostics only |
| 6 | `map` | Terms, per [01 §4](01-identity-and-pairing.md#4-terms) — present when this side is the destination |

Keys 1 and 2 are a **closed range**, and key 2 MUST NOT be below key 1. A hello violating that is `malformed`.

**`SessionAccept`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u16` | Protocol version selected |
| 2 | `array of text` | Features in effect — the intersection, per §6 |

The agent version in key 5 is **informational**. A conforming implementation MUST NOT branch on it. Behaviour is decided by negotiated features and by nothing else, because version-sniffing is how a protocol acquires undocumented compatibility rules that no specification can then describe.

## 5 Protocol version

The protocol version is a `u16`, independent of the repository format version ([00 §2.1](00-conventions.md#21-versioning-is-separate)) and of §3's `binding_version`.

Each side declares the closed range of versions it supports. The selected version MUST be the highest both sides speak — the lower of the two maxima, provided it is not below the higher of the two minima. Where the ranges do not overlap, a side MUST refuse with `version_unsupported`, **naming both its own range and the range it was offered**. → FR-REP-004, NFR-COMP-006

A range rather than a single version is what makes that refusal message possible, and what lets an old and a new agent meet in the middle without either having to guess what the other can do. A hello carrying one version could only ever say "this or nothing".

## 6 Feature negotiation

A feature identifier is a non-empty string of `a`–`z`, `0`–`9`, `-`, `_` and `.`. A reader MUST refuse an identifier containing anything else as `malformed`, and MUST NOT case-fold one to make it fit: an identifier two implementations spell differently and one of them silently normalises is a negotiation that disagrees with itself while both sides believe they agree.

Each side offers what it supports and separately states what it **requires** of the other.

The features in effect are the intersection of the two offered sets, and MUST be written in the accept sorted by byte value. A side MUST refuse the session when any feature it requires is absent from the other's offered set, with reason `feature_unsupported` naming the missing feature.

Both sides compute the intersection alone, from the same two hellos, and exchange no further message about it. A set has no order of its own, so without a stated one the two sides could write different accepts while both were correct.

A side MUST NOT use a feature that is not in the intersection, and MUST NOT infer support from any other signal.

This mirrors the repository format's required/optional split ([repository format 00 §5](../repository-format/00-conventions.md#5-versioning-and-feature-negotiation)) for the same reason: a peer that supports most of a version needs a way to say so, rather than having to refuse everything or claim everything.

The features defined so far:

| Identifier | Meaning |
|------------|---------|
| `termination-notice` | The peer understands `PeeringTermination` ([01 §3.1](01-identity-and-pairing.md#31-ending-a-peering)) |

The mechanism predates its first feature deliberately — retrofitting negotiation onto a deployed protocol means a flag day — and `termination-notice` is the proof it was worth specifying early: the message it gates is announced only to peers that offered it, and an older build is never sent a type it would refuse as `message_unknown`.

## 7 Framing

Every message is one frame:

```text
frame = u32(payload_length) ‖ payload
```

`payload` is a CBOR map in deterministic encoding ([00 §1](00-conventions.md#1-what-is-inherited)) whose key 0 is a `u16` **message type**. Remaining keys are defined per message.

`payload_length` MUST NOT exceed the frame limit of [00 §2.3](00-conventions.md#23-limits-are-the-protocols-own). A reader MUST check the length **before allocating**, and MUST refuse and close on a frame that exceeds it. → [`threat-model.md` T-7](../../docs/threat-model.md#t-7-malicious-or-malformed-protocol-input)

| Type | Message | Document |
|------|---------|----------|
| 1 | `PairOffer` | [01 §2.2](01-identity-and-pairing.md#22-messages) |
| 2 | `PairAccept` | [01 §2.2](01-identity-and-pairing.md#22-messages) |
| 3 | `PairConfirm` | [01 §2.2](01-identity-and-pairing.md#22-messages) |
| 4 | `PairRefuse` | [01 §2.2](01-identity-and-pairing.md#22-messages) |
| 5 | `SessionHello` | §4 |
| 6 | `SessionAccept` | §4 |
| 7 | `SessionRefuse` | §8 |
| 8 | `SessionAuth` | §3.1 |
| 9 | `SessionAuthProof` | §3.1 |
| 10 | `PeeringTermination` | [01 §3.1](01-identity-and-pairing.md#31-ending-a-peering) |
| 11–255 | Reserved for this specification | — |
| 256–261 | Replication | [03](03-replication.md#6-framing-and-limits) |
| 262+ | Reserved for [04–05](README.md#documents) | — |

A message type a reader does not know MUST cause refusal with `message_unknown`. It MUST NOT be skipped: a protocol that ignores messages it does not understand cannot tell a new feature from a corrupted stream.

A map key a reader does not know, inside a message type it *does* know, is skipped. The asymmetry is deliberate: where the shape is known, a field a later version adds cannot be mistaken for a corrupted stream, and where it is not, there is nothing to reason from.

Length framing outside TLS is deliberate rather than redundant. It bounds allocation before any CBOR is parsed, and it keeps the frame boundary a property of this protocol rather than of the transport — which is what lets QUIC substitute for TCP without changing anything above.

## 8 Errors and refusal

A side that cannot continue sends `SessionRefuse` (or `PairRefuse` during pairing) and closes. There is no error that leaves a session half-open.

**Closing follows the refusal being *read*, not merely sent.** After writing the refusal, the refusing side SHOULD hold the connection open — reading and discarding whatever the peer has in flight — until the peer closes or a short timeout passes. Tearing the connection down at once turns the peer's in-flight write into a transport reset, and on common stacks the reset purges the unread refusal from the peer's receive buffer: a `revoked` experienced as a broken pipe loses the one fact the refusal existed to carry. For the same reason, a side that receives a refusal MUST NOT answer it with a refusal of its own — it closes, and that close is what releases the refusing side's linger.

**`SessionRefuse`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u16` | Reason code |
| 2 | `text` | Reason, for a human (≤ 256 bytes) |

| Code | Name | Meaning |
|------|------|---------|
| 1 | `identity_changed` | The presented key is not the pinned key ([01 §2.5](01-identity-and-pairing.md#25-rejecting-a-changed-identity)) |
| 2 | `not_paired` | No grant exists for the presented identity |
| 3 | `revoked` | A grant existed and was revoked — only where a revocation record is kept (§3.3) |
| 4 | `version_unsupported` | No common protocol version (§5) |
| 5 | `feature_unsupported` | A required feature is absent (§6) |
| 6 | `message_unknown` | An unrecognised message type (§7) |
| 7 | `malformed` | A frame or CBOR body that violates this specification, or a message not permitted in the current state (§2) |
| 8 | `terms_refused` | The offered terms are unacceptable to the receiving side |
| 9 | `busy` | The peer cannot serve a session now; retry later |
| 10 | `pairing_declined` | A human declined ([01 §2.4](01-identity-and-pairing.md#24-approval-and-pinning)) |
| 11 | `authentication_failed` | The peer did not prove possession of the identity it presented (§3.3) |

The code is what a client branches on; the text is what a human reads. A conforming implementation MUST NOT parse the text.

**Refusal reasons are deliberately coarse.** `not_paired`, `revoked` and `identity_changed` are distinguished because they call for different operator action, but nothing here reports *why* a peer is busy, *which* internal condition produced a `malformed`, or *which* of §3.3's checks produced an `authentication_failed`. A refusal message is served to an unauthenticated or newly authenticated stranger, and detail served there is reconnaissance.

## 9 What a session does not carry

**No key material, in either direction.** NFR-SEC-009 applies to this wire as it does to the command surface: no message in this protocol or any document extending it may carry a passphrase, a repository key, or anything from which one can be derived. A destination stores objects it cannot read, and nothing here changes that.

The ephemeral TLS certificate key of §1 is not an exception. It authenticates nothing, expires with the connection, and is not derived from anything that outlives it.

**No plaintext paths or filenames.** Everything that crosses is either an encrypted repository object or protocol metadata about which objects exist. → NFR-SEC-001, NFR-SEC-004

**No repository content in a refusal.** See §8.

---

**Previous:** [01 — Identity and pairing](01-identity-and-pairing.md) · **Next:** [03 — Replication](03-replication.md)

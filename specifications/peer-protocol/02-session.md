# 02 — Session

**Normative.** Rationale in [ADR-0030](../../docs/adr/0030-peer-identity-and-pairing.md) and [architecture 09 §2](../../docs/architecture/09-replication-and-peers.md#2-transport).

---

## 1 Transport

A session runs over **TLS 1.3** ([RFC 8446](https://www.rfc-editor.org/rfc/rfc8446)) carrying **raw public keys** ([RFC 7250](https://www.rfc-editor.org/rfc/rfc7250)) in both directions. Both sides authenticate: the client certificate message is not optional here.

The raw public key presented MUST be the peer identity of [01 §1](01-identity-and-pairing.md#1-peer-identity). Each side MUST verify that the key its peer presents is byte-for-byte the key pinned in the grant, and MUST close the connection on any difference ([01 §2.5](01-identity-and-pairing.md#25-rejecting-a-changed-identity)).

X.509 is not used and MUST NOT be accepted. There is no certificate authority in this design and no name worth validating — the pinned key is already an exact expectation, and a chain would layer a weaker check on top of it. → [ADR-0030 §4](../../docs/adr/0030-peer-identity-and-pairing.md#4-the-transport-authenticates-the-pinned-key-directly)

QUIC ([RFC 9000](https://www.rfc-editor.org/rfc/rfc9000)) MAY be used in place of TCP, with the same TLS 1.3 handshake and the same pinning rule. Whether a session is direct or relayed MUST be reported to the operator either way ([architecture 09 §7](../../docs/architecture/09-replication-and-peers.md#7-relay)).

**Pairing is the exception.** The ceremony of [01 §2](01-identity-and-pairing.md#2-the-pairing-ceremony) runs before any key is pinned, so it cannot pin one. It runs over TLS 1.3 with both sides presenting their raw peer keys **unverified**, and the ceremony's short authentication string is what authenticates them. A conforming implementation MUST NOT reuse an unverified connection for anything but pairing.

## 2 Session establishment

After the TLS handshake completes and pinning is verified:

1. each side sends `SessionHello`;
2. each side either sends `SessionAccept` or refuses;
3. the session is open, and payload documents ([03–05](README.md#documents)) apply.

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
| 2 | `array of text` | Features in effect — the intersection, per §4 |

The agent version in key 5 is **informational**. A conforming implementation MUST NOT branch on it. Behaviour is decided by negotiated features and by nothing else, because version-sniffing is how a protocol acquires undocumented compatibility rules that no specification can then describe.

## 3 Protocol version

The protocol version is a `u16`, independent of the repository format version ([00 §2.1](00-conventions.md#21-versioning-is-separate)).

Each side declares the closed range of versions it supports. The selected version MUST be the highest both sides speak — the lower of the two maxima, provided it is not below the higher of the two minima. Where the ranges do not overlap, a side MUST refuse with `version_unsupported`, **naming both its own range and the range it was offered**. → FR-REP-004, NFR-COMP-006

A range rather than a single version is what makes that refusal message possible, and what lets an old and a new agent meet in the middle without either having to guess what the other can do. A hello carrying one version could only ever say "this or nothing".

## 4 Feature negotiation

A feature identifier is a non-empty string of `a`–`z`, `0`–`9`, `-`, `_` and `.`. A reader MUST refuse an identifier containing anything else as `malformed`, and MUST NOT case-fold one to make it fit: an identifier two implementations spell differently and one of them silently normalises is a negotiation that disagrees with itself while both sides believe they agree.

Each side offers what it supports and separately states what it **requires** of the other.

The features in effect are the intersection of the two offered sets, and MUST be written in the accept sorted by byte value. A side MUST refuse the session when any feature it requires is absent from the other's offered set, with reason `feature_unsupported` naming the missing feature.

Both sides compute the intersection alone, from the same two hellos, and exchange no further message about it. A set has no order of its own, so without a stated one the two sides could write different accepts while both were correct.

A side MUST NOT use a feature that is not in the intersection, and MUST NOT infer support from any other signal.

This mirrors the repository format's required/optional split ([repository format 00 §5](../repository-format/00-conventions.md#5-versioning-and-feature-negotiation)) for the same reason: a peer that supports most of a version needs a way to say so, rather than having to refuse everything or claim everything.

No features are defined at protocol version 1. The mechanism is specified now because retrofitting negotiation onto a deployed protocol means a flag day, and the documents that will define features ([03–05](README.md#documents)) are not written yet.

## 5 Framing

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
| 5 | `SessionHello` | §2 |
| 6 | `SessionAccept` | §2 |
| 7 | `SessionRefuse` | §6 |
| 8–255 | Reserved for this specification | — |
| 256+ | Reserved for [03–05](README.md#documents) | — |

A message type a reader does not know MUST cause refusal with `message_unknown`. It MUST NOT be skipped: a protocol that ignores messages it does not understand cannot tell a new feature from a corrupted stream.

Length framing outside TLS is deliberate rather than redundant. It bounds allocation before any CBOR is parsed, and it keeps the frame boundary a property of this protocol rather than of the transport — which is what lets QUIC substitute for TCP without changing anything above.

## 6 Errors and refusal

A side that cannot continue sends `SessionRefuse` (or `PairRefuse` during pairing) and closes. There is no error that leaves a session half-open.

**`SessionRefuse`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u16` | Reason code |
| 2 | `text` | Reason, for a human (≤ 256 bytes) |

| Code | Name | Meaning |
|------|------|---------|
| 1 | `identity_changed` | The presented key is not the pinned key ([01 §2.5](01-identity-and-pairing.md#25-rejecting-a-changed-identity)) |
| 2 | `not_paired` | No grant exists for the presented identity |
| 3 | `revoked` | A grant existed and was revoked |
| 4 | `version_unsupported` | No common protocol version (§3) |
| 5 | `feature_unsupported` | A required feature is absent (§4) |
| 6 | `message_unknown` | An unrecognised message type (§5) |
| 7 | `malformed` | A frame or CBOR body that violates this specification |
| 8 | `terms_refused` | The offered terms are unacceptable to the receiving side |
| 9 | `busy` | The peer cannot serve a session now; retry later |
| 10 | `pairing_declined` | A human declined ([01 §2.4](01-identity-and-pairing.md#24-approval-and-pinning)) |

The code is what a client branches on; the text is what a human reads. A conforming implementation MUST NOT parse the text.

**Refusal reasons are deliberately coarse.** `not_paired`, `revoked` and `identity_changed` are distinguished because they call for different operator action, but nothing here reports *why* a peer is busy or *which* internal condition produced a `malformed`. A refusal message is served to an unauthenticated or newly authenticated stranger, and detail served there is reconnaissance.

## 7 What a session does not carry

**No key material, in either direction.** NFR-SEC-009 applies to this wire as it does to the command surface: no message in this protocol or any document extending it may carry a passphrase, a repository key, or anything from which one can be derived. A destination stores objects it cannot read, and nothing here changes that.

**No plaintext paths or filenames.** Everything that crosses is either an encrypted repository object or protocol metadata about which objects exist. → NFR-SEC-001, NFR-SEC-004

**No repository content in a refusal.** See §6.

---

**Previous:** [01 — Identity and pairing](01-identity-and-pairing.md) · **Next:** Replication — [not written](README.md#documents)

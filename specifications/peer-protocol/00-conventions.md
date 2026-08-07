# 00 — Conventions

**Normative.** Applies to every other document in this specification.

---

## 1 What is inherited

This protocol inherits, unchanged, from [repository format §00](../repository-format/00-conventions.md):

| Inherited | Section |
|-----------|---------|
| Notation (`‖`, `u8`…`u64`, `bytes[n]`, big-endian framing) | [§1](../repository-format/00-conventions.md#1-notation) |
| Sizes and units (`KiB`, `MiB`, `GiB`; never `KB`) | [§2](../repository-format/00-conventions.md#2-sizes-and-units) |
| CBOR deterministic encoding, and the duty to reject non-deterministic input | [§4](../repository-format/00-conventions.md#4-cbor-encoding) |
| Integer map keys, stable key numbers, unknown-field rules | [§4.2](../repository-format/00-conventions.md#42-map-keys), [§4.3](../repository-format/00-conventions.md#43-unknown-fields) |
| Base32 rendering of identifiers | [§6](../repository-format/00-conventions.md#6-object-identifiers-in-paths) |
| Time as `u64` Unix milliseconds | [§7](../repository-format/00-conventions.md#7-time) |
| Reserved-value handling | [§9](../repository-format/00-conventions.md#9-reserved-and-zero-values) |
| Refuse-never-guess posture | [§10](../repository-format/00-conventions.md#10-error-handling-posture) |

Inheriting rather than restating is deliberate. Two copies of an encoding rule drift, and the one that drifts is always the copy.

## 2 What differs

### 2.1 Versioning is separate

The repository format version and the protocol version are **independent** and MUST NOT be conflated. Two peers may speak protocol version 1 while holding repositories at different format versions, and a peer may be upgraded on either axis alone.

A session negotiates protocol features ([02 §6](02-session.md#6-feature-negotiation)). Repository format compatibility is a property of the objects exchanged and is negotiated separately, per [ADR-0014](../../docs/adr/0014-format-versioning-and-stability.md). → FR-REP-004, NFR-COMP-006

### 2.2 There is no repository descriptor on the wire

The repository format bootstraps from a descriptor object. This protocol does not: a session bootstraps from the pinned peer key and the negotiated feature set, and a destination that holds objects for several repositories serves them all through one session identity.

### 2.3 Limits are the protocol's own

The repository format's limits bound stored objects. These bound *messages*, and a reader MUST enforce each before allocating:

| Limit | Value |
|-------|-------|
| Maximum frame payload | 16 MiB |
| Maximum pairing message CBOR body | 4 096 bytes |
| Maximum authentication message CBOR body | 1 024 bytes |
| Maximum session hello CBOR body | 65 536 bytes |
| Maximum feature identifiers per hello | 256 |
| Maximum label length (human-chosen, UTF-8) | 256 bytes |
| Maximum grants per peer | 1 024 |

A frame exceeding its limit is refused and the session closed. Unlike a stored object exceeding a limit, it is **not** a damage finding: the wire is an untrusted channel and an oversized frame is an ordinary hostile input rather than evidence of a broken writer. → [`threat-model.md` T-7](../../docs/threat-model.md#t-7-malicious-or-malformed-protocol-input)

## 3 Cryptographic primitives

This protocol uses exactly these, and adds nothing:

| Purpose | Primitive |
|---------|-----------|
| Peer identity signature | Ed25519 ([RFC 8032](https://www.rfc-editor.org/rfc/rfc8032)) |
| Pairing key agreement | X25519 ([RFC 7748](https://www.rfc-editor.org/rfc/rfc7748)) |
| Key derivation | HKDF-SHA-256 ([RFC 5869](https://www.rfc-editor.org/rfc/rfc5869)) |
| Session encryption | TLS 1.3 ([RFC 8446](https://www.rfc-editor.org/rfc/rfc8446)), authenticating nothing — see [02 §1](02-session.md#1-transport) |
| Ephemeral TLS certificate key | ECDSA P-256 ([FIPS 186-5](https://doi.org/10.6028/NIST.FIPS.186-5)), per connection, discarded after |
| Session authentication | Ed25519 over the channel-bound transcript of [02 §3.2](02-session.md#32-the-bound-transcript) |
| Channel binding | `SHA-256` over the DER `SubjectPublicKeyInfo` of each side's TLS certificate |
| Challenge response | HMAC-SHA-256 ([RFC 2104](https://www.rfc-editor.org/rfc/rfc2104)) |

Ed25519 and SHA-256 are already in the repository format's dependency closure, so the protocol adds X25519, HKDF, and the P-256 keypair every TLS stack already has — and no more. Keeping that closure small is the same constraint the recovery tool is held to ([architecture 11 §2](../../docs/architecture/11-solution-structure.md#2-dependency-rules)): every primitive here is one an independent implementer must obtain.

The P-256 key is the one entry here that establishes nothing. It exists because the platform's TLS will not open a connection without a certificate, and it is discarded when the connection ends; an implementer who can supply a raw public key instead needs no curve at all, provided the binding of [02 §3.2](02-session.md#32-the-bound-transcript) still covers whatever the transport did present.

There is no cipher agility on this wire. A peer offering an unlisted suite is refused, and there is no compatibility switch that admits one — NFR-SEC-002's rule applies to the protocol exactly as it applies to stored objects.

## 4 Domain separation

Every derived key and every authentication string is derived under a distinct label, and the labels are namespaced to this protocol:

```text
HKDF-SHA-256(salt, ikm, "fbp-peer-v1:" ‖ purpose)
```

Labels defined by this specification are listed where they are used and MUST NOT be reused for another purpose. A derivation that shares a label with another is a derivation whose two uses can be substituted for one another, which is the shape of most protocol confusion attacks.

---

**Next:** [01 — Identity and pairing](01-identity-and-pairing.md)

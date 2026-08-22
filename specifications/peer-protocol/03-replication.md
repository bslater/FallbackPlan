# 03 — Replication

**Normative.** Rationale in [ADR-0030](../../docs/adr/0030-peer-identity-and-pairing.md) and [architecture 09 §1](../../docs/architecture/09-replication-and-peers.md#1-what-replication-moves).

---

This document defines the payload a peer speaks once a session is [Open](02-session.md#2-session-states): the exchange that moves immutable repository objects from a **source** to a **destination**. It is the first of the payload documents 02 §7 reserves the 256+ type range for.

A peer that has never parsed a blob can still forward one. Every message here names objects and moves their bytes; none reads them. → [00 §what-this-is-not](README.md#what-this-is-not)

## 1 Roles and direction

The **source** holds objects and offers to send them. The **destination** stores objects it cannot read. In a session, the side that authenticated as an initiator (it dialled) is the **source**, and the side that accepted is the **destination**, when the destination's grant for the source permits storing here ([01 §3](01-identity-and-pairing.md#3-grants): role `stores-here` or `both`). A destination whose grant does not permit it MUST refuse with `not_paired`.

This document defines a one-way push: the source sends, the destination receives. Pull and bidirectional reconciliation are later revisions; nothing here precludes them, because the object set is immutable and a have/want exchange is symmetric in principle.

## 2 The exchange

After Open, the source drives a fixed sequence. Each step is one or more frames; a violation at any step is refused and closes the session (§7).

1. The source sends a **`ReplicationOffer`**: the repository the objects belong to, the format capability it speaks, and the **scope** it will offer.
2. The destination answers with its **`ReplicationInventory`** — the object keys, within the offered scope, it already holds — sent as one or more pages. A destination that will not serve this repository or scope refuses instead.
3. The source computes the objects in scope the destination lacks, and for each sends a **`ReplicationObject`** naming it and its length, then one or more **`ReplicationChunk`** frames carrying its bytes in order.
4. The source sends **`ReplicationComplete`** with the count it sent.
5. The destination sends **`ReplicationAck`** with the counts it received and stored, and the session may close or the source may begin another scope.

The inventory precedes the transfer so the cheapest thing crosses first: the destination declares what it has once, and the source sends only the difference. An implementation MAY replace the explicit inventory with a compact set filter negotiated as a feature ([02 §6](02-session.md#6-feature-negotiation)); the explicit inventory is the base exchange every implementation supports.

A dialler with no objects to move but a peering to end sends a **`PeeringTermination`** ([01 §3.1](01-identity-and-pairing.md#31-ending-a-peering)) in place of the offer, where its feature is negotiated; the exchange then carries no payload and the session ends. Anywhere else in the sequence the type is a violation like any other.

## 3 Messages

Bodies are deterministic CBOR maps; key 0 is the message type ([02 §7](02-session.md#7-framing)). Keys below start at 1. An unknown key inside a known message is skipped; an unknown message type is refused ([02 §7](02-session.md#7-framing)).

### 3.1 ReplicationOffer

Source → destination, once, first.

**`ReplicationOffer`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `bytes[16]` | The repository identity the offered objects belong to |
| 2 | `u32` | The repository format capability the source speaks ([02 §5](02-session.md#5-protocol-version) governs the *protocol* version; this is the *format* the objects are in) |
| 3 | `text` | The scope, ≤ 64 bytes (§4) |

A destination that does not implement the offered format capability MUST refuse with `feature_unsupported`. A destination that will not accept this repository at all MUST refuse with `not_paired` — it is a policy refusal, and no finer reason is owed a peer (§7).

### 3.2 ReplicationInventory

Destination → source, one or more pages.

**`ReplicationInventory`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `array of text` | Object keys the destination already holds in scope, ≤ 4096 per page, each ≤ 1024 bytes |
| 2 | `bool` | Whether another page follows |
| 3 | `u64` | *(optional)* Bytes the destination can still accept under the quota in force ([05 §1](05-quotas.md)) |
| 4 | `bytes[16]` | *(optional)* `claim_token` — present only when the `replica-claim` feature is in effect and this destination holds no claim credential for this repository ([07 §5.3](07-retrieval.md#53-the-token-is-per-destination-and-is-not-a-secret)) |

The keys are the store's own object keys ([architecture 02](../repository-format/README.md) names them). A destination that holds nothing in scope sends one page with an empty array and `false`. The page cap keeps each frame under the [00 §2.3](00-conventions.md#23-limits-are-the-protocols-own) limit without the source having to trust the destination's framing.

**Key 3 is optional and its absence is not zero.** A destination under no quota omits it, and so does one implementing an earlier revision of this document; both mean *not stated*, which a source MUST NOT read as *no room*. A destination whose quota is fully consumed sends `0`, which is a different statement and MUST be sent rather than omitted. A destination that sends key 3 SHOULD send it on every page of the inventory; a source that sees it more than once takes the last.

The headroom rides here rather than in the hello or the terms, and both alternatives were rejected for reasons worth recording. Terms are persisted in the pairing grant and compared for narrowing ([05 §6](05-quotas.md)), so a per-session number there would announce a reduction on every session. The hello is too early: the destination does not yet know which repository is coming, and computing usage means walking every object it holds — a cost the periodic verification sessions would pay for a number nobody reads. By the inventory the scope is known and the destination has already computed `quota − usage` in order to enforce the boundary stop of [05 §4](05-quotas.md).

A source MUST NOT treat a small headroom as a refusal. The boundary stop already refuses the exact object that would cross the line, with exact numbers, at the exact moment, and preserves everything committed before it; key 3 exists so the operator hears about it a session earlier, not so the source invents an earlier refusal.

#### 3.2.1 Registering the claim credential

**Key 4 is how disaster recovery is armed, one session ahead of the disaster.**

A destination that has accepted a repository can serve it back ([07](07-retrieval.md)),
but only to the identity its attribution ledger names. That identity dies with
the source's durable local state, and nothing in the repository can rebuild it
([architecture 00](../../docs/architecture/00-overview.md)). So the destination
records, alongside the attribution, a credential the *passphrase* can
reproduce — and it must do so while the pairing is still alive, because a
machine that has already been lost cannot register anything.

When key 4 is present, the source MUST answer with a **`ClaimRegister`** (282)
before its first `ReplicationObject`:

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `bytes[16]` | `repository_id` the credential is for |
| 2 | `bytes[32]` | `claim_public`, derived per [07 §5.2](07-retrieval.md#52-the-credential) from the passphrase and the destination's `claim_token` |

The destination stores `claim_token` and `claim_public` with the attribution
and MUST NOT send key 4 for that repository again. It never receives the
private half and learns nothing about the passphrase: `claim_public` is one
point on a curve, and recovering the root behind it is the discrete-log
problem, not a KDF the destination could grind.

A source that does not implement the feature simply does not answer, and the
destination keeps serving that replica exactly as before — unclaimable, and
[07 §5.3](07-retrieval.md#53-the-token-is-per-destination-and-is-not-a-secret)
says so plainly when someone tries.

**A source that cannot derive the credential does not answer either, and this
is not hypothetical.** A provisioned write-only (v2) service holds the write
bundle and not the Argon2id root it came from ([ADR-0042](../../docs/adr/0042-write-only-repositories.md)),
so it cannot compute `claim_public` during an unattended backup at all. Such a
source MUST omit the answer rather than send anything derived from material
that is not the root; the destination MAY re-offer key 4 on a later session,
since nothing was registered. How a v2 set arms its recovery is unsettled and
tracked as [Q23](../../docs/open-questions.md#q23--arming-disaster-recovery-on-a-write-only-repository);
until it is settled, a v2 replica is registered only when a session happens to
run while the passphrase is present.

### 3.3 ReplicationObject and ReplicationChunk

Source → destination, for each object the destination lacks: one `ReplicationObject`, then zero or more `ReplicationChunk`.

**`ReplicationObject`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `text` | The object's key, ≤ 1024 bytes |
| 2 | `u64` | The object's total length in bytes |

**`ReplicationChunk`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u64` | The offset of these bytes within the current object |
| 2 | `bytes` | The bytes, ≤ the replication chunk limit ([00 §2.3](00-conventions.md#23-limits-are-the-protocols-own)) |

A `ReplicationChunk` belongs to the object named by the most recent `ReplicationObject`; a chunk with no current object, or an offset that is not the running total of bytes already received for it, is `malformed`. An object of length 0 is a `ReplicationObject` with no chunks. Chunking exists because a single frame is bounded to 16 MiB ([02 §7](02-session.md#7-framing)) and an object may exceed it; it is not fragmentation the destination reassembles into anything but the object's own bytes.

The destination MUST NOT make an object visible in its store until every byte of it has arrived — it commits the object whole, or not at all (§5).

### 3.4 ReplicationComplete and ReplicationAck

**`ReplicationComplete`** — source → destination, after the last object.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u64` | The number of objects the source sent this scope |

**`ReplicationAck`** — destination → source, in answer.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u64` | The number of objects the destination received and committed |

The ack confirms receipt, not durability against a challenge — that is [04](README.md#documents)'s verification, which this document does not carry. A destination that committed fewer objects than the source sent has already refused the offending object (§7); the ack is reached only when the transfer completed.

## 4 Scope

The scope in the offer is a short token naming what the source offers. This revision defines one value:

- `all` — every object the source holds for the offered repository.

Snapshot-scoped replication (a specific snapshot and its object closure) is a later revision; it will carry a structured scope in this field, which is why the field is present now rather than assumed. A destination that does not understand a scope token MUST refuse with `malformed`.

## 5 Resumption and atomicity

The destination commits each object atomically: it holds an object's bytes until complete, then writes it under a create-if-absent condition ([architecture 04 §5](../../docs/architecture/04-concurrency-and-publication.md)). An interruption therefore never leaves a partial object visible, and re-running the exchange resumes correctly with no special state — the destination's next inventory already lists everything it committed, so the source sends only what is still missing. → FR-REP-003

Objects are immutable and content-addressed, so a create-if-absent write is idempotent: an object the destination already holds is identical to the one the source would send, and re-sending it is wasteful but never wrong. This is what makes resumption a property of the exchange rather than a checkpoint the two sides must agree on.

Resumption at boundaries *within* a single large object is not defined here; an interrupted object is re-sent whole. That is a later refinement, and the wire admits it — a future revision may let the inventory carry partial-object offsets.

## 6 Framing and limits

Frames are as [02 §7](02-session.md#7-framing) defines them. This document occupies the reserved 256+ type range:

| Type | Message | Section |
|------|---------|---------|
| 256 | `ReplicationOffer` | §3.1 |
| 257 | `ReplicationInventory` | §3.2 |
| 258 | `ReplicationObject` | §3.3 |
| 259 | `ReplicationChunk` | §3.3 |
| 260 | `ReplicationComplete` | §3.4 |
| 261 | `ReplicationAck` | §3.4 |

The per-message body limits are in [00 §2.3](00-conventions.md#23-limits-are-the-protocols-own). The one that constrains the wire design is the chunk limit: an object larger than it is sent as several chunks, none of which — with its CBOR framing — may push a frame past the 16 MiB cap.

## 7 Refusal

Replication reuses [02 §8](02-session.md#8-errors-and-refusal)'s `SessionRefuse` and its codes; it defines no error mechanism of its own. The codes this document uses: `not_paired` (a grant that does not permit storing here, §1), `feature_unsupported` (an unimplemented format capability, §3.1), and `malformed` (a chunk out of order, a scope not understood, or any body that violates this document). A refusal closes the session, as everywhere in this protocol; there is no partial transfer left half-open.

## 8 What replication does not carry

**No key material and no plaintext, as [02 §9](02-session.md#9-what-a-session-does-not-carry) requires of every payload.** The objects that cross are encrypted repository objects; their keys are store keys, not file paths. A destination stores what it cannot read. → NFR-SEC-001, NFR-SEC-004, NFR-SEC-009

**No storage location.** Where a destination keeps a replica is its own choice and never appears on this wire ([01 §4](01-identity-and-pairing.md#4-terms) keeps storage paths off the protocol). The offer names the repository; the destination decides where its objects live.

---

**Previous:** [02 — Session](02-session.md) · **Next:** [04 — Verification](04-verification.md)

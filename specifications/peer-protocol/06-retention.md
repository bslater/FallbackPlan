# 06 — Retention instructions

**Normative.** Rationale in [architecture 07 §3.0.1](../../docs/architecture/07-retention-and-gc.md#301-where-the-collector-runs-under-hub-and-spoke) and [ADR-0009 Amendment 4](../../docs/adr/0009-garbage-collection-safety.md#amendment-4-2026-08--where-the-collector-runs-under-hub-and-spoke).

---

This document lets a hub age a peer replica under policy. The division of authority is the whole design: **the hub computes, because only the hub can read manifests; the spoke deletes exactly what it is told and nothing else, bounded below by its own granted floor** (FR-GC-010). A spoke holds ciphertext it cannot mark, so its local reachability is never an input — a replica's view is exactly the partial view collection exists to distrust.

It rides the replication session of [03](03-replication.md), after the object exchange, under a negotiated feature. One loosening of [03 §1](03-replication.md#1-roles-and-direction) applies here: the dialler is the **commander** rather than strictly the source of objects — the same side, dialling for a different purpose. The grant that admits it is the same `stores-here` grant; a peer entitled to push objects into a replica is the peer entitled to age that replica under the policy both households accepted at pairing.

## 1 Feature and placement

The instruction is gated by the feature **`retention-instruction`** ([02 §6](02-session.md#6-feature-negotiation)): a commander MUST NOT send it unless the feature is in the session's intersection. Against a spoke that does not offer it, the hub records the destination as not converging and retries at a later session — an older build is never sent a type it would refuse as `message_unknown`.

The instruction follows a completed object exchange — after `ReplicationAck`, on the same session — because the exchange is where the commander learned what the spoke holds: the spoke's own `ReplicationInventory` is the ground truth the drop-list is computed from, so an instruction can only name keys the spoke itself declared. A `RetentionOffer` at any other point in the session is `malformed`.

## 2 The exchange

1. The commander sends a **`RetentionOffer`**: the repository it applies to and the store keys to delete, in one or more pages.
2. The spoke validates every page (§3). A violation refuses the session; nothing is deleted from a refused instruction.
3. The spoke deletes exactly the named keys — snapshots first, in the order given — and answers **`RetentionAck`** with the count removed.

The commander computes the drop-list as *inventory minus keep-closure*: what the spoke declared, less everything the destination's effective policy keeps ([architecture 07 §2](../../docs/architecture/07-retention-and-gc.md#2-retention-policy)). The commander MUST order snapshot keys before the keys of objects they reference, so an interruption leaves the replica lagging-but-valid, exactly as the copy order guarantees in the other direction.

## 3 What the spoke validates

The spoke MUST refuse the whole instruction — `terms_refused`, deleting nothing — when any of the following holds:

- the offered repository is not one this peer is attributed to ([05 §2](05-quotas.md#2-ownership));
- a named key is outside the `blobs/`, `snapshots/`, `index/`, `journal/` or `hints/` namespaces — `repository-format` and `keys/` are never deletable by instruction, and a key under `tombstones/` or `leases/` names an object that should never have replicated at all;
- deleting the named `snapshots/` keys would leave fewer snapshot objects for this repository than the grant's **retention floor** (`retention_floor_generations`, [01 §4](01-identity-and-pairing.md#4-terms));
- this spoke holds a **reclaim public key** for the repository ([05 §2](05-quotas.md#2-ownership)) and the page carries no signature, or one that does not verify against it — **whatever the session negotiated**.

That last check is what makes a deletion instruction authorised by something narrower than "the peer we paired with". A session's grant says who is speaking; the reclaim signature says the instruction came from whoever holds the repository's reclaim key — which a compromised *publisher* does not ([ADR-0055](../../docs/adr/0055-reclaim-authority.md)).

**The check MUST NOT be gated on the negotiated feature.** Negotiation is an intersection of what the two sides offer and a listener cannot require a feature ([02 §6](02-session.md#6-feature-negotiation)), so a check conditioned on `signed-retention` is a check the *sender* decides to be subject to — and the sender is what it defends against. A source that omitted the feature from its hello would have an unsigned, freshly forged drop-list obeyed, which is strictly more than the replay this signature was criticised for permitting. The gate is therefore the spoke's own durable record: **if it holds a key, it requires a signature.** `signed-retention` remains an offer, so a commander learns at the hello what will be expected of it, and carries no authority to waive anything.

A spoke whose attribution carries no key yet cannot manufacture a verdict from an absence and proceeds as it always did; refusing every instruction for a peering established before the key existed would strand it. That is the whole of the compatibility surface, and it closes on its own: the key is recorded at the first offer a current build sends.

The floor check needs no decryption: a spoke counts the snapshot objects it holds under the repository's `snapshots/` prefix, subtracts the named deletions, and compares. The floor is the one safeguard that holds when the hub is fully compromised — a ransomed hub cannot instruct history below it ([architecture 07 §5](../../docs/architecture/07-retention-and-gc.md#5-destructive-change-safeguards)) — which is why the refusal is loud and total rather than a partial, best-effort delete.

A key the spoke does not hold is not an error: deletion is idempotent, the exchange may be a resume, and the ack counts only what was actually removed.

## 4 Messages

This document occupies types 262+ of the range [02 §7](02-session.md#7-framing) reserves.

| Type | Message | Section |
|------|---------|---------|
| 262 | `RetentionOffer` | §4.1 |
| 263 | `RetentionAck` | §4.2 |

### 4.1 RetentionOffer

Commander → spoke, one or more pages.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `bytes[16]` | Repository identifier the instruction applies to |
| 2 | `array of text` | Store keys to delete, ≤ 4096 per page |
| 3 | `bool` | More pages follow |
| 4 | `bytes[64]` | *(optional)* Ed25519 signature over this page's canonical bytes under the repository's **reclaim key** ([ADR-0055](../../docs/adr/0055-reclaim-authority.md)) |

The signed bytes are the repository identifier, then `u32` key count, then each key as `u32` byte length followed by its UTF-8 bytes in the order sent, then one byte carrying key 3. Length-prefixed rather than delimited, so that no two different drop-lists can produce identical signed bytes by concatenating the same way.

A signature present with a length other than 64 bytes is `malformed`. A spoke MUST NOT silently ignore one: a spoke that did would fall back to accepting the instruction unsigned, which is the check the feature exists to make.

The signature covers **one page**. A page therefore cannot be forged and cannot be edited, and a page dropped in transit deletes less than was instructed, which is the safe direction. An old page **can** be replayed into a later session: that requires an attacker already inside an authenticated, encrypted session, deletion is idempotent so a replayed page usually names keys that are already gone, and the floor of §3 still bounds what any instruction can do. Closing it wants the signature bound to session-unique material, which this revision does not carry — stated so an implementer does not assume otherwise.

Every page repeats the repository identifier, and a page whose identifier differs from the first is `malformed`. The spoke MUST read all pages before deleting anything: the floor check of §3 is over the whole instruction, and acting page-by-page would let an instruction pass the floor piecewise while breaching it in total.

### 4.2 RetentionAck

Spoke → commander, once, after the last page's deletions.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u64` | Objects actually deleted |

## 5 What this does not carry

No object ids, no manifests, no reasons: the store keys are the whole vocabulary, because they are the only names both sides share for objects one of them cannot read. The hub's *why* — which policy, which keep-set — stays on the hub, in its dry-run report (FR-GC-005); the spoke's answer to "why is this gone" is "the peering's commander instructed it, on this date, within my floor", which its own audit trail records.

---

**Previous:** [05 — Quotas](05-quotas.md) · **Next:** [README](README.md)

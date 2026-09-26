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

The commander computes the drop-list as *inventory minus keep-closure*: what the spoke declared, less everything the destination's effective policy keeps ([architecture 07 §2](../../docs/architecture/07-retention-and-gc.md#2-retention-policy)). A commander that cannot sign the instruction (§3) — a write-only source outside a granted collection run ([ADR-0055 §6 and Amendment 2](../../docs/adr/0055-reclaim-authority.md#6-a-write-only-set-still-collects-under-a-grant-that-does-not-outlive-the-run)) — sends none: it pushes the whole copy and instructs on the run that carries the grant, rather than send a page the spoke will refuse whole. The commander MUST order snapshot keys before the keys of objects they reference, so an interruption leaves the replica lagging-but-valid, exactly as the copy order guarantees in the other direction.

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

**The claim interlock is deliberately asymmetric with reading, and the asymmetry is the point.** A claimed replica is readable the instant the proof verifies ([07 §5.9](07-retrieval.md#59-what-a-claim-does-and-does-not-carry)), because a disaster is exactly when the far household is least reachable and a recovery that waits on a sleeping friend is a recovery that fails. Deleting waits, because the two acts have different blast radii. A passphrase that has fallen into the wrong hands lets its holder *read* what that passphrase already decrypts wherever they find it — gating that on a human buys nothing and costs real recoveries — but it must not let them quietly destroy the copy that outlived the machine they took it from.

The floor of §3 bounds a compromised hub that was never lost; this bounds a stolen passphrase arriving as a stranger. Neither replaces the other: an acknowledged claim is still held to the floor.

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
| 4 | `bytes[64]` | *(optional)* Ed25519 signature over this page's canonical bytes **and the session identifier** under the repository's **reclaim key** ([ADR-0055](../../docs/adr/0055-reclaim-authority.md)) |

The signed bytes are the repository identifier, then `u32` key count, then each key as `u32` byte length followed by its UTF-8 bytes in the order sent, then one byte carrying key 3, then the 32-byte `session_id` of [02 §3.5](02-session.md#35-the-session-identifier). Length-prefixed rather than delimited, so that no two different drop-lists can produce identical signed bytes by concatenating the same way; the `session_id` is last and fixed-length, so a bound page and an unbound one share a prefix and neither can be read as the other.

A commander appends the `session_id` when the session negotiated **`session-bound-retention`** and omits it otherwise, because a spoke that cannot verify the bound form would refuse every page of an instruction meant for it. A spoke's own behaviour is **not** a function of the intersection: it requires whichever form it offered, since the intersection is half the commander's to choose and a spoke that read its requirement out of it would let the party being checked decide it ([02 §6](02-session.md#6-feature-negotiation)). A spoke MUST NOT accept both forms — accepting both is accepting the replayable one.

A signature present with a length other than 64 bytes is `malformed`. A spoke MUST NOT silently ignore one: a spoke that did would fall back to accepting the instruction unsigned, which is the check the feature exists to make.

The signature covers **one page and one session**. A page therefore cannot be forged, cannot be edited, and cannot be replayed: `session_id` is derived from material neither end chose and no other connection shares, so a recording of an exchange verifies against nothing afterwards.

A page can still be *dropped* by the commander, which deletes less than was instructed and is the safe direction. Nobody else can drop or reorder one — the pages ride a single ordered, integrity-protected stream — so there is no position from which to truncate an instruction without also being the party that composed it.

A commander that signs no page at all is a separate case and is covered by §3: a spoke holding a reclaim key refuses it.

Every page repeats the repository identifier, and a page whose identifier differs from the first is `malformed`. The spoke MUST read all pages before deleting anything: the floor check of §3 is over the whole instruction, and acting page-by-page would let an instruction pass the floor piecewise while breaching it in total.

### 4.2 RetentionAck

Spoke → commander, once, after the last page's deletions.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u64` | Objects actually deleted |
| 2 | `bytes` | The deletion receipt (§4.3): the spoke's signed statement of what it did |
| 3 | `bytes[64]` | Ed25519 signature over key 2 under the spoke's **device** key ([01](01-identity-and-pairing.md)) |

Keys 2 and 3 are present together or not at all. One without the other, a signature of another width, or a receipt that does not parse is `malformed`. A spoke that predates receipts sends neither and a commander that predates them skips both, so no feature gates them ([02 §6](02-session.md#6-feature-negotiation)): an absence that can only mean less needs no negotiation, and a commander states "no receipt" rather than refusing.

### 4.3 The deletion receipt

The spoke's signed statement of what it did on the instruction ([ADR-0063](../../docs/adr/0063-deletion-receipts.md)), issued after the deletions and before the acknowledgement. It is signed under the device key because that is the only key the spoke holds that the commander can check — pinned at pairing and proved at this session's start — and it is carried in the acknowledgement because this session is the only place the commander already trusts that identity. It is a record of what the spoke *says* it deleted: it licenses nothing (the reclaim signature of §3 does that) and it proves nothing about what the spoke still holds (that is [04](04-verification.md)'s job).

The signed bytes are a fixed, label-separated encoding rather than a CBOR map, so that the statement is exactly its bytes ([00 §4](00-conventions.md#4-domain-separation)); integers are big-endian.

| Field | Width | Meaning |
|-------|-------|---------|
| label | 28 | `fbp-peer-v1:deletion-receipt`, ASCII |
| session_id | 32 | The session the instruction arrived in ([02 §3.5](02-session.md)) — the real identifier, whatever the pages' signatures were bound to |
| repository_id | 16 | The repository instructed |
| commander_public_key | 32 | The commanding device's Ed25519 public key |
| issued_at | `u64` | Unix milliseconds |
| floor_generations | `u32` | The retention floor in force (§3) |
| has_reclaim_key | `u8` | 1 when the next field is present |
| reclaim_public_key | 32 or 0 | The key the pages were verified against (§3), absent when the spoke held none |
| page_count | `u32` | At most 4096 |
| page_digests | 32 × page_count | SHA-256 of each page's signed bytes (§4.1), in the order accepted |
| deleted_count | `u64` | Keys actually removed |
| listed_count | `u32` | At most 4096, and never more than deleted_count |
| deleted | (`u32` length ‖ UTF-8 key) × listed_count | The removed keys, in instruction order; beyond the cap the count and the page digests stand for the rest |
| not_held | `u32` | Keys the instruction named that the spoke did not hold — counted, never listed as deleted |

A reader parses the statement as the exact inverse of this table and refuses anything left over: trailing bytes are `malformed`, because a reader must never show as attested what the signature does not cover. An instruction of more pages than a receipt can attest is refused by the spoke before anything is deleted, not after.

**What the commander checks**, in order, stopping at the first failure and naming it: the signature is the pinned peer's; `session_id` is this session's; `repository_id` and `commander_public_key` are its own; the page digests equal, one for one and in order, the digests of the pages it sent; `deleted_count` equals key 1; and every listed key was in the instruction it composed. A receipt that fails is not filed and is reported, never acted on — the deletion has already happened, so a refusal would change nothing at the spoke and would hide the one fact worth a human's attention.

**Both parties file their own copy** — the spoke before it acknowledges, the commander after it verifies — under `<state>/receipts/deletions/<repository>/`, one immutable file per receipt, and re-check the signature on every read. A copy the spoke cannot write is reported on its side and does not withhold the acknowledgement. The `receipts` verb on either host reads them back without the service.

## 5 What this does not carry

No object ids, no manifests, no reasons: the store keys are the whole vocabulary, because they are the only names both sides share for objects one of them cannot read. The hub's *why* — which policy, which keep-set — stays on the hub, in its dry-run report (FR-GC-005); the spoke's answer to "why is this gone" is "the peering's commander instructed it, in this session, within my floor" — and since [ADR-0063](../../docs/adr/0063-deletion-receipts.md) that answer is a statement the spoke signed and both sides filed (§4.3), not a line in a log. The receipt carries the same vocabulary and no more, and it attests what was done, never why or what remains.

---

**Previous:** [05 — Quotas](05-quotas.md) · **Next:** [README](README.md)

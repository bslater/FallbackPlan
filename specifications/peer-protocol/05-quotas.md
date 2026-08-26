# 05 — Quotas and exhaustion

**Normative.** Rationale in [architecture 09 §6](../../docs/architecture/09-replication-and-peers.md#6-quotas-and-exhaustion) and [01 §4](01-identity-and-pairing.md#4-terms).

---

This document defines how a destination enforces the quota its terms promised, and how the three ways a transfer can stop — quota exhaustion, the destination's own disk trouble, and a transient fault — are kept distinct on the wire. They are distinguished because they call for entirely different human action: a quota is a policy the lender chose, disk trouble is a fault the lender fixes, and a transient error is nobody's decision at all ([FR-QUOTA-001](../../docs/requirements/functional.md#quotas-and-capacity)).

No new message types are defined here. Enforcement speaks through `SessionRefuse` ([02 §8](02-session.md#8-errors-and-refusal)) at boundaries [03](03-replication.md) already draws.

## 1 What the quota bounds

The quota is the `u64` byte count in the destination's terms ([01 §4](01-identity-and-pairing.md#4-terms) key 1), set by the side that owns the disk and carried in its grant for the peer.

**A quota of 0 means the destination declared no byte ceiling.** The common household pairing lends space without pre-computing a number, and a protocol that read 0 as "store nothing" would make the unconfigured default a refusal of everything. A destination that means "nothing" does not pair, or revokes ([01 §3](01-identity-and-pairing.md#3-grants)).

A quota greater than 0 bounds **the total bytes of committed objects the destination stores for that peer, across every repository attributed to it** — not per repository and not per session, because the person lending 50 GB lent 50 GB, however many backup sets arrive ([ADR-0034](../../docs/adr/0034-hub-and-spoke-destinations.md)). Usage is measured as the sum of the stored objects' lengths; spooled bytes not yet committed do not count, and the destination's own filesystem overhead is its own affair.

## 2 Ownership

Quota accounting requires knowing which peer a replica belongs to, so the destination records an **attribution** — repository id → peer identity — durably, the first time it accepts a `ReplicationOffer` for a repository ([03 §3.1](03-replication.md#31-replicationoffer)). The attribution outlives sessions and restarts; it is what makes "the total this peer stores here" a computable number.

A `ReplicationOffer` naming a repository already attributed to a **different** peer MUST be refused `terms_refused`: the destination's terms extend only to repositories that are the peer's own here, and silently counting one household's archive against another's quota would corrupt both ledgers. Re-pairing does not transfer attributions; they are keyed by repository, not by grant.

**One ceremony moves an attribution, and it is not re-pairing.** A device that loses its durable local state loses its identity and every pairing with it, which would otherwise strand its own replica here permanently — the disaster-recovery case of [07 §5](07-retrieval.md#5-claiming-a-replica). A peer that proves it holds the repository's **passphrase**, against a credential this destination registered for that replica ([03 §3.2.1](03-replication.md#321-registering-the-claim-credential)), may re-point the attribution to its current identity. Nothing weaker suffices: a new grant does not, and an unproved assertion does not.

When an attribution moves, **the usage moves with it**. The bytes counted against the old identity are counted against the new one from that moment, because they are the same bytes on the same disk lent to the same household; the old identity's usage drops by exactly what the new one gains. A destination MUST NOT double-count them during or after the transfer, and MUST NOT treat a claim as an admission of new bytes against the claimant's quota — a claim stores nothing.

The attribution ledger therefore holds, per replica: the owning fingerprint, the destination's own `claim_token`, the registered `claim_public`, and whether a claim is awaiting the operator's acknowledgement ([06 §3](06-retention.md#3-what-the-spoke-validates)).

## 3 Enforcement at the object boundary

The check runs where [03 §2](03-replication.md#2-the-exchange) announces each object: a `ReplicationObject` declares its length before any chunk crosses. On receiving one, a destination with a quota greater than 0 MUST refuse `terms_refused` when

```
usage(peer) + declared_length > quota
```

and MUST NOT spool or commit any part of that object. Everything committed before the refusal stays committed — the stop is clean at an object boundary, no partial object is ever visible, and snapshots already durable at the destination are untouched ([FR-QUOTA-002](../../docs/requirements/functional.md#quotas-and-capacity), [03 §5](03-replication.md#5-resumption-and-atomicity)).

The refusal follows [02 §8](02-session.md#8-errors-and-refusal) in full, including the linger: a source told `terms_refused` knows the lender's policy said no, which is actionable — ask for more, keep less, or add a destination — where a dropped connection would say nothing.

A re-run after exhaustion is the ordinary resumption of [03 §5](03-replication.md#5-resumption-and-atomicity): the inventory declares what the destination holds, the source sends the difference, and the same check refuses at the same boundary until the quota rises or retention at the destination frees space. Exhaustion needs no state beyond what the objects themselves record.

### 3.1 Remaining headroom is told, not inferred

A destination under a quota knows `quota − usage` before it sends its inventory, because it needs that number to enforce §3 at all. It reports it there, as [03 §3.2](03-replication.md#32-replicationinventory) key 3, so the source can tell the operator the loan is nearly spent a session *before* a push runs into the boundary.

Absence means **not stated** — no quota in force, or a destination implementing an earlier revision — and a source MUST NOT read it as no room. Zero means no room and MUST be sent rather than omitted.

A source MUST NOT convert a small headroom into a refusal of its own. The boundary stop below is exact, arrives at the exact moment, and preserves everything committed before it; an early refusal would discard that partial progress to say something vaguer, sooner. What headroom buys is warning, not enforcement.

## 4 Disk trouble is not policy

A destination that cannot **store** — the underlying store refuses a write, the replica directory cannot be created, the disk is full — MUST refuse `storage_exhausted` (code 12, [02 §8](02-session.md#8-errors-and-refusal)), never `terms_refused`: the quota said yes and the hardware said no, and telling the source "policy" would send the human to renegotiate terms when the fix is a disk on the other side of the wire.

The boundary discipline is the same: nothing partial becomes visible, committed objects stay, and a re-run resumes from the inventory once the destination is healthy.

Faults of the **wire** — the connection breaks, a read times out — are not refusals at all; there is nobody to send one to. A source treats them as transient and retries under its own back-off ([FR-DEST-003](../../docs/requirements/functional.md#destinations-and-fan-out)).

## 5 What the source does with each answer

| The destination said | It means | The source records |
|----------------------|----------|--------------------|
| `terms_refused` | The lender's policy is exhausted | The pair failed with the quota named, a durable notice for the human, `degraded` for that destination while local protection continues |
| `storage_exhausted` | The lender's storage is faulty or full | The pair unavailable with the fault named; retried under back-off, converging when the destination recovers |
| *(connection fault)* | Nothing — the wire broke | The pair unavailable; retried under back-off |

The distinction is the point: three stops, three different people who can fix them ([architecture 09 §6](../../docs/architecture/09-replication-and-peers.md#6-quotas-and-exhaustion)).

## 6 Terms at the hello

A destination's hello carries its **current** terms for the authenticated peer ([02 §4](02-session.md#4-session-establishment) key 6) — the grant's terms as they stand, not as they stood at pairing. A destination MAY change its terms at any time; the change takes effect at the next session, exactly as revocation does.

The source MUST adopt the received terms as the ones in force and persist them with its grant. Where they **narrow** what the source was relying on ([01 §4](01-identity-and-pairing.md#4-terms)), the source MUST surface a durable notice rather than letting replication shrink silently — "your friend reduced the space they are lending you" is a fact the person relying on it needs told, before the first refusal arrives rather than after.

Enforcement authority never moves: the destination enforces from its own grant, and the terms in the hello are the destination telling the source what that grant says. A source that ignores the hello changes nothing about what the destination will accept.

---

**Previous:** [04 — Verification](04-verification.md) · **Next:** [06 — Retention instructions](06-retention.md)

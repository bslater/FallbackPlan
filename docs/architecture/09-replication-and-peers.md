# 09 — Replication and peers

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §8.3–8.4, §16.2 · **Resolves:** [H6](../review/2026-08-architecture-review.md#h6--independently-verified-trusts-the-destination-to-report-on-itself), [C5](../review/2026-08-architecture-review.md#c5--snapshot-commit-is-defined-so-that-one-offline-destination-stalls-all-protection)

---

## 1. What replication moves

Peers exchange immutable repository objects — blobs, manifests, snapshots, and index generations. They never reconcile live folders, and there is no notion of a single "current" global file state. This is the distinction from Syncthing set out in [`00-overview.md` §5.2](00-overview.md#52-syncthing).

Exchange sequence:

1. repository identity and compatible format capabilities;
2. authorised snapshot scopes;
3. generation summaries and compact object-set filters;
4. missing snapshot declarations;
5. missing index generations;
6. missing blobs or blob ranges;
7. verification receipts.

Steps 3–6 are ordered so the cheapest discovery happens first: a filter exchange establishes most of what is missing without enumerating anything.

## 2. Transport

Specified in [`specifications/peer-protocol/02`](../../specifications/peer-protocol/02-session.md).

Direct QUIC or TLS · mutually authenticated peer identity · relay fallback where the relay cannot decrypt · restart from verified ranges · dynamic concurrency · bandwidth schedules · source selection across multiple replicas · end-to-end content verification · optional local-network preference · fairness between backup sets.

Fairness matters more than it sounds: without it, one large backup set starves every other set on the same link indefinitely, and the user sees a set that has simply stopped making progress with no explanation.

**TLS carries the session; it does not establish who is on it.** Both sides present a certificate generated for that connection and discarded with it, and neither makes a trust decision about it. Identity is proved inside the protocol, by each side signing a transcript that binds its pinned Ed25519 key to the certificates this connection actually used — so a man in the middle, who must terminate TLS with certificates of its own, cannot produce or relay a proof that verifies. The original design put this in the transport with RFC 7250 raw public keys; that is unreachable on the platform, and [ADR-0030 Amendment 1](../adr/0030-peer-identity-and-pairing.md#amendment-1-2026-08--authentication-moves-out-of-tls) records the move and why the guarantee survives it.

A consequence worth stating where an architect will look for it: **a completed handshake is not an authenticated peer**, and the session's states are named so that no code can quietly assume otherwise ([02 §2](../../specifications/peer-protocol/02-session.md#2-session-states)).

### 2.1 Version skew

Paired peers may run different agent versions. The protocol negotiates a common feature set at connection time; a peer that cannot satisfy the other's **required** features refuses the connection with a clear reason rather than proceeding into an undefined state mid-transfer (NFR-COMP-006).

Repository format compatibility is negotiated separately from protocol compatibility, because they version independently ([ADR-0014](../adr/0014-format-versioning-and-stability.md)).

## 3. Pairing

Specified in [`specifications/peer-protocol/01`](../../specifications/peer-protocol/01-identity-and-pairing.md); the decisions behind it in [ADR-0030](../adr/0030-peer-identity-and-pairing.md). The properties this section fixes are the ones that specification is written to satisfy:

- Both sides see the same short authentication string — six base32 characters, per [01 §2.3](../../specifications/peer-protocol/01-identity-and-pairing.md) — and both must approve. An earlier draft specified words from a wordlist and shipped no wordlist; the characters say what they mean and need nothing carried.
- Identity is pinned on approval; a changed identity is a hard failure requiring explicit re-approval, not a prompt that can be clicked through.
- Direct connection is negotiated first; relay is a fallback and is reported as such.
- The **destination** sets quota, storage path, schedule window, and retention floor. These are its terms: a source may operate under narrower ones of its own choosing and can never ask for more generous. The storage path is deliberately not on the wire at all — a source that knew it would be a source that could name it.
- A source never receives unrestricted filesystem access to a destination — it speaks the repository protocol ([`05-storage-providers.md` §4.2](05-storage-providers.md#42-fallbackplan-peer)).
- A destination cannot read source content. Holding blobs conveys no ability to decrypt them.

That last pair of properties is what makes "back up to a friend's computer" a reasonable thing to ask of a friend. Neither party has to trust the other with anything.

## 4. Durability policy

A backup set declares its policy over per-destination replication state ([`04-concurrency-and-publication.md` §6](04-concurrency-and-publication.md#6-commit-versus-replication)):

```text
Snapshot captured when:
  - any replica: durable

Snapshot protected when:
  - at least one replica outside the source's failure domain: durable

Snapshot policy-compliant when:
  - local repository:  durable, and
  - at least one peer: durable

Snapshot healthy when:
  - local repository:  verified within 7 days,  and
  - trusted peer:      verified within 30 days, and
  - cloud replica:     durable within 24 hours
```

Because commit is per-replica, a destination that is offline delays *policy compliance* without blocking *capture*. The status display can say "captured locally, waiting on the offsite copy" — a true statement the original design could not make, because it would have had no snapshot to report at all.

`protected` deliberately requires a replica outside the source's failure domain, so that a local repository sharing a disk with the source data never reads as safe. Domains and rationale in [`04-concurrency-and-publication.md` §6.4](04-concurrency-and-publication.md#64-protected-requires-an-independent-failure-domain).

## 5. Destination verification

### 5.1 The problem with asking

"When was the last independently verified recoverable snapshot?" is the product's central promise ([`00-overview.md` §2](00-overview.md#2-product-promise)). It is also the one status a destination can fabricate for free.

A peer that lost the data to a failed disk, deleted it to reclaim space, or is running buggy software can answer "verified" while holding nothing. The obvious implementations do not help: asking it to hash a blob lets it cache the answer from the first challenge and reuse it forever; asking for the whole blob back defeats the purpose of not transferring it.

### 5.2 Keyed random-range challenge

The verifier selects a blob, a random byte range within it, and a fresh nonce, then requests:

```text
response = MAC(challenge_key, nonce ‖ blob_id ‖ range ‖ bytes_at_range)
```

The verifier recomputes the expected value from its own copy, or from another replica holding the same blob.

Because the nonce is fresh and the range unpredictable, the response cannot be precomputed, cached, or replayed. Producing it requires actually holding those bytes at that moment. The bandwidth cost is one MAC per challenge rather than one blob.

### 5.3 Sampling policy

Full verification of every blob on every cycle is prohibitive; verifying nothing is what we are fixing. The policy is therefore a coverage-versus-cost trade with the trade made visible:

- a bounded random sample per verification interval;
- weighted towards blobs longest since their last successful challenge;
- always covering the objects a *recent* snapshot depends on, so the newest recovery point is the best-verified one;
- full verification available on demand and before a recovery drill.

Status reports **coverage and challenge age**, not a boolean. "Verified" with no indication of how much was checked or how long ago is the kind of green light §23 of the original proposal warned about under "consumer UI hides degraded state".

### 5.4 What this does not prove

A challenge proves the destination holds those bytes **now**. It does not prove it will return them when asked to restore — a destination can pass every challenge and then refuse or fail at restore time. Nothing short of an actual restore proves that, which is why recovery drills exist ([`08-restore-and-recovery.md` §4.4](08-restore-and-recovery.md#44-lifecycle)).

Recorded in [`../threat-model.md`](../threat-model.md#t-8-destination-withholding-data).

## 6. Quotas and exhaustion

A destination enforces its quota. When a source reaches it:

- the transfer stops cleanly at a blob boundary — never mid-blob, and never leaving a partial object visible;
- the source is told **why**, distinguishing quota exhaustion from disk-full and from a transient error, because the three call for entirely different user actions;
- previously durable snapshots at that destination are unaffected;
- the backup set reports `degraded` for that destination while continuing to protect locally;
- retention at the destination proceeds under its own floor, which may in time free space.

Disk-full on the *destination's* underlying store is reported distinctly from quota exhaustion. Quota is a policy the destination chose; disk-full is a fault it needs to fix.

## 7. Relay

A relay forwards encrypted traffic between peers that cannot connect directly. It:

- cannot decrypt content or metadata;
- learns which device identities are communicating, and how much — traffic analysis is the residual exposure and is recorded in [`../threat-model.md`](../threat-model.md#t-13-relay-traffic-analysis);
- is optional, self-hostable, and reported in the connection path so a user always knows whether they are relayed;
- applies quotas and rate limits, since an open relay is otherwise an abuse vector.

Relay use is never silent. A user paying for metered bandwidth, or expecting LAN-speed transfer, needs to know when traffic is going the long way round.

---

**Previous:** [08 — Restore and recovery](08-restore-and-recovery.md) · **Next:** [10 — Observability](10-observability.md)

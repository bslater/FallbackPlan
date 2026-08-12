# 09 — Replication and peers

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §8.3–8.4, §16.2 · **Resolves:** [H6](../review/2026-08-architecture-review.md#h6--independently-verified-trusts-the-destination-to-report-on-itself), [C5](../review/2026-08-architecture-review.md#c5--snapshot-commit-is-defined-so-that-one-offline-destination-stalls-all-protection)

**Built:** Identity, pairing and the session layer built and carried over a real TLS socket; the object exchange (§1) built for the whole-repository scope ([peer-protocol 03](../../specifications/peer-protocol/03-replication.md)); quotas and their distinct exhaustion reporting (§6) built ([peer-protocol 05](../../specifications/peer-protocol/05-quotas.md)); destination verification (§5) not yet built — see [implementation status](../implementation-status.md).

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

**Built so far (peer-protocol 03, first slice).** The exchange runs for the widest scope — the source offers a whole repository, the destination declares the object keys it holds as an explicit inventory, and the source streams the rest, each object committed whole under a create-if-absent write so a re-run resumes with no checkpoint. Three decisions this section left open were settled there rather than in the architecture, because they are encoding and placement, not behaviour: the destination keeps each source's replica in a store it names locally by repository id (a storage path never crosses the wire, §3); an object commits atomically, so resumption is a property of the exchange rather than a negotiated position; and step 3's compact object-set filter is an *optional negotiated feature* layered over the explicit inventory, so a v1 implementation is complete without it. Snapshot scoping (steps 2, 4, 5) and the filter are a later slice; quotas (§6) are built per [peer-protocol 05](../../specifications/peer-protocol/05-quotas.md); verification (§5) follows.

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
- **The direction of storage is part of what is approved.** The ceremony's offer proposes which side stores for which — one way, the other, or both — and the acceptance confirms it inside the authenticated transcript, so the two grants cannot disagree about who lends the disk ([ADR-0030 Amendment 2](../adr/0030-peer-identity-and-pairing.md#amendment-2-2026-08--the-pairing-lifecycle-completes-roles-on-the-wire-endings-announced-terms-enforced)).

That pair of properties above — protocol-only access, ciphertext-only holding — is what makes "back up to a friend's computer" a reasonable thing to ask of a friend. Neither party has to trust the other with anything.

### 3.1 Ending a peering

Either side may end a peering unilaterally, at any time, for any reason — revocation is a local act and no protocol round-trip is ever a precondition for it ([peer-protocol 01 §3](../../specifications/peer-protocol/01-identity-and-pairing.md)). What the ending must not be is *silent*. The ender sends a best-effort **termination notice** when the peer is reachable (feature-gated, so an older peer is simply not sent what it cannot parse); a peer that was unreachable learns the same thing from the `Revoked` refusal at its next dial. Both paths produce a **durable notice** the user sees until acknowledged: the hub that lost a destination is told to reconfigure the sets that counted on it, and the spoke left holding a departed hub's ciphertext is told the data is now its own to evict, after a stated grace period. Eviction is the storing side's own decision on its own timetable — the notice creates awareness, never an obligation.

## 4. Durability policy

A backup set declares one or more named destinations, and its policy is evaluated over per-destination replication state ([`04-concurrency-and-publication.md` §6](04-concurrency-and-publication.md#6-commit-versus-replication), [ADR-0034](../adr/0034-hub-and-spoke-destinations.md)). **None of the destinations has to be local.** Publication always lands in the set's staging archive on the hub — that is what makes capture unconditional — but staging is a cache the hub manages, not a destination a policy may count:

```text
Snapshot captured when:
  - committed to the set's staging archive

Snapshot protected when:
  - at least one destination outside the source's failure domain: durable

Snapshot policy-compliant when:
  - every destination the set's policy requires: durable

Snapshot healthy when (example policy):
  - a local-path destination: verified within 7 days,  and
  - a peer destination:       verified within 30 days, and
  - a cloud destination:      durable within 24 hours
```

Because commit is to staging and replication is per destination, a destination that is offline delays *policy compliance* without blocking *capture*. The status display can say "captured, waiting on the offsite copy" — a true statement the original design could not make, because it would have had no snapshot to report at all.

`protected` deliberately requires a destination outside the source's failure domain, so that a repository directory sharing a disk with the source data never reads as safe — and the staging archive, which shares the source's domain by construction, never counts at all ([ADR-0018 Amendment 1](../adr/0018-replica-failure-domains.md#amendment-1-2026-08--the-domain-is-declared-per-configured-destination)). Domains and rationale in [`04-concurrency-and-publication.md` §6.4](04-concurrency-and-publication.md#64-protected-requires-an-independent-failure-domain).

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

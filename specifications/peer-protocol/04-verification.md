# 04 — Verification

**Normative.** Rationale in [architecture 09 §5](../../docs/architecture/09-replication-and-peers.md#5-destination-verification) and the threat it answers in [T-8](../../docs/threat-model.md#t-8-destination-withholding-data).

---

This document lets a source learn whether a destination still **holds** what it
claims to hold. "Verified" is the one status a destination can fabricate for
free: a peer that lost the data to a dead disk, deleted it to reclaim space, or
is running buggy software can answer "yes" while holding nothing, and the
obvious probes do not help — a plain hash can be computed once and cached
forever, and asking for the bytes back defeats the point of not transferring
them. The answer is the **keyed random-range challenge** (FR-VER-001): a proof
that can only be produced by a party holding those exact bytes at that exact
moment.

The exchange proves possession **now**. It does not prove the destination will
return the bytes at restore time — nothing short of a restore proves that,
which is why recovery drills exist ([architecture 08 §4.4](../../docs/architecture/08-restore-and-recovery.md#44-lifecycle))
— and it does not prove integrity of anything unchallenged. What it buys is
that "verified" in a status line traces to bytes actually read on the
destination's disk, sampled and dated, never to the destination's word.

## 1 Feature and placement

The exchange is gated by the feature **`destination-verification`**
([02 §6](02-session.md#6-feature-negotiation)): a source MUST NOT send a
challenge unless the feature is in the session's intersection, so an older
build is never sent a type it would refuse as `message_unknown`.

A source MUST **require** the feature of a destination it replicates to,
naming it in the hello's required set so a destination that does not offer it
is refused at negotiation with `feature_unsupported` before any object crosses
(FR-VER-006). This document once said the source records such a destination as
unverifiable and carries on; that was wrong, and the reason is worth stating
where it will be read. Verification is the stated mitigation for a destination
that discards data and claims otherwise ([T-8](../../docs/threat-model.md#t-8-destination-withholding-data)),
the feature set is the destination's own declaration, and a mitigation the
defended-against party may decline in silence is not a mitigation. It was also
the cheaper move: a destination that offered the feature and failed a challenge
earned a durable finding (§5), while one that never offered it earned nothing
at all.

A deployment may still keep a destination it cannot challenge, but not by
accident: the source's configuration must say so for that destination
explicitly, and a destination kept on those terms never reports `verified` and
never satisfies a gate that would delete the source's last copy of an object
(FR-VER-006, [ADR-0034 §6](../../docs/adr/0034-hub-and-spoke-destinations.md#6-the-costs-accepted)).

> The parallel wording in [06 §1](06-retention.md#1-feature-and-placement) for
> `retention-instruction` is deliberately *not* changed to match. A spoke that
> will not accept retention instructions costs disk; a destination that will
> not prove itself costs the guarantee. Only one of those is worth refusing a
> session over.

Challenges ride the replication session of [03](03-replication.md), after
`ReplicationAck` — and after the retention exchange of [06](06-retention.md)
when that feature also ran, because verifying a key the same session then
deletes proves nothing anyone keeps. The dialler is the **verifier**, under the
same loosening [06](06-retention.md) records: the side entitled to push
objects into a replica is the side entitled to ask whether they are still
there. A challenge at any other point in the session is `malformed`.

## 2 The challenge

The verifier selects a store key it can recompute the answer for, a random byte
range within that object, a fresh 16-byte nonce, and a fresh 32-byte challenge
key, and sends all of them. The destination reads exactly the named range from
its stored copy and answers:

```text
proof = HMAC-SHA-256(challenge_key, "fbp/verify/v1" ‖ nonce ‖ store_key ‖ u64_be(offset) ‖ u32_be(length) ‖ bytes_at_range)
```

The verifier computes the same value from its own copy and compares. Because
the nonce, key and range are fresh and unpredictable, the proof cannot be
precomputed, cached, or replayed (FR-VER-001); producing it requires holding
those bytes at that moment. The challenge key travels in the clear inside the
TLS session — its purpose is domain separation and freshness, not secrecy.

Three consequences the verifier MUST respect:

- **It can only challenge what it can recompute.** A source challenges keys its
  own staging archive still holds. A key the staging trim removed
  ([ADR-0034 §6](../../docs/adr/0034-hub-and-spoke-destinations.md#6-the-costs-accepted))
  has no local ground truth to compare against; challenging it from another
  replica's answer is a stated future extension, not this document.
- **Ranges come from the verifier's copy.** The verifier knows the object's
  exact length; a range it sends is always inside it. A destination whose copy
  is shorter cannot read the range and MUST answer `cannot-prove` (§3) — a
  truncated replica is exactly what the challenge exists to catch.
- **A wrong proof is a finding, not a protocol error.** The session continues;
  the verifier records the failure durably (FR-VER-005) and draws its own
  conclusions about the destination.

## 3 Sampling, coverage, and what is reported

How many challenges a session carries is the verifier's policy, not the
wire's. The policy requirements (FR-VER-002/003/004):

- a bounded random sample per verification interval, weighted towards objects
  longest since their last successful challenge;
- the most recent snapshot's dependencies always covered, so the newest
  recovery point is the best-verified one;
- full verification available on demand, and run before a recovery drill;
- status reported as **coverage and age** — how much was checked and how long
  ago — never as a bare boolean.

## 4 Messages

This document occupies types 264–265 of the range [02 §7](02-session.md#7-framing) reserves.

| Type | Message | Section |
|------|---------|---------|
| 264 | `VerificationChallenge` | §4.1 |
| 265 | `VerificationProof` | §4.2 |

Challenges are strictly request/response: the verifier MUST NOT send a second
`VerificationChallenge` before the previous proof arrives, and a destination
answers each challenge with exactly one proof, in order.

### 4.1 VerificationChallenge

Verifier → destination.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `bytes[16]` | Repository identifier the key belongs to |
| 2 | `text` | Store key to prove, ≤ 1024 bytes |
| 3 | `u64` | Range offset in bytes |
| 4 | `u32` | Range length in bytes, 1–65536 |
| 5 | `bytes[16]` | Nonce, fresh per challenge |
| 6 | `bytes[32]` | Challenge key, fresh per challenge |

A repository this peer is not attributed to ([05 §2](05-quotas.md#2-ownership)),
a length outside 1–65536, or a key under `tombstones/` or `leases/` is
`malformed`. A key the destination simply does not hold is **not** an error —
that is an answer (§4.2), and it is the interesting one.

### 4.2 VerificationProof

Destination → verifier, one per challenge.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u8` | Status: `0` = proof follows, `1` = cannot prove |
| 2 | `bytes[32]` | The proof (§2); present exactly when status is `0` |

`cannot prove` covers every honest inability — the key is not held, the object
is shorter than the range, the read failed. The destination MUST NOT guess or
zero-fill: answering with a proof over different bytes than the stored range is
indistinguishable from corruption at the verifier, and that is the correct
outcome. Status `1` with key 2 present, or status `0` without it, is
`malformed`.

## 5 What the verifier does with the outcome

A passed challenge is evidence about one range of one object at one moment; the
verifier aggregates. A failed or `cannot-prove` answer for a key the
destination's own inventory or keep-set says it should hold marks the affected
`(snapshot, destination)` pairs `degraded` and raises a warning requiring
action (FR-VER-005) — durable, like every fact a human must eventually see
([architecture 10 §3.1](../../docs/architecture/10-observability.md#31-how-a-client-learns-any-of-this)).
Nothing on the wire distinguishes "lost it" from "never had it"; the ledger the
verifier keeps is what tells those apart, and either way the destination is not
currently protecting that data.

---

**Previous:** [03 — Replication](03-replication.md) · **Next:** [05 — Quotas](05-quotas.md)

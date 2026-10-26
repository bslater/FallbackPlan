# 08 — Repair

**Normative.** Rationale in [ADR-0100](../../docs/adr/0100-a-peer-replaces-what-its-own-check-finds-damaged.md) and [architecture 09 §5](../../docs/architecture/09-replication-and-peers.md#5-destination-verification).

---

This document lets a replica's owner replace a blob that has rotted at a destination. The design rests on one constraint: **the destination holds no key, and takes nothing on the owner's word.** It cannot tell which blob a key names, so it cannot be told what to replace, only offered a replacement, which it takes or refuses on evidence it holds itself.

That evidence is the blob's own locator ([repository format 05 §4](../repository-format/05-blob.md#4-footer-locator)): the footer's offset, and the first eight bytes of the SHA-256 of every byte before the locator. Unkeyed and truncated, it proves nothing about which blob the bytes are. It does tell whether they have changed since they were sealed. This document calls checking a whole object against it the **locator check**:
- the object is at least sixteen bytes long;
- its last sixteen bytes parse as a locator whose `footer_offset` falls inside the blob, at or after the envelope and before the locator;
- the first eight bytes of the SHA-256 of bytes `[0, length − 16)` equal `digest_prefix`.

## 1 Feature and placement

The exchange is gated by the feature **`replica-repair`** ([02 §6](02-session.md#6-feature-negotiation)). An owner MUST NOT send a `RepairOffer` unless the feature is in the session's intersection. An owner SHOULD require the feature in its hello, so a destination whose build predates it is refused at negotiation and never sent a type it would refuse as `message_unknown`.

The feature gates only what the destination can understand. The check that defends the destination (§3) is made on its own copy and on the bytes it receives, and nothing in either hello can waive it. Withholding the feature can only leave damage where it is, which is why a gate is safe here.

Repair is a payload of its own, as retrieval is ([07](07-retrieval.md)). A session whose first payload frame is a `RepairOffer` is a repair session, and carries nothing else. It is not carried on a retrieval session, which holds no write path ([ADR-0058](../../docs/adr/0058-peer-write-adapter.md)). Nor does it follow a replication exchange's acknowledgement, because the sweep that finds damage reads over retrieval and has no object exchange to follow.

The destination serves a repair session only for the replica's **owner**: the peer the repository is attributed to ([05 §2](05-quotas.md#2-ownership)). It MUST refuse an offer naming any other repository with `terms_refused`, and MUST give the same refusal whether the repository is another peer's or was never stored there.

## 2 The exchange

1. The owner sends a **`RepairOffer`**: the repository, one blob's store key, and the length of the replacement it would send.
2. The destination judges its own copy (§3) and answers a **`RepairVerdict`**.
3. Only when the verdict is `send`, the owner sends the replacement as one `ReplicationObject` (no resume offset) and the `ReplicationChunk` frames that carry its bytes in order from offset zero ([03 §3.3](03-replication.md)). The object's key and length MUST be the offer's.
4. The destination checks what it received (§3) and answers a second `RepairVerdict`: `replaced` or `unmatched`.

The owner may then offer another key. Every offer in a session MUST name the repository of the first, and one that does not is `malformed`. The session ends when the owner closes it. Any other frame in a repair session is `malformed`.

A repair **never creates** an object, so it never moves the destination's usage against a quota ([05](05-quotas.md)). A key the destination does not hold is answered `not_held`, and filling it is the replication exchange's.

## 3 What the destination checks

An offer MUST name a key under `blobs/`. Anything else is `malformed`: nothing else a replica holds carries a check a keyless holder could make of a replacement.

The destination answers the offer from its own copy:

| Its copy | Verdict |
|----------|---------|
| Not held | `not_held` |
| Held, of a length other than the offer's | `length_differs` |
| Held, of the offer's length, and passes the locator check | `intact` |
| Held, of the offer's length, and fails the locator check | `send` |

When it asked for the replacement, it MUST stage the received bytes before touching its copy. It replaces its copy only when the staged bytes pass the locator check **and their last sixteen bytes are the last sixteen bytes of its copy**. Otherwise it answers `unmatched` and keeps its copy.

It replaces by removing its copy and creating the staged bytes under the key, and only then answers `replaced`. A cut between the two leaves the key absent, which the next replication exchange fills if the owner still offers it.

**Why the locator must match.** Rot that lands anywhere before a blob's last sixteen bytes leaves its locator as it was sealed, still naming the bytes that were written. A replacement that passes the check under that locator is those bytes, unless someone found a second preimage of a 64-bit SHA-256 prefix at the same length and footer offset. An owner, honest or not, can therefore replace a copy only with the bytes sealed under its key, and only where the copy no longer holds them:
- a sound copy is never touched, because it passes the check;
- a damaged one is never replaced with anything else, because the locator binds the replacement.

A repair needs no authority beyond the pairing for that reason. It is the reason this document asks for none, where a deletion asks for the reclaim signature ([06 §3](06-retention.md#3-what-the-spoke-validates)).

**What the check cannot see.** A well-formed blob stored under another blob's key passes it, and is answered `intact`. A copy whose length changed, or whose last sixteen bytes rotted, names no blob a replacement could match. The destination keeps all of these. Telling them apart from sound copies needs the repository's keys, which it does not hold.

## 4 Messages

This document occupies types 280–281 of the range [02 §7](02-session.md#7-framing) reserves, and reuses 03's `ReplicationObject` (258) and `ReplicationChunk` (259) for the replacement's bytes.

| Type | Message | Section |
|------|---------|---------|
| 280 | `RepairOffer` | §4.1 |
| 281 | `RepairVerdict` | §4.2 |

### 4.1 RepairOffer

Owner → destination.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `bytes[16]` | Repository identifier |
| 2 | `text` | The blob's store key, ≤ 1024 bytes, the bound [03 §3.3](03-replication.md#33-replicationobject-and-replicationchunk) sets on an object's key |
| 3 | `u64` | The length of the replacement the owner would send |

A missing key, a repository identifier of another width, or an empty or over-long store key is `malformed`.

### 4.2 RepairVerdict

Destination → owner: once for every offer, and once more after a replacement it asked for.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `text` | The store key, as offered |
| 2 | `u16` | The verdict |

| Code | Verdict | Meaning |
|------|---------|---------|
| 1 | `send` | Its copy fails the locator check and is the offered length: send the replacement |
| 2 | `replaced` | The replacement passed and now stands in place of the damaged copy |
| 3 | `intact` | Its copy passes the locator check; it keeps it |
| 4 | `not_held` | It holds nothing under the key; a repair creates nothing |
| 5 | `length_differs` | Its copy is not the offered length; it keeps it |
| 6 | `unmatched` | The replacement failed the check or stated a locator its copy does not; it keeps its copy |

A verdict to an offer is one of 1, 3, 4 or 5; a verdict to a replacement is 2 or 6. Any other, a code this table does not define, or a key other than the one offered is `malformed`. An owner MUST NOT read a code it does not know as any it does: a verdict decides whether a blob crosses.

## 5 What the owner does

What the owner offers is its own business, and what it does after is not the destination's, but a conforming owner:
- **proves its replacement before offering it**, against the repository's keys: its whole blob hashing to the digest sealed into its footer, and its envelope naming the blob the key derives from. The destination's check cannot tell another well-formed blob from this one, and the owner's can;
- **counts a `replaced` as a claim**, and proves the blob again by reading it back over [07](07-retrieval.md) before treating the damage as gone. A destination's word about what it holds is never proof of it ([04 §5.1](04-verification.md#51-what-a-source-with-no-copy-of-its-own-can-prove));
- **treats `not_held` as the damage gone**, not repaired, and leaves refilling the key to replication;
- **does not offer another copy after `intact`, `length_differs` or `unmatched`**. Each is about the destination's copy, and every proven replacement is the same bytes.

---

**Previous:** [07 — Retrieval](07-retrieval.md) · **Next:** [README](README.md)

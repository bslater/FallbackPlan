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
| 4 | `bytes[32]` | *(optional)* The repository's **reclaim public key** ([ADR-0055](../../docs/adr/0055-reclaim-authority.md)) — what this destination checks a deletion instruction's signature against ([06 §3](06-retention.md#3-what-the-spoke-validates)) |
| 5 | `bytes[32]` | *(optional)* The installation's **claim public key** ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)) — what this destination checks a claim against (§6) |

A destination that does not implement the offered format capability MUST refuse with `feature_unsupported`. A destination that will not accept this repository at all MUST refuse with `not_paired` — it is a policy refusal, and no finer reason is owed a peer (§7).

Key 4 or key 5 present with a length other than 32 bytes is `malformed`. A destination MUST NOT silently ignore a key of the wrong width: one that did would go on accepting unsigned deletion instructions while believing it held a key to check them against, or would refuse the owner's own claim while believing it held a key to check that.

A destination records keys 4 and 5 **at first attribution** ([05 §2](05-quotas.md#2-ownership)) and MUST NOT let a later offer replace a key it already holds — the peer sending deletion instructions is exactly the peer that would like the key they are checked against to be its own, and the same is true of whoever would like the replica handed to them. A destination whose attribution carries neither key yet, or only one of them, MAY record what a later offer publishes: filling an absence is not replacing an answer, and without it a peering established before these keys existed could never be secured without being torn down. Each key fills independently — a source may publish one and not the other.

A source with no key to publish — an older build, or a repository provisioned before the decision — omits the key, and an older destination skips it like any other key it does not know.

Key 5 is the **installation's** key rather than the repository's, so the same 32 bytes appear against every repository one installation stores at this destination; the attribution stays per repository, because the quota ([05 §1](05-quotas.md)) and the retrieval gate ([07 §4](07-retrieval.md)) are.

### 3.2 ReplicationInventory

Destination → source, one or more pages.

**`ReplicationInventory`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `array of text` | Object keys the destination already holds in scope, ≤ 4096 per page, each ≤ 1024 bytes |
| 2 | `bool` | Whether another page follows |
| 3 | `u64` | *(optional)* Bytes the destination can still accept under the quota in force ([05 §1](05-quotas.md)) |

The keys are the store's own object keys ([architecture 02](../repository-format/README.md) names them). A destination that holds nothing in scope sends one page with an empty array and `false`. The page cap keeps each frame under the [00 §2.3](00-conventions.md#23-limits-are-the-protocols-own) limit without the source having to trust the destination's framing.

**Key 3 is optional and its absence is not zero.** A destination under no quota omits it, and so does one implementing an earlier revision of this document; both mean *not stated*, which a source MUST NOT read as *no room*. A destination whose quota is fully consumed sends `0`, which is a different statement and MUST be sent rather than omitted. A destination that sends key 3 SHOULD send it on every page of the inventory; a source that sees it more than once takes the last.

The headroom rides here rather than in the hello or the terms, and both alternatives were rejected for reasons worth recording. Terms are persisted in the pairing grant and compared for narrowing ([05 §6](05-quotas.md)), so a per-session number there would announce a reduction on every session. The hello is too early: the destination does not yet know which repository is coming, and computing usage means walking every object it holds — a cost the periodic verification sessions would pay for a number nobody reads. By the inventory the scope is known and the destination has already computed `quota − usage` in order to enforce the boundary stop of [05 §4](05-quotas.md).

A source MUST NOT treat a small headroom as a refusal. The boundary stop already refuses the exact object that would cross the line, with exact numbers, at the exact moment, and preserves everything committed before it; key 3 exists so the operator hears about it a session earlier, not so the source invents an earlier refusal.

### 3.3 ReplicationObject and ReplicationChunk

Source → destination, for each object the destination lacks: one `ReplicationObject`, then zero or more `ReplicationChunk`.

**`ReplicationObject`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `text` | The object's key, ≤ 1024 bytes |
| 2 | `u64` | The object's total length in bytes |
| 3 | `u64` | Optional. Where this transfer begins within the object; absent means zero |

**Key 3 is optional and its absence is zero.** A source MUST omit it for a whole-object transfer, so a destination that does not implement resumption reads exactly the message it has always read. A source MUST NOT send a non-zero key 3 unless `partial-object-resume` ([02 §6](02-session.md#6-feature-negotiation)) is in the session's intersection, and a destination that has not negotiated it MUST refuse one as `malformed`. A key 3 greater than key 2 is `malformed`: it names a gap no later chunk can fill.

**`ReplicationChunk`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u64` | The offset of these bytes within the current object |
| 2 | `bytes` | The bytes, ≤ the replication chunk limit ([00 §2.3](00-conventions.md#23-limits-are-the-protocols-own)) |

A `ReplicationChunk` belongs to the object named by the most recent `ReplicationObject`; a chunk with no current object, or an offset that is not the running total of bytes already received for it — counting the resume point of key 3 as bytes already received — is `malformed`. An object of length 0 is a `ReplicationObject` with no chunks. Chunking exists because a single frame is bounded to 16 MiB ([02 §7](02-session.md#7-framing)) and an object may exceed it; it is not fragmentation the destination reassembles into anything but the object's own bytes.

The destination MUST NOT make an object visible in its store until every byte of it has arrived — it commits the object whole, or not at all (§5).

### 3.3.1 ReplicationPartial

Destination → source, once, immediately after the last `ReplicationInventory` page, and only when `partial-object-resume` is in the session's intersection. The message is sent even when it declares nothing: a source that expected a frame and did not receive one would read the next message in its place.

**`ReplicationPartial`**

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `array of text` | Object keys the destination holds part of, ≤ 64 entries, each ≤ 1024 bytes |
| 2 | `array of u64` | Bytes staged for each, parallel to key 1 |
| 3 | `array of bytes[32]` | SHA-256 of exactly those staged bytes, parallel to key 1 |

The three arrays MUST have the same length; a declaration whose arrays disagree is `malformed` rather than trimmed to the shortest, because pairing a key with another entry's digest is how a resumed transfer would land bytes in the wrong object. A digest that is not 32 bytes is `malformed` rather than skipped: it is the check this message exists for, arriving broken.

Each digest MUST be computed from the staged bytes at the time of declaring, not carried forward from when they arrived. A prefix that was damaged between sessions then fails the source's comparison and the object restarts, which is the outcome this rule exists to produce.

A declaration is a **claim, not an instruction**. The source decides where a transfer begins, and MUST NOT accept a claim it has not checked against its own copy of the object: it reads its own first *n* bytes, computes SHA-256, and compares. Where they differ — or where the source declines for any reason — it sends the object with key 3 absent and the destination starts again from zero. A destination MUST NOT treat a whole-object transfer as an error after declaring a partial.

Staged bytes are **not** part of the replica. They answer no read, appear in no inventory, and cannot satisfy a verification challenge ([04](04-verification.md)); a destination MUST NOT make them visible in its store before the object is complete (§5). A destination MAY discard a staged prefix at any time for any reason.

### 3.4 ReplicationComplete and ReplicationAck

**`ReplicationComplete`** — source → destination, after the last object.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u64` | The number of objects the source sent this scope |

**`ReplicationAck`** — destination → source, in answer.

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `u64` | The number of objects the destination received and committed |
| 2 | `bytes` | The replication receipt's signed bytes (§3.5) — optional |
| 3 | `bytes[64]` | The destination's Ed25519 signature over key 2 under its device key — optional |

Keys 2 and 3 are present together or not at all; one without the other, or a receipt that does not parse, is `malformed`. They are additive and ungated ([02 §6](02-session.md#6-feature-negotiation)): a destination that predates receipts sends neither, a source that predates them skips both, and an absence can only mean less.

The ack confirms receipt, not durability against a challenge — that is [04](README.md#documents)'s verification, which this document does not carry. A destination that committed fewer objects than the source sent has already refused the offending object (§7); the ack is reached only when the transfer completed. The receipt it carries is the destination's own statement of what this session created and what it holds afterwards ([ADR-0064](../../docs/adr/0064-replication-receipts.md)); it is checked by the source against what the source sent and what the inventory declared, and it is the one record on which a source may count a destination complete — as the destination's attestation, never as possession (§9).

### 3.5 The replication receipt

The destination's signed statement of what a push created and what its replica holds afterwards ([ADR-0064](../../docs/adr/0064-replication-receipts.md)), issued after the last commit and before the acknowledgement, on every push — one that committed nothing included. It is signed under the device key because that is the only key the destination holds that the source can check, and carried in the acknowledgement because this session is the only place the source already trusts that identity — the same three choices as the deletion receipt ([06 §4.3](06-retention.md#43-the-deletion-receipt)).

The signed bytes are a fixed, label-separated encoding rather than a CBOR map, so that the statement is exactly its bytes ([00 §4](00-conventions.md#4-domain-separation)); integers are big-endian.

| Field | Width | Meaning |
|-------|-------|---------|
| label | 31 | `fbp-peer-v1:replication-receipt`, ASCII |
| session_id | 32 | The session the push arrived in ([02 §3.5](02-session.md)) |
| repository_id | 16 | The repository offered |
| commander_public_key | 32 | The pushing device's Ed25519 public key |
| issued_at | `u64` | Unix milliseconds |
| committed_count | `u64` | Objects this session created — equal to key 1 |
| listed_count | `u32` | At most 4096, and never more than committed_count |
| committed | (`u32` length ‖ UTF-8 key) × listed_count | Those keys, in commit order; beyond the cap the count stands for the rest |
| held_objects | `u64` | Objects held for the repository after this session — never less than committed_count |
| held_bytes | `u64` | Bytes held for the repository after this session |

The held figures are the destination's inventory walk (§3.2) plus what this session committed; a destination makes no third pass over its replica to issue one. A reader parses the statement as the exact inverse of this table and refuses anything left over: trailing bytes are `malformed`, because a reader must never show as attested what the signature does not cover.

**What the source checks**, in order, stopping at the first failure and naming it: the signature is the pinned peer's; `session_id` is this session's; `repository_id` and `commander_public_key` are its own; `committed_count` equals key 1; every listed key was sent this session; and `held_objects` is at least the inventory's count plus `committed_count` — a smaller figure means something declared has gone since, and is not a figure to count on. A receipt that fails is not filed and is reported, never acted on: the objects have already been committed, so refusing the session would change nothing at the destination.

**Both parties file their own copy** — the destination before it acknowledges, the source after it verifies — under `<state>/receipts/replications/<repository>/`, one immutable file per receipt in the same envelope the deletion receipts use, each naming its kind, and re-check the signature on every read. A copy the destination cannot write is reported on its side and does not withhold the acknowledgement. The `receipts` verb on either host reads both kinds back without the service, and the service answers them over the command contract (`list_receipts`, 1.33).

## 4 Scope

The scope in the offer is a short token naming what the source offers. This revision defines one value:

- `all` — every object the source holds for the offered repository.

Snapshot-scoped replication (a specific snapshot and its object closure) is a later revision; it will carry a structured scope in this field, which is why the field is present now rather than assumed. A destination that does not understand a scope token MUST refuse with `malformed`.

## 5 Resumption and atomicity

The destination commits each object atomically: it holds an object's bytes until complete, then writes it under a create-if-absent condition ([architecture 04 §5](../../docs/architecture/04-concurrency-and-publication.md)). An interruption therefore never leaves a partial object visible, and re-running the exchange resumes correctly with no special state — the destination's next inventory already lists everything it committed, so the source sends only what is still missing. → FR-REP-003

Objects are immutable and content-addressed, so a create-if-absent write is idempotent: an object the destination already holds is identical to the one the source would send, and re-sending it is wasteful but never wrong. This is what makes resumption a property of the exchange rather than a checkpoint the two sides must agree on.

Resumption at boundaries *within* a single large object is defined by the `partial-object-resume` feature ([§3.3.1](#331-replicationpartial); [ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)). Without it — and towards any peer that does not offer it — an interrupted object is re-sent whole, which remains correct and is what this section's atomicity argument rests on.

With it, the destination declares what it part holds and the **source** decides whether to begin there, after checking the declared digest against its own bytes. Nothing above changes: the destination still commits whole or not at all, a create-if-absent write is still idempotent, and a staged prefix is still invisible until the object is complete. What the feature adds is a checkpoint the two sides agree on for the duration of one object — which §5 previously did without, and could do without only by re-sending everything an interruption cost.

The agreement is deliberately weak in one direction: the destination's claim binds nothing, and a source that cannot verify it simply sends the object whole. A peer cannot therefore cause a source to skip bytes it has not proved it holds.

## 6 The claim

A machine rebuilt after total loss proves a replica is its own and has the attribution follow it to the device identity it now has ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) Amendment 2). Gated by the `replica-claim` feature ([02 §6](02-session.md#6-feature-negotiation)).

The claimant holds the **passphrase and nothing else** — no repository id, no salt, no kit. The claim key derives from the passphrase and the installation's KDF salt ([repository format 03 §4](../repository-format/03-keys.md)), and the salt is inside the replica, behind the attribution gate the claimant is trying to pass. So the ceremony is two phases in one session: the destination first says which derivations to run, then checks the claim. A session that carries a claim carries nothing else; the open is the **first payload frame**. The claimant pairs first, as any new peer does ([01](01-identity-and-pairing.md)) — pairing establishes who is speaking and nothing more.

### 6.1 ReplicationClaimOpen and ReplicationClaimParameters

**`ReplicationClaimOpen`** — claimant → destination. No keys: a claimant that has lost everything has nothing to say yet. A reader skips any key a later version adds.

**`ReplicationClaimParameters`** — destination → claimant

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `array` of `bytes[16]` | KDF salts, one per distinct derivation, **sorted by byte value** |
| 2 | `array` of `uint` | Argon2id memory in KiB, parallel to key 1 |
| 3 | `array` of `uint` | Argon2id iterations, parallel to key 1 |
| 4 | `array` of `uint` | Argon2id parallelism, parallel to key 1 |

The entries are the **distinct** `(salt, parameters)` pairs behind every replica this destination holds whose attribution carries a claim public key ([05 §2](05-quotas.md#2-ownership)), read from each replica's own descriptor ([repository format 01 §3.3](../repository-format/01-object-layout.md)). A replica whose descriptor cannot be read is skipped, not fatal. Sorted by salt, so the answer says nothing about the order attributions were recorded in; distinct, so one installation storing several sets here costs the claimant one derivation. At most 16 entries; the first 16 in salt order when there are more.

Arrays of unequal length, a salt that is not 16 bytes, an unsorted or repeated salt, more than 16 entries, or a cost of zero is `malformed`. So is a cost above the caps — memory above 1 GiB, iterations above 64, parallelism above 64 — and a reader MUST refuse such an entry rather than clamp it: the claimant is about to run Argon2id on parameters the destination chose, and a destination naming a terabyte of memory is naming a denial of service.

An **empty** answer is an answer, not a refusal: nothing here is claimable, which is what a paired peer whose replicas were all attributed before key 5 existed is told. The claimant stops there.

**What this hands a paired peer, and what it does not.** It learns how many installations have claimable replicas here, their public salts and their KDF costs — exactly what it would learn by holding any one of those replicas, since a descriptor is served unencrypted within an authorised retrieval ([07 §4](07-retrieval.md#4-authorization)). It does **not** learn repository ids, sealing public keys, or which fingerprint each pair belongs to. A salt without a verifier beside it is no offline oracle for the passphrase; the only oracle is the claim itself, one per session, refusing identically. The sealing public key, which *is* an offline wrong-passphrase verifier, is deliberately not served, which is why the parameters are not simply the descriptor.

### 6.2 ReplicationClaim and ReplicationClaimAccepted

**`ReplicationClaim`** — claimant → destination

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `array` of `bytes[32]` | Claim public keys, one per served derivation, each the 32 bytes its predecessor published as §3.1's key 5 |
| 2 | `array` of `bytes[64]` | Ed25519 over the signed material below, parallel to key 1, each under the private half of that entry's key |

One entry per served derivation, because the claimant cannot tell which salt is its own: every pair it was given yields a well-formed key under its passphrase, and only the destination knows which one it recorded. No entries, arrays of unequal length, more than 16 entries, or an entry of the wrong width is `malformed`. A claim arriving as the first payload frame — the shape this ceremony replaced — is `malformed` too: there is nothing for it to have been derived against.

**The claim names no repository**, and that is deliberate. A claimant that has lost everything cannot supply one; nor can it ask, because the owner inventory ([07 §3.5](07-retrieval.md#35-owner-inventory)) is itself gated on attribution and answers an unrecognised device with an empty page. The claim public key is therefore the selector: the destination acts on every repository it recorded any presented key against.

The signed material is the concatenation, all fields fixed-length so no separator is needed ([00 §4](00-conventions.md#4-domain-separation)):

```text
"fbp-peer-v1:replica-claim" ‖ session_id ‖ claimant_fingerprint
```

where `session_id` is [02 §3.5](02-session.md#35-the-session-identifier)'s 32 bytes and `claimant_fingerprint` is the claimant's peer fingerprint as lower-hex text. The same bytes are signed under every entry's key; the entries differ by key, not by statement. Binding to the session is what makes a recorded claim verify against nothing in a later connection — the same reason [06 §4.1](06-retention.md#41-retentionoffer) binds a retention instruction, and it matters more here, because a replayed claim re-points ownership rather than deleting one page.

**`ReplicationClaimAccepted`** — destination → claimant

| Key | Type | Meaning |
|-----|------|---------|
| 1 | `array` of `bytes[16]` | The repositories now attributed to the claimant, at most 1024 |

An entry that is not 16 bytes, or an array longer than the limit, is `malformed`. The answer exists because the claimant could not have known to ask: it named no repository, and this is the list it then opens for retrieval.

A destination MUST verify **every** signature against the key its entry presents, and MUST act only on entries whose key matches one **it recorded** — the presented key is a claim about identity, and the recorded key is what makes it checkable. On success it re-points each matching attribution at the claimant's fingerprint, replacing the old one rather than duplicating it: one repository, one owner here. The recorded keys are unchanged; the same passphrase re-derives them.

A claim in which no key matches anything recorded here, and one in which any signature does not verify, MUST refuse **identically**, with `terms_refused` and the same text. This is [07 §4](07-retrieval.md#4-authorization)'s reconnaissance rule in the place it matters most: distinguishable refusals would make this a way to ask a stranger's peer whether it holds a given installation's replicas. A destination MUST check every entry and collect every match before acting on any, so the two do not time differently. The stated cost: at a peer, a wrong passphrase reads as *nothing here is claimable*, not *wrong passphrase*. A claimant can only be told the latter with one of its own archives mounted.

A destination that does not offer `replica-claim` MUST refuse the open with `feature_unsupported` naming the feature, rather than letting it fall through to the offer reader. The claimant is mid-recovery, and "expected a replication offer" is true and no use at all.

**A replica attributed before key 5 existed has nothing to check against**, and is not in the parameters. It becomes claimable the moment an updated source makes one more offer, because a destination fills an absence — but if the machine died before that offer, there is nothing, and the remedy is the destination's own operator re-pointing the attribution out of band. That verb is not on this wire: it is the destination's own command contract (`reattribute_replica`, [ADR-0053 Amendment 3](../../docs/adr/0053-peer-claim-and-configuration-recovery.md#amendment-3-2026-09--the-operators-re-attribution-is-a-stated-verb)), local to the destination's operator, and refused there for any attribution that does carry a key — the claim is the only path for those.

## 7 Framing and limits

Frames are as [02 §7](02-session.md#7-framing) defines them. This document occupies the reserved 256+ type range:

| Type | Message | Section |
|------|---------|---------|
| 256 | `ReplicationOffer` | §3.1 |
| 257 | `ReplicationInventory` | §3.2 |
| 258 | `ReplicationObject` | §3.3 |
| 259 | `ReplicationChunk` | §3.3 |
| 260 | `ReplicationComplete` | §3.4 |
| 261 | `ReplicationAck` | §3.4 |
| 266 | `ReplicationPartial` | §3.3.1 |
| 267 | `ReplicationClaim` | §6.2 |
| 268 | `ReplicationClaimAccepted` | §6.2 |
| 269 | `ReplicationClaimOpen` | §6.1 |
| 270 | `ReplicationClaimParameters` | §6.1 |

The per-message body limits are in [00 §2.3](00-conventions.md#23-limits-are-the-protocols-own). The one that constrains the wire design is the chunk limit: an object larger than it is sent as several chunks, none of which — with its CBOR framing — may push a frame past the 16 MiB cap.

## 8 Refusal

Replication reuses [02 §8](02-session.md#8-errors-and-refusal)'s `SessionRefuse` and its codes; it defines no error mechanism of its own. The codes this document uses: `not_paired` (a grant that does not permit storing here, §1), `feature_unsupported` (an unimplemented format capability, §3.1, or a claim opened where `replica-claim` is not offered, §6), `terms_refused` (a claim that proves nothing, §6.2), and `malformed` (a chunk out of order, a scope not understood, a claim sent before its open or served parameters outside the caps, §6, or any body that violates this document). A refusal closes the session, as everywhere in this protocol; there is no partial transfer left half-open.

## 9 What replication does not carry

**No key material and no plaintext, as [02 §9](02-session.md#9-what-a-session-does-not-carry) requires of every payload.** The objects that cross are encrypted repository objects; their keys are store keys, not file paths. A destination stores what it cannot read. → NFR-SEC-001, NFR-SEC-004, NFR-SEC-009

**No storage location.** Where a destination keeps a replica is its own choice and never appears on this wire ([01 §4](01-identity-and-pairing.md#4-terms) keeps storage paths off the protocol). The offer names the repository; the destination decides where its objects live.

**No possession proof.** The receipt (§3.5) is the destination's own statement of what it committed and holds, checked for consistency with what the source sent and what the inventory declared, and nothing more. A source that counts a destination complete on it counts the destination's word; whether the bytes are there is [04](04-verification.md)'s question, and a receipt MUST NOT be reported as verification of anything. → FR-VER-001, FR-GC-009

---

**Previous:** [02 — Session](02-session.md) · **Next:** [04 — Verification](04-verification.md)

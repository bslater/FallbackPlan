# 07 — Retrieval

**Normative.** Rationale in [ADR-0041](../../docs/adr/0041-guided-restore-and-peer-retrieval.md); the restore surface it serves in [architecture 08](../../docs/architecture/08-restore-and-recovery.md).

---

This document lets an owner read its own replica **back** from a destination,
over the same authenticated session replication pushed it through. Everything
served is the owner's ciphertext returning home: the destination decrypts
nothing, learns nothing new about the content, and serves nobody but the peer
its attribution ledger says the replica belongs to. What retrieval buys is a
restore path that needs no shared disk and no recovery kit — a hub whose
staging archive is gone opens the replica as a repository over the wire,
rebuilds a catalogue from its index plane, and fetches only the objects a
restore plan actually needs.

## 1 Posture

A replica is **read-only** over retrieval. The message set has no write and no
delete on purpose: writing travels replication (03), aging travels retention
instructions (06), and a session that could do either through the read path
would be one state machine wearing two hats.

The observable cost is stated rather than hidden: a destination serving
retrieval learns **which objects** the owner reads and when. It already holds
the ciphertext; access patterns are the only new information, and an owner for
whom that is too much should restore from a local replica or the recovery kit
instead.

## 2 Feature and negotiation

The exchange is gated by the feature **`retrieval`**
([02 §6](02-session.md#6-feature-negotiation)). An owner MUST NOT send a
retrieval frame unless the feature is in the session's intersection, and MUST
name it in the hello's required set when the session's purpose is retrieval,
so an older destination refuses at negotiation with `feature_unsupported`
rather than at the first unknown frame.

Retrieval frames are payload messages: they are permitted only once the
session is **Open** ([02 §2](02-session.md#2-session-states)), and the roles
are those of replication — the dialling owner must hold a grant whose role
admits storing here (`stores-here` or `both`), because what it reads is what
that role stored.

## 3 Messages

All messages are CBOR maps with integer keys, framed as every other peer
message ([00 §2](00-conventions.md#2-what-differs)). Limits are those of
[00 §2.3](00-conventions.md#23-limits-are-the-protocols-own); the chunk bound is `ReplicationChunk`'s
1 MiB.

### 3.1 `retrieve_open` (272)

| Key | Type | Value |
|-----|------|-------|
| 1 | bytes[16] | `repository_id` — the replica to open, or all zeros for the owner inventory (§3.5) |
| 2 | uint | `format_capability` — the repository format the owner expects |

The first payload frame of a retrieval session. A destination that cannot
serve the named format refuses `feature_unsupported`.

### 3.2 `retrieve_ready` (273)

An empty map. The destination's grant: the replica is this owner's to read,
and the session is retrieval-only from here — any later frame that is not
`retrieve_list` or `retrieve_read` is refused `malformed`.

### 3.3 `retrieve_list` (274) / `retrieve_list_page` (275)

`retrieve_list`: key 1 `prefix` (text, ≤1024 bytes), key 2 `after` (text,
≤1024 bytes; empty starts at the beginning). `retrieve_list_page`: key 1
`keys` (array of text, ≤4096), key 2 `lengths` (array of uint, parallel to
`keys`), key 3 `more` (bool). Keys come in ordinal order; the owner resumes
by sending the last key of a page as `after`.

### 3.4 `retrieve_read` (276) / `retrieve_data` (277)

`retrieve_read`: key 1 `key` (text), key 2 `offset` (uint64), key 3 `length`
(uint64, ≤ the chunk bound; 0 asks for existence and total length only).
`retrieve_data`: key 1 `found` (bool), key 2 `total_length` (uint64), key 3
`bytes` (bytes, ≤ the chunk bound).

Strictly request/response, one outstanding request: every read is answered by
exactly one data frame. A read past the object's end answers the bytes that
exist rather than refusing, so an owner never has to guess lengths; a longer
range is fetched by further reads.

### 3.5 Owner inventory

A `retrieve_open` whose `repository_id` is all zeros opens the **owner
inventory**: `retrieve_list` pages then answer the repository ids (lowercase
hex, as listing keys with length 0) the destination's attribution ledger
assigns to the dialling identity — and nothing else. This is how a hub that
lost its staging learns what to ask for. `retrieve_read` is refused
`malformed` in an inventory session.

## 4 Authorization

The destination MUST serve a replica only when **both** hold: the attribution
ledger ([05 §2](05-quotas.md#2-ownership)) assigns the named repository to
the dialling peer's pinned identity, and the replica exists on disk. Both
failure shapes — someone else's replica, and one never stored here — MUST
refuse identically (`terms_refused`, one message), because which of the two it
was is reconnaissance the requester is not owed.

Within an authorised replica every key is servable, `keys/` included: the
key objects are the owner's own passphrase-wrapped bytes, indistinguishable
in sensitivity from every other object the destination already holds for it.

## 5 Flow and termination

```
owner                          destination
  retrieve_open  ─────────────▶
                 ◀─────────────  retrieve_ready | session_refuse
  retrieve_list  ─────────────▶
                 ◀─────────────  retrieve_list_page
  retrieve_read  ─────────────▶
                 ◀─────────────  retrieve_data
  …                              …
  (close)        ─────────────▶  (session ends)
```

The owner ends the session by closing the stream, exactly as replication's
post-acknowledgement phase ends. There is no goodbye frame to lose.

## 6 Refusals

`feature_unsupported` — the format capability or the feature itself;
`terms_refused` — not this owner's replica (§4); `malformed` — a frame that
violates this document or arrives outside its state; every refusal is a
`session_refuse` written before the destination closes, per
[02 §8](02-session.md#8-errors-and-refusal).

## 7 Security considerations

Nothing here weakens the confidentiality story: ciphertext out is ciphertext
back. The destination gains read-pattern knowledge (§1) and nothing else; the
owner gains no way to read another peer's replicas (§4, enforced by
attribution, with no-reconnaissance refusals); and a stolen device that was
never paired gains nothing at all, because retrieval sits behind the session's
pinned-identity authentication like every other payload. Availability is the
honest limit: a destination can refuse or stall a retrieval exactly as it
could withhold data at any restore — which is what verification (04) and
multiple destinations exist to bound.

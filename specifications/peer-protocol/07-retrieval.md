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

**The attribution the ledger assigns can move, and §5 is the only thing that
moves it.** A device that has lost its durable local state is a new identity
to every destination ([01 §1](01-identity-and-pairing.md#1-peer-identity)), and
under this section alone its recovery is unreachable — the ledger names a
fingerprint that no longer exists. §5 lets the holder of the repository
passphrase re-point that attribution to its current identity. Nothing else
does: re-pairing does not, an operator's assertion at either end does not, and
possession of the recovery kit does not.

## 5 Claiming a replica

**Disaster recovery.** The case is total loss — the machine is gone, not
degraded, and the surviving copy is here. → [ADR-0046](../../docs/adr/0046-replica-claim-after-total-loss.md),
[FR-DR-001..005](../../docs/requirements/functional.md#disaster-recovery)

### 5.1 Feature

The exchange is gated by the feature **`replica-claim`**
([02 §6](02-session.md#6-feature-negotiation)). Neither side may send a claim
frame unless the feature is in the session's intersection. A claimant SHOULD
name it in the hello's required set, so a destination running an older build
refuses at negotiation and the operator learns "this peer cannot do that yet"
as a capability rather than as a wrong passphrase.

### 5.2 The credential

The claim credential is derived from the repository passphrase and a token the
destination chose. It is **not** stored by the claimant and never appears on
the wire:

```text
claim_root   = Argon2id(passphrase, descriptor.kdf_salt, descriptor.kdf_parameters)
claim_seed   = HKDF-Expand(PRK = claim_root, info = "fbp/peer-claim/v1" ‖ claim_token, L = 32)
claim_public = Ed25519 public key of claim_seed
```

`claim_root` is the same Argon2id output the repository format already derives
from the passphrase — the KEK of [repository format 03
§2](../repository-format/03-keys.md#2-key-encryption-key) for a v1 repository,
and the root of a v2 write-only repository. Both formats therefore claim by
one path, which is why the label carries no format-version suffix. The
parameters and salt come from the replica's own descriptor, which the
destination holds and serves.

`claim_seed` is an Ed25519 private-key seed in the sense of
[RFC 8032](https://www.rfc-editor.org/rfc/rfc8032) §5.1.5 — the same
interpretation [repository format 03 §4](../repository-format/03-keys.md#4-derived-keys)
fixes for the repository signing key, and deliberately a **different** key: a
manifest-signing key reused as a network authentication key would be exactly
the cross-protocol reuse [00 §4](00-conventions.md#4-domain-separation) exists
to prevent.

### 5.3 The token is per destination, and is not a secret

`claim_token` is 16 random bytes minted by the **destination**, once per
replica, when it first accepts that repository ([03 §3.2](03-replication.md)).
It is stored beside the attribution and disclosed to any peer that asks to
claim.

Its secrecy is not what it is for. Its uniqueness is. Two destinations holding
replicas of the same repository mint different tokens, so the derived keypairs
differ and a proof produced at one destination is inert at the other. Without
it, one captured proof would claim that repository everywhere it is stored.

A destination that holds no `claim_token` and no `claim_public` for a replica —
one stored before this document — MUST refuse a claim for it and MUST say that
the replica predates the ceremony, rather than reporting a failed proof. The
two call for different action: the first is fixed by one session under the
still-living identity, the second by a different passphrase. This is the one
place §5 departs from §4's no-reconnaissance rule, and it is safe to: it
discloses only that *something* not yet registered is held, which a claimant
proving nothing already learns from the challenge being empty.

### 5.4 `claim_request` (278)

An empty map, sent as the first payload frame of a claim session in place of
`retrieve_open`. A session that has sent `claim_request` MUST NOT send
`retrieve_open`, `retrieve_list` or `retrieve_read`: the claim completes and
the claimant re-dials.

### 5.5 `claim_challenge` (279)

| Key | Type | Value |
|-----|------|-------|
| 1 | array | `candidates`, ≤ 256 entries, each a map of `1` `repository_id` (bytes[16]), `2` `claim_token` (bytes[16]), `3` `nonce` (bytes[32]) |

One entry per replica the destination holds that (a) is **not** already
attributed to the dialling identity and (b) carries a registered
`claim_public`. Every `nonce` MUST be freshly generated for this frame and
MUST NOT be reused across sessions.

An **empty** array is the ordinary answer to a claimant with nothing to claim,
and MUST be sent rather than a refusal. It is not reconnaissance: it says only
that this identity has nothing unclaimed waiting, which is what the owner
inventory of §3.5 already tells an attributed peer about itself.

### 5.6 `claim_proof` (280)

| Key | Type | Value |
|-----|------|-------|
| 1 | array | `proofs`, ≤ 256 entries, each a map of `1` `repository_id` (bytes[16]), `2` `claim_public` (bytes[32]), `3` `signature` (bytes[64]) |

The claimant answers for the candidates it can. Omitting a candidate is not an
error; a claimant holding one repository's passphrase and not another's sends
one proof.

```text
signature = Ed25519-Sign(claim_seed,
                "fbp-peer-v1:replica-claim"
              ‖ repository_id[16]
              ‖ claim_token[16]
              ‖ nonce[32]
              ‖ SHA-256(context)[32]
              ‖ claimant_fingerprint[32])
```

`context` is the bound transcript of [02 §3.2](02-session.md#32-the-bound-transcript),
so a proof is inseparable from the connection and the two identities that
authenticated on it. `claimant_fingerprint` is the dialling peer's pinned
identity — the fingerprint the claim would move the attribution **to** — so a
proof cannot be replayed by a third party who observed it.

### 5.7 Validation

For each proof, the destination MUST verify **all** of:

1. `repository_id` names a candidate it issued in this session's
   `claim_challenge`;
2. `claim_public` is byte-for-byte the public key registered for that replica;
3. `signature` verifies under `claim_public` over the byte string of §5.6,
   rebuilt from the destination's own copy of every field.

Check 2 is the comparison the ceremony rests on, and check 3 is what stops the
comparison being a bearer test: `claim_public` is disclosed to whoever holds a
replica's bytes, so equality alone would let a thief of the ledger claim. Only
a holder of the passphrase can produce a signature under it.

A proof failing any check MUST be omitted from the result, and MUST NOT be
distinguished from one for a replica that is absent — §4's rule, unchanged. A
destination MUST NOT report *which* check failed.

### 5.8 `claim_result` (281)

| Key | Type | Value |
|-----|------|-------|
| 1 | array | `claimed`, each a map of `1` `repository_id` (bytes[16]), `2` `backup_set_ids` (array of bytes[16], ≤ 256) |

For every proof that validated, the destination re-points the attribution to
the claimant's identity, atomically with recording the claim, and answers with
the repository and the backup-set identifiers its snapshots carry.

The set ids are returned because they are the one piece of the claimant's lost
configuration that nothing else can supply: a recovering hub matches candidate
replicas on `backup_set_id`, and its own configuration died with the machine.
Disclosing them costs nothing — the peer receiving them has just proved it can
decrypt the whole repository.

A re-claim by the identity that already owns a replica is **idempotent**: it
validates, the attribution does not move, and the set ids are answered.

### 5.9 What a claim does and does not carry

A claim moves attribution and nothing else.

- The **quota denominator moves with it** ([05 §1](05-quotas.md#1-what-the-quota-bounds)):
  the bytes counted against the old identity are counted against the new one,
  because they are the same bytes and the same household.
- The **grant's terms do not change.** The claimant is bounded by the terms of
  the grant it holds now, not by those the lost identity held.
- **Reading is available immediately.** A destination MUST NOT require an
  operator action at its end before serving a claimed replica. A disaster is
  when the far household is least reachable, and a recovery that waits on a
  sleeping friend is a recovery that fails.
- **Deleting is not.** Retention instructions from the claiming identity are
  refused until the destination's operator acknowledges the claim
  ([06 §3](06-retention.md#3-what-the-spoke-validates)). Reading grants an
  attacker holding the passphrase nothing they did not already have; deleting
  a household's last copy is a different act, and it waits for the person who
  owns the disk.

A destination MUST raise a durable operator notice on every claim that moves
an attribution.

## 6 Flow and termination

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

## 7 Refusals

`feature_unsupported` — the format capability or the feature itself;
`terms_refused` — not this owner's replica (§4); `malformed` — a frame that
violates this document or arrives outside its state; every refusal is a
`session_refuse` written before the destination closes, per
[02 §8](02-session.md#8-errors-and-refusal).

## 8 Security considerations

Nothing here weakens the confidentiality story: ciphertext out is ciphertext
back. The destination gains read-pattern knowledge (§1) and nothing else; the
owner gains no way to read another peer's replicas (§4, enforced by
attribution, with no-reconnaissance refusals); and a stolen device that was
never paired gains nothing at all, because retrieval sits behind the session's
pinned-identity authentication like every other payload. Availability is the
honest limit: a destination can refuse or stall a retrieval exactly as it
could withhold data at any restore — which is what verification (04) and
multiple destinations exist to bound.

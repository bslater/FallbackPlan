# 03 — Cryptography

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §7.3, §7.6 · **Resolves:** [C2](../review/2026-08-architecture-review.md#c2--nonce-uniqueness-is-asserted-but-never-constructed), [C3](../review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard)

---

## 1. Rules

1. Use audited platform primitives or established libraries. Write no primitive ourselves.
2. Every repository object is protected by an **AEAD**. There is no plaintext mode, and none is offered as a compatibility switch.
3. Nonce uniqueness is guaranteed **structurally**, not probabilistically — §3.
4. Key separation is by explicit domain-separated derivation, never by convention.
5. Secrets never reach logs, telemetry, crash dumps, manifests, or configuration exports. Redaction is by *type*, so a new secret-bearing field is redacted by construction rather than by someone remembering to add a string pattern.
6. Displaying or exporting recovery material requires explicit user acknowledgement.

## 2. Key hierarchy

```text
User passphrase                          Hardware / OS key store
       |                                          |
       +--------> Argon2id (memory-hard) <--------+
                          |
                    Key Encryption Key (KEK)
                          |
                    wraps
                          |
                  Repository Master Key
                          |
        +-----------------+------------------+------------------+
        |                 |                  |                  |
  Content-ID key    Data key gen(s)   Metadata key gen(s)   Signing key(s)
        |                 |                  |                  |
        |            per-blob keys      per-blob keys      snapshot &
        |            (§3.1)             (§3.1)             journal records
        |
   object identifiers (§4)
```

Requirements:

- **AEAD suite**: AES-256-GCM where hardware AES is available, XChaCha20-Poly1305 otherwise. Both are permitted profiles; the profile is recorded per record. Unsupported or unsafe combinations are rejected at configuration time, not at write time.
- **Password KDF**: Argon2id with parameters recorded in `/repository-format` so a future reader can reproduce them.
- **Key generations** support cryptographic agility: new generations can be introduced without invalidating old records.
- **Wrapping-key rotation** (changing the passphrase) rewrites only `/keys/*` — no repository data is touched. This is the common operation and it must be cheap.
- **Data-key rotation** is a separate, explicitly invoked background rewrite. Conflating the two is a Kopia-derived lesson ([`00-overview.md` §5.4](00-overview.md#54-kopia)); users routinely believe changing a password re-encrypts their data, and the UI must say plainly that it does not.
- Unattended agents may protect the KEK with an OS key store.

## 3. Nonce and key construction

This is the one place in the system where a mistake is unrecoverable rather than merely expensive, so the construction is given in full rather than asserted.

### 3.1 The construction

```text
blob_salt   ← 256 bits from a CSPRNG, drawn once per blob,
              stored in the blob's cleartext envelope

blob_key    ← HKDF-Expand(
                  PRK  = data_key[generation],       (or metadata_key[generation])
                  info = "fbp/blob/v1" ‖ blob_salt,
                  L    = 32 bytes)

nonce(i)    ← 96-bit big-endian ordinal of record i within that blob (0, 1, 2, …)

AAD(i)      ← repository_id ‖ format_version ‖ object_type ‖ object_id ‖ i
```

Every blob has its own key. Nonce uniqueness therefore only has to hold **within a single blob**, where exactly one writer owns a strictly increasing record ordinal.

### 3.2 Why this removes the coordination problem

The original requirement (NFR-SEC-003) demanded uniqueness "by construction … across concurrent writers and resumed operations" and gave no construction. Both halves of that are genuinely hard, and both are eliminated here rather than solved:

**Concurrent writers.** Direct-store mode permits many writers with no coordination channel. Under a shared key, a counter would need partitioning and random 96-bit nonces would need a birthday-bound budget nobody tracks. Under per-blob keys, two writers cannot collide *because they hold different keys* — the blob salt is drawn independently and 256 bits of CSPRNG output makes collision negligible. No coordination is required because there is nothing to coordinate.

**Resumed operations.** This is the more likely failure in practice, and the original design made it a *designed-for* path: FR-ARCH-011 explicitly resumes interrupted blob construction from a spool checkpoint. Any nonce sequence derived from something that resets — a session counter, a timestamp, a per-job counter — re-emits a nonce under the same key on replay. Under AES-GCM that leaks the XOR of two plaintexts and, far worse, allows recovery of the GHASH authentication subkey, after which an attacker can forge arbitrary authenticated records.

### 3.3 Why resumption is safe

A resumed spool replays the same `(blob_salt, ordinal)` pairs under the same derived key. Encrypting the same plaintext at the same ordinal under the same key yields **byte-identical output**. Replay is therefore idempotent, not catastrophic: the resumed blob is bit-for-bit what the interrupted one would have been.

A *restarted* blob is a different matter and is handled by construction too — it draws a fresh `blob_salt`, so it is a different key, and no nonce is reused even though the ordinals begin again at zero.

The distinction that makes this work is that the salt lives in the durable spool checkpoint. Resume reads it; restart draws a new one. Everything else follows.

### 3.4 Associated data

Binding `repository_id ‖ format_version ‖ object_type ‖ object_id ‖ ordinal` as AAD means a record cannot be relocated to a different blob position, a different object type, a different repository, or replayed under a different format version without authentication failing. This is what defends against the substitution and splicing attacks in [`../threat-model.md`](../threat-model.md#t-3-object-substitution-and-splicing).

### 3.5 Test obligations

The construction is only as good as its enforcement, so these are requirements on the test suite, not aspirations:

- property test: no `(key, nonce)` pair repeats across any generated write sequence;
- interruption test: resume-after-kill at every record boundary produces byte-identical blobs;
- interruption test: restart-after-kill produces a *different* blob salt in every case;
- concurrency test: *N* writers against one repository produce pairwise-distinct blob salts;
- negative test: a record moved between blobs, ordinals, or repositories fails authentication.

## 4. Object identifiers

Two identifiers, with different exposure:

| Identifier | Derivation | Exposure |
|------------|-----------|----------|
| **Content identifier** | Cryptographic hash of canonical plaintext | Inside the trust boundary only. Never written to a store. |
| **Object identifier** | Keyed function of the content identifier and object type, under the repository's content-ID key | Written to stores, indexes, footers, manifests |

The keying is what stops a storage provider from testing whether a repository contains a known file by hashing that file and looking for its raw digest. Without it, any provider could enumerate which of a list of known documents a user holds.

The hash function is profile-selected; the recommendation and its portability reasoning are in [ADR-0004](../adr/0004-segment-hash-function.md).

## 5. Deduplication trust domains

### 5.1 The problem

Keyed object identifiers defend against the *storage provider*. They do nothing against a repository *member*, because members hold the key.

All writers in a repository share the content-ID key, so device B can see that a segment with content identifier `H` already exists and reference it instead of uploading its own copy. B cannot check that claim without downloading and decrypting the segment — which is exactly the work deduplication exists to avoid. A device A that is compromised, or merely shipping a bug in its hashing path, can publish a record labelled `H` whose contents are something else. Every device that subsequently deduplicates against it silently backs up corrupt data.

Restore-time verification (FR-RST-002) catches this at the moment the user needs the file and the source is gone. For a backup product that is barely better than not catching it, and in the meantime the status display reports healthy, because nothing verifies a segment the device believes it already has.

### 5.2 The domains

| Domain | Behaviour | Cost | Default |
|--------|-----------|------|---------|
| `device` | Reuse only segments this device wrote. | Storage duplication across devices; **none** for a single-device repository | ✅ |
| `repository` | Reuse any member's segments, after **verify-on-reuse**: fetch, decrypt, confirm the content identifier before referencing. | One read per first reuse; still avoids re-upload and re-storage | Opt-in |
| `repository-unverified` | Reuse without verification. | None | Opt-in, requires explicit acknowledgement |

`device` is the default because it costs nothing at all in the overwhelmingly common single-device case, and because the failure it prevents is silent and permanent. `repository` keeps most of the bandwidth saving while restoring the integrity guarantee, and is the right setting for a household backing up four laptops that share an OS and a music library.

`repository-unverified` exists because there are legitimate deployments — a single administrator, uniform managed devices — where every writer really is equally trusted. It is never a default and never silent.

### 5.3 The residual leak

In any domain other than `device`, a member can determine whether another member has backed up a *known* file by observing whether deduplication hits. This is inherent to cross-device deduplication rather than a flaw in this scheme; `device` mode is the answer for anyone to whom it matters. Recorded in [`../threat-model.md`](../threat-model.md#t-12-dedup-confirmation-by-a-repository-member).

## 6. Authentication of repository state

- Snapshot manifests and journal records are **signed** by the writing device.
- Index deltas and checkpoints are authenticated, and carry writer identity and sequence (see [`02-repository-format.md` §7.2](02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing)).
- Anti-rollback: the catalogue retains the highest generation and per-writer sequence it has observed, anchored in durable local state. A store presenting an older view is detected rather than accepted. Optional external witnesses are a later enhancement.
- Conflicting sequence use, identity cloning, and rollback raise a **security alert**, not a warning buried in a log.

## 7. What this does not protect

Stated here so it is never implied elsewhere:

- a compromised source reads plaintext before encryption — no backup system can prevent this;
- ransomware holding source credentials *and* unlocked keys can act with the user's authority (mitigations, not solutions, in [`07-retention-and-gc.md` §5](07-retention-and-gc.md#5-destructive-change-safeguards));
- loss of all recovery material makes the repository permanently unreadable — by design, and the reason the recovery-kit workflow is mandatory;
- stored record lengths leak compressed sizes ([`../threat-model.md`](../threat-model.md#t-11-metadata-side-channels)).

---

**Previous:** [02 — Repository format](02-repository-format.md) · **Next:** [04 — Concurrency and publication](04-concurrency-and-publication.md)

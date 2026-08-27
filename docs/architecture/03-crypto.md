# 03 — Cryptography

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §7.3, §7.6 · **Resolves:** [C2](../review/2026-08-architecture-review.md#c2--nonce-uniqueness-is-asserted-but-never-constructed), [C3](../review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard)

**Built:** Yes — the key hierarchy, per-blob derivation and object identifiers all exist — see [implementation status](../implementation-status.md).

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
        +-----------------+------------------+------------------+------------------+
        |                 |                  |                  |                  |
  Content-ID key    Data key gen(s)   Metadata key gen(s)   Signing key gen(s)  Key-ID key
        |                 |                  |                  |                  |
        |            per-blob keys      per-blob keys      snapshot &         store blob
        |            (§3.1)             (§3.1)             journal records    keys (02 §4.3)
        |
   object identifiers (§4)
```

Five derived keys, not four — the key-ID key is easy to forget and is what every blob's store key is derived from, so a hierarchy that omits it describes a repository whose objects cannot be named.

Requirements:

- **AEAD suite**: AES-256-GCM where hardware AES is available, XChaCha20-Poly1305 otherwise. Both are permitted profiles; the profile is recorded per record. Unsupported or unsafe combinations are rejected at configuration time, not at write time.
- **Password KDF**: Argon2id with parameters recorded in `/repository-format` so a future reader can reproduce them.
- **Key generations** support cryptographic agility: new generations can be introduced without invalidating old records.
- **Wrapping-key rotation** (changing the passphrase) rewrites only `/keys/*` — no repository data is touched. This is the common operation and it must be cheap.
- **Data-key rotation** is a separate, explicitly invoked background rewrite. Conflating the two is a lesson from the prior art ([`00-overview.md` §5.4](00-overview.md#54-layered-repositories-over-a-minimal-blob-store)); users routinely believe changing a password re-encrypts their data, and the UI must say plainly that it does not.
- Unattended agents may protect the KEK with an OS key store.

### 2.1 The write-only hierarchy (format v2)

A write-only repository ([ADR-0042](../adr/0042-write-only-repositories.md);
format spec [03 §9](../../specifications/repository-format/03-keys.md)) has
no master key, no KEK, no wrap step and no `/keys/` object. Everything
derives from the passphrase:

```text
User passphrase  +  KDF salt & parameters (recorded in /repository-format)
       |
  Argon2id  →  root (never stored, never wrapped)
       |
       +── HKDF "fbp/seal/v2"        → X25519 scalar → sealing PUBLIC key (in the descriptor)
       +── HKDF "fbp/metadata/v2"    ─┐
       +── HKDF "fbp/signing/v2"      ├─ the WRITE BUNDLE: what the service
       +── HKDF "fbp/content-id/v2"   │  holds — browse, plan, dedup, trim,
       +── HKDF "fbp/key-id/v2"      ─┘  replicate, verify structure, and write
```

Data blobs seal their records under a fresh random per-blob content key,
wrapped to the sealing public key; their footers — the record tables, the
structure plane — derive from the **metadata** class key, so the write
bundle still reads every blob's own structure. The private scalar exists
only while a passphrase entry is alive: setup, adoption of a moved
archive, or a restore grant inside a source handle. Each HKDF domain is
independently one-way (NFR-SEC-010): the whole write bundle in hand yields
neither the root, the passphrase, the scalar, nor any sibling key. The
descriptor's public-key copy is the passphrase verifier — derive and
compare, no decryption — and rule 6 above is load-bearing at setup: a v2
passphrase can never change, and losing it loses the backup.

## 3. Nonce and key construction

This is the one place in the system where a mistake is unrecoverable rather than merely expensive, so the construction is given in full rather than asserted.

### 3.1 The construction

```text
blob_salt   ← 256 bits from a CSPRNG, drawn once per blob,
              stored in the blob's cleartext envelope

blob_key    ← HKDF-Expand(
                  PRK  = data_key[generation],       (or metadata_key[generation])
                  info = "fbp/blob/v1" ‖ blob_salt ‖ writer_id ‖ u64(blob_counter),
                  L    = 32 bytes)

nonce(i)    ← 96-bit big-endian ordinal of record i within that blob (0, 1, 2, …)

AAD(i)      ← repository_id ‖ u16(format_version) ‖ u8(object_type) ‖ object_id ‖ u32(i)
```

Integer widths are part of the construction — "given in full" means an implementer can reproduce it byte-for-byte, and an unwidthed `blob_counter` is not reproducible. All integers big-endian ([spec 00 §1](../../specifications/repository-format/00-conventions.md#1-notation)); the AAD is exactly 55 bytes.

Every blob has its own key. Nonce uniqueness therefore only has to hold **within a single blob**, where exactly one writer owns a strictly increasing record ordinal.

`writer_id` and `blob_counter` are bound into the derivation alongside the random salt so that key separation does not rest on CSPRNG quality alone. A cloned VM or an early-boot embedded device can replay RNG state and draw the same salt twice; binding writer identity and a monotonic per-writer blob counter means collision would additionally require the same writer and the same counter value. The counter comes from the writer's journal sequence, which is gapless, monotonic, and protected against cloning by the identity-conflict alert in [`../threat-model.md` T-18](../threat-model.md#t-18-writer-identity-cloning). This costs nothing and removes a dependency on an assumption we cannot enforce on the user's hardware.

### 3.2 Why this removes the coordination problem

The original requirement (NFR-SEC-003) demanded uniqueness "by construction … across concurrent writers and resumed operations" and gave no construction. Both halves of that are genuinely hard, and both are eliminated here rather than solved:

**Concurrent writers.** Direct-store mode permits many writers with no coordination channel. Under a shared key, a counter would need partitioning and random 96-bit nonces would need a birthday-bound budget nobody tracks. Under per-blob keys, two writers cannot collide *because they hold different keys* — the blob salt is drawn independently and 256 bits of CSPRNG output makes collision negligible. No coordination is required because there is nothing to coordinate.

**Resumed operations.** This is the more likely failure in practice, and the original design made it a *designed-for* path: FR-ARCH-011 explicitly resumes interrupted blob construction from a spool checkpoint. Any nonce sequence derived from something that resets — a session counter, a timestamp, a per-job counter — re-emits a nonce under the same key on replay. Under AES-GCM that leaks the XOR of two plaintexts and, far worse, allows recovery of the GHASH authentication subkey, after which an attacker can forge arbitrary authenticated records.

### 3.3 Why resumption is safe

A resumed spool re-emits **the sealed record bytes it already produced**, read from the durable spool checkpoint. It does not recompute them. The resumed blob is therefore bit-for-bit what the interrupted one would have been, and replay is idempotent rather than catastrophic.

A *restarted* blob is a different matter and is handled by construction too — it draws a fresh `blob_salt`, so it is a different key, and no nonce is reused even though the ordinals begin again at zero.

#### Why the checkpoint stores bytes, not offsets

This is the detail the whole construction depends on, and getting it wrong reintroduces exactly the failure §3 exists to prevent.

The obvious design is to checkpoint a plaintext offset and recompute from there — "the same plaintext at the same ordinal under the same key yields the same output". That reasoning is wrong, because the input to the AEAD is not the segment's plaintext. It is the segment's plaintext **after compression** (§[`02-repository-format.md` §4](02-repository-format.md#4-compression)), and recompression is not guaranteed to be reproducible. Zstandard is deterministic for a given library version and parameter set; it offers no guarantee across versions, and compressors change their output when internal heuristics are tuned.

So an agent that crashes, is upgraded, and resumes would recompress the same segment into *different bytes* and encrypt those under the *same* `(blob_key, ordinal)`. Two different plaintexts under one key and nonce leaks their XOR and — far worse — permits recovery of the GHASH authentication subkey, after which an attacker can forge arbitrary authenticated records in that blob. No attacker and no unusual configuration is required: a crash, an unattended update, and a resume.

Storing the sealed bytes makes byte-identity a property of the checkpoint rather than an assumption about a third-party codec. See [PT-1](../review/2026-08-fix-pressure-test.md#pt-1--c2s-resume-guarantee-silently-assumes-bit-reproducible-compression).

#### Everything that varies must be pinned

The same reasoning applies to any input that could differ between crash and resume. The spool checkpoint records the blob salt, the writer ID and blob counter, the segmentation profile and its parameters, the compression codec **and its exact version**, and the encryption profile. On resume, any mismatch between the checkpoint and the running agent forces a **restart** — which draws a fresh salt and is safe — rather than a resume.

Restart is the safe failure. The engine always prefers it when there is any doubt.

### 3.4 Associated data

Binding `repository_id ‖ format_version ‖ object_type ‖ object_id ‖ ordinal` as AAD means a record cannot be relocated to a different blob position, a different object type, a different repository, or replayed under a different format version without authentication failing. This is what defends against the substitution and splicing attacks in [`../threat-model.md`](../threat-model.md#t-3-object-substitution-and-splicing).

### 3.5 Test obligations

The construction is only as good as its enforcement, so these are requirements on the test suite, not aspirations:

- property test: no `(key, nonce)` pair repeats across any generated write sequence;
- interruption test: resume-after-kill at every record boundary produces byte-identical blobs;
- interruption test: **resume with a changed compression codec version re-emits the checkpointed bytes, or refuses to resume** — it never recompresses under an already-used ordinal;
- interruption test: restart-after-kill produces a *different* blob salt in every case;
- concurrency test: *N* writers against one repository produce pairwise-distinct blob salts;
- concurrency test: two writers seeded with an *identical* CSPRNG stream still derive distinct blob keys, via `writer_id` and `blob_counter`;
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
| `device` | Reuse only segments this device wrote. | Storage duplication across devices; **none** for a single-device repository | Opt-in (hardened) |
| `repository` | Reuse any member's segments, after **verify-on-reuse**: fetch, decrypt, confirm the content identifier before referencing. | One read per first reuse; still avoids re-upload and re-storage | ✅ |
| `repository-unverified` | Reuse without verification. | None | Opt-in, requires explicit acknowledgement |

`repository` is the default. In a single-writer repository it **degenerates exactly to `device` behaviour at zero cost**, because there are no other writers' segments to verify — it is free precisely where `device` is free. Where the two differ is the multi-device household backing up four laptops that share an operating system and a music library, and there `device` stores four copies of everything they have in common. That is the classic consumer use case and this project's stated reason for existing, so the default should serve it.

An earlier draft made `device` the default on the grounds that it "costs nothing in the single-device case". True — and it does not distinguish the two options, because `repository` costs nothing there either. The argument selected between them on a criterion where they are identical ([PT-11](../review/2026-08-fix-pressure-test.md#pt-11--the-stated-rationale-for-the-device-dedup-default-does-not-distinguish-it-from-repository)).

`device` remains available as the hardened setting, and is the right choice for anyone who wants to close the confirmation side channel in §5.3 entirely, or who does not want their device reading another member's data even to verify it.

`repository-unverified` exists because there are legitimate deployments — a single administrator, uniform managed devices — where every writer really is equally trusted. It is never a default and never silent.

Two set shapes override the default to `device`, for reasons that are not
mistrust: a **write-only** set ([ADR-0042](../adr/0042-write-only-repositories.md))
cannot read content back to verify a reuse, and a **direct-ship** set
([ADR-0046](../adr/0046-direct-to-destination-publication.md) §6) could only
verify by pulling ranges from a destination — a round trip per reuse to
re-check bytes the catalogue already vouches for. Both run on catalogue-decided
reuse guarded by the stale-catalogue check that survives in either shape: a
**presence probe** against wherever the blob actually lives (the destinations,
for direct-ship). Since a per-set repository is single-writer, this is the
degenerate-to-`device`-at-zero-cost cell of the table, made explicit.

#### Both domains need state that outlives the catalogue

`device` must know which segments *this device* wrote; `repository` must remember which shared segments it has already verified, or it pays the cost repeatedly. Both are catalogue state, and the catalogue is disposable ([ADR-0010](../adr/0010-local-store-separation.md)).

- **Writer attribution is recoverable.** Index deltas carry `writer_id`, so a rebuild reconstructs which segments this device authored. The dedup lookup key includes writer attribution for exactly this reason (FR-MAN-006).
- **Verification outcomes are catalogue state, and a rebuild re-imposes the read once.** [PT-12](../review/2026-08-fix-pressure-test.md#pt-12--device-attribution-and-verify-on-reuse-state-live-only-in-a-disposable-cache) named the cost and offered a durable repository object as one answer; the [freeze-gate decision](../freeze-gate-decisions-2026-08.md#decision-1--verification-outcomes-live-in-the-catalogue-not-the-repository) took the other: the cost only exists in a multi-writer repository, none exists yet, and adding an optional object later is a minor-version change. Deleting the catalogue therefore re-imposes the verification read once — a bounded, user-initiated cost, accepted rather than optimised away with pre-freeze format surface.

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

One property to note for the external cryptographic review: **AES-GCM is not key-committing**. A ciphertext can be constructed that authenticates under two different keys. Exploitability here is low, because keys derive from the repository master key and an attacker without it cannot choose them — but `repository-unverified` deduplication accepts records from other writers without checking them, which is the closest this design comes to an adversary influencing what gets decrypted under a key the victim holds. The AAD binding in §3.4 should be assessed against this. Not a v1 blocker ([PT-15](../review/2026-08-fix-pressure-test.md#pt-15--aes-gcm-is-not-key-committing)).

---

**Previous:** [02 — Repository format](02-repository-format.md) · **Next:** [04 — Concurrency and publication](04-concurrency-and-publication.md)

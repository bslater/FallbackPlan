# ADR-0022 — Standalone metadata records, index identifiers, and other phase-0 gap resolutions

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-MAN-008, FR-SNP-001, NFR-SEC-003, NFR-COMP-004
**Related:** [ADR-0005](0005-aead-suite-and-nonce-construction.md), [ADR-0008](0008-index-generations-and-checkpoints.md), [ADR-0016](0016-blob-identifier-formation.md), [ADR-0020](0020-ed25519-signing-key-semantics.md), [ADR-0023](0023-cdc-v1-rabin-parameters.md)

---

## Context

Implementing specifications [06](../../specifications/repository-format/06-manifests.md) (manifests), [07](../../specifications/repository-format/07-index.md) (index), and [08](../../specifications/repository-format/08-journal.md) (journal) surfaced a set of places where the normative text is silent or contradicts itself. The phase-0 execution plan's standing rule is that such places are resolved by a **flagged design decision** — written down, cited from the code, and marked in the specification as an erratum — never by a silent choice inside an implementation.

This ADR records every such resolution except the `cdc-v1` parameter pin, which is large enough to carry its own record ([ADR-0023](0023-cdc-v1-rabin-parameters.md)). Each section below names the gap, the resolution, and the reasoning. The specification files carry short erratum notes pointing here; the text of the specification is otherwise unchanged, so the resolutions remain visibly *decisions of this implementation generation* until the format-freeze pass folds them into normative text.

## Decision 1 — Standalone metadata records: the `FBPKSREC` framing

**The gap.** Index deltas (07 §2), checkpoints (07 §5), journal records (08 §2), and the standalone snapshot object (06 §6) are all "metadata records … stored as standalone objects rather than inside blobs". But record encryption is defined only *inside* a blob: the blob key derives from `blob_salt`, `writer_id`, and `blob_counter`, all carried by the blob's cleartext envelope (05 §2, 03 §5), and the nonce and AAD both consume the record's `ordinal`, defined as its position in the blob (04 §2.1–§4). A standalone object has no envelope and no position, so none of those inputs has a home. As written, the four object families above cannot be encrypted at all.

**The resolution.** A standalone metadata record is framed with a fixed 72-byte cleartext prefix carrying exactly the selectors the blob envelope would have carried, followed by a verbatim [04 §2](../../specifications/repository-format/04-record.md#2-framing) record header with `ordinal = 0`, the ciphertext, and the 16-byte tag:

```text
offset  size  field
------  ----  -----------------------------------------------
     0     8  magic          = "FBPKSREC" (0x46 42 50 4B 53 52 45 43)
     8     2  format_version u16
    10     2  reserved       u16, MUST be zero
    12     4  key_generation u32
    16    32  blob_salt
    48    16  writer_id
    64     8  counter        u64 — drawn from the writer's shared sequence space (08 §2)
    72    54  record header  exactly 04 §2, with ordinal = 0
   126     N  ciphertext
 126+N    16  tag
```

Key derivation, nonce, and AAD are **reused byte-for-byte** from the blob path: `record_key = HKDF-Expand(metadata_key[key_generation], "fbp/blob/v1" ‖ blob_salt ‖ writer_id ‖ u64(counter), 32)` (03 §5), nonce = the 04 §3 construction at ordinal 0, AAD = the 55-byte 04 §4 form at ordinal 0. Nothing new is invented: a standalone record is cryptographically a one-record blob without the blob. Nonce uniqueness holds by the same argument as ADR-0005 — the `(blob_salt, writer_id, counter)` triple never repeats for a writer that draws `counter` from its gapless sequence space, and `blob_salt` is fresh CSPRNG output per sealed object, so even a counter-reuse bug degrades to the 03 §5.2 salt-separation defence.

**Why not a distinct AAD or a distinct info string.** A separate derivation domain would be *more* separated but would require a second derivation path in every implementation and a second set of conformance vectors, and would put standalone records outside the review that the blob path has already had. Reuse keeps one auditable construction. The domains cannot collide: a blob's first record and a standalone record with the same `(salt, writer, counter)` cannot both exist, because the counter space is shared and each consumed counter produces either a blob **or** a standalone record, never both.

**New object types.** The 04 §2 header requires an `object_type`, and 02 §3.1 assigns `0x01`–`0x06` with `0x07` reserved as the store-blob-key domain separator. Standalone record bodies are assigned:

| Value | Object type |
|-------|-------------|
| `0x08` | Index delta (07 §2) |
| `0x09` | Index checkpoint (07 §5) |
| `0x0A` | Journal record (08 §2) |

`0x07` remains reserved and MUST NOT be assigned. The standalone snapshot object (06 §6) keeps object type `0x04` — it is the same manifest bytes as the in-blob record, re-sealed under its own framing, and its object identifier is unchanged.

## Decision 2 — Delta and checkpoint identifiers

**The gap.** `<delta-id>` and `<checkpoint-id>` appear as path placeholders (01 §2, 07 §2/§5) and as CBOR reference fields (`predecessor_delta_id` bytes[16], `subsumed_delta_ids`, `predecessor_checkpoint_id`), but no document defines how one is allocated or rendered. `docs/architecture/02-repository-format.md` §2 spells the same path segment `<index-id>` — a third name for the same undefined thing.

**The resolution.** A delta or checkpoint identifier is **16 bytes drawn from a CSPRNG at publication**, rendered in the store path as 26 lowercase base32 characters (00 §6). This satisfies 01 §2.1's opacity rule with nothing to prove: random bytes reveal nothing.

It is deliberately **not** content-derived. A retried publication re-seals the object under a fresh `blob_salt`, so the stored bytes differ between attempts; a content-derived identifier would then put *different* bytes at the *same* key across a retry, violating 01 §4's immutability rule ("a writer MUST NOT overwrite an existing key with different content"). With a random identifier, the idempotent-retry rule is: **retry re-puts the identical sealed buffer** under the identifier already allocated — the writer holds both until the put is acknowledged. The identifier is allocated once per logical publication, not once per attempt.

The delta's own identifier does not appear in its CBOR body — the body is signed, and the identifier is derivable from the store key; carrying it inside would add a field the signature must cover and a new cross-check that can only ever fail on a copy-paste bug. Readers take the identifier from the path.

The architecture document's `<index-id>` spelling is an erratum for `<delta-id>`.

## Decision 3 — Key-object discovery

**The gap.** Discovery step 3 (01 §6) says "Fetch and unwrap `/keys/<key-id>`", but nothing tells a reader the key-id: the descriptor body (01 §3.2) has no key-id field, and 01 §1 explicitly refuses to assume listing consistency.

**The resolution, for phase 0.** A reader **lists `/keys/`** and attempts to unwrap each object found; in a phase-0 repository there is exactly one. Creation order closes most of the listing-consistency window: `CreateAsync` writes `/keys/<key-id>` **before** `/repository-format`, so a store that shows the descriptor has already acknowledged the key object. On a store whose listing still lags, an empty `/keys/` listing under a present descriptor is a **transient open failure** — retry — not a damage finding: 01 §5's missing-object rule ("MUST report it as a damage finding") applies to references from live objects, and a listing is not a reference.

The durable fix — a key-id field in the descriptor body — is a format change and is recorded as a deferred proposal in [open questions Q16 territory](../open-questions.md); it costs a descriptor field and removes the listing dependency entirely. Phase 0 does not add fields to a specified structure.

## Decision 4 — Multi-shard deltas and the scalar `shard` key

**The gap.** Delta key 5 `shard` is a scalar u16 (07 §2), yet 07 §8 permits "a single delta spanning multiple shards for a small job". No encoding is given for the multi-shard case, and since object identifiers are HMAC outputs, essentially **every** real delta spans many shards — the scalar is the corner case, not the rule.

**The resolution.** Key 5 is **optional**. Present, it asserts the delta's entries all fall in that one shard (a reader MAY verify and MUST treat a mismatch as a damage finding). Absent, the delta's shard coverage is exactly the set `{ top 16 bits of entry.object_id }` implied by its entries. The phase-0 writer always omits it. Checkpoints are unaffected: `shard_set` (07 §5 key 4) already enumerates coverage explicitly and is computed from the checkpoint's entries.

## Decision 5 — "Current generation"

**The gap.** Intent expiry condition 1 ("the repository's current generation exceeds `expiry_generation`", 08 §7) and generation precedence (07 §3) both consume a repository-wide "current generation" that no document defines — the key bundle carries `current_data_generation` and `current_metadata_generation` (03 §3.1) but nothing says which, or what role store state plays.

**The resolution.** The repository's current generation is:

```text
current_generation = max( key_bundle.current_data_generation,
                          key_bundle.current_metadata_generation,
                          highest generation directory observed under /index/checkpoint/ and /index/delta/ )
```

The bundle terms make the definition available before any index object exists; the observed term makes it advance with publication activity, which is what 08 §7's rationale ("generations advance when *others* publish") requires. Because listing may lag, the observed term is a **lower bound** on true activity — which errs in the safe direction for expiry: an intent is expired *later*, never earlier, than a perfectly consistent view would allow. The definition lives behind a single seam (`IGenerationOracle`) so a future format-level definition replaces one implementation.

## Decision 6 — Inner shapes left unspecified

The following maps and enums have no key assignments in the specification. Phase 0 pins the minimal shapes below; each is an erratum candidate for the freeze pass.

**Policy manifest key 2 `segmentation_parameters`** (06 §7) — uint-keyed map:

| Key | Type | Value |
|-----|------|-------|
| 1 | u64 | `segment_size` (fixed-v1) or `target_size` (cdc-v1) |
| 2 | u64 | `min_size` — cdc-v1 only |
| 3 | u64 | `max_size` — cdc-v1 only |
| 4 | u8 | `window_size` — cdc-v1 only, 64 in v1 |

`fixed-v1` emits key 1 only.

**Policy manifest key 6 `blob_write_profile`** (06 §7) — uint-keyed map mirroring the engine's `BlobWriteProfile`:

| Key | Type | Value |
|-----|------|-------|
| 1 | u64 | `target_size` |
| 2 | u64 | `max_size` |
| 3 | u32 | `max_record_count` |

**Snapshot manifest key 12 `source_filesystem`** (06 §6) — uint-keyed map:

| Key | Type | Value |
|-----|------|-------|
| 1 | bool | `case_sensitive` |
| 2 | bool | `supports_sparse` |
| 3 | text | `name` — e.g. `"ntfs"`, `"ext4"`; informational |

**Audit record payload** (08 §6): key 1 `action` enumerates `1` retention-reduction, `2` bulk-snapshot-deletion, `3` gc-pass, `4` force-expiry; key 3 `parameters` is a uint-keyed map whose shape is per-action and which phase 0 emits empty.

**Checkpoint key 5 `shard_hashes`** (07 §5): `shard_hashes[i]` is **SHA-256 over the deterministic CBOR encoding of the array of shard `shard_set[i]`'s post-precedence entries, each in §2.1 six-element form, sorted by `object_id` bytes ascending**. Deterministic and reader-recomputable from the checkpoint's own `entries`, which is what makes it usable as a cross-replica comparison.

**Out of phase-0 scope entirely:** the lease record format (08 §9), `/tombstones/…`, and `/audit/<period>/…` object formats. Recorded in [open questions Q17](../open-questions.md#q17--lease-tombstone-and-audit-period-object-formats).

## Decision 7 — Sequence accounting across the shared space

08 §2 makes journal records, index deltas, **and blob counters** (02 §4) draw from one gapless per-writer sequence space, but only deltas and journal records produce an object at `/journal/<writer>/<seq>` or `/index/delta/<gen>/<id>` — a sequence number consumed by a blob counter publishes nothing at a sequence-addressed key. Gap detection (07 §4) therefore needs a definition of "accounted for". Sequence *n* is accounted for when any of the following exists:

1. an index delta with `sequence = n`;
2. a journal record with `sequence = n`;
3. a void delta with `sequence = n`; or
4. a blob whose structured identifier embeds counter *n* (`blob_id = writer_id[0..8] ‖ u64(n)`, ADR-0016) **named by any intent's** `intended_blob_ids` or `additional_blob_ids`.

A number satisfying none of these after the reader's configured bounded number of generations is a damage finding, per 07 §4. The writer's own recovery obligation is unchanged: a number it knows it skipped gets a void delta.

## Decision 8 — Ed25519 conformance vectors, and the Bodu verifier's strictness

The conformance suite's Ed25519 gap closes with a **pure-Python RFC 8032 implementation inside `generate.py`** (standard-library `hashlib.sha512` and integer arithmetic), self-checked against the RFC 8032 §7.1 published test vectors in the builder's inline asserts — the same pattern as the generator's RFC 5869 HKDF self-test. `ed25519.json` then carries both the §7.1 known-answer cases and format-real cases (seed = `HKDF-Expand(master_key, "fbp/signing/v1" ‖ u32(g), 32)` signing pinned canonical-CBOR snapshot bytes), all computed, so the file is honestly `independently_derived: true` under the suite's own definition.

One engine-side property is recorded here so it is not rediscovered as a surprise: the Ed25519 implementation the engine uses (`Bodu.Security.Cryptography`, [ADR-0021](0021-consume-bodu-via-committed-package-feed.md)) applies **strict cofactorless verification** and rejects small-order points and non-canonical encodings. Every signature this engine produces verifies under it, and all RFC 8032 §7.1 vectors pass; the strictness only narrows the set of accepted third-party signatures relative to a lenient cofactored verifier, which for a security boundary is the desirable direction. Cross-implementation signers must produce canonical RFC 8032 signatures — which the specification already requires.

## Consequences

**Positive**

- Specifications 06, 07, and 08 become implementable end to end; every resolution is cited from code as `ADR-0022` rather than existing only in an implementer's head.
- The standalone framing adds no new cryptographic construction — one derivation path, one AAD shape, one set of vectors covers blob records and standalone records alike.
- Random index identifiers make retry semantics exact and keep the namespace opaque with no analysis needed.

**Negative**

- 72 bytes of framing overhead per standalone object, including per-object salt. Irrelevant at index-object sizes.
- The `/keys/` listing dependency survives in phase 0; the descriptor-field fix waits for a format change window.
- The specification and this ADR must be reconciled at the freeze gate — every erratum note is a pending edit, and drift between the two would be worse than either alone. The freeze checklist gains that reconciliation as an explicit item.

## Alternatives considered

**Wrap standalone objects in a one-record blob (envelope + record + footer + locator).** Rejected: ~200 bytes of framing whose fields (record table, locator, digest prefix) are meaningless at one record, and it would make `/index/…` and `/journal/…` objects enumerate as blobs to any scanner keyed on the envelope magic.

**A dedicated HKDF info string (`"fbp/standalone/v1"`) for standalone record keys.** Rejected as needless divergence: the reused path is already domain-separated by the shared counter space, and a second derivation domain doubles the vector surface for no attack-surface reduction.

**Content-derived delta identifiers (hash of the sealed bytes).** Rejected: breaks idempotent retry under per-attempt salts, as described in Decision 2.

**Deterministic per-writer delta identifiers (`writer_id ‖ sequence`).** Rejected: leaks writer identity into the store namespace, which 01 §2.1 forbids and ADR-0016/02 §4.3 exist to prevent.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Phase-0 resolutions for the specification gaps blocking 06/07/08 implementation |

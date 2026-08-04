# ADR-0025 — Compaction re-seals records; the ordinal stays in the AAD

**Status:** Accepted
**Date:** 2026-08
**Requirements:** NFR-REL-004, NFR-COMP-004
**Related:** [ADR-0005](0005-aead-suite-and-nonce-construction.md), [ADR-0007](0007-logical-object-identifiers-in-manifests.md), [ADR-0017](0017-index-entry-supersession.md), [ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md), [specification 04 §4](../../specifications/repository-format/04-record.md#4-associated-data)

---

## Context

Open question Q15 recorded a live contradiction in specification 04. The
record AAD includes `ordinal`, and 04 §2.1 requires the ordinal to equal
the record's zero-based position in its blob — yet 04 §4 claimed a record
is "intentionally relocatable between blobs by compaction, which
republishes its index entry without re-encrypting it". A byte-identical
relocation generally lands at a different position, so it cannot keep its
authenticated ordinal and satisfy the position rule at once.

Examined closely, the relocation claim was never coherent even without the
ordinal: the record's key derives from the **source blob's** salt, writer,
and counter (03 §5), none of which travel to a destination blob. Moving
ciphertext bytes between blobs "without re-encrypting" would require the
destination to carry a foreign key context per record — machinery the
format does not have and nothing else needs. The only relocation the
shipped bytes ever supported is copying an entire blob unchanged, which is
not compaction.

The architecture documents never depended on the claim. Their compaction
promise is narrower and survives untouched: compaction republishes index
entries as supersessions and **touches no manifest** (architecture
02 §6.2, 07-retention-and-gc §3 step 7; phase-0 exit criterion 9). That
property comes from manifests referencing segments by logical object
identifier (ADR-0007), not from any byte-preservation trick.

## Decision

### 1 The AAD is unchanged

The associated data remains exactly as shipped and as frozen in
`records.json` and `fixture-repository-v1`:

```text
AAD = repository_id ‖ u16(format_version) ‖ u8(object_type) ‖ object_id ‖ u32(ordinal)
```

The ordinal stays in. 04 §2.1's position rule is now unconditionally true:
every record's ordinal equals its position in the blob that carries it, in
every blob, including compacted ones.

### 2 Compaction re-seals

A compaction pass moves a live record by **decrypting it and re-sealing
it** into the destination blob: the destination's own key (fresh salt,
fresh counter, the compactor's writer identity), the record's new ordinal,
a freshly computed AAD. The object identifier — the record's *logical*
name — is unchanged, so every manifest reference continues to resolve. The
compactor then republishes the object identifier's index entry at its new
location as a supersession (07 §3; ADR-0017), and retires the source blob
under the deletion discipline. No manifest, tree, or snapshot is touched.

### 3 The blob identifier stays out of the AAD

04 §4's stated rationale for excluding the blob id — enabling zero-decrypt
relocation — is void, but the exclusion stands on a better one: it is
**redundant**. The record key already binds the blob's salt, writer, and
counter; a record cannot be opened under any other blob's context in the
first place. Binding the blob id would add bytes and a format change while
preventing nothing new.

### 4 What compaction costs, and who pays

Re-sealing makes compaction a keyed, CPU-bearing operation: the compactor
must hold the repository keys and pays AEAD open + seal over every moved
byte. This adds no trust surface — the 08 §8 collector already opens blob
envelopes, and any maintenance component already operates inside the key
boundary. The CPU cost lands on a background maintenance pass, which is
where the format prefers costs over reader complexity.

## Consequences

**Positive**

- The §2.1/§4 contradiction is gone with **zero format change**: no vector
  regenerates, no fixture byte moves, no reader changes.
- Nonce uniqueness under compaction holds by construction — a destination
  blob is an ordinary new blob with its own key and its own dense ordinal
  sequence (ADR-0005's argument applies verbatim).
- The reordering/splicing protection the ordinal provides (T-3) is kept at
  full strength, in compacted blobs identically to freshly written ones.
- Compacted blobs are indistinguishable from ordinary blobs to every
  reader and to the forensic rebuilder — no special case exists.

**Negative**

- Compaction throughput is bounded by AEAD speed, not copy speed. At
  AES-GCM rates this is unlikely to bind before storage does, but it is a
  real cost the Phase 4 design must budget.
- A compactor cannot run keyless. A hypothetical storage-side maintenance
  agent without repository keys can never compact; that capability is
  knowingly given up.

## Alternatives considered

**Relax 04 §2.1 for compacted blobs** — relocated records keep their
original ordinals, non-contiguously. Preserves zero-decrypt relocation
only for whole-blob-key transplants that the key schedule cannot express
anyway, weakens the position invariant every reader currently relies on,
and makes compacted blobs a special case in every validator. Rejected.

**Drop the ordinal from the AAD** — a real format change (new vectors,
regenerated fixture), surrenders in-blob reordering protection, and still
fails to enable cross-blob byte-identical moves because the key context
does not travel. All cost, no capability. Rejected.

**Leave Q15 open until Phase 4** — the freeze gate would then freeze the
AAD while the contradiction stands, making the eventual resolution a
format revision instead of a documentation fix. Resolving now, while both
options were still free, was the point of this pass. Rejected.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Resolves Q15 with no format change; 04 §4 rewritten to match; compaction defined as a re-sealing operation for Phase 4 |

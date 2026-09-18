# ADR-0052 — Format v3: a sealed record stops encoding where it lives

**Status:** Proposed
**Date:** 2026-09
**Requirements:** NFR-SEC-003, NFR-COMP-004, NFR-REL-004
**Related:** [ADR-0005](0005-aead-suite-and-nonce-construction.md), [ADR-0007](0007-logical-object-identifiers-in-manifests.md), [ADR-0014](0014-format-versioning-and-stability.md), [ADR-0025](0025-compaction-reseals-records.md), [ADR-0042](0042-write-only-repositories.md), [specification 03 §5](../../specifications/repository-format/03-keys.md), [specification 04 §§3–4](../../specifications/repository-format/04-record.md#3-nonce), [specification 07 §1](../../specifications/repository-format/07-index.md)

---

## Context

Specification 07 opens by saying what the index is for:

> The index maps **object identifiers to physical locations**. It is the only
> authority on where a record lives, because manifests deliberately carry no
> physical information.

The second sentence is not true, and the reason it is not true is the subject
of this record. The sealed bytes say where they live as well, in three
independent places:

1. **The key.** A record's key comes from its blob's key, which is
   `HKDF-Expand(class_key, "fbp/blob/v1" ‖ blob_salt ‖ writer_id ‖
   u64(blob_counter))` — three inputs that belong to the container, none of
   which travel with the record (`Repository.Crypto/BlobKeyDeriver`).
2. **The nonce.** It *is* the position: the record's zero-based ordinal
   within its blob, big-endian in the trailing bytes
   (`Repository.Format/Records/RecordNonce`).
3. **The associated data.** It binds the same ordinal again, as the last four
   of its fifty-five bytes (`Repository.Format/Records/RecordAad`).

So the format has two authorities on placement, and only one of them can be
updated. Moving a record to a different blob means decrypting and re-sealing
it, which is exactly what [ADR-0025](0025-compaction-reseals-records.md)
decided, with its eyes open, and recorded the cost of:

> A compactor cannot run keyless. A hypothetical storage-side maintenance
> agent without repository keys can never compact; that capability is
> knowingly given up.

That decision was right on the evidence in front of it. This record exists
because two things have changed since, and a third was never quite seen.

**What ADR-0025 could not see.** It considered dropping the ordinal from the
AAD and rejected it in one sentence — "still fails to enable cross-blob
byte-identical moves because the key context does not travel. All cost, no
capability." That is correct, and it is also correct of the other two changes
taken singly: making the key travel while the nonce stays positional achieves
nothing, and so does the reverse. The three are only worth anything
**together**, and evaluated one at a time each one is rejected on the grounds
that the other two exist. That is how the capability stayed invisible.

**What has changed since.** Format v2 already makes a content key travel with
the bytes it seals: a random key per data blob, X25519-sealed to the
repository's sealing public key with associated data `repository_id ‖ blob_id`
(`Repository.Packing/SealedContentKey`, [ADR-0042](0042-write-only-repositories.md)).
The mechanism this record needs already exists and already ships; v3's move is
to change what that key is scoped to.

**And what direct-ship broke.** ADR-0025's Amendment 1 confines compaction to
"the hub's staging archive", on the reasoning that exactly one place per set
holds both the keys and a writer identity. A direct-ship set
([ADR-0046](0046-direct-to-destination-publication.md)) has no staging
archive. The amendment is therefore architecturally orphaned for what is now
the default shape of a new local-path set, and the record it belongs to has no
answer for those sets at all.

**The window.** Nothing compacts yet — implementation status has carried
ADR-0025 as *Specified only* since it was written. Reversing the decision
today costs a **format revision**; reversing it after a compactor ships costs
a **data migration**. That window closes on its own.

## Decision

### 1 Format version 3, and the version is the whole of the change

A v3 repository declares `format_version = 3` in its descriptor and lists
required feature `0x0002` (`relocatable-records`), so a reader that predates
this record refuses by name rather than half-reading records whose nonces it
would misconstruct ([01 §2](../../specifications/repository-format/01-object-layout.md)).

**The AEAD suite does not change.** Records stay AES-256-GCM,
`aes-256-gcm-v1` (`0x0001`). Encryption profile `0x0002` remains **withdrawn
and unassignable** for the reason [03 §6](../../specifications/repository-format/03-keys.md#6-aead-suites)
gives — draft repositories understood it as XChaCha20-Poly1305, and a value
meaning two things is an ambiguity no version number repairs. What changes is
the nonce and AAD construction, which the format version governs; this is not
a new cipher.

### 2 A record's key is scoped to the record, not to its container

**Metadata records, and data records in a v3 repository without the sealed
data plane:**

```text
record_key = HKDF-Expand(class_key[generation],
                         "fbp/record/v3" ‖ u8(object_type) ‖ object_id, 32)
```

The object identifier is the record's *logical* name — the one thing
guaranteed not to change when it moves, because ADR-0007 keeps physical
location out of manifests precisely so that it can move. Deriving from it
means the key travels wherever the bytes do, at no storage cost at all: there
is nothing to carry, because the derivation inputs are already in the footer
and the index.

**Data records in a v3 repository with the sealed data plane (v2's lineage):**
a random 32-byte content key **per record**, X25519-sealed to the sealing
public key with associated data `repository_id ‖ object_id`, carried in the
record's header. This is `SealedContentKey` with `blob_id` replaced by
`object_id`, which is the same substitution this record makes everywhere else.

The two cases differ because they must. A derived key is derivable by whoever
holds the class key, and in a write-only repository the service holds it and
must not be able to read content ([FR-WOR-001](../requirements/functional.md),
NFR-SEC-010). FR-WOR-003 already draws exactly this line — the structure plane
stays readable to the write-bundle holder, the content plane does not — so v3
follows a boundary the format already has rather than inventing one.

### 3 The nonce stops being the position

The record nonce is **twelve zero bytes**. Uniqueness moves from position to
key: one key per object identifier, one message under it.

This is the decision's sharpest edge and it is stated as a condition rather
than a claim. Under §2's derived-key case, two *different* plaintexts sealed
under one object identifier would reuse a `(key, nonce)` pair, which for
AES-GCM is a plaintext-recovery failure. That requires two different
plaintexts to produce one content identifier — a hash collision, under the
profile-selected hash — and it is the identical condition deduplication
already rests on: FR-DED-003's verify-on-reuse checks that a record's bytes
match its claimed content identifier before referencing it, and every reuse in
the repository is already trusting exactly this. v3 does not add a new
assumption; it adds a second consequence to an existing one, and the security
review must be told so in those words.

Under §2's sealed case the key is random per record, so uniqueness is
unconditional and rests on nothing.

Re-sealing the same object identifier's same bytes now produces byte-identical
ciphertext. That is a property, not a leak: the store sees neither, and within
the trust boundary the index already names the object identifier openly.

### 4 The associated data stops binding the position

```text
AAD = repository_id ‖ u16(format_version) ‖ u8(object_type) ‖ object_id
```

Fifty-one bytes; the trailing `u32(ordinal)` is gone. The blob identifier
stays out, now for the reason 04 §4 originally gave and ADR-0025 had to void:
a record genuinely is relocatable, and binding its container would be the one
thing that stopped it.

**Where in-blob reordering protection goes.** ADR-0025 kept the ordinal partly
for this: the AAD is what stops records being spliced or reordered within a
blob (T-3). Under v3 the protection does not disappear, it moves, and it moves
somewhere stronger. A reader resolves a record through the blob's **recovery
footer**, which is sealed under the blob's own key and names each record's
object identifier, offset and length ([05 §3](../../specifications/repository-format/05-blob.md)).
An attacker who reorders two records must reorder their footer entries to
match, and the footer is authenticated as a unit. Swapping bytes without
touching the footer produces a record whose bytes do not open under the key
derived from the object identifier the footer claims for that offset. Both
attacks fail, and the second fails in a way the ordinal never covered — under
v1, two records *within one blob* are protected by their ordinals, but nothing
stops a record being presented under a different object identifier's index
entry, because the AAD binds the object id and the reader believes the index.
v3 tightens that: the key itself depends on the object identifier.

### 5 v1 and v2 are read forever; nothing is rewritten

A repository's format version is fixed at creation. v1 and v2 repositories are
read in place by every future reader, their conformance vectors unchanged and
their published specification unchanged. There is no in-place upgrade and no
migration pass, because there is nothing that needs one: the reason to want v3
is a capability a *future* compactor needs, and a repository that never
compacts loses nothing by staying where it is.

This is what makes the change cheap enough to make. It is a third reader
branch, not a fleet operation.

### 6 What this buys, stated exactly

A compactor can move records between blobs **without holding any content
key**. It reads the source footer, copies ciphertext bytes, writes a
destination blob, and republishes index entries as supersessions
([ADR-0017](0017-index-entry-supersession.md)) — never opening a record,
never deriving a record key, never seeing a plaintext byte.

**It is not fully keyless, and this record will not pretend otherwise.** The
destination blob needs a sealed recovery footer, and the footer is sealed
under a blob key. So a v3 compactor needs footer-sealing capability and a
writer identity; what it no longer needs is the ability to read content. That
is a real reduction in what a maintenance component must be trusted with — it
moves compaction from "inside the key boundary" to "inside the structure
boundary", the same line §2 draws and the same line FR-WOR-003 already draws —
and it is a smaller claim than "storage-side agent". §9 records what would
have to change to close the rest.

## Consequences

**Positive**

- Specification 07's opening sentence becomes true. The index is the only
  authority on where a record lives, in fact and not merely in doctrine.
- Compaction stops being a decrypt-and-reseal operation, so it stops costing
  AEAD time over every moved byte and stops requiring content keys.
- ADR-0025 Amendment 1's staging confinement is no longer load-bearing, which
  matters because it is already orphaned for direct-ship sets — the default
  shape of a new local-path set has no staging archive to confine anything to.
- The twelve compaction exit criteria in ADR-0025 Amendment 2 survive intact.
  They are about index correctness, and this record touches the record plane.

**Negative**

- A third reader branch, and a third set of conformance vectors to generate
  and hold. NFR-COMP-004's promise is now made three times.
- The derived-key case couples record confidentiality to content-hash
  collision resistance in a way v1 does not. Dedup already rests there, but
  "already rests there" is an argument that must survive the external review
  rather than replace it.
- Convergent ciphertext within a repository: identical plaintext under an
  identical object identifier seals identically. Judged harmless above; it is
  a property a reviewer should be shown rather than discover.
- Two key schedules in one format version — derived for structure, sealed for
  content — where v1 had one. The write-only plane already forced this shape;
  v3 makes it explicit rather than incidental.

## Alternatives considered

**Leave ADR-0025 standing.** Defensible, and it stays defensible right up
until the first compactor ships, at which point the same change costs a data
migration instead of a format revision. The asymmetry is the argument: the
option to do this expires, and the option to not do it does not.

**A per-record sealed key everywhere, including the structure plane.**
Uniform, no collision-resistance coupling, and no derived keys at all. Costs a
sealed share — tens of bytes — on every metadata record, of which there are
one per file version, per tree, per snapshot. Rejected as a default for the
structure plane, where the class key is legitimately held by the reader
anyway; kept for the content plane, where it is the only correct answer.

**Keep the nonce positional and make only the key travel.** The nonce would
then have to be rewritten on relocation, which means re-sealing, which is what
this record exists to avoid. This is the trap ADR-0025 fell into from the
other side, and naming it here is the point.

**Relocate whole blobs only.** Already possible in v1 and already useless: a
whole-blob copy is replication, not compaction, and compaction exists
precisely to reclaim the space that partially-live blobs hold.

## Open questions

1. **The footer's key.** §6 leaves a compactor needing footer-sealing
   capability. Whether the footer should be *signed* (Ed25519, under
   [ADR-0020](0020-ed25519-signing-key-semantics.md)) rather than AEAD-sealed
   would close the gap and make compaction genuinely keyless — at the cost of
   a footer that is authenticated but not confidential, which changes what a
   store learns about a blob's contents. Not decided here.
2. **Where a v3 compactor runs.** With content keys out of the picture, a
   destination could in principle compact its own replica. It still may not
   allocate a writer sequence number ([ADR-0034](0034-hub-and-spoke-destinations.md)),
   so either the hub plans the compaction and the destination executes it, or
   destinations keep converging and never compact. This is the question
   ADR-0025 Amendment 1 answered for staging sets and left unanswered for
   direct-ship ones.
3. **Generation rotation.** §2's derived key takes `class_key[generation]`, so
   a record's key depends on the generation it was written under, which the
   footer records. Full data-key rotation ([03 §7](../../specifications/repository-format/03-keys.md#7-rotation))
   already rewrites every blob, so this composes — but it wants stating in the
   specification rather than inferring.
4. **A digest a peer can be challenged against.** *(Added 2026-09.)* The
   covered-blob digest a delta publishes ([07 §2.2](../../specifications/repository-format/07-index.md))
   is a flat `SHA-256` over the sealed bytes. It lets a source that reads a
   blob back check what it read, and it lets nothing else: a peer asked to
   hash its own copy and answer is answering a question whose answer it
   could have cached at receipt, so a "digest challenge" over the flat
   digest is a self-report, not a proof of possession
   ([ADR-0058](0058-peer-write-adapter.md) §8, as amended). What would make
   one sound is publishing the digest as a **Merkle root** over fixed-size
   chunks of the sealed bytes — the chunk size stated, on the order of a
   mebibyte — so a source holding only the signed root can ask a peer for
   random leaves with their authentication paths and verify them without
   the blob. This is the reviewing architect's R1 point, and it is a change
   to what the index carries; v3 is the window in which the shape of that
   field is still free. Whether the root sits beside the flat digest or
   replaces it, and whether the leaf size is fixed by the format or
   recorded per delta, are the decisions this item would take.

## What this record does not do

It does not import the reviewing architect's vocabulary. The external review
proposes "pieces", "packs" and a "placement catalogue" as new structures; all
three already exist here under names the glossary
([01 §1](../architecture/01-domain-model.md)) has carried since phase 0 — a
piece is a **segment record**, a pack is a **blob**, and the placement
catalogue is the **index**. Adopting a second set of names for one set of
things is the drift the glossary exists to prevent, so the review's §12 target
architecture is, in its nouns, the architecture that is already built. What
was genuinely missing is not a structure but a property, and this record is
about that property alone.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Amended (open question 4) | The covered-blob digest as a Merkle root, so a peer can be challenged for possession against the signed root without the bytes crossing the wire — recorded here after [ADR-0058](0058-peer-write-adapter.md)'s digest challenge was refused as a self-report over the flat digest |
| 2026-09 | Proposed | Design only, in response to the 2026-09 architecture review's R3. Supersedes [ADR-0025](0025-compaction-reseals-records.md)'s decrypt-and-reseal decision for format v3 and leaves it in force for v1 and v2. Nothing implements v3; the record exists to take the decision while it is still a format revision rather than a data migration |

# ADR-0065 — The Merkle commitment and the chunk possession challenge

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-VER-001, FR-VER-003, FR-WOR-003, NFR-COMP-004
**Related:** [ADR-0052](0052-relocatable-records-format-v3.md), [ADR-0058](0058-peer-write-adapter.md), [ADR-0014](0014-format-versioning-and-stability.md), [repository-format 05 §5.2](../../specifications/repository-format/05-blob.md#52-the-merkle-commitment), [repository-format 07 §2.3](../../specifications/repository-format/07-index.md#23-covered-blob-merkle-roots), [peer-protocol 07 §3.6](../../specifications/peer-protocol/07-retrieval.md#36-merkle_challenge-278--merkle_proof-279), [architecture 09 §5](../architecture/09-replication-and-peers.md)

**Built:** `Repository.Packing/BlobMerkle` (the RFC 6962 tree over one-mebibyte leaves, the length-bound root, the path and its verification) and `BlobMerkleAccumulator` (fed by the calls that already feed the blob's digest, so the tree costs no second pass), `Repository.Packing/BlobWriter` and `SealedBlob` (the root produced at seal and carried with the blob), `Repository.Index/IndexDeltaCodec` (delta key 11, its parallel-or-absent rule and its never-alone rule), `Repository.Index/IndexPublisher`, `Repository/SnapshotPublication` and `Repository/PublicationOrchestrator` (published only at repository format 3 or above), `Repository.Catalogue/CatalogueSchema` and `Catalogue` (schema 7's `merkle_root`, upserted beside the digest and never erased by a delta that carries none, read back by `SignedMerkleRootOf`), `Protocol/PeerRetrievalMessages.cs` (`MerkleChallenge` and `MerkleProof`), `Protocol/PeerFrame.cs`, `Protocol/PeerAuthenticator` and `Protocol/PeerSessionNegotiation` (types 278–279, the state gate and the `chunk-possession` token), `Agent/RetrievalResponder` (the destination streams its own copy and answers with one leaf and its path), `Agent/PeerRetrievalClient` (the challenge over the session the read-back already holds), `Replication/ReplicaVerifier` (the chunk tier ahead of the digest tier, and `VerificationOutcome.Chunk`), `Agent/FanOut` (the prover closed over the session, and the tier on the ledger), `Application/DestinationSyncStore` (schema 4's `verified_chunk`), `Api/Results.cs` and `Api/ContractVersion.cs` (contract 1.34); `Repository.Tests/Packing/BlobMerkleTests`, `Repository.ConformanceTests/MerkleConformanceTests`, `Repository.Tests/Index/IndexPlaneTests`, `Repository.Tests/Catalogue/CatalogueTests`, `Repository.Tests/EndToEnd/WriteOnlyRepositoryTests`, `Repository.ConformanceTests/FixtureRepositoryV3Tests`, `Repository.ConformanceTests/FixtureRepositoryV2Tests`, `Protocol.Tests/RetrievalMessageTests`, `Hosts.Tests/PeerReadBackVerificationTests`, `Application.Tests/DestinationSyncStoreTests`, `Api.Tests/ConfigurationContractTests`, `Api.Tests/ContractAdditiveFieldsTests`.

---

## Context

A source with no second copy of its own content proves a peer holds a sealed
blob by **pulling the whole blob back and hashing it** against the flat
digest the writer signed into the index
([ADR-0058](0058-peer-write-adapter.md) §8,
[07 §2.2](../../specifications/repository-format/07-index.md#22-covered-blob-digests)).
That is a real proof and an expensive one: the bytes cross a domestic uplink
at one round trip per mebibyte, to establish something the peer could have
stated in thirty-two bytes if thirty-two bytes meant anything.

They do not. [ADR-0058](0058-peer-write-adapter.md)'s amendment refused the
obvious cheaper message: a peer asked to hash its own copy and answer is
answering a question whose answer it may have computed once at receipt and
kept after discarding the bytes. The source holds only the flat digest, so it
can key nothing to a nonce, and a bare digest is therefore a **claim**.
[04 §5.1](../../specifications/peer-protocol/04-verification.md) says so
normatively and names the remedy: such a challenge becomes sound only when
the index commits to the blob in a form the source can open without the
bytes.

This record builds that commitment and the challenge over it. It is
[ADR-0052](0052-relocatable-records-format-v3.md)'s open question 4, decided
in shape by that record's Amendment 1 and left to the index plane.

## Decisions

### 1. The commitment is an RFC 6962 tree over one-mebibyte leaves

Over exactly the flat digest's preimage — bytes `[0, length − 16)`,
everything before the locator — so the two commitments name the same bytes
and a blob cannot satisfy one and fail the other without the disagreement
meaning something. `leaf = SHA-256(0x00 ‖ chunk)`,
`node = SHA-256(0x01 ‖ left ‖ right)`, and `MTH` splits at the largest power
of two strictly below the leaf count, which is not a halving: three leaves
are 2 + 1 and never 1 + 2.

The prefixes are load-bearing. Without the leaf prefix a one-leaf tree's head
is the chunk's bare digest, and an interior node's preimage could be
presented as a leaf's — which is how a second tree is made to produce a root
somebody already signed.

**The leaf size is stated by the format, not recorded per blob.** Both
parties to a challenge then agree on it by construction, and a size a reader
could be *told* is a size the party being checked could choose to make cheap.

### 2. The published root binds the preimage's length

`merkle_root = SHA-256(0x02 ‖ u64_be(preimage_length) ‖ MTH(leaves))`, a
third prefix beside the leaf's and the node's.

This was found by building, not by designing, and it is the decision most
worth reading twice. RFC 6962's inclusion check takes the tree size **from
its caller**, and for a four-leaf tree's first leaf the path a three-leaf
tree wants has the same length and walks to the same head. At a peer the
tree size can only come from the length the destination declares for its own
copy — so without the binding a destination could understate its copy by a
whole leaf, exempt that leaf from ever being drawn, and still answer every
challenge correctly. With it, a length that disagrees with the one the writer
signed produces a root that does not match, and the challenge fails closed.

### 3. Beside the flat digest, never instead of it

A delta that carries Merkle roots carries digests too; the codec refuses one
without the other. The root is the stronger commitment and the digest is the
cheaper check, and a reader that will not walk a tree — or a party holding
the whole blob, for which the flat hash is simply less work — must always
have the other to fall back on.

### 4. Published only at repository format 3 or above

Delta key 11, `covered_blob_merkle_roots`, parallel to `covered_blob_ids`.
A writer at format 3 or above MUST publish it for every blob a delta covers;
a writer below format 3 MUST NOT.

The reason is the index codec's oldest rule, now written down at
[07 §2.4](../../specifications/repository-format/07-index.md): unlike a map
key inside a message on the peer wire, an unknown key in a delta is refused
rather than skipped, because a delta is a signed statement about what exists
and where and a key a reader cannot interpret may be changing the meaning of
the keys it can. It follows that a delta carrying key 11 is unreadable to any
build that predates it. Tying publication to the declared format version
means a repository an older reader is entitled to read never contains one,
and [ADR-0014](0014-format-versioning-and-stability.md)'s refuse-by-name rule
holds without anything having to be refused. No format-2 repository migrates.

### 5. The bytes are the proof; the path is not

The challenge names a repository, a blob's key and a leaf index; the answer
carries that leaf's **bytes** and the sibling hashes that carry them to the
root, or an honest inability to produce either
([07 §3.6](../../specifications/peer-protocol/07-retrieval.md)).

An authentication path is public arithmetic over hashes the examined party
may freely cache, so producing one establishes nothing; producing the chunk
it commits to establishes that the chunk is held. That is the whole
difference between this message and the digest answer ADR-0058 refuses, and
it is why the leaf crosses the wire rather than a hash of it.

Three further rules follow from the same reading. The leaf is drawn **at
random** per blob, because a fixed choice is one a party that discarded part
of a blob survives for ever. "Cannot produce" from a party that listed the
blob in its own inventory is a **finding**, not a shrug — it is the
truncation the challenge exists to catch. And the destination reads its whole
blob from local disk to build the path: that is the cost this message moves
rather than removes, a disk read at the destination instead of an uplink's
worth of bytes at both ends.

### 6. The feature is offered and never required

`chunk-possession`, negotiated as every peer feature is.

[02 §6](../../specifications/peer-protocol/02-session.md) forbids a feature
being the sole gate on a check that defends one side against the other,
because the intersection is computed from both hellos and conditioning such a
check on a feature hands the decision to the party being checked. This
feature survives that rule for one reason, and it is the design's keystone:
**its absence costs the destination more, never less.** A peer that does not
offer it is read back whole, exactly as every peer is today. The feature buys
the *destination* bandwidth, so declining it is self-harm rather than
evasion — the shape 02 §6 already admits for `replica-claim`. The source
therefore does not require it, and no existing peer is stranded.

### 7. The tier is counted apart from the digest tier

`VerificationOutcome.Chunk`, the ledger's `verified_chunk` at schema 4, the
contract's row at 1.34, and the console's "N by chunk".

A chunk proof **samples** the blob where a digest proof reads all of it.
Summing them would let the cheaper tier be read as the stronger one, which is
exactly what FR-VER-003's "coverage and age, never a bare boolean" exists to
prevent. The tier runs ahead of the digest tier and falls through to it:
given a signed root and a prover, one leaf settles the blob; with either
missing — a format-2 repository with no root on record, a peer that does not
offer the feature — the digest tier decides under its own byte budget,
unchanged.

## Alternatives

- **Replace the flat digest with the root.** One field instead of two, and a
  saving of thirty-two bytes a blob. Rejected: every reader that holds the
  whole blob would have to walk a tree to check what a single hash already
  answers, and a rebuilt catalogue would lose the cheaper commitment for the
  sake of tidiness.
- **Record the leaf size per delta.** More flexible, and the flexibility has
  one use — a future change of leaf size — which the format version already
  covers. It would also add a field the party being checked could be told,
  and a challenge is not somewhere to admit a negotiable cost.
- **Answer with the leaf's hash rather than its bytes.** Thirty-two bytes
  instead of a mebibyte. Rejected for the reason the whole record exists: a
  destination that cached its leaf hashes could answer without holding the
  bytes, which is the self-report again, one level down.
- **A nonce-keyed answer over the leaf.** A destination that holds the bytes
  could answer `HMAC(nonce, leaf)` in thirty-two bytes, and the source could
  check it — if the source knew the leaf's bytes, which it does not, or held
  every leaf hash, which would mean publishing sixteen kibibytes of hashes a
  blob. Rejected on that arithmetic.
- **Make the challenge a required feature.** It would guarantee the cheap
  path. It would also refuse every session with a peer that predates it, for
  a saving that is the destination's own, and it is the shape 02 §6 warns
  against even where the direction happens to be benign.

## Consequences

**Positive.** A write-only set's sealed data plane at a peer is proved for a
mebibyte a blob instead of a blob a blob, which is what makes the proof
affordable often enough to be worth having. The commitment is durable and
signed, so any party holding the root — not only this source — can check a
leaf. The root costs the writer no second pass over the sealed bytes, because
the accumulator is fed by the same calls that feed the flat digest.

**Negative.** The destination reads its whole blob from disk to answer, which
is real work it did not do before; the record states it rather than hiding it,
and a destination is free to cache its leaf hashes to avoid it. A delta
carrying key 11 is unreadable to builds that predate it, contained by the
format-version gate and by nothing else. And a chunk proof is a **sampled**
proof: it establishes that one leaf of the blob is held, and repeated passes
over a rotating sample are what turn that into coverage — which is why it is
counted apart from the whole-blob tier rather than with it.

## What this record does not do

- It does not make the challenge available on a format-2 repository. Nothing
  at format 2 publishes a root, and nothing may.
- It does not give the service a way to create a format-3 archive. That is
  still `init --format-version 3`
  ([ADR-0052](0052-relocatable-records-format-v3.md) Amendment 1 item 7); the
  peer suite reaches format 3 through a test-only seam and says so.

  > **Amended 2026-09 ([ADR-0066](0066-the-format-upgrade-record.md)):** it
  > does now — not by this record, but by the creation default moving to
  > format 3, so every set the product creates publishes a root and this
  > challenge is reachable by an ordinary installation rather than only
  > through that seam. A set still at format 2 publishes no root and is
  > proved by reading the whole blob back, which is the bullet above.
- It does not carry the challenge to a local-path destination. There is no
  wire there and no bandwidth to save: a local path is proved by tag, and by
  the digest tier where the records are sealed.
- It does not prove the blob. One leaf is one leaf.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Amended (reachable in production) | The creation default moved to format 3 ([ADR-0066](0066-the-format-upgrade-record.md)), so every set the product creates publishes a Merkle root and the chunk challenge is reachable without the test-only seam. Nothing in the commitment, the messages or the tier changed; what changed is who has one |
| 2026-09 | Accepted | [ADR-0052](0052-relocatable-records-format-v3.md)'s open question 4, built over four commits: the commitment and its conformance vectors (`Repository.Packing/BlobMerkle`, `merkle.json`); the index plane (`Repository.Index/IndexDeltaCodec` key 11, `Repository.Catalogue/Catalogue` schema 7, both committed fixtures regenerated); the wire (`Protocol/PeerRetrievalMessages.cs` types 278–279, the `chunk-possession` token); and both ends with the tier (`Agent/RetrievalResponder`, `Agent/PeerRetrievalClient`, `Replication/ReplicaVerifier`, `Agent/FanOut`, ledger schema 4, contract 1.34). Building it found the length binding: plain RFC 6962 admits a four-leaf tree's path under a claimed size of three, so a destination could understate its copy to exempt its last leaf. Held by `Repository.Tests/Packing/BlobMerkleTests`, `Repository.ConformanceTests/MerkleConformanceTests`, `Protocol.Tests/RetrievalMessageTests` and `Hosts.Tests/PeerReadBackVerificationTests` |

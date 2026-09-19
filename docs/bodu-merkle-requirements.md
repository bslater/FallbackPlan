# Requirements: an RFC 6962 Merkle tree and proof type for Bodu

**Status: Raised, not yet satisfied** — `Bodu.Security.Cryptography` 0.2.0
carries `MerkleTreeHash` and `ParallelMerkleTreeHash`, which share this
document's domain-separation scheme and **not** its tree shape, and which
expose no proof API ([§2](#2-current-state-and-gaps)).
**Audience:** the Bodu maintainer and FallbackPlan contributors ·
**Raised:** 2026-09-19

FallbackPlan's repository format commits to each sealed blob twice: a flat
`SHA-256` over the blob's bytes, and — since
[ADR-0065](adr/0065-merkle-commitment-and-chunk-possession.md) — a **Merkle
root** over one-mebibyte chunks of the same preimage. The root is what lets a
party holding neither the blob nor any key check a *single chunk* of it: given
the root, one leaf's bytes and that leaf's authentication path, the chunk is
either where the writer said it was or it is not. That is what turns a peer
possession challenge from a self-report into a proof, and it is why
FallbackPlan now carries `Repository.Packing/BlobMerkle` — roughly two hundred
lines of tree, path and verification code that is not backup-specific in any
way.

It should not be there. It is a general cryptographic primitive, it belongs in
a cryptography library, and Bodu already has the beginnings of one. This
document states what a shared Bodu type must provide to serve FallbackPlan
**and** any other consumer with the same shape: transparency logs, artifact
attestation, content-addressed stores, audit trails — anything that needs to
prove one element of a committed set without shipping the set. It is a
requirements statement against the *library*, written from the consumer side;
how Bodu implements it is Bodu's decision.

Requirement IDs here are `MTH-F-*` (functional) and `MTH-N-*`
(non-functional). They are scoped to this document and deliberately do not use
FallbackPlan's `FR-*`/`NFR-*` namespace; where a requirement exists to satisfy
a FallbackPlan requirement, that ID is cited.

---

## 1. Consumers and their shapes

| Consumer | What it needs |
|----------|---------------|
| **FallbackPlan repository format** | A root over fixed-size blocks of a byte stream, computed once at seal from bytes that are streamed past and never held (blobs run to 512 MiB), published in a signed index object. Plus the authentication path for one block, produced by a party that holds the bytes, and verification of that path by a party that holds only the root ([FR-VER-001](requirements/functional.md), [ADR-0065](adr/0065-merkle-commitment-and-chunk-possession.md)). |
| **FallbackPlan peer protocol** | Path generation on a destination reading its own replica from disk, and verification on a source that has neither the blob nor a key — over a wire format that bounds the path length before allocating ([peer-protocol 07 §3.6](../specifications/peer-protocol/07-retrieval.md)). |
| **Transparency-log consumers** | Entry-mode trees over variable-length entries, inclusion proofs, and **consistency proofs** between two published tree sizes — the RFC 6962 use case the algorithm was designed for. |
| **Artifact and supply-chain attestation** | A root over a manifest of file digests, with per-file inclusion proofs so a consumer can verify one file of a release without downloading the release. |
| **Content-addressed and deduplicating stores** | Chunk-level integrity that localises damage to a chunk rather than condemning an object, using a commitment the store itself did not compute. |

The common thread: **the library computes and checks commitments; the consumer
owns the bytes, the transport, and the signature over the root.** Anything that
would pull I/O policy, a wire format, or a signing scheme into the library is
out of scope ([§10](#10-non-goals)).

## 2. Current state and gaps

`Bodu.Security.Cryptography` 0.2.0 provides:

| Surface | Provides |
|---------|----------|
| `MerkleTreeFormat` | The shared constants: `LeafPrefix`, `InternalNodePrefix`. Documented as "following the RFC 6962 §2.1 domain-separation scheme". |
| `MerkleTreeHash` | Single-threaded root computation over a byte stream, configurable hash algorithm, `blockSize` (default 1024) and `fanOut` (default 3). `ComputeHash` over `Stream`, `ReadOnlySpan<byte>`, `byte[]`; `ProcessInput` + `ComputeFinalHash` for incremental feeding. |
| `ParallelMerkleTreeHash` | The same computation over a concurrent level-worker pipeline; `blockSize` default 4096, `fanOut` default 2; `ComputeHashAsync` with cancellation. |
| `MerkleTreeDiagnostics` / `MerkleTreeDiagnosticNode` | A node-by-node trace of one computation — level, index, child hashes, output — documented as diagnostic only and "should not be enabled in production paths". |

Four gaps against this document, the first two measured rather than inferred.

### 2.1 The tree shape is not RFC 6962's

The domain-separation claim is accurate. The **tree** is a different tree.

RFC 6962 splits a tree of *n* leaves at `k`, the largest power of two strictly
below *n*, and recurses: the left subtree is perfect and the right holds the
remainder. Bodu 0.2.0 reduces **level by level**, pairing adjacent nodes and
hashing a lone leftover as a **one-child node** `H(0x01 ‖ child)` — where
RFC 6962 promotes that subtree root unchanged.

The two agree exactly when the leaf count is a power of two, and differ
otherwise. Measured against `MerkleTreeHash(SHA256, blockSize: 4, fanOut: 2)`
over the inputs of [Appendix B](#appendix-b--block-mode-vectors):

| Input bytes | Leaves | RFC 6962 MTH | Bodu 0.2.0 | Agree |
|---|---|---|---|---|
| 1 | 1 | `96a296d2…` | `96a296d2…` | ✅ |
| 4 | 1 | `a498efa8…` | `a498efa8…` | ✅ |
| 5 | 2 | `a568e844…` | `a568e844…` | ✅ |
| 8 | 2 | `000134e5…` | `000134e5…` | ✅ |
| 9 | 3 | `7d0d7649…` | `30cc4e02…` | ❌ |
| 12 | 3 | `3b469d6b…` | `a8ff2177…` | ❌ |
| 13 | 4 | `311ae2a2…` | `311ae2a2…` | ✅ |
| 16 | 4 | `516c43cb…` | `516c43cb…` | ✅ |
| 17 | 5 | `557a6fa2…` | `30b62b0e…` | ❌ |
| 20 | 5 | `0a30d614…` | `0892a381…` | ❌ |
| 28 | 7 | `6a648b43…` | `f9bea195…` | ❌ |
| 32 | 8 | `4caa2890…` | `4caa2890…` | ✅ |
| 33 | 9 | `31dce6ff…` | `7b29c4db…` | ❌ |

This is not presented as a defect in the computation — a level-by-level tree is
a perfectly good commitment — but it is a **documentation defect**, and it is
the reason FallbackPlan could not simply consume the existing type. A reader of
"following the RFC 6962 §2.1 domain-separation scheme" may reasonably conclude
the type produces RFC 6962 roots, and cross-check against a transparency-log
implementation, and be wrong. Whatever else this document produces,
MTH-F-012 should
land.

### 2.2 There is no proof API

Neither type can answer "give me the path for leaf *m*", and neither can verify
one. `MerkleTreeDiagnostics` captures enough to *derive* a path — every node
with its level, index and child hashes — but the library's own documentation
rules it out of production use, and deriving a path from a full trace is
quadratic work for a logarithmic answer.

The proof half is the larger half of the requirement, and it is the half that
has no workaround: a consumer can hand-roll a root in twenty lines, but a
correct inclusion-proof verifier is where the subtle failures live
([§3.4](#34-inclusion-proof-verification), [§3.7](#37-the-tree-size-ambiguity-and-the-bound-root)).

### 2.3 The empty tree throws

`ComputeHash` over zero bytes raises `InvalidOperationException` ("No input data
was provided"). RFC 6962 defines `MTH({}) = HASH()` — the hash of the empty
string — and a log that has published nothing still has a head to sign.

### 2.4 Fan-out is a knob where the standard has none

RFC 6962 is binary. `fanOut` defaults to 3 on `MerkleTreeHash` and 2 on
`ParallelMerkleTreeHash`, so two types in one library, both documented against
the same RFC, disagree with each other out of the box. A conforming mode must
not offer the knob at all.

---

## 3. Normative definitions

This section is the specification an implementer builds from. It restates
[RFC 6962 §2.1](https://www.rfc-editor.org/rfc/rfc6962#section-2.1) with the
additions this document requires, and nothing in it is novel except
[§3.6](#36-block-mode) and [§3.7](#37-the-tree-size-ambiguity-and-the-bound-root).

Notation: `‖` is concatenation, `H` is the configured hash function,
`u64_be(x)` is `x` as eight big-endian bytes, and `D[n] = d(0), …, d(n−1)` is an
ordered list of *n* entries.

### 3.1 Hashing and domain separation

```text
empty_hash          = H()                       -- the hash of zero bytes
leaf_hash(d)        = H(0x00 ‖ d)
node_hash(l, r)     = H(0x01 ‖ l ‖ r)
```

The prefixes are not decoration. Without the leaf prefix, a one-leaf tree's root
is the entry's bare digest, and an interior node's preimage — a concatenation of
two child hashes — could be presented as leaf data. That is how a second tree is
constructed to produce a root somebody already signed.

### 3.2 The Merkle Tree Hash

```text
MTH(D[0])   = empty_hash
MTH(D[1])   = leaf_hash(d(0))
MTH(D[n])   = node_hash(MTH(D[0:k]), MTH(D[k:n]))        for n > 1
              where k is the largest power of two strictly less than n
```

`k < n ≤ 2k`. The left subtree is always perfect; the right holds the remainder
and may be any size from 1 to k. **A subtree root is promoted unchanged, never
re-hashed as a one-child node** — this is the single point on which
[§2.1](#21-the-tree-shape-is-not-rfc-6962s) turns.

### 3.3 Inclusion proofs

The proof for entry *m* of `D[n]` is the list of sibling subtree roots from the
leaf upward:

```text
PATH(m, D[1])  = {}
PATH(m, D[n])  = PATH(m,   D[0:k]) : MTH(D[k:n])         for m <  k
               = PATH(m−k, D[k:n]) : MTH(D[0:k])         for m >= k
```

The path has at most `ceil(log2(n))` elements, and for a given *n* the length
varies by *m*: in a seven-leaf tree, leaves 0–5 have three-element paths and
leaf 6 has two ([Appendix A2](#a2--inclusion-proofs-tree-size-7)).

### 3.4 Inclusion proof verification

Given a leaf hash, its index *m*, the tree size *n*, the claimed root, and the
path:

```text
1.  if m >= n                       -> reject
2.  fn := m ; sn := n − 1 ; r := leaf_hash
3.  for each p in path:
        if sn = 0                   -> reject          (path too long)
        if fn is odd or fn = sn:
            r := node_hash(p, r)
            if fn is not odd:
                right-shift fn and sn together until fn is odd or sn = 0
        else:
            r := node_hash(r, p)
        right-shift fn and sn by one
4.  accept iff sn = 0 and r = root
```

Three properties of this algorithm are worth stating because they are easy to
lose in a re-implementation:

- **The tree size is an input, not a derivation.** Where it comes from is the
  caller's problem, and [§3.7](#37-the-tree-size-ambiguity-and-the-bound-root)
  is what happens when the caller gets it from an untrusted party.
- **A path that is too short or too long is rejected** by the `sn` bookkeeping
  alone; no separate length check is needed, and adding one that disagrees with
  the walk is a way to reject valid proofs.
- **The final comparison must be constant-time** (MTH-N-004).

> **A note for the implementer.** FallbackPlan's implementation terminates the
> inner shift loop on `fn = 0` where the RFC says `sn = 0`. These were compared
> exhaustively over all `(n ≤ 39, m < n)` with correct, truncated and extended
> paths and every claimed tree size from 1 to 44 — 102 960 cases, zero
> differences. Bodu should nonetheless use the RFC's wording verbatim, so that a
> reader comparing the code with the standard is never asked to prove an
> equivalence.

### 3.5 Consistency proofs

For an append-only log, the proof that `D[m]` is a prefix of `D[n]` (`0 < m ≤ n`):

```text
PROOF(m, D[n])       = SUBPROOF(m, D[n], true)

SUBPROOF(m, D[m], true)   = {}
SUBPROOF(m, D[m], false)  = { MTH(D[m]) }
SUBPROOF(m, D[n], b)      = SUBPROOF(m, D[0:k], b) : MTH(D[k:n])        for m <= k
                          = SUBPROOF(m−k, D[k:n], false) : MTH(D[0:k])  for m >  k
              where k is the largest power of two strictly less than n
```

Verification takes both roots and both sizes and reconstructs each; the
algorithm is [RFC 6962 §2.1.2](https://www.rfc-editor.org/rfc/rfc6962#section-2.1.2)
and is not restated here.

FallbackPlan needs none of this — its commitment is over an immutable blob,
which never grows — so consistency proofs are **SHOULD**, not **MUST**
(MTH-F-008). They are specified because a
general Merkle type in a cryptography library that offers inclusion proofs and
not consistency proofs is half a type, and the second consumer to arrive will be
a log.

### 3.6 Block mode

A byte stream is turned into entries by fixed-size blocks:

```text
block_count(L, B)     = ceil(L / B)        , and 1 when L = 0 is FALSE -- see below
block(i, L, B)        = bytes [ i·B , min((i+1)·B, L) )
```

The final block is short whenever `L` is not a whole multiple of `B`, and is
hashed **at its actual length** — never zero-padded to `B`. Padding would make a
short final block indistinguishable from a full block of the same bytes followed
by zeroes.

A zero-length stream has **zero** blocks and its root is `MTH(D[0]) = empty_hash`
([§3.1](#31-hashing-and-domain-separation)), not one empty leaf.

### 3.7 The tree-size ambiguity, and the bound root

This is the one addition to RFC 6962 that this document asks for, and the reason
is a concrete attack that FallbackPlan found by building rather than by
designing.

RFC 6962's verifier takes the tree size from its caller. When the caller obtains
that size from the party being examined — which is exactly the possession-proof
case, where the size is derived from the length the holder claims its copy is —
the holder can lie about it. Specifically:

```text
4-leaf tree, leaf 0:  path = [ leaf_hash(1), node(leaf_hash(2), leaf_hash(3)) ]

verify(root, tree_size = 4, m = 0, leaf 0, path)  ->  accept   (correct)
verify(root, tree_size = 3, m = 0, leaf 0, path)  ->  accept   (!!)
```

Both accept against the **same root and the same path**, because a four-leaf
tree's first path has exactly the length a three-leaf tree's first path wants and
walks to the same head. A holder of a four-leaf object that has lost leaf 3 can
therefore declare a three-leaf object, never be asked for leaf 3, and answer
every challenge correctly for ever. The measured case is in
[Appendix D](#appendix-d--the-ambiguity-and-the-bound).

Note that the partial-final-block rule of [§3.6](#36-block-mode) does **not**
close this: the verifier never hashes the sibling as a leaf, so leaf-level length
binding is irrelevant to it.

The remedy is to bind the size into the published commitment, under a prefix of
its own:

```text
bound_root(D[n])          = H(0x02 ‖ u64_be(n) ‖ MTH(D[n]))          -- entry mode
bound_root(bytes, B)      = H(0x02 ‖ u64_be(L) ‖ MTH(blocks))        -- block mode, L = byte length
```

A size that disagrees with the one the publisher committed to now produces a
root that does not match, and the challenge fails closed. Verification against a
bound root takes the size as an input exactly as before and re-applies the
binding at the end.

Block mode binds the **byte length** rather than the block count, because it is
strictly stronger — it pins the final block's length too — and because the byte
length is what a holder declares about a stored object.

---

## 4. Functional requirements

### Tree and root

- **MTH-F-001 — RFC 6962 tree shape.** The type computes
  `MTH` exactly as [§3.2](#32-the-merkle-tree-hash) defines it, for every leaf
  count, with no fan-out knob. *Acceptance:* every root in
  [Appendix A](#appendix-a--entry-mode-vectors) and
  [Appendix B](#appendix-b--block-mode-vectors) reproduces, including all seven
  non-power-of-two leaf counts on which Bodu 0.2.0 currently differs.

- **MTH-F-002 — Domain separation.** Leaves and internal
  nodes are hashed as [§3.1](#31-hashing-and-domain-separation) defines.
  *Acceptance:* a one-leaf tree's root is `H(0x00 ‖ d)` and **not** `H(d)`; the
  vectors in [Appendix A](#appendix-a--entry-mode-vectors) at *n* = 1 hold.

- **MTH-F-003 — The empty tree.** A tree of zero entries
  has root `H()`; no exception is raised. *Acceptance:* the *n* = 0 vector
  reproduces; the current `InvalidOperationException`
  ([§2.3](#23-the-empty-tree-throws)) is gone from the conforming mode.

- **MTH-F-004 — Entry mode.** Roots may be computed over
  an ordered sequence of variable-length entries. *Acceptance:*
  [Appendix A](#appendix-a--entry-mode-vectors), whose entries are the ones the
  RFC 6962 reference test suite uses, so an implementer can cross-check against
  any transparency-log library.

- **MTH-F-005 — Block mode.** Roots may be computed over a
  byte stream divided into fixed-size blocks per [§3.6](#36-block-mode),
  including a short final block and a zero-length input. *Acceptance:*
  [Appendix B](#appendix-b--block-mode-vectors).

- **MTH-F-006 — Leaf hashes are obtainable.** A caller may
  obtain the ordered leaf hashes of a computation without enabling diagnostics.
  *Rationale:* generating a path requires them, and a consumer that has just
  streamed a 512 MiB object past itself must not be made to stream it again, nor
  to pay the allocation `MerkleTreeDiagnostics` documents as unfit for
  production. *Acceptance:* a streamed computation yields both the root and the
  leaf hashes in one pass, and `AuthenticationPath` accepts those hashes
  directly.

### Proofs

- **MTH-F-007 — Inclusion proofs.** The type generates the
  path for any leaf index of a tree, from entries, from a byte stream, or from
  leaf hashes already computed; and verifies a path against a root, a tree size,
  a leaf index and the leaf's **bytes or hash**. *Acceptance:*
  [Appendix A2](#a2--inclusion-proofs-tree-size-7) and
  [A3](#a3--inclusion-proofs-tree-size-8) reproduce exactly, element for element
  and in order; every leaf of every tree size from 1 to 64 round-trips
  generate-then-verify; and each of the negative cases in
  [§9](#9-conformance-test-plan) is rejected.

- **MTH-F-008 — Consistency proofs.** *(SHOULD.)* The type
  generates and verifies consistency proofs per
  [§3.5](#35-consistency-proofs). *Acceptance:*
  [Appendix A4](#a4--consistency-proofs) reproduces; a proof between sizes that
  do not share a prefix is rejected. Not required by FallbackPlan.

- **MTH-F-009 — Verification is total.** Verification
  returns a boolean for every input it can be given — a leaf index at or beyond
  the tree size, a path longer or shorter than the tree admits, a path element of
  the wrong width, a zero tree size — and throws only for a caller error that
  cannot be a wire value (a null argument). *Rationale:* verifiers sit directly
  behind untrusted input; an exception where a `false` belongs turns a failed
  proof into a denial of service.

### The bound root

- **MTH-F-010 — Length-bound roots.** The type computes and
  verifies against a bound root per
  [§3.7](#37-the-tree-size-ambiguity-and-the-bound-root), as a distinct,
  explicitly-chosen mode. *Acceptance:*
  [Appendix C](#appendix-c--length-bound-roots) reproduces; a verification whose
  declared size differs from the bound one is rejected even when the unbound walk
  would accept it — specifically, the `tree_size = 3` case of
  [Appendix D](#appendix-d--the-ambiguity-and-the-bound).

- **MTH-F-011 — The ambiguity is documented.** The XML
  documentation on the unbound verification entry point states that the tree size
  is trusted input and names the consequence. *Rationale:* a consumer who gets
  the size from the examined party has a soundness bug that no test they are
  likely to write will catch, and the library is the only place that can warn
  them at the point of use.

### Compatibility

- **MTH-F-012 — The existing shape is documented for what
  it is.** `MerkleTreeHash` and `ParallelMerkleTreeHash` state that they
  implement RFC 6962 §2.1 **domain separation** and a level-by-level reduction
  with configurable fan-out that is **not** RFC 6962's tree, and that their roots
  agree with RFC 6962 only when the leaf count is a power of two. *Acceptance:*
  the sentence exists, and the divergence table of
  [§2.1](#21-the-tree-shape-is-not-rfc-6962s) — or a reproduction of it — is in
  the library's test suite as a pinned expectation, so the two shapes cannot
  silently converge or diverge further.

- **MTH-F-013 — Existing roots do not move.** No change
  made for this document alters a root that `MerkleTreeHash` or
  `ParallelMerkleTreeHash` produces today at any `blockSize` or `fanOut`.
  *Rationale:* they are shipped in a 0.2.0 package consumed from a committed feed
  ([ADR-0021](adr/0021-consume-bodu-via-committed-package-feed.md)); a consumer
  may already have persisted one. The conforming behaviour is a **new type or a
  new explicitly-selected mode**, never a fix applied in place.

---

## 5. Non-functional requirements

- **MTH-N-001 — Single-pass streaming.** A root over a byte
  stream is computed in one forward pass, with memory bounded by
  `O(blockSize + log n · hashSize)` for the root alone, and
  `O(blockSize + n · hashSize)` when leaf hashes are retained for path
  generation. *Rationale:* FallbackPlan hashes objects up to 512 MiB and must not
  buffer them; at a 1 MiB block that is 512 leaf hashes, or 16 KiB retained.

- **MTH-N-002 — Determinism.** The root is a pure function
  of (hash algorithm, block size, entry sequence). No wall clock, no environment,
  no parallelism-dependent ordering. *Acceptance:* the parallel and sequential
  implementations of the conforming mode produce identical roots for every
  Appendix vector.

- **MTH-N-003 — Bounded proof acceptance.** Verification
  allocates nothing proportional to attacker-controlled input before validating
  it: a path longer than `ceil(log2(n))` is rejected without walking it, and a
  path element of the wrong width is rejected without hashing it.

- **MTH-N-004 — Fixed-time comparison.** The final
  root comparison uses a fixed-time primitive
  (`CryptographicOperations.FixedTimeEquals`).

- **MTH-N-005 — Algorithm agnosticism.** Any
  `HashAlgorithm` may be configured; nothing assumes 32-byte output. *Acceptance:*
  a SHA-512 tree round-trips generate-then-verify.

- **MTH-N-006 — Thread safety, stated.** Whether an
  instance may be shared across threads is documented explicitly, and the
  proof-generation and verification entry points are either static or safe for
  concurrent use. *Rationale:* a destination answering challenges serves them from
  a connection handler.

- **MTH-N-007 — Managed and dependency-free.** No native
  dependency and no `unsafe` beyond what the BCL hashing primitives already use.
  *Rationale:* FallbackPlan's format-critical closure admits managed, first-party
  code only ([ADR-0019 §2](adr/0019-third-party-dependency-policy.md)), and this
  type would sit inside it.

- **MTH-N-008 — Trimmable and AOT-clean.** No reflection on
  the hot path; the type survives trimming and AOT publication without warnings.

- **MTH-N-009 — Packaged for the committed feed.** Shipped
  in `Bodu.Security.Cryptography` and published as a `.nupkg` suitable for
  FallbackPlan's committed `external/packages` feed
  ([ADR-0021](adr/0021-consume-bodu-via-committed-package-feed.md)).

- **MTH-N-010 — Vectors ship with the library.** The
  Appendix vectors, or a superset, are in the library's own test suite as pinned
  expectations — not only in this document.

---

## 6. Proposed API surface

Illustrative, not prescriptive; the requirements are [§4](#4-functional-requirements)
and [§5](#5-non-functional-requirements).

```csharp
namespace Bodu.Security.Cryptography;

/// <summary>RFC 6962 Merkle Tree Hash, inclusion and consistency proofs.</summary>
public sealed class MerkleTree : IDisposable
{
    public MerkleTree(Func<HashAlgorithm> algorithmFactory);

    /// <summary>The RFC 6962 leaf and node hashes, exposed for callers that build trees themselves.</summary>
    public byte[] HashLeaf(ReadOnlySpan<byte> entry);
    public byte[] HashNode(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right);

    /// <summary>MTH over entries, over leaf hashes, or over blocks of a stream.</summary>
    public byte[] ComputeRoot(IReadOnlyList<ReadOnlyMemory<byte>> entries);
    public byte[] ComputeRootOfLeafHashes(IReadOnlyList<byte[]> leafHashes);
    public MerkleComputation ComputeBlocked(Stream source, int blockSize, CancellationToken ct = default);

    /// <summary>Paths, from entries or from hashes a caller already has (MTH-F-006).</summary>
    public byte[][] AuthenticationPath(IReadOnlyList<ReadOnlyMemory<byte>> entries, long leafIndex);
    public byte[][] AuthenticationPath(IReadOnlyList<byte[]> leafHashes, long leafIndex);

    /// <summary>
    /// Verifies an inclusion proof. <paramref name="treeSize"/> is TRUSTED INPUT:
    /// a caller that obtains it from the party being examined has no soundness
    /// guarantee — see <see cref="VerifyInclusionBound"/> (MTH-F-011).
    /// </summary>
    public bool VerifyInclusion(
        ReadOnlySpan<byte> root, long treeSize, long leafIndex,
        ReadOnlySpan<byte> entry, IReadOnlyList<ReadOnlyMemory<byte>> path);

    /// <summary>As above, against a root that binds the tree size (MTH-F-010).</summary>
    public bool VerifyInclusionBound(
        ReadOnlySpan<byte> boundRoot, long boundValue, long treeSize, long leafIndex,
        ReadOnlySpan<byte> entry, IReadOnlyList<ReadOnlyMemory<byte>> path);

    public byte[] BindRoot(ReadOnlySpan<byte> root, long boundValue);

    /// <summary>Consistency proofs (MTH-F-008, SHOULD).</summary>
    public byte[][] ConsistencyProof(IReadOnlyList<ReadOnlyMemory<byte>> entries, long firstSize);
    public bool VerifyConsistency(
        ReadOnlySpan<byte> firstRoot, long firstSize,
        ReadOnlySpan<byte> secondRoot, long secondSize,
        IReadOnlyList<ReadOnlyMemory<byte>> proof);
}

/// <summary>One streamed computation: the root, and the leaf hashes a path needs.</summary>
public sealed class MerkleComputation
{
    public byte[] Root { get; }
    public long InputLength { get; }
    public int BlockSize { get; }
    public IReadOnlyList<byte[]> LeafHashes { get; }
}

/// <summary>Block arithmetic, so every consumer agrees on it (§3.6).</summary>
public static class MerkleBlocks
{
    public static long BlockCount(long inputLength, int blockSize);
    public static long BlockOffset(long blockIndex, int blockSize);
    public static int  BlockLength(long inputLength, long blockIndex, int blockSize);
}
```

Two shape notes. `MerkleComputation` exists so that
MTH-F-006 is satisfied by the type's
normal return rather than by a diagnostics side channel. `MerkleBlocks` exists
because every consumer of block mode has to compute the same three functions and
getting `BlockLength` wrong on the final block is a silent wrong answer — it is
three lines that belong in one place.

---

## 7. Compatibility and naming

`MerkleTreeHash` and `ParallelMerkleTreeHash` stay exactly as they are
(MTH-F-013). The conforming behaviour
arrives as a new type — `MerkleTree` above, or `Rfc6962MerkleTree` if the
distinction is worth carrying in the name — and the two existing types gain the
documentation correction of
MTH-F-012 and a
cross-reference to it.

A conforming *mode* on the existing types would also satisfy this document, but
is less attractive: `fanOut` has no meaning under RFC 6962, so the mode would
have to refuse a constructor argument the type advertises, and a type whose
`blockSize` means one thing and whose `fanOut` sometimes means nothing is harder
to document than two types.

---

## 8. Test vectors

All values below were **computed** from the definitions in
[§3](#3-normative-definitions) by the script in
[Appendix E](#appendix-e--the-generator), not transcribed from any
implementation or from memory. The distinction matters: this repository has
already shipped a vector written from recall that turned out to be wrong
([`specifications/repository-format/conformance/README.md`](../specifications/repository-format/conformance/README.md)),
and the correction is recorded there.

The entry-mode inputs are the eight entries the RFC 6962 reference test suite
uses, so the roots in [Appendix A](#appendix-a--entry-mode-vectors) are expected
to match any transparency-log implementation. An implementer should treat that
agreement as the primary cross-check and verify it independently rather than
taking this document's word for it.

Hash function throughout: **SHA-256**.

### Appendix A — Entry-mode vectors

Entries, in order (hex; the first is the empty entry):

```text
d(0) = (empty)                      d(4) = 3031
d(1) = 00                           d(5) = 40414243
d(2) = 10                           d(6) = 5051525354555657
d(3) = 2021                         d(7) = 606162636465666768696a6b6c6d6e6f
```

| Entries | `MTH` |
|---|---|
| 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| 1 | `6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d` |
| 2 | `fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125` |
| 3 | `aeb6bcfe274b70a14fb067a5e5578264db0fa9b51af5e0ba159158f329e06e77` |
| 4 | `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |
| 5 | `4e3bbb1f7b478dcfe71fb631631519a3bca12c9aefca1612bfce4c13a86264d4` |
| 6 | `76e67dadbcdf1e10e1b74ddc608abd2f98dfb16fbce75277b5232a127f2087ef` |
| 7 | `ddb89be403809e325750d3d263cd78929c2942b7942a34b77e122c9594a74c8c` |
| 8 | `5dc9da79a70659a9ad559cb701ded9a2ab9d823aad2f4960cfe370eff4604328` |

The *n* = 0 root is `SHA-256` of zero bytes, per
MTH-F-003.

#### A2 — Inclusion proofs, tree size 7

Root: `ddb89be403809e325750d3d263cd78929c2942b7942a34b77e122c9594a74c8c`

| Leaf | Steps | Path (leaf-upward) |
|---|---|---|
| 0 | 3 | `96a296d224f285c67bee93c30f8a309157f0daa35dc5b87e410b78630a09cfc7` · `5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e` · `837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e` |
| 1 | 3 | `6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d` · `5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e` · `837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e` |
| 2 | 3 | `07506a85fd9dd2f120eb694f86011e5bb4662e5c415a62917033d4a9624487e7` · `fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125` · `837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e` |
| 3 | 3 | `0298d122906dcfc10892cb53a73992fc5b9f493ea4c9badb27b791b4127a7fe7` · `fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125` · `837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e` |
| 4 | 3 | `4271a26be0d8a84f0bd54c8c302e7cb3a3b5d1fa6780a40bcce2873477dab658` · `b08693ec2e721597130641e8211e7eedccb4c26413963eee6c1e2ed16ffb1a5f` · `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |
| 5 | 3 | `bc1a0643b12e4d2d7c77918f44e0f4f79a838b6cf9ec5b5c283e1f4d88599e6b` · `b08693ec2e721597130641e8211e7eedccb4c26413963eee6c1e2ed16ffb1a5f` · `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |
| 6 | **2** | `0ebc5d3437fbe2db158b9f126a1d118e308181031d0a949f8dededebc558ef6a` · `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |

Leaf 6's two-step path in a seven-leaf tree is the case that catches a verifier
which assumes a uniform path length.

#### A3 — Inclusion proofs, tree size 8

Root: `5dc9da79a70659a9ad559cb701ded9a2ab9d823aad2f4960cfe370eff4604328`

| Leaf | Steps | Path (leaf-upward) |
|---|---|---|
| 0 | 3 | `96a296d224f285c67bee93c30f8a309157f0daa35dc5b87e410b78630a09cfc7` · `5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e` · `6b47aaf29ee3c2af9af889bc1fb9254dabd31177f16232dd6aab035ca39bf6e4` |
| 1 | 3 | `6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d` · `5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e` · `6b47aaf29ee3c2af9af889bc1fb9254dabd31177f16232dd6aab035ca39bf6e4` |
| 2 | 3 | `07506a85fd9dd2f120eb694f86011e5bb4662e5c415a62917033d4a9624487e7` · `fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125` · `6b47aaf29ee3c2af9af889bc1fb9254dabd31177f16232dd6aab035ca39bf6e4` |
| 3 | 3 | `0298d122906dcfc10892cb53a73992fc5b9f493ea4c9badb27b791b4127a7fe7` · `fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125` · `6b47aaf29ee3c2af9af889bc1fb9254dabd31177f16232dd6aab035ca39bf6e4` |
| 4 | 3 | `4271a26be0d8a84f0bd54c8c302e7cb3a3b5d1fa6780a40bcce2873477dab658` · `ca854ea128ed050b41b35ffc1b87b8eb2bde461e9e3b5596ece6b9d5975a0ae0` · `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |
| 5 | 3 | `bc1a0643b12e4d2d7c77918f44e0f4f79a838b6cf9ec5b5c283e1f4d88599e6b` · `ca854ea128ed050b41b35ffc1b87b8eb2bde461e9e3b5596ece6b9d5975a0ae0` · `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |
| 6 | 3 | `46f6ffadd3d06a09ff3c5860d2755c8b9819db7df44251788c7d8e3180de8eb1` · `0ebc5d3437fbe2db158b9f126a1d118e308181031d0a949f8dededebc558ef6a` · `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |
| 7 | 3 | `b08693ec2e721597130641e8211e7eedccb4c26413963eee6c1e2ed16ffb1a5f` · `0ebc5d3437fbe2db158b9f126a1d118e308181031d0a949f8dededebc558ef6a` · `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |

#### A4 — Consistency proofs

| First size | Second size | Steps | Proof |
|---|---|---|---|
| 1 | 1 | 0 | *(empty)* |
| 1 | 8 | 3 | `96a296d224f285c67bee93c30f8a309157f0daa35dc5b87e410b78630a09cfc7` · `5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e` · `6b47aaf29ee3c2af9af889bc1fb9254dabd31177f16232dd6aab035ca39bf6e4` |
| 2 | 5 | 2 | `5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e` · `bc1a0643b12e4d2d7c77918f44e0f4f79a838b6cf9ec5b5c283e1f4d88599e6b` |
| 3 | 7 | 4 | `0298d122906dcfc10892cb53a73992fc5b9f493ea4c9badb27b791b4127a7fe7` · `07506a85fd9dd2f120eb694f86011e5bb4662e5c415a62917033d4a9624487e7` · `fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125` · `837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e` |
| 4 | 8 | 1 | `6b47aaf29ee3c2af9af889bc1fb9254dabd31177f16232dd6aab035ca39bf6e4` |
| 6 | 8 | 3 | `0ebc5d3437fbe2db158b9f126a1d118e308181031d0a949f8dededebc558ef6a` · `ca854ea128ed050b41b35ffc1b87b8eb2bde461e9e3b5596ece6b9d5975a0ae0` · `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |
| 7 | 8 | 4 | `b08693ec2e721597130641e8211e7eedccb4c26413963eee6c1e2ed16ffb1a5f` · `46f6ffadd3d06a09ff3c5860d2755c8b9819db7df44251788c7d8e3180de8eb1` · `0ebc5d3437fbe2db158b9f126a1d118e308181031d0a949f8dededebc558ef6a` · `d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7` |

### Appendix B — Block-mode vectors

`blockSize = 4`; input is the first *L* bytes of the sequence
`0x00, 0x01, 0x02, …`. The third column is what Bodu 0.2.0's
`MerkleTreeHash(SHA256, blockSize: 4, fanOut: 2)` produces today, for the
divergence table of [§2.1](#21-the-tree-shape-is-not-rfc-6962s).

| *L* | Blocks | `MTH` (required) | Bodu 0.2.0 (current) |
|---|---|---|---|
| 0 | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | *throws* |
| 1 | 1 | `96a296d224f285c67bee93c30f8a309157f0daa35dc5b87e410b78630a09cfc7` | *(same)* |
| 4 | 1 | `a498efa8d0759e7b704095853db9bc1ef8cf65af37bed9d006c4b2917e695061` | *(same)* |
| 5 | 2 | `a568e8449ea491fdb4a261e3e75d2a8562b7c66887ad89cd590df667eae30146` | *(same)* |
| 8 | 2 | `000134e55e67854e1a3d28a78c6392b02623c2505580b0f9d571e0b4d3e2257f` | *(same)* |
| 9 | 3 | `7d0d764930953cf7bda9cfaf927efb27e0c74b7601ea8e5ef87a1b03e7ac85c2` | `30cc4e02e75df443946d7e314cdba6d945e15dfe80e1df0afa95578e4bb134ba` |
| 12 | 3 | `3b469d6b2565374bf3f9d7405627a59e3dba9a3207f3c52b06db08e7190e1f14` | `a8ff2177cdb8f89bb760ae8e20ca64ae92db7a5a84acf125ac8fca371ea2b38f` |
| 13 | 4 | `311ae2a2c5f7b96b71ad76148419619697a19c5d61f894a9dcc254c8363341e9` | *(same)* |
| 16 | 4 | `516c43cb9e4f82fe70437703f0a41649c5fd963ba5f4be8b736dca20bae05bdd` | *(same)* |
| 17 | 5 | `557a6fa29055fbfb4466b8068539aa5e6c9b2c1500624cab414631484e5bc551` | `30b62b0e0ec135a451f000364fd08edd8a38c89772d026f751f707ac3673e0a0` |
| 20 | 5 | `0a30d6148fc3d1c38bc90624148f2021ee9d4a72d2fdcb53dcba91e46237f6d0` | `0892a38103267a72d521a4afac95671df51e8901e625cd26664ffb5c24ecc993` |
| 28 | 7 | `6a648b436a7ba0966d61d03e31033b8e6a5d69dcbf73777afb508ab1f0260a63` | `f9bea195c90f6ee0e71ffca1af2226c7cae2b55e1cee0e3c580ebf8c6c0b2db3` |
| 32 | 8 | `4caa28905e0e6530cced23948c7f3a575f09c618644c66e36f8bd5bc19b2f11c` | *(same)* |
| 33 | 9 | `31dce6ff9c5ac1336d203f99ea214a31eaba3429a3316fdc7eb7dfa1e787e9ec` | `7b29c4db9bf5e233839abffdf1b142eeff5a0e4d07bf64f2be83df6d06230e3d` |

`L = 1` and `L = 4` are both one block and have different roots — the
partial-final-block rule of [§3.6](#36-block-mode) at work. `L = 0` is the
empty tree, not one empty leaf.

### Appendix C — Length-bound roots

`bound_root = SHA-256(0x02 ‖ u64_be(L) ‖ MTH)` over the same inputs as
Appendix B.

| *L* | `bound_root` |
|---|---|
| 1 | `ee8acb9cb20eaf4b807ef586e552774d803cc3dd837bf26e9f509500cc2604bd` |
| 4 | `c39c67e10308a8c59d1ed505e3d5effdfb95919fcf0f27e43b55b943ed8dc583` |
| 5 | `ac3f0c480d6809a0c217a999ddb73789d1995e1b655b88027b6b9b20090cb752` |
| 8 | `c5f132c32e9477705f6d0fa233deaebb338dc1da33b4edf94a9077e0a90e7941` |
| 9 | `0114742dacb57b039e052a558685ad805108cc03fdeb82af5917bb07cd2545e7` |
| 12 | `29f46d734672b404873bd2548b2ab1cf66cf22c5c412fb150331b344cd50332a` |
| 13 | `571c6e2f33df94b2fb8d993f3b4da89a4d1e1570b44bf50875a362e57517d8bb` |
| 16 | `f5ed505e0cb1f0f1cb6909f10211fefe06a7d976b18f9c38e9bdf497802f2b04` |
| 17 | `ac4de219663afebd46a0c39de495cc49abec2c321a1db9ca549c3a5bb2a0eee4` |
| 20 | `8418fd8ce3f3e1895644537a10c7b12a048bb541789045420bdd6ece2251ee0a` |
| 28 | `0847c78802618221fc7da3a3b64bcab9524558efe3115e6a7cb807185b008d1e` |
| 32 | `9fe7554b5c4798e70be59bf08e95550caca6e3d1032065e14ec4879d0438771e` |
| 33 | `e269c9dcf6ca5202ed027971c97e81db585373aeff5897c629a07b4d16f04951` |

### Appendix D — The ambiguity and the bound

`blockSize = 4`, `L = 16` — four full blocks.

```text
MTH          = 516c43cb9e4f82fe70437703f0a41649c5fd963ba5f4be8b736dca20bae05bdd
path(leaf 0) = 5ab0680027d25adbc39635ae41b3bfdbd9b3c4bd829899d1ba9c845155e604f0
               d5ebbc39e8bd0dceb9f7120ba588f91a386714bf51e320373f667927231666da

VerifyInclusion(MTH, treeSize = 4, leaf 0, block 0, path)  ->  true    (correct)
VerifyInclusion(MTH, treeSize = 3, leaf 0, block 0, path)  ->  true    (the hole)

bound_root(L = 16) = f5ed505e0cb1f0f1cb6909f10211fefe06a7d976b18f9c38e9bdf497802f2b04
bound_root(L = 12) = 29f46d734672b404873bd2548b2ab1cf66cf22c5c412fb150331b344cd50332a
                     -- different, so the understated size fails closed
```

A conforming implementation MUST reproduce both the `true` on line 1 and the
`true` on line 2 — the second is not a bug to be fixed in the unbound
verifier, it is the RFC's algorithm behaving as specified — and MUST reject the
understated size when verifying against the bound root
(MTH-F-010).

### Appendix E — The generator

The script that produced Appendices A–D is at
`eng/bodu-merkle-vectors.py` in this repository. It depends on nothing but the
Python standard library, so any implementer can reproduce every value here
without installing anything and without trusting either implementation.

---

## 9. Conformance test plan

A conforming implementation passes all of the following. Cases marked **†** are
the ones that distinguish a correct implementation from a plausible one.

**Roots**

1. Every vector in Appendices A and B, including *n* = 0 and the short final
   block. **†** the seven non-power-of-two leaf counts.
2. Three leaves split 2 + 1, not 1 + 2: `MTH` of three leaves equals
   `node(node(l0, l1), l2)` and **not** `node(l0, node(l1, l2))`. **†**
3. A one-leaf root equals `H(0x00 ‖ d)` and not `H(d)`. **†**
4. A lone subtree root is promoted, not re-hashed: the three-leaf root is
   **not** `node(node(l0, l1), H(0x01 ‖ l2))`. **†** *(This is the exact
   difference from Bodu 0.2.0.)*
5. Streaming in arbitrary slice sizes — one byte at a time, prime-sized chunks,
   the whole buffer at once — yields the same root.
6. The parallel and sequential implementations agree on every vector.

**Inclusion proofs**

7. Appendices A2 and A3 reproduce element for element **and in order**.
8. Generate-then-verify round-trips for every leaf of every tree size 1…64.
9. A tree of one leaf produces an empty path, and that path verifies. **†**
10. Rejected: a leaf index equal to or greater than the tree size; a path with a
    step removed; a path with a step appended; any single path element altered
    by one bit; the correct path against a different leaf's bytes; a path
    element of the wrong width; a tree size of zero.
11. **†** The leaf-6-of-7 case: a short path in a non-perfect tree verifies.
12. No exception is thrown for any input in case 10
    (MTH-F-009).

**Consistency proofs** *(if implemented)*

13. Appendix A4 reproduces.
14. Round-trip for every `(m, n)` with `1 ≤ m ≤ n ≤ 32`.
15. Rejected: a proof between roots of trees that do not share a prefix; an
    altered step; a wrong first or second size.

**Bound roots**

16. Appendix C reproduces.
17. **†** Appendix D: the unbound verifier accepts the understated size and the
    bound verifier rejects it.

**Non-functional**

18. A 512 MiB stream at a 1 MiB block completes without the peak working set
    growing with the input (MTH-N-001).
19. A SHA-512 tree round-trips (MTH-N-005).
20. The divergence table of [§2.1](#21-the-tree-shape-is-not-rfc-6962s) is
    pinned against the existing types
    (MTH-F-012).

---

## 10. Non-goals

- **Signing the root.** The commitment is a hash; who signs it, under what key
  and with what envelope is the consumer's. FallbackPlan signs it inside an
  index delta under a repository-derived Ed25519 key, and that scheme has no
  business in a Merkle type.
- **Transport or serialisation.** Path encoding on a wire is the consumer's
  format. FallbackPlan's is a CBOR array of 32-byte strings with a bound on the
  count.
- **Storage of trees or proofs.** No caching layer, no persistence.
- **Sparse Merkle trees, Bitcoin-style duplicated-last-leaf trees, or
  certificate-transparency's higher-level structures (STHs, SCTs).** Different
  shapes and different problems; a type that tried to be all of them would be
  configurable in exactly the way [§2.4](#24-fan-out-is-a-knob-where-the-standard-has-none)
  warns against.
- **Changing what `MerkleTreeHash` computes today**
  (MTH-F-013).

## 11. Adoption in FallbackPlan

If this lands upstream, `Repository.Packing/BlobMerkle` becomes a thin binding
rather than an implementation: the format's leaf size (1 MiB), the format's
preimage (`[0, blob_length − 16)`), and the length-bound root, over a library
primitive. `BlobMerkleAccumulator` is replaced by the streamed computation of
MTH-F-006, and
`AuthenticationPath`/`VerifyLeaf` forward.

What does **not** change is the on-disk and on-wire format: the tree this
document specifies is the tree
[repository-format 05 §5.2](../specifications/repository-format/05-blob.md#52-the-merkle-commitment)
already publishes, so adoption moves no bytes and needs no format version. That
is the point of specifying the library against the standard rather than
specifying FallbackPlan against the library.

Two consumer-side wins beyond deleting code:

- `ParallelMerkleTreeHash`'s pipeline, in a conforming mode, would speed up the
  destination side of a possession challenge, which today hashes up to 512 MiB
  single-threaded to produce one path.
- `MerkleBlocks` removes the final-block arithmetic from three call sites in
  FallbackPlan that each have to get it right.

Adoption would be recorded as an amendment to
[ADR-0065](adr/0065-merkle-commitment-and-chunk-possession.md) and a feed bump
under [ADR-0021](adr/0021-consume-bodu-via-committed-package-feed.md); until
then `BlobMerkle` stands and the format is unaffected either way.

## 12. Open questions for the maintainer

1. **New type or new mode?** [§7](#7-compatibility-and-naming) argues for a new
   type; the maintainer owns the call.
2. **Is the one-child-node promotion in the existing types intentional?** It is
   a defensible construction, but if it was not a deliberate choice it may be
   worth a `fanOut`-2 conforming default in a future major rather than only a
   documentation note.
3. **Should `BindRoot` be in the library at all?** It is four lines a consumer
   could write. The argument for including it is that the *failure* it prevents
   took building a system to notice ([§3.7](#37-the-tree-size-ambiguity-and-the-bound-root)),
   and a library that ships the verifier is the right place to ship the warning.
4. **Is `long` the right index and size type?** FallbackPlan's trees have at
   most 512 leaves; a transparency log has billions. `long` costs nothing and
   avoids a breaking change later.
5. **Consistency proofs now or later?** They are SHOULD here because no current
   consumer needs them, but they are cheap alongside inclusion proofs and
   expensive to retrofit into a frozen API.

# Open questions

**Status:** live document

Decisions that are deliberately unresolved, who owns them, and what they block. An ADR in `Proposed` state carries the analysis; this page tracks the ones still needing a human answer.

---

## Q1 — Project licence

**Owner:** project maintainer · **Blocks:** first public release, any CrashPlan code reuse, external contributions · **ADR:** [0001](adr/0001-licence-and-contribution-model.md)

The document describes an open-source project and never names a licence. The choice has a consequence beyond the usual, because it determines whether GPL-licensed prior art — potentially including CrashPlan reader implementations — could ever be reused.

| Option | Consequence |
|--------|-------------|
| Apache-2.0 | Permissive, explicit patent grant. Best for third-party readers and distro packaging. **Cannot absorb GPL code** — the importer would need clean-room implementation. |
| MPL-2.0 | File-level copyleft. Combinable with proprietary code. Still cannot absorb GPL code. |
| GPL-3.0 / AGPL-3.0 | Strong copyleft. **Could reuse GPL reader code directly.** Deters some commercial adoption and embedding. |

**Recommendation:** Apache-2.0, with the CrashPlan importer clean-roomed. The format's value depends on independent implementations existing, and a patent grant plus permissive terms maximises that. The importer is isolated in its own package precisely so this choice does not have to be made to suit it ([`architecture/11-solution-structure.md` §4](architecture/11-solution-structure.md#4-import-isolation)).

**Not decided.** Recorded as open at the maintainer's direction.

---

## Q2 — Plan C licence and reuse posture

**Owner:** project maintainer, with legal review · **Blocks:** Phase 5 · **ADR:** [0015](adr/0015-crashplan-importer-isolation.md)

The original proposal cites Plan C as evidence that CrashPlan archives can be read, and instructs that its licence be reviewed before any reuse. **That licence has not been verified** — it could not be checked from the environment this review was produced in, and it is not asserted anywhere in this document set.

Required before any parser work:

1. Verify Plan C's licence and its compatibility with Q1's answer.
2. Decide reuse posture: direct reuse (if compatible), documentation-only reference, or full clean-room with an independent implementer who has not read the source.
3. Confirm the interoperability position for reverse engineering in the target jurisdictions.

**Constraint regardless of the answer:** no parser work begins before this gate passes. Reading source under an incompatible licence contaminates the clean-room option permanently, so the sequence matters more than the speed.

---

## Q3 — Product name and trademark

**Owner:** project maintainer, with legal review · **Blocks:** first public release · **Review finding:** [M7](review/2026-08-architecture-review.md#m7--naming-proximity-to-crashplan-carries-trademark-risk)

"FallbackPlan" shares a structure, a domain, and a rhyme with "CrashPlan", and the project's most visible advertised capability is reading CrashPlan archives. That combination is what makes a confusion argument easy to state.

This is a flag rather than a legal opinion. It should be assessed alongside Q2 — the same review, the same lawyer — while renaming is still cheap. It becomes expensive the moment a repository format, a wire protocol, and a package name carry it.

---

## Q4 — Canonical metadata encoding

**Owner:** engineering · **Blocks:** format v1 freeze · **ADR:** [0003](adr/0003-canonical-metadata-encoding.md)

Canonical CBOR is the candidate. Confirm with cross-language determinism tests and an encoding-size benchmark on realistic manifests before the format freezes. The requirement it has to satisfy is that an independent implementer, in another language, produces byte-identical output from the same logical input.

---

## Q5 — Segmentation default

**Owner:** engineering · **Blocks:** format v1 freeze · **ADR:** [0002](adr/0002-segmentation-strategy.md)

`fixed-v1` is the v1 default and `cdc-v1` is specified alongside it. The corpus benchmark decides whether the default changes. Deferring this past the freeze — as originally planned — would have meant discovering the answer after users had committed data ([H1](review/2026-08-architecture-review.md#h1--fixed-size-segmentation-is-under-argued-and-its-review-is-scheduled-after-the-point-of-no-return)).

Needed: a corpus that actually represents the target workloads — documents, photographs, VM images, source trees, mailboxes — rather than synthetic data, which flatters CDC and fixed-size in different and misleading ways.

---

## Q6 — Segment hash function

**Owner:** engineering · **Blocks:** format v1 freeze · **ADR:** [0004](adr/0004-segment-hash-function.md)

SHA-256 in-box versus a BLAKE3 native binding. The trade is throughput against NFR-PORT-001 — a native binding adds a platform-specific dependency to the one component that must run everywhere, including the standalone recovery tool. Recommendation is SHA-256 as the default with the profile field allowing another later; confirm against benchmark.

---

## Q7 — Performance targets

**Owner:** engineering · **Blocks:** nothing; revised continuously

The targets in [`requirements/non-functional.md`](requirements/non-functional.md) are initial and some will be wrong. Revise after Phase 0 benchmarks, and **record each revision** rather than silently editing — a target that quietly moves to meet the measurement is not a target.

---

## Q8 — Reference-machine definition

**Owner:** engineering · **Blocks:** benchmark comparability

The reference machine in [`requirements/non-functional.md`](requirements/non-functional.md#reference-machine) is a starting point. It needs to be pinned to something reproducible — a specific CI runner class or documented hardware — before published numbers mean anything across time.

---

## Q9 — Repository-server scope

**Owner:** product · **Blocks:** Phase 2 design detail

Repository-server mode is described as an ownership model but its administrative surface is undefined: multi-user households, quota delegation, policy locks, and per-device grants. Enough is specified for the format to be correct; the product surface is not.

---

## Q10 — Padding policy

**Owner:** engineering, with security review · **Blocks:** nothing in v1

[`threat-model.md` T-11](threat-model.md#t-11-metadata-side-channels) proposes optional record padding for high-sensitivity backup sets. Bucket granularity, storage cost, and whether it is per-set or per-repository are undecided. It is optional in v1, so this can wait — but the format should be checked to confirm it can express padding without a version bump.

---

## Q11 — Physical hints in segment references

**Owner:** project maintainer · **Blocks:** format v1 freeze · **ADR:** [0007](adr/0007-logical-object-identifiers-in-manifests.md) · **Finding:** [PT-10](review/2026-08-fix-pressure-test.md#pt-10--emergency-single-file-restore-regressed-from-one-fetch-to-a-full-scan)

Manifests carry logical object identifiers only, and that decision is confirmed. What is open is whether a segment reference should *also* carry a non-authoritative `last_known_blob` hint.

**Why it matters.** With no index, recovering a single file means scanning blob footers — hours at scale **M** for one document, against roughly one fetch if a hint were present. That is the emergency-recovery path, so it is the worst place to be slow.

**Why the original rejection does not hold.** ADR-0007 dismissed hints as "a correctness question dressed up as an optimisation". Record headers are independently authenticated and carry the object identifier, so a reader following a stale hint **detects** it and falls back to the index. Detectably stale is not silently wrong.

**What still argues against it.** It partially re-couples manifests to physical layout, and invites implementations that trust the hint without validating. The mitigation is a mandatory stale-hint conformance fixture.

Either answer preserves the core decision: because the hint may go stale, compaction still touches no manifest.

| Option | Trade |
|--------|-------|
| **Add the hint** | O(1) first-byte recovery with no index; a few bytes per segment reference; a stale-hint fixture becomes mandatory |
| **No hint** | Manifests stay purely logical; emergency single-file recovery relies on prioritised footer scanning (NFR-PERF-015) |

**Recommendation:** add it, with mandatory validation and a conformance fixture. The cost is small and bounded; the benefit lands exactly when the user is in the worst position.

---

## Q12 — XChaCha20-Poly1305 has no second implementation to check against

**Owner:** engineering, with security review · **Blocks:** format v1 freeze · **ADR:** [0019](adr/0019-third-party-dependency-policy.md) · **Specification:** [03 §6.1](../specifications/repository-format/03-keys.md#61-where-each-primitive-comes-from)

The format admits two AEAD profiles. `aes-256-gcm-v1` uses a platform primitive. `xchacha20-poly1305-v1` cannot: .NET provides `ChaCha20Poly1305` (RFC 8439, 12-byte nonce) and **not** the 24-byte extended-nonce variant, so that profile requires a third-party implementation.

Argon2id is in the same position and is handled: it is cross-verified against a second independent implementation on every CI run, which is how the empty-passphrase gap in [03 §2.1](../specifications/repository-format/03-keys.md#21-the-passphrase-is-constrained-too-and-the-primitive-will-not-do-it-for-you) was found. **XChaCha20-Poly1305 has no such check**, because no second implementation was available to check against.

An unverified AEAD is worse than an unverified KDF. A KDF defect makes keys weaker; an AEAD defect can make ciphertext forgeable or, with a nonce-handling bug, make plaintext recoverable — and by the time anyone notices, it is in the user's stored bytes.

| Option | Trade |
|--------|-------|
| **Find a second implementation and cross-verify** | Matches the Argon2id posture. Depends on one existing and being maintained. |
| **Drop the profile before freeze** | `aes-256-gcm-v1` alone is sufficient and 03 §6.1 already lets an implementer omit the extended-nonce profile. Costs the non-AES-hardware performance case. |
| **Ship it unverified, flagged** | Cheapest now, and the option that ages worst — an unverified primitive is hardest to remove after repositories exist that use it. |

**Recommendation:** decide at the freeze gate, and prefer dropping the profile over shipping it unverified. It costs nothing while unused, but a format version cannot un-admit a profile once written repositories depend on it.

**Not decided.**

---

## Q13 — Device-level signature attribution

**Owner:** engineering, with security review · **Blocks:** nothing in v1 · **ADR:** [0020](adr/0020-ed25519-signing-key-semantics.md) · **Finding:** the signing key derives from the shared master key, so signatures cannot attribute anything to a single device

[ADR-0020](adr/0020-ed25519-signing-key-semantics.md) settled format v1: signatures are **repository-scoped** — they prove "a holder of the master key at generation *g* produced this" and no more. `device_id` and `writer_id` are attribution by claim, tamper-evident once signed but chosen freely by the signer. One repository member impersonating another is undetectable cryptographically; the mitigation is the writer-identity conflict alert ([T-18](threat-model.md#t-18-writer-identity-cloning)).

What remains open is whether a later format version should add **per-device signing keys**: device keypairs, an enrolment flow, a public-key registry object with its own integrity rules, and revocation. That buys real attribution — a compromised member can no longer sign as its neighbours — at the cost of a new object type and a trust bootstrap the current format deliberately avoids.

| Option | Trade |
|--------|-------|
| **Stay repository-scoped** | No new surface; multi-user trust rests on the conflict alert and on not admitting untrusted members |
| **Per-device keys + registry** | True attribution and member revocation; new object type, enrolment, registry integrity, key-loss recovery per device |

**Recommendation:** revisit when repository-server mode (Q9) is designed — multi-user households are where attribution starts to matter, and the two designs share an administrative surface.

**Not decided.**

---

## Q14 — Minimum passphrase length, and the globalization dependency behind normalisation

**Owner:** engineering, with security review · **Blocks:** nothing in v1 · **Spec:** [03 §2.1](../specifications/repository-format/03-keys.md#21-the-passphrase-is-constrained-too-and-the-primitive-will-not-do-it-for-you)

Specification 03 §2.1 requires refusing an empty passphrase (implemented — `Passphrase.Create` refuses it at the engine level, because Argon2id itself accepts one) and says an implementation SHOULD enforce a minimum length, refusing rather than warning. No number is specified. The engine carries `Passphrase.RecommendedMinimumLength = 12` as a named constant but does not yet enforce it — picking the number is a policy decision that deserves one deliberate pass (length alone versus a strength estimate, and what the recovery story says to a user whose old passphrase no longer meets the bar).

A related build decision is recorded here so it is not silently re-made: `InvariantGlobalization` was removed from `Directory.Build.props` (it had been set for reproducibility hardening) because NFC normalisation of passphrases — mandatory per 03 §2 — throws `PlatformNotSupportedException` for non-ASCII input in invariant mode. Correctness beat the hardening; the runtime now carries ICU. If invariant mode is ever wanted back, passphrase normalisation needs a vendored NFC path first.

**Not decided** (the minimum length); the globalization removal is decided and recorded above.

---

## Q15 — Record ordinal in the AAD versus byte-identical relocation

**Owner:** engineering · **Blocks:** any compaction implementation · **ADR:** [0022](adr/0022-standalone-metadata-records-and-index-identifiers.md) · **Spec:** [04 §2.1](../specifications/repository-format/04-record.md#21-field-constraints), [04 §4](../specifications/repository-format/04-record.md#4-associated-data)

Specification 04 §4 excludes the blob identifier from the record AAD so that compaction can relocate a record "without re-encrypting it" — the enabling property for [ADR-0007](adr/0007-logical-object-identifiers-in-manifests.md)'s manifests-stay-logical rule. But the AAD **does** include `ordinal`, and 04 §2.1 requires the ordinal to equal the record's zero-based position in its blob. A record copied byte-identically into a new blob generally lands at a different position, so it cannot simultaneously keep its authenticated ordinal and satisfy the position rule. The two statements are in live contradiction.

Nothing in phase 0 hits this — compaction is not implemented, and the precedence engine ([ADR-0017](adr/0017-index-entry-supersession.md)) handles supersession entries regardless of who produced them. It must be resolved before the first compaction pass exists. The options:

| Option | Trade |
|--------|-------|
| **Relax 04 §2.1 for compacted blobs** — a relocated record keeps its original ordinal; the footer's record table already binds ordinal to offset, so lookup is unaffected. Ordinals in a compacted blob are non-contiguous | Preserves zero-decrypt relocation; weakens the "position = ordinal" invariant readers may be tempted to assume; nonce uniqueness unaffected (the moved record keeps its original blob key) |
| **Compaction re-encrypts** — moved records are decrypted and re-sealed under the destination blob's key at their new ordinal | Preserves 04 §2.1 as written; makes compaction a cryptographic operation (key access, CPU over every moved byte) and voids 04 §4's stated rationale for excluding the blob id |

**Not decided.**

---

## Q16 — The blob digest has no home in the index

**Owner:** engineering · **Blocks:** replication receipts (verify level 2 at scale) · **ADR:** [0022](adr/0022-standalone-metadata-records-and-index-identifiers.md) · **Spec:** [05 §5](../specifications/repository-format/05-blob.md#5-sealing), [07 §2](../specifications/repository-format/07-index.md#2-index-delta)

05 §5 says the blob digest is "recorded in the index and used for end-to-end verification during replication", but no index delta or checkpoint field carries it — 07's entry array has no digest position and its object-level keys have none either. Phase 0 records the digest in the **catalogue** (`blobs.digest`, populated at seal and by verify level 2), which serves single-machine verification but is device-local and disposable — it cannot serve as a replication receipt another participant can check.

The candidate format fix is an optional parallel array on the delta (`covered_blob_digests`, aligned with `covered_blob_ids`, inside the signed prefix), which would make digests durable, signed, and discoverable exactly where the covered blobs are declared. That is a format change and waits for a format-change window; the 05 §5 sentence carries an erratum note meanwhile.

**Not decided** (the format-level carriage; the catalogue-domain recording is implemented).

---

## Q17 — Lease, tombstone, and audit-period object formats

**Owner:** engineering · **Blocks:** garbage collection implementation (post-phase-0) · **ADR:** [0022](adr/0022-standalone-metadata-records-and-index-identifiers.md)

Three namespaces in [01 §2](../specifications/repository-format/01-object-layout.md#2-namespace) have no object format anywhere in the specification: `/leases/<scope>/<lease-id>` (semantics in [08 §9](../specifications/repository-format/08-journal.md#9-leases) — advisory only, the sole mutable namespace — but no record shape), `/tombstones/<object-type>/<object-id>` (named by the deletion discipline in 01 §5 and 07 §7, shape undefined), and `/audit/<period>/<record-id>` (distinct from audit *journal* records, which live at `/journal/<writer-id>/<sequence>`; nothing defines the period rendering or the object). None are needed by phase 0 — no phase-0 component takes a lease, tombstones an object, or writes the audit-period namespace — so their formats are deliberately not invented here. They must be specified before the garbage collector exists, since all three belong to its surface.

**Not decided.**

---

## Closed

| Question | Resolution |
|----------|-----------|
| Do manifests carry physical locations? | No — logical object identifiers only ([ADR-0007](adr/0007-logical-object-identifiers-in-manifests.md)) |
| How is nonce uniqueness guaranteed? | Per-blob key derivation, record ordinal as nonce ([ADR-0005](adr/0005-aead-suite-and-nonce-construction.md)) |
| Is cross-device dedup safe by default? | Yes — `repository` is the default and verifies on reuse; `device` is the hardened opt-in ([ADR-0006](adr/0006-object-identifiers-and-dedup-trust-domains.md)) |
| How does GC avoid deleting in-flight blobs? | Write-intent journal records; leases are advisory ([ADR-0009](adr/0009-garbage-collection-safety.md)) |
| Does an offline destination block snapshots? | No — commit is per-replica ([ADR-0011](adr/0011-commit-versus-replication-semantics.md)) |
| How is a checkpoint conflict resolved? | Both retained, both applied, under explicit generation precedence ([ADR-0008](adr/0008-index-generations-and-checkpoints.md), [ADR-0017](adr/0017-index-entry-supersession.md)) |
| What is in the recovery kit? | Specified ([ADR-0013](adr/0013-recovery-kit.md)) |
| How are blob identifiers formed? | Writer-allocated and opaque, not content-derived ([ADR-0016](adr/0016-blob-identifier-formation.md)) |
| What happens when two index entries map one object? | Highest generation wins; relocations typed as supersessions ([ADR-0017](adr/0017-index-entry-supersession.md)) |
| Does `protected` require an offsite copy? | Yes — a replica outside the source's failure domain ([ADR-0018](adr/0018-replica-failure-domains.md)) |
| Is the local database disposable? | The catalogue is; device identity and pairings are not ([ADR-0010](adr/0010-local-store-separation.md)) |

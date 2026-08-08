# Open questions

**Status:** live document

Decisions that are deliberately unresolved, who owns them, and what they block. An ADR in `Proposed` state carries the analysis; this page tracks the ones still needing a human answer.

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

**Closed (2026-08): a hint exists, and it is not in the manifest.** See [ADR-0007 Amendment](adr/0007-logical-object-identifiers-in-manifests.md) and [specification 06 §10](../specifications/repository-format/06-manifests.md).

The question assumed the only place to put a hint was beside the segment reference, and neither it nor ADR-0007 recorded what that would cost. A manifest is identified by its own bytes, and the specification states that identical bytes for identical content across devices is what makes cross-device deduplication possible — so a device-specific hint inside a manifest would have disabled that quietly, in a way no single-device test could catch.

The hint is a separate optional object per snapshot instead. Emergency recovery gets its fast path, manifests stay byte-identical across devices, and absence is the normal case a reader already handles.

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

## Q18 — Streaming restored content to a remote client

**Owner:** product · **Blocks:** Phase 2 console work

[ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md) §6 withholds file content from remote clients by default: a restore commanded from a console is written on the machine running the service. Whether to offer the opt-in — and what approval it should demand — is undecided. It is the difference between "administer a fleet" and "read a fleet", and the answer probably differs between a household and a managed estate.

---

## Q19 — Console identity and multi-operator access

**Owner:** product · **Blocks:** Phase 2 console work

A console pairs with each service it manages, and pairing is revocable at the service. What is undecided is what happens above that: whether several operators may share one console, whether a service can distinguish them, and whether an action taken through a console is attributable to a person rather than to the console's device identity. Shares an administrative surface with [Q9](#q9--repository-server-scope) and [Q13](#q13--device-level-signature-attribution), and should be settled with them rather than separately.

---

## Closed

| Question | Resolution |
|----------|-----------|
| Do manifests carry physical locations? | No — logical object identifiers only ([ADR-0007](adr/0007-logical-object-identifiers-in-manifests.md)) |
| Q21 — the source-identity hint grew with the repository, not with the change | Resolved by keying it on the **source key** rather than the snapshot: `/hints/identity/<shard>/<source-key>/<captured-at>/<snapshot-id>`, one small object per file version created, found by listing one prefix whose entries are chronological. The per-snapshot map it replaced described the whole tree every run, at a measured ~52 bytes per file; a one-file change to a 1 024-file tree now adds 7 386 bytes to the store against ~57 200 before. The accepted price is object count — one store object, and on a metered store one request, per changed file — which is the per-object overhead blobs exist to amortise, and it is the cheaper side from the second capture onward ([ADR-0007 Amendment 2](adr/0007-logical-object-identifiers-in-manifests.md), [06 §11](../specifications/repository-format/06-manifests.md#11-source-identity)) |
| Q12 — should `xchacha20-poly1305-v1` ship while unverified? | No — the profile is **withdrawn** and `0x0002` is reserved, never to be assigned to another suite. Cross-verification against a second independent implementation is the condition on which a third-party primitive is admitted here, and none existed for XChaCha20-Poly1305. An unverified AEAD is a different order of risk from an unverified KDF, and it would be discovered inside bytes the user had already stored; a format version can add a profile but cannot un-admit one that written repositories depend on. The cost — slower on hardware without AES acceleration — is accepted ([ADR-0005 Amendment 4](adr/0005-aead-suite-and-nonce-construction.md), [03 §6.1](../specifications/repository-format/03-keys.md#61-where-each-primitive-comes-from)) |
| Q16 — where does the blob digest live? | On the index delta, as `covered_blob_digests` — an optional array parallel to `covered_blob_ids`, inside the signature ([07 §2.2](../specifications/repository-format/07-index.md#22-covered-blob-digests)). A replication receipt has to be checkable by the participant receiving the blob, and the catalogue is device-local, so it stays as a cache and this is the durable copy. Optional because the format's integrity rests on per-record AEAD tags; a **present** digest that does not match is a damage finding, and absence is not |
| Q17 — what shape are leases, tombstones and audit-period records? | Specified as [11 — Lifecycle objects](../specifications/repository-format/11-lifecycle-objects.md), with types `0x0D`, `0x0E`, `0x0F`. Only the **tombstone** is signed, because only it authorises anything; its grace is counted in index generations rather than wall time, since the format has no trusted clock. A lease authorises nothing and may be ignored. An audit-period record carries counts and keyed identifiers and no path, name or content hash, because it is the object most likely to be exported in a diagnostic bundle |
| Q11 — should a segment reference carry a `last_known_blob` hint? | No. A hint exists, as a separate optional object per snapshot; in the manifest it would have made the same content encode differently per device and broken cross-device dedup ([ADR-0007 Amendment](adr/0007-logical-object-identifiers-in-manifests.md), [06 §10](../specifications/repository-format/06-manifests.md)) |
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
| Can compaction relocate records byte-identically? | No — compaction decrypts and re-seals; the AAD and its ordinal are unchanged ([ADR-0025](adr/0025-compaction-reseals-records.md)) |
| What licence does the project carry? | Dual: code AGPL-3.0-only + commercial licences from the maintainer; `specifications/` Apache-2.0 so independent readers stay unencumbered ([ADR-0001](adr/0001-licence-and-contribution-model.md)) |
| Where does the concurrency default sit, and does per-record spool pinning survive measurement? | Both answered by measurement, not opinion. `Concurrency` stays at **2**: 360.8 MiB/s at 2 against 356.9 at 4, because a single-threaded reader and a single-threaded ordering barrier leave four logical cores little to spare. Pinning **survives**, moved from per record to per blob — one `fsync` and one sidecar write per blob, at which price the question of whether it earns its cost does not arise ([ADR-0029](adr/0029-pipeline-and-service-concurrency.md) §6, [phase-2 benchmarks](phase-2-benchmarks.md)) |

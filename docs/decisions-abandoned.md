# Abandoned choices

**Status:** maintained · **Checked by:** [`eng/check-adr-status.py`](../eng/check-adr-status.py)

---

Every decision record ends with the options it turned down, and each of those sections is written where it is useful to whoever is reading that one decision. Nobody reads thirty of them to answer "did anyone consider X, and what happened?" — so this is that index.

It also holds something the ADRs structurally cannot. An "alternatives considered" section lists roads not taken. It does not list roads *walked down and abandoned* — positions that were the plan, shipped in a document, and were then given up. Those are the more valuable record, because someone will propose them again, and the second time they will sound new.

**Each entry is one line and a citation.** The full argument lives where it was made; duplicating it here would create a second copy to keep true, and the copy would lose.

---

## Positions abandoned after they were the plan

These were adopted, written down, and then given up. Each cost something to reverse, which is the point of recording it.

| Position | Why it was abandoned | Where |
|----------|---------------------|-------|
| Manifests embed the physical location of the blobs they reference | Compaction moves records; an immutable manifest that names a location can never be corrected, so the format contradicted its own maintenance story | [C1](review/2026-08-architecture-review.md#c1--immutable-manifests-embed-physical-locations-that-compaction-changes) → [ADR-0007](adr/0007-logical-object-identifiers-in-manifests.md) |
| Nonce uniqueness guaranteed by assertion | It was required everywhere and constructed nowhere; a scheme that says "nonces must be unique" without saying how is a scheme with reused nonces | [C2](review/2026-08-architecture-review.md#c2--nonce-uniqueness-is-asserted-but-never-constructed) → [ADR-0005](adr/0005-aead-suite-and-nonce-construction.md) |
| Cross-device dedup verified at restore time | Detection arrives after the source data is gone, which is the one moment it is worth nothing | [C3](review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard) → [ADR-0006](adr/0006-object-identifiers-and-dedup-trust-domains.md) |
| Blocking leases protect in-flight blobs from the collector | A lease makes the race less likely without closing it; leases were demoted to advisory and write intents made the actual guard | [C4](review/2026-08-architecture-review.md#c4--garbage-collection-can-delete-blobs-belonging-to-an-in-flight-snapshot) → [ADR-0009](adr/0009-garbage-collection-safety.md) |
| A snapshot commits globally, across all destinations | One offline destination then stalls all protection, so a laptop away from its home peer is unprotectable exactly when backup matters. *(Partially reinstated, knowingly: a direct-ship set with zero reachable destinations refuses to capture — not a return to global commit, since any one reachable destination suffices, but the "unprotectable while away" failure mode is accepted for those sets in exchange for holding no local copy — [ADR-0011 Amendment 4](adr/0011-commit-versus-replication-semantics.md#amendment-4-2026-08--commit-re-unifies-with-the-destinations-for-direct-ship-sets))* | [C5](review/2026-08-architecture-review.md#c5--snapshot-commit-is-defined-so-that-one-offline-destination-stalls-all-protection) → [ADR-0011](adr/0011-commit-versus-replication-semantics.md) |
| Checkpoint compaction reads a complete store listing | The design elsewhere forbids relying on listings being complete; the two could not both be true | [C6](review/2026-08-architecture-review.md#c6--checkpoint-compaction-requires-a-complete-listing-the-design-forbids-relying-on) → [ADR-0008](adr/0008-index-generations-and-checkpoints.md) |
| Spool checkpoints record a plaintext offset for resume | It assumes recompression is bit-reproducible; a crash, an upgrade and a resume would reuse a nonce over different plaintext — the exact catastrophe C2 exists to prevent | [PT-1](review/2026-08-fix-pressure-test.md#pt-1--c2s-resume-guarantee-silently-assumes-bit-reproducible-compression) |
| Index merge is commutative, so order does not matter | C1 made it false: compaction remaps an object identifier, and order then decides which mapping wins | [PT-2](review/2026-08-fix-pressure-test.md#pt-2--c6s-commutativity-claim-is-false-once-c1-is-in-place) → [ADR-0017](adr/0017-index-entry-supersession.md) |
| The collector is exempt from publishing write intents | It creates blobs during compaction, so a second collector could delete them; the rule is now that *any* component creating a blob publishes an intent first | [PT-3](review/2026-08-fix-pressure-test.md#pt-3--compaction-output-blobs-are-unprotected-between-creation-and-index-publication) |
| `device` as the default dedup trust domain | The stated rationale did not distinguish it from `repository`, which is safer; the default changed rather than the argument being improved | [PT-11](review/2026-08-fix-pressure-test.md#pt-11--the-stated-rationale-for-the-device-dedup-default-does-not-distinguish-it-from-repository) |
| The CLI and the Agent are peer hosts over a shared library | Two processes sharing a state directory share a writer identity, and therefore the single gapless sequence space the format requires | [ADR-0027 Amendment](adr/0027-services-scheduling-status-telemetry.md#amendment-2026-08-the-peer-host-model-is-superseded) → [ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md) |
| The staging archive as every set's publication target | The owner rejected the local copy outright — backups are written to destinations directly, nothing cached or temporarily stored — trading the capture-never-blocks durability property for a machine that holds metadata only; staging survives for unflagged sets until the `direct_ship` default flips | [ADR-0034 §1](adr/0034-hub-and-spoke-destinations.md#1-each-backup-set-publishes-into-its-own-staging-archive) → [ADR-0046](adr/0046-direct-to-destination-publication.md) |
| Schedule arithmetic computed by hand | The daily branch mixed the argument's clock with a machine-timezone conversion — right on a UTC machine, a day wrong everywhere else, and invisible to a UTC-only CI matrix | [ADR-0027 §1 Amendment](adr/0027-services-scheduling-status-telemetry.md#amendment-2026-08-05-the-arithmetic-moves-to-a-shared-library) |
| Konscious as the shipped Argon2id implementation | Keeping it would leave exactly one implementation with no oracle; swapping which library ships and keeping the other as a cross-check buys a real comparison for free | [ADR-0019](adr/0019-third-party-dependency-policy.md#alternatives-considered) |
| Bodu consumed as a git submodule | No amount of documentation makes a ZIP download contain a submodule, and the Windows flow was a requirement rather than an option | [ADR-0021](adr/0021-consume-bodu-via-committed-package-feed.md) |
| TLS 1.3 with RFC 7250 raw public keys, X.509 prohibited | Unreachable on the reference platform — no raw-public-key API, no keying-material exporter, no usable Ed25519 certificate — so authentication moved into the protocol and kept the guarantee | [ADR-0030 Amendment 1](adr/0030-peer-identity-and-pairing.md#amendment-1-2026-08--authentication-moves-out-of-tls) |
| A session hello offering one protocol version | The specification required a refusal naming "both ranges", which one number cannot express; an operator could not tell which side needed upgrading | [peer-protocol 02 §5](../specifications/peer-protocol/02-session.md#5-protocol-version) |
| A short authentication string of words from a wordlist | The document specified six words from a thousand-word list, argued the security of three, and carried no list; six base32 characters says what it means and needs nothing shipped | [peer-protocol 01 §2.6](../specifications/peer-protocol/01-identity-and-pairing.md) |
| Two of the phase-0 exit criteria | One needed the phase-5 legacy reader and a corpus we do not have; the other needed a second implementation to exist before the format was drafted | [H2](review/2026-08-architecture-review.md#h2--two-phase-0-exit-criteria-cannot-be-met-at-phase-0) → [roadmap](roadmap.md#phase-0--archive-engine-vertical-slice) |

---

## Roads not taken

Grouped by what they would have changed. One line each; the reasoning is at the citation.

### Format and encoding

| Choice | Why not | Where |
|--------|---------|-------|
| Content-defined chunking as the v1 default | Complicates deterministic fixtures and cross-language conformance at the moment both are being established, and loses on in-place-rewrite workloads | [0002](adr/0002-segmentation-strategy.md#alternatives-considered) |
| Fixed-size only, CDC deferred entirely | Schedules an irreversible format decision after the point of no return | [0002](adr/0002-segmentation-strategy.md#alternatives-considered) |
| Per-file heuristic segmentation | Reproducibility and fixture determinism become much harder, with no evidence yet about which heuristic is right | [0002](adr/0002-segmentation-strategy.md#alternatives-considered) |
| Protocol Buffers for repository objects | proto3 is explicitly not canonical: identical logical input can produce different bytes, and identifiers are derived from bytes | [0003](adr/0003-canonical-metadata-encoding.md#alternatives-considered) |
| A bespoke binary format | Every independent implementer would reimplement it from prose with no library, against NFR-COMP-004 | [0003](adr/0003-canonical-metadata-encoding.md#alternatives-considered) |
| JSON with JCS canonicalisation | Number handling disagrees across languages, and the size cost at scale is unacceptable for metadata | [0003](adr/0003-canonical-metadata-encoding.md#alternatives-considered) |
| BLAKE3 as the segment hash | Deferred, not rejected — revisit if hashing proves the binding constraint and a trimmable implementation removes the portability objection | [0004](adr/0004-segment-hash-function.md#alternatives-considered) |
| SHA-512/256 | Less universally available in other languages, and the machines it helps are the ones least likely to be the reference case | [0004](adr/0004-segment-hash-function.md#alternatives-considered) |
| A non-cryptographic hash, verified elsewhere | Dedup decisions are made on this identifier, so a collision is a data-corruption path | [0004](adr/0004-segment-hash-function.md#alternatives-considered) |
| A gear-hash / FastCDC function | A different algorithm family from the one the specification names — a spec change, not a parameter pin | [0023](adr/0023-cdc-v1-rabin-parameters.md#alternatives-considered) |
| A random irreducible polynomial per deployment | A format needs one fixed value, and a bespoke constant is unverifiable against the literature | [0023](adr/0023-cdc-v1-rabin-parameters.md#alternatives-considered) |
| Version the format by a single integer | Cannot express partial capability, so a reader supporting most of a version must refuse everything | [0014](adr/0014-format-versioning-and-stability.md#alternatives-considered) |
| Guarantee forward compatibility from the first public build | Freezes the format before the segmentation benchmark and before any independent implementation has stressed it | [0014](adr/0014-format-versioning-and-stability.md#alternatives-considered) |
| gitignore-compatible rules | Its semantics are defined by one implementation's behaviour, not a specification; freezing them as format surface is a research project | [0024](adr/0024-include-exclude-rule-dialect.md#alternatives-considered) |
| Glob only, no regex | Leaves literal-`*` paths unexpressible forever, and adding an escape hatch later is a dialect revision | [0024](adr/0024-include-exclude-rule-dialect.md#alternatives-considered) |
| Full host regex | Flag semantics differ across engines, and catastrophic backtracking is an input-driven denial of service inside a backup agent | [0024](adr/0024-include-exclude-rule-dialect.md#alternatives-considered) |

### Cryptography

| Choice | Why not | Where |
|--------|---------|-------|
| Random 96-bit nonces under a shared key | Needs a birthday-bound budget tracked across all writers and all time, and nobody will track it | [0005](adr/0005-aead-suite-and-nonce-construction.md#alternatives-considered) |
| A nonce counter partitioned by writer ID | Needs durable per-writer counter state surviving crashes and clones; a cloned device reuses its partition | [0005](adr/0005-aead-suite-and-nonce-construction.md#alternatives-considered) |
| XChaCha20-Poly1305 with random nonces throughout | Viable, but AES-GCM is much faster with AES-NI, and per-blob derivation makes both suites safe uniformly | [0005](adr/0005-aead-suite-and-nonce-construction.md#alternatives-considered) |
| The `xchacha20-poly1305-v1` **profile itself**, as a second record AEAD | Withdrawn at the freeze gate: no second implementation existed to cross-verify against, and an unverified AEAD cannot be un-admitted once repositories depend on it. Value `0x0002` stays reserved. Costs the non-AES-hardware performance case | [0005 Amendment 4](adr/0005-aead-suite-and-nonce-construction.md#amendment-4--the-extended-nonce-profile-is-withdrawn) |
| Shipping that profile unverified with a warning | The warning does not travel with the bytes; the defect would be found inside data the user had already stored | [0005 Amendment 4](adr/0005-aead-suite-and-nonce-construction.md#amendment-4--the-extended-nonce-profile-is-withdrawn) |
| Per-device content-ID keys | Makes cross-device dedup impossible rather than optional, with no security gain over the `device` domain | [0006](adr/0006-object-identifiers-and-dedup-trust-domains.md#alternatives-considered) |
| Signed segment records attributing each to its writer | Attribution without prevention: it names who corrupted the data after the fact | [0006](adr/0006-object-identifiers-and-dedup-trust-domains.md#alternatives-considered) |
| Raw-scalar Ed25519 seed interpretation | No mainstream API consumes it, manual clamping is an error magnet, and nothing is gained | [0020](adr/0020-ed25519-signing-key-semantics.md#alternatives-considered) |
| Per-device signing keys with a public-key registry | The honest long-term answer for attribution; rejected for v1 as an object type, an enrolment flow, revocation semantics and a registry needing its own integrity story — see [Q13](open-questions.md#q13--device-level-signature-attribution) | [0020](adr/0020-ed25519-signing-key-semantics.md#alternatives-considered) |
| Drop signatures from v1 entirely | AEAD tags do not travel with the object graph; the snapshot signature is what binds it | [0020](adr/0020-ed25519-signing-key-semantics.md#alternatives-considered) |
| Write Argon2id or an AEAD ourselves | Forbidden by specification 03 §1, and it is the defect class this project cannot recover from | [0019](adr/0019-third-party-dependency-policy.md#alternatives-considered) |
| A bare master key in the recovery kit | Single-factor, and a printed kit becomes as sensitive as the data — which no user will treat it as | [0013](adr/0013-recovery-kit.md#alternatives-considered) |
| A recovery kit including store credentials | A printout in a drawer would grant access to a cloud account | [0013](adr/0013-recovery-kit.md#alternatives-considered) |
| Shamir-split recovery kits | Deferred, not rejected — useful for high-value repositories, unjustified complexity for the consumer default | [0013](adr/0013-recovery-kit.md#alternatives-considered) |
| A digital-only recovery kit | A kit stored on the machine being backed up is not a recovery kit | [0013](adr/0013-recovery-kit.md#alternatives-considered) |

### Concurrency, indexing and safety

| Choice | Why not | Where |
|--------|---------|-------|
| A single global index rewritten periodically | Reproduces the monolithic-manifest failure the project exists to avoid | [0008](adr/0008-index-generations-and-checkpoints.md#alternatives-considered) |
| Listing-based compaction with a settle delay | A delay makes a silent data-loss race less likely without eliminating it | [0008](adr/0008-index-generations-and-checkpoints.md#alternatives-considered) |
| Leader election for compaction | Needs a coordination primitive object stores do not uniformly provide; merging needs none | [0008](adr/0008-index-generations-and-checkpoints.md#alternatives-considered) |
| Last-writer-wins on conflicting checkpoints | Needs a trusted clock, and discards the losing checkpoint's coverage | [0008](adr/0008-index-generations-and-checkpoints.md#alternatives-considered), [0017](adr/0017-index-entry-supersession.md#alternatives-considered) |
| Refuse to collect while any writer is active | On a multi-device repository some writer is nearly always active, so collection never runs | [0009](adr/0009-garbage-collection-safety.md#alternatives-considered) |
| Reference-count blobs at upload | Mutable counters on an eventually consistent store need atomic increment, and a crashed writer leaks counts permanently | [0009](adr/0009-garbage-collection-safety.md#alternatives-considered) |
| Collect only blobs older than a fixed age | An age safe for a slow initial backup is long enough to make collection useless — and it is still a clock | [0009](adr/0009-garbage-collection-safety.md#alternatives-considered) |
| Content-derived blob identifiers | Incompatible with write intents, and nothing addresses a blob by its content | [0016](adr/0016-blob-identifier-formation.md#alternatives-considered) |
| Store-assigned blob identifiers | Not all providers return one, it cannot be known before upload, and it makes the object graph depend on provider behaviour | [0016](adr/0016-blob-identifier-formation.md#alternatives-considered) |
| Forbid compaction, keep physical locations | Compaction is how space is reclaimed; without it the repository grows monotonically and retention becomes advisory | [0007](adr/0007-logical-object-identifiers-in-manifests.md#alternatives-considered), [0017](adr/0017-index-entry-supersession.md#alternatives-considered) |
| Rewrite manifests on compaction | Abandons immutability, invalidates snapshot signatures, and makes maintenance proportional to history | [0007](adr/0007-logical-object-identifiers-in-manifests.md#alternatives-considered) |
| An indirection object between manifest and blob | Redundant — the index already is that indirection | [0007](adr/0007-logical-object-identifiers-in-manifests.md#alternatives-considered) |
| A `last_known_blob` hint inside the segment reference | It makes a manifest device-specific, and manifests are identified by their bytes — identical content on two devices would stop deriving one object id, disabling cross-device dedup silently. The hint moved to its own object instead | [0007 Amendment](adr/0007-logical-object-identifiers-in-manifests.md) |
| Deterministic per-writer delta identifiers | Leaks writer identity into the store namespace, which the format forbids | [0022](adr/0022-standalone-metadata-records-and-index-identifiers.md#alternatives-considered) |
| Wrap standalone objects in a one-record blob | ~200 bytes of framing whose fields are meaningless at one record, and index objects would enumerate as blobs | [0022](adr/0022-standalone-metadata-records-and-index-identifiers.md#alternatives-considered) |
| Parallelise across files rather than within one | Puts concurrent callers straight onto the non-re-entrant append path — the catastrophic race | [0029](adr/0029-pipeline-and-service-concurrency.md#alternatives-considered) |
| A blob writer per worker | Legal, and deferred — it multiplies open spools, in-flight intents and partially filled blobs for an unmeasured gain | [0029](adr/0029-pipeline-and-service-concurrency.md#alternatives-considered) |
| Add threads first, measure later | The pipeline was doing ~128 fsyncs per blob; concurrency multiplies that work rather than removing it | [0029](adr/0029-pipeline-and-service-concurrency.md#alternatives-considered) |
| Declare NFR-PERF-002 aspirational | A requirement nobody intends to meet should be deleted, not left implying a guarantee | [0029](adr/0029-pipeline-and-service-concurrency.md#alternatives-considered) |
| Relax the position invariant for compacted blobs | Preserves zero-decrypt relocation only for transplants the key schedule cannot express anyway | [0025](adr/0025-compaction-reseals-records.md#alternatives-considered) |
| Drop the record ordinal from the AAD | A real format change that surrenders in-blob reordering protection and still enables nothing | [0025](adr/0025-compaction-reseals-records.md#alternatives-considered) |

### Capture, scheduling and status

| Choice | Why not | Where |
|--------|---------|-------|
| Let the scanner decide each capture shape as it meets it | Ten shapes decided under implementation pressure is ten chances to write a byte into a pre-freeze format because it was convenient that afternoon | [0026](adr/0026-phase-1-capture-shapes.md#alternatives-considered) |
| Derive `hardlink_group` from the source inode number | A stable filesystem identifier, visible to a destination that holds only ciphertext everywhere else | [0026](adr/0026-phase-1-capture-shapes.md#alternatives-considered) |
| Capture special files by reading their content | A FIFO has none, a device node's content is the device, and opening one can block forever | [0026](adr/0026-phase-1-capture-shapes.md#alternatives-considered) |
| A free-text capture note instead of `key: value` diagnostics | A restore planner would be pattern-matching English to decide whether a file was captured inconsistently | [0026](adr/0026-phase-1-capture-shapes.md#alternatives-considered) |
| Cron expressions for schedules | Makes the common case expressible several ways, for an audience whose schedule is "every few hours" or "overnight" | [0027](adr/0027-services-scheduling-status-telemetry.md#alternatives-considered) |
| Replay every missed run after downtime | Five missed runs and one leave the repository in the same place, so four are pure cost | [0027](adr/0027-services-scheduling-status-telemetry.md#alternatives-considered) |
| A durable, transactional job history | A second store with its own consistency obligations, holding data that is explicitly disposable | [0027](adr/0027-services-scheduling-status-telemetry.md#alternatives-considered) |
| Ship an OpenTelemetry exporter in the box | An exporter is a deployment decision with a package identity and a network destination | [0027](adr/0027-services-scheduling-status-telemetry.md#alternatives-considered) |
| Derive user-facing status from the job state machine | `Running` is not `Protected`; a status saying what the software is doing answers a question nobody asked | [0027](adr/0027-services-scheduling-status-telemetry.md#alternatives-considered) |

### Storage, state and portability

| Choice | Why not | Where |
|--------|---------|-------|
| One local database, with the durability requirement narrowed to tables | The property becomes unenforceable, and any instruction that deletes the file destroys the device identity | [0010](adr/0010-local-store-separation.md#alternatives-considered) |
| Device identity in the recovery kit | A stolen kit could impersonate the device to its destinations, and a new identity is the correct recovery model | [0010](adr/0010-local-store-separation.md#alternatives-considered) |
| Configuration stored in the repository | Leaks backup-set names and destination endpoints into a store treated as untrusted | [0010](adr/0010-local-store-separation.md#alternatives-considered) |
| A seekable-stream requirement instead of a content factory | Forces every caller to materialise content even when it could be regenerated more cheaply | [0012](adr/0012-storage-provider-contract.md#alternatives-considered) |
| Result types everywhere, no exceptions | Forces every caller to handle transport faults inline where an exception is the right tool | [0012](adr/0012-storage-provider-contract.md#alternatives-considered) |
| Provider-specific interfaces | How provider capabilities leak into repository semantics, which NFR-COMP-005 forbids | [0012](adr/0012-storage-provider-contract.md#alternatives-considered) |
| Infer a replica's failure domain from the destination type | A network path gives no reliable signal about physical location, and a wrong inference is worst where the stakes are highest | [0018](adr/0018-replica-failure-domains.md#alternatives-considered) |
| Require an independent replica before any backup runs | Hostile: a local-only backup is worth having, and refusing to start pushes users to no backup | [0018](adr/0018-replica-failure-domains.md#alternatives-considered) |

### Peers, the service boundary and identity

| Choice | Why not | Where |
|--------|---------|-------|
| Derive peer identity from the master key | Can only authenticate repository members, so a destination would need the keys to the data it stores | [0030](adr/0030-peer-identity-and-pairing.md#alternatives-considered) |
| X.509 with a project-run certificate authority | Introduces an authority the design otherwise lacks, and there is no name worth validating | [0030](adr/0030-peer-identity-and-pairing.md#alternatives-considered) |
| Trust on first use with no confirmation | Ignores an attacker present at pairing — the one moment an attacker most wants to be present | [0030](adr/0030-peer-identity-and-pairing.md#alternatives-considered) |
| A shared secret typed on both machines | Becomes the weakest part of the system, and gives no way to pin an identity for later sessions | [0030](adr/0030-peer-identity-and-pairing.md#alternatives-considered) |
| Make every local store multi-process-safe | Four independent mechanisms that must each stay correct forever | [0028](adr/0028-service-boundary-and-deployment-topologies.md#alternatives-considered) |
| Give each process its own writer identity | Writer identities are device-scoped and carry authorisation, journal chains and rollback detection | [0028](adr/0028-service-boundary-and-deployment-topologies.md#alternatives-considered) |
| Loopback TCP with a token file for the local binding | A credential to talk to your own machine, and a port any local process may reach | [0028](adr/0028-service-boundary-and-deployment-topologies.md#alternatives-considered) |
| HTTP for both bindings | Makes every single-machine install carry a listening port and a credential it has no use for | [0028](adr/0028-service-boundary-and-deployment-topologies.md#alternatives-considered) |
| A shared secret or API token for remote clients | A phishable, copyable credential that unlocks other people's machines, with rotation as the only revocation | [0028](adr/0028-service-boundary-and-deployment-topologies.md#alternatives-considered) |
| A console holding repository keys and reading content centrally | Concentrates every managed machine's plaintext in one place — the property the design refuses to concede | [0028](adr/0028-service-boundary-and-deployment-topologies.md#alternatives-considered) |
| Client supplies the passphrase for every operation | Makes scheduled unattended backup impossible, which is the Agent's whole purpose | [0028](adr/0028-service-boundary-and-deployment-topologies.md#alternatives-considered) |
| Unlock once per boot, held in memory | A reboot silently stops backups until a human returns | [0028](adr/0028-service-boundary-and-deployment-topologies.md#alternatives-considered) |

### Dependencies, build and scope

| Choice | Why not | Where |
|--------|---------|-------|
| Adopt Bodu across all three candidate areas | Two of them are the wrong shape — a document container is not a storage abstraction, and a non-cryptographic hash is not a segment hash | [0019](adr/0019-third-party-dependency-policy.md#alternatives-considered) |
| Drop XChaCha20-Poly1305 from the format | Genuinely open, deliberately undecided — it costs nothing while unused, and the freeze gate is the right point | [0019](adr/0019-third-party-dependency-policy.md#alternatives-considered) |
| Vendor Bodu by copying sources | Severs upstream history, makes provenance unauditable, and turns every upstream fix into a manual merge | [0019](adr/0019-third-party-dependency-policy.md#alternatives-considered) |
| Vendor a Bodu source subset in-tree | Imports a second build system's props, targets and analyzers into every build | [0021](adr/0021-consume-bodu-via-committed-package-feed.md#alternatives-considered) |
| `git subtree` merge | Imports a 126 MB monorepo and its history to use two libraries | [0021](adr/0021-consume-bodu-via-committed-package-feed.md#alternatives-considered) |
| Publish Bodu to nuget.org | The cleanest end state, but not this repository's decision — the committed feed migrates to it in one commit if it happens | [0021](adr/0021-consume-bodu-via-committed-package-feed.md#alternatives-considered) |
| Put a legacy importer in the core | Couples the project's licence to the importer's dependencies and puts a hostile-input parser inside the trust boundary | [0015](adr/0015-legacy-importer-isolation.md#alternatives-considered) |
| Begin legacy parser work in parallel with legal review | Precisely the sequencing error the gate exists to prevent | [0015](adr/0015-legacy-importer-isolation.md#alternatives-considered) |
| Skip legacy import entirely | Considered seriously as the highest-risk feature; kept because isolation makes the risk containable and dropping it later costs nothing | [0015](adr/0015-legacy-importer-isolation.md#alternatives-considered) |
| MPL-2.0, GPL and other licence shapes | Weighed in a table against third-party readers, GPL reuse, proprietary forks and distro packaging; the promise that a user can leave depends on independent implementations existing | [0001](adr/0001-licence-and-contribution-model.md#options) |

---

## What is deferred rather than rejected

Worth separating, because a deferred option is a decision someone still has to make.

| Option | Waiting on |
|--------|-----------|
| BLAKE3 as the segment hash | Evidence that hashing is the binding constraint, plus a trimmable implementation | 
| CDC as the segmentation default | The benchmark at the [format freeze gate](adr/0002-segmentation-strategy.md) |
| Shamir-split recovery kits | A kit format version, if high-value repositories justify it |
| Per-device signing keys | [Q13](open-questions.md#q13--device-level-signature-attribution) — whether device-level attribution should exist at all |
| A blob writer per worker | Measurement showing the barrier is the bound ([ADR-0029](adr/0029-pipeline-and-service-concurrency.md)) |
| RFC 7250, TLS exporters, Ed25519 certificates | Platform support; the protocol-layer proof stays authoritative either way ([ADR-0030 Amendment 1](adr/0030-peer-identity-and-pairing.md#amendment-1-2026-08--authentication-moves-out-of-tls)) |
| The peer write adapter for direct-ship sets | The sink serving peer destinations (today a stated `NotSupported` in the ledger, never a silent skip) — one of the three tails gating the `direct_ship` default flip, with the retention-with-trimming drill ([ADR-0046](adr/0046-direct-to-destination-publication.md)) |

---

**See also:** [Implementation status](implementation-status.md) — what is built · [Open questions](open-questions.md) — what is still undecided · [Architecture review](review/2026-08-architecture-review.md) and [pressure test](review/2026-08-fix-pressure-test.md) — where most of the abandonments were argued

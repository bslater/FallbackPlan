# Non-functional requirements

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §3.5, §19 · **Resolves:** [H5](../review/2026-08-architecture-review.md#h5--there-are-no-quantitative-performance-targets-anywhere), [M4](../review/2026-08-architecture-review.md#m4--whole-requirement-categories-are-missing)

---

## About the numbers

The original document contained no quantitative target anywhere: §19 was entirely directional ("saturating typical consumer storage without excessive CPU") and NFR-PERF-007 named scales without outcomes, so a benchmark could be declared a pass regardless of what it measured.

The values below are **initial targets**, and some of them will turn out to be wrong. That is fine and expected: a wrong number gets corrected by the first benchmark that contradicts it, whereas an adjective never gets corrected at all. Each is revised once Phase 0 reports real measurements, and each revision is recorded rather than silently applied.

### Reference machine

Unless a requirement says otherwise, targets are measured on:

> 8 physical cores with AES-NI · 32 GB RAM · NVMe SSD sustaining ≥ 2 GB/s sequential read · Linux, Windows, and macOS all measured · repository on local NVMe · default profiles (1 MiB `fixed-v1` segments, 128 MiB blobs, Zstd, AES-256-GCM).

### Reference scales

| Scale | Files | File versions | Segment references | Logical size |
|-------|-------|---------------|-------------------|--------------|
| S | 100 000 | 1 M | 5 M | 500 GB |
| M | 1 M | 10 M | 50 M | 5 TB |
| L | 10 M | 100 M | 500 M | 50 TB |

---

## Performance

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-PERF-001 **[changed]** | Memory shall be bounded by configured concurrency, segment size, and blob-buffer limits — not by file size, file count, version count, or repository size. | Agent peak RSS ≤ 1 GB at scale **L** with default profiles. Processing a 2 TiB single file adds ≤ 256 MB over idle. |
| NFR-PERF-002 | The pipeline shall parallelise read, hash, compress, encrypt, and write while preserving deterministic segment order. | Restored bytes identical regardless of concurrency setting. |
| NFR-PERF-003 **[changed]** | Unchanged files and segments shall avoid payload reads and writes when identity, size, and mtime match and the prior version's content identifiers are known. | An incremental scan of an unchanged tree at scale **M** reads ≤ 1% of logical bytes. |
| NFR-PERF-004 **[changed]** | Catalogue path resolution shall not require remote enumeration. | p99 ≤ 10 ms at scale **M**; p99 ≤ 50 ms at scale **L**. |
| NFR-PERF-005 | Metadata growth shall be incremental; one snapshot shall not rewrite metadata proportional to repository size. | Metadata written per snapshot is proportional to changed entries, within 10%. |
| NFR-PERF-006 | Cloud operation shall minimise transaction amplification by packing segments into blobs. | See NFR-PERF-008. |
| NFR-PERF-007 **[changed]** | Single-stream capture throughput on the reference machine. | ≥ 400 MB/s on incompressible data; ≥ 250 MB/s on mixed compressible data, both including hashing, compression, and encryption. |
| NFR-PERF-008 **[new]** | Object-store PUT requests per GB written, at default blob size. | ≤ 10 PUTs/GB for data blobs; ≤ 20 total requests/GB including index and metadata. |
| NFR-PERF-009 **[new]** | Object-store GET requests during a restore, where the store supports range reads. | ≤ 1.2 × the number of distinct blobs holding the required segments. |
| NFR-PERF-010 **[new]** | Catalogue deduplication lookup latency. | p99 ≤ 1 ms at scale **M**; ≤ 5 ms at scale **L**. |
| NFR-PERF-011 **[new]** | Catalogue size per file version. | ≤ 400 bytes/file version at scale **M**, so scale **L** stays under ~40 GB — a number that must fit on a consumer laptop. |
| NFR-PERF-012 **[new]** | Forensic rebuild rate from blob recovery footers. | ≥ 500 blobs/s from local NVMe; scale **M** rebuilds in ≤ 2 hours. |
| NFR-PERF-013 **[new]** | Background activity shall observe configured CPU, disk, network, and time-window limits. | With a 25% CPU cap, measured agent CPU stays ≤ 30% over any 60 s window. |
| NFR-PERF-014 **[new]** | **Repository-side** index size per distinct segment object. Physical location moved from manifests into the index, so this is the structure that grew and nothing previously bounded it. | ≤ 80 bytes per distinct segment object after checkpoint compaction. A reader resolving one file fetches only the shards covering its segments, never the whole index. |
| NFR-PERF-015 **[new]** | Targeted forensic recovery: time to first restored file when all index objects are lost, with prioritised footer scanning. | ≤ 10 minutes at scale **M** for a single named file, against the ≥ 2 hours a full rebuild takes. |

## Reliability and recoverability

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| NFR-REL-001 | Process termination, restart, network loss, out-of-space, and provider timeouts at any step shall not make a committed snapshot unreadable. | Interruption harness covers every persistence boundary in [`../architecture/04-concurrency-and-publication.md` §5.1](../architecture/04-concurrency-and-publication.md#51-interruption-at-each-step). |
| NFR-REL-002 **[changed]** | **The catalogue** shall be treated as a cache. Deleting or corrupting it shall not cause repository data loss. *(Scoped to the catalogue — see NFR-REL-007.)* | Delete the catalogue, rebuild, restore successfully. |
| NFR-REL-007 **[new]** | Durable local state — device keypair, pairing grants, destination authorisations — is **not** rebuildable from the repository and shall be stored separately from the catalogue. | Deleting the catalogue leaves device identity and pairings intact. |
| NFR-REL-003 | Full catalogue rebuild shall be possible from format metadata, blob recovery footers, index generations, and snapshot roots. | Forensic rebuild succeeds with every index object deleted. |
| NFR-REL-004 | Corruption shall be localised to the smallest practical unit, and unaffected snapshots shall remain available. | One corrupt record affects only the file versions referencing it. |
| NFR-REL-005 | Maintenance operations shall be resumable, cancellable, idempotent where practical, and safe to repeat. | Each operation interrupted at every step converges on re-run. |
| NFR-REL-006 | Enough redundant structural metadata shall be retained to recover when global index objects are lost, without duplicating user payload for metadata recovery. | Blob footers suffice; payload is stored once. |
| NFR-REL-008 **[new]** | Recovery-tool artifacts for every supported major format version shall remain downloadable and buildable from published source. | Reproducible build verified per release. |

## Security and privacy

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| NFR-SEC-001 | Content, filenames, paths, metadata, manifests, and content identifiers shall be encrypted or keyed before leaving the trust boundary. | No plaintext path or raw content hash appears in any stored object. |
| NFR-SEC-002 | Only reviewed AEAD suites and approved profiles shall be accepted. Insecure selection shall not exist as a compatibility switch. | Unapproved suites rejected at configuration time. |
| NFR-SEC-003 **[changed]** | Nonce uniqueness shall be structural: a fresh CSPRNG salt per blob, a key derived per blob, and the record ordinal as nonce, per [`../architecture/03-crypto.md` §3](../architecture/03-crypto.md#3-nonce-and-key-construction). | Property test: no `(key, nonce)` pair repeats across any generated sequence. Resume produces byte-identical blobs; restart produces a different salt. *N* concurrent writers produce pairwise-distinct salts. |
| NFR-SEC-004 | A compromised store or relay shall not decrypt content, enumerate plaintext paths, or confirm possession of a known plaintext through raw identifiers. | All stored identifiers are keyed. |
| NFR-SEC-005 | Repository metadata and writer publications shall be authenticated so rollback, substitution, truncation, and identity conflicts are detectable. | Each attack class is detected by the corruption suite. |
| NFR-SEC-006 **[changed]** | Secrets shall not reach logs, telemetry, crash dumps, manifests, or configuration exports. Redaction shall be **by declared type**, not string pattern. | A newly added secret-bearing field is redacted without any filter-list change. |
| NFR-SEC-007 **[amended]** | Cross-device deduplication shall not permit one writer to corrupt another's backups without detection. | The default (`repository`) verifies on reuse before referencing another writer's segment (FR-DED-003); `device` avoids cross-writer reuse entirely. |
| NFR-SEC-008 **[new]** | Key separation shall not depend on CSPRNG quality alone. Writer identity and a monotonic per-writer counter shall be bound into per-blob key derivation. | Two writers seeded with an identical CSPRNG stream still derive distinct blob keys. |
| NFR-SEC-009 **[new]** | Key material shall never cross the command surface in either direction, and unlocked key material shall be confined to the service account. Operations that mint new access shall re-derive the key-encryption key from a user-supplied passphrase per invocation. | No command or event carries key material. Holding a running service is not sufficient to export a recovery kit ([T-19](../threat-model.md), [ADR-0028](../adr/0028-service-boundary-and-deployment-topologies.md) §9). |
| NFR-PRIV-001 **[new]** | No telemetry shall leave the device without explicit opt-in. | Default build transmits nothing; verified by network capture. |
| NFR-PRIV-002 **[new]** | Enabled telemetry shall exclude paths, filenames, repository identifiers, destination endpoints, and anything derived from file content. | Payload schema review plus automated assertion. |
| NFR-PRIV-003 **[new]** | Diagnostic bundles shall exclude credentials, keys, plaintext paths, and correlatable repository identifiers by default. | Bundle inspection test. |
| NFR-TIME-001 **[new]** | No correctness property shall depend on wall-clock agreement between machines. | Skew injection of ±24 h changes no GC, publication, or index outcome. |
| NFR-TIME-002 **[new]** | Grace periods shall carry a configured skew margin, and observed skew shall be recorded in snapshot manifests. | Skew is queryable per snapshot. |

## Compatibility and evolvability

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| NFR-COMP-001 | Repository, blob, record, manifest, index, and encryption profiles shall be independently versioned and discoverable. | Each version is readable from `/repository-format` without decrypting content. |
| NFR-COMP-002 | Existing repositories shall remain readable when new segmentation, compression, or encryption profiles are introduced. | Old fixtures read by new builds. |
| NFR-COMP-003 | Unknown **required** features shall cause safe refusal; unknown optional fields shall follow documented preservation or ignore rules. | A fixture with an unknown required feature is refused with a named reason, never misread. |
| NFR-COMP-004 | The format and conformance fixtures shall be documented sufficiently for an independent restore implementation. | A reader written from the specification alone, by an author who did not write the format, in a different language, passes the fixtures. This gates format v1 freeze. |
| NFR-COMP-005 | Provider capabilities shall not leak into snapshot or file-version semantics. | A repository written to one provider restores identically from another. |
| NFR-COMP-006 **[new]** | Peers of differing agent versions shall negotiate a common feature set or refuse with a stated reason. Neither shall enter an undefined state mid-transfer. | Version-skew matrix tested across supported versions. |
| NFR-COMP-007 **[new]** | Pre-1.0 repository formats carry **no** forward-compatibility guarantee. Builds shall warn at repository creation, and each breaking change shall ship a migration tool or an explicit statement that re-seeding is required. | Warning present; [ADR-0014](../adr/0014-format-versioning-and-stability.md) governs. |

## Operability and usability

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| NFR-OPS-001 | The engine shall expose pipeline throughput, queue depth, deduplication and compression ratios, blob utilisation, catalogue state, verification state, and destination durability. | All NFR-PERF targets are observable in production, not only in benchmarks. |
| NFR-OPS-002 **[amended]** | Status shall distinguish `captured`, `protected`, `replicated`, `verified`, `policy-compliant`, `degraded`, and `unrecoverable`. | No UI surface merges `degraded` with `unrecoverable`, or `captured` with `protected`. |
| NFR-OPS-003 | Configuration shall be schema-versioned, validated before use, and exportable without secrets. Effective values shall be preserved per snapshot. | An invalid config is rejected with a named field before any job starts. |
| NFR-OPS-004 | Defaults shall be safe on consumer hardware while permitting advanced overrides. | Defaults meet NFR-PERF-013 on a 4-core laptop. |
| NFR-OPS-005 | Recovery shall be possible from a clean machine using only the repository, the recovery software, and the recovery kit. | End-to-end test on a machine with no prior state. |
| NFR-OPS-006 **[new]** | A client shall present a service it cannot reach as stale with the age of last contact — never as healthy, and never as failed. Aggregated views shall derive from per-set, per-destination detail that stays reachable. | A console with an unreachable service shows staleness, not a green tick; no roll-up produces a state outside the [10 §1.1](../architecture/10-observability.md#11-states-must-be-distinguishable) vocabulary (NFR-OPS-002 still holds across machines). |
| NFR-UX-001 **[new]** | User-facing surfaces shall meet WCAG 2.2 AA. | Automated and manual audit before the consumer release. |
| NFR-UX-002 **[new]** | User-facing strings shall be externalised for localisation, with no assumptions of English word order or Latin script. | Pseudo-localisation build renders without truncation or concatenation defects. |

## Portability and maintainability

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| NFR-PORT-001 | Core processing shall run on supported Windows, macOS, and Linux without a platform-specific database engine. | CI runs the full suite on all three. |
| NFR-PORT-002 | Storage, cryptography, compression, segmentation, catalogue, and legacy import shall be separated behind tested interfaces. | Architecture tests enforce [`../architecture/11-solution-structure.md` §2](../architecture/11-solution-structure.md#2-dependency-rules). |
| NFR-PORT-003 | Deterministic fixtures and property-based tests shall cover all repository encodings and state transitions. | Every encoding has a round-trip property test. |
| NFR-PORT-004 | Public APIs shall use asynchronous streaming, cancellation, bounded concurrency, and explicit result types. | Expected outcomes are results, not exceptions ([`../architecture/05-storage-providers.md` §2.2](../architecture/05-storage-providers.md#22-results-not-exceptions)). |

## Supply chain

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| NFR-SUP-001 **[new]** | Every release shall publish an SBOM. | Generated and signed per release. |
| NFR-SUP-002 **[new]** | Dependencies shall be pinned by version and integrity hash, with automated vulnerability scanning in CI. | CI fails on an unpinned or known-vulnerable dependency. |
| NFR-SUP-003 **[new]** | Release artifacts shall be signed, and builds shall be reproducible. | An independent rebuild produces identical artifacts. |
| NFR-SUP-004 **[new]** | Auto-update shall verify signatures and prevent rollback to a superseded version. | Downgrade attempt refused. |

---

**Previous:** [Functional requirements](functional.md) · **Next:** [Traceability](traceability.md)

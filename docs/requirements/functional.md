# Functional requirements

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §3.4 · **Traceability:** [`traceability.md`](traceability.md)

---

Each requirement states an **observable acceptance criterion**. The original set contained a number of requirements that could not fail a test — "where beneficial", "shall be configuration driven", "trustworthy metadata" ([M5](../review/2026-08-architecture-review.md#m5--most-requirements-are-not-testable-as-written-and-the-promised-traceability-does-not-exist)) — and those have been given thresholds or observable outcomes.

Requirements marked **[changed]** differ materially from the original; **[new]** did not exist. The original wording of every changed requirement is quoted in the [review](../review/2026-08-architecture-review.md).

---

## Archive engine

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-ARCH-001 **[changed]** | The engine shall segment each regular file according to the backup set's segmentation profile. `fixed-v1` produces equal-length segments at fixed offsets; only the final segment may be shorter. `cdc-v1` produces content-defined boundaries within configured min/target/max bounds. | Given a byte string and a profile, segmentation is deterministic and reproducible across platforms and runs. |
| FR-ARCH-002 | Segmentation, hashing, compression, encryption, and packing shall operate as a bounded-memory stream. | Peak RSS while processing a 2 TiB file stays within the NFR-PERF-001 bound. |
| FR-ARCH-003 | The engine shall compute a cryptographic plaintext content identifier for each segment before encryption. | Content identifier is reproducible from plaintext alone. |
| FR-ARCH-004 | Unchanged segments shall be reused by reference rather than re-stored. | Modifying one segment of an *n*-segment file writes exactly one new segment record. |
| FR-ARCH-005 **[changed]** | New or changed segments shall be compressed, encrypted independently, authenticated, and appended to a blob. A segment shall be stored uncompressed when compression saves less than the configured fraction of its length (default 5%). The choice shall be recorded per record. | Incompressible input yields records marked uncompressed; compressible input yields records marked compressed. Both restore correctly. |
| FR-ARCH-006 | A file version may reference segments in any number of blobs; blob boundaries shall not affect file semantics or restore order. | A file spanning ≥ 100 blobs restores byte-identically. |
| FR-ARCH-007 **[changed]** | Target and maximum blob sizes shall come from a versioned write profile, be validated against the store's reported `MaximumObjectSize`, and be recorded in the policy manifest for each snapshot. | A profile exceeding a provider limit is rejected at configuration time with a named reason, not at write time. |
| FR-ARCH-008 | Encryption algorithm, key generation, and parameters shall be selected from supported profiles and recorded per encrypted record. Unsupported or unsafe combinations shall be rejected. | An unapproved suite is rejected at configuration time. |
| FR-ARCH-009 | Every encrypted segment record shall be independently authenticated. | Corrupting one record leaves all other records in the same blob readable and verifiable. |
| FR-ARCH-010 **[changed]** | File versions shall be reconstructed from ordered segment references containing logical offset, logical length, and **object identifier**. A segment reference shall contain **no blob identifier and no physical offset**. | A manifest encoding containing a blob ID or physical offset fails format validation. After blob compaction, every pre-existing manifest resolves without modification. |
| FR-ARCH-011 | Interrupted blob construction shall resume from a verified spool checkpoint or be safely abandoned, never becoming visible as a committed object. | Kill-and-resume at any record boundary produces a byte-identical blob (NFR-SEC-003). Kill-and-restart produces a blob with a different salt. |
| FR-ARCH-012 | A sealed blob shall be immutable, and its authenticated recovery footer shall contain sufficient metadata to inspect it and rebuild index entries. | Every record in a blob is locatable, decryptable, and verifiable from the blob and repository keys alone. |
| FR-ARCH-013 | The engine shall correctly represent empty files, sparse extents, files smaller than one segment, and files spanning many blobs. | Each case round-trips byte-identically, and sparse extents restore without materialising zero payload. |
| FR-ARCH-014 **[changed]** | The format shall define both `fixed-v1` and `cdc-v1` segmentation profiles in v1. The profile shall be recorded per file version and selectable per backup set. Format v1 shall not be frozen until both are benchmarked against a representative corpus and the results published. | Both profiles have conformance fixtures. The benchmark report exists before freeze. |

## Manifests, indexes, and catalogue

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-MAN-001 | Authoritative manifests and indexes shall be immutable repository objects. No local database shall be required to interpret or restore a committed snapshot. | Restore succeeds on a machine with no catalogue and no prior state. |
| FR-MAN-002 | Each instance shall maintain a local transactional catalogue for fast lookup. It shall be disposable and fully rebuildable from repository objects. | Deleting the catalogue and rebuilding reproduces equivalent lookup results. |
| FR-MAN-003 **[changed]** | Each file version shall have an immutable manifest containing path identity, metadata, logical length, ordered segment references (logical facts only, per FR-ARCH-010), parent-version reference where known, and a whole-file verification hash. | Manifest bytes are unchanged by any maintenance operation. |
| FR-MAN-004 | Trees and snapshots shall reference file-version manifests through immutable sharded tree objects. No snapshot shall require a monolithic repository-wide manifest. | No single manifest's size grows with repository or history size. |
| FR-MAN-005 | The catalogue shall resolve a path within a snapshot without scanning unrelated snapshots, trees, blobs, or records. | Resolution meets the NFR-PERF-004 latency target at the stated scale. |
| FR-MAN-006 | The catalogue shall resolve a content identifier within its segmentation profile and dedup trust domain to determine whether a reusable segment exists. | Lookup meets the NFR-PERF-010 target. |
| FR-MAN-007 | Every sealed blob shall include an authenticated recovery footer listing each record's object identifier, physical offset, stored length, logical length, and encoding profiles. | Forensic rebuild reconstructs all index entries from footers alone. |
| FR-MAN-008 **[changed]** | Index deltas shall be immutable and published only after their blobs are durable. Each delta shall carry `(writer_id, sequence, predecessor_delta_id)` with strictly increasing, gapless per-writer sequence. Checkpoints shall enumerate the exact delta IDs they subsume and the per-writer high-water sequence. | A reader detects a missing sequence number rather than assuming completeness. A delta above a checkpoint's watermark is applied even when listing omits it. |
| FR-MAN-013 **[new]** | Two checkpoints published at the same generation shall both be retained and both applied. | Applying either order, or both, yields identical catalogue state. |
| FR-MAN-009 | The engine shall support normal rebuild (checkpoint plus deltas) and forensic rebuild (blob footers plus snapshot roots). | Both succeed; forensic rebuild succeeds after every index object is deleted. |
| FR-MAN-010 | Listing and restore shall become available incrementally during rebuild, as soon as the required dependency graph is known. | A snapshot restores before full repository rebuild completes. |
| FR-MAN-011 | Rebuild shall verify signatures, authenticated metadata, record bounds, duplicate and conflicting mappings, generation ordering, and references to missing blobs. | Each injected fault class is detected and named. |
| FR-MAN-012 | The engine shall distinguish catalogue corruption, missing index objects, missing blobs, corrupt records, and unreachable orphans, reporting which snapshots and file versions are affected. | Each fault class produces a distinct report naming the affected scope. |
| FR-MAN-014 **[new]** | Rebuild shall never rewrite or repair repository objects. | A rebuild run leaves every repository object byte-identical. |

## Deduplication trust

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-DED-001 **[new]** | The repository shall support dedup trust domains `device`, `repository`, and `repository-unverified`. | Each is selectable and recorded in the policy manifest. |
| FR-DED-002 **[new]** | `device` shall be the default: a device reuses only segments it wrote. | A fresh repository defaults to `device` with no prompt. |
| FR-DED-003 **[new]** | In `repository`, a segment written by another writer shall be fetched, decrypted, and its content identifier confirmed before being referenced. | A segment whose stored plaintext does not match its claimed content identifier is never referenced, and the mismatch is reported as a security finding. |
| FR-DED-004 **[new]** | `repository-unverified` shall require explicit acknowledgement recording that a faulty or hostile writer can corrupt other devices' backups. | It cannot be enabled without the acknowledgement. |

## Snapshots

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-SNP-001 **[changed]** | A snapshot shall be committed to a replica only after every object it references is durable **in that replica**. Commit is per-replica; replication to other destinations is independent state. | A destination being offline delays policy compliance but does not prevent local commit. |
| FR-SNP-002 | File deletion shall create new snapshot state and shall not directly delete older file versions or segment records. | Prior versions remain restorable after a deletion snapshot. |
| FR-SNP-003 **[new]** | Each `(snapshot, destination)` pair shall carry independent replication status: `pending`, `replicating`, `durable`, `verified`, `degraded`. | Status is queryable per destination and drives policy evaluation. |
| FR-SNP-004 **[new]** | A write-intent record naming the blobs a job will create shall be durable **before** those blobs are uploaded, and retired when the snapshot is published. | No blob is uploaded without covering intent. Garbage collection never deletes an intent-covered blob. |

## Restore

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-RST-001 | Restore shall support selecting device, backup set, snapshot or time, path, file version, and destination. | Each selector is exercised end to end. |
| FR-RST-002 | Restore shall verify each segment after decryption and the complete reconstructed file before reporting success. | A corrupted segment fails the restore rather than producing a corrupt file. |
| FR-RST-003 **[new]** | A restore plan shall be produced before transfer, reporting required objects, replicas, logical and physical size, target conflicts, path and case collisions, unpreservable metadata, free space, and required privileges. | A collision or space shortfall is reported before any byte is written. |
| FR-RST-004 **[new]** | Restore shall produce a machine-readable receipt listing every file restored, every degraded attribute, and every failure. | The receipt accounts for every file in the plan. |
| FR-RST-005 **[new]** | Restore shall never report success when any required file failed. | A partial restore reports failure and names what failed. |
| FR-RST-006 **[new]** | Restore of historical content shall default to a quarantine path rather than the original location. | In-place restore requires an explicit choice. |

## Recovery kit

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-KIT-001 **[new]** | The kit shall contain kit format version, minimum recovery-tool version, repository ID, format profile, **wrapped** repository master key, KDF parameters, destination descriptors, issuing device public identity, issue timestamp, embedded instructions, and an integrity checksum. | A conformance fixture kit parses and opens a fixture repository. |
| FR-KIT-002 **[new]** | The kit shall never contain the passphrase, store credentials, or the device private key. | Format validation rejects a kit containing any of them. |
| FR-KIT-003 **[new]** | The kit shall be produced in printable (QR plus checksummed transcribable text) and machine-readable representations with identical content. | A hand-transcribed printable kit opens the repository; a transcription error is detected by the checksum. |
| FR-KIT-004 **[new]** | Kit generation shall occur during first-run setup and require explicit confirmation that it has been saved before setup completes. | Setup cannot complete without the confirmation. |
| FR-KIT-005 **[new]** | Kit status — never generated, saved, stale — shall be surfaced continuously. | Changing destinations marks the kit stale. |
| FR-KIT-006 **[new]** | A recovery drill restoring a file using only the kit shall be a supported, prompted workflow. | The drill runs without the catalogue or durable local state. |

## Replication and verification

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-REP-001 **[changed]** | Repositories shall replicate between instances by exchanging immutable blobs, manifests, snapshots, and index generations. Replication state is tracked per `(snapshot, destination)` and is independent of commit. | A destination catching up after an outage advances only replication state. |
| FR-REP-002 | Azure Blob Storage and Amazon S3/S3-compatible stores shall be supported through the common object-store abstraction. | Both pass the shared contract suite. |
| FR-REP-003 | Transfer shall resume at verified boundaries and never expose a partially written blob as committed. | Interruption mid-transfer leaves no visible partial object. |
| FR-REP-004 **[new]** | Peers shall negotiate a common protocol feature set at connection. A peer unable to satisfy the other's required features shall refuse with a stated reason. | A version-skew pair refuses cleanly rather than failing mid-transfer. |
| FR-VER-001 **[new]** | Destination verification shall use a keyed random-range challenge whose response cannot be precomputed, cached, or replayed. | A destination that discarded the data fails, even having answered a prior challenge for the same blob. |
| FR-VER-002 **[new]** | Verification shall sample blobs per interval, weighted towards those longest unverified, always covering the most recent snapshot's dependencies. | Coverage and challenge age are reported per destination. |
| FR-VER-003 **[new]** | Verification status shall be reported as coverage and age, never as a bare boolean. | No UI surface presents "verified" without both. |
| FR-VER-004 **[new]** | Full on-demand verification shall be available and shall run before a recovery drill. | It completes and reports per-object results. |
| FR-VER-005 **[new]** | A verification failure shall mark the affected `(snapshot, destination)` `degraded` and raise a warning requiring action. | Status changes and the warning appears. |

## Retention and garbage collection

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-GC-001 **[new]** | Retention shall select protected snapshots and delete nothing. | A retention change reclaims no space until collection runs. |
| FR-GC-002 **[new]** | Collection shall mark from a generation cut-off over snapshots protected as of that generation. | Objects published after the cut-off are out of scope. |
| FR-GC-003 **[new]** | Every blob covered by an unretired write intent shall be treated as reachable. | Collection concurrent with an in-flight backup deletes none of its blobs. |
| FR-GC-004 **[new]** | Blob compaction shall republish index entries only and shall modify no manifest, tree, or snapshot. | Manifest bytes are unchanged after compaction; all references still resolve. |
| FR-GC-005 **[new]** | A dry-run report shall be produced before any destructive pass, stating what would be deleted and compacted, space reclaimed, and which snapshots were treated as protected. | The report is generated and no object is deleted. |
| FR-GC-006 **[new]** | Deletion shall follow tombstoning, a configurable grace period, and pre-delete revalidation, in bounded batches. | Interruption at any step leaves every published snapshot recoverable. |
| FR-GC-007 **[new]** | Destination-side retention floors shall not be reducible by a source device. | A source request to reduce below the floor is refused and audited. |
| FR-GC-008 **[new]** | Retention reduction and bulk snapshot deletion shall require stronger authorisation than ordinary backup, and shall produce signed audit records. | Both are recorded and attributable. |

## Quotas and capacity

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-QUOTA-001 **[new]** | Quota exhaustion, destination disk-full, and transient errors shall be reported as distinct conditions. | Each produces a distinct status and user action. |
| FR-QUOTA-002 **[new]** | On exhaustion, transfer shall stop at a blob boundary leaving no partial object visible, and previously durable snapshots shall be unaffected. | Exhaustion mid-set leaves the repository consistent. |

## Governance

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-GOV-001 **[new]** | The repository shall carry an OSI-approved licence before the first public release. | `LICENSE` exists and [ADR-0001](../adr/0001-licence-and-contribution-model.md) is Accepted. |
| FR-GOV-002 **[new]** | The contribution model — DCO or CLA — shall be documented before accepting external contributions. | `CONTRIBUTING.md` states it. |
| FR-GOV-003 **[new]** | A security disclosure policy with a contact and response commitment shall be published before the first beta. | `SECURITY.md` exists. |
| FR-GOV-004 **[new]** | The repository format specification and conformance fixtures shall be public before format v1 freeze. | Published under `specifications/`. |

## CrashPlan migration

| ID | Requirement | Acceptance |
|----|-------------|-----------|
| FR-CP-001 | CrashPlan archives shall be opened read-only. | Source bytes are unchanged after any import, verified by digest. |
| FR-CP-002 | The importer shall map recoverable file versions into the same native pipeline used by filesystem capture. | Imported versions are indistinguishable in format from natively captured ones. |
| FR-CP-003 | Conversion shall stream from the archive into native blobs without a complete temporary plaintext restore. | Peak scratch usage stays bounded regardless of archive size. |
| FR-CP-004 | Import shall be resumable, produce provenance records, and verify imported versions against every hash available from the source archive. | Interrupted import resumes without republishing completed snapshots. |
| FR-CP-005 | Unsupported versions, encryption schemes, and damaged records shall be identified before high-volume processing. | Assessment completes and reports before any conversion begins. |
| FR-CP-006 **[new]** | The importer shall be a separately packaged optional component that the core never references. | The architecture test suite fails if the core references it. |

---

**Next:** [Non-functional requirements](non-functional.md) · [Traceability](traceability.md)

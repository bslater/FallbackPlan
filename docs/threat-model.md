# Threat model

**Status:** draft · **Supersedes:** [original proposal](review/2026-08-original-proposal.md) §14 · **Resolves:** [C3](review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard), [H6](review/2026-08-architecture-review.md#h6--independently-verified-trusts-the-destination-to-report-on-itself), [M2](review/2026-08-architecture-review.md#m2--the-threat-model-omits-metadata-side-channels)

---

This document is versioned alongside the format and must be reviewed before the first beta. It states what we defend against, what we do not, and — importantly — what we know we leak.

## Trust boundaries

```text
┌─────────────────────────── Source device (TRUSTED) ────────────────────────────┐
│  plaintext files · catalogue · device key · repository keys · unwrapped KEK    │
└──────────────────────────────────┬─────────────────────────────────────────────┘
                                   │  encrypted + authenticated objects only
        ┌──────────────────────────┼──────────────────────────┐
        v                          v                          v
┌───────────────┐         ┌────────────────┐        ┌──────────────────┐
│ Local store   │         │ Peer / relay   │        │ Cloud store      │
│ SEMI-TRUSTED  │         │ UNTRUSTED      │        │ UNTRUSTED        │
└───────────────┘         └────────────────┘        └──────────────────┘

┌──────────────── Repository members (PARTIALLY TRUSTED) ────────────────┐
│  other devices in the same repository — hold repository keys           │
│  ⚠ this boundary was absent from the original threat model             │
└────────────────────────────────────────────────────────────────────────┘
```

The fourth boundary is the addition. The original model treated everything holding repository keys as fully trusted, which is what let [C3](review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard) through.

## Threats in scope

### T-1 Untrusted store reads repository content
**Mitigation:** AEAD over every object; keyed object identifiers; encrypted filenames, paths, and metadata. NFR-SEC-001, NFR-SEC-004.

### T-2 Tampering and truncation
**Mitigation:** every record independently authenticated; blob-level digest over the sealed representation; authenticated recovery footer. Corruption is localised to a single record. NFR-SEC-005.

### T-3 Object substitution and splicing
An attacker moves a valid record into a different blob, position, object type, or repository.
**Mitigation:** AAD binds `repository_id ‖ format_version ‖ object_type ‖ object_id ‖ record_ordinal`. Any relocation fails authentication. [`03-crypto.md` §3.4`](architecture/03-crypto.md#34-associated-data).

### T-4 Rollback to an older repository view
A store or peer presents a stale snapshot set to hide recent backups or restore deleted content.
**Mitigation:** the catalogue retains the highest observed generation and per-writer sequence in durable local state; deltas form gapless per-writer chains so a missing sequence is *detected* rather than assumed absent. Optional external witnesses later. [`03-crypto.md` §6](architecture/03-crypto.md#6-authentication-of-repository-state).

### T-5 Stolen repository media
**Mitigation:** no plaintext mode; master key wrapped under an Argon2id-derived KEK. Media alone yields nothing.

### T-6 Deletion by compromised store credentials
**Mitigation:** destination-side retention floors a source cannot reduce; stronger authorisation for retention reduction and bulk deletion; signed audit records; provider object lock in a later phase. FR-GC-007, FR-GC-008.

### T-7 Malicious or malformed protocol input
**Mitigation:** bounded allocations and parser limits; fuzz testing of every binary parser; peer identity pinned at pairing.

### T-8 Destination withholding data
A destination claims to hold data it has discarded.
**Mitigation:** keyed random-range challenges that cannot be precomputed or cached ([`09-replication-and-peers.md` §5](architecture/09-replication-and-peers.md#5-destination-verification)); coverage and challenge age reported rather than a boolean.
**Residual:** a challenge proves possession *now*, not willingness to serve a restore later. Only a recovery drill proves that.

### T-9 Compromised destination without source keys
**Mitigation:** destinations never receive content keys. A destination holding every blob can decrypt nothing.

### T-10 Malicious repository member poisons deduplication
A device holding repository keys publishes a segment record whose claimed content identifier does not match its plaintext. Other devices deduplicate against it and silently back up corrupt data, discovered only at restore — after the source is gone.
**Mitigation:** dedup trust domains ([`03-crypto.md` §5](architecture/03-crypto.md#5-deduplication-trust-domains)). Default `device` reuses only self-written segments. `repository` requires verify-on-reuse. `repository-unverified` requires explicit acknowledgement of exactly this risk. FR-DED-001..004, NFR-SEC-007.

### T-11 Metadata side channels
An honest-but-curious store learns from what it is legitimately given:

| Channel | Leaks |
|---------|-------|
| Stored record lengths | Compressed sizes, which fingerprint file types and sometimes individual files |
| Blob arrival timing and volume | When a device is active and roughly how much changed |
| Record boundaries within a blob | Segment-size distribution |
| Object count growth | Approximate repository scale |

Compressing before encrypting is correct for efficiency and is what creates the length channel — a deliberate trade, stated rather than hidden.
**Mitigation:** an optional record-padding policy (padding stored lengths to size buckets) for high-sensitivity backup sets, at a storage cost.
**Residual:** padding narrows the length channel; it does not close the timing or volume channels. A store always learns *when* you back up and *roughly how much*.

### T-12 Dedup confirmation by a repository member
In any trust domain other than `device`, a member can determine whether another member has backed up a *known* file by observing whether deduplication hits.
**Mitigation:** `device` is the default and closes this entirely.
**Residual:** inherent to cross-device deduplication. Anyone for whom it matters should stay on `device`.

### T-13 Relay traffic analysis
A relay cannot decrypt, but it learns which device identities communicate, when, and how much.
**Mitigation:** relays are optional and self-hostable; direct connection is preferred and the path is always reported. Minimal metadata retention.
**Residual:** a hosted relay observes the communication graph. Self-host if that matters.

### T-14 Supply-chain compromise
**Mitigation:** pinned dependencies with integrity hashes; vulnerability scanning in CI; SBOM per release; signed, reproducible builds; auto-update with signature verification and rollback protection. NFR-SUP-001..004.

### T-15 Parser attacks through legacy archives
A crafted CrashPlan archive attacks the importer.
**Mitigation:** importer isolated in an optional package; read-only source access; bounded allocations; fuzz testing of every parser; path traversal containment. FR-CP-001, FR-CP-006.

### T-16 Local privilege boundaries
The UI user and the service run at different privilege levels.
**Mitigation:** the local API authenticates callers; remote management requires explicit enablement; the service does not expose raw filesystem access to UI clients.

### T-17 Secrets in logs and diagnostics
**Mitigation:** redaction by declared **type**, not string pattern — a new secret-bearing field is redacted by construction. Diagnostic bundles exclude credentials, keys, plaintext paths, and correlatable identifiers by default. NFR-SEC-006, NFR-PRIV-003.

### T-18 Writer identity cloning
A copied device identity publishes under an existing writer ID.
**Mitigation:** per-writer journal sequences are gapless and monotonic; conflicting or regressing sequence use raises a security alert rather than a log line. [`04-concurrency-and-publication.md` §2](architecture/04-concurrency-and-publication.md#2-writer-identity).

## Threats not solvable by backup software

Stated plainly so no other document implies otherwise:

- **A compromised source** reads plaintext before encryption. No backup system prevents this.
- **Ransomware holding source credentials and unlocked keys** acts with the user's authority. Retention floors and destination policy locks limit the damage; they do not prevent it.
- **Loss of all recovery material** makes the repository permanently unreadable. This is by design, and it is why the recovery-kit workflow is mandatory and drills are prompted.
- **A malicious administrator** with access to every device and to retention controls can destroy data. Audit records make it attributable, not impossible.
- **Hardware faults across every replica** are undetectable without verification, which is why verification coverage is a first-class status.
- **Malware already present in a historical snapshot** will be faithfully restored. Restore defaults to quarantine for this reason ([`08-restore-and-recovery.md` §3.1](architecture/08-restore-and-recovery.md#31-quarantine-by-default)).

## Controls summary

Mutual device authentication · least-privilege repository grants · separate read/append/retention/administrative permissions · AEAD for every object · per-blob key derivation with structural nonce uniqueness · signed snapshots and journal records · anti-rollback anchored in durable local state · dedup trust domains · keyed verification challenges · bounded parsers · type-based secret redaction · pinned dependencies and vulnerability scanning · signed reproducible releases · rollback-protected auto-update · repository-server rate limits and quotas · signed audit trail for destructive operations · quarantine-by-default restore.

## Review obligations

- Reviewed before the first beta and before format v1 freeze.
- Re-reviewed whenever a trust boundary changes — new provider class, new sharing model, new relay capability.
- External security review is a release gate ([`roadmap.md`](roadmap.md#phase-6--consumer-ready-release)).

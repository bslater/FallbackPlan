# Threat model

**Status:** draft · **Supersedes:** [original proposal](review/2026-08-original-proposal.md) §14 · **Resolves:** [C3](review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard), [H6](review/2026-08-architecture-review.md#h6--independently-verified-trusts-the-destination-to-report-on-itself), [M2](review/2026-08-architecture-review.md#m2--the-threat-model-omits-metadata-side-channels)

---

This document is versioned alongside the format and must be reviewed before the first beta. It states what we defend against, what we do not, and — importantly — what we know we leak.

## Trust boundaries

```text
┌─────────────────────────── Source device ─────────────────────────────────────┐
│  ┌───────────── Service account (TRUSTED) ─────────────────────────────────┐  │
│  │  device key · repository keys · unwrapped KEK · catalogue · spool       │  │
│  └──────────────────────────────┬──────────────────────────────────────────┘  │
│   command surface (ADR-0028) →   │  commands · status · progress               │
│  ┌──────────────────────────────v──────────────────────────────────────────┐  │
│  │  UI user and local clients (LESS TRUSTED) — never hold key material     │  │
│  └─────────────────────────────────────────────────────────────────────────┘  │
│  plaintext files sit outside both: readable by whoever can already read them  │
└──────────────────────────────────┬────────────────────────────────────────────┘
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
**Residual:** a challenge proves possession *now*, not willingness to serve a restore later. Only a recovery drill proves that. The mitigation is no longer declinable — a destination that does not offer the challenge feature is refused rather than replicated to, since the feature set is the destination's own declaration and this mitigation defends against that same destination (FR-VER-006). Keeping an unprovable destination requires an explicit acknowledgement, and one kept on those terms never reports `verified` and never licenses reclaiming the source's last copy.

### T-9 Compromised destination without source keys
**Mitigation:** destinations never receive content keys. A destination holding every blob can decrypt nothing.

### T-10 Malicious repository member poisons deduplication
A device holding repository keys publishes a segment record whose claimed content identifier does not match its plaintext. Other devices deduplicate against it and silently back up corrupt data, discovered only at restore — after the source is gone.
**Mitigation, as designed:** dedup trust domains ([`03-crypto.md` §5](architecture/03-crypto.md#5-deduplication-trust-domains)). The default `repository` verifies on reuse — it fetches, decrypts, and confirms the content identifier before referencing another writer's segment — so a mismatched record is never referenced and the mismatch is reported. `device` avoids cross-writer reuse entirely. `repository-unverified` requires explicit acknowledgement of exactly this risk. FR-DED-001..004, NFR-SEC-007.

> **Built (2026-08).** `DedupTrustGate` decides every reuse. Another writer's object is confirmed before it is referenced under the default domain, refused outright under `device`, and referenced unread only under `repository-unverified`. A record that reads and does not verify is written again from the bytes this device holds and reported as a damage finding — which is the point of moving detection to write time.
>
> Two residuals remain and are not the same size. **FR-DED-004's acknowledgement gate does not exist**, so `repository-unverified` — the one domain that leaves T-10 open — can be selected without anyone being told what it means; the gate belongs in the client that offers the choice. And **verification is remembered in the catalogue, not the repository**, so deleting the catalogue re-imposes the reads once; that is a cost, not an exposure, and [ADR-0006](adr/0006-object-identifiers-and-dedup-trust-domains.md#what-is-deliberately-not-solved) records why it was accepted. → [implementation status](implementation-status.md#0006--the-integrity-guard-is-built-and-one-thing-is-deliberately-not)

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
**Mitigation:** `device` mode closes this entirely, and is available as an opt-in.
**Residual:** the default is `repository` ([ADR-0006](adr/0006-object-identifiers-and-dedup-trust-domains.md)), so this channel is open by default in multi-device repositories — a deliberate trade for cross-device deduplication, which is the product's headline use case. It must be stated in the UI where the trust domain is chosen. Anyone for whom it matters should select `device`.

### T-13 Relay traffic analysis
A relay cannot decrypt, but it learns which device identities communicate, when, and how much.
**Mitigation:** relays are optional and self-hostable; direct connection is preferred and the path is always reported. Minimal metadata retention.
**Residual:** a hosted relay observes the communication graph. Self-host if that matters.

### T-14 Supply-chain compromise
**Mitigation:** pinned dependencies with integrity hashes; vulnerability scanning in CI; SBOM per release; signed, reproducible builds; auto-update with signature verification and rollback protection. NFR-SUP-001..004.

### T-15 Parser attacks through legacy archives
A crafted legacy archive attacks the importer.
**Mitigation:** importer isolated in an optional package; read-only source access; bounded allocations; fuzz testing of every parser; path traversal containment. FR-CP-001, FR-CP-006.

### T-16 Local privilege boundaries
The UI user and the service run at different privilege levels, and the service holds key material the UI user must never obtain.
**Mitigation:** the local binding is a Unix domain socket or named pipe in a directory only the service account may write, so **the operating system authenticates callers** — filesystem permissions decide who may connect and the service reads peer credentials to identify them. No token file and no local port, both of which would put a copyable credential where a local process could take it ([ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md) §5). Key material never crosses the boundary in either direction; clients receive commands, results and progress only. The service exposes no raw filesystem access to clients.
**Note (2026-08):** the identifying half of that mitigation did not work on Linux until a coverage audit went looking — the `SO_PEERCRED` read used an accessor that rejects raw native option names, so every local caller was reported as unidentified. Authentication was never affected; identification now works and is pinned by a test over a real socket pair ([coverage audit G5](review/2026-08-coverage-audit.md#g5--apitransportpeercredentialscs-222--including-the-linux-success-path)).

### T-17 Secrets in logs and diagnostics
**Mitigation:** redaction by declared **type**, not string pattern — a new secret-bearing field is redacted by construction. Diagnostic bundles exclude credentials, keys, plaintext paths, and correlatable identifiers by default. NFR-SEC-006, NFR-PRIV-003.

### T-18 Writer identity cloning
A copied device identity publishes under an existing writer ID.
**Mitigation:** per-writer journal sequences are gapless and monotonic; conflicting or regressing sequence use raises a security alert rather than a log line. [`04-concurrency-and-publication.md` §2](architecture/04-concurrency-and-publication.md#2-writer-identity).
**Note:** the same alarm fires when two local processes share a state directory, which is why exclusive ownership by the service ([ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md) §2) matters to this threat: without it, an ordinary user running two commands at once is indistinguishable from a stolen device key.

### T-19 Unlocked key material at rest in the service account
The service unlocks itself from the platform keystore so scheduled backups run unattended, which means key material is available to whoever controls the service account — without knowing the passphrase.
**Mitigation:** partial, and the residual risk is accepted deliberately. Key material is released by the OS keystore (DPAPI, Keychain, kernel keyring) scoped to the service account, so obtaining it requires that account rather than a file read; it lives in the service process only and never crosses the command surface. Operations that mint new access — key export above all — re-derive the KEK from a **user-supplied passphrase per invocation** and never from the keystore, so holding the running service is not sufficient to produce a recovery kit.
**Residual (format v1):** an attacker with the service account can read the backups. The alternative — prompting a human for every scheduled run — is not a backup product, and pretending otherwise would be worse than saying so. Recorded here rather than left implicit in ADR-0028.
**Materially improved by write-only repositories ([ADR-0042](adr/0042-write-only-repositories.md), format v2):** a provisioned v2 set's service state holds only the write bundle — no passphrase, no keystore entry, no private key in any form. An attacker with the service account gets **metadata readability plus write capability**: the structure of what exists, and the ability to add to history. They provably cannot get file contents (sealed to a public key whose scalar is never stored), the passphrase, or restore capability — each derived key is an independently one-way HKDF domain of the Argon2id root (NFR-SEC-010, held by `WriteOnlyDerivationTests`). The v2 residuals are narrower and stated: metadata visibility itself (names, sizes, structure — the write bundle reads the whole structure plane); the in-flight spool window, where a blob's content key lives in an owner-only checkpoint until seal (bounded — the same machine holds the source plaintext anyway); and a restore grant's lifetime, during which the scalar lives inside the source handle until close, idle expiry, or shutdown.

### T-20 Hostile client on the command surface
A local process, or a remote host once the remote binding is enabled, attempts to command the service: start or cancel jobs, alter backup sets, read status, or extract content.
**Mitigation:** locally, T-16's OS authentication. Remotely, the binding is **off until explicitly enabled** and names the interface it binds; clients are **paired with pinned device identity** rather than given a password, both sides approve, and a changed identity is a hard failure requiring re-approval, not a prompt that can be clicked through ([`architecture/09-replication-and-peers.md` §3](architecture/09-replication-and-peers.md#3-pairing)). Pairing is revocable at the service — the party at risk. A remote client may command and observe but **does not receive file content**: a restore it commands is written on the machine running the service, and streaming content to a remote client is a separately enabled capability. Version skew is refused with both versions named rather than met with a silent failure.
**Why content is withheld by default:** a management console that could pull plaintext from every machine it administers would concentrate what the repository design refuses to concede to a destination, a relay, or a peer. Withholding it is what lets an operator administer machines they are not entitled to read.

## Threats not solvable by backup software

Stated plainly so no other document implies otherwise:

- **A compromised source** reads plaintext before encryption. No backup system prevents this.
- **Ransomware holding source credentials and unlocked keys** acts with the user's authority. Retention floors and destination policy locks limit the damage; they do not prevent it.
- **Loss of all recovery material** makes the repository permanently unreadable. This is by design, and it is why the recovery-kit workflow is mandatory and drills are prompted.
- **A malicious administrator** with access to every device and to retention controls can destroy data. Audit records make it attributable, not impossible.
- **Hardware faults across every replica** are undetectable without verification, which is why verification coverage is a first-class status.
- **Malware already present in a historical snapshot** will be faithfully restored. Restore defaults to a quarantine path for this reason ([`08-restore-and-recovery.md` §3.1](architecture/08-restore-and-recovery.md#31-quarantine-by-default)): content lands under a directory of its own and reaching the live tree is a deliberate choice. FR-RST-006.

## Controls summary

**This is the design's control set, and roughly half of it is built.** The distinction is drawn here rather than left to be inferred, because a threat model is read by people deciding whether to trust a system, and a designed control read as a deployed one is worse than no entry at all. Per-decision detail is in [implementation status](implementation-status.md).

**In force** — implemented, with tests holding them:

OS-authenticated local command surface · key material confined to the service account and never crossing the command surface except as sealed envelopes on the two named write-only ceremonies (NFR-SEC-009 as amended by ADR-0042) · write-only repositories: content sealed to a derived public key whose scalar is never stored, the service holding a provably one-way write bundle (NFR-SEC-010) · AEAD for every object · per-blob key derivation with structural nonce uniqueness · signed snapshots and journal records · anti-rollback anchored in durable local state (NFR-SEC-005) · bounded parsers, fuzzed · type-based secret redaction · pinned dependencies with locked restore and a CI vulnerability gate (NFR-SUP-002/003) · reproducible conformance vectors · restore refuses repository paths that do not resolve under the restore root.

**Designed, not built** — each waits on a phase, not on a decision:

| Control | Waiting on |
|---------|-----------|
| Paired device identity for remote clients, off by default | Built and carried over a real TLS socket; an unpaired client is refused and a substituted identity is refused rather than prompted ([implementation status](implementation-status.md#0030--the-socket-exists)) |
| Mutual device authentication | The same — the construction exists and has never spoken to another machine |
| Content withheld from remote clients unless separately enabled | [Q18](open-questions.md#q18--streaming-restored-content-to-a-remote-client) |
| Least-privilege repository grants; separate read/append/retention/administrative permissions | Phase 2–3 |
| Keyed verification challenges | Replication (architecture 09 §5) |
| Repository-server rate limits and quotas | Phase 3 |
| Signed audit trail for destructive operations | Retention and GC, which are not built at all |
| Signed reproducible releases · rollback-protected auto-update | There is no release pipeline yet |

**Dedup trust domains are not in either list above, and that is the point.** An earlier version of this page put them under *in force* with a note that the `device` domain was "specified and unexercised". That was wrong in the direction that matters: **verify-on-reuse is not implemented at all**, including for `repository`, which is the default. Reuse is decided by index presence alone. See T-10 above, and treat the control as absent rather than partial.

## Review obligations

- Reviewed before the first beta and before format v1 freeze.
- Re-reviewed whenever a trust boundary changes — new provider class, new sharing model, new relay capability, **or a new client-facing surface** (T-16, T-19, T-20 arrived exactly this way).
- External security review is a release gate ([`roadmap.md`](roadmap.md#phase-6--consumer-ready-release)).

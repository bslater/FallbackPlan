# Threat model

**Status:** draft · **Supersedes:** [original proposal](review/2026-08-original-proposal.md) §14 · **Resolves:** [C3](review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard), [H6](review/2026-08-architecture-review.md#h6--independently-verified-trusts-the-destination-to-report-on-itself), [M2](review/2026-08-architecture-review.md#m2--the-threat-model-omits-metadata-side-channels)

---

This document is versioned alongside the format and must be reviewed before the first beta. It states what we defend against, what we do not, and — importantly — what we know we leak.

## Trust boundaries

```text
┌─────────────────────────── Source device ─────────────────────────────────────┐
│  ┌───────────── Service account (TRUSTED) ─────────────────────────────────┐  │
│  │  device key · write credential · catalogue · spool                      │  │
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

**Note (2026-08, [ADR-0046](adr/0046-direct-to-destination-publication.md)) — the staging copy leaves the source device.** A direct-ship set's content is written to its destinations directly: the service account's state holds the set's *metadata* (descriptor, keys, journal, index, snapshots) and a bounded in-flight spool, but no whole-archive replica of the backup. Nothing about the crypto boundary moves — every object was already encrypted and authenticated before leaving the trusted zone, so direct-ship changes where objects land, not what they contain, and every threat above reads the same. What changes is exposure and dependency, in opposite directions: media stolen from the source machine (T-5) no longer carries a full encrypted copy of the backup beside the originals, while the *availability* of capture now depends on a destination being reachable — with every destination away there is nowhere to write and the run refuses, a durability property consciously traded away and recorded in ADR-0046 §4. The metadata store keeps the structure plane readable to whoever holds the service account, exactly as T-19 already states for the write bundle.

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
**Mitigation:** the catalogue retains the highest observed generation and per-writer sequence in durable local state; deltas form gapless per-writer chains so a missing sequence is *detected* rather than assumed absent; when that local state is itself lost or rolled back, the repository's own signed checkpoints, deltas and journal keys are the witness at archive open ([ADR-0008](adr/0008-index-generations-and-checkpoints.md)); and when the *whole* state directory rolls back — taking a direct-ship set's metadata plane with it — the destination is the witness: every fan-out pass reads the destination's journal head for this writer, moves the sequence past it, deletes nothing there, and heals the set from the destination — a direct-ship set's metadata and catalogue, a staging set's content as well, bounded by the history it lacks ([ADR-0062](adr/0062-the-destination-is-the-rollback-witness.md) and its Amendment 2, FR-DEST-018). [`03-crypto.md` §6](architecture/03-crypto.md#6-authentication-of-repository-state).

A peer is witnessed from the inventory every push already declares, before the push filters or drops anything, and a set of either kind is healed from it over the retrieval session, one chunk in memory at a time ([ADR-0062 Amendment 1](adr/0062-the-destination-is-the-rollback-witness.md#amendment-1--the-peer-is-a-witness-too-from-the-inventory-it-already-declares-2026-09)).

**Residual:** a rollback that restores the state directory and every destination from one older image has no witness anywhere, because nothing is ahead of anything.

### T-5 Stolen repository media
**Mitigation:** no plaintext mode; every key derives from the passphrase through Argon2id, nothing derivable is stored, and file contents are sealed to a public key. Media alone yields nothing.

### T-6 Deletion by compromised store credentials
**Mitigation:** destination-side retention floors a source cannot reduce; a **reclaim key** that authorises removal, on its own derivation domain and deliberately absent from a write-only service's write credential, so a service that can publish cannot author a tombstone ([ADR-0055](adr/0055-reclaim-authority.md)); a collection run on such a set taking that authority from a grant sealed to the service and held only for the run; a peer retention instruction signed under the same key **over the session it is sent in**, refused whole by a destination that cannot verify it — and required by any destination holding the repository's reclaim public key, never by a feature the sender may decline to offer ([ADR-0059](adr/0059-session-bound-deletion-authority.md)); the signed tombstone itself as the audit record of what was condemned, by whom and when; on the peer plane, a **deletion receipt** signed under the destination's own device key, carried back in the acknowledgement of every retention instruction, verified by the commander against the instruction it sent and filed by both parties ([ADR-0063](adr/0063-deletion-receipts.md)); provider object lock in a later phase. FR-GC-007, FR-GC-008.

The split defends every repository, now that format 1 — whose service held a master key and derived both keys from it — is withdrawn ([ADR-0014 Amendment 1](adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)). The retention floor stays first in this list and first in [architecture 07 §5](architecture/07-retention-and-gc.md#5-destructive-change-safeguards), because it is the measure that holds against a compromised *grant*: a collection run's authority is still a run's.

**Residual:** a deletion receipt attests what the destination *says* it deleted, under a key the destination holds. A destination that reports deleting what it quietly kept is caught by nothing here and need not be — that is a destination holding more, and possession of what remains is verification's job (T-8); one that lists a key it was never told to delete is caught by the commander's check. A destination that deletes outside any instruction — on its own floor, or on its operator's decision — writes no receipt, because nothing instructed it. A crash between deleting and filing loses the destination's copy of that one receipt; the deletion is idempotent and the next instruction produces one.

### T-7 Malicious or malformed protocol input
**Mitigation:** bounded allocations and parser limits; fuzz testing of every binary parser; peer identity pinned at pairing.

### T-8 Destination withholding data
A destination claims to hold data it has discarded.
**Mitigation:** keyed random-range challenges that cannot be precomputed or cached ([`09-replication-and-peers.md` §5](architecture/09-replication-and-peers.md#5-destination-verification)); coverage and challenge age reported rather than a boolean; where this side holds no copy to judge a challenge against, the replica read back and proved by a record's AEAD tag, and — for the sealed data plane of a write-only set, which no tag here can open — by the **digest tier**, the whole blob hashed at the destination against the digest the writer signed into the index, with the ledger saying which tier proved what (contract 1.32); and a restore drill on a cadence, at a local path by default and at a peer on a cadence the operator states ([ADR-0054](adr/0054-scheduled-restore-drills.md)).
**Residual:** a challenge proves possession *now*, not willingness to serve a restore later. Only a recovery drill proves that. The mitigation is no longer declinable — a destination that does not offer the challenge feature is refused rather than replicated to, since the feature set is the destination's own declaration and this mitigation defends against that same destination (FR-VER-006). Keeping an unprovable destination requires an explicit acknowledgement, and one kept on those terms never reports `verified` and never licenses reclaiming the source's last copy. A replication receipt ([ADR-0064](adr/0064-replication-receipts.md)) is the examined party's own statement: a signed count of what a peer committed and holds, checked for consistency with what was sent and counted on the ledger as attested — never as possession, which stays with the challenge, the read-back and the drill.

### T-9 Compromised destination without source keys
**Mitigation:** destinations never receive content keys. A destination holding every blob can decrypt nothing.
**Note (2026-09, [ADR-0053](adr/0053-peer-claim-and-configuration-recovery.md)):** a destination now also records two *public* keys per attribution — the reclaim key it checks deletion instructions against, and the claim key it checks a rebuilt machine's claim against. Neither confers anything: a public key authorises nothing, and they exist precisely because a destination holds no repository keys and would otherwise have nothing to check. What a compromised destination gains is the ability to *refuse* a legitimate claim, which is the same availability it already has by refusing to serve the replica at all.

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
The policy manifest records the set's root paths, name and schedule since ADR-0061; it sits in the metadata plane, so a store or peer sees ciphertext, while the structure plane a write-credential holder can read (FR-WOR-003) now names *where on the source disk* a root sat as well as what its tree is called.

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
**Amendment (2026-08, [ADR-0045](adr/0045-client-authentication.md)):** what the operating system authenticates is a *process*, and what it identifies is a *uid* — which is not a person. A shared account, or two administrators on one machine, are one uid and were until now one indistinguishable "operator". Person-identity is added **inside** this already-authenticated channel as a session, and it neither replaces nor weakens the check above: the socket's permissions remain the whole of the connection decision, there is still no token file and no local port, and a password reaches no service the permissions would have refused.
**Note (2026-08):** the identifying half of that mitigation did not work on Linux until a coverage audit went looking — the `SO_PEERCRED` read used an accessor that rejects raw native option names, so every local caller was reported as unidentified. Authentication was never affected; identification now works and is pinned by a test over a real socket pair ([coverage audit G5](review/2026-08-coverage-audit.md#g5--apitransportpeercredentialscs-222--including-the-linux-success-path)).

### T-17 Secrets in logs and diagnostics
**Mitigation:** redaction by declared **type**, not string pattern — a new secret-bearing field is redacted by construction. Diagnostic bundles exclude credentials, keys, plaintext paths, and correlatable identifiers by default. NFR-SEC-006, NFR-PRIV-003.

### T-18 Writer identity cloning
A copied device identity publishes under an existing writer ID.
**Mitigation:** per-writer journal sequences are gapless and monotonic; conflicting or regressing sequence use raises a security alert rather than a log line. [`04-concurrency-and-publication.md` §2](architecture/04-concurrency-and-publication.md#2-writer-identity).
**Note:** the same alarm fires when two local processes share a state directory, which is why exclusive ownership by the service ([ADR-0028](adr/0028-service-boundary-and-deployment-topologies.md) §2) matters to this threat: without it, an ordinary user running two commands at once is indistinguishable from a stolen device key.

### T-19 Key material at rest in the service account
Scheduled backups run unattended, so whatever the service needs to write is available to whoever controls the service account — without knowing the passphrase.
**Mitigation:** the service holds only the **write credential** ([ADR-0042](adr/0042-write-only-repositories.md)) — no passphrase, no keystore entry, no private key in any form. The platform keystore and the `unlock` verb that once released a passphrase to the service are gone with format 1 ([ADR-0028 §9](adr/0028-service-boundary-and-deployment-topologies.md) retired; [ADR-0014 Amendment 1](adr/0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)); the credential is what first-run setup stores, readable by the service account and by nothing that needs a human present. Operations that mint new access — a restore grant, a reclaim grant, a peer claim — re-derive from a **user-supplied passphrase per invocation**, so holding the running service is not sufficient to read a backup, delete one, or re-point a replica. There is no recovery kit to produce ([ADR-0060](adr/0060-the-passphrase-is-the-recovery-credential.md)): for a format-2 installation it was never a factor, its payload being a strict subset of every archive's descriptor. The residual this section once accepted for format 1 — *an attacker with the service account can read the backups* — no longer exists; what remains is narrower and stated next.
**What an attacker with the service account gets:** **metadata readability plus write capability**: the structure of what exists, and the ability to add to history. They provably cannot get file contents (sealed to a public key whose scalar is never stored), the passphrase, or restore capability — each derived key is an independently one-way HKDF domain of the Argon2id root (NFR-SEC-010, held by `WriteOnlyDerivationTests`). The residuals are narrower and stated: metadata visibility itself (names, sizes, structure — the write bundle reads the whole structure plane); the in-flight spool window, where a blob's content key lives in an owner-only checkpoint until seal (bounded — the same machine holds the source plaintext anyway); and a restore grant's lifetime, during which the scalar lives inside the source handle until close, idle expiry, or shutdown. Two authorities are withheld from the bundle outright and reached only from the passphrase: the **reclaim** key, so a service that may publish for ever cannot author a deletion ([ADR-0055](adr/0055-reclaim-authority.md)); and the **claim** key, so it cannot re-point a peer replica's attribution at a machine of its choosing ([ADR-0053](adr/0053-peer-claim-and-configuration-recovery.md)). Neither is grantable — a restore grant carries the sealing scalar, a collection run carries the reclaim sub-root, and nothing carries the claim seed.

### T-20 Hostile client on the command surface
A local process, or a remote host once the remote binding is enabled, attempts to command the service: start or cancel jobs, alter backup sets, read status, or extract content.
**Mitigation:** locally, T-16's OS authentication. Remotely, the binding is **off until explicitly enabled** and names the interface it binds; clients are **paired with pinned device identity** rather than given a password, both sides approve, and a changed identity is a hard failure requiring re-approval, not a prompt that can be clicked through ([`architecture/09-replication-and-peers.md` §3](architecture/09-replication-and-peers.md#3-pairing)). Pairing is revocable at the service — the party at risk. A remote client may command and observe but **does not receive file content**: a restore it commands is written on the machine running the service, and streaming content to a remote client is a separately enabled capability. Version skew is refused with both versions named rather than met with a silent failure.
**Amendment (2026-08, [ADR-0045](adr/0045-client-authentication.md)):** pairing answers which *device* may command the service and stays exactly as described — clients are still paired, not passworded, and no password is a way to reach a service that pairing would refuse. What pairing cannot answer is which *person* at that paired console is acting, so a session inside the connection now does, making an action attributable and one person revocable without revoking the device.

**Why content is withheld by default:** a management console that could pull plaintext from every machine it administers would concentrate what the repository design refuses to concede to a destination, a relay, or a peer. Withholding it is what lets an operator administer machines they are not entitled to read.

## Threats not solvable by backup software

Stated plainly so no other document implies otherwise:

- **A compromised source** reads plaintext before encryption. No backup system prevents this.
- **Ransomware holding source credentials and unlocked keys** acts with the user's authority. Retention floors and destination policy locks limit the damage; they do not prevent it.
- **Loss of the passphrase** makes the repository permanently unreadable. This is by design, and it is why recovery is drilled rather than assumed ([ADR-0060](adr/0060-the-passphrase-is-the-recovery-credential.md)).
- **A malicious administrator** with access to every device and to retention controls can destroy data. Audit records make it attributable, not impossible.
- **Hardware faults across every replica** are undetectable without verification, which is why verification coverage is a first-class status.
- **Malware already present in a historical snapshot** will be faithfully restored. Restore defaults to a quarantine path for this reason ([`08-restore-and-recovery.md` §3.1](architecture/08-restore-and-recovery.md#31-quarantine-by-default)): content lands under a directory of its own and reaching the live tree is a deliberate choice. FR-RST-006.

## Controls summary

**This is the design's control set, and most of it is built.** The distinction is drawn here rather than left to be inferred, because a threat model is read by people deciding whether to trust a system, and a designed control read as a deployed one is worse than no entry at all. Per-decision detail is in [implementation status](implementation-status.md).

**In force** — implemented, with tests holding them:

Paired device identity for remote clients, off by default, carried over a real TLS socket — an unpaired client is refused, and a substituted identity is refused rather than prompted ([implementation status](implementation-status.md#0030--the-socket-exists)); mutual device authentication by the same construction, which has never yet spoken to another machine · dedup trust domains: every reuse of another writer's object passes `DedupTrustGate`, confirmed before it is referenced under the default `repository`, refused outright under `device`, referenced unread only under `repository-unverified` (T-10) · keyed verification challenges: a destination answers keyed random-range proofs and a local-path replica answers to direct read-back, with coverage and age carried on the sync ledger · peer quotas, enforced as bytes land · retention and garbage collection with signed tombstones over destructive operations · OS-authenticated local command surface · key material confined to the service account and never crossing the command surface except as sealed envelopes on the two named write-only ceremonies (NFR-SEC-009 as amended by ADR-0042) · write-only repositories: content sealed to a derived public key whose scalar is never stored, the service holding a provably one-way write bundle (NFR-SEC-010) · AEAD for every object · per-blob key derivation with structural nonce uniqueness · signed snapshots and journal records · anti-rollback anchored in durable local state and, when that state is lost or rolled back, recovered from the head the repository itself attests — signed checkpoints, signed deltas and journal keys, all of which outlive the machine — and, when the whole state directory including a direct-ship set's metadata plane rolls back, from the head the destination attests, the destination being the one copy that outlives the state directory (NFR-SEC-005, FR-DEST-018) · bounded parsers, fuzzed · type-based secret redaction · pinned dependencies with locked restore and a CI vulnerability gate (NFR-SUP-002/003) · reproducible conformance vectors · restore refuses repository paths that do not resolve under the restore root.

**Designed, not built** — each waits on a phase, not on a decision:

| Control | Waiting on |
|---------|-----------|
| Content withheld from remote clients unless separately enabled | [Q18](open-questions.md#q18--streaming-restored-content-to-a-remote-client) |
| Least-privilege repository grants; separate read/append/retention/administrative permissions | Phase 2–3 |
| Repository-server rate limits | Phase 3 — per-peer quotas are enforced (see *in force*); rate limiting is not |
| Signed reproducible releases · rollback-protected auto-update | There is no release pipeline yet |

**Dedup trust domains have moved twice, and the history is kept because the direction of each move is the point.** They were first listed *in force* with the `device` domain noted as "specified and unexercised" — flattery, because verify-on-reuse was not implemented at all. They were then moved out of both lists and described as absent, which was accurate when written and went stale when `DedupTrustGate` landed: from that point this page called an implemented control unbuilt while [T-10](#t-10-malicious-repository-member-poisons-deduplication) six sections above called it built. They are now *in force*, with T-10's two named residuals — FR-DED-004's acknowledgement gate, and verification remembered in the catalogue rather than the repository — stated there rather than here.

## Review obligations

- Reviewed before the first beta and before the format freezes.
- Re-reviewed whenever a trust boundary changes — new provider class, new sharing model, new relay capability, **or a new client-facing surface** (T-16, T-19, T-20 arrived exactly this way).
- External security review is a release gate ([`roadmap.md`](roadmap.md#phase-6--consumer-ready-release)).

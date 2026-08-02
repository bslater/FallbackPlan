# ADR-0006 — Object identifiers and deduplication trust domains

**Status:** Proposed
**Date:** 2026-08
**Requirements:** FR-DED-001..004, NFR-SEC-004, NFR-SEC-007, FR-MAN-006
**Review finding:** [C3](../review/2026-08-architecture-review.md#c3--cross-device-deduplication-has-no-integrity-guard)

---

## Context

The proposal specified two identifiers — a plaintext content identifier for in-client deduplication, and a keyed repository object identifier exposed to stores. That construction is correct and defends properly against a storage provider testing whether a repository holds a known file.

It defends against the wrong adversary for deduplication, though. Keying stops the *provider*; it does nothing against a repository *member*, because members hold the key.

All writers in a repository share the content-ID key, so device B can see that a segment with content identifier `H` exists and reference it instead of uploading its own copy. B cannot verify that claim without downloading and decrypting the segment — precisely the work deduplication exists to avoid. A device A that is compromised, or shipping a bug in its hashing path, can publish a record labelled `H` whose plaintext is something else. Every device that later deduplicates against it silently backs up corrupt data.

Restore-time verification (FR-RST-002) catches this at the moment the user needs the file and the source is gone. In the meantime the status display reports healthy, because nothing verifies a segment the device believes it already has.

## Decision

### Identifiers — unchanged

| Identifier | Derivation | Exposure |
|------------|-----------|----------|
| Content identifier | Cryptographic hash of canonical plaintext | Trust boundary only |
| Object identifier | Keyed function of content identifier and object type, under the repository content-ID key | Stores, indexes, footers, manifests |

### Trust domains — new

| Domain | Behaviour | Default |
|--------|-----------|---------|
| `device` | Reuse only segments this device wrote | ✅ |
| `repository` | Reuse any member's segments, after **verify-on-reuse**: fetch, decrypt, confirm the content identifier before referencing | Opt-in |
| `repository-unverified` | Reuse without verification | Opt-in, explicit acknowledgement required |

The domain is recorded in the policy manifest and participates in the dedup index key.

## Rationale

`device` is the default because it costs **nothing at all** in the single-device case, which is the overwhelmingly common one, and because the failure it prevents is silent, permanent, and only discovered when it is too late to do anything about it. A default that is free in the common case and prevents an unrecoverable failure in the uncommon one is not a difficult call.

`repository` is the setting for the case that motivated cross-device deduplication in the first place — a household backing up four laptops sharing an OS and a music library. Verify-on-reuse costs one read on first reuse and still avoids the re-upload and the duplicate storage, so most of the benefit survives.

`repository-unverified` exists because there are deployments — a single administrator, uniform managed devices — where every writer genuinely is equally trusted. It is never a default and never silent.

## Consequences

**Positive**

- A hostile or buggy writer cannot corrupt another device's backup under the default.
- Detection moves from restore time to write time, where it is actionable.
- The trust assumption becomes explicit and configurable instead of implicit and universal.

**Negative**

- Multi-device repositories on the default store some duplicate data. Users who want the saving opt in.
- `repository` adds a read per first reuse, and the catalogue must track which segments have been verified so the cost is paid once.

**Residual**

In any domain other than `device`, a member can determine whether another member has backed up a *known* file by observing whether deduplication hits. This is inherent to cross-device deduplication, not a defect of this scheme, and `device` closes it. Recorded as [T-12](../threat-model.md#t-12-dedup-confirmation-by-a-repository-member).

## Alternatives considered

**Cross-device dedup with restore-time verification only.** The original design. Rejected: detection arrives after the source data is gone.

**Per-device content-ID keys.** Would make cross-device dedup impossible rather than optional — the same effect as `device` mode, with no path to enabling it later. Rejected as less flexible for no security gain.

**Signed segment records attributing each to its writer.** Attribution without prevention: it identifies who corrupted the data after the fact, and does not stop the corruption. Worth adding as a forensic aid, not as the mitigation.

**Always verify on reuse, no domains.** Rejected — imposes the read cost on single-device repositories, where there is no other writer and therefore no threat.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |

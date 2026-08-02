# ADR-0006 — Object identifiers and deduplication trust domains

**Status:** Accepted (amended 2026-08 after [pressure test](../review/2026-08-fix-pressure-test.md))
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
| `device` | Reuse only segments this device wrote | Opt-in (hardened) |
| `repository` | Reuse any member's segments, after **verify-on-reuse**: fetch, decrypt, confirm the content identifier before referencing | ✅ |
| `repository-unverified` | Reuse without verification | Opt-in, explicit acknowledgement required |

The domain is recorded in the policy manifest and participates in the dedup index key.

## Rationale

`repository` is the default because in a single-writer repository it **degenerates exactly to `device` behaviour at zero cost** — there are no other writers' segments to verify — while in a multi-device repository it preserves the deduplication that motivated the feature.

### Amendment — the default was `device`, and the argument for it did not hold

The original version of this ADR made `device` the default, reasoning that it "costs nothing at all in the single-device case, which is the overwhelmingly common one".

The premise is true; the conclusion does not follow. `repository` also costs nothing in the single-device case, for the reason above. The argument therefore selected between two options on a criterion where they are identical, and said nothing about the case where they actually differ — the multi-device household backing up four laptops that share an operating system and a music library, where `device` stores four copies. That is CrashPlan's classic use case and this project's stated reason for existing ([PT-11](../review/2026-08-fix-pressure-test.md#pt-11--the-stated-rationale-for-the-device-dedup-default-does-not-distinguish-it-from-repository)).

The default is therefore `repository`: free where `device` is free, cheaper where they differ, and it keeps the integrity guarantee that motivated the trust domains in the first place. The residual cost is one read per first reuse, plus the confirmation side channel below — for which `device` remains available as the hardened setting.

`device` is the right choice for anyone who wants to close that side channel entirely, or who does not want their device reading another member's data even to verify it.

`repository-unverified` exists because there are deployments — a single administrator, uniform managed devices — where every writer genuinely is equally trusted. It is never a default and never silent.

### Amendment — state that outlives the catalogue

`device` must know which segments this device wrote; `repository` must remember what it has verified. Both are catalogue state, and the catalogue is disposable ([ADR-0010](0010-local-store-separation.md)), so both need a recovery story that the original ADR did not give ([PT-12](../review/2026-08-fix-pressure-test.md#pt-12--device-attribution-and-verify-on-reuse-state-live-only-in-a-disposable-cache)):

- **Writer attribution is recoverable** from `writer_id` on index deltas, and writer attribution is part of the dedup lookup key (FR-MAN-006).
- **Verification outcomes are recorded durably**, so deleting the catalogue does not silently re-impose full re-verification.

## Consequences

**Positive**

- A hostile or buggy writer cannot corrupt another device's backup under the default.
- Detection moves from restore time to write time, where it is actionable.
- The trust assumption becomes explicit and configurable instead of implicit and universal.

**Negative**

- The default adds a read per first reuse of another writer's segment, and verification outcomes must be recorded durably so the cost is paid once rather than after every catalogue rebuild.
- The default exposes the confirmation side channel below. Users who care opt into `device`, at the cost of duplicate storage across their devices.

**Residual**

In any domain other than `device`, a member can determine whether another member has backed up a *known* file by observing whether deduplication hits. This is inherent to cross-device deduplication, not a defect of this scheme, and `device` closes it. Because `repository` is now the default, this residual applies by default in multi-device repositories and must be stated in the UI where the domain is chosen. Recorded as [T-12](../threat-model.md#t-12-dedup-confirmation-by-a-repository-member).

## Alternatives considered

**Cross-device dedup with restore-time verification only.** The original design. Rejected: detection arrives after the source data is gone.

**Per-device content-ID keys.** Would make cross-device dedup impossible rather than optional — the same effect as `device` mode, with no path to enabling it later. Rejected as less flexible for no security gain.

**Signed segment records attributing each to its writer.** Attribution without prevention: it identifies who corrupted the data after the fact, and does not stop the corruption. Worth adding as a forensic aid, not as the mitigation.

**Always verify on reuse, no domains.** Rejected — it removes the hardened option for users who do not want their device reading another member's data at all, and it forecloses `repository-unverified` for uniformly trusted fleets. Note that with `repository` as the default, this is now close to the shipped behaviour; what the domains add is the two ends of the range, not the middle.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-08 | Accepted (amended) | Default changed from `device` to `repository` — the original rationale did not distinguish the two (PT-11). Writer-attribution and verification-state recovery specified (PT-12). Three-domain model itself unchanged. |

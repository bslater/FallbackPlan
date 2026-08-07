# ADR-0013 — Recovery kit contents and format

**Status:** Proposed · Implemented — see [implementation status](../implementation-status.md#by-decision)
**Date:** 2026-08
**Requirements:** FR-KIT-001..006, NFR-OPS-005, NFR-SEC-006
**Review finding:** [H4](../review/2026-08-architecture-review.md#h4--the-recovery-kit-is-load-bearing-but-never-specified)

---

## Context

The recovery kit appears throughout the proposal as the thing that makes recovery possible. It is a release gate ("a repository can be restored from a clean machine using only repository access and a recovery kit"), its format is independently versioned, confirming it is a mandatory first-run step, and the emergency tool consumes it.

Its entire specification was one line: "Export containing repository identity, key material **or wrapped keys**, format details, and recovery instructions."

That disjunction is the crux, and it was left open. Bare key material means a stolen kit is game over. Wrapped key material means the kit is one factor and the passphrase the other. Nothing else was pinned down either — what identifies the repository, how a user gets from a printed kit back to a working restore, whether it can be split, or what happens when a kit's format version predates the repository's.

A release gate that depends on an unspecified artefact is not a gate.

## Decision

### Contents

| Field | Purpose | Sensitivity |
|-------|---------|-------------|
| Kit format version | Lets a future tool parse an old kit | — |
| Minimum recovery-tool version | Refuse rather than misread | — |
| Repository ID | Identifies which repository this opens | Low |
| Repository format profile | Check compatibility before starting | — |
| **Wrapped** repository master key | Key material, encrypted under the KEK | High — useless without the passphrase |
| KDF parameters (Argon2id salt, memory, iterations, parallelism) | Reproduce the KEK from the passphrase | — |
| Destination descriptors | Where the repository lives: endpoint, container/bucket, prefix | Low |
| Issuing device public identity | Names the issuing device | — |
| Issue timestamp | Detect an outdated kit | — |
| Recovery instructions | Step by step, embedded | — |
| Integrity checksum | Detect transcription errors | — |

### Excluded

- **The passphrase.** The kit is one factor. A stolen kit alone does not open the repository.
- **Store credentials.** The kit says *where*, never *how to authenticate*. A kit found on a printout must not grant access to the user's cloud account.
- **The device private key.** Recovery does not need it; a recovering device establishes a new identity and is re-authorised ([ADR-0010](0010-local-store-separation.md)).

### Representations

**Printable** — QR code plus checksummed, hand-transcribable text. It must survive a printer, a filing cabinet, a decade, and a person typing it back in. The checksum is what makes the last of those survivable.

**Machine-readable** — a single file for a password manager or an encrypted USB stick.

Identical content. Both embed their own instructions, because when the kit is needed no other documentation is reachable — that is the scenario it exists for.

### Lifecycle

Generated at first-run setup with explicit confirmation before setup completes · regenerated when destinations change materially, with the old kit marked stale · status surfaced continuously (never generated / saved / stale) · recovery drills supported and prompted.

## Consequences

**Positive**

- Clean-machine recovery becomes testable, and therefore a real gate.
- A stolen kit is not sufficient to read the repository.
- A kit found years later is self-explanatory, which is when kits are actually used.

**Negative**

- Users must remember the passphrase **and** keep the kit. A kit alone is not enough, and that must be said plainly at generation time rather than discovered at recovery time.
- Destination changes make kits stale, requiring regeneration. An old kit still *opens* the repository; it may not know where all of it is.

**Neutral**

- The kit is not a backup of configuration. Restoring *data* needs repository plus kit; resuming *operation* additionally needs configuration and re-pairing ([`../architecture/08-restore-and-recovery.md` §6](../architecture/08-restore-and-recovery.md#6-what-must-survive-a-clean-machine)).

## Alternatives considered

**Bare master key in the kit.** Rejected. Single-factor, and a printed kit becomes as sensitive as the data itself — which no user will treat it as.

**Kit including store credentials.** Convenient for recovery, unacceptable for exposure: a printout in a drawer would grant access to a cloud account. Rejected.

**Shamir-split kits.** Genuinely useful for high-value repositories, and consistent with the format. Deferred — the complexity is not justified for the consumer default, and it can be added as a kit format version.

**No printable representation.** Rejected. A digital-only kit stored on the machine being backed up is not a recovery kit at all.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |

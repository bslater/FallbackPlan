# ADR-0013 — Recovery kit contents and format

**Status:** Accepted · Implemented — see [implementation status](../implementation-status.md#by-decision)
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

## Amendment (2026-08): the kit describes an installation, not a repository

The contents table above opens with a **Repository ID**, and the lifecycle
line says the kit is "generated at first-run setup". Those two sentences
cannot both be satisfied. A repository id means an archive, an archive means
a backup set, a set means a destination — so honouring the identity field
would have pushed the whole of §16.1's nine steps into a ceremony that exists
to capture one passphrase. Building [ADR-0044](0044-first-run-setup.md)
brought the collision into the open.

The resolution is that the identity field was the mistaken half. Under
ADR-0042 a write-only installation's keys derive from
`root = Argon2id(passphrase, salt, params)` with **no repository identifier
anywhere in the derivation**, and [ADR-0044](0044-first-run-setup.md) makes
one root serve every archive an installation writes. So everything this kit
exists to carry is known the moment the passphrase is chosen, and the id was
never load-bearing for opening anything — only for saying which archive the
kit came from, which is precisely the thing that stops one kit opening the
others.

**Kit format v2** therefore drops three fields, and each absence is a
statement rather than an economy:

- **repository id** — the archive supplies its own, from the descriptor the
  recovering tool reads off the store it was pointed at. One installation,
  one passphrase, one keypair, one kit; a kit that named one archive could
  not open the rest.
- **wrapped master key** — a write-only repository has none. This half was
  already recorded in §2.1 of the specification.
- **destination descriptors** — the kit is generated before any destination
  is declared. A field that is empty in every kit anyone will hold teaches a
  reader nothing, and *where the archives live* is knowledge the operator has
  while *how to re-derive* is the only thing the kit can uniquely carry.

What the kit gains is proof in both directions: the derived sealing public
key is compared against the kit's copy **and** the archive's descriptor, so a
wrong passphrase and a wrong archive fail differently and by name.

**v1 kits are unaffected** — they remain valid and keep opening the
repository they name. The consequence recorded above, that "destination
changes make kits stale, requiring regeneration", no longer applies to a v2
kit, which has no destinations to go stale. Whether an installation kit can
go stale at all is left open rather than answered by omission
(FR-KIT-005 remains unmet).

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-08 | Accepted | Built, specified as [`specifications/recovery-kit/`](../../specifications/recovery-kit/README.md), conformance-fixtured, and exercised by the clean-machine drill — restore from store plus kit plus passphrase, with per-line transcription checks. Shamir splitting stays deferred rather than rejected. |
| 2026-08 | Amended | Kit format v2 describes an **installation** rather than a repository ([ADR-0044](0044-first-run-setup.md)): repository id, wrapped key and destinations are dropped, the archive supplies its own identity from its descriptor, and one kit opens every archive the passphrase wrote. v1 kits are untouched |

# ADR-0010 — Local store separation

**Status:** Accepted · Implemented — see [implementation status](../implementation-status.md#by-decision)
**Date:** 2026-08
**Requirements:** NFR-REL-002, NFR-REL-007, NFR-OPS-003, FR-MAN-002
**Review finding:** [H3](../review/2026-08-architecture-review.md#h3--disposable-conflates-three-stores-with-incompatible-durability-requirements)

---

## Context

The proposal specified "SQLite for disposable local cache, job state, and UI configuration — not repository authority", and NFR-REL-002 stated that "deleting or corrupting it shall not cause repository data loss".

The catalogue genuinely is disposable, and insisting on that is right — it is the direct answer to the surveyed product's failure mode the proposal identifies. But three things were placed in one sentence and one database, and they do not share the property:

- The **device private key** is not rebuildable from the repository. Losing it means the device loses its identity, and every pairing must be re-approved by hand at the other end.
- **Pairing grants and destination authorisations** are likewise not derivable from repository contents.
- **Job history and schedules** are not needed for recovery, but silently losing them means backups stop happening — and nothing alerts, because the thing that would have alerted is gone too.

NFR-REL-002 was therefore true of the catalogue and false of its housemates. Anyone acting on it — a support article saying "delete the database and let it rebuild", or the rebuild tooling itself — would destroy the device identity while following documented advice.

## Decision

Three stores, three lifecycles, **separate on disk**:

| Store | Contents | Rebuildable | Loss consequence |
|-------|----------|-------------|------------------|
| **Catalogue** | Path, version, segment, blob, generation indexes; watermarks | ✅ From repository | Slow rebuild; no data loss |
| **Durable local state** | Device keypair, pairing grants, destination authorisations, job history | ❌ | Device identity lost; pairings must be re-approved manually |
| **Configuration** | Backup sets, schedules, policies, provider settings | Partially — policy manifests record what each snapshot used | Backups silently stop |

Separate files, not separate tables in one file, so "delete the catalogue and let it rebuild" cannot take the device identity with it.

**Catalogue:** SQLite behind an abstraction so another embedded engine can replace it. Never repository authority.

**Durable local state:** separate store, OS-key-store protected where available. The device *private key* is never written to the recovery kit — a recovering device establishes a new identity and is re-authorised.

**Configuration:** file-based, schema-versioned, validated before use, exportable without secrets. Files rather than a database because users edit, version-control, and diff them.

## Consequences

**Positive**

- NFR-REL-002 becomes true as stated, because it is now scoped to the thing it is true of.
- Deleting the catalogue — a legitimate, documented recovery action — is safe.
- Each store gets the protection it warrants: the catalogue none, durable state key-store protection, configuration file permissions.
- Configuration is version-controllable, which advanced users will do regardless.

**Negative**

- Three stores to manage, back up, and reason about instead of one.
- Users must be told that durable local state is separately backed up or re-established by re-pairing — an extra concept in the recovery story.

**Neutral**

- Recovery of **data** needs only repository plus kit. Recovery of **operation** additionally needs configuration and re-pairing. Stating the difference plainly is better than implying restore-and-done, which leaves a user believing they are protected when they are not.

## Alternatives considered

**One database, with NFR-REL-002 narrowed to specific tables.** Rejected — the property becomes unenforceable in practice, and any tool or instruction that deletes the file destroys the device identity.

**Device identity in the recovery kit.** Rejected. It would let a stolen kit impersonate the device to its destinations, and it is unnecessary: a new device establishing a new identity is the correct recovery model.

**Configuration in the repository.** Attractive — it would survive a clean machine — but it would leak backup-set names and destination endpoints into a store we treat as untrusted, and it would make configuration edits repository writes. Rejected. Policy *manifests* already record what each snapshot used, which covers the audit need.

## Amendment 1 (2026-08) — where the hub-and-spoke state lands in the split

[ADR-0034](0034-hub-and-spoke-destinations.md) adds three kinds of client-side
state, and each slots into an existing row of the table rather than a new one.
**Destination declarations** — names, kinds, paths, peer fingerprints and
endpoints, per-set references and retention policies — are **configuration**:
user-edited, schema-versioned, exportable. The export guidance changes with
them: the file still holds no secrets, but it now names who stores your backups
and where, so "exportable without secrets" is no longer "shareable without
thought", and the docs say so. **Per-destination sync state** (what each
destination holds, when it was last reached, why it last failed) and **notices**
(a peering ended, terms narrowed, a quota was hit) are journal-shaped files
beside `jobs.json`: not rebuildable from the repository, tolerable to lose —
sync state re-derives from a destination inventory pass, and a lost notice is
re-raised by the condition still holding — and therefore deliberately outside
the durable-state file whose loss costs the device its identity.

The rejected alternative "configuration in the repository" stays rejected, and
gains its sharpest example yet: destination endpoints in the repository would
hand every destination the list of all the others.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | |
| 2026-08 | Accepted | Built and held to: `LocalStateSeparationTests` deletes the catalogue and asserts device identity and configuration survive, which is the whole claim. Nothing about the three-way split awaits a later phase. |
| 2026-08 | Accepted (amended) | Amendment 1: destinations are configuration, sync state and notices are sacrificial journals beside `jobs.json` ([ADR-0034](0034-hub-and-spoke-destinations.md)). |

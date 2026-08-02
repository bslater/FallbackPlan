# FallbackPlan repository format — specification

**Format version:** 1 (draft) · **Status:** unfrozen — see [stability](#stability)

---

## What this is

The normative on-disk and on-store format for a FallbackPlan repository. It is written to be implementable by someone who has never read the project's architecture documents, in a language other than C#, without access to the reference implementation.

That is not an aspiration. It is a release gate: NFR-COMP-004 and item 2 of the [format v1 freeze gate](../../docs/roadmap.md#format-v1-freeze-gate) require a reader written from this specification alone, by an author who did not write the format, to pass the conformance suite. If you find yourself unable to implement something from what is written here, that is a defect in this specification — please report it.

## Authority

| Question | Authority |
|----------|-----------|
| What bytes go where | **This specification** |
| Why it is that way | [`docs/architecture/`](../../docs/architecture/) and [`docs/adr/`](../../docs/adr/) |
| What the system must do | [`docs/requirements/`](../../docs/requirements/) |

Where this specification and an architecture document disagree about **format**, this specification wins and the architecture document is stale. Where they disagree about **rationale**, the architecture document wins — rationale is deliberately not duplicated here, only linked.

## Documents

| # | Document | Covers |
|---|----------|--------|
| — | [Conventions](00-conventions.md) | Notation, byte order, primitive encodings, versioning and feature negotiation |
| 01 | [Object layout](01-object-layout.md) | Store namespace, the `/repository-format` bootstrap object |
| 02 | [Identifiers](02-identifiers.md) | Content identifiers, keyed object identifiers, blob identifiers |
| 03 | [Keys](03-keys.md) | Key hierarchy, derivation, wrapping, generations |
| 04 | [Records](04-record.md) | Record framing, AEAD, nonce and AAD construction |
| 05 | [Blobs](05-blob.md) | Cleartext envelope, record sequence, recovery footer, digest |
| 06 | [Manifests](06-manifests.md) | Segment references, file-version, tree, snapshot, policy, error |
| 07 | [Index](07-index.md) | Deltas, checkpoints, precedence, supersession, void deltas |
| 08 | [Journal](08-journal.md) | Write intents, retirement, expiry, audit records |
| 09 | [Segmentation](09-segmentation.md) | `fixed-v1` and `cdc-v1` profiles |
| 10 | [Compression](10-compression.md) | Zstandard profile and the storage threshold |

Conformance material is in [`conformance/`](conformance/README.md).

## Requirement language

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHALL NOT**, **SHOULD**, **SHOULD NOT**, **RECOMMENDED**, **MAY** and **OPTIONAL** are to be interpreted as described in [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119) and [RFC 8174](https://www.rfc-editor.org/rfc/rfc8174), when and only when they appear in capitals.

A **conforming reader** MUST be able to restore any file version from a repository written by a conforming writer at the same format version and feature set. A **conforming writer** MUST produce objects that a conforming reader accepts.

Readers and writers MUST refuse a repository that requires a feature they do not implement, and MUST NOT attempt partial interpretation of an object they do not fully understand. Refusing is recoverable; misreading is not.

## Design constraints a reader should know up front

These shape almost every decision in the documents that follow. Each links to its rationale.

1. **Everything durable is encrypted.** There is no plaintext mode and none is offered as a compatibility switch. → [`03-crypto.md`](../../docs/architecture/03-crypto.md#1-rules)
2. **Manifests carry no physical location.** A segment reference names a segment by object identifier; resolving that to a blob and offset is the index's job. This is what allows blob compaction to relocate records without rewriting immutable objects. → [ADR-0007](../../docs/adr/0007-logical-object-identifiers-in-manifests.md)
3. **Blob identifiers are writer-allocated and opaque**, while record identifiers are content-derived and keyed. The asymmetry is required: a writer must name blobs in a journal record before creating them. → [ADR-0016](../../docs/adr/0016-blob-identifier-formation.md)
4. **Every blob has its own encryption key**, so nonce uniqueness holds within a blob rather than across the repository. → [ADR-0005](../../docs/adr/0005-aead-suite-and-nonce-construction.md)
5. **Index entries are ordered by generation, not by arrival.** Compaction republishes an object identifier at a new location, so entries are not commutative and precedence is explicit. → [ADR-0017](../../docs/adr/0017-index-entry-supersession.md)
6. **No object's size grows with the repository.** There is no monolithic manifest, index, or catalogue anywhere in this format.

## Stability

Format version 1 is **not frozen**. Repositories created by pre-1.0 builds carry **no forward-compatibility guarantee** — a later build may be unable to read them, and may or may not ship a migration path.

Builds MUST warn at repository creation while this remains true. The format version is always recorded, so a build that cannot read a repository refuses it rather than misreading it.

The conditions for freezing are listed in the [freeze gate](../../docs/roadmap.md#format-v1-freeze-gate). Governing decision: [ADR-0014](../../docs/adr/0014-format-versioning-and-stability.md).

## Open questions affecting this specification

| Question | Effect if answered differently |
|----------|-------------------------------|
| [Q4 — canonical encoding](../../docs/open-questions.md#q4--canonical-metadata-encoding) | Canonical CBOR is used throughout §06–§08; a different encoding changes every metadata object |
| [Q5 — segmentation default](../../docs/open-questions.md#q5--segmentation-default) | Both profiles are specified; only which is *default* is open |
| [Q6 — segment hash](../../docs/open-questions.md#q6--segment-hash-function) | SHA-256 is the v1 profile; the profile field permits another |
| [Q11 — physical hints](../../docs/open-questions.md#q11--physical-hints-in-segment-references) | Would add an optional non-authoritative field to segment references (§06) |

# ADR-0016 — Blob identifier formation

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-SNP-004, FR-ARCH-012, FR-GC-003
**Pressure-test finding:** [PT-4](../review/2026-08-fix-pressure-test.md#pt-4--blob-identifier-formation-is-unspecified-and-c4-cannot-be-implemented-without-it)

---

## Context

Every *record* identifier in the format is derived from content and then keyed ([ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md)). The format never said how **blob** identifiers are formed — §5.3 said only that a blob is uploaded "under its final immutable identifier".

Left unstated, a reader would reasonably infer that blob identifiers are content-derived too. If they were, [ADR-0009](0009-garbage-collection-safety.md) could not be implemented: a write-intent record must name the blobs a job will create *before* creating them, and an identifier derived from content that does not yet exist cannot be known in advance.

The gap therefore blocked the mechanism that protects in-flight work from garbage collection.

## Decision

**Blob identifiers are writer-allocated and opaque.** They are random, or derived from `(writer_id, sequence)` — and they are **not** content-derived.

A writer pre-allocates identifiers, names them in its write intent, and then creates the blobs. Identifiers are unique across the repository by construction, since `writer_id` is unique and sequences are per-writer and monotonic.

This is a deliberate asymmetry, and the specification calls it out for independent implementers:

| Object | Identifier | Rationale |
|--------|-----------|-----------|
| Segment / metadata **record** | Content-derived, then keyed | Deduplication and verification address records |
| **Blob** | Writer-allocated, opaque | A blob is a container; nothing addresses it by content |

## Rationale

Content addressing earns its cost where content identity is the question being asked. For records that is exactly the question: is this segment the same as one we already hold? For blobs it is never the question. Nothing deduplicates blobs, nothing verifies a blob by recomputing its identity, and two blobs holding the same records in a different order are not interchangeable in any useful sense.

What blob identifiers must do is be unique, be allocatable in advance, and be stable once assigned. Writer allocation delivers all three, and it costs nothing that content addressing would have provided.

Uniqueness is the property to be careful about, and it does not rest on randomness alone: `(writer_id, sequence)` is unique by construction because writer identity is unique and the journal sequence is gapless and monotonic per writer. An implementation may add random bits to avoid leaking job size through identifier density, but must not rely on randomness for uniqueness.

## Consequences

**Positive**

- Write intents can name blobs before they exist, which is what [ADR-0009](0009-garbage-collection-safety.md) requires.
- Identifier allocation needs no coordination between writers.
- Sealing a blob does not have to complete before its identifier is usable, so intent publication and blob assembly can overlap.

**Negative**

- Blob identifiers carry no integrity property of their own. Integrity comes from the blob digest and the authenticated recovery footer ([`../architecture/02-repository-format.md` §5.2](../architecture/02-repository-format.md#52-layout)), not from the name — and readers must not assume otherwise.
- Identifiers must avoid leaking writer identity or job structure to the store. Derivation from `(writer_id, sequence)` should be keyed or randomised before use as a store key, consistent with the rule that store-visible identifiers reveal nothing ([`../architecture/02-repository-format.md` §2](../architecture/02-repository-format.md#2-object-classes)).

**Neutral**

- A writer that allocates identifiers and then abandons the job leaves them unused. Harmless: they are never reused, and the intent expires.

## Alternatives considered

**Content-derived blob identifiers.** Rejected — incompatible with write intents, and it buys nothing, because no operation addresses a blob by its content.

**Identifier assigned by the store on upload.** Rejected. Not all providers return a usable identifier, it cannot be known before upload, and it would make the repository's object graph depend on provider behaviour, which NFR-COMP-005 forbids.

**Content-derived, with intents naming a writer-scoped namespace instead of specific blobs.** Workable — the collector would protect a whole namespace rather than named blobs. Rejected as strictly less precise: it protects blobs that do not exist and gives the dry-run report nothing specific to say.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Forced into the open by PT-4; no viable alternative preserves the write-intent mechanism |

# ADR-0012 — Storage provider contract

**Status:** Proposed
**Date:** 2026-08
**Requirements:** NFR-PORT-004, NFR-REL-005, NFR-COMP-005, FR-REP-002, FR-QUOTA-001
**Review finding:** [H7](../review/2026-08-architecture-review.md#h7--the-sample-interfaces-contradict-the-requirements-they-illustrate)

---

## Context

`IObjectStore` is the boundary between the repository engine and every storage backend. The proposal's sketch was explicitly conceptual, but three of its properties were semantic rather than cosmetic, and each encodes a decision that gets expensive to reverse once several providers are implemented against it.

1. **`PutAsync(ObjectKey, Stream, …)` cannot be retried.** After a failed upload the stream is partially consumed, and the caller — who has been streaming encrypted segment records through it — usually cannot reproduce it. Yet NFR-REL-005, throttling handling, and FR-REP-003 all assume retry. Left ambiguous, each provider invents its own buffering workaround and they differ.
2. **Errors are exceptions.** Nothing returns a status, so conditional create — the primitive publication depends on — reports "already exists" by throwing. That makes the most common *expected* outcome an exception path, contradicting NFR-PORT-004.
3. **Continuation is expressed twice.** `IAsyncEnumerable` already models resumable iteration; a continuation-token parameter beside it means every provider must decide which wins.

## Decision

```csharp
public interface IObjectStore
{
    StoreCapabilities Capabilities { get; }

    ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken ct);

    ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken ct);

    ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken ct);

    IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken ct);

    ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken ct);
}

public enum PutOutcome { Created, AlreadyExists, PreconditionFailed }
```

1. **Content is a re-openable factory.** The caller guarantees it can produce the content again; the provider may call it as many times as its retry policy requires.
2. **Expected outcomes are results; faults are exceptions.** `Created`, `AlreadyExists`, `PreconditionFailed` are results. Network failure, authentication failure, and provider errors remain exceptions.
3. **Continuation belongs to the enumerator.** Callers needing to persist a position across process restarts read a resume token from `ObjectEntry`.
4. **Capabilities are probed once** and exposed as a property, never on the data path.

### The core assumes nothing

No filesystem rename. No strong listing consistency. No provider checksums. No mutable objects. Each is absent from at least one provider we intend to support, and designing around their absence is why the format works on all of them.

Two capabilities change engine behaviour rather than merely informing it: `ConditionalCreate = false` (publication relies on unique final identifiers instead) and `RangedReads = false` (restore fetches whole blobs, and the restore plan reports the cost up front rather than surprising the user with a slow transfer).

## Consequences

**Positive**

- Retry semantics are stated once, in the contract, rather than reinvented per provider.
- Conditional create — used on every publication path — stops being an exception path.
- Callers cannot accidentally depend on capabilities a provider lacks, because the capability is visible.

**Negative**

- Callers must supply re-openable content, which occasionally means spooling to disk before upload. The blob spool already does this ([`../architecture/02-repository-format.md` §5.3](../architecture/02-repository-format.md#53-spooling-and-sealing)), so the cost is already paid.
- `StoreCapabilities` will grow as providers are added. Additive, and better than discovering the difference at runtime.

## Contract suite

A provider is not supported until it passes, including the simulated-fault cases: conditional creation · range reads · interrupted upload · listing pagination · duplicate writes · stale metadata · eventual-visibility simulation · deletion batching · retries and throttling · checksum mismatch · credential expiry mid-operation · multipart abandonment and cleanup · object-size limits · disk-full and quota exhaustion.

The eventual-visibility and quota cases matter most: the engine's correctness arguments explicitly depend on handling both, and neither reproduces reliably against a real provider on demand.

## Alternatives considered

**Seekable-stream requirement instead of a factory.** Simpler, but forces every caller to materialise content as a seekable stream even when it could be regenerated more cheaply.

**Result types for everything, no exceptions.** Rejected as noise: it forces every caller to handle transport faults inline where an exception is the right tool.

**Provider-specific interfaces.** Rejected — it is how provider capabilities leak into repository semantics, which NFR-COMP-005 forbids.

## Amendment 1 — the concrete shapes, as implemented (Wave A6)

The first implementation (`FallbackPlan.Storage.Abstractions` + the local filesystem provider) fixed the supporting-type shapes this ADR named but did not define. Recorded here because the next provider author will look here first:

- **`ObjectKey` grammar** — one or more `/`-separated components of `[a-z0-9._-]`, no component starting with `.`, component ≤ 255 chars, total ≤ 1024. Traversal (`..`) and hidden paths are *unconstructible*, which is the first line of the local provider's path-safety defence; providers get the same guarantee for free.
- **Immutability at the contract level** — a put against any existing key reports `PutOutcome.AlreadyExists` (never overwrites, never throws), unconditionally: store objects are immutable (spec 01 §4) and the idempotent-retry read of §2.2 applies to the unconditional path too. `PutConditions.IfNotExists` exists for providers whose conditional-create needs to be explicit; `PreconditionFailed` is reserved for genuinely conditional operations arriving with later providers.
- **Results** — `GetMetadataResult` (found/not-found + `ObjectMetadata(Length, LastModified?)`), `OpenReadResult` (`Found`/`NotFound`/`RangeNotSatisfiable`, disposable, owning the stream when found), `DeleteResult` (`Deleted`/`NotFound`/`PreconditionFailed`).
- **Listing** — ordinal key order is part of the contract; `ObjectEntry.ResumeToken` is opaque (the local provider uses the key itself) and `ListOptions.ResumeAfter` resumes strictly after the token's entry across enumerator instances and process restarts.
- **Local provider durability** — contents are flushed to disk before the temp-to-final rename publishes an object; the directory *entry* is best-effort (no directory fsync in .NET), which is acceptable because write intents and publication ordering (Waves C–D) make entry loss detectable.

The contract suite named below now exists as `ObjectStoreContractTests`, inherited per provider; the simulated-fault cases (throttling, credential expiry, eventual visibility, quota) remain to be added alongside the first remote provider.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Revisit after the first two providers are implemented |
| 2026-08 | Proposed (amended) | Amendment 1: concrete type shapes fixed by the Wave A6 implementation |

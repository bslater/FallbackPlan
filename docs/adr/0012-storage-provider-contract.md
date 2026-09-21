# ADR-0012 — Storage provider contract

**Status:** Proposed · Partly implemented — see [implementation status](../implementation-status.md#by-decision)
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

## Amendment 2 (2026-08) — the contract is also the fan-out seam

[ADR-0034](0034-hub-and-spoke-destinations.md) makes every destination an
object store holding a whole-archive replica, which promotes this contract from
"the engine's storage boundary" to **the seam the hub copies archives across**:
a store-to-store copier reads from the staging archive's store and writes to a
destination's through nothing but this interface. That is the design working as
intended — a cloud provider becomes a destination kind by implementing
`IObjectStore` and passing the contract suite, with no fan-out code knowing
which kind it is — and it firms up two obligations that were latent:

- **`DeleteAsync` gets its first production caller** in hub-planned retention
  against local-path destinations, so the deletion semantics of Amendment 1
  (`Deleted`/`NotFound`/`PreconditionFailed`) stop being test-only surface.
- **The copier's ordering discipline is a caller obligation, stated here:**
  objects are copied in dependency order (blobs before the metadata that
  references them, heads last) and deleted in the reverse, so a destination
  interrupted at any byte is a lagging-but-valid replica, never a corrupt one.
  The contract itself stays order-free; the guarantee is the copier's, built on
  `PutOutcome.AlreadyExists` idempotency.

The simulated-fault cases the contract suite still lacks — throttling,
credential expiry, eventual visibility, quota — remain scheduled with the first
remote provider, and matter more now that a provider failure is a destination
failure a status matrix must classify.

## Amendment 3 (2026-09) — the capabilities are read, and one promise is withdrawn

This record has said since it was written that two capabilities "change engine
behaviour rather than merely informing it": `ConditionalCreate = false`, where
"publication relies on unique final identifiers instead", and
`RangedReads = false`, where "restore fetches whole blobs, and the restore plan
reports the cost up front". **Neither behaviour was ever built.** Every reader
of `Capabilities` in the product asked for `MaximumObjectSize`, and
`ListingConsistency` — the capability the correctness arguments lean on
hardest — was read by nothing at all.

**The withdrawal.** Those two alternative engine paths are not built and will
not be. Every provider this product intends to support has had conditional
create since 2024 (S3 `If-None-Match: *`, Azure `If-None-Match`, MinIO), so
they would be dead code guarding a case that does not arise, and a decision
record promising behaviour the code does not have is worse than a stated
incapacity. In their place the engine **checks**: `Repository/StoreAdmission`
answers why a store cannot be used, and `Repository/RepositoryLifecycle`
refuses by name on every create and open path.

Three things about that gate are decisions rather than details.

**It lives beside the engine, not beside `IObjectStore`.** The requirement
belongs to the consumer; an abstraction that stated what its consumers need
would have stopped being one, and the next consumer's needs would have had to
go there too. It is also not a seam in the service layer, because there is
none — `new LocalFileSystemObjectStore(...)` appears at twenty-three sites
across four projects, and a gate called at twenty-three sites is a gate that
will be forgotten. `RepositoryLifecycle` is the one place an arbitrary store is
handed to the engine.

**Reading and writing ask different questions.** A reader never puts, so
conditional create is nothing to it, and one real store here has no put at all:
a peer's replica over the retrieval session
([07 §1](../../specifications/peer-protocol/07-retrieval.md)), which is what an
adoption or a restore from a peer opens. A single requirement refused peer
adoption outright — `Hosts.Tests/PeerAdoptionTests` went red the first time the
gate ran — so the caller states its `StoreUse`, defaulting to writing because
that is the direction that fails closed.

**Discovery is not gated.** `ReadDescriptorAsync` is credential-free and
read-only ([ADR-0061](0061-adopt-a-destinations-archives.md)), and refusing a
store before the product can say what it found would help nobody.

**Listing consistency is now load-bearing too, and what it gates is deletion.**
Collection reasons from **absence** end to end — a blob is garbage because
nothing reachable names it, and what is reachable is what a listing of
`snapshots/` could enumerate. Against a store that cannot promise a listing
reflects what it holds, absence is not a fact, and a snapshot the listing has
not caught up to is indistinguishable from one that was never written: it
decodes perfectly, so it vetoes nothing, while its blobs read as fully dead.
Measured, three protected snapshots, newest concealed from `snapshots/` alone:
a pass that would have deleted nothing proposed two blobs and wrote the
tombstones. So `Retention/CollectionPlanner` vetoes on anything but
`Strong`, and `Retention/DestinationConvergence` refuses to build a keep-set
from such a listing — the same reasoning executed at a destination that may
hold the only other copy. Operations that reason from **presence** are
untouched and were pinned rather than changed, because they survive: a
replication pass re-offers and is refused, a stale head collides, an unseen
delta is an unresolved gap.

**The obligations this leaves.** The veto is not a permanent answer, and
lifting it needs an attested witness of completeness for the snapshot plane —
something that says "there are N snapshots" without enumerating them. The
catalogue cannot be it: it is a cache, and this design has always said it is
never the authority a deletion hangs off. ~~Second,
`Agent/DestinationShipSink.Capabilities` forwards the local metadata store's,
so a sink shipping to remote destinations reports the local filesystem's
promises; harmless while every destination is a local path, and owed as the
**intersection** of the metadata store's capabilities and every destination's
before one is an object store.~~ *(Done — Amendment 4.)* Third, this record stays *Proposed* for the
reason it always has — the second provider that disagrees with the contract
has not been written, and the eventual-visibility fault case is now an
instrument (`TestSupport/LaggingObjectStore`) and an answer rather than a
scheduled item, but still not a provider.

### Amendment 4 (2026-09): a store that stands in front of others promises the weakest answer they give

Amendment 3 recorded the ship sink's forwarded capabilities as owed and the
slice that recorded it deliberately did not fix them, on the grounds that
doing so would be a change with no test that could fail. That was true and
stopped being true in the same slice: `TestSupport/DegradedObjectStore`, built
there to drive the admission gate, is a store that promises less while
delegating everything — which is exactly the destination this rule needs.

`Storage.Abstractions/StoreCapabilities.Intersect` is the rule.
`Agent/DestinationShipSink` computes it once, where a run's targets are
resolved, over the metadata store's capabilities and every target's. Outside a
run it answers the metadata store's: nothing is being shipped and there is
nothing to promise less. A destination dropped mid-run is deliberately not
recomputed — dropping one can only remove a constraint, so the cached answer
stays at least as conservative as the survivors', which is the direction that
cannot mislead.

Booleans **and**. The listing consistency is the laggiest. The object and
metadata ceilings are the smallest accepted anywhere. The minimum storage
duration is the **longest** wait, being the one numeric member where weakest
means larger — a caller planning around early-deletion charges has to satisfy
every target.

`ArchivalTiers` is **or**ed, and the asymmetry is a decision rather than a
slip. It is a hazard, not a promise: it says rehydration latency applies.
Intersecting it away would have a fan-out claim that nothing it writes
archives while one of its targets does, which is the opposite of the
conservative answer every other member gives.

**The find, and why it belongs in this record.** With the sink telling the
truth, thirty-three tests went red at once: both peer adapters declared
`MaximumObjectSize = 0` — not as a statement, but by leaving the member at its
struct default — so every direct-ship run to a peer validated its capture
policy against a ceiling of nought and refused itself. It had been invisible
for exactly as long as the sink forwarded somebody else's answer. That is the
second defect of this shape in two slices (Amendment 3 found `PeerShipStore`
declaring no conditional create while implementing one), and both say the same
thing about this contract: **a capability nobody reads is a capability nobody
has to get right.** Making one load-bearing is what makes the declarations
true, and it finds the untrue ones by breaking.

Both adapters now declare what a local path declares, because the peer wire
sets no ceiling of its own — `ReplicationObject` carries a u64 length and
chunking is the transport's business. Declaring the format's own limit was
considered and rejected: it would turn another layer's constant into a promise
this adapter cannot keep updated.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Revisit after the first two providers are implemented |
| 2026-08 | Proposed (amended) | Amendment 1: concrete type shapes fixed by the Wave A6 implementation |
| 2026-08 | Proposed (amended) | Amendment 2: the contract is the fan-out seam — copier ordering stated, deletion activated by retention ([ADR-0034](0034-hub-and-spoke-destinations.md)) |
| 2026-09 | Proposed (amended) | Amendment 3: the two unimplemented capability behaviours withdrawn for a named refusal at `Repository/StoreAdmission`, split by whether the caller reads or writes; `ListingConsistency` made load-bearing, vetoing deletion that reasons from absence; the ship sink's forwarded capabilities and an attested completeness witness recorded as owed. Still *Proposed*: there is still one provider |
| 2026-09 | Proposed (amended) | Amendment 4: `Storage.Abstractions/StoreCapabilities`'s `Intersect` and `Agent/DestinationShipSink` promising the weakest answer its destinations give, Amendment 3's owed item discharged; the archival-tier hazard **or**ed where every other member is **and**ed; `Agent/PeerShipStore` and `Agent/PeerRetrievalObjectStore` found declaring a zero object ceiling by struct default, which nothing read until the sink stopped forwarding. Still *Proposed*: there is still one provider (`Storage.ContractTests/CapabilityIntersectionTests`, `Hosts.Tests/ShipSinkCapabilityTests`) |

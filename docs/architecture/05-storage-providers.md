# 05 — Storage providers

**Status:** draft · **Supersedes:** [original proposal](../review/2026-08-original-proposal.md) §9 · **Resolves:** [H7](../review/2026-08-architecture-review.md#h7--the-sample-interfaces-contradict-the-requirements-they-illustrate)

**Built:** Contract and local provider built; cloud providers are phase 3 — see [implementation status](../implementation-status.md).

---

## 1. Principles

The repository engine depends on a deliberately small interface. The core must **not** assume filesystem rename, strong listing consistency, provider checksums, or mutable objects — every one of those is absent from at least one provider we intend to support.

Provider capabilities are probed once and reported separately from the data path, so a capability check never sits inside a hot loop, and no provider-specific behaviour leaks into snapshot or file-version semantics (NFR-COMP-005).

> **Terminology.** A **repository blob** is our immutable container ([`01-domain-model.md`](01-domain-model.md#1-glossary)). A **store object** is the provider's unit of storage. Azure calls the latter a blob; this document does not.

## 2. The store interface

```csharp
public interface IObjectStore
{
    StoreCapabilities Capabilities { get; }

    ValueTask<GetMetadataResult> GetMetadataAsync(
        ObjectKey key,
        CancellationToken cancellationToken);

    ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key,
        ObjectRange? range,
        CancellationToken cancellationToken);

    // Content is a factory, not a Stream: a retry must be able to produce it again.
    ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken);

    // Continuation is owned by the enumerator. Entries carry a resume token
    // for callers that must persist a position across process restarts.
    IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        CancellationToken cancellationToken);

    ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key,
        DeleteConditions conditions,
        CancellationToken cancellationToken);
}
```

Three corrections to the original shape, each of which encodes a decision that would be expensive to reverse once providers exist.

### 2.1 Content is a factory

`PutAsync(… Stream content …)` cannot be retried. After a failed upload the stream is partially consumed, and the caller — who by then has streamed encrypted segment records through it — usually cannot reproduce it.

Every requirement around this method assumes retry: NFR-REL-005 ("resumable, cancellable, idempotent where practical"), throttling handling (§4.2), and FR-REP-003's resume at verified boundaries. Left ambiguous, each provider would invent its own buffering workaround and they would differ.

A factory states the contract explicitly: the caller guarantees it can produce the content again, and the provider may call it as many times as its retry policy requires.

### 2.2 Results, not exceptions

```csharp
public enum PutOutcome { Created, AlreadyExists, PreconditionFailed }
```

Conditional create is the primitive that publication and index compaction depend on ([`04-concurrency-and-publication.md` §5](04-concurrency-and-publication.md#5-publication-order)). "Already exists" is its most common *expected* outcome — an idempotent retry of a write that in fact succeeded. Reporting that by throwing makes the normal path an exception path and contradicts NFR-PORT-004's requirement for explicit result types.

Exceptions remain for genuine faults: network failure, authentication failure, provider error. The distinction is *expected outcome* versus *fault*.

### 2.3 Continuation belongs to the enumerator

`IAsyncEnumerable` already models resumable iteration. A continuation-token parameter alongside it forces every provider to decide which wins and leaves callers unable to tell whether re-enumerating resumes or restarts. Callers needing to persist a position across process restarts read a resume token from `ObjectEntry`.

## 3. Capabilities

```csharp
public sealed record StoreCapabilities
{
    public bool ConditionalCreate { get; init; }
    public bool ConditionalReplace { get; init; }
    public bool RangedReads { get; init; }
    public bool MultipartUpload { get; init; }
    public bool BatchDelete { get; init; }
    public bool ObjectVersioning { get; init; }
    public bool ObjectLock { get; init; }
    public bool ServerSideChecksums { get; init; }
    public ListingConsistency ListingConsistency { get; init; }
    public TimeSpan? MinimumStorageDuration { get; init; }
    public bool ArchivalTiers { get; init; }
    public long MaximumObjectSize { get; init; }
    public int MaximumMetadataBytes { get; init; }
}
```

Two capabilities change engine behaviour rather than merely informing it:

- **`ConditionalCreate = false`** — publication cannot rely on create-if-absent, so it relies on unique final object identifiers followed by index-delta and snapshot publication instead ([`02-repository-format.md` §5.3](02-repository-format.md#53-spooling-and-sealing)). This is why the format was designed not to need it.
- **`RangedReads = false`** — restore must fetch whole blobs rather than the ranges it needs. Correct but much more expensive, and the cost is surfaced in the restore plan ([`08-restore-and-recovery.md` §2](08-restore-and-recovery.md#2-restore-planning)) rather than discovered as a slow transfer.

`MinimumStorageDuration` and `ArchivalTiers` inform retention and garbage collection about early-deletion charges and rehydration latency. They never change what is *correct* to delete — only what is *advisable*, and when.

## 4. Providers

### 4.1 Local filesystem

Durable file creation with explicit flush · atomic temp-to-final rename where the filesystem supports it · permissions hardening on the repository directory · removable-media detection · filesystem capability reporting · protection against path traversal and symlink redirection when the repository path is attacker-influenced.

### 4.2 FallbackPlan peer

Speaks the [peer protocol](../../specifications/peer-protocol/README.md) rather than exposing raw filesystem access. Quota and authorisation controls, streamed uploads, ranged downloads, peer-side integrity verification, optional store-and-forward. A source device never receives unrestricted filesystem access to a destination ([`09-replication-and-peers.md` §3](09-replication-and-peers.md#3-pairing)).

### 4.3 Azure Blob Storage

`Azure.Storage.Blobs` · block blobs with staged block uploads above the multipart threshold · conditional creation via ETag or if-none-match · managed identity, workload identity, connection strings, and SAS · access-tier policy expressed separately from repository correctness.

### 4.4 Amazon S3 and S3-compatible

AWS SDK for .NET · multipart upload above the threshold · IAM roles, profiles, web identity, access keys, custom endpoints · object lock only through explicit repository policy.

S3-compatible implementations vary in conditional-operation semantics, checksum support, and listing consistency. A tested compatibility matrix is maintained per implementation, and a store whose behaviour cannot be established is treated as the weakest case rather than assumed compatible.

## 5. Request economics

Object stores charge per request, so request count is a first-class design concern, not a tuning detail.

- **Never one request per segment.** Segments are packed into blobs of 128 MiB by default ([`02-repository-format.md` §5.1](02-repository-format.md#51-purpose-and-sizing)).
- **Never enumerate to resolve a lookup.** The catalogue and index answer lookups; listing is an accelerator for finding recent checkpoints and a fallback for forensic rebuild ([`02-repository-format.md` §7](02-repository-format.md#7-index-architecture)).
- **Range reads on restore** so a single needed segment does not drag its whole blob across the network.
- **Batch deletes** where supported, in bounded batches.

Requests and PUTs per GB are measured against explicit targets — see [`../requirements/non-functional.md`](../requirements/non-functional.md#performance) NFR-PERF-008/009 — because §23 of the original proposal named request amplification as a major risk and named packing as the mitigation, without any way to detect the mitigation ceasing to work.

## 6. Contract tests

Every provider runs the same suite, including the simulated-fault cases. A provider is not supported until it passes:

conditional creation · range reads · interrupted upload · listing pagination · duplicate writes · stale metadata · eventual-visibility simulation · deletion batching · retries and throttling · checksum mismatch · credential expiry mid-operation · multipart abandonment and cleanup · object-size limits · **disk-full and quota exhaustion** (FR-QUOTA-001).

The eventual-visibility and quota cases matter most: both are conditions the engine's correctness arguments explicitly depend on handling, and neither reproduces reliably against a real provider on demand.

---

**Previous:** [04 — Concurrency and publication](04-concurrency-and-publication.md) · **Next:** [06 — Filesystem capture](06-filesystem-capture.md)

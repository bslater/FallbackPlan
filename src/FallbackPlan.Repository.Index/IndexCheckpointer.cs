using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Repository.Index;

/// <summary>
/// What a writer has learned of a repository's checkpoints: which it has read,
/// and which deltas they carry.
/// </summary>
/// <remarks>
/// A checkpointer consults it so that a run reads only the checkpoints
/// published since the last, rather than every one the repository holds. It
/// is a cache and nothing depends on it being right: a delta it wrongly counts
/// as carried is merely not checkpointed, and a load reads that delta from its
/// key as it always did.
/// </remarks>
public sealed class CheckpointCoverage
{
    private readonly Lock _gate = new();
    private readonly HashSet<ObjectKey> _read = [];
    private readonly HashSet<DeltaId> _carried = [];

    internal bool HasRead(ObjectKey checkpointKey)
    {
        lock (_gate)
        {
            return _read.Contains(checkpointKey);
        }
    }

    internal void Read(ObjectKey checkpointKey, IEnumerable<DeltaId> carried)
    {
        lock (_gate)
        {
            _read.Add(checkpointKey);
            _carried.UnionWith(carried);
        }
    }

    internal bool Carries(DeltaId deltaId)
    {
        lock (_gate)
        {
            return _carried.Contains(deltaId);
        }
    }
}

/// <summary>
/// Builds format-5 checkpoints (specification 07 §5; FR-MAN-008, FR-MAN-009;
/// [ADR-0097](../../docs/adr/0097-format-5-a-checkpoint-carries-its-deltas.md)):
/// once enough deltas sit outside every checkpoint, it reads each from its own
/// store, keeps those that open and verify, and publishes them verbatim in as
/// many checkpoints as the metadata limit needs.
/// </summary>
/// <remarks>
/// <para>
/// A delta that does not open is never carried, so a checkpoint never holds
/// what no reader could read, and the loader goes on reporting that delta from
/// its key as it always did. A delta larger than the budget is never carried
/// either, and does not count toward the threshold: no checkpoint could hold
/// it.
/// </para>
/// <para>
/// Nothing here deletes a delta. A checkpoint changes how many objects a load
/// reads; retiring what it carries is owed (FR-MAN-017).
/// </para>
/// </remarks>
public sealed class IndexCheckpointer : IDisposable
{
    /// <summary>How many uncovered deltas make a checkpoint due, unless told otherwise.</summary>
    public const int DefaultThreshold = 64;

    // Headroom for the checkpoint's header, signature and framing, as the
    // delta budget leaves for a delta's.
    private const int HeaderMargin = 64 * 1024;

    // Conservative CBOR costs: the fixed keys of a checkpoint carrying deltas
    // come to well under a hundred bytes, and each delta's identifier and two
    // byte-string heads to at most twenty-six beside its stored bytes.
    private const int CheckpointOverhead = 256;
    private const int PerDeltaOverhead = 32;

    private readonly IObjectStore _store;
    private readonly RepositoryId _repositoryId;
    private readonly WriterId _writerId;
    private readonly RepositoryWriteCredential _credential;
    private readonly WriterSequence _sequence;
    private readonly IndexLoader _loader;
    private readonly ILogger _log;

    /// <summary>Creates a checkpointer for one writer.</summary>
    /// <param name="store">The repository's store, read and written.</param>
    /// <param name="repositoryId">The repository.</param>
    /// <param name="writerId">The writer publishing the checkpoints.</param>
    /// <param name="credential">What opens the deltas and signs and seals the checkpoints.</param>
    /// <param name="sequence">The writer's sequence space; each checkpoint draws one number.</param>
    /// <param name="logger">Where publication is logged.</param>
    public IndexCheckpointer(
        IObjectStore store,
        RepositoryId repositoryId,
        WriterId writerId,
        RepositoryWriteCredential credential,
        WriterSequence sequence,
        ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(credential);
        ThrowHelper.ThrowIfNull(sequence);

        _store = store;
        _repositoryId = repositoryId;
        _writerId = writerId;
        _credential = credential;
        _sequence = sequence;
        _log = logger ?? NullLogger.Instance;
        _loader = new IndexLoader(store, repositoryId, credential);
    }

    /// <summary>The budget for one checkpoint's encoded bytes, unless told otherwise.</summary>
    public static int DefaultByteBudget => FormatLimits.MaxMetadataObjectSize - HeaderMargin;

    /// <summary>The most one checkpoint's encoded form may occupy.</summary>
    public int ByteBudget { get; init; } = DefaultByteBudget;

    /// <summary>What earlier runs learned of the repository's checkpoints; a fresh one reads them all.</summary>
    public CheckpointCoverage Coverage { get; init; } = new();

    /// <summary>
    /// Publishes checkpoints carrying every uncovered delta that opens, when
    /// at least <paramref name="threshold"/> of them do.
    /// </summary>
    /// <param name="generation">The generation the checkpoints are keyed and signed at.</param>
    /// <param name="threshold">How many uncovered deltas make a checkpoint due.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The checkpoints published, in order; empty when none was due.</returns>
    public async ValueTask<IReadOnlyList<CheckpointId>> CheckpointAsync(
        ulong generation, int threshold, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);

        // The loader reports damage; a checkpointer only declines to carry it.
        var findings = new List<DamageFinding>();

        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("index/checkpoint/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            if (Coverage.HasRead(entry.Key))
            {
                continue;
            }

            var bytes = await _loader.FetchAsync(entry.Key, findings, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                continue;
            }

            var checkpoint = _loader.OpenCheckpoint(entry.Key, bytes, findings)?.Checkpoint.Checkpoint;
            Coverage.Read(entry.Key, checkpoint is { CarriesDeltas: true } ? checkpoint.SubsumedDeltaIds : []);
        }

        var uncovered = new List<(DeltaId DeltaId, ObjectKey Key)>();
        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("index/delta/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            if (MetadataStoreKeys.TryParseIndexDelta(entry.Key, out var deltaId) &&
                !Coverage.Carries(deltaId) &&
                Fits(entry.Length))
            {
                uncovered.Add((deltaId, entry.Key));
            }
        }

        if (uncovered.Count < threshold)
        {
            return [];
        }

        var carriable = new List<(BundledDelta Bundled, IndexDelta Delta)>();
        foreach (var (deltaId, key) in uncovered)
        {
            var bytes = await _loader.FetchAsync(key, findings, cancellationToken).ConfigureAwait(false);
            if (bytes is not null && Fits(bytes.Length) && _loader.OpenDelta(key.Value, bytes, findings) is { } opened)
            {
                carriable.Add((new BundledDelta(deltaId, bytes), opened.Delta));
            }
        }

        if (carriable.Count < threshold)
        {
            return [];
        }

        // In each writer's own order, so a checkpoint holds runs of a
        // writer's history rather than a scatter of it.
        carriable.Sort(static (left, right) =>
        {
            var byWriter = left.Delta.WriterId.ToArray().AsSpan().SequenceCompareTo(right.Delta.WriterId.ToArray());
            return byWriter != 0 ? byWriter : left.Delta.Sequence.CompareTo(right.Delta.Sequence);
        });

        var published = new List<CheckpointId>();
        using var publisher = new IndexPublisher(_store, _repositoryId, _writerId, _credential, _sequence, _log);
        var batch = new List<BundledDelta>();
        var cost = CheckpointOverhead;

        foreach (var (bundled, _) in carriable)
        {
            var itemCost = bundled.Stored.Length + PerDeltaOverhead;
            if (batch.Count > 0 && cost + itemCost > ByteBudget)
            {
                published.Add(await PublishAsync(publisher, generation, batch, published, cancellationToken).ConfigureAwait(false));
                batch = [];
                cost = CheckpointOverhead;
            }

            batch.Add(bundled);
            cost += itemCost;
        }

        published.Add(await PublishAsync(publisher, generation, batch, published, cancellationToken).ConfigureAwait(false));
        return published;
    }

    private bool Fits(long storedLength) => CheckpointOverhead + PerDeltaOverhead + storedLength <= ByteBudget;

    private async ValueTask<CheckpointId> PublishAsync(
        IndexPublisher publisher,
        ulong generation,
        List<BundledDelta> batch,
        List<CheckpointId> earlier,
        CancellationToken cancellationToken)
    {
        var checkpointId = await publisher.PublishBundledCheckpointAsync(
            generation, batch, earlier.Count > 0 ? earlier[^1] : null, cancellationToken).ConfigureAwait(false);
        Coverage.Read(MetadataStoreKeys.IndexCheckpoint(generation, checkpointId), batch.Select(delta => delta.DeltaId));
        return checkpointId;
    }

    /// <inheritdoc />
    public void Dispose() => _loader.Dispose();
}

using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Repository.Index;

/// <summary>What one retirement pass did at one store.</summary>
/// <param name="Retired">The deltas deleted from their keys.</param>
/// <param name="Unsettled">
/// How many deltas a checkpoint carries whose writer has not published at the
/// store since; a pass after that writer's next publication retires them.
/// </param>
public sealed record DeltaRetirement(IReadOnlyList<DeltaId> Retired, int Unsettled)
{
    /// <summary>A pass that retired nothing and found nothing waiting.</summary>
    public static DeltaRetirement None { get; } = new([], 0);
}

/// <summary>
/// Retires the deltas a store's checkpoints carry (specification 07 §7;
/// FR-MAN-017): deletes a delta's key once the same store holds a checkpoint
/// whose copy of it opens and is byte for byte the object at that key, and the
/// checkpoint's writer has published there since.
/// </summary>
/// <remarks>
/// <para>
/// The decision is the store's own: every fact it rests on is read from the
/// store the delta is deleted from, at the time it is deleted. Checkpoints are
/// never deleted, so whichever checkpoint's authority a delta goes on, every
/// reader of that store still applies it, and concurrent checkpoints carrying
/// different sets need no tie-break.
/// </para>
/// <para>
/// A reader racing the deletion loses nothing because it lists the deltas
/// before the checkpoints and looks again among the checkpoints for a listed
/// delta that is gone (07 §5.2). That holds only where a listing shows every
/// put completed before it began, so a store whose listing may lag is not
/// touched.
/// </para>
/// <para>
/// The grace is one publication of the checkpoint's writer, read from journal
/// keys, which carry their sequence in the clear (08 §1): a journal key above
/// the checkpoint's own number. A reader built before 07 §5.2 lists the
/// checkpoints first; a checkpoint that old was durable before any such
/// reader's load that ends now could have begun, unless that load outlasted a
/// whole publication.
/// </para>
/// </remarks>
public sealed class DeltaRetirer : IDisposable
{
    private readonly IObjectStore _store;
    private readonly IndexLoader _loader;
    private readonly ILogger _log;

    /// <summary>Creates a retirer for one store.</summary>
    /// <param name="store">The store whose deltas are retired; every fact is read from it.</param>
    /// <param name="repositoryId">The repository.</param>
    /// <param name="credential">What opens the checkpoints and the copies they carry.</param>
    /// <param name="logger">Where a pass that retired something is recorded.</param>
    public DeltaRetirer(
        IObjectStore store, RepositoryId repositoryId, RepositoryWriteCredential credential, ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(credential);

        _store = store;
        _log = logger ?? NullLogger.Instance;
        _loader = new IndexLoader(store, repositoryId, credential);
    }

    /// <summary>
    /// What earlier passes learned of the repository's checkpoints, so a pass
    /// opens only the checkpoints carrying a delta still at its key; a fresh
    /// one opens every checkpoint once.
    /// </summary>
    public CheckpointCoverage Coverage { get; init; } = new();

    /// <summary>Retires every admitted delta the store's settled checkpoints carry.</summary>
    /// <param name="admit">Which deltas may go; null admits every one.</param>
    /// <param name="cancellationToken">Cancels the pass; a delta already deleted stays retired.</param>
    public async ValueTask<DeltaRetirement> RetireAsync(Func<DeltaId, bool>? admit, CancellationToken cancellationToken)
    {
        if (_store.Capabilities.ListingConsistency != ListingConsistency.Strong)
        {
            return DeltaRetirement.None;
        }

        var present = new Dictionary<DeltaId, ObjectKey>();
        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("index/delta/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            if (MetadataStoreKeys.TryParseIndexDelta(entry.Key, out var deltaId) && admit?.Invoke(deltaId) != false)
            {
                present.TryAdd(deltaId, entry.Key);
            }
        }

        if (present.Count == 0)
        {
            return DeltaRetirement.None;
        }

        var carriers = await CarriersAsync(present, cancellationToken).ConfigureAwait(false);

        var journalHeads = new Dictionary<WriterId, ulong>();
        var retired = new List<DeltaId>();
        var unsettled = 0;

        foreach (var (deltaId, copies) in carriers)
        {
            var settled = new List<ReadOnlyMemory<byte>>();
            foreach (var (writer, counter, copy) in copies)
            {
                if (!journalHeads.TryGetValue(writer, out var head))
                {
                    head = await ObservedHead.JournalHeadAsync(_store, writer, cancellationToken).ConfigureAwait(false);
                    journalHeads[writer] = head;
                }

                if (head > counter)
                {
                    settled.Add(copy);
                }
            }

            if (settled.Count == 0)
            {
                unsettled++;
                continue;
            }

            var key = present[deltaId];
            var atKey = await _loader.TryFetchAsync(key, cancellationToken).ConfigureAwait(false);
            if (atKey is null || !settled.Any(copy => copy.Span.SequenceEqual(atKey)))
            {
                continue;
            }

            var deleted = await _store.DeleteAsync(key, DeleteConditions.None, cancellationToken).ConfigureAwait(false);
            if (deleted.Outcome == DeleteOutcome.Deleted)
            {
                retired.Add(deltaId);
            }
        }

        if (retired.Count > 0 || unsettled > 0)
        {
            Log.DeltasRetired(_log, retired.Count, unsettled);
        }

        return new DeltaRetirement(retired, unsettled);
    }

    // Every checkpoint copy of a present delta that opens, with the writer and
    // number of the checkpoint carrying it.
    private async ValueTask<Dictionary<DeltaId, List<(WriterId Writer, ulong Counter, ReadOnlyMemory<byte> Copy)>>> CarriersAsync(
        Dictionary<DeltaId, ObjectKey> present, CancellationToken cancellationToken)
    {
        // The loader reports damage; a retirer only declines to act on it.
        var findings = new List<DamageFinding>();
        var carriers = new Dictionary<DeltaId, List<(WriterId, ulong, ReadOnlyMemory<byte>)>>();

        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("index/checkpoint/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            if (Coverage.TryGetCarried(entry.Key, out var known) && !known.Any(present.ContainsKey))
            {
                continue;
            }

            var bytes = await _loader.TryFetchAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                continue;
            }

            var opened = _loader.OpenCheckpoint(entry.Key, bytes, findings);
            var checkpoint = opened?.Checkpoint.Checkpoint;
            Coverage.Read(entry.Key, checkpoint is { CarriesDeltas: true } ? checkpoint.SubsumedDeltaIds : []);
            if (opened is not { } carrier || checkpoint is not { CarriesDeltas: true })
            {
                continue;
            }

            for (var index = 0; index < checkpoint.SubsumedDeltaIds.Count; index++)
            {
                var deltaId = checkpoint.SubsumedDeltaIds[index];
                var copy = checkpoint.SubsumedDeltas[index];
                if (!present.ContainsKey(deltaId) ||
                    _loader.OpenDelta($"{entry.Key}, delta {deltaId}", copy.ToArray(), findings) is null)
                {
                    continue;
                }

                if (!carriers.TryGetValue(deltaId, out var copies))
                {
                    copies = [];
                    carriers[deltaId] = copies;
                }

                copies.Add((carrier.Counter.Writer, carrier.Counter.Counter, copy));
            }
        }

        return carriers;
    }

    /// <inheritdoc />
    public void Dispose() => _loader.Dispose();
}

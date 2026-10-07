using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;

namespace FallbackPlan.Repository;

/// <summary>
/// A publication's write intent and the blob numbers it names
/// ([ADR-0092](../../docs/adr/0092-a-backup-names-its-blobs-a-batch-at-a-time.md)):
/// the intent names the first batch before any is used, and when a batch
/// runs out the next is reserved and named by one extension, durable before
/// the first blob numbered from it is uploaded (08 §3.1, §4).
/// </summary>
/// <remarks>
/// <para>
/// It is both halves of what a blob's number needs. As the counter allocator
/// it hands numbers out of the reserved batches, so every blob the
/// publication numbers is named by a journal record; as the intent scope it
/// makes sure that record is durable before the blob's put. A blob it did
/// not number, a spool an earlier run left and this one resumed, is named by
/// an extension of its own, as every blob once was.
/// </para>
/// <para>
/// A number the intent named and the backup never used is in no blob, so
/// case 4 of ADR-0022 §Decision 7 can never account for it.
/// <see cref="Settle"/> does, once the Completed retirement is durable:
/// every number a durable record of this intent named is accounted, used or
/// not. A publication that dies first leaves the unused ones pending, and
/// the next run voids them as it voids any other leftover (07 §4).
/// </para>
/// </remarks>
internal sealed class ReservingIntentScope : IIntentScope, IBlobCounterAllocator, IDisposable
{
    private readonly JournalPublisher _journal;
    private readonly WriterSequence _sequence;
    private readonly WriterId _writerId;
    private readonly ulong _declaredMaxDurationMs;
    private readonly ulong _nowUnixMilliseconds;
    private readonly uint _generation;

    // Guards the batches and the place numbers are handed from; never held
    // across an await.
    private readonly Lock _gate = new();

    // One extension at a time, because uploads run concurrently (ADR-0029
    // §2) and batches are named in the order they were reserved.
    private readonly SemaphoreSlim _naming = new(1, 1);

    private readonly List<IReadOnlyList<ulong>> _batches = [];
    private readonly Dictionary<BlobId, int> _batchOf = [];
    private readonly HashSet<BlobId> _namedAlone = [];

    // Batches [0, _named) are named by a durable record; numbers are handed
    // from batch _handing, of which _handed have gone.
    private int _named;
    private int _handing;
    private int _handed;

    private ReservingIntentScope(
        JournalPublisher journal,
        WriterSequence sequence,
        WriterId writerId,
        ulong declaredMaxDurationMs,
        ulong nowUnixMilliseconds,
        uint generation)
    {
        _journal = journal;
        _sequence = sequence;
        _writerId = writerId;
        _declaredMaxDurationMs = declaredMaxDurationMs;
        _nowUnixMilliseconds = nowUnixMilliseconds;
        _generation = generation;
    }

    /// <summary>The journal sequence of the write intent.</summary>
    public ulong IntentSequence { get; private set; }

    /// <summary>
    /// Reserves the first batch and publishes the write intent naming it,
    /// returning once the intent is durable.
    /// </summary>
    /// <param name="journal">The writer's journal.</param>
    /// <param name="sequence">The writer's sequence, which the numbers come from.</param>
    /// <param name="writerId">The writer, whose identity prefixes every blob id.</param>
    /// <param name="backupSetId">The set the intent is for.</param>
    /// <param name="declaredMaxDurationMs">How long the intent claims it may take (08 §4).</param>
    /// <param name="expiryGeneration">The generation past which the intent is stale.</param>
    /// <param name="purpose">What the intent is for.</param>
    /// <param name="nowUnixMilliseconds">Informational stamp for the journal records.</param>
    /// <param name="generation">The signing generation.</param>
    /// <param name="cancellationToken">Cancels the publication.</param>
    /// <returns>The scope, whose intent is durable.</returns>
    public static async ValueTask<ReservingIntentScope> OpenAsync(
        JournalPublisher journal,
        WriterSequence sequence,
        WriterId writerId,
        ReadOnlyMemory<byte> backupSetId,
        ulong declaredMaxDurationMs,
        ulong expiryGeneration,
        IntentPurpose purpose,
        ulong nowUnixMilliseconds,
        uint generation,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(journal);
        ThrowHelper.ThrowIfNull(sequence);

        var scope = new ReservingIntentScope(
            journal, sequence, writerId, declaredMaxDurationMs, nowUnixMilliseconds, generation);
        try
        {
            IReadOnlyList<BlobId> first;
            lock (scope._gate)
            {
                first = scope.Reserve(BlobCounterReservation.FirstBatch);
            }

            scope.IntentSequence = await journal.PublishAsync(
                JournalRecordKind.WriteIntent,
                new JournalPayload.WriteIntent(backupSetId, first, declaredMaxDurationMs, expiryGeneration, purpose),
                nowUnixMilliseconds,
                generation,
                cancellationToken).ConfigureAwait(false);

            lock (scope._gate)
            {
                scope._named = 1;
            }

            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>Reserves the next batch when this one has run out; naming it waits for its first upload.</remarks>
    public ulong AllocateNext()
    {
        lock (_gate)
        {
            if (_handed == _batches[_handing].Count)
            {
                if (_handing == _batches.Count - 1)
                {
                    Reserve(BlobCounterReservation.After(_batches[_handing].Count));
                }

                _handing++;
                _handed = 0;
            }

            return _batches[_handing][_handed++];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The blob is durable and a durable record names it, ADR-0022
    /// §Decision 7 case 4, so a run that dies after this owes nothing for
    /// its number.
    /// </remarks>
    public void MarkAccounted(ulong blobCounter) => _sequence.MarkAccounted(blobCounter);

    /// <inheritdoc />
    /// <remarks>
    /// A blob numbered from a batch no durable record names yet has that
    /// batch named first, and every batch reserved before it, so the journal
    /// names them in the order they were reserved.
    /// </remarks>
    public async ValueTask EnsureCoveredAsync(BlobId blobId, CancellationToken cancellationToken)
    {
        int batch;
        lock (_gate)
        {
            batch = _batchOf.TryGetValue(blobId, out var reserved) ? reserved : -1;
            if (batch >= 0 && batch < _named)
            {
                return;
            }
        }

        await _naming.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (batch < 0)
            {
                if (!_namedAlone.Contains(blobId))
                {
                    await PublishExtensionAsync([blobId], cancellationToken).ConfigureAwait(false);
                    _namedAlone.Add(blobId);
                }

                return;
            }

            while (true)
            {
                IReadOnlyList<BlobId> next;
                lock (_gate)
                {
                    if (_named > batch)
                    {
                        return;
                    }

                    next = [.. _batches[_named].Select(number => BlobId.FromWriterCounter(_writerId, number))];
                }

                await PublishExtensionAsync(next, cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    _named++;
                }
            }
        }
        finally
        {
            _naming.Release();
        }
    }

    /// <summary>
    /// Accounts for every number a durable record of this intent named, used
    /// or not. Only once the intent's Completed retirement is durable: it is
    /// what accounts for a number no blob carries (ADR-0092).
    /// </summary>
    public void Settle()
    {
        ulong[] named;
        lock (_gate)
        {
            named = [.. _batches.Take(_named).SelectMany(batch => batch)];
        }

        _sequence.MarkAccounted(named);
    }

    /// <inheritdoc />
    public void Dispose() => _naming.Dispose();

    // Under _gate.
    private BlobId[] Reserve(int count)
    {
        var numbers = _sequence.AllocateBatch(count);
        var index = _batches.Count;
        _batches.Add(numbers);

        var ids = new BlobId[numbers.Count];
        for (var position = 0; position < ids.Length; position++)
        {
            ids[position] = BlobId.FromWriterCounter(_writerId, numbers[position]);
            _batchOf.Add(ids[position], index);
        }

        return ids;
    }

    private ValueTask<ulong> PublishExtensionAsync(IReadOnlyList<BlobId> blobIds, CancellationToken cancellationToken) =>
        _journal.PublishAsync(
            JournalRecordKind.IntentExtension,
            new JournalPayload.IntentExtension(IntentSequence, blobIds, _declaredMaxDurationMs),
            _nowUnixMilliseconds,
            _generation,
            cancellationToken);
}

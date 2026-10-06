using FallbackPlan.Application;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Repository.Catalogue;

namespace FallbackPlan.Agent;

/// <summary>
/// Counts a destination's files of one snapshot as the blobs their content is
/// read from land there (ADR-0088 Amendment 1). A file is held once every
/// blob it needs is; a file with no content needs none.
/// </summary>
/// <remarks>
/// Fed every key the destination holds, those it held before the sync and
/// those the sync delivers alike, so a destination that already holds most of
/// a backup is counted from where it stands rather than from nothing.
/// </remarks>
internal sealed class FileHoldingCounter
{
    private readonly Dictionary<string, int> _blobOfKey = new(StringComparer.Ordinal);
    private readonly List<int>[] _filesOfBlob;
    private readonly int[] _missing;
    private readonly bool[] _landed;
    private long _held;

    /// <summary>Builds the counter from the snapshot's content and the key each blob is stored under.</summary>
    /// <param name="content">The snapshot's files as the blobs they need.</param>
    /// <param name="keyOf">The store key of one of those blobs.</param>
    public FileHoldingCounter(SnapshotContent content, Func<ContentBlob, string> keyOf)
    {
        Total = content.Files;
        _held = content.Files - content.FileBlobs.Count;
        _landed = new bool[content.Blobs.Count];
        _filesOfBlob = new List<int>[content.Blobs.Count];
        for (var blob = 0; blob < content.Blobs.Count; blob++)
        {
            _blobOfKey[keyOf(content.Blobs[blob])] = blob;
            _filesOfBlob[blob] = [];
        }

        _missing = new int[content.FileBlobs.Count];
        for (var file = 0; file < content.FileBlobs.Count; file++)
        {
            _missing[file] = content.FileBlobs[file].Length;
            foreach (var blob in content.FileBlobs[file])
            {
                _filesOfBlob[blob].Add(file);
            }
        }
    }

    /// <summary>The snapshot's files.</summary>
    public long Total { get; }

    /// <summary>Of them, how many the destination holds.</summary>
    public long Held => _held;

    /// <summary>The destination holds <paramref name="key"/>.</summary>
    /// <param name="key">A store key it holds, whether or not any file needs it.</param>
    /// <returns>Whether that brought a file in.</returns>
    public bool Landed(string key)
    {
        if (!_blobOfKey.TryGetValue(key, out var blob) || _landed[blob])
        {
            return false;
        }

        _landed[blob] = true;
        var moved = false;
        foreach (var file in _filesOfBlob[blob])
        {
            if (--_missing[file] == 0)
            {
                _held++;
                moved = true;
            }
        }

        return moved;
    }
}

/// <summary>
/// One sync's live count of the newest backup's files at its destination
/// (ADR-0088 Amendment 1), published where the status reads it until the sync
/// ends. The ledger's own figure moves only once a pass has succeeded.
/// </summary>
internal sealed class SyncCount : IDisposable
{
    private readonly FileHoldingCounter _counter;
    private readonly LiveHolding _live;

    private SyncCount(FileHoldingCounter counter, LiveHolding live)
    {
        _counter = counter;
        _live = live;
    }

    /// <summary>
    /// Starts counting, or returns null when the set has no backup yet and so
    /// nothing to count against, or when its catalogue cannot say what the
    /// backup's files need: the count is a display, and the sync goes on
    /// without it.
    /// </summary>
    /// <param name="runtime">Where the count is published.</param>
    /// <param name="set">The set being synced.</param>
    /// <param name="destination">The destination being filled.</param>
    /// <param name="archive">The set's archive, whose catalogue says what each file needs.</param>
    public static SyncCount? Start(
        ServiceRuntime runtime, BackupSetConfiguration set, string destination, ArchiveHandle archive)
    {
        try
        {
            return Build(runtime, set, destination, archive);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.SyncCountUnavailable(
                runtime.LoggerFor<SyncCount>(), new LogLabel(set.Name), new LogLabel(destination), exception.Message);
            return null;
        }
    }

    private static SyncCount? Build(
        ServiceRuntime runtime, BackupSetConfiguration set, string destination, ArchiveHandle archive)
    {
        using var catalogue = archive.OpenReadCatalogue();
        var setId = Convert.FromHexString(set.Id);
        var newest = catalogue.EnumerateSnapshots()
            .FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(setId));
        if (newest is null)
        {
            return null;
        }

        var content = catalogue.ContentOf(newest.SnapshotId.Span);
        using var derive = new Repository.Crypto.StoreBlobKeyDeriver(archive.Repository.Keys.KeyIdKey);
        var counter = new FileHoldingCounter(
            content,
            blob => Repository.Packing.BlobStoreKeys.ForBlob(blob.Class, blob.StoreBlobKey ?? derive.Derive(blob.BlobId)).Value);
        return new SyncCount(counter, runtime.Holdings.Track(set.Id, destination, counter.Total));
    }

    /// <summary>
    /// Starts counting from what the destination already holds under
    /// <c>blobs/</c>, or returns null as <see cref="Start"/> does.
    /// </summary>
    /// <param name="runtime">Where the count is published.</param>
    /// <param name="set">The set being synced.</param>
    /// <param name="destination">The destination being filled.</param>
    /// <param name="archive">The set's archive.</param>
    /// <param name="replica">The destination's store, listed once for what it holds.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    public static async ValueTask<SyncCount?> StartAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, string destination, ArchiveHandle archive,
        Storage.Abstractions.IObjectStore replica, CancellationToken cancellationToken)
    {
        var count = Start(runtime, set, destination, archive);
        if (count is null)
        {
            return null;
        }

        try
        {
            await foreach (var held in replica.ListAsync(
                Storage.Abstractions.ObjectPrefix.Parse("blobs/"), Storage.Abstractions.ListOptions.Default,
                cancellationToken).ConfigureAwait(false))
            {
                count._counter.Landed(held.Key.Value);
            }
        }
        catch
        {
            count.Dispose();
            throw;
        }

        count._live.Report(count._counter.Held);
        return count;
    }

    /// <summary>What the destination held before anything was sent, counted at once.</summary>
    /// <param name="keys">Every key it declared holding.</param>
    public void Seed(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            _counter.Landed(key);
        }

        _live.Report(_counter.Held);
    }

    /// <summary>The destination holds <paramref name="key"/> now.</summary>
    /// <param name="key">A key the sync found held or delivered.</param>
    public void Holds(string key)
    {
        if (_counter.Landed(key))
        {
            _live.Report(_counter.Held);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _live.Dispose();
}

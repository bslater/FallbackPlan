using FallbackPlan.Application;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Agent;

/// <summary>
/// The room a direct-ship run beside a peer alone keeps its blobs in until the
/// set's stores have them
/// ([ADR-0196](../../docs/adr/0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md)
/// Amendment 8). Beside a local path a store is sent each blob read back from
/// the local path; beside a peer alone nothing on this machine holds it, and
/// reading it back from the peer would cross the peer's link twice.
/// </summary>
/// <remarks>
/// <para>
/// Bounded. A blob is kept while the room holds less than its capacity, so the
/// room holds at most its capacity and the blobs being kept at that moment. A
/// blob that finds it full, or the volume under the state directory at the
/// floor a destination keeps (FR-DEST-010), or a write here that fails, is
/// not kept: it is the sync's, which reads it from the peer, and the run goes
/// on either way.
/// </para>
/// <para>
/// A copy is deleted once every store it was kept for has it. What is still
/// here at the run's end goes with the room, and a room a run left behind,
/// cut short by a crash, is emptied before the next run keeps anything in it.
/// </para>
/// </remarks>
internal sealed class WriteBehindRoom : IDisposable
{
    /// <summary>The room a run keeps when nothing says otherwise: 1 GiB.</summary>
    public const long DefaultCapacityBytes = 1L << 30;

    private readonly LocalFileSystemObjectStore _copies;
    private readonly string _directory;
    private readonly long _capacity;
    private readonly Func<string, long?> _availableBytesOn;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (int Waiting, long Length)> _kept = new(StringComparer.Ordinal);
    private long _bytes;

    private WriteBehindRoom(string directory, long capacity, Func<string, long?> availableBytesOn, ILogger log)
    {
        _directory = directory;
        _capacity = capacity;
        _availableBytesOn = availableBytesOn;
        _copies = new LocalFileSystemObjectStore(directory, log);
    }

    /// <summary>The copies, for the write-behind to read each blob back from.</summary>
    public IObjectStore Copies => _copies;

    /// <summary>
    /// Opens the room at <paramref name="directory"/>, emptied of anything a
    /// run before left in it.
    /// </summary>
    /// <param name="directory">Where the copies are kept.</param>
    /// <param name="capacity">The bytes the room may hold.</param>
    /// <param name="availableBytesOn">The free bytes on the volume holding a path, or null where they cannot be read.</param>
    /// <param name="log">The copies' store's log.</param>
    /// <returns>The room, or null where it cannot be made.</returns>
    public static WriteBehindRoom? Open(
        string directory, long capacity, Func<string, long?> availableBytesOn, ILogger log)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.CreateDirectory(directory);
            return new WriteBehindRoom(directory, capacity, availableBytesOn, log);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps a copy of a blob for <paramref name="stores"/> stores to be sent,
    /// where there is room for it.
    /// </summary>
    /// <param name="key">The blob's key.</param>
    /// <param name="openContent">Opens the blob's content, as the run's own put does.</param>
    /// <param name="stores">How many stores will each send it on and say so.</param>
    /// <param name="cancellationToken">The run's cancellation.</param>
    /// <returns>Whether a copy is kept, to be read back by each store.</returns>
    public async ValueTask<bool> TryKeepAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        int stores,
        CancellationToken cancellationToken)
    {
        // Room is taken before the copy is written, so blobs kept at once
        // cannot each find the room empty.
        lock (_gate)
        {
            if (_kept.TryGetValue(key.Value, out var already))
            {
                _kept[key.Value] = already with { Waiting = already.Waiting + stores };
                return true;
            }

            if (_bytes >= _capacity)
            {
                return false;
            }

            _kept[key.Value] = (stores, 1);
            _bytes += 1;
        }

        long length = 0;
        try
        {
            if (DestinationCapacity.FloorShortfall(_directory, _availableBytesOn(_directory)) is null)
            {
                _ = await _copies.PutAsync(key, openContent, PutConditions.IfNotExists, cancellationToken)
                    .ConfigureAwait(false);
                length = (await _copies.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false))
                    .Metadata?.Length ?? 0;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy this machine cannot keep is the sync's, as one the room
            // had no space for is.
        }

        lock (_gate)
        {
            if (length > 0)
            {
                _kept[key.Value] = (_kept[key.Value].Waiting, length);
                _bytes += length - 1;
                return true;
            }

            _ = _kept.Remove(key.Value);
            _bytes -= 1;
            return false;
        }
    }

    /// <summary>
    /// Says one store has a blob: once every store it was kept for has, its
    /// copy is deleted and its room freed.
    /// </summary>
    /// <param name="key">The blob's key.</param>
    public async ValueTask SentAsync(ObjectKey key)
    {
        lock (_gate)
        {
            if (!_kept.TryGetValue(key.Value, out var kept))
            {
                return;
            }

            if (kept.Waiting > 1)
            {
                _kept[key.Value] = kept with { Waiting = kept.Waiting - 1 };
                return;
            }

            _ = _kept.Remove(key.Value);
            _bytes -= kept.Length;
        }

        try
        {
            _ = await _copies.DeleteAsync(key, DeleteConditions.None, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the room's own deletion at the run's end.
        }
    }

    /// <summary>Deletes the room and every copy still in it.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Emptied by the next run's opening instead.
        }
    }
}

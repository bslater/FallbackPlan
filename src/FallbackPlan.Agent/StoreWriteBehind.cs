using System.Globalization;
using System.Threading.Channels;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Agent;

/// <summary>
/// A direct-ship run's write-behind to one object store beside a local path
/// ([ADR-0196](../../docs/adr/0196-a-set-whose-only-destinations-are-stores-ships-straight-to-them.md)
/// Amendment 6): each blob the run lands on a local path is sent on to the
/// store, one at a time and in the order it landed, read back from that local
/// path. The run never waits on it.
/// </summary>
/// <remarks>
/// <para>
/// Blobs only. The snapshot, the index and the descriptor are the sync's to
/// send, after the run, and it sends a snapshot only once that snapshot's
/// blobs are at the store: a store fed by the run as well as by the sync then
/// still never holds a backup whose blobs it lacks. A blob the run did not
/// write, because the local path already held it, is the sync's too.
/// </para>
/// <para>
/// The first failure ends the write-behind and is kept as its reason; it never
/// reaches the run. The run's end ends it too, an upload in flight with it:
/// what has not reached the store by then is the sync's to send, as it was
/// before this existed. Either way the store is behind until that sync.
/// </para>
/// </remarks>
internal sealed class StoreWriteBehind : IDisposable
{
    private readonly Channel<ObjectKey> _waiting =
        Channel.CreateUnbounded<ObjectKey>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _ending = new();
    private readonly IObjectStore _store;
    private readonly IReadOnlyList<IObjectStore> _sources;
    private readonly ILogger _log;
    private Task _sending = Task.CompletedTask;
    private int _offered;
    private int _sent;
    private string? _stoppedBecause;

    private StoreWriteBehind(
        string destinationName, IObjectStore store, IReadOnlyList<IObjectStore> sources, ILogger log)
    {
        DestinationName = destinationName;
        _store = store;
        _sources = sources;
        _log = log;
    }

    /// <summary>The store's destination name.</summary>
    public string DestinationName { get; }

    /// <summary>
    /// Starts sending, in the background, whatever <see cref="Offer"/> is
    /// handed from now on.
    /// </summary>
    /// <param name="destinationName">The store's destination name, for the ledger and the log.</param>
    /// <param name="store">The store, as the run writes to it.</param>
    /// <param name="sources">The local paths the run writes through, in the order to read a blob back from.</param>
    /// <param name="log">Where a stop is said.</param>
    public static StoreWriteBehind Start(
        string destinationName, IObjectStore store, IReadOnlyList<IObjectStore> sources, ILogger log)
    {
        var writeBehind = new StoreWriteBehind(destinationName, store, sources, log);
        writeBehind._sending = Task.Run(writeBehind.SendAsync);
        return writeBehind;
    }

    /// <summary>
    /// Queues a blob a local path has just taken, to be sent on after those
    /// before it. Never waits: once the write-behind has stopped it is only
    /// counted.
    /// </summary>
    /// <param name="key">The blob's key.</param>
    public void Offer(ObjectKey key)
    {
        Interlocked.Increment(ref _offered);
        _ = _waiting.Writer.TryWrite(key);
    }

    /// <summary>
    /// Ends the write-behind, an upload in flight with it, and says what it
    /// did: the store's behind detail until the sync that follows the run.
    /// </summary>
    public async Task<string> FinishAsync()
    {
        _ = _waiting.Writer.TryComplete();
        await _ending.CancelAsync().ConfigureAwait(false);
        await _sending.ConfigureAwait(false);

        var sent = Volatile.Read(ref _sent);
        var offered = Volatile.Read(ref _offered);
        return _stoppedBecause is { } reason
            ? string.Format(
                CultureInfo.InvariantCulture,
                "the run stopped sending its blobs on to it after {0} of {1}: {2}; the sync that follows sends the rest, and the snapshot",
                sent, offered, reason)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0} of the {1} blob(s) the run wrote went up as the run went; the sync that follows sends the rest, and the snapshot",
                sent, offered);
    }

    /// <inheritdoc />
    /// <remarks>After <see cref="FinishAsync"/>, which is what ends the sending.</remarks>
    public void Dispose() => _ending.Dispose();

    private async Task SendAsync()
    {
        var ending = _ending.Token;
        try
        {
            await foreach (var key in _waiting.Reader.ReadAllAsync(ending).ConfigureAwait(false))
            {
                // Put only where absent: content-addressed, so one already
                // there is this one, and a put that finds it is no failure.
                _ = await _store.PutAsync(key, token => ReadBackAsync(key, token), PutConditions.IfNotExists, ending)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
            }
        }
        catch (Exception) when (ending.IsCancellationRequested)
        {
            // The run ended, and whatever its end cut short, however the
            // store's client reports that, says only so: what is not at the
            // store yet is the sync's.
        }
        catch (Exception exception) when (!ending.IsCancellationRequested)
        {
            // Whatever the store or the read back said, it is said once, as
            // the reason this stopped, and never to the run: the store is
            // behind as it would have been without this.
            _stoppedBecause = exception.Message;
            _ = _waiting.Writer.TryComplete();
            Log.WriteBehindStopped(_log, new LogLabel(DestinationName), exception.Message);
        }
    }

    private async ValueTask<Stream> ReadBackAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        foreach (var source in _sources)
        {
            var read = await source.OpenReadAsync(key, range: null, cancellationToken).ConfigureAwait(false);
            if (read is { Outcome: OpenReadOutcome.Found, Content: { } content })
            {
                return content;
            }
        }

        throw new IOException($"Blob {key.Value} is at none of the run's local paths to be sent on from.");
    }
}

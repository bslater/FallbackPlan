using System.Collections.Concurrent;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Store decorators that produce the failures a clean exception cannot: a
/// put that tears and leaves partial bytes visible under the final key, an
/// acknowledged object that later vanishes, and a read that fails after the
/// world was already loaded. The local provider's temp-plus-rename makes the
/// first two unreachable on its own disk — these model the providers and
/// platforms that do not offer that atomicity, which is exactly the store
/// model the format says it assumes
/// (specifications/repository-format/01-object-layout.md §1).
/// </summary>
/// <remarks>
/// All three are thread-safe: uploads run concurrently (ADR-0029 §2) and a
/// fault injector that races its own bookkeeping is a broken instrument.
/// </remarks>
public sealed class TearingObjectStore(IObjectStore inner, int tearAtBlobPut) : IObjectStore
{
    private int _blobPuts;
    private volatile string? _tornKey;

    /// <summary>The key whose put tore, once it has.</summary>
    public string? TornKey => _tornKey;

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(key, range, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        if (!key.ToString().StartsWith("blobs/", StringComparison.Ordinal)
            || Interlocked.Increment(ref _blobPuts) != tearAtBlobPut)
        {
            return await inner.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);
        }

        // The tear: a prefix of the content becomes visible under the final
        // key — the state a non-atomic provider leaves when the connection
        // dies mid-body — and the caller is told the put failed.
        byte[] whole;
        var content = await openContent(cancellationToken).ConfigureAwait(false);
        await using (content.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            whole = buffer.ToArray();
        }

        var torn = whole.AsMemory(0, Math.Max(1, (whole.Length * 3) / 5));
        await inner.PutAsync(
            key,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(torn.ToArray(), writable: false)),
            PutConditions.None,
            cancellationToken).ConfigureAwait(false);

        _tornKey = key.ToString();
        throw new IOException($"Injected fault: the put of '{key}' tore after {torn.Length} of {whole.Length} bytes.");
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);
}

/// <summary>
/// Acknowledges every put and remembers it as unflushed; losing power deletes
/// what was never flushed. This is the documented local-provider window —
/// contents fsynced, the directory entry best-effort — widened into an
/// instrument: an acknowledged object that later vanishes must read as one
/// that was never written (architecture 04 §5.1), never as a wedged state.
/// </summary>
public sealed class VanishingObjectStore(IObjectStore inner) : IObjectStore
{
    private readonly ConcurrentQueue<ObjectKey> _unflushed = new();

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <summary>Marks everything acknowledged so far as durable.</summary>
    public void Checkpoint() => _unflushed.Clear();

    /// <summary>
    /// Deletes every unflushed object matching <paramref name="keyFilter"/>
    /// (all of them when null) — the state a power loss leaves when the
    /// directory entries never reached the platter.
    /// </summary>
    public async Task LosePowerAsync(Func<string, bool>? keyFilter = null)
    {
        while (_unflushed.TryDequeue(out var key))
        {
            if (keyFilter is null || keyFilter(key.ToString()))
            {
                await inner.DeleteAsync(key, DeleteConditions.None, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(key, range, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        var result = await inner.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == PutOutcome.Created)
        {
            _unflushed.Enqueue(key);
        }

        return result;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);
}

/// <summary>
/// Accepts puts normally until armed, then fails every put of a matching
/// key with an <see cref="IOException"/> — the store outage that arrives
/// mid-publication and clears before the next run, which is the shape a
/// same-process retry needs: the first run loses work to the fault, the
/// second runs over a healthy store.
/// </summary>
public sealed class PutFaultingObjectStore(IObjectStore inner) : IObjectStore
{
    private volatile Func<string, bool>? _failing;

    /// <summary>Starts failing puts of keys matching <paramref name="keyFilter"/> (all when null).</summary>
    public void Arm(Func<string, bool>? keyFilter = null) => _failing = keyFilter ?? (_ => true);

    /// <summary>Stops failing — the outage clears.</summary>
    public void Heal() => _failing = null;

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(key, range, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        if (_failing is { } failing && failing(key.ToString()))
        {
            throw new IOException($"Injected fault: the put of '{key}' failed.");
        }

        return inner.PutAsync(key, openContent, conditions, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);
}

/// <summary>
/// Serves reads normally until armed, then fails every read of a matching
/// key with an <see cref="IOException"/> — the transient network or disk
/// fault that arrives mid-restore, after the world was already loaded.
/// </summary>
public sealed class ReadFaultingObjectStore(IObjectStore inner) : IObjectStore
{
    private volatile Func<string, bool>? _failing;

    /// <summary>Starts failing reads of keys matching <paramref name="keyFilter"/> (all when null).</summary>
    public void Arm(Func<string, bool>? keyFilter = null) => _failing = keyFilter ?? (_ => true);

    /// <summary>Stops failing — the transient fault clears.</summary>
    public void Heal() => _failing = null;

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        if (_failing is { } failing && failing(key.ToString()))
        {
            throw new IOException($"Injected fault: the read of '{key}' failed.");
        }

        return inner.OpenReadAsync(key, range, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken) =>
        inner.PutAsync(key, openContent, conditions, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);
}

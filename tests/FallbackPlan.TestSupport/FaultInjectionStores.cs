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
/// Holds matching puts open <em>after</em> the inner put has made the object
/// durable, so a test can stand several objects in the "durable, upload
/// still in flight" state at once and act — kill, cancel, inspect — while
/// they are held. The park is deliberately after the inner put: the state of
/// interest is a durable object whose writer has not yet been told so.
/// Ordering claims (the covering intent before the blob's put) are
/// ConcurrentUploadTests' territory, and nothing here is evidence for them.
/// </summary>
public sealed class GatingObjectStore(IObjectStore inner, int parkTarget, Func<string, bool> keyFilter) : IObjectStore
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private int _parked;

    /// <summary>How many matching puts are currently parked.</summary>
    public int ParkedCount
    {
        get
        {
            lock (_gate)
            {
                return _parked;
            }
        }
    }

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <summary>Completes once <c>parkTarget</c> matching puts are parked at the same time.</summary>
    public async Task<bool> WaitUntilParkedAsync(TimeSpan timeout) =>
        await Task.WhenAny(_reached.Task, Task.Delay(timeout)).ConfigureAwait(false) == _reached.Task;

    /// <summary>Releases every parked put, and every future one.</summary>
    public void Release() => _release.TrySetResult();

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

        if (keyFilter(key.ToString()))
        {
            lock (_gate)
            {
                if (++_parked >= parkTarget)
                {
                    _reached.TrySetResult();
                }
            }

            await _release.Task.ConfigureAwait(false);
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
/// Holds matching puts <em>before</em> the inner put runs, so a test can act
/// — cancel, kill the caller — while the object is requested but not yet
/// durable, then release and observe what the put does under the new state.
/// The dual of <see cref="GatingObjectStore"/>: park-before models "the
/// write had not happened yet", park-after models "the write had happened
/// and nobody knew".
/// </summary>
public sealed class HoldingObjectStore(IObjectStore inner, int parkTarget, Func<string, bool> keyFilter) : IObjectStore
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private int _parked;

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <summary>Completes once <c>parkTarget</c> matching puts are parked at the same time.</summary>
    public async Task<bool> WaitUntilParkedAsync(TimeSpan timeout) =>
        await Task.WhenAny(_reached.Task, Task.Delay(timeout)).ConfigureAwait(false) == _reached.Task;

    /// <summary>Releases every parked put, and every future one.</summary>
    public void Release() => _release.TrySetResult();

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
        if (keyFilter(key.ToString()))
        {
            lock (_gate)
            {
                if (++_parked >= parkTarget)
                {
                    _reached.TrySetResult();
                }
            }

            await _release.Task.ConfigureAwait(false);
        }

        return await inner.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);
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

/// <summary>
/// Deletes normally until armed, then refuses matching keys — either by
/// throwing an <see cref="IOException"/> or by reporting
/// <see cref="DeleteOutcome.NotFound"/> without removing anything.
/// </summary>
/// <remarks>
/// <para>
/// Deletion is the one operation whose failure is invisible from its own
/// result unless the caller looks: a store that reports "not found" for an
/// object that is still there, and a caller that counts every call as a
/// removal, together produce a ledger claiming work nobody did. The two arming
/// modes are the two halves of that — the loud failure and the quiet one.
/// </para>
/// <para>
/// A sharing violation on Windows and a permission change under a running
/// collector both reach a caller as one of these; neither is a reason to
/// abandon the objects behind the failure in listing order.
/// </para>
/// </remarks>
public sealed class DeleteFaultingObjectStore(IObjectStore inner) : IObjectStore
{
    private volatile Func<string, bool>? _throwing;
    private volatile Func<string, bool>? _lying;
    private volatile Func<string, Exception>? _exceptionFactory;

    /// <summary>Starts throwing on deletes of keys matching <paramref name="keyFilter"/> (all when null).</summary>
    public void ArmThrow(Func<string, bool>? keyFilter = null) => _throwing = keyFilter ?? (_ => true);

    /// <summary>
    /// As <see cref="ArmThrow(Func{string, bool}?)"/>, but the caller chooses
    /// the exception — for proving the sweep's posture holds for fault types
    /// no local store throws today (a remote store's timeout, say), not just
    /// the two the filesystem does.
    /// </summary>
    public void ArmThrow(Func<string, Exception> exceptionFactory, Func<string, bool>? keyFilter = null)
    {
        _exceptionFactory = exceptionFactory;
        _throwing = keyFilter ?? (_ => true);
    }

    /// <summary>
    /// Starts reporting <see cref="DeleteOutcome.NotFound"/> for matching keys
    /// while leaving the object in place — the delete that says it had nothing
    /// to do and is believed.
    /// </summary>
    public void ArmNotFound(Func<string, bool>? keyFilter = null) => _lying = keyFilter ?? (_ => true);

    /// <summary>Stops both faults — the outage clears.</summary>
    public void Heal()
    {
        _throwing = null;
        _lying = null;
        _exceptionFactory = null;
    }

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
        CancellationToken cancellationToken) =>
        inner.PutAsync(key, openContent, conditions, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
    {
        if (_throwing is { } throwing && throwing(key.ToString()))
        {
            throw _exceptionFactory?.Invoke(key.ToString())
                ?? new IOException($"Injected fault: the delete of '{key}' failed.");
        }

        return _lying is { } lying && lying(key.ToString())
            ? ValueTask.FromResult(new DeleteResult(DeleteOutcome.NotFound))
            : inner.DeleteAsync(key, conditions, cancellationToken);
    }
}

/// <summary>
/// A store whose read of one chosen object dies part-way through, the way a
/// link dies part-way through a transfer.
/// </summary>
/// <remarks>
/// <para>
/// The gap the other decorators in this file leave. They all inject at the
/// <see cref="IObjectStore"/> boundary — a put that fails, a delete that lies,
/// a read that refuses — and none of them can stop a transfer *inside* an
/// object, which is the only interruption that makes resumption mean anything.
/// A source whose content stream throws after N bytes drops the connection
/// mid-object from the sending side, which is what a rebooting router looks
/// like to the peer on the other end.
/// </para>
/// <para>
/// The cut is armed once and cleared by <see cref="Heal"/>, so one fixture can
/// sever a transfer and then let the retry run over a healthy store — the
/// two-phase shape a resume test needs.
/// </para>
/// </remarks>
/// <param name="inner">The store doing the actual work.</param>
/// <param name="keyFilter">Which object to cut.</param>
/// <param name="readableBytes">How many bytes of it to hand over before throwing.</param>
public sealed class SeveringReadObjectStore(
    IObjectStore inner, Func<string, bool> keyFilter, long readableBytes) : IObjectStore
{
    private volatile bool _healed;

    /// <summary>Whether the cut has fired.</summary>
    public bool Severed { get; private set; }

    /// <summary>Stops cutting: later reads are whole.</summary>
    public void Heal() => _healed = true;

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        var read = await inner.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
        if (_healed || read.Outcome != OpenReadOutcome.Found || read.Content is null || !keyFilter(key.Value))
        {
            return read;
        }

        Severed = true;
        return new OpenReadResult(new SeveringStream(read.Content, readableBytes, key.Value));
    }

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken) =>
        inner.PutAsync(key, openContent, conditions, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);

    private sealed class SeveringStream(Stream inner, long readableBytes, string key) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= readableBytes)
            {
                throw new IOException($"Injected fault: the read of '{key}' was severed after {_read} bytes.");
            }

            var allowed = (int)Math.Min(buffer.Length, readableBytes - _read);
            var got = await inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
            _read += got;
            return got;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// A store whose <em>listings</em> lag its reads: a put is readable the
/// instant it is acknowledged and does not appear in
/// <see cref="IObjectStore.ListAsync"/> until <see cref="Release"/>; a delete
/// stops reading immediately and goes on being listed until the same call.
/// Reports <see cref="ListingConsistency.Eventual"/>, which nothing in this
/// product has ever said before.
/// </summary>
/// <remarks>
/// <para>
/// This is the store model the format claims to assume and has never met:
/// "the core must <b>not</b> assume filesystem rename, strong listing
/// consistency, provider checksums, or mutable objects"
/// (docs/architecture/05-storage-providers.md §1). Every provider the engine
/// has ever run against is one POSIX filesystem, where a listing cannot lag
/// because there is nothing for it to lag behind.
/// </para>
/// <para>
/// Both directions are modelled because they fail differently. A put that is
/// not yet listable <b>hides live data from a reader that enumerates</b> —
/// the dangerous one, because absence reads as "there is nothing there"
/// rather than as damage. A delete that is still listed <b>offers a reader
/// something that is already gone</b>, which surfaces as a read failure the
/// caller can see. An instrument that modelled only one would leave the other
/// believed rather than held.
/// </para>
/// <para>
/// <paramref name="keyFilter"/> scopes the lag, because the planes lag
/// independently and the interesting states are the mixed ones — a
/// <c>snapshots/</c> listing behind a current <c>blobs/</c> listing is not the
/// same repository as one where both are behind. Null lags everything.
/// </para>
/// </remarks>
public sealed class LaggingObjectStore(IObjectStore inner, Func<string, bool>? keyFilter = null) : IObjectStore
{
    private readonly Lock _gate = new();
    private readonly HashSet<ObjectKey> _unlisted = [];
    private readonly Dictionary<ObjectKey, ObjectEntry> _lingering = [];
    private Func<string, bool>? _concealed;

    /// <inheritdoc />
    public StoreCapabilities Capabilities =>
        inner.Capabilities with { ListingConsistency = ListingConsistency.Eventual };

    /// <summary>How many acknowledged puts are not yet listable.</summary>
    public int UnlistedCount
    {
        get
        {
            lock (_gate)
            {
                return _unlisted.Count;
            }
        }
    }

    /// <summary>How many deleted objects are still being listed.</summary>
    public int LingeringCount
    {
        get
        {
            lock (_gate)
            {
                return _lingering.Count;
            }
        }
    }

    /// <summary>
    /// Holds a listing back from keys this store did not write — the ordinary
    /// case, since the process that published is rarely the process that
    /// enumerates. A writer's put and a collector's listing are different
    /// clients of the same bucket, and it is the second one's view that lags.
    /// </summary>
    public void Conceal(Func<string, bool> predicate)
    {
        lock (_gate)
        {
            _concealed = predicate;
        }
    }

    /// <summary>Lets every held listing catch up — the moment the lag ends.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _unlisted.Clear();
            _lingering.Clear();
            _concealed = null;
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
        if (result.Outcome == PutOutcome.Created && Lags(key))
        {
            lock (_gate)
            {
                // A key written back over one this store is still listing as
                // deleted is visible again on the strength of the write, so
                // the lingering entry goes rather than racing the new one.
                _lingering.Remove(key);
                _unlisted.Add(key);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        HashSet<ObjectKey> hidden;
        List<ObjectEntry> lingering;
        Func<string, bool>? concealed;
        lock (_gate)
        {
            hidden = [.. _unlisted];
            lingering = [.. _lingering.Values];
            concealed = _concealed;
        }

        var merged = new List<ObjectEntry>();
        await foreach (var entry in inner.ListAsync(prefix, options, cancellationToken).ConfigureAwait(false))
        {
            if (!hidden.Contains(entry.Key) && concealed?.Invoke(entry.Key.ToString()) != true)
            {
                merged.Add(entry);
            }
        }

        // The inner store applied the prefix and the resume point to what it
        // holds; a lingering entry is not held any more, so both are applied
        // here, by the same ordinal comparison the local provider uses.
        foreach (var entry in lingering)
        {
            if (!prefix.Matches(entry.Key))
            {
                continue;
            }

            if (options.ResumeAfter is { } resumeAfter &&
                string.CompareOrdinal(entry.Key.Value, resumeAfter) <= 0)
            {
                continue;
            }

            merged.Add(entry);
        }

        merged.Sort(static (left, right) => left.Key.CompareTo(right.Key));

        foreach (var entry in merged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    /// <inheritdoc />
    public async ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
    {
        // The length is read before the object goes: a listing that still
        // shows a deleted object shows the length it had, and asking the
        // inner store afterwards would answer nothing.
        var metadata = await inner.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        var result = await inner.DeleteAsync(key, conditions, cancellationToken).ConfigureAwait(false);

        if (result.Outcome == DeleteOutcome.Deleted && Lags(key))
        {
            lock (_gate)
            {
                // An object deleted before its put ever became listable was
                // never visible; it does not now become so by being removed.
                if (!_unlisted.Remove(key) && metadata.Metadata is { } found)
                {
                    _lingering[key] = new ObjectEntry(key, found.Length, key.Value);
                }
            }
        }

        return result;
    }

    private bool Lags(ObjectKey key) => keyFilter is null || keyFilter(key.ToString());
}

/// <summary>
/// A store that declares less than it can do, so the capabilities the
/// contract calls load-bearing can be withheld one at a time
/// ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md)).
/// </summary>
/// <remarks>
/// It withholds the <em>behaviour</em> as well as the declaration, which is
/// the point: a decorator that said <c>RangedReads = false</c> and went on
/// serving ranges would let a caller that never checked pass anyway, and the
/// whole reason for a capability is that somebody checks. Absent conditional
/// create, <see cref="PutConditions.IfNotExists"/> is simply not honoured —
/// which is what a store without it does, and why admitting one unchecked
/// would let a publication overwrite instead of refusing.
/// </remarks>
public sealed class DegradedObjectStore(IObjectStore inner, StoreCapabilities capabilities) : IObjectStore
{
    /// <inheritdoc />
    public StoreCapabilities Capabilities { get; } = capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        if (range is not null && !Capabilities.RangedReads)
        {
            // Not an expected outcome with a result of its own: asking a
            // provider for something it never offered is a caller fault.
            throw new NotSupportedException("this store does not serve ranged reads");
        }

        return inner.OpenReadAsync(key, range, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        if (Capabilities.ConditionalCreate)
        {
            return await inner.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);
        }

        // A store without conditional create does not refuse a second write,
        // it performs it — and answers Created, because from its side one
        // happened. Downgrading the condition and delegating would model
        // nothing: the local provider refuses a rewrite whatever it is asked,
        // so the decorator has to reach past that refusal to be the store it
        // is declaring itself to be.
        await inner.DeleteAsync(key, DeleteConditions.None, cancellationToken).ConfigureAwait(false);
        return await inner.PutAsync(key, openContent, PutConditions.None, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);
}

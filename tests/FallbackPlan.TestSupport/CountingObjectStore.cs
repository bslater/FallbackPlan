using System.Runtime.CompilerServices;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.TestSupport;

/// <summary>
/// A store that passes everything through to another and counts what it was
/// asked to do: listings begun, entries yielded, objects read, written and
/// deleted, and metadata asked for.
/// </summary>
/// <remarks>
/// <para>
/// The measuring instrument for "work proportional to what changed"
/// (NFR-PERF-016). A pass's cost is not visible in its outcome — a pass that
/// copies nothing can still have walked the whole archive eight times — so the
/// only way to hold the claim is to count the calls underneath it.
/// </para>
/// <para>
/// <see cref="Listings"/> records the prefix each listing was opened under, in
/// order, because a listing scoped to <c>blobs/</c> and one scoped to
/// everything cost the same in a test with four objects in it and nothing
/// alike at scale. Counting calls alone would let a whole-archive walk hide
/// behind a small fixture.
/// </para>
/// </remarks>
/// <param name="inner">The store doing the actual work.</param>
public sealed class CountingObjectStore(IObjectStore inner) : IObjectStore
{
    private readonly List<string> _listings = [];
    private readonly List<string> _putKeys = [];
    private readonly List<string> _readKeys = [];
    private readonly Lock _gate = new();
    private long _entriesYielded;
    private long _reads;
    private long _metadataReads;
    private long _puts;
    private long _deletes;

    /// <summary>The prefix of every listing opened, in the order they were opened.</summary>
    public IReadOnlyList<string> Listings
    {
        get
        {
            lock (_gate)
            {
                return [.. _listings];
            }
        }
    }

    /// <summary>
    /// The key of every put attempted, in the order they were attempted, so a
    /// measurement can say which kind of object each request was for.
    /// </summary>
    public IReadOnlyList<string> PutKeys
    {
        get
        {
            lock (_gate)
            {
                return [.. _putKeys];
            }
        }
    }

    /// <summary>The key of every object opened for reading, in the order they were opened.</summary>
    public IReadOnlyList<string> ReadKeys
    {
        get
        {
            lock (_gate)
            {
                return [.. _readKeys];
            }
        }
    }

    /// <summary>How many entries every listing yielded between them.</summary>
    public long EntriesYielded => Interlocked.Read(ref _entriesYielded);

    /// <summary>How many objects were opened for reading.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    /// <summary>How many objects' metadata was asked for: a request like any other on a metered store.</summary>
    public long MetadataReads => Interlocked.Read(ref _metadataReads);

    /// <summary>How many puts were attempted.</summary>
    public long Puts => Interlocked.Read(ref _puts);

    /// <summary>How many deletes were attempted.</summary>
    public long Deletes => Interlocked.Read(ref _deletes);

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <summary>Forgets every count, so a second pass can be measured on its own.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _listings.Clear();
            _putKeys.Clear();
            _readKeys.Clear();
        }

        Interlocked.Exchange(ref _entriesYielded, 0);
        Interlocked.Exchange(ref _reads, 0);
        Interlocked.Exchange(ref _metadataReads, 0);
        Interlocked.Exchange(ref _puts, 0);
        Interlocked.Exchange(ref _deletes, 0);
    }

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _metadataReads);
        return inner.GetMetadataAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        lock (_gate)
        {
            _readKeys.Add(key.Value);
        }

        return inner.OpenReadAsync(key, range, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _puts);
        lock (_gate)
        {
            _putKeys.Add(key.Value);
        }

        return inner.PutAsync(key, openContent, conditions, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _listings.Add(prefix.Value);
        }

        await foreach (var entry in inner.ListAsync(prefix, options, cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _entriesYielded);
            yield return entry;
        }
    }

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _deletes);
        return inner.DeleteAsync(key, conditions, cancellationToken);
    }
}

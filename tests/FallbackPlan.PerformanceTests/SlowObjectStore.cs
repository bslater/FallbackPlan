using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// A store that answers correctly and slowly — a stand-in for the destination
/// the design actually cares about.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NullObjectStore"/> consumes and discards, so a PUT costs nothing
/// and cannot stall anything. That makes it the right instrument for measuring
/// the pipeline and the wrong one for measuring ADR-0029 §2, whose whole claim
/// is about destinations that are <i>not</i> free: a spinning disk, a LAN peer,
/// an object store on the other side of a WAN.
/// </para>
/// <para>
/// The delay is per PUT and fixed, which is not what a real network looks like.
/// It does not need to be: the question is whether the archive loop stops for
/// the duration of a transfer, and a constant is enough to answer it.
/// </para>
/// </remarks>
/// <param name="inner">The store that does the real work.</param>
/// <param name="latency">How long each put takes before it is accepted.</param>
public sealed class SlowObjectStore(IObjectStore inner, TimeSpan latency) : IObjectStore
{
    private long _imposedTicks;

    /// <summary>
    /// The total latency this store imposed, summed over every put.
    /// </summary>
    /// <remarks>
    /// The number that makes ADR-0029 §2 self-evidencing: when this exceeds the
    /// run's wall clock, the uploads provably overlapped the archive loop,
    /// because they could not otherwise have fitted.
    /// </remarks>
    public TimeSpan ImposedLatency => TimeSpan.FromTicks(Interlocked.Read(ref _imposedTicks));

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
        var result = await inner.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);
        await Task.Delay(latency, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _imposedTicks, latency.Ticks);
        return result;
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

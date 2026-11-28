using System.Collections.Concurrent;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository;

/// <summary>
/// Every record an archive session has written, so that a duplicate is
/// reused rather than written twice (09 §6; NFR-PERF-010). Records still in
/// flight, and those in a blob not yet uploaded, are kept in memory; once a
/// blob is uploaded its records wait in the session's spool instead
/// (ADR-0201), so what the set costs in memory does not grow with what the
/// snapshot stores.
/// </summary>
/// <remarks>
/// <para>
/// A reservation set, not a record of what was written. The concurrent stage
/// claims an object id before compressing it, so a duplicate segment loses
/// the race and is treated as reused rather than compressed and appended a
/// second time. ADR-0029 §1 frees which duplicate wins, but not how many
/// records get written, and the reuse suites assert exact record counts.
/// </para>
/// <para>
/// A record leaves memory only once it is in the spooled set, and a claim
/// looks in the spooled set again after taking its place in memory. A claim
/// that found the record in neither, and took its place in the moment
/// between its record moving and its looking, is undone rather than written.
/// </para>
/// </remarks>
internal sealed class WrittenSet : IDisposable
{
    private const byte Claimed = 0;
    private const byte Appended = 1;

    private readonly ConcurrentDictionary<ObjectId, byte> _inMemory = [];
    private readonly Lock _gate = new();
    private SpooledObjectSet? _spooled;

    /// <summary>
    /// Claims <paramref name="objectId"/> for writing, or returns
    /// <see langword="false"/> when the session has already written it or
    /// another of its segments has claimed it.
    /// </summary>
    public bool TryClaim(ObjectId objectId)
    {
        if (_inMemory.ContainsKey(objectId) || IsSpooled(objectId) || !_inMemory.TryAdd(objectId, Claimed))
        {
            return false;
        }

        if (IsSpooled(objectId))
        {
            _inMemory.TryRemove(new KeyValuePair<ObjectId, byte>(objectId, Claimed));
            return false;
        }

        return true;
    }

    /// <summary>A claimed record has been appended to the open blob.</summary>
    public void MarkAppended(ObjectId objectId) => _inMemory[objectId] = Appended;

    /// <summary>A record a resumed blob arrived holding, which this session did not write.</summary>
    public void AddResumed(ObjectId objectId) => _inMemory.TryAdd(objectId, Appended);

    /// <summary>Whether <paramref name="objectId"/> is claimed and not yet appended.</summary>
    public bool ClaimantPending(ObjectId objectId) =>
        _inMemory.TryGetValue(objectId, out var state) && state == Claimed;

    /// <summary>
    /// An uploaded blob's records leave memory for the spooled set at
    /// <paramref name="spooledPath"/>, which the first blob creates.
    /// </summary>
    public void Retire(IReadOnlyList<RecordTableEntry> table, string spooledPath)
    {
        ArgumentNullException.ThrowIfNull(table);
        SpooledObjectSet spooled;
        lock (_gate)
        {
            spooled = _spooled ??= SpooledObjectSet.Create(spooledPath);
        }

        spooled.AddRange(table.Select(entry => entry.ObjectId));
        foreach (var entry in table)
        {
            _inMemory.TryRemove(new KeyValuePair<ObjectId, byte>(entry.ObjectId, Appended));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _spooled?.Dispose();
            _spooled = null;
        }
    }

    private bool IsSpooled(ObjectId objectId) => Volatile.Read(ref _spooled) is { } spooled && spooled.Contains(objectId);
}

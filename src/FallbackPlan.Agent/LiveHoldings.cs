namespace FallbackPlan.Agent;

/// <summary>
/// How many of its set's newest backup's files each destination a sync is
/// filling holds so far (ADR-0088 Amendment 1). The status reads a live count
/// here in place of the one it works out from the ledger, which only moves
/// when a sync finishes.
/// </summary>
public sealed class LiveHoldings
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string SetId, string Destination), LiveHolding> _live = [];

    /// <summary>
    /// Called after every report with the set, the destination, the files
    /// held and the total, outside the registry's lock and on the sync's own
    /// thread.
    /// </summary>
    public Action<string, string, long, long>? Reported { get; set; }

    /// <summary>Starts counting one destination's files; disposing the handle ends it.</summary>
    /// <param name="setId">The set being synced.</param>
    /// <param name="destination">The destination being filled.</param>
    /// <param name="total">The newest backup's files, which the count is out of.</param>
    public LiveHolding Track(string setId, string destination, long total)
    {
        var holding = new LiveHolding(this, setId, destination, total);
        lock (_gate)
        {
            _live[(setId, destination)] = holding;
        }

        return holding;
    }

    /// <summary>The live count for one destination, or null when no sync is counting it.</summary>
    /// <param name="setId">The set.</param>
    /// <param name="destination">The destination.</param>
    public (long Held, long Total)? Find(string setId, string destination)
    {
        lock (_gate)
        {
            return _live.TryGetValue((setId, destination), out var holding) ? (holding.Held, holding.Total) : null;
        }
    }

    internal void Report(LiveHolding holding)
    {
        Reported?.Invoke(holding.SetId, holding.Destination, holding.Held, holding.Total);
    }

    internal void End(LiveHolding holding)
    {
        lock (_gate)
        {
            if (_live.TryGetValue((holding.SetId, holding.Destination), out var current) && ReferenceEquals(current, holding))
            {
                _live.Remove((holding.SetId, holding.Destination));
            }
        }
    }
}

/// <summary>One sync's live count of the files a destination holds.</summary>
public sealed class LiveHolding : IDisposable
{
    private readonly LiveHoldings _registry;
    private long _held;

    internal LiveHolding(LiveHoldings registry, string setId, string destination, long total)
    {
        _registry = registry;
        SetId = setId;
        Destination = destination;
        Total = total;
    }

    /// <summary>The set being synced.</summary>
    public string SetId { get; }

    /// <summary>The destination being filled.</summary>
    public string Destination { get; }

    /// <summary>The newest backup's files.</summary>
    public long Total { get; }

    /// <summary>Of them, how many the destination holds so far.</summary>
    public long Held => Interlocked.Read(ref _held);

    /// <summary>The destination now holds <paramref name="held"/> of the files.</summary>
    /// <param name="held">The files it holds.</param>
    public void Report(long held)
    {
        Interlocked.Exchange(ref _held, held);
        _registry.Report(this);
    }

    /// <inheritdoc />
    public void Dispose() => _registry.End(this);
}

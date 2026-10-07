namespace FallbackPlan.Agent;

/// <summary>
/// The restore drills under way, one per (set, destination) pair (FR-DRL-003,
/// ADR-0054 Amendment 6): whoever asks for a drill of a pair being drilled
/// joins the drill under way and is told its answer.
/// </summary>
/// <remarks>
/// <para>
/// A second drill of the same pair at once would prove nothing the first does
/// not. It would rebuild a second catalogue from the same replica, and both
/// would record, so the ledger's counts of drills in a row would move twice
/// for one state of the replica. The schedule's drill and a person's join one
/// another in either order.
/// </para>
/// <para>
/// A pair is let go before its drill is answered, so whoever is told a drill
/// has finished may ask for the next one at once and gets a new drill rather
/// than the answer just given, as a sync's queue slot is let go first.
/// </para>
/// </remarks>
/// <param name="joined">Told whenever an ask joins a drill under way; a test harness's observation.</param>
internal sealed class DrillFlights(Action<string, string>? joined)
{
    private readonly Dictionary<(string SetId, string Destination), Task<RecoveryDrillJob.DrillOutcome>> _flights = [];
    private readonly Lock _gate = new();

    /// <summary>Whether a drill of the pair is under way.</summary>
    /// <param name="setId">The backup set's id.</param>
    /// <param name="destinationName">The destination's declared name.</param>
    /// <returns><see langword="true"/> while one is.</returns>
    public bool IsDrilling(string setId, string destinationName)
    {
        lock (_gate)
        {
            return _flights.ContainsKey((setId, destinationName));
        }
    }

    /// <summary>
    /// Starts <paramref name="drill"/> for the pair, or joins the drill of the
    /// pair already under way.
    /// </summary>
    /// <param name="setId">The backup set's id.</param>
    /// <param name="destinationName">The destination's declared name.</param>
    /// <param name="drill">The drill to run when none is under way.</param>
    /// <returns>The answer of whichever drill of the pair this ask is part of.</returns>
    public Task<RecoveryDrillJob.DrillOutcome> RunOrJoin(
        string setId, string destinationName, Func<Task<RecoveryDrillJob.DrillOutcome>> drill)
    {
        var key = (setId, destinationName);
        var answer = new TaskCompletionSource<RecoveryDrillJob.DrillOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RecoveryDrillJob.DrillOutcome>? running;
        lock (_gate)
        {
            if (!_flights.TryGetValue(key, out running))
            {
                _flights[key] = answer.Task;
            }
        }

        if (running is not null)
        {
            joined?.Invoke(setId, destinationName);
            return running;
        }

        _ = FlyAsync(key, drill, answer);
        return answer.Task;
    }

    private async Task FlyAsync(
        (string SetId, string Destination) key,
        Func<Task<RecoveryDrillJob.DrillOutcome>> drill,
        TaskCompletionSource<RecoveryDrillJob.DrillOutcome> answer)
    {
        RecoveryDrillJob.DrillOutcome? outcome = null;
        Exception? failure = null;
        try
        {
            outcome = await drill().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        lock (_gate)
        {
            _flights.Remove(key);
        }

        if (failure is null)
        {
            answer.SetResult(outcome!);
        }
        else
        {
            answer.SetException(failure);
        }
    }
}

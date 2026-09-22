using Bodu;

namespace FallbackPlan.Agent;

/// <summary>
/// One scheduler pass against a state directory, start to finish.
/// </summary>
/// <remarks>
/// This is the shape a test wants and the shape <c>--once</c> wants: take the
/// writer role, evaluate every set, run what is due, release. The service does
/// the same thing on an interval without releasing in between, which is the
/// only difference between them.
/// </remarks>
public static class AgentPass
{
    /// <summary>Runs one pass over the configuration in <paramref name="stateDirectory"/>.</summary>
    /// <param name="archivesRoot">The root holding one staging archive per set (ADR-0034).</param>
    /// <param name="stateDirectory">The state directory whose writer role the pass takes.</param>
    /// <param name="now">The clock, passed in so schedule arithmetic stays pure.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What happened to each configured set.</returns>
    public static async ValueTask<AgentPassResult> RunAsync(
        string archivesRoot,
        string stateDirectory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(archivesRoot);
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);

        var options = new ServiceOptions
        {
            ArchivesRoot = archivesRoot,
            StateDirectory = stateDirectory,
        };

        await using var runtime = await ServiceRuntime.StartAsync(options, cancellationToken)
            .ConfigureAwait(false);

        // A person asked for this pass, so the background window does not
        // hold it (ADR-0069) — this entry point exists for exactly that.
        var result = await Scheduler
            .RunPassAsync(runtime, now, cancellationToken, userInitiated: true).ConfigureAwait(false);

        // --once means once, whole: the transfer phases the service would
        // leave running (ADR-0029 Amendment 4) are awaited here, because the
        // runtime — and with it every queued job — is torn down on return.
        await result.Transfers.WaitAsync(cancellationToken).ConfigureAwait(false);

        // And the drill phase with them. It starts when the transfers finish,
        // so returning after the transfers alone returns while a drill is
        // still issuing commands — which the disposal below then cancels
        // underneath it, and which the drill used to write into the state
        // directory as a recovery failure, after the caller believed the pass
        // was over ([ADR-0054](../../docs/adr/0054-scheduled-restore-drills.md)).
        await result.Drills.WaitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}

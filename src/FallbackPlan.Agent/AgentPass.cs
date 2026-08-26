using Bodu;
using FallbackPlan.Repository.Crypto;

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
    /// <param name="passphrase">Unlocks and creates the archives.</param>
    /// <param name="stateDirectory">The state directory whose writer role the pass takes.</param>
    /// <param name="now">The clock, passed in so schedule arithmetic stays pure.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What happened to each configured set.</returns>
    public static async ValueTask<AgentPassResult> RunAsync(
        string archivesRoot,
        Passphrase passphrase,
        string stateDirectory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(archivesRoot);
        ThrowHelper.ThrowIfNull(passphrase);
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);

        var options = new ServiceOptions
        {
            ArchivesRoot = archivesRoot,
            StateDirectory = stateDirectory,
        };

        await using var runtime = await ServiceRuntime.StartAsync(options, passphrase, cancellationToken)
            .ConfigureAwait(false);

        var result = await Scheduler.RunPassAsync(runtime, now, cancellationToken).ConfigureAwait(false);

        // --once means once, whole: the transfer phases the service would
        // leave running (ADR-0029 Amendment 4) are awaited here, because the
        // runtime — and with it every queued job — is torn down on return.
        await result.Transfers.WaitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}

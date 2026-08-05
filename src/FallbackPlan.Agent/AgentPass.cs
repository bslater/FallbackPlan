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
    /// <param name="repositoryPath">The repository to write to.</param>
    /// <param name="passphrase">Unlocks the repository.</param>
    /// <param name="stateDirectory">The state directory whose writer role the pass takes.</param>
    /// <param name="now">The clock, passed in so schedule arithmetic stays pure.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What happened to each configured set.</returns>
    public static async ValueTask<AgentPassResult> RunAsync(
        string repositoryPath,
        Passphrase passphrase,
        string stateDirectory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        var options = new ServiceOptions
        {
            RepositoryPath = repositoryPath,
            StateDirectory = stateDirectory,
        };

        await using var runtime = await ServiceRuntime.StartAsync(options, passphrase, cancellationToken)
            .ConfigureAwait(false);

        return await Scheduler.RunPassAsync(runtime, now, cancellationToken).ConfigureAwait(false);
    }
}

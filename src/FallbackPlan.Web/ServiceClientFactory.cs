using FallbackPlan.Api;
using FallbackPlan.Api.Transport;

namespace FallbackPlan.Web;

/// <summary>
/// Where the console gets a connected client from. A seam rather than a call so
/// the web host can be tested against a fake service client, which is the same
/// reason <c>FakeService</c> exists on the other side of the contract.
/// </summary>
public interface IServiceClientFactory
{
    /// <summary>Where the service is looked for, for messages.</summary>
    string Address { get; }

    /// <summary>Connects a client, or throws <see cref="ServiceConnectionException"/>.</summary>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>The connected client; dispose it when the exchange is over.</returns>
    ValueTask<IFallbackPlanClient> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The production factory: one fresh connection per exchange, over the local
/// binding.
/// </summary>
/// <remarks>
/// Per-exchange rather than held open, deliberately. A held connection
/// serialises every browser request behind the slowest one — a running
/// <c>verify --full</c> would block the status panel for its whole duration —
/// and it turns a service restart into a broken console. Connecting is cheap on
/// loopback, a socket that accepts is the liveness check (ADR-0028 §3), and a
/// connect that fails is exactly the fact the console must surface as
/// staleness (NFR-OPS-006).
/// </remarks>
/// <param name="stateDirectory">The state directory whose service is wanted.</param>
public sealed class LocalServiceClientFactory(string stateDirectory) : IServiceClientFactory
{
    /// <summary>What this console calls itself in the service's log.</summary>
    private const string ClientName = "fallbackplan-web";

    /// <inheritdoc/>
    public string Address { get; } = stateDirectory;

    /// <inheritdoc/>
    public async ValueTask<IFallbackPlanClient> ConnectAsync(CancellationToken cancellationToken) =>
        await LocalServiceClient.ConnectAsync(Address, ClientName, cancellationToken).ConfigureAwait(false);
}

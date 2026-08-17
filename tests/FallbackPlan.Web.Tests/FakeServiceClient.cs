using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Web;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// A service client that does nothing but answer, so the web host can be tested
/// for what it is: authentication, relay fidelity, and the event bridge. It is
/// the same shape as <c>Api.Tests/FakeService</c>, one contract layer up.
/// </summary>
internal sealed class FakeServiceClient : IFallbackPlanClient
{
    private readonly Channel<JobProgressEvent> _progress = Channel.CreateUnbounded<JobProgressEvent>();

    private long _sequence;

    public List<ServiceCommand> Received { get; } = [];

    public Func<ServiceCommand, ServiceResult> Respond { get; set; } = _ => new AcknowledgedResult();

    public ContractVersion ServiceContractVersion => ContractVersion.Current;

    public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
    {
        lock (Received)
        {
            Received.Add(command);
        }

        return ValueTask.FromResult(Respond(command));
    }

    public async IAsyncEnumerable<JobProgressEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var progress in _progress.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }
    }

    public void Emit(FallbackPlan.Domain.Jobs.JobProgress progress) =>
        _progress.Writer.TryWrite(new JobProgressEvent(Interlocked.Increment(ref _sequence), progress));

    /// <summary>
    /// A no-op: the web host disposes a client after every exchange, and the
    /// fake is shared across them so a test can read what it received.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The factory the web host is tested against.</summary>
internal sealed class FakeClientFactory : IServiceClientFactory
{
    public FakeServiceClient Client { get; } = new();

    /// <summary>When set, every connect fails the way an absent service does.</summary>
    public bool Unreachable { get; set; }

    public string Address => "(fake state directory)";

    public ValueTask<IFallbackPlanClient> ConnectAsync(CancellationToken cancellationToken) =>
        Unreachable
            ? throw new ServiceConnectionException("No service is listening at '(fake state directory)'.")
            : ValueTask.FromResult<IFallbackPlanClient>(Client);
}

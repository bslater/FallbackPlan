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
    // Fan-out, like the real hub: every watch gets its own channel and
    // every Emit reaches all of them. A single shared channel would SPLIT
    // events between two concurrent watches — misrepresenting the real
    // architecture, where each browser's SSE request holds its own upstream
    // watch and each watch receives everything. Events emitted before a
    // watch opens are buffered for the first subscriber (the pre-existing
    // convenience the single-watch tests lean on).
    private readonly List<Channel<JobProgressEvent>> _watches = [];
    private readonly Queue<JobProgressEvent> _beforeAnyWatch = [];
    private readonly Lock _gate = new();

    private long _sequence;

    public List<ServiceCommand> Received { get; } = [];

    public Func<ServiceCommand, ServiceResult> Respond { get; set; } = _ => new AcknowledgedResult();

    /// <summary>Counts watches the host has begun, if a test cares.</summary>
    public int WatchesOpened { get; private set; }

    /// <summary>Counts watches the host has let go — cancellation or disposal alike.</summary>
    public int WatchesEnded { get; private set; }

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
        var channel = Channel.CreateUnbounded<JobProgressEvent>();
        lock (_gate)
        {
            WatchesOpened++;
            while (_beforeAnyWatch.Count > 0)
            {
                channel.Writer.TryWrite(_beforeAnyWatch.Dequeue());
            }

            _watches.Add(channel);
        }

        try
        {
            await foreach (var progress in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return progress;
            }
        }
        finally
        {
            lock (_gate)
            {
                _watches.Remove(channel);
                WatchesEnded++;
            }
        }
    }

    public void Emit(FallbackPlan.Domain.Jobs.JobProgress progress)
    {
        lock (_gate)
        {
            var published = new JobProgressEvent(Interlocked.Increment(ref _sequence), progress);
            if (_watches.Count == 0)
            {
                _beforeAnyWatch.Enqueue(published);
                return;
            }

            foreach (var watch in _watches)
            {
                watch.Writer.TryWrite(published);
            }
        }
    }

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

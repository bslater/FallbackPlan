using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// A service that does nothing but answer, so the transport can be tested for
/// what it is: framing, handshake, and dispatch. Wiring a real engine in here
/// would test the engine again and the transport barely at all.
/// </summary>
internal sealed class FakeService : IFallbackPlanService
{
    private readonly Channel<JobProgressEvent> _progress =
        Channel.CreateUnbounded<JobProgressEvent>();

    private long _sequence;

    public List<ServiceCommand> Received { get; } = [];

    public Func<ServiceCommand, ServiceResult> Respond { get; set; } = _ => new AcknowledgedResult();

    /// <summary>
    /// When set, answers instead of <see cref="Respond"/> — for the tests that
    /// need to see the token the transport hands a command, or to hold an
    /// answer back while something happens to the connection.
    /// </summary>
    public Func<ServiceCommand, CancellationToken, ValueTask<ServiceResult>>? RespondAsync { get; set; }

    public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
    {
        lock (Received)
        {
            Received.Add(command);
        }

        return RespondAsync?.Invoke(command, cancellationToken) ?? ValueTask.FromResult(Respond(command));
    }

    public async IAsyncEnumerable<JobProgressEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var progress in _progress.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }
    }

    public void Emit(JobProgress progress) =>
        _progress.Writer.TryWrite(new JobProgressEvent(Interlocked.Increment(ref _sequence), progress));
}

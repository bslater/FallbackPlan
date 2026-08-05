using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Api;

/// <summary>
/// One progress observation as it travels to a client.
/// </summary>
/// <remarks>
/// This is a <b>client-facing channel, not telemetry</b> (ADR-0029 §5). It may
/// carry job identity because it travels to an authenticated local caller or a
/// paired remote client and is shown to the person whose data it is; the
/// OpenTelemetry instruments keep their closed four-attribute allowlist
/// untouched. Conflating the two is how a path ends up in a metrics backend.
/// </remarks>
/// <param name="Sequence">Monotonic per connection, so a client can detect a gap.</param>
/// <param name="Progress">The observation.</param>
public sealed record JobProgressEvent(long Sequence, JobProgress Progress);

/// <summary>
/// What a service does. Implemented by the service host; consumed by the
/// transport, which knows nothing about backups.
/// </summary>
public interface IFallbackPlanService
{
    /// <summary>Executes one command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancels the wait, not the job.</param>
    /// <returns>The outcome — never an exception for an expected failure.</returns>
    ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken);

    /// <summary>Streams job progress until the caller stops listening.</summary>
    /// <param name="cancellationToken">Ends the stream.</param>
    /// <returns>The progress events.</returns>
    IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What a client holds. The same shape as <see cref="IFallbackPlanService"/>
/// because a client is a proxy for one — which is what lets a front end be
/// written once and run against a local service, a remote service, or (in the
/// CLI's case) an in-process direct-mode implementation.
/// </summary>
public interface IFallbackPlanClient : IAsyncDisposable
{
    /// <summary>The contract version the far end speaks.</summary>
    ContractVersion ServiceContractVersion { get; }

    /// <summary>Sends one command and awaits its result.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The outcome.</returns>
    ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken);

    /// <summary>Streams job progress from the far end.</summary>
    /// <param name="cancellationToken">Ends the stream.</param>
    /// <returns>The progress events.</returns>
    IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The caller on the other end of a local connection, as the operating system
/// reports it ([T-16](../../docs/threat-model.md)).
/// </summary>
/// <remarks>
/// Authentication is the filesystem's: the endpoint lives in a directory only
/// the service account may write, so permissions decide who may connect at all.
/// This type is <b>identification</b> on top of that — who the admitted caller
/// is. Where the platform will not say, <see cref="IsKnown"/> is false and the
/// service says so rather than inventing an identity.
/// </remarks>
/// <param name="IsKnown">Whether the platform reported anything.</param>
/// <param name="UserId">The caller's user identifier, where known.</param>
/// <param name="ProcessId">The caller's process identifier, where known.</param>
/// <param name="Name">A human-readable rendering, where known.</param>
public sealed record PeerIdentity(bool IsKnown, long UserId, long ProcessId, string? Name)
{
    /// <summary>The identity used when the platform reports nothing.</summary>
    public static PeerIdentity Unknown { get; } = new(false, -1, -1, null);

    /// <summary>Renders for a log line or an error message.</summary>
    /// <returns>The rendering.</returns>
    public override string ToString() =>
        IsKnown ? (Name ?? $"uid {UserId}, pid {ProcessId}") : "an unidentified local caller";
}

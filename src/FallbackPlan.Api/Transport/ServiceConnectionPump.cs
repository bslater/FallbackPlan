using Bodu;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Api.Transport;

/// <summary>
/// Serves one already-connected client stream: the command-contract hello
/// (ADR-0028 §7), then request/response and progress-stream framing until the
/// client goes away.
/// </summary>
/// <remarks>
/// This is the part of the service boundary that is the same over every
/// transport. The local binding reaches it once an operating-system socket is
/// accepted; the remote binding reaches it once a peer session has
/// authenticated and opened (ADR-0030), carrying the same command contract as
/// its payload over the same length-prefixed framing. Neither the commands nor
/// the version gate know which transport carried them here, which is what keeps
/// "a console commands the same service a local caller does" true by
/// construction rather than by a second implementation.
/// </remarks>
public static class ServiceConnectionPump
{
    /// <summary>Runs the command contract over one connected stream until it closes.</summary>
    /// <param name="stream">The connected duplex stream. Not disposed here — the caller owns it.</param>
    /// <param name="service">The service to dispatch commands to.</param>
    /// <param name="clientDescription">How to name this client in log lines.</param>
    /// <param name="logger">Where connection-level diagnostics go (ADR-0043).</param>
    /// <param name="cancellationToken">Stops the pump when the listener is stopping.</param>
    /// <returns>A task that completes when the connection ends.</returns>
    public static async Task RunAsync(
        Stream stream,
        IFallbackPlanService service,
        string clientDescription,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(service);

        // Hoisted once: rendering it inside a log argument would be work done
        // even when the level is off (CA1873), and the frames below want it
        // anyway.
        var serviceVersion = ContractVersion.Current.ToString();

        try
        {
            if (await FrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false) is not HelloFrame hello)
            {
                return;
            }

            if (!ContractVersion.TryParse(hello.ContractVersion, out var clientVersion)
                || !ContractVersion.Current.IsCompatibleWith(clientVersion))
            {
                // Name both versions rather than dropping the connection: an
                // unexplained disconnect is the failure this rule exists for.
                await FrameCodec.WriteAsync(
                    stream,
                    new HelloAcknowledgementFrame(
                        serviceVersion,
                        false,
                        ContractVersion.DescribeMismatch(clientVersion, ContractVersion.Current)),
                    cancellationToken).ConfigureAwait(false);
                Log.VersionRefused(logger, hello.ContractVersion, serviceVersion);
                return;
            }

            await FrameCodec.WriteAsync(
                stream,
                new HelloAcknowledgementFrame(serviceVersion, true, null),
                cancellationToken).ConfigureAwait(false);

            Log.ClientConnected(logger, hello.ContractVersion, serviceVersion, accepted: true);
            await PumpAsync(stream, service, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException)
        {
            // A client that disconnects, or sends nonsense, ends its own
            // connection and nothing else. Bounded parsing is what makes that
            // true rather than hopeful (T-7).
            Log.ConnectionFailed(logger, exception.Message);
        }
    }

    private static async Task PumpAsync(
        Stream stream, IFallbackPlanService service, ILogger logger, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await FrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            switch (frame)
            {
                case null:
                    return;

                case RequestFrame request:
                {
                    // Timed and named only when somebody is listening: reading
                    // the type names is work, and CA1873 is an error, so both
                    // land in locals behind the level check rather than in a
                    // log argument.
                    var timing = logger.IsEnabled(LogLevel.Debug);
                    var startedAt = timing ? Stopwatch.GetTimestamp() : 0L;

                    var result = await service.ExecuteAsync(request.Command, cancellationToken)
                        .ConfigureAwait(false);
                    await FrameCodec.WriteAsync(stream, new ResponseFrame(request.Id, result), cancellationToken)
                        .ConfigureAwait(false);

                    if (timing)
                    {
                        // The verb and the result kind, never the command
                        // itself: arguments carry set names, paths and sealed
                        // envelopes, and none of those belong in a connection
                        // log.
                        var verb = request.Command.GetType().Name;
                        var answered = result.GetType().Name;
                        var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                        Log.CommandHandled(logger, verb, answered, elapsedMs);
                    }

                    break;
                }

                case WatchFrame:
                    Log.WatchOpened(logger);
                    await StreamProgressAsync(stream, service, cancellationToken).ConfigureAwait(false);
                    return;

                default:
                    return;
            }
        }
    }

    private static async Task StreamProgressAsync(
        Stream stream, IFallbackPlanService service, CancellationToken cancellationToken)
    {
        await foreach (var progress in service.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            await FrameCodec.WriteAsync(stream, new ProgressFrame(progress), cancellationToken).ConfigureAwait(false);
        }
    }
}

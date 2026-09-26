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
        // The token a command runs under is the CONNECTION's life, not the
        // listener's. The distinction stayed invisible until a preview scan
        // outlived the browser that asked for it by seven hours and answered
        // into a broken pipe (2026-08-25): the service side of cancellation —
        // OnReaderLaneAsync releasing the lane when the caller's token fires —
        // existed and was dead, because the token handed in here never fired
        // before shutdown.
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<WireFrame?>? readAhead = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WireFrame? frame;
                if (readAhead is null)
                {
                    frame = await FrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    frame = await readAhead.ConfigureAwait(false);
                    readAhead = null;
                }

                switch (frame)
                {
                    case null:
                        return;

                    case RequestFrame request:
                    {
                        var startedAt = Stopwatch.GetTimestamp();

                        // Read the next frame NOW, while the command runs. The
                        // contract is strictly request/response, so a client
                        // sends nothing until it has its answer — the only way
                        // this read resolves mid-command is the client going
                        // away, and that is exactly when the command should
                        // stop. A frame it does return early (a client
                        // free-running ahead) is simply held for the next turn
                        // of the loop.
                        readAhead = ReadBehindAsync(stream, connection, cancellationToken);

                        ServiceResult? result;
                        try
                        {
                            result = await service.ExecuteAsync(request.Command, connection.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (
                            connection.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            result = null;
                        }

                        if (result is null
                            || (connection.IsCancellationRequested && !cancellationToken.IsCancellationRequested))
                        {
                            // Nobody is owed the answer, and writing it would
                            // only manufacture a broken-pipe warning out of a
                            // hang-up.
                            var abandonedVerb = request.Command.GetType().Name;
                            var abandonedAfterMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                            Log.CommandAbandoned(logger, abandonedVerb, abandonedAfterMs);
                            return;
                        }

                        await FrameCodec.WriteAsync(stream, new ResponseFrame(request.Id, result), cancellationToken)
                            .ConfigureAwait(false);

                        if (logger.IsEnabled(LogLevel.Debug))
                        {
                            // The verb and the result kind, never the command
                            // itself: arguments carry set names, paths and
                            // sealed envelopes, and none of those belong in a
                            // connection log.
                            var verb = request.Command.GetType().Name;
                            var answered = result.GetType().Name;
                            var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                            Log.CommandHandled(logger, verb, answered, elapsedMs);
                        }

                        break;
                    }

                    case WatchFrame watch:
                    {
                        Log.WatchOpened(logger);

                        // This connection's gate has seen no session — the
                        // watch took a fresh connection — so the frame's
                        // session is presented to it first (contract 1.20).
                        // A refusal is not an error: the gate then answers
                        // the anonymous empty stream it always answered.
                        if (watch.Session is { } session)
                        {
                            _ = await service.ExecuteAsync(new ResumeSessionCommand(session), connection.Token)
                                .ConfigureAwait(false);
                        }

                        // A watch is one-way from here — the client sends
                        // nothing after the WatchFrame — so a read armed
                        // behind the stream resolves only when the client
                        // hangs up, and that is the moment the subscription
                        // must end. Without it, a dead watcher was reaped
                        // only by the NEXT event's failed write; an idle
                        // service produces no next event, so the stale
                        // subscription lingered and the service could not
                        // tell "nobody is watching" from "not yet written
                        // to".
                        readAhead = ReadBehindAsync(stream, connection, cancellationToken);
                        try
                        {
                            await StreamProgressAsync(stream, service, connection.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (
                            connection.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            // The client hung up; ending the stream is the point.
                        }

                        return;
                    }

                    default:
                        return;
                }
            }
        }
        finally
        {
            if (readAhead is not null)
            {
                // However the pump leaves — hang-up, shutdown, a write into a
                // pipe that just broke — a read it started must not surface as
                // an unobserved task exception: the connection is over either
                // way.
                Observe(readAhead);
            }
        }
    }

    /// <summary>
    /// The between-commands read, started while a command is still running so
    /// that end-of-stream — the client hanging up — cancels the connection's
    /// work instead of waiting politely behind it.
    /// </summary>
    private static async Task<WireFrame?> ReadBehindAsync(
        Stream stream, CancellationTokenSource connection, CancellationToken cancellationToken)
    {
        try
        {
            var frame = await FrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                TryCancel(connection);
            }

            return frame;
        }
        catch (Exception)
        {
            // A broken pipe and a garbage frame both end the connection, so
            // both abandon its work; the pump's own await of this task is what
            // surfaces the exception when nothing was running.
            TryCancel(connection);
            throw;
        }
    }

    /// <summary>Cancels unless the pump already left and took the source with it.</summary>
    private static void TryCancel(CancellationTokenSource connection)
    {
        try
        {
            connection.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The connection ended first; there is nothing left to stop.
        }
    }

    /// <summary>
    /// Keeps a read the pump is abandoning from surfacing as an unobserved
    /// task exception: the connection is over either way.
    /// </summary>
    private static void Observe(Task readAhead) =>
        _ = readAhead.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private static async Task StreamProgressAsync(
        Stream stream, IFallbackPlanService service, CancellationToken cancellationToken)
    {
        await foreach (var progress in service.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            await FrameCodec.WriteAsync(stream, new ProgressFrame(progress), cancellationToken).ConfigureAwait(false);
        }
    }
}

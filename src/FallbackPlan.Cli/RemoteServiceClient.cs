using Bodu;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using System.Runtime.CompilerServices;
using ProtocolIdentity = FallbackPlan.Protocol.PeerIdentity;

namespace FallbackPlan.Cli;

/// <summary>
/// The console's client over the remote binding (ADR-0028 §5; ADR-0030): dial
/// the service, authenticate as a pinned peer, and then speak the same command
/// contract a local caller does over the opened TLS session.
/// </summary>
/// <remarks>
/// The shape mirrors <see cref="LocalServiceClient"/>: a request/response
/// exchange serialised over one connection, and a watch that takes a second
/// connection of its own. The difference is the reaching: a peer session
/// handshake must open before the first command, and it refuses hard if the
/// service that answers is not the one whose identity was pinned
/// (<c>identity_changed</c>) or if this console was never paired
/// (<c>not_paired</c>).
/// </remarks>
public sealed class RemoteServiceClient : IFallbackPlanClient
{
    private readonly PeerTlsConnection _connection;
    private readonly PeerSession _session;
    private readonly PeerKeypair _keypair;
    private readonly PeerGrantStore _grants;
    private readonly ProtocolIdentity _expected;
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private long _nextRequestId;

    private RemoteServiceClient(
        PeerTlsConnection connection,
        PeerSession session,
        PeerKeypair keypair,
        PeerGrantStore grants,
        ProtocolIdentity expected,
        string host,
        int port,
        ContractVersion serviceVersion)
    {
        _connection = connection;
        _session = session;
        _keypair = keypair;
        _grants = grants;
        _expected = expected;
        _host = host;
        _port = port;
        ServiceContractVersion = serviceVersion;
    }

    /// <inheritdoc/>
    public ContractVersion ServiceContractVersion { get; }

    /// <summary>
    /// Dials and authenticates against the service pinned for <paramref name="expected"/>.
    /// </summary>
    /// <param name="host">The service's host.</param>
    /// <param name="port">The service's remote-binding port.</param>
    /// <param name="keypair">This console's peer keypair.</param>
    /// <param name="grants">The pinned pairings this console holds.</param>
    /// <param name="expected">The service identity this console pinned for this endpoint.</param>
    /// <param name="clientName">What to call this client in the service's log.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>The connected client.</returns>
    /// <exception cref="ServiceConnectionException">The peer refused, or the command contract mismatched.</exception>
    public static async ValueTask<RemoteServiceClient> ConnectAsync(
        string host,
        int port,
        PeerKeypair keypair,
        PeerGrantStore grants,
        ProtocolIdentity expected,
        string clientName,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(host);
        ThrowHelper.ThrowIfNull(keypair);
        ThrowHelper.ThrowIfNull(grants);
        ThrowHelper.ThrowIfNull(expected);

        var (connection, session) = await OpenSessionAsync(host, port, keypair, grants, expected, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var version = await HelloAsync(session.Stream, clientName, cancellationToken).ConfigureAwait(false);
            return new RemoteServiceClient(connection, session, keypair, grants, expected, host, port, version);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(command);

        var id = Interlocked.Increment(ref _nextRequestId);
        await _exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_session.Stream, new RequestFrame(id, command), cancellationToken)
                .ConfigureAwait(false);

            var frame = await FrameCodec.ReadAsync(_session.Stream, cancellationToken).ConfigureAwait(false);
            return frame switch
            {
                ResponseFrame response when response.Id == id => response.Result,
                ResponseFrame => throw new ServiceConnectionException("The service answered a request that was not outstanding."),
                null => throw new ServiceConnectionException("The service closed the connection without answering."),
                _ => throw new ServiceConnectionException("The service sent a frame that was not a response."),
            };
        }
        finally
        {
            _exchange.Release();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<JobProgressEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A watch takes its own connection and its own full peer handshake: a
        // stream and a command exchange have different lifetimes, exactly as on
        // the local binding.
        var (connection, session) = await OpenSessionAsync(
            _host, _port, _keypair, _grants, _expected, cancellationToken).ConfigureAwait(false);

        await using (connection.ConfigureAwait(false))
        {
            await HelloAsync(session.Stream, "fallbackplan-cli-watch", cancellationToken).ConfigureAwait(false);
            await FrameCodec.WriteAsync(session.Stream, new WatchFrame(), cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var frame = await FrameCodec.ReadAsync(session.Stream, cancellationToken).ConfigureAwait(false);
                if (frame is not ProgressFrame progress)
                {
                    yield break;
                }

                yield return progress.Event;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _exchange.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask<(PeerTlsConnection Connection, PeerSession Session)> OpenSessionAsync(
        string host,
        int port,
        PeerKeypair keypair,
        PeerGrantStore grants,
        ProtocolIdentity expected,
        CancellationToken cancellationToken)
    {
        var connection = await PeerTlsConnection.DialAsync(host, port, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var session = await PeerSessionDriver.DialAsync(
                connection, keypair, grants, expected, "fallbackplan-cli", terms: null, cancellationToken)
                .ConfigureAwait(false);
            return (connection, session);
        }
        catch (PeerProtocolException refusal)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new ServiceConnectionException(
                $"The service refused the connection: {refusal.Reason} — {refusal.Message}", refusal);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<ContractVersion> HelloAsync(
        Stream stream, string clientName, CancellationToken cancellationToken)
    {
        await FrameCodec.WriteAsync(
            stream, new HelloFrame(ContractVersion.Current.ToString(), clientName), cancellationToken)
            .ConfigureAwait(false);

        if (await FrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            is not HelloAcknowledgementFrame acknowledgement)
        {
            throw new ServiceConnectionException("The service did not answer the command-contract hello.");
        }

        if (!acknowledgement.Accepted)
        {
            throw new ServiceConnectionException(
                acknowledgement.Message ?? "The service refused the connection without saying why.");
        }

        if (!ContractVersion.TryParse(acknowledgement.ContractVersion, out var serviceVersion))
        {
            throw new ServiceConnectionException(
                $"The service reported contract version '{acknowledgement.ContractVersion}', which is not a version.");
        }

        return serviceVersion;
    }
}

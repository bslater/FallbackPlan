using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Agent;

/// <summary>
/// The owner side of a retrieval session (peer-protocol 07, ADR-0041): one
/// dialled, authenticated, feature-negotiated connection to a peer
/// destination, opened onto one replica, serving strictly one request at a
/// time. The transport a <see cref="PeerRetrievalObjectStore"/> reads
/// through.
/// </summary>
internal sealed class PeerRetrievalClient : IAsyncDisposable
{
    private readonly PeerKeypair _keypair;
    private readonly PeerTlsConnection _connection;
    private readonly PeerSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PeerRetrievalClient(PeerKeypair keypair, PeerTlsConnection connection, PeerSession session)
    {
        _keypair = keypair;
        _connection = connection;
        _session = session;
    }

    /// <summary>
    /// Dials the destination, opens the named replica, and returns the live
    /// client. An all-zero repository id opens the owner inventory instead
    /// (07 §3.5): listings then answer the repository ids this peer owns
    /// there, which is how a hub that lost its staging learns what to ask
    /// for.
    /// </summary>
    /// <exception cref="PeerProtocolException">The peer refused — not paired, feature absent, or not this owner's replica.</exception>
    /// <exception cref="IOException">The peer is unreachable.</exception>
    public static async Task<PeerRetrievalClient> DialAsync(
        ServiceRuntime runtime,
        DestinationConfiguration destination,
        ReadOnlyMemory<byte> repositoryId,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(runtime);
        ThrowHelper.ThrowIfNull(destination);

        if (!PeerAddress.TryResolve(runtime, destination, out var resolved, out var refusal))
        {
            throw new IOException(refusal ?? $"Destination '{destination.Name}' has no dialable address.");
        }

        var (grants, grant, host, port) = resolved!;
        var keypair = PeerKeypairStore.Open(runtime.Options.StateDirectory);
        PeerTlsConnection? connection = null;
        try
        {
            connection = await PeerTlsConnection.DialAsync(host, port, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            var session = await PeerSessionDriver.DialAsync(
                connection, keypair, grants, grant.Identity, "fallbackplan-agent", terms: null,
                requiredFeatures: [PeerSessionNegotiation.RetrievalFeature],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await PeerFrame.WriteAsync(
                session.Stream,
                new RetrieveOpen(repositoryId, ReplicationInitiator.FormatCapability),
                cancellationToken).ConfigureAwait(false);
            await ReplicationWire.ReadAsync(
                session.Stream, PeerMessageType.RetrieveReady, RetrieveReady.Read, cancellationToken)
                .ConfigureAwait(false);

            return new PeerRetrievalClient(keypair, connection, session);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            keypair.Dispose();
            throw;
        }
    }

    /// <summary>One page of the replica's listing.</summary>
    public async ValueTask<RetrieveListPage> ListPageAsync(
        string prefix, string after, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PeerFrame.WriteAsync(_session.Stream, new RetrieveList(prefix, after), cancellationToken)
                .ConfigureAwait(false);
            return await ReplicationWire.ReadAsync(
                _session.Stream, PeerMessageType.RetrieveListPage, RetrieveListPage.Read, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One bounded read of one object; length 0 stats it.</summary>
    public async ValueTask<RetrieveData> ReadAsync(
        string key, ulong offset, ulong length, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PeerFrame.WriteAsync(_session.Stream, new RetrieveRead(key, offset, length), cancellationToken)
                .ConfigureAwait(false);
            return await ReplicationWire.ReadAsync(
                _session.Stream, PeerMessageType.RetrieveData, RetrieveData.Read, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _keypair.Dispose();
        _gate.Dispose();
    }
}

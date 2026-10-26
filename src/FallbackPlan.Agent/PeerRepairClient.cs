using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Agent;

/// <summary>
/// The owner side of a repair session (peer-protocol 08,
/// [ADR-0100](../../docs/adr/0100-a-peer-replaces-what-its-own-check-finds-damaged.md)):
/// one dialled, authenticated connection to a peer destination over which
/// replacements are offered for blobs found damaged in this owner's replica
/// there, one at a time.
/// </summary>
internal sealed class PeerRepairClient : IAsyncDisposable
{
    private readonly PeerKeypair _keypair;
    private readonly PeerTlsConnection _connection;
    private readonly Stream _stream;
    private readonly PacedStream? _paced;
    private readonly ReadOnlyMemory<byte> _repositoryId;

    private PeerRepairClient(
        PeerKeypair keypair, PeerTlsConnection connection, Stream session, PacedStream? paced,
        ReadOnlyMemory<byte> repositoryId)
    {
        _keypair = keypair;
        _connection = connection;
        _stream = (Stream?)paced ?? session;
        _paced = paced;
        _repositoryId = repositoryId;
    }

    /// <summary>
    /// Dials the destination and returns the live client. The feature is
    /// required of the peer, so one whose build takes no repair is refused at
    /// the hello and never sent an offer it would refuse as unknown.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="destination">The peer.</param>
    /// <param name="repositoryId">The replica's repository.</param>
    /// <param name="limiter">The peer's transfer limit, which a replacement's bytes cross; null for none.</param>
    /// <param name="cancellationToken">Cancels the dial.</param>
    /// <exception cref="PeerProtocolException">The peer refused — not paired, or the feature is absent.</exception>
    /// <exception cref="IOException">The peer is unreachable.</exception>
    public static async Task<PeerRepairClient> DialAsync(
        ServiceRuntime runtime,
        DestinationConfiguration destination,
        ReadOnlyMemory<byte> repositoryId,
        ByteRateLimiter? limiter,
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
                requiredFeatures: [PeerSessionNegotiation.ReplicaRepairFeature],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // The session outlives the wrapper, so it is left open.
            return new PeerRepairClient(
                keypair, connection, session.Stream,
                limiter is null ? null : new PacedStream(session.Stream, limiter, leaveOpen: true),
                repositoryId);
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

    /// <summary>Offers a replacement of <paramref name="length"/> bytes, and answers the peer's verdict on its own copy.</summary>
    /// <param name="key">The blob's store key.</param>
    /// <param name="length">The length of the copy that would be sent.</param>
    /// <param name="cancellationToken">Cancels the offer.</param>
    public async ValueTask<RepairVerdictCode> OfferAsync(string key, long length, CancellationToken cancellationToken)
    {
        await PeerFrame.WriteAsync(_stream, new RepairOffer(_repositoryId, key, (ulong)length), cancellationToken)
            .ConfigureAwait(false);
        return await VerdictAsync(
            key,
            verdict => verdict is RepairVerdictCode.Send or RepairVerdictCode.Intact or RepairVerdictCode.NotHeld
                or RepairVerdictCode.LengthDiffers,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the replacement a <see cref="RepairVerdictCode.Send"/> asked
    /// for, whole and from its start, and answers what the peer did with it.
    /// </summary>
    /// <param name="key">The blob's store key, as offered.</param>
    /// <param name="content">The replacement's bytes.</param>
    /// <param name="length">How many bytes it is, as offered.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async ValueTask<RepairVerdictCode> SendAsync(
        string key, Stream content, long length, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(content);

        await PeerFrame.WriteAsync(_stream, new ReplicationObject(key, (ulong)length), cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[ReplicationChunk.MaximumBytes];
        var offset = 0UL;
        while (offset < (ulong)length)
        {
            var got = await content
                .ReadAsync(buffer.AsMemory(0, (int)Math.Min((ulong)buffer.Length, (ulong)length - offset)), cancellationToken)
                .ConfigureAwait(false);
            if (got == 0)
            {
                throw new IOException($"'{key}' ended at {offset} of the {length} bytes offered");
            }

            await PeerFrame.WriteAsync(_stream, new ReplicationChunk(offset, buffer.AsMemory(0, got)), cancellationToken)
                .ConfigureAwait(false);
            offset += (ulong)got;
        }

        return await VerdictAsync(
            key, verdict => verdict is RepairVerdictCode.Replaced or RepairVerdictCode.Unmatched, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_paced is not null)
        {
            await _paced.DisposeAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        _keypair.Dispose();
    }

    /// <summary>Reads a verdict, holding it to the key asked about and to the verdicts that question admits (08 §4.2).</summary>
    private async ValueTask<RepairVerdictCode> VerdictAsync(
        string key, Func<RepairVerdictCode, bool> admitted, CancellationToken cancellationToken)
    {
        var verdict = await ReplicationWire.ReadAsync(
            _stream, PeerMessageType.RepairVerdict, RepairVerdict.Read, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(verdict.Key, key, StringComparison.Ordinal))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"A repair verdict names '{verdict.Key}'; '{key}' was offered.");
        }

        if (!admitted(verdict.Verdict))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"A repair verdict of {verdict.Verdict} does not answer what was asked (08 §4.2).");
        }

        return verdict.Verdict;
    }
}

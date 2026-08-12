using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// The source half of replication (specification peer-protocol 03): over an open
/// peer session, offer a repository, learn what the destination already holds,
/// and stream the objects it lacks. It reads raw objects and never decrypts one
/// — replication forwards ciphertext (03 §8).
/// </summary>
internal static class ReplicationInitiator
{
    /// <summary>The repository format capability this build speaks (03 §3.1).</summary>
    public const uint FormatCapability = 1;

    /// <summary>Pushes every object the destination lacks, and returns how many it committed.</summary>
    /// <param name="source">The raw object store to read from.</param>
    /// <param name="repositoryId">The repository the objects belong to (16 bytes).</param>
    /// <param name="stream">The open session stream.</param>
    /// <param name="cancellationToken">Cancels the push.</param>
    /// <returns>The count the destination acknowledged committing.</returns>
    public static async Task<long> PushAllAsync(
        IObjectStore source, ReadOnlyMemory<byte> repositoryId, Stream stream, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(stream);

        try
        {
            await PeerFrame.WriteAsync(
                stream, new ReplicationOffer(repositoryId, FormatCapability, "all"), cancellationToken)
                .ConfigureAwait(false);

            var held = await ReadInventoryAsync(stream, cancellationToken).ConfigureAwait(false);

            var sent = 0L;
            await foreach (var entry in source.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                if (held.Contains(entry.Key.Value))
                {
                    continue;
                }

                await SendObjectAsync(source, entry, stream, cancellationToken).ConfigureAwait(false);
                sent++;
            }

            await PeerFrame.WriteAsync(stream, new ReplicationComplete((ulong)sent), cancellationToken)
                .ConfigureAwait(false);

            var ack = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.ReplicationAck, ReplicationAck.Read, cancellationToken).ConfigureAwait(false);
            return (long)ack.Count;
        }
        catch (PeerProtocolException exception)
        {
            if (!exception.ReceivedFromPeer)
            {
                await ReplicationWire.TryRefuseAsync(stream, exception).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task<HashSet<string>> ReadInventoryAsync(Stream stream, CancellationToken cancellationToken)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var page = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.ReplicationInventory, ReplicationInventory.Read, cancellationToken)
                .ConfigureAwait(false);

            foreach (var objectKey in page.Keys)
            {
                held.Add(objectKey);
            }

            if (!page.More)
            {
                return held;
            }
        }
    }

    private static async Task SendObjectAsync(
        IObjectStore source, ObjectEntry entry, Stream stream, CancellationToken cancellationToken)
    {
        using var read = await source.OpenReadAsync(entry.Key, range: null, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != OpenReadOutcome.Found || read.Content is null)
        {
            // Immutable objects are never deleted in this build, so a key that
            // just listed must still read; treating the impossible as a fault is
            // honest rather than silently shipping a short object.
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"Object {entry.Key.Value} listed but could not be read to send.");
        }

        await PeerFrame.WriteAsync(
            stream, new ReplicationObject(entry.Key.Value, (ulong)entry.Length), cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[ReplicationChunk.MaximumBytes];
        var offset = 0UL;
        int got;
        while ((got = await read.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await PeerFrame.WriteAsync(
                stream, new ReplicationChunk(offset, buffer.AsMemory(0, got)), cancellationToken).ConfigureAwait(false);
            offset += (ulong)got;
        }
    }
}

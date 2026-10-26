using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// The destination side of a repair (specification peer-protocol 08,
/// [ADR-0100](../../docs/adr/0100-a-peer-replaces-what-its-own-check-finds-damaged.md)):
/// a replica's owner offers replacements for blobs its sweep found damaged
/// here, and each is taken only on evidence this side holds itself.
/// </summary>
/// <remarks>
/// <para>
/// This side holds no key, so it cannot tell which blob a key names, and it
/// takes nothing on the source's word. It can tell whether bytes have
/// changed since they were sealed — the locator check — and that is the
/// whole of what it acts on: it replaces a copy only when that copy fails
/// the check, only with bytes of the copy's length that pass it, and only
/// when they state the locator the copy still states. A source, buggy or
/// taken over, can therefore put back only the bytes that were written
/// here, and only where they are no longer here — which is why this needs
/// no authority the replication grant does not already give.
/// </para>
/// <para>
/// Served only for the replica's owner, as retrieval is: a repository
/// another peer stores here and one never stored here refuse identically.
/// A repair never creates an object, so it never moves the quota (05).
/// </para>
/// </remarks>
internal static class RepairResponder
{
    /// <summary>Serves one repair session over an open peer stream.</summary>
    /// <param name="replicasRoot">Where replicas live, one directory per repository id.</param>
    /// <param name="spoolRoot">Where a replacement is staged while it arrives.</param>
    /// <param name="stream">The open session stream, positioned after the first offer.</param>
    /// <param name="peer">The authenticated peer.</param>
    /// <param name="owners">The replica attribution ledger (peer-protocol 05 §2).</param>
    /// <param name="firstOffer">The body of the already-read first offer, which names the repository.</param>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <returns>How many copies were replaced.</returns>
    public static async Task<int> ServeAsync(
        string replicasRoot,
        string spoolRoot,
        Stream stream,
        PeerGrant peer,
        Application.ReplicaOwnerStore owners,
        System.Formats.Cbor.CborReader firstOffer,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(replicasRoot);
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolRoot);
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(peer);
        ThrowHelper.ThrowIfNull(owners);
        ThrowHelper.ThrowIfNull(firstOffer);

        try
        {
            var first = RepairOffer.Read(firstOffer);
            var repositoryIdHex = Convert.ToHexStringLower(first.RepositoryId.Span);
            var replicaPath = Path.Combine(replicasRoot, repositoryIdHex);
            if (!owners.OwnedBy(peer.Identity.Fingerprint).Contains(repositoryIdHex, StringComparer.Ordinal)
                || !Directory.Exists(replicaPath))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.TermsRefused,
                    "No replica of that repository is repairable under this pairing.");
            }

            var replica = new LocalFileSystemObjectStore(replicaPath);
            var replaced = 0;
            var offer = first;
            while (true)
            {
                if (await ServeOfferAsync(replica, spoolRoot, stream, offer, cancellationToken).ConfigureAwait(false))
                {
                    replaced++;
                }

                // The owner simply closing the stream ends the session.
                var frame = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return replaced;
                }

                if (frame.Value.Type != PeerMessageType.RepairOffer)
                {
                    throw new PeerProtocolException(
                        PeerRefusalReason.Malformed, $"A {frame.Value.Type} is not part of a repair session (08 §2).");
                }

                offer = RepairOffer.Read(frame.Value.Body);
                if (!offer.RepositoryId.Span.SequenceEqual(first.RepositoryId.Span))
                {
                    throw new PeerProtocolException(
                        PeerRefusalReason.Malformed, "A repair session's offers name one repository (08 §2).");
                }
            }
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

    /// <summary>Answers one offer, and takes the replacement if this side asked for it.</summary>
    /// <returns>Whether the copy was replaced.</returns>
    private static async Task<bool> ServeOfferAsync(
        LocalFileSystemObjectStore replica, string spoolRoot, Stream stream, RepairOffer offer,
        CancellationToken cancellationToken)
    {
        // The locator check is a blob's; nothing else a replica holds has
        // anything a keyless holder could check a replacement against.
        if (!ObjectKey.TryParse(offer.Key, out var key) || !offer.Key.StartsWith("blobs/", StringComparison.Ordinal))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"A repair names a blob, and '{offer.Key}' is not one (08 §3.1).");
        }

        var (verdict, locator) = await JudgeCopyAsync(replica, key, offer.Length, cancellationToken)
            .ConfigureAwait(false);
        await PeerFrame.WriteAsync(stream, new RepairVerdict(offer.Key, verdict), cancellationToken)
            .ConfigureAwait(false);
        if (verdict != RepairVerdictCode.Send)
        {
            return false;
        }

        var header = await ReplicationWire.ReadAsync(
            stream, PeerMessageType.ReplicationObject, ReplicationObject.Read, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(header.Key, offer.Key, StringComparison.Ordinal)
            || header.Length != offer.Length || header.ResumeOffset != 0)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A replacement is the whole object that was offered (08 §3.3).");
        }

        var staged = Path.Combine(spoolRoot, "repair", Guid.NewGuid().ToString("n"));
        try
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await ReceiveAsync(stream, staged, offer.Length, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception storage) when (storage is IOException or UnauthorizedAccessException)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.StorageExhausted, $"The destination cannot store: {storage.Message}", storage);
            }

            BlobLocatorCheck.Result replacement;
            await using (var read = File.OpenRead(staged))
            {
                replacement = await BlobLocatorCheck.CheckAsync(read, (long)offer.Length, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Bytes consistent with themselves are not thereby the blob that
            // was sealed: the locator the damaged copy still states is.
            if (!replacement.Passes || !replacement.Locator.Span.SequenceEqual(locator.Span))
            {
                await PeerFrame.WriteAsync(
                    stream, new RepairVerdict(offer.Key, RepairVerdictCode.Unmatched), cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            try
            {
                // Immutability holds: what is put is byte for byte what was
                // sealed under the key, to within the locator's digest prefix.
                // A cut between the two leaves the key absent, which the next
                // push fills if the owner is still owed it.
                await replica.DeleteAsync(key, DeleteConditions.None, cancellationToken).ConfigureAwait(false);
                await replica.PutAsync(
                    key,
                    _ => new ValueTask<Stream>(File.OpenRead(staged)),
                    PutConditions.IfNotExists,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception storage) when (storage is IOException or UnauthorizedAccessException)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.StorageExhausted, $"The destination cannot store: {storage.Message}", storage);
            }

            await PeerFrame.WriteAsync(
                stream, new RepairVerdict(offer.Key, RepairVerdictCode.Replaced), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            try
            {
                File.Delete(staged);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stage that will not delete is scratch under the spool, not a replica object.
            }
        }
    }

    /// <summary>What this side's own copy says about taking a replacement, and the locator it states.</summary>
    private static async Task<(RepairVerdictCode Verdict, ReadOnlyMemory<byte> Locator)> JudgeCopyAsync(
        LocalFileSystemObjectStore replica, ObjectKey key, ulong offeredLength, CancellationToken cancellationToken)
    {
        var metadata = await replica.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (metadata.Metadata is not { } held)
        {
            return (RepairVerdictCode.NotHeld, ReadOnlyMemory<byte>.Empty);
        }

        // A copy that changed length lost the place its locator was, so
        // nothing on it names the blob a replacement would have to be.
        if ((ulong)held.Length != offeredLength)
        {
            return (RepairVerdictCode.LengthDiffers, ReadOnlyMemory<byte>.Empty);
        }

        using var read = await replica.OpenReadAsync(key, range: null, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != OpenReadOutcome.Found || read.Content is null)
        {
            return (RepairVerdictCode.NotHeld, ReadOnlyMemory<byte>.Empty);
        }

        var copy = await BlobLocatorCheck.CheckAsync(read.Content, held.Length, cancellationToken).ConfigureAwait(false);
        return copy.Passes
            ? (RepairVerdictCode.Intact, copy.Locator)
            : (RepairVerdictCode.Send, copy.Locator);
    }

    /// <summary>Stages exactly <paramref name="length"/> bytes of chunks, strictly in order (03 §3.3).</summary>
    private static async Task ReceiveAsync(Stream stream, string staged, ulong length, CancellationToken cancellationToken)
    {
        await using var spool = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        var received = 0UL;
        while (received < length)
        {
            var chunk = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.ReplicationChunk, ReplicationChunk.Read, cancellationToken)
                .ConfigureAwait(false);
            if (chunk.Offset != received || received + (ulong)chunk.Bytes.Length > length)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"A replacement chunk at {chunk.Offset} of {chunk.Bytes.Length} byte(s) does not continue "
                    + $"the {received} of {length} received.");
            }

            await spool.WriteAsync(chunk.Bytes, cancellationToken).ConfigureAwait(false);
            received += (ulong)chunk.Bytes.Length;
        }

        await spool.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

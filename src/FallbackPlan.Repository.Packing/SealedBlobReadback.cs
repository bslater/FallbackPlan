using System.Buffers.Binary;
using Bodu;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// Decides whether an object a store already holds under a blob's key is the
/// blob the writer just sealed. Specification 05 §5.1 permits exactly one
/// re-put — a byte-identical one — so a writer answered
/// <see cref="PutOutcome.AlreadyExists"/> for a freshly allocated blob must
/// establish identity before treating the upload as durable: anything else
/// means the blob identifier has been used before, the store is holding
/// another run's bytes, and an index delta published over them would commit
/// a snapshot that cannot be restored.
/// </summary>
/// <remarks>
/// Identity is established from two range reads' worth of evidence: the
/// stored length, and the footer locator's digest prefix over the sealed
/// bytes (05 §3). A same-length object whose tail is not a parseable locator,
/// or whose prefix disagrees, is not this blob.
/// </remarks>
public static class SealedBlobReadback
{
    /// <summary>
    /// Whether the object at <paramref name="key"/> is byte-identical to
    /// <paramref name="sealedBlob"/>, judged by length and locator digest
    /// prefix.
    /// </summary>
    public static async ValueTask<bool> MatchesAsync(
        IObjectStore store,
        ObjectKey key,
        SealedBlob sealedBlob,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(sealedBlob);

        var metadata = await store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (!metadata.Found || metadata.Metadata!.Length != sealedBlob.Length)
        {
            return false;
        }

        using var read = await store.OpenReadAsync(
            key,
            new ObjectRange(sealedBlob.Length - FooterLocator.Length, FooterLocator.Length),
            cancellationToken).ConfigureAwait(false);

        if (read.Outcome != OpenReadOutcome.Found)
        {
            return false;
        }

        var locatorBytes = new byte[FooterLocator.Length];
        await read.Content!.ReadExactlyAsync(locatorBytes, cancellationToken).ConfigureAwait(false);

        FooterLocator locator;
        try
        {
            locator = FooterLocator.Parse(locatorBytes, sealedBlob.Length);
        }
        catch (BlobFormatException)
        {
            return false;
        }

        var digestPrefix = new byte[8];
        for (var index = 0; index < digestPrefix.Length; index++)
        {
            digestPrefix[index] = sealedBlob.Digest[index];
        }

        return locator.DigestPrefix == BinaryPrimitives.ReadUInt64BigEndian(digestPrefix);
    }
}

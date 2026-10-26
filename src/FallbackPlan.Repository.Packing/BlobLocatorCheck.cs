using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Bodu;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// The check a holder with no key can make of a whole blob (specification
/// 05 §4): its locator's footer offset falls inside it, and the first eight
/// bytes of the SHA-256 of everything before the locator are the digest
/// prefix the locator states.
/// </summary>
/// <remarks>
/// A corruption check, never an integrity guarantee. A blob rewritten to be
/// consistent with itself passes it under a locator of its own, which is why
/// a peer judging a repair compares the replacement's locator with the one
/// its damaged copy still states
/// ([ADR-0100](../../docs/adr/0100-a-peer-replaces-what-its-own-check-finds-damaged.md)).
/// </remarks>
public static class BlobLocatorCheck
{
    /// <summary>What the check found of one blob's bytes.</summary>
    /// <param name="Passes">Whether the locator parses within the blob and its digest prefix is the bytes'.</param>
    /// <param name="Locator">The last sixteen bytes as held, whether or not they parse; empty for an object shorter than that.</param>
    public sealed record Result(bool Passes, ReadOnlyMemory<byte> Locator);

    /// <summary>Reads <paramref name="length"/> bytes of a blob and checks them.</summary>
    /// <param name="content">The blob's bytes from its start.</param>
    /// <param name="length">How long the blob is; a stream that ends sooner fails the check.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async ValueTask<Result> CheckAsync(Stream content, long length, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(content);

        if (length < FooterLocator.Length)
        {
            return new Result(Passes: false, ReadOnlyMemory<byte>.Empty);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var preimage = length - FooterLocator.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            for (var remaining = preimage; remaining > 0;)
            {
                var read = await content
                    .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return new Result(Passes: false, ReadOnlyMemory<byte>.Empty);
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var locator = new byte[FooterLocator.Length];
        try
        {
            await content.ReadExactlyAsync(locator, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return new Result(Passes: false, ReadOnlyMemory<byte>.Empty);
        }

        FooterLocator parsed;
        try
        {
            parsed = FooterLocator.Parse(locator, length);
        }
        catch (BlobFormatException)
        {
            return new Result(Passes: false, locator);
        }

        var digest = hash.GetHashAndReset();
        return new Result(parsed.DigestPrefix == BinaryPrimitives.ReadUInt64BigEndian(digest), locator);
    }
}

using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Repository;

/// <summary>
/// What a part a restore run kept holds of the file it was assembling
/// (FR-RST-008; ADR-0129): the file's pieces in its order, and how far the
/// part proves them, by one rule for the resume that takes a part up and the
/// plan that counts what it will not read again (ADR-0132).
/// </summary>
/// <remarks>
/// A segment is held where the part holds all of it and its bytes derive the
/// object identifier the manifest names; a hole where the part holds all of
/// it and it reads as zeroes. Nothing past the first piece that is not held
/// is held, because a resume takes a part up only from the file's start.
/// </remarks>
public static class RestorePart
{
    private const int ZeroesChunk = 64 * 1024;

    /// <summary>The name a run gives the part of a file: its whole-file hash.</summary>
    /// <param name="manifest">The file's manifest.</param>
    public static string NameOf(FileVersionManifest manifest)
    {
        ThrowHelper.ThrowIfNull(manifest);
        return $"{Convert.ToHexStringLower(manifest.WholeFileHash.Span)}.part";
    }

    /// <summary>
    /// Whether <paramref name="plaintext"/> is the segment
    /// <paramref name="reference"/> names: as long as it says, and deriving
    /// its object identifier under the content-ID key
    /// <paramref name="deriver"/> holds, the proof every record read from a
    /// store is held to (04 §6 step 7).
    /// </summary>
    /// <param name="deriver">The repository's object-identifier deriver.</param>
    /// <param name="reference">The segment the manifest names.</param>
    /// <param name="plaintext">The bytes held for it.</param>
    public static bool Proves(ObjectIdDeriver deriver, SegmentReference reference, ReadOnlySpan<byte> plaintext)
    {
        ThrowHelper.ThrowIfNull(deriver);
        return plaintext.Length == reference.LogicalLength
            && deriver.Derive(ObjectType.SegmentRecord, ContentHasher.Hash(plaintext)) == reference.ObjectId;
    }

    /// <summary>
    /// How many of the file's segments the part at <paramref name="path"/>
    /// holds, read and proved as a resume would take them up, writing
    /// nothing. A part that is not there, or will not open, holds none.
    /// </summary>
    /// <param name="path">Where the part would be.</param>
    /// <param name="manifest">The file's manifest.</param>
    /// <param name="proves">Whether a segment's bytes derive its object identifier.</param>
    /// <param name="cancellationToken">Cancels the reading.</param>
    public static async ValueTask<int> HeldSegmentsAsync(
        string path,
        FileVersionManifest manifest,
        Func<SegmentReference, byte[], bool> proves,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(path);
        ThrowHelper.ThrowIfNull(manifest);
        ThrowHelper.ThrowIfNull(proves);

        FileStream part;
        try
        {
            part = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        await using (part.ConfigureAwait(false))
        {
            var pieces = PiecesOf(manifest);
            var (held, _) = await WalkAsync(part, pieces, proves, taken: null, cancellationToken).ConfigureAwait(false);
            return pieces.Take(held).Count(piece => piece.Reference is not null);
        }
    }

    /// <summary>A file's pieces in its order: each segment, and each hole.</summary>
    internal static List<(ulong Offset, SegmentReference? Reference, SparseExtent? Extent)> PiecesOf(FileVersionManifest manifest) =>
        [
            .. manifest.SegmentReferences
                .Select(reference => (Offset: (ulong)reference.LogicalOffset, Reference: (SegmentReference?)reference, Extent: default(SparseExtent?)))
                .Concat(manifest.SparseExtents.Select(extent => (extent.Offset, (SegmentReference?)null, (SparseExtent?)extent)))
                .OrderBy(piece => piece.Item1),
        ];

    /// <summary>
    /// Walks <paramref name="part"/> through <paramref name="pieces"/> from
    /// the file's start, stopping at the first piece it does not hold.
    /// </summary>
    /// <param name="part">The part, open for reading.</param>
    /// <param name="pieces">The file's pieces in its order.</param>
    /// <param name="proves">Whether a segment's bytes derive its object identifier.</param>
    /// <param name="taken">
    /// Told of each piece held, in order: a segment's bytes, or null and the
    /// length of a hole.
    /// </param>
    /// <param name="cancellationToken">Cancels the reading.</param>
    /// <returns>How many pieces the part holds, and where the last of them ends.</returns>
    internal static async ValueTask<(int Pieces, long End)> WalkAsync(
        FileStream part,
        List<(ulong Offset, SegmentReference? Reference, SparseExtent? Extent)> pieces,
        Func<SegmentReference, byte[], bool> proves,
        Action<byte[]?, long>? taken,
        CancellationToken cancellationToken)
    {
        var length = part.Length;
        var held = 0;
        var end = 0L;
        foreach (var (offset, reference, extent) in pieces)
        {
            var size = reference is { } segment ? segment.LogicalLength : (long)extent!.Value.Length;
            if ((long)offset + size > length)
            {
                break;
            }

            part.Position = (long)offset;
            if (reference is { } proved)
            {
                var plaintext = new byte[proved.LogicalLength];
                await part.ReadExactlyAsync(plaintext, cancellationToken).ConfigureAwait(false);
                if (!proves(proved, plaintext))
                {
                    break;
                }

                taken?.Invoke(plaintext, size);
            }
            else
            {
                if (!await ReadsAsZeroesAsync(part, size, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                taken?.Invoke(null, size);
            }

            held++;
            end = (long)offset + size;
        }

        return (held, end);
    }

    /// <summary>Whether the next <paramref name="length"/> bytes of <paramref name="part"/> are all zero.</summary>
    private static async ValueTask<bool> ReadsAsZeroesAsync(FileStream part, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[ZeroesChunk];
        while (length > 0)
        {
            var take = (int)Math.Min(length, buffer.Length);
            await part.ReadExactlyAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            if (buffer.AsSpan(0, take).ContainsAnyExcept((byte)0))
            {
                return false;
            }

            length -= take;
        }

        return true;
    }
}

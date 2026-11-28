using System.Runtime.CompilerServices;
using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
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
    /// What restoring a file writes, and what the part at
    /// <paramref name="partPath"/> holds of it, read and proved as a resume
    /// would take it up, writing nothing: one walk of the file in its order,
    /// a reference run at a time (ADR-0202). A part that is not there, or
    /// will not open, holds none of it.
    /// </summary>
    /// <param name="manifest">The file's manifest.</param>
    /// <param name="partPath">Where the file's part would be, or null where the run kept none.</param>
    /// <param name="readRun">Reads a run's plaintext, or answers null when it cannot.</param>
    /// <param name="proves">Whether a segment's bytes derive its object identifier.</param>
    /// <param name="eachSegment">
    /// Told of each segment in the file's order, and whether the part holds
    /// it, for a caller that asks something of each; null for none.
    /// </param>
    /// <param name="cancellationToken">Cancels the reading.</param>
    /// <exception cref="ManifestValidationException">A run did not read, or does not follow the one before it.</exception>
    public static async ValueTask<RestoreMeasure> MeasureAsync(
        FileVersionManifest manifest,
        string? partPath,
        Func<ObjectId, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> readRun,
        Func<SegmentReference, byte[], bool> proves,
        Func<SegmentReference, bool, ValueTask>? eachSegment,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(manifest);
        ThrowHelper.ThrowIfNull(readRun);
        ThrowHelper.ThrowIfNull(proves);

        var part = Open(partPath);
        try
        {
            var written = 0UL;
            var heldSegments = 0;
            var heldBytes = 0UL;
            var holding = part is not null;
            await foreach (var pieces in StretchesAsync(manifest, readRun, cancellationToken).ConfigureAwait(false))
            {
                // Nothing past the first piece the part does not hold is
                // held, so a stretch is walked only while every one before it
                // was held whole.
                var held = 0;
                if (holding)
                {
                    (held, _) = await WalkAsync(part!, pieces, proves, taken: null, cancellationToken).ConfigureAwait(false);
                    holding = held == pieces.Count;
                }

                for (var index = 0; index < pieces.Count; index++)
                {
                    if (pieces[index].Reference is not { } segment)
                    {
                        continue;
                    }

                    written += (ulong)segment.LogicalLength;
                    if (index < held)
                    {
                        heldSegments++;
                        heldBytes += (ulong)segment.LogicalLength;
                    }

                    if (eachSegment is not null)
                    {
                        await eachSegment(segment, index < held).ConfigureAwait(false);
                    }
                }
            }

            return new RestoreMeasure(written, heldSegments, heldBytes);
        }
        finally
        {
            if (part is not null)
            {
                await part.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static FileStream? Open(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A file's pieces in its order: each segment, and each hole.</summary>
    internal static List<(ulong Offset, SegmentReference? Reference, SparseExtent? Extent)> PiecesOf(FileVersionManifest manifest) =>
        PiecesOf(manifest.SegmentReferences, manifest.SparseExtents);

    /// <summary>
    /// A file's pieces in its order a stretch at a time: the manifest's own,
    /// or each of its reference runs, read through <paramref name="reader"/>
    /// (ADR-0202), so that what a reader holds of a large file's pieces is one
    /// run's however many the file has.
    /// </summary>
    /// <exception cref="ManifestValidationException">A run did not read, or does not follow the one before it.</exception>
    internal static IAsyncEnumerable<List<(ulong Offset, SegmentReference? Reference, SparseExtent? Extent)>> StretchesAsync(
        FileVersionManifest manifest,
        RepositoryReader reader,
        CancellationToken cancellationToken) =>
        StretchesAsync(manifest, reader.ReadReferenceRunAsync, cancellationToken);

    private static async IAsyncEnumerable<List<(ulong Offset, SegmentReference? Reference, SparseExtent? Extent)>> StretchesAsync(
        FileVersionManifest manifest,
        Func<ObjectId, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> readRun,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (manifest.ReferenceRuns.Count == 0)
        {
            yield return PiecesOf(manifest);
            yield break;
        }

        await foreach (var run in ReferenceRunReader.ReadAsync(manifest, readRun, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return PiecesOf(run.SegmentReferences, run.SparseExtents);
        }
    }

    private static List<(ulong Offset, SegmentReference? Reference, SparseExtent? Extent)> PiecesOf(
        IReadOnlyList<SegmentReference> references, IReadOnlyList<SparseExtent> extents) =>
        [
            .. references
                .Select(reference => (Offset: (ulong)reference.LogicalOffset, Reference: (SegmentReference?)reference, Extent: default(SparseExtent?)))
                .Concat(extents.Select(extent => (extent.Offset, (SegmentReference?)null, (SparseExtent?)extent)))
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
        var buffer = new byte[Math.Min(length, ZeroesChunk)];
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

/// <summary>
/// What restoring a file writes, its segments with its holes skipped, and
/// what a part its run kept already holds of it in the file's order: how
/// many of its segments, and their bytes (ADR-0132, ADR-0136).
/// </summary>
/// <param name="WrittenBytes">The bytes the file's segments write.</param>
/// <param name="HeldSegments">How many of its segments, from its start, the part holds.</param>
/// <param name="HeldBytes">The bytes those segments write.</param>
public readonly record struct RestoreMeasure(ulong WrittenBytes, int HeldSegments, ulong HeldBytes);

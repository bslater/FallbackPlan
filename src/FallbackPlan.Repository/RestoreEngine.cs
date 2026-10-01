using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository;

/// <summary>
/// Restores a file version from its manifest (specification 06 §4, 04 §6–§7;
/// FR-RST-002, FR-RST-005): coverage validated by the codec, every segment
/// through the full 04 §6 sequence, and the whole-file hash verified over the
/// reassembly <b>before</b> a single byte reaches the caller's destination —
/// per-segment verification proves each part; the whole-file hash proves the
/// assembly.
/// </summary>
/// <remarks>
/// A sparse extent is hashed as the zeroes it reads as and never written
/// (09 §4; FR-ARCH-013). The spool keeps it as a hole, and so does a
/// destination with nothing past where the file begins, because every
/// platform reads zeroes from a range a write skipped. A destination that
/// cannot seek, or already holds bytes where the file will go, is given the
/// zeroes written out instead.
/// </remarks>
public sealed class RestoreEngine
{
    private static readonly byte[] Zeroes = new byte[64 * 1024];

    private readonly RepositoryReader _reader;
    private readonly string _spoolDirectory;

    /// <summary>Creates an engine over a loaded reader.</summary>
    /// <param name="reader">The reader segments come from.</param>
    /// <param name="spoolDirectory">
    /// Where a file's reassembly waits for its whole-file hash to verify; the
    /// system temporary directory by default.
    /// </param>
    public RestoreEngine(RepositoryReader reader, string? spoolDirectory = null)
    {
        ThrowHelper.ThrowIfNull(reader);
        _reader = reader;
        _spoolDirectory = spoolDirectory ?? Path.GetTempPath();
    }

    /// <summary>
    /// Restores <paramref name="manifest"/> into
    /// <paramref name="destination"/>, from its current position. Nothing is
    /// written unless everything — every segment and the final hash —
    /// verifies (FR-RST-005).
    /// </summary>
    public async ValueTask<RestoreResult> RestoreFileAsync(
        FileVersionManifest manifest,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(manifest);
        ThrowHelper.ThrowIfNull(destination);

        var spoolPath = Path.Combine(_spoolDirectory, $"fbp-restore-{Guid.NewGuid():n}.spool");
        var holes = manifest.SparseExtents.Count > 0;

        try
        {
            using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var pieces = manifest.SegmentReferences
                .Select(reference => (Offset: (ulong)reference.LogicalOffset, Reference: (SegmentReference?)reference, Extent: default(SparseExtent?)))
                .Concat(manifest.SparseExtents.Select(extent => (extent.Offset, (SegmentReference?)null, (SparseExtent?)extent)))
                .OrderBy(piece => piece.Item1)
                .ToList();

            // Every segment this file needs, fetched in as few reads as the
            // reader's bounds allow before the loop asks for the first of
            // them (NFR-PERF-009). What the reader then holds is bounded by
            // its own budget, not by this file.
            _ = await _reader.PrefetchAsync(
                [.. manifest.SegmentReferences.Select(reference => reference.ObjectId)],
                cancellationToken).ConfigureAwait(false);

            // The pieces cover the file end to end (the codec checked), so
            // their lengths sum to it. A file with holes is given it before
            // anything is written (see SparseFile).
            var length = pieces.Sum(piece => piece.Item2 is { } segment
                ? segment.LogicalLength
                : (long)piece.Item3!.Value.Length);
            var spool = SparseFile.Create(spoolPath, FileMode.CreateNew, holes, length);
            await using (spool.ConfigureAwait(false))
            {
                foreach (var (_, reference, extent) in pieces)
                {
                    if (reference is { } segment)
                    {
                        var read = await _reader.ReadSegmentAsync(segment.ObjectId, cancellationToken).ConfigureAwait(false);

                        if (read.Outcome != RecordReadOutcome.Ok)
                        {
                            return Refuse($"Segment at offset {segment.LogicalOffset}: {read.Outcome} — {read.Detail}");
                        }

                        if (read.Plaintext!.LongLength != segment.LogicalLength)
                        {
                            return Refuse(
                                $"Segment at offset {segment.LogicalOffset} restored {read.Plaintext.Length} bytes; the reference declares {segment.LogicalLength}.");
                        }

                        await spool.WriteAsync(read.Plaintext, cancellationToken).ConfigureAwait(false);
                        wholeFile.AppendData(read.Plaintext);
                    }
                    else if (extent is { } sparse)
                    {
                        // The hash covers the zeroes a hole reads as (06
                        // §4.2), so a filesystem without sparse support
                        // still verifies. The spool skips it.
                        AppendZeroes(wholeFile, sparse.Length);
                        spool.Seek((long)sparse.Length, SeekOrigin.Current);
                    }
                }

                // To where the last piece ended, which is where the hash
                // stopped, so the spool is exactly the bytes hashed whatever a
                // segment's declared length said. A file with holes already
                // has this length; a dense one reached it by being written.
                spool.SetLength(spool.Position);

                var hash = new byte[32];
                wholeFile.GetHashAndReset(hash);

                // 06 §4.2: verified AFTER reassembly and BEFORE emission —
                // the assembly check per-segment verification cannot make.
                if (!hash.AsSpan().SequenceEqual(manifest.WholeFileHash.Span))
                {
                    return Refuse(
                        "Every segment verified individually, but the reassembly does not hash to whole_file_hash (specification 06 §4.2; FR-RST-002).");
                }

                if (holes && destination.CanSeek && destination.Position >= destination.Length)
                {
                    await EmitLeavingHolesAsync(spool, destination, pieces, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    spool.Position = 0;
                    await spool.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }

                return new RestoreResult(true, spool.Length, hash, null);
            }
        }
        finally
        {
            if (File.Exists(spoolPath))
            {
                File.Delete(spoolPath);
            }
        }

        static RestoreResult Refuse(string detail) => new(false, 0, null, detail);
    }

    /// <summary>
    /// Gives <paramref name="destination"/> the file's length, then copies
    /// only the spool's data into it, each run where the spool holds it,
    /// counted from where the destination stands, so a hole is never written
    /// there either. The length comes first because a range is left a hole
    /// only inside a length the file already had (see <see cref="SparseFile"/>).
    /// The pieces are walked as the spool was written, so what lands is what
    /// was hashed. Called only for a destination with nothing past its
    /// position, where an unwritten range reads as zeroes on every platform.
    /// </summary>
    private static async ValueTask EmitLeavingHolesAsync(
        FileStream spool,
        Stream destination,
        List<(ulong Offset, SegmentReference? Reference, SparseExtent? Extent)> pieces,
        CancellationToken cancellationToken)
    {
        if (destination is FileStream file)
        {
            _ = SparseFile.AllowHoles(file.SafeFileHandle);
        }

        var start = destination.Position;
        destination.SetLength(start + spool.Length);
        var buffer = new byte[64 * 1024];
        long position = 0;

        foreach (var (_, reference, extent) in pieces)
        {
            if (extent is { } hole)
            {
                position += (long)hole.Length;
                continue;
            }

            spool.Position = position;
            destination.Position = start + position;

            var remaining = reference!.Value.LogicalLength;
            while (remaining > 0)
            {
                var take = (int)Math.Min(remaining, (long)buffer.Length);
                await spool.ReadExactlyAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
                remaining -= take;
                position += take;
            }
        }

        destination.Position = start + position;
    }

    private static void AppendZeroes(IncrementalHash hash, ulong length)
    {
        while (length > 0)
        {
            var take = (int)Math.Min(length, (ulong)Zeroes.Length);
            hash.AppendData(Zeroes.AsSpan(0, take));
            length -= (ulong)take;
        }
    }
}

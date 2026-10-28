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
    public ValueTask<RestoreResult> RestoreFileAsync(
        FileVersionManifest manifest,
        Stream destination,
        CancellationToken cancellationToken) =>
        RestoreAsync(manifest, destination, part: null, takeUp: false, cancellationToken);

    /// <summary>
    /// Restores <paramref name="manifest"/> as the overload without a part
    /// does, assembling it in <paramref name="part"/> (ADR-0129). The part is
    /// kept unless the file is emitted or its assembly is refused, so a run
    /// stopped in the file, or one a segment would not read for, leaves what
    /// it had assembled.
    /// </summary>
    /// <param name="manifest">The file version.</param>
    /// <param name="destination">Where the verified file is emitted.</param>
    /// <param name="part">Where the file is assembled, and kept.</param>
    /// <param name="takeUp">
    /// Whether a part already there is this run's to take up: it is, piece by
    /// piece, so far as each segment of it proves and each hole reads as
    /// zeroes, and only the rest is read from the store. Otherwise a part
    /// already there is assembled over.
    /// </param>
    /// <param name="cancellationToken">Cancels the restore, leaving the part.</param>
    public ValueTask<RestoreResult> RestoreFileAsync(
        FileVersionManifest manifest,
        Stream destination,
        string part,
        bool takeUp,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(part);
        return RestoreAsync(manifest, destination, part, takeUp, cancellationToken);
    }

    private async ValueTask<RestoreResult> RestoreAsync(
        FileVersionManifest manifest,
        Stream destination,
        string? part,
        bool takeUp,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(manifest);
        ThrowHelper.ThrowIfNull(destination);

        var spoolPath = part ?? Path.Combine(_spoolDirectory, $"fbp-restore-{Guid.NewGuid():n}.spool");
        var holes = manifest.SparseExtents.Count > 0;
        var keep = part is not null;

        try
        {
            using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var pieces = RestorePart.PiecesOf(manifest);

            var spool = SparseFile.Create(
                spoolPath, part is null ? FileMode.CreateNew : takeUp ? FileMode.OpenOrCreate : FileMode.Create, holes);
            await using (spool.ConfigureAwait(false))
            {
                var taken = takeUp ? await TakeUpAsync(spool, pieces, wholeFile, cancellationToken).ConfigureAwait(false) : 0;
                var resumed = spool.Position;

                // Every segment this file still needs, fetched in as few
                // reads as the reader's bounds allow before the loop asks for
                // the first of them (NFR-PERF-009). What the reader then holds
                // is bounded by its own budget, not by this file.
                _ = await _reader.PrefetchAsync(
                    [.. pieces.Skip(taken).Where(piece => piece.Item2 is not null).Select(piece => piece.Item2!.Value.ObjectId)],
                    cancellationToken).ConfigureAwait(false);

                foreach (var (_, reference, extent) in pieces.Skip(taken))
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

                // A file ending in a hole has nothing written past its last
                // data, so its length is set rather than reached: to where the
                // last piece ends, which is where the hash stopped.
                spool.SetLength(spool.Position);

                var hash = new byte[32];
                wholeFile.GetHashAndReset(hash);

                // 06 §4.2: verified AFTER reassembly and BEFORE emission —
                // the assembly check per-segment verification cannot make.
                if (!hash.AsSpan().SequenceEqual(manifest.WholeFileHash.Span))
                {
                    keep = false;
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

                keep = false;
                return new RestoreResult(true, spool.Length, hash, null) { Resumed = resumed };
            }
        }
        finally
        {
            if (!keep && File.Exists(spoolPath))
            {
                File.Delete(spoolPath);
            }
        }

        static RestoreResult Refuse(string detail) => new(false, 0, null, detail);
    }

    /// <summary>
    /// Takes up what a kept part already holds, in the file's order and by
    /// <see cref="RestorePart"/>'s rule, hashing what is taken as the rest
    /// will be, and cuts the part back to the end of it.
    /// </summary>
    /// <returns>How many of <paramref name="pieces"/> were taken.</returns>
    private async ValueTask<int> TakeUpAsync(
        FileStream part,
        List<(ulong Offset, SegmentReference? Reference, SparseExtent? Extent)> pieces,
        IncrementalHash wholeFile,
        CancellationToken cancellationToken)
    {
        var (taken, end) = await RestorePart.WalkAsync(
            part,
            pieces,
            (reference, plaintext) => _reader.Proves(reference, plaintext),
            (plaintext, length) =>
            {
                if (plaintext is null)
                {
                    AppendZeroes(wholeFile, (ulong)length);
                }
                else
                {
                    wholeFile.AppendData(plaintext);
                }
            },
            cancellationToken).ConfigureAwait(false);

        part.SetLength(end);
        part.Position = end;
        return taken;
    }

    /// <summary>
    /// Copies only the spool's data into <paramref name="destination"/>, each
    /// run where the spool holds it, counted from where the destination
    /// stands, and sets the length — so a hole is never written there either.
    /// The destination is left at the file's end, where a dense copy leaves
    /// it. The pieces are walked as the spool was written, so what lands is
    /// what was hashed. Called only for a destination with nothing past its
    /// position, where a skipped range reads as zeroes on every platform.
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

        destination.SetLength(start + position);
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

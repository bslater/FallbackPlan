using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository;

/// <summary>
/// Restores a file version from its manifest (specification 06 §4, 04 §6–§7;
/// FR-RST-002, FR-RST-005): coverage validated by the codec, sparse extents
/// materialised as zeroes, every segment through the full 04 §6 sequence,
/// and the whole-file hash verified over the reassembly <b>before</b> a
/// single byte reaches the caller's destination — per-segment verification
/// proves each part; the whole-file hash proves the assembly.
/// </summary>
public sealed class RestoreEngine
{
    private readonly RepositoryReader _reader;

    /// <summary>Creates an engine over a loaded reader.</summary>
    public RestoreEngine(RepositoryReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>
    /// Restores <paramref name="manifest"/> into
    /// <paramref name="destination"/>. Nothing is written unless everything
    /// — every segment and the final hash — verifies (FR-RST-005).
    /// </summary>
    public async ValueTask<RestoreResult> RestoreFileAsync(
        FileVersionManifest manifest,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(manifest);
        ThrowHelper.ThrowIfNull(destination);

        var spoolPath = Path.Combine(Path.GetTempPath(), $"fbp-restore-{Guid.NewGuid():n}.spool");

        try
        {
            using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var pieces = manifest.SegmentReferences
                .Select(reference => (Offset: (ulong)reference.LogicalOffset, Reference: (SegmentReference?)reference, Extent: default(SparseExtent?)))
                .Concat(manifest.SparseExtents.Select(extent => (extent.Offset, (SegmentReference?)null, (SparseExtent?)extent)))
                .OrderBy(piece => piece.Item1)
                .ToList();

            var spool = new FileStream(
                spoolPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
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
                        // A hole materialises as zeroes (09 §4) — and the
                        // hash covers the materialised form, so a filesystem
                        // without sparse support still verifies.
                        var zeroes = new byte[Math.Min(sparse.Length, 64UL * 1024)];
                        var remaining = sparse.Length;
                        while (remaining > 0)
                        {
                            var take = (int)Math.Min(remaining, (ulong)zeroes.Length);
                            await spool.WriteAsync(zeroes.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
                            wholeFile.AppendData(zeroes.AsSpan(0, take));
                            remaining -= (ulong)take;
                        }
                    }
                }

                var hash = new byte[32];
                wholeFile.GetHashAndReset(hash);

                // 06 §4.2: verified AFTER reassembly and BEFORE emission —
                // the assembly check per-segment verification cannot make.
                if (!hash.AsSpan().SequenceEqual(manifest.WholeFileHash.Span))
                {
                    return Refuse(
                        "Every segment verified individually, but the reassembly does not hash to whole_file_hash (specification 06 §4.2; FR-RST-002).");
                }

                spool.Position = 0;
                await spool.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

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
}

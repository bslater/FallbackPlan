using Bodu;
using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>The three verification levels (specification 05 §8, verbatim).</summary>
public enum VerifyLevel
{
    /// <summary>Two range reads: the blob is structurally intact and its record table authentic.</summary>
    LocatorAndFooter = 1,

    /// <summary>Whole blob: nothing has been altered since sealing.</summary>
    FooterAndDigest = 2,

    /// <summary>Whole blob plus decryption: every record is authentic and its content identifier truthful.</summary>
    EveryRecord = 3,
}

/// <summary>The outcome of verifying one blob.</summary>
public sealed record BlobVerifyResult(bool Ok, VerifyLevel Level, int RecordsVerified, string? Detail);

/// <summary>The outcome of verifying one file version (E4).</summary>
public sealed record FileVerifyResult(bool Ok, string? Detail);

/// <summary>
/// The verification engine (specification 05 §8; FR-RST-002, NFR-REL-004):
/// routine verification uses level 1, replication receipts level 2, a full
/// check level 3 — and file verification proves the two distinct things
/// 06 §4.2 requires: each part authentic (per-record step 7) <b>and</b>
/// assembled correctly (the whole-file hash).
/// </summary>
public sealed class VerifyEngine : IDisposable
{
    private readonly RepositoryId _repositoryId;
    private readonly RepositoryKeySet _keys;
    private readonly IObjectStore _store;
    private readonly ObjectIdDeriver _objectIdDeriver;

    /// <summary>Creates an engine.</summary>
    public VerifyEngine(RepositoryId repositoryId, RepositoryKeySet keys, IObjectStore store)
    {
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(store);

        _repositoryId = repositoryId;
        _keys = keys;
        _store = store;
        _objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);
    }

    /// <summary>Verifies one blob at the requested level.</summary>
    public async ValueTask<BlobVerifyResult> VerifyBlobAsync(
        ObjectKey storeKey,
        long blobLength,
        VerifyLevel level,
        CancellationToken cancellationToken)
    {
        // Level 1 — and the entry point for every level: locator, footer,
        // authentication, table validation, in two range reads plus the
        // envelope read.
        BlobReader reader;
        try
        {
            reader = await BlobReader.OpenAsync(
                _store, storeKey, blobLength, _repositoryId, _keys.DeriveClassKey, _objectIdDeriver, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BlobFormatException exception)
        {
            return new BlobVerifyResult(false, VerifyLevel.LocatorAndFooter, 0, exception.Message);
        }

        using (reader)
        {
            if (level == VerifyLevel.LocatorAndFooter)
            {
                return new BlobVerifyResult(true, level, 0, null);
            }

            if (level == VerifyLevel.FooterAndDigest)
            {
                // The digest preimage is [0, blobLength − 16) — the locator
                // cannot be inside its own preimage (05 §5 erratum); its
                // stored prefix is the comparison surface here, and the full
                // digest's durable home is the catalogue (Q16).
                var digest = await HashRangeAsync(storeKey, blobLength - FooterLocator.Length, cancellationToken)
                    .ConfigureAwait(false);

                var locatorBytes = new byte[FooterLocator.Length];
                using (var read = await _store.OpenReadAsync(
                    storeKey, new ObjectRange(blobLength - FooterLocator.Length, FooterLocator.Length), cancellationToken)
                    .ConfigureAwait(false))
                {
                    await read.Content!.ReadExactlyAsync(locatorBytes, cancellationToken).ConfigureAwait(false);
                }

                var locator = FooterLocator.Parse(locatorBytes, blobLength);
                var prefix = BinaryPrimitives.ReadUInt64BigEndian(digest);

                return prefix == locator.DigestPrefix
                    ? new BlobVerifyResult(true, level, 0, null)
                    : new BlobVerifyResult(false, level, 0,
                        "The blob's bytes do not hash to the sealed digest — altered since sealing (specification 05 §8).");
            }

            // Level 3: every record, 04 §6 in full, step 7 included.
            var verified = 0;
            foreach (var entry in reader.RecordTable)
            {
                var result = await reader.ReadRecordAsync(entry, cancellationToken).ConfigureAwait(false);

                if (result.Outcome != RecordReadOutcome.Ok)
                {
                    return new BlobVerifyResult(false, level, verified,
                        $"Record {entry.ObjectId} at offset {entry.PhysicalOffset}: {result.Outcome} — {result.Detail}");
                }

                verified++;
            }

            return new BlobVerifyResult(true, level, verified, null);
        }
    }

    /// <summary>
    /// Verifies one file version end to end (E4; 06 §4.2; FR-RST-002): every
    /// segment authentic and truthfully identified, then the whole-file hash
    /// over the reassembled plaintext — sparse extents materialised as
    /// zeroes. The two checks are different and both required: per-segment
    /// verification cannot catch a wrong assembly of individually valid
    /// parts.
    /// </summary>
    public async ValueTask<FileVerifyResult> VerifyFileAsync(
        FileVersionManifest manifest,
        RepositoryReader reader,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(manifest);
        ThrowHelper.ThrowIfNull(reader);

        using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var pieces = manifest.SegmentReferences
            .Select(reference => (Offset: (ulong)reference.LogicalOffset, Reference: (SegmentReference?)reference, Extent: default(SparseExtent?)))
            .Concat(manifest.SparseExtents.Select(extent => (extent.Offset, (SegmentReference?)null, (SparseExtent?)extent)))
            .OrderBy(piece => piece.Item1);

        foreach (var (_, reference, extent) in pieces)
        {
            if (reference is { } segment)
            {
                var read = await reader.ReadSegmentAsync(segment.ObjectId, cancellationToken).ConfigureAwait(false);

                if (read.Outcome != RecordReadOutcome.Ok)
                {
                    return new FileVerifyResult(false,
                        $"Segment at offset {segment.LogicalOffset}: {read.Outcome} — {read.Detail}");
                }

                wholeFile.AppendData(read.Plaintext!);
            }
            else if (extent is { } sparse)
            {
                var zeroes = new byte[Math.Min(sparse.Length, 64 * 1024)];
                var remaining = sparse.Length;
                while (remaining > 0)
                {
                    var take = (int)Math.Min(remaining, (ulong)zeroes.Length);
                    wholeFile.AppendData(zeroes.AsSpan(0, take));
                    remaining -= (ulong)take;
                }
            }
        }

        Span<byte> hash = stackalloc byte[32];
        wholeFile.GetHashAndReset(hash);

        return hash.SequenceEqual(manifest.WholeFileHash.Span)
            ? new FileVerifyResult(true, null)
            : new FileVerifyResult(false,
                "Every segment verified individually, but the reassembly does not hash to whole_file_hash — wrong order, gap, or duplication (specification 06 §4.2).");
    }

    private async ValueTask<byte[]> HashRangeAsync(ObjectKey key, long length, CancellationToken cancellationToken)
    {
        using var read = await _store.OpenReadAsync(key, new ObjectRange(0, length), cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[64 * 1024];
        var remaining = length;

        while (remaining > 0)
        {
            var take = (int)Math.Min(remaining, buffer.Length);
            await read.Content!.ReadExactlyAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer.AsSpan(0, take));
            remaining -= take;
        }

        var digest = new byte[32];
        hash.GetHashAndReset(digest);
        return digest;
    }

    /// <inheritdoc />
    public void Dispose() => _objectIdDeriver.Dispose();
}

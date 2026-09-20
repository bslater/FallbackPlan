using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Resources;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Repository;

/// <summary>
/// One blob a compaction pass would drain, with the records worth carrying
/// out of it. The caller decides which blobs these are — the collector's
/// plan knows what is reachable and this does not.
/// </summary>
/// <param name="StoreKey">Where the blob lives in the source store.</param>
/// <param name="BlobId">Its writer-allocated identity.</param>
/// <param name="Live">The records to carry, as the source blob's footer names them.</param>
public sealed record CompactionSource(
    ObjectKey StoreKey, BlobId BlobId, IReadOnlyList<RecordTableEntry> Live);

/// <summary>
/// One blob a compaction pass produced: the sealed bytes, the index entries
/// that move its records' readers onto it, and the sources it drained.
/// </summary>
/// <remarks>
/// The blob is <b>sealed and not uploaded</b>. No blob may be uploaded before
/// a durable intent names it (specification 08 §3.1), and a direct-ship set's
/// blobs must go through the ship sink — both of which are the caller's, not
/// the compactor's. <see cref="SealedBlob.BlobCounter"/> is carried here for
/// the same reason: a counter allocated and never marked accounted has the
/// next run publish a void delta claiming a number a durable blob embeds was
/// skipped (<see cref="IBlobCounterAllocator.MarkAccounted"/>).
/// </remarks>
/// <param name="Sealed">The produced blob, ready to upload.</param>
/// <param name="Superseding">One supersession entry per record it carries (07 §2.1, ADR-0017).</param>
/// <param name="Drained">The source blobs whose live records it now holds.</param>
public sealed record CompactedBlob(
    SealedBlob Sealed, IReadOnlyList<IndexEntry> Superseding, IReadOnlyList<BlobId> Drained);

/// <summary>
/// Rewrites partly dead blobs into dense ones without opening a record
/// ([ADR-0067](../../docs/adr/0067-the-keyless-compactor.md)).
/// </summary>
/// <remarks>
/// <para>
/// This is what format 3 was for. A record's key is derived from its object
/// and its nonce rides its own prefix, so its sealed bytes authenticate in
/// one blob exactly as they did in another: they are copied verbatim and only
/// the 54-byte header is re-framed, with the ordinal the destination gives
/// them ([ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md) §4).
/// At format 2 none of that holds and compaction is decrypt-and-reseal, which
/// a write-only service cannot do at all — it holds the structure key and not
/// the content key (FR-WOR-003). So this type constructs no content key, is
/// handed none, and could not use one.
/// </para>
/// <para>
/// It produces blobs and nothing else: no upload, no index publication, no
/// tombstone. Those are ordered steps with an interruption discipline of
/// their own, and putting them here would bury the order that makes an
/// interrupted compaction safe.
/// </para>
/// </remarks>
public sealed class BlobCompactor : IDisposable
{
    private readonly RepositoryId _repositoryId;
    private readonly WriterId _writerId;
    private readonly KeyGeneration _generation;
    private readonly byte[] _structureKey;
    private readonly byte[] _sealingPublicKey;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly CapturePolicy _policy;
    private readonly IObjectStore _source;
    private readonly IBlobCounterAllocator _counters;
    private readonly string _spoolDirectory;
    private readonly ushort _containerVersion;
    private readonly ILogger? _logger;

    /// <summary>Creates a compactor over one repository's blobs.</summary>
    /// <param name="repositoryId">The repository whose blobs these are.</param>
    /// <param name="writerId">This device's writer identity — the produced blobs are its.</param>
    /// <param name="generation">The key generation to write under.</param>
    /// <param name="keys">The repository's key set; only the structure plane is touched.</param>
    /// <param name="policy">Sizing and profile for the blobs produced.</param>
    /// <param name="source">Where the candidates are read from — a staging archive, or a destination through the sink.</param>
    /// <param name="counters">The blob-counter allocator; the caller marks each counter accounted once its blob is durable.</param>
    /// <param name="spoolDirectory">Where produced blobs spool before they are sealed.</param>
    /// <param name="repositoryFormatVersion">The repository's effective format version (ADR-0066).</param>
    /// <param name="logger">Where the rewrite reports what it moved.</param>
    /// <exception cref="ArgumentOutOfRangeException">The repository is below format 3, where records do not relocate.</exception>
    public BlobCompactor(
        RepositoryId repositoryId,
        WriterId writerId,
        KeyGeneration generation,
        RepositoryKeySet keys,
        CapturePolicy policy,
        IObjectStore source,
        IBlobCounterAllocator counters,
        string spoolDirectory,
        ushort repositoryFormatVersion,
        ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(policy);
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(counters);
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolDirectory);

        if (!FormatVersions.HasRelocatableRecords(repositoryFormatVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(repositoryFormatVersion),
                repositoryFormatVersion,
                Strings.BlobCompactor_CompactionNeedsFormatThree);
        }

        _repositoryId = repositoryId;
        _writerId = writerId;
        _generation = generation;

        // The structure plane's key, exactly as an archive session takes it:
        // a data blob's footer derives from the METADATA class key while its
        // records are sealed to the repository's public key (ADR-0042 §2).
        // Nothing here asks for a content key, because nothing here opens a
        // record.
        _structureKey = keys.DeriveClassKey(BlobClass.Metadata, generation);
        _sealingPublicKey = keys.SealingPublicKey.ToArray();
        _objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);

        _policy = policy;
        _source = source;
        _counters = counters;
        _spoolDirectory = spoolDirectory;
        _containerVersion = FormatVersions.ContainerVersion(repositoryFormatVersion, dataClass: true);
        _logger = logger;
    }

    /// <summary>
    /// Packs the live records of <paramref name="candidates"/> into as few
    /// blobs as the write profile allows, in the order given.
    /// </summary>
    /// <param name="candidates">The blobs to drain, with the records to carry out of each.</param>
    /// <param name="cancellationToken">Cancels the rewrite.</param>
    /// <returns>The blobs produced, each naming what it carries and what it drained.</returns>
    /// <exception cref="BlobFormatException">A source blob or one of its records is damaged; nothing is relocated from it.</exception>
    public async ValueTask<IReadOnlyList<CompactedBlob>> CompactAsync(
        IReadOnlyList<CompactionSource> candidates, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(candidates);

        var produced = new List<CompactedBlob>();
        BlobWriter? writer = null;
        var drained = new List<BlobId>();

        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Live.Count == 0)
                {
                    continue;
                }

                using var reader = await OpenSourceAsync(candidate, cancellationToken).ConfigureAwait(false);

                foreach (var entry in candidate.Live)
                {
                    var read = await reader.ReadSealedRecordAsync(entry, cancellationToken).ConfigureAwait(false);
                    if (read.Sealed is not { } sealedRecord)
                    {
                        // A source whose header disagrees with its own footer
                        // is damage, and a compactor that carried it anyway
                        // would launder the damage into a blob nothing
                        // suspects. Refused whole rather than partly.
                        throw new BlobFormatException(Strings.FormatBlobCompactor_SourceRecordRefused(
                            candidate.StoreKey, entry.ObjectId, read.Detail ?? read.Outcome.ToString()));
                    }

                    if (writer is not null && !writer.CanAppend((int)entry.StoredLength))
                    {
                        produced.Add(await SealAsync(writer, drained, cancellationToken).ConfigureAwait(false));
                        writer = null;
                        drained = [];
                    }

                    writer ??= CreateWriter();
                    await writer.AppendSealedRecordAsync(entry, sealedRecord, cancellationToken).ConfigureAwait(false);
                }

                if (!drained.Contains(candidate.BlobId))
                {
                    drained.Add(candidate.BlobId);
                }
            }

            if (writer is not null)
            {
                produced.Add(await SealAsync(writer, drained, cancellationToken).ConfigureAwait(false));
                writer = null;
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }

        return produced;
    }

    private async ValueTask<BlobReader> OpenSourceAsync(
        CompactionSource candidate, CancellationToken cancellationToken)
    {
        var metadata = await _source.GetMetadataAsync(candidate.StoreKey, cancellationToken).ConfigureAwait(false);
        if (metadata.Metadata is not { } found)
        {
            throw new BlobFormatException(Strings.FormatBlobCompactor_SourceBlobMissing(candidate.StoreKey));
        }

        // sealedContentKeyOpener stays null: structure only, which is all a
        // relocation needs and all this type is entitled to.
        return await BlobReader.OpenAsync(
            _source,
            candidate.StoreKey,
            found.Length,
            _repositoryId,
            (_, _) => _structureKey,
            _objectIdDeriver,
            cancellationToken,
            sealedContentKeyOpener: null,
            logger: _logger).ConfigureAwait(false);
    }

    private BlobWriter CreateWriter() => BlobWriter.CreateSealed(
        _repositoryId,
        _writerId,
        _generation,
        _structureKey,
        _sealingPublicKey,
        _counters.AllocateNext(),
        _policy.EncryptionProfile,
        _policy.BlobWriteProfile,
        _spoolDirectory,
        logger: _logger,
        formatVersion: _containerVersion);

    /// <summary>Releases the content-identifier deriver this compactor owns.</summary>
    public void Dispose() => _objectIdDeriver.Dispose();

    private static async ValueTask<CompactedBlob> SealAsync(
        BlobWriter writer, IReadOnlyList<BlobId> drained, CancellationToken cancellationToken)
    {
        var sealedBlob = await writer.SealAsync(cancellationToken).ConfigureAwait(false);

        // Supersessions, not insertions: these locations must be honoured in
        // order against the ones the source blobs still carry, and an
        // insertion would leave the winner to a tie-break rather than to the
        // decision this pass just made (07 §3, FR-MAN-015).
        var entries = sealedBlob.RecordTable
            .Select(record => new IndexEntry(
                record.ObjectId,
                sealedBlob.BlobId,
                record.PhysicalOffset,
                record.StoredLength,
                record.CompressionProfileValue,
                record.EncryptionProfileValue,
                IndexEntryType.Supersession))
            .ToList();

        return new CompactedBlob(sealedBlob, entries, [.. drained]);
    }
}

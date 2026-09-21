using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Compression;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FallbackPlan.Repository.Packing.Resources;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// Reads a sealed blob through its recovery footer alone (specification
/// 05 §4, 04 §6; FR-ARCH-006, FR-MAN-007): the locator and footer are
/// reached in exactly two range reads, one further small read fetches the
/// envelope's key-derivation selectors, and from there every record is
/// locatable and verifiable with no index and no catalogue.
/// </summary>
/// <remarks>
/// Every record read ends with 04 §6 step 7: the decrypted, decompressed
/// plaintext is re-hashed and its implied object identifier compared — a
/// reader that skips that step restores corrupt data and reports success.
/// Corruption is local: each record resolves to its own
/// <see cref="RecordReadResult"/>.
/// <para>
/// One reader may be read from several threads at once, which is the whole
/// point of caching it: the objects a publication wants cluster into a few
/// blobs. Everything a record read touches is either immutable or local to
/// the call, with one exception — the zstd context, which is a stateful
/// native decoder and is guarded below.
/// </para>
/// </remarks>
/// <summary>
/// Where a record sits and how long it is — the part of a
/// <see cref="RecordTableEntry"/> a location cache can answer, without the
/// ordinal, the logical length or the object type that only the
/// authenticated footer states.
/// </summary>
/// <remarks>
/// Those three are exactly the fields the record's own protection already
/// covers: the object type and (before format 3) the ordinal are AAD
/// inputs, so a header that lies about either fails its tag; the logical
/// length is what 04 §6 step 7's plaintext re-hash checks. So a read from a
/// span is not a read with three checks missing — it is a read whose three
/// remaining checks moved from the footer to the cipher.
/// </remarks>
public readonly record struct RecordSpan(
    ObjectId ObjectId,
    ulong PhysicalOffset,
    uint StoredLength,
    ushort CompressionProfileValue,
    ushort EncryptionProfileValue)
{
    /// <summary>The span a footer's table entry describes.</summary>
    public static RecordSpan From(RecordTableEntry entry) => new(
        entry.ObjectId,
        entry.PhysicalOffset,
        entry.StoredLength,
        entry.CompressionProfileValue,
        entry.EncryptionProfileValue);
}

public sealed class BlobReader : IDisposable
{
    private readonly IObjectStore _store;
    private readonly ObjectKey _key;
    private readonly long _blobLength;
    private readonly RepositoryId _repositoryId;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly byte[] _blobKey;

    // How this blob's records are keyed, which its envelope decides and its
    // caller does not. Format 1 and 2: one key for every record — the blob
    // key, or the content key a grant opened — in _recordKey. Format 3: a key
    // per record, expanded from _classKey on the structure plane, or opened
    // one share at a time through _opener in the sealed data plane. Exactly
    // one of the three is non-null; all three null means the records are
    // sealed to an authority this reader was not given.
    private readonly byte[]? _recordKey;
    private readonly byte[]? _classKey;
    private readonly SealedContentKeyOpener? _opener;
    private readonly int _recordPrefixLength;
    private readonly ZstdSegmentDecompressor _decompressor = new();
    private readonly Lock _decompressorGate = new();

    private BlobReader(
        IObjectStore store,
        ObjectKey key,
        long blobLength,
        RepositoryId repositoryId,
        ObjectIdDeriver objectIdDeriver,
        BlobEnvelope envelope,
        byte[] blobKey,
        byte[]? recordKey,
        byte[]? classKey,
        SealedContentKeyOpener? opener,
        IReadOnlyList<RecordTableEntry> recordTable)
    {
        _store = store;
        _key = key;
        _blobLength = blobLength;
        _repositoryId = repositoryId;
        _objectIdDeriver = objectIdDeriver;
        Envelope = envelope;
        _blobKey = blobKey;
        _recordKey = recordKey;
        _classKey = classKey;
        _opener = opener;
        _recordPrefixLength = RecordFraming.PrefixLength(envelope.FormatVersion, envelope.BlobClass);
        RecordTable = recordTable;
    }

    /// <summary>
    /// The keys an envelope implies — shared by the full open and the
    /// framing-only one, so a blob opened either way is keyed identically.
    /// </summary>
    /// <remarks>
    /// Three ways a record is keyed, and the envelope picks one. A format-3
    /// blob keys per record, so nothing is opened here: a structure-plane
    /// record expands the class key (kept for the reads), and a data
    /// record's share is opened one at a time, where a refusal costs that
    /// record and not the blob.
    /// </remarks>
    private static (byte[] BlobKey, byte[]? RecordKey, byte[]? ClassKey, SealedContentKeyOpener? Opener) DeriveKeys(
        BlobEnvelope envelope,
        Func<BlobClass, KeyGeneration, byte[]> classKeyProvider,
        SealedContentKeyOpener? sealedContentKeyOpener,
        ILogger log)
    {
        // A sealed data blob's STRUCTURE lives on the metadata plane
        // (ADR-0042 §2) in both formats that have one: its footer key derives
        // from the metadata class key, and only its records need a content
        // key. Asked by version and class rather than by "at least version
        // 2", because format 3 seals per record and would answer the second
        // question wrongly.
        var isData = envelope.BlobClass == BlobClass.Data;
        var sealedPerBlob = FormatVersions.SealsContentPerBlob(envelope.FormatVersion, isData);
        var sealedPerRecord = FormatVersions.SealsContentPerRecord(envelope.FormatVersion, isData);
        var structureClass = sealedPerBlob || sealedPerRecord ? BlobClass.Metadata : envelope.BlobClass;

        var classKey = classKeyProvider(structureClass, envelope.KeyGeneration);
        var blobKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, envelope.BlobSalt, envelope.WriterId, envelope.BlobCounter, blobKey);

        byte[]? recordKey = null;
        byte[]? retainedClassKey = null;
        SealedContentKeyOpener? recordOpener = null;

        if (FormatVersions.HasRelocatableRecords(envelope.FormatVersion))
        {
            if (sealedPerRecord)
            {
                recordOpener = sealedContentKeyOpener;
            }
            else
            {
                retainedClassKey = classKey.AsSpan().ToArray();
            }
        }
        else if (!sealedPerBlob)
        {
            recordKey = blobKey;
        }
        else if (sealedContentKeyOpener is not null)
        {
            try
            {
                recordKey = sealedContentKeyOpener.OpenBlobKey(envelope);
            }
            catch (Exception refusal) when (refusal is SealedContentException or ArgumentException)
            {
                // Every call site proves the authority against the descriptor
                // before constructing an opener, so a share that still does
                // not open is THIS blob's damage — tampered, transplanted, or
                // a low-order ephemeral. Contained to the blob exactly as a
                // failed footer is (ADR-0042 §7): one hostile object must not
                // abort loading every other blob.
                CryptographicOperations.ZeroMemory(blobKey);
                Log.SealedShareRefused(log, envelope.BlobId);
                throw new BlobFormatException(Strings.BlobReader_SealedShareDoesNotOpen);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(blobKey);
                throw;
            }
        }

        return (blobKey, recordKey, retainedClassKey, recordOpener);
    }

    /// <summary>
    /// Opens a blob's <b>framing</b> alone: one ranged read of the envelope,
    /// no locator and no footer, so <see cref="RecordTable"/> is empty and
    /// only <see cref="ReadRecordAsync(RecordSpan, CancellationToken)"/> can
    /// be used. For a caller that already knows where its records are
    /// (NFR-PERF-009), this is the whole per-blob cost: one read instead of
    /// three, and nothing scattered across the object.
    /// </summary>
    /// <remarks>
    /// A format-3 blob needs the envelope only for its format version and
    /// key generation; a format-2 blob needs its salt, writer and counter
    /// too, because the per-blob key derives from them
    /// (<see cref="BlobKeyDeriver"/>). Either way it is the same 88 or 168
    /// bytes at offset 0, and the same fact for every record in the blob.
    /// </remarks>
    public static async ValueTask<BlobReader> OpenFramingAsync(
        IObjectStore store,
        ObjectKey key,
        long blobLength,
        RepositoryId repositoryId,
        Func<BlobClass, KeyGeneration, byte[]> classKeyProvider,
        ObjectIdDeriver objectIdDeriver,
        CancellationToken cancellationToken,
        SealedContentKeyOpener? sealedContentKeyOpener = null,
        ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(classKeyProvider);
        ThrowHelper.ThrowIfNull(objectIdDeriver);

        if (blobLength < BlobEnvelope.Length + BlobFooter.HeaderLength + RecordCipher.TagLength + FooterLocator.Length)
        {
            throw new BlobFormatException(Strings.FormatBlobReader_ByteObjectTooShortSealed(blobLength));
        }

        var envelopeLength = Math.Min(BlobEnvelope.MaxLength, blobLength - FooterLocator.Length);
        var envelopeBytes = await ReadRangeAsync(store, key, 0, envelopeLength, cancellationToken).ConfigureAwait(false);
        var envelope = BlobEnvelope.Parse(envelopeBytes);

        var (blobKey, recordKey, retainedClassKey, recordOpener) =
            DeriveKeys(envelope, classKeyProvider, sealedContentKeyOpener, logger ?? NullLogger.Instance);

        return new BlobReader(
            store, key, blobLength, repositoryId, objectIdDeriver, envelope, blobKey, recordKey, retainedClassKey,
            recordOpener, []);
    }

    /// <summary>The store key this blob was opened from.</summary>
    public ObjectKey StoreKey => _key;

    /// <summary>The blob's cleartext envelope.</summary>
    public BlobEnvelope Envelope { get; }

    /// <summary>The authenticated record table from the recovery footer.</summary>
    public IReadOnlyList<RecordTableEntry> RecordTable { get; }

    /// <summary>
    /// Opens a blob: locator (range read one), footer (range read two),
    /// envelope (one further small read), then footer authentication and
    /// record-table validation.
    /// </summary>
    /// <param name="store">The object store.</param>
    /// <param name="key">The blob's store key.</param>
    /// <param name="blobLength">The object's length, from the listing or metadata.</param>
    /// <param name="repositoryId">The repository identity the footer must authenticate against.</param>
    /// <param name="classKeyProvider">
    /// Supplies the data or metadata key for a generation; the caller owns
    /// the returned buffer. A sealed v2 data blob's structure derives from
    /// the METADATA class key (ADR-0042 §2), and the provider is asked for
    /// exactly that — a write-only holder never needs a data key.
    /// </param>
    /// <param name="objectIdDeriver">The caller-owned deriver used for content verification.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <param name="sealedContentKeyOpener">
    /// A restore grant's capability to open sealed content keys: one per blob
    /// from a format-2 envelope, one per record from a format-3 record's
    /// prefix. Null means structure-only — the record table opens and record
    /// reads answer <see cref="RecordReadOutcome.ContentSealed"/>.
    /// </param>
    /// <param name="logger">Where the open and any contained sealed-share refusal are recorded.</param>
    /// <exception cref="BlobFormatException">The blob is damaged — every refusal names its finding.</exception>
    public static async ValueTask<BlobReader> OpenAsync(
        IObjectStore store,
        ObjectKey key,
        long blobLength,
        RepositoryId repositoryId,
        Func<BlobClass, KeyGeneration, byte[]> classKeyProvider,
        ObjectIdDeriver objectIdDeriver,
        CancellationToken cancellationToken,
        SealedContentKeyOpener? sealedContentKeyOpener = null,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;

        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(classKeyProvider);
        ThrowHelper.ThrowIfNull(objectIdDeriver);

        if (blobLength < BlobEnvelope.Length + BlobFooter.HeaderLength + RecordCipher.TagLength + FooterLocator.Length)
        {
            throw new BlobFormatException(Strings.FormatBlobReader_ByteObjectTooShortSealed(blobLength));
        }

        // Range read one: the locator — the last 16 bytes.
        var locatorBytes = await ReadRangeAsync(
            store, key, blobLength - FooterLocator.Length, FooterLocator.Length, cancellationToken).ConfigureAwait(false);
        var locator = FooterLocator.Parse(locatorBytes, blobLength);

        // Range read two: the footer — everything between its offset and the
        // locator. The footer is now in hand: two range reads, as specified.
        var footerLength = blobLength - FooterLocator.Length - (long)locator.FooterOffset;
        var footerBytes = await ReadRangeAsync(
            store, key, (long)locator.FooterOffset, footerLength, cancellationToken).ConfigureAwait(false);

        var (recordCount, cborLength) = BlobFooter.ParseHeader(footerBytes);

        if (footerLength != BlobFooter.HeaderLength + cborLength + RecordCipher.TagLength)
        {
            throw new BlobFormatException(Strings.BlobReader_FooterSDeclaredTableLength);
        }

        // One further small read: the envelope's key-derivation selectors.
        // The largest envelope is read unconditionally — the version that
        // decides its shape sits inside it.
        var envelopeLength = Math.Min(BlobEnvelope.MaxLength, blobLength - FooterLocator.Length);
        var envelopeBytes = await ReadRangeAsync(store, key, 0, envelopeLength, cancellationToken).ConfigureAwait(false);
        var envelope = BlobEnvelope.Parse(envelopeBytes);

        // A sealed data blob's STRUCTURE lives on the metadata plane
        // (ADR-0042 §2) in both formats that have one: its footer key derives
        // from the metadata class key, and only its records need a content
        // key. Asked by version and class rather than by "at least version
        // 2", because format 3 seals per record and would answer the second
        // question wrongly.
        var isData = envelope.BlobClass == BlobClass.Data;
        var sealedPerBlob = FormatVersions.SealsContentPerBlob(envelope.FormatVersion, isData);
        var sealedPerRecord = FormatVersions.SealsContentPerRecord(envelope.FormatVersion, isData);
        var structureClass = sealedPerBlob || sealedPerRecord ? BlobClass.Metadata : envelope.BlobClass;

        var classKey = classKeyProvider(structureClass, envelope.KeyGeneration);
        var blobKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, envelope.BlobSalt, envelope.WriterId, envelope.BlobCounter, blobKey);

        Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
        RecordNonce.WriteFooterNonce(nonce);
        Span<byte> aad = stackalloc byte[FooterAad.Length];
        FooterAad.Write(repositoryId, envelope.FormatVersion, envelope.BlobId, recordCount, aad);

        var table = new byte[cborLength];
        var authenticated = RecordCipher.TryOpen(
            blobKey,
            nonce,
            aad,
            footerBytes.AsSpan(BlobFooter.HeaderLength, (int)cborLength),
            footerBytes.AsSpan(BlobFooter.HeaderLength + (int)cborLength, RecordCipher.TagLength),
            table);

        if (!authenticated)
        {
            CryptographicOperations.ZeroMemory(blobKey);
            throw new BlobFormatException(Strings.BlobReader_RecoveryFooterFailedAuthentication);
        }

        var entries = BlobFooter.DecodeRecordTable(
            table, recordCount, blobLength, RecordFraming.PrefixLength(envelope.FormatVersion, envelope.BlobClass));

        var (_, recordKey, retainedClassKey, recordOpener) =
            DeriveKeys(envelope, classKeyProvider, sealedContentKeyOpener, log);

        Log.BlobOpened(log, envelope.BlobId, entries.Count);

        return new BlobReader(
            store, key, blobLength, repositoryId, objectIdDeriver, envelope, blobKey, recordKey, retainedClassKey,
            recordOpener, entries);
    }

    /// <summary>
    /// Reads one record's sealed bytes — prefix, ciphertext and tag, exactly
    /// as this blob holds them — without opening it, for
    /// <c>BlobWriter.AppendSealedRecordAsync</c> to copy verbatim into
    /// another blob (ADR-0052 §4, ADR-0067).
    /// </summary>
    /// <remarks>
    /// There is no key path here at all, which is the point: a format-3
    /// record's key is its object's and its nonce rides its own prefix, so
    /// relocation needs nothing this reader would have to hold. The
    /// header-against-table cross-check still runs, because a compactor that
    /// skipped it would relocate corruption faithfully.
    /// </remarks>
    /// <param name="entry">The record's footer-table entry.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The sealed bytes, or the finding that stopped the read.</returns>
    public async ValueTask<SealedRecordReadResult> ReadSealedRecordAsync(
        RecordTableEntry entry, CancellationToken cancellationToken)
    {
        var framed = await ReadFramedAsync(entry, cancellationToken).ConfigureAwait(false);
        return framed.Bytes is { } bytes
            ? SealedRecordReadResult.Success(bytes[RecordHeader.Length..])
            : SealedRecordReadResult.Failure(framed.Outcome, framed.Detail!);
    }

    /// <summary>
    /// The read every record read begins with: the profile and length
    /// guards, one ranged read of the whole framed record, and the
    /// header-against-table cross-check of 05 §3.1. Shared so that opening a
    /// record and relocating one cannot disagree about what a well-formed
    /// record is.
    /// </summary>
    private async ValueTask<FramedRecord> ReadFramedAsync(
        RecordTableEntry entry, CancellationToken cancellationToken)
    {
        var framed = await ReadFramedAsync(
            RecordSpan.From(entry), entry.LogicalLength, cancellationToken).ConfigureAwait(false);
        if (framed.Header is not { } header)
        {
            return framed;
        }

        // The three a span cannot state, checked here because a footer did.
        if (header.Ordinal != entry.Ordinal ||
            header.LogicalLength != entry.LogicalLength ||
            header.ObjectType != entry.ObjectType)
        {
            return new FramedRecord(
                null,
                null,
                RecordReadOutcome.FormatViolation,
                $"The record header at offset {entry.PhysicalOffset} disagrees with the footer's table entry — a damage finding (specification 05 §3.1).");
        }

        return framed;
    }

    /// <summary>
    /// The same read from a <see cref="RecordSpan"/>: a caller that knows
    /// where a record is but holds no footer. The four fields a span states
    /// are cross-checked against the header exactly as the footer's are; the
    /// other three are left to the cipher and to step 7, which is what
    /// <see cref="RecordSpan"/>'s own remarks explain.
    /// </summary>
    private async ValueTask<FramedRecord> ReadFramedAsync(
        RecordSpan entry, ulong logicalLengthGuard, CancellationToken cancellationToken)
    {
        if (!EncryptionProfile.TryFromValue(entry.EncryptionProfileValue, out var encryptionProfile) ||
            encryptionProfile != EncryptionProfile.Aes256GcmV1)
        {
            return new FramedRecord(
                null,
                null,
                RecordReadOutcome.UnsupportedProfile,
                $"Encryption profile 0x{entry.EncryptionProfileValue:x4} is not supported by this reader; refused, not guessed (specification 00 §3).");
        }

        if (logicalLengthGuard > (ulong)FormatLimits.MaxRecordStoredLength)
        {
            return new FramedRecord(
                null,
                null,
                RecordReadOutcome.FormatViolation,
                $"logical_length {logicalLengthGuard} exceeds the 64 MiB segment bound (specification 00 §8) — refused before allocation.");
        }

        var recordLength = RecordHeader.Length + _recordPrefixLength + entry.StoredLength + RecordCipher.TagLength;
        var recordBytes = await ReadRangeAsync(
            _store, _key, (long)entry.PhysicalOffset, recordLength, cancellationToken).ConfigureAwait(false);

        RecordHeader header;
        try
        {
            header = RecordHeader.Parse(recordBytes.AsSpan(0, RecordHeader.Length));
        }
        catch (RecordFormatException exception)
        {
            return new FramedRecord(null, null, RecordReadOutcome.FormatViolation, exception.Message);
        }

        if (header.ObjectId != entry.ObjectId ||
            header.StoredLength != entry.StoredLength ||
            header.CompressionProfile.Value != entry.CompressionProfileValue ||
            header.EncryptionProfile.Value != entry.EncryptionProfileValue)
        {
            return new FramedRecord(
                null,
                null,
                RecordReadOutcome.FormatViolation,
                $"The record header at offset {entry.PhysicalOffset} disagrees with the table entry that named it — a damage finding (specification 05 §3.1).");
        }

        return new FramedRecord(recordBytes, header, RecordReadOutcome.Ok, null);
    }

    private readonly record struct FramedRecord(
        byte[]? Bytes, RecordHeader? Header, RecordReadOutcome Outcome, string? Detail);

    /// <summary>
    /// Reads, authenticates, decompresses, and content-verifies one record —
    /// the 04 §6 sequence in order, step 7 included.
    /// </summary>
    public ValueTask<RecordReadResult> ReadRecordAsync(RecordTableEntry entry, CancellationToken cancellationToken) =>
        ReadRecordCoreAsync(
            () => ReadFramedAsync(entry, cancellationToken), cancellationToken);

    /// <summary>
    /// Reads one record from a <see cref="RecordSpan"/> — a caller that knows
    /// where the record is and holds no footer, which is what makes a read
    /// possible after <see cref="OpenFramingAsync"/> (NFR-PERF-009).
    /// </summary>
    /// <remarks>
    /// Identical to the footer's overload from the cipher onwards: the same
    /// nonce and AAD construction, the same decompression, and the same
    /// 04 §6 step 7 content verification. What differs is only which of the
    /// header's fields were cross-checked before it, and
    /// <see cref="RecordSpan"/> says why the remainder is safe.
    /// </remarks>
    public ValueTask<RecordReadResult> ReadRecordAsync(RecordSpan span, CancellationToken cancellationToken) =>
        ReadRecordCoreAsync(
            () => ReadFramedAsync(span, logicalLengthGuard: 0, cancellationToken), cancellationToken);

    private async ValueTask<RecordReadResult> ReadRecordCoreAsync(
        Func<ValueTask<FramedRecord>> read, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (_recordKey is null && _classKey is null && _opener is null)
        {
            return RecordReadResult.Failure(RecordReadOutcome.ContentSealed, Strings.BlobReader_ContentKeySealed);
        }

        var framed = await read().ConfigureAwait(false);
        if (framed.Bytes is not { } recordBytes)
        {
            return RecordReadResult.Failure(framed.Outcome, framed.Detail!);
        }

        var header = framed.Header!.Value;

        Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
        Span<byte> aad = stackalloc byte[RecordAad.Length];
        Span<byte> derived = stackalloc byte[RecordKeyDeriver.RecordKeyLength];
        scoped ReadOnlySpan<byte> aadForRecord;
        scoped ReadOnlySpan<byte> keyForRecord;

        if (_recordPrefixLength == 0)
        {
            RecordNonce.Write(header.Ordinal, nonce);
            RecordAad.Write(
                _repositoryId, Envelope.FormatVersion, header.ObjectType, header.ObjectId, header.Ordinal, aad);
            aadForRecord = aad;
            keyForRecord = _recordKey!;
        }
        else
        {
            // Format 3: the nonce travels in the prefix and the key is the
            // object's, so neither depends on where the record now sits —
            // which is why these same bytes read identically after being
            // copied into another blob (04 §3, 05 §2.2).
            recordBytes.AsSpan(RecordHeader.Length + RecordFraming.NonceOffset, RecordNonce.AesGcmLength)
                .CopyTo(nonce);
            RecordAad.WriteRelocatable(
                _repositoryId, Envelope.FormatVersion, header.ObjectType, header.ObjectId,
                aad[..RecordAad.RelocatableLength]);
            aadForRecord = aad[..RecordAad.RelocatableLength];

            if (_classKey is not null)
            {
                RecordKeyDeriver.Derive(_classKey, header.ObjectType, header.ObjectId, derived);
            }
            else
            {
                byte[] shareKey;
                try
                {
                    shareKey = _opener!.OpenRecordKey(
                        recordBytes.AsSpan(
                            RecordHeader.Length + RecordFraming.SealedKeyOffset, SealedRecordKey.SealedLength),
                        header.ObjectId);
                }
                catch (Exception refusal) when (refusal is SealedContentException or ArgumentException)
                {
                    // Contained to the record, not the blob: a share
                    // transplanted onto another object's record refuses here
                    // while every other record in the blob still reads. The
                    // per-blob equivalent has to fail the open, because there
                    // the one share is the whole plane.
                    return RecordReadResult.Failure(
                        RecordReadOutcome.AuthenticationFailed, Strings.BlobReader_RecordShareDoesNotOpen);
                }

                shareKey.CopyTo(derived);
                CryptographicOperations.ZeroMemory(shareKey);
            }

            keyForRecord = derived;
        }

        var storedPayload = new byte[header.StoredLength];
        var opened = RecordCipher.TryOpen(
            keyForRecord,
            nonce,
            aadForRecord,
            recordBytes.AsSpan(RecordHeader.Length + _recordPrefixLength, (int)header.StoredLength),
            recordBytes.AsSpan(
                RecordHeader.Length + _recordPrefixLength + (int)header.StoredLength, RecordCipher.TagLength),
            storedPayload);

        CryptographicOperations.ZeroMemory(derived);

        if (!opened)
        {
            return RecordReadResult.Failure(
                RecordReadOutcome.AuthenticationFailed,
                $"Record {header.ObjectId} in blob {Envelope.BlobId} failed authentication (specification 04 §7).");
        }

        byte[] plaintext;
        if (header.CompressionProfile == CompressionProfile.ZstdV1)
        {
            plaintext = new byte[header.LogicalLength];
            try
            {
                // The one piece of shared mutable state in a record read.
                // Concurrent calls on one native decoder do not fail loudly —
                // they produce plausible garbage, which then fails step 7
                // below and reads as corruption in the repository rather than
                // as a bug here. Held only across the decompress: the range
                // read above and the hash below stay outside it.
                lock (_decompressorGate)
                {
                    _decompressor.Decompress(storedPayload, plaintext);
                }
            }
            catch (CompressionFormatException exception)
            {
                return RecordReadResult.Failure(RecordReadOutcome.FormatViolation, exception.Message);
            }
        }
        else
        {
            plaintext = storedPayload;
        }

        // 04 §6 step 7: verify the plaintext hashes to the content identifier
        // the object identifier implies. Not redundant with the tag — this is
        // what catches an honest-writer bug or a poisoned reused segment.
        var impliedObjectId = _objectIdDeriver.Derive(header.ObjectType, ContentHasher.Hash(plaintext));
        if (impliedObjectId != header.ObjectId)
        {
            return RecordReadResult.Failure(
                RecordReadOutcome.ContentMismatch,
                $"Record {header.ObjectId} decrypted but its plaintext does not match its identifier (specification 04 §6 step 7).");
        }

        return RecordReadResult.Success(plaintext);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_blobKey);
        if (_recordKey is not null && !ReferenceEquals(_recordKey, _blobKey))
        {
            CryptographicOperations.ZeroMemory(_recordKey);
        }

        if (_classKey is not null)
        {
            CryptographicOperations.ZeroMemory(_classKey);
        }

        _decompressor.Dispose();
    }

    private static async ValueTask<byte[]> ReadRangeAsync(
        IObjectStore store,
        ObjectKey key,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        using var result = await store.OpenReadAsync(key, new ObjectRange(offset, length), cancellationToken).ConfigureAwait(false);

        if (result.Outcome != OpenReadOutcome.Found)
        {
            throw new BlobFormatException(Strings.FormatBlobReader_RangeBlobObjectCouldNot(offset, offset + length, key, result.Outcome));
        }

        var buffer = new byte[length];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await result.Content!.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new BlobFormatException(Strings.FormatBlobReader_RangeReadEndedBytesEarly(key, buffer.Length - filled));
            }

            filled += read;
        }

        return buffer;
    }
}

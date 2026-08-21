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
public sealed class BlobReader : IDisposable
{
    private readonly IObjectStore _store;
    private readonly ObjectKey _key;
    private readonly long _blobLength;
    private readonly RepositoryId _repositoryId;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly byte[] _blobKey;
    private readonly byte[]? _recordKey;
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
        RecordTable = recordTable;
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
    /// Opens a sealed v2 data blob's content key from its envelope — a
    /// restore grant's capability. Null means structure-only: the record
    /// table opens, record reads answer
    /// <see cref="RecordReadOutcome.ContentSealed"/>.
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
        Func<BlobEnvelope, byte[]>? sealedContentKeyOpener = null,
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

        // A sealed v2 data blob's STRUCTURE lives on the metadata plane
        // (ADR-0042 §2): its footer key derives from the metadata class key,
        // and only its records need the sealed content key below.
        var sealedContent =
            envelope.FormatVersion >= FormatLimits.SealedFormatVersion && envelope.BlobClass == BlobClass.Data;
        var structureClass = sealedContent ? BlobClass.Metadata : envelope.BlobClass;

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

        var entries = BlobFooter.DecodeRecordTable(table, recordCount, blobLength);

        // v1 and metadata blobs read records under the structure key itself;
        // a sealed blob's records need the content key a grant can open.
        byte[]? recordKey;
        if (!sealedContent)
        {
            recordKey = blobKey;
        }
        else if (sealedContentKeyOpener is not null)
        {
            try
            {
                recordKey = sealedContentKeyOpener(envelope);
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
        else
        {
            recordKey = null;
        }

        Log.BlobOpened(log, envelope.BlobId, entries.Count);

        return new BlobReader(store, key, blobLength, repositoryId, objectIdDeriver, envelope, blobKey, recordKey, entries);
    }

    /// <summary>
    /// Reads, authenticates, decompresses, and content-verifies one record —
    /// the 04 §6 sequence in order, step 7 included.
    /// </summary>
    public async ValueTask<RecordReadResult> ReadRecordAsync(RecordTableEntry entry, CancellationToken cancellationToken)
    {
        if (_recordKey is null)
        {
            return RecordReadResult.Failure(RecordReadOutcome.ContentSealed, Strings.BlobReader_ContentKeySealed);
        }

        if (!EncryptionProfile.TryFromValue(entry.EncryptionProfileValue, out var encryptionProfile) ||
            encryptionProfile != EncryptionProfile.Aes256GcmV1)
        {
            return RecordReadResult.Failure(
                RecordReadOutcome.UnsupportedProfile,
                $"Encryption profile 0x{entry.EncryptionProfileValue:x4} is not supported by this reader; refused, not guessed (specification 00 §3).");
        }

        if (entry.LogicalLength > (ulong)FormatLimits.MaxRecordStoredLength)
        {
            return RecordReadResult.Failure(
                RecordReadOutcome.FormatViolation,
                $"logical_length {entry.LogicalLength} exceeds the 64 MiB segment bound (specification 00 §8) — refused before allocation.");
        }

        var recordLength = RecordHeader.Length + entry.StoredLength + RecordCipher.TagLength;
        var recordBytes = await ReadRangeAsync(
            _store, _key, (long)entry.PhysicalOffset, recordLength, cancellationToken).ConfigureAwait(false);

        RecordHeader header;
        try
        {
            header = RecordHeader.Parse(recordBytes.AsSpan(0, RecordHeader.Length));
        }
        catch (RecordFormatException exception)
        {
            return RecordReadResult.Failure(RecordReadOutcome.FormatViolation, exception.Message);
        }

        if (header.Ordinal != entry.Ordinal ||
            header.ObjectId != entry.ObjectId ||
            header.StoredLength != entry.StoredLength ||
            header.LogicalLength != entry.LogicalLength ||
            header.CompressionProfile.Value != entry.CompressionProfileValue ||
            header.EncryptionProfile.Value != entry.EncryptionProfileValue ||
            header.ObjectType != entry.ObjectType)
        {
            return RecordReadResult.Failure(
                RecordReadOutcome.FormatViolation,
                $"The record header at offset {entry.PhysicalOffset} disagrees with the footer's table entry — a damage finding (specification 05 §3.1).");
        }

        Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
        RecordNonce.Write(header.Ordinal, nonce);
        Span<byte> aad = stackalloc byte[RecordAad.Length];
        RecordAad.Write(_repositoryId, Envelope.FormatVersion, header.ObjectType, header.ObjectId, header.Ordinal, aad);

        var storedPayload = new byte[header.StoredLength];
        var opened = RecordCipher.TryOpen(
            _recordKey,
            nonce,
            aad,
            recordBytes.AsSpan(RecordHeader.Length, (int)header.StoredLength),
            recordBytes.AsSpan(RecordHeader.Length + (int)header.StoredLength, RecordCipher.TagLength),
            storedPayload);

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

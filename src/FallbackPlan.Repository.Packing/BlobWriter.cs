using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// Assembles one blob (specification 05; FR-ARCH-012): envelope, records,
/// authenticated recovery footer, locator. The blob identifier is allocated
/// up front — before any byte exists — which is what the write-intent
/// mechanism will require (specification 02 §4.1; FR-SNP-006).
/// </summary>
/// <remarks>
/// <para>
/// Records spool to a durable temp file as their final sealed bytes; ordinals
/// are assigned monotonically inside this instance, the salt is drawn once at
/// creation, and every record is encrypted exactly once — by construction the
/// same <c>(blob_key, ordinal)</c> can never cover two byte strings, the
/// invariant the spool rules exist to protect (05 §6.1). The C1 crash-resume
/// checkpoint builds on exactly this file later; nothing here needs
/// retrofitting.
/// </para>
/// <para>
/// <see cref="CanAppend"/> bounds the <em>whole sealed object</em> — records
/// plus a conservative footer reserve plus the locator — against the
/// profile's maximum, because a blob that exceeded a provider's object-size
/// limit only once the footer landed would defeat configuration-time
/// validation (05 §5; FR-ARCH-007). Records are never split (04 §8).
/// </para>
/// </remarks>
public sealed class BlobWriter : IAsyncDisposable
{
    private readonly BlobEnvelope _envelope;
    private readonly BlobWriteProfile _profile;
    private readonly EncryptionProfile _encryptionProfile;
    private readonly RepositoryId _repositoryId;
    private readonly byte[] _blobKey;
    private readonly string _spoolPath;
    private readonly FileStream _spool;
    private readonly IncrementalHash _digest;
    private readonly List<RecordTableEntry> _entries = [];
    private bool _sealed;

    private BlobWriter(
        BlobEnvelope envelope,
        BlobWriteProfile profile,
        EncryptionProfile encryptionProfile,
        RepositoryId repositoryId,
        byte[] blobKey,
        string spoolPath,
        FileStream spool)
    {
        _envelope = envelope;
        _profile = profile;
        _encryptionProfile = encryptionProfile;
        _repositoryId = repositoryId;
        _blobKey = blobKey;
        _spoolPath = spoolPath;
        _spool = spool;
        _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    /// <summary>The writer-allocated blob identifier, known before any byte exists.</summary>
    public BlobId BlobId => _envelope.BlobId;

    /// <summary>The number of records appended so far.</summary>
    public int RecordCount => _entries.Count;

    /// <summary>The current spool length in bytes.</summary>
    public long CurrentLength { get; private set; }

    /// <summary>
    /// Whether the writer has reached a sealing trigger — the target size or
    /// the record-count limit (specification 05 §5). The open-blob age
    /// trigger is policy the caller applies.
    /// </summary>
    public bool ShouldSeal =>
        CurrentLength >= _profile.TargetSizeBytes || _entries.Count >= _profile.MaximumRecordCount;

    /// <summary>
    /// Creates a writer. The class key is copied and zeroed on dispose; the
    /// salt is drawn from the CSPRNG unless the caller supplies one for
    /// deterministic tests.
    /// </summary>
    public static BlobWriter Create(
        RepositoryId repositoryId,
        WriterId writerId,
        KeyGeneration keyGeneration,
        BlobClass blobClass,
        ReadOnlySpan<byte> classKey,
        ulong blobCounter,
        EncryptionProfile encryptionProfile,
        BlobWriteProfile profile,
        string spoolDirectory,
        ReadOnlySpan<byte> blobSalt = default)
    {
        ArgumentNullException.ThrowIfNull(encryptionProfile);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);

        if (encryptionProfile != EncryptionProfile.Aes256GcmV1)
        {
            throw new ArgumentException(
                "Only aes-256-gcm-v1 is implemented; xchacha20-poly1305-v1 has no platform primitive (open question Q12) and is refused, not guessed.",
                nameof(encryptionProfile));
        }

        Span<byte> salt = stackalloc byte[BlobKeyDeriver.BlobSaltLength];
        if (blobSalt.IsEmpty)
        {
            RandomNumberGenerator.Fill(salt);
        }
        else if (blobSalt.Length == BlobKeyDeriver.BlobSaltLength)
        {
            blobSalt.CopyTo(salt);
        }
        else
        {
            throw new ArgumentException($"A blob salt is exactly {BlobKeyDeriver.BlobSaltLength} bytes.", nameof(blobSalt));
        }

        var envelope = new BlobEnvelope(
            FormatLimits.FormatVersion,
            blobClass,
            keyGeneration,
            BlobId.FromWriterCounter(writerId, blobCounter),
            salt,
            blobCounter,
            writerId);

        var blobKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, salt, writerId, blobCounter, blobKey);

        Directory.CreateDirectory(spoolDirectory);
        var spoolPath = Path.Combine(spoolDirectory, $"blob-{envelope.BlobId}.spool");
        var spool = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);

        var writer = new BlobWriter(envelope, profile, encryptionProfile, repositoryId, blobKey, spoolPath, spool);

        Span<byte> envelopeBytes = stackalloc byte[BlobEnvelope.Length];
        envelope.WriteTo(envelopeBytes);
        writer._spool.Write(envelopeBytes);
        writer._digest.AppendData(envelopeBytes);
        writer.CurrentLength = BlobEnvelope.Length;

        return writer;
    }

    /// <summary>
    /// Whether a record of <paramref name="storedLength"/> payload bytes fits
    /// without the sealed object — records, a conservative footer reserve,
    /// and the locator — exceeding the profile's maximum. A record that does
    /// not fit is sealed around, never split (specification 04 §8).
    /// </summary>
    public bool CanAppend(int storedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(storedLength);

        if (_entries.Count >= _profile.MaximumRecordCount || _entries.Count >= FormatLimits.MaxRecordsPerBlob)
        {
            return false;
        }

        var recordSize = RecordHeader.Length + (long)storedLength + RecordCipher.TagLength;
        var footerReserve = BlobFooter.HeaderLength + RecordCipher.TagLength + 96L * (_entries.Count + 1);

        return CurrentLength + recordSize + footerReserve + FooterLocator.Length <= _profile.MaximumSizeBytes;
    }

    /// <summary>
    /// Encrypts and appends one record, assigning its ordinal. The payload is
    /// the stored form — compressed, or the plaintext when the threshold said
    /// no (specification 04 §5).
    /// </summary>
    /// <exception cref="InvalidOperationException">The writer is sealed or the record does not fit.</exception>
    public async ValueTask<uint> AppendRecordAsync(
        ObjectType objectType,
        ObjectId objectId,
        CompressionProfile compressionProfile,
        ulong logicalLength,
        ReadOnlyMemory<byte> storedPayload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_sealed, this);

        if (!CanAppend(storedPayload.Length))
        {
            throw new InvalidOperationException(
                "The record does not fit this blob; seal and start a new one — records are never split (specification 04 §8).");
        }

        var ordinal = (uint)_entries.Count;
        var header = new RecordHeader(
            objectType,
            compressionProfile,
            _encryptionProfile,
            ordinal,
            logicalLength,
            (uint)storedPayload.Length,
            objectId);

        var record = new byte[RecordHeader.Length + storedPayload.Length + RecordCipher.TagLength];
        header.WriteTo(record.AsSpan(0, RecordHeader.Length));

        Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
        RecordNonce.Write(ordinal, nonce);
        Span<byte> aad = stackalloc byte[RecordAad.Length];
        RecordAad.Write(_repositoryId, _envelope.FormatVersion, objectType, objectId, ordinal, aad);

        RecordCipher.Seal(
            _blobKey,
            nonce,
            aad,
            storedPayload.Span,
            record.AsSpan(RecordHeader.Length, storedPayload.Length),
            record.AsSpan(RecordHeader.Length + storedPayload.Length, RecordCipher.TagLength));

        await _spool.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        _digest.AppendData(record);

        _entries.Add(new RecordTableEntry(
            objectId,
            ordinal,
            (ulong)CurrentLength,
            (uint)storedPayload.Length,
            logicalLength,
            compressionProfile.Value,
            _encryptionProfile.Value,
            objectType));

        CurrentLength += record.Length;

        return ordinal;
    }

    /// <summary>
    /// Seals the blob (specification 05 §5): encrypts the record table under
    /// the blob key with the reserved footer nonce, appends footer and
    /// locator, flushes durably, and returns the immutable result.
    /// </summary>
    public async ValueTask<SealedBlob> SealAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_sealed, this);
        _sealed = true;

        var table = BlobFooter.EncodeRecordTable(_entries);

        var footer = new byte[BlobFooter.HeaderLength + table.Length + RecordCipher.TagLength];
        BlobFooter.WriteHeader((uint)_entries.Count, (uint)table.Length, footer.AsSpan(0, BlobFooter.HeaderLength));

        Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
        RecordNonce.WriteFooterNonce(nonce);
        Span<byte> aad = stackalloc byte[FooterAad.Length];
        FooterAad.Write(_repositoryId, _envelope.FormatVersion, _envelope.BlobId, (uint)_entries.Count, aad);

        RecordCipher.Seal(
            _blobKey,
            nonce,
            aad,
            table,
            footer.AsSpan(BlobFooter.HeaderLength, table.Length),
            footer.AsSpan(BlobFooter.HeaderLength + table.Length, RecordCipher.TagLength));

        await _spool.WriteAsync(footer, cancellationToken).ConfigureAwait(false);
        _digest.AppendData(footer);
        var footerOffset = (ulong)CurrentLength;
        CurrentLength += footer.Length;

        // The digest covers every sealed byte before the locator — the
        // locator's own prefix cannot be part of its preimage (see
        // FooterLocator's erratum note).
        var digest = new byte[32];
        _digest.GetHashAndReset(digest);

        var locatorBytes = new byte[FooterLocator.Length];
        new FooterLocator(footerOffset, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest))
            .WriteTo(locatorBytes);
        await _spool.WriteAsync(locatorBytes, cancellationToken).ConfigureAwait(false);
        CurrentLength += FooterLocator.Length;

        _spool.Flush(flushToDisk: true);
        await _spool.DisposeAsync().ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(_blobKey);

        return new SealedBlob(_spoolPath, _envelope.BlobId, _envelope.BlobClass, CurrentLength, digest, _entries);
    }

    /// <summary>
    /// Zeroes key material and, when the writer was abandoned before
    /// sealing, discards the partial spool — a partial spool is never
    /// uploaded under any circumstances (specification 05 §6.3).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        CryptographicOperations.ZeroMemory(_blobKey);
        _digest.Dispose();

        if (!_sealed)
        {
            await _spool.DisposeAsync().ConfigureAwait(false);
            if (File.Exists(_spoolPath))
            {
                File.Delete(_spoolPath);
            }
        }
    }
}

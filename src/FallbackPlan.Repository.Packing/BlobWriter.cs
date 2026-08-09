using Bodu;
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

    // One key schedule per blob rather than per record. The blob key does not
    // change for the writer's life, and AesGcm is not safe for concurrent use —
    // which is fine, because record sealing sits inside the ordered stage
    // (ADR-0029 §1) by construction.
    private readonly AesGcm _cipher;
    private readonly string _spoolPath;
    private readonly FileStream _spool;
    private readonly IncrementalHash _digest;
    private readonly List<RecordTableEntry> _entries = [];
    private readonly SpoolPinnedConfiguration? _pinned;
    private bool _sealed;
    private bool _abandoned;

    private BlobWriter(
        BlobEnvelope envelope,
        BlobWriteProfile profile,
        EncryptionProfile encryptionProfile,
        RepositoryId repositoryId,
        byte[] blobKey,
        string spoolPath,
        FileStream spool,
        SpoolPinnedConfiguration? pinned)
    {
        _envelope = envelope;
        _profile = profile;
        _encryptionProfile = encryptionProfile;
        _repositoryId = repositoryId;
        _blobKey = blobKey;
        _cipher = new AesGcm(blobKey, RecordCipher.TagLength);
        _spoolPath = spoolPath;
        _spool = spool;
        _pinned = pinned;
        _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    /// <summary>The writer-allocated blob identifier, known before any byte exists.</summary>
    public BlobId BlobId => _envelope.BlobId;

    /// <summary>The number of records appended so far.</summary>
    public int RecordCount => _entries.Count;

    /// <summary>
    /// The records held so far, in ordinal order. A resumed writer arrives
    /// holding records the resuming session did not produce, so the caller
    /// rebuilds its own view — dedup set, pending index entries — from this
    /// rather than assuming the blob is empty.
    /// </summary>
    public IReadOnlyList<RecordTableEntry> Entries => _entries;

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
        ReadOnlySpan<byte> blobSalt = default,
        SpoolPinnedConfiguration? pinned = null)
    {
        ThrowHelper.ThrowIfNull(encryptionProfile);
        ThrowHelper.ThrowIfNull(profile);
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolDirectory);

        if (encryptionProfile != EncryptionProfile.Aes256GcmV1)
        {
            throw new ArgumentException(
                "Format version 1 admits one record AEAD, aes-256-gcm-v1 (specification 03 §6); any other profile is refused, not guessed.",
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

        var writer = new BlobWriter(envelope, profile, encryptionProfile, repositoryId, blobKey, spoolPath, spool, pinned);

        Span<byte> envelopeBytes = stackalloc byte[BlobEnvelope.Length];
        envelope.WriteTo(envelopeBytes);
        writer._spool.Write(envelopeBytes);
        writer._digest.AppendData(envelopeBytes);
        writer.CurrentLength = BlobEnvelope.Length;

        if (pinned is not null)
        {
            writer._spool.Flush(flushToDisk: true);
            writer.WriteCheckpoint();
        }

        return writer;
    }

    /// <summary>
    /// Attempts to resume the checkpointed spool in
    /// <paramref name="spoolDirectory"/> (specification 05 §6.3; C1;
    /// FR-ARCH-011). Resume re-emits the spooled sealed bytes verbatim and
    /// continues at the next ordinal; <b>any</b> mismatch between the
    /// checkpoint's pinned fields and the current configuration — codec
    /// version included — discards the spool and reports restart, because a
    /// restarted blob draws a fresh salt and reuses nothing (05 §6.2).
    /// </summary>
    /// <remarks>
    /// The resume point is found by <b>authenticating</b> every record, not by
    /// trusting a durable watermark. A record whose tag verifies under
    /// <c>(blob_key, ordinal)</c> is one this writer sealed and the disk holds
    /// whole; anything else — a torn tail, a flipped ciphertext byte, a gap in
    /// the ordinals, a spool from another repository — fails and forces a
    /// restart. So no ordinal is ever re-used under one salt, which is the
    /// property 05 §6.1 exists to protect, and it holds without writing
    /// anything per record.
    /// </remarks>
    public static ResumeResult TryResume(
        string spoolDirectory,
        RepositoryId repositoryId,
        WriterId writerId,
        KeyGeneration keyGeneration,
        BlobClass blobClass,
        ReadOnlySpan<byte> classKey,
        EncryptionProfile encryptionProfile,
        BlobWriteProfile profile,
        SpoolPinnedConfiguration current)
    {
        ThrowHelper.ThrowIfNull(encryptionProfile);
        ThrowHelper.ThrowIfNull(profile);
        ThrowHelper.ThrowIfNull(current);
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolDirectory);

        if (!Directory.Exists(spoolDirectory))
        {
            return new ResumeResult.NoSpool();
        }

        var checkpointPath = Directory.EnumerateFiles(spoolDirectory, "blob-*.spool.checkpoint")
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

        if (checkpointPath is null)
        {
            return new ResumeResult.NoSpool();
        }

        var spoolPath = checkpointPath[..^".checkpoint".Length];

        ResumeResult.MustRestart Discard(string reason)
        {
            File.Delete(checkpointPath);
            if (File.Exists(spoolPath))
            {
                File.Delete(spoolPath);
            }

            return new ResumeResult.MustRestart(reason);
        }

        if (!SpoolCheckpoint.TryParse(File.ReadAllBytes(checkpointPath), out var checkpoint))
        {
            return Discard("checkpoint_unreadable");
        }

        // 05 §6.2: every pinned field compared; any mismatch is a restart.
        if (checkpoint!.Pinned.CodecVersion != current.CodecVersion)
        {
            return Discard("codec_version_changed");
        }

        if (checkpoint.Pinned.CompressionProfile != current.CompressionProfile)
        {
            return Discard("compression_profile_changed");
        }

        if (checkpoint.Pinned.SegmentationProfile != current.SegmentationProfile)
        {
            return Discard("segmentation_profile_changed");
        }

        if (checkpoint.Pinned.SegmentationParameter1 != current.SegmentationParameter1 ||
            checkpoint.Pinned.SegmentationParameter2 != current.SegmentationParameter2 ||
            checkpoint.Pinned.SegmentationParameter3 != current.SegmentationParameter3)
        {
            return Discard("segmentation_parameters_changed");
        }

        if (checkpoint.Pinned.EncryptionProfile != current.EncryptionProfile ||
            checkpoint.Pinned.EncryptionProfile != encryptionProfile.Value)
        {
            return Discard("encryption_profile_changed");
        }

        if (checkpoint.FormatVersion != FormatLimits.FormatVersion)
        {
            return Discard("format_version_changed");
        }

        if (checkpoint.KeyGeneration != keyGeneration)
        {
            return Discard("key_generation_changed");
        }

        if (checkpoint.BlobClass != blobClass)
        {
            return Discard("blob_class_changed");
        }

        if (checkpoint.WriterId != writerId)
        {
            return Discard("writer_changed");
        }

        if (!File.Exists(spoolPath))
        {
            return Discard("spool_missing");
        }

        // The spool's own bytes are the resume state (05 §6.1). Verify the
        // envelope, then walk the records authenticating each one: a record
        // whose tag verifies reached the disk whole, so the tags themselves
        // bound the resume. Nothing has to be kept in step with the bytes,
        // which is what lets the sidecar be written once at create.
        var spoolBytes = File.ReadAllBytes(spoolPath);

        if (spoolBytes.Length < BlobEnvelope.Length)
        {
            return Discard("spool_shorter_than_envelope");
        }

        BlobEnvelope envelope;
        try
        {
            envelope = BlobEnvelope.Parse(spoolBytes.AsSpan(0, BlobEnvelope.Length));
        }
        catch (BlobFormatException)
        {
            return Discard("envelope_unreadable");
        }

        if (envelope.BlobId != checkpoint.BlobId ||
            envelope.BlobCounter != checkpoint.BlobCounter ||
            envelope.WriterId != checkpoint.WriterId ||
            envelope.KeyGeneration != checkpoint.KeyGeneration ||
            envelope.BlobClass != checkpoint.BlobClass ||
            !envelope.BlobSalt.SequenceEqual(checkpoint.BlobSalt.Span))
        {
            return Discard("envelope_checkpoint_mismatch");
        }

        // Derived before the walk rather than after it: the key is now walk
        // input, because authenticating a record is what proves it.
        var blobKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, envelope.BlobSalt, envelope.WriterId, envelope.BlobCounter, blobKey);

        var entries = new List<RecordTableEntry>();
        var offset = BlobEnvelope.Length;
        var scratch = Array.Empty<byte>();

        // One reason for every way the walk can fail. Structural damage and a
        // failed tag are the same finding — these bytes are not a dense
        // sequence of records this key sealed — and both resolve to restart,
        // which draws a fresh salt and so reuses no ordinal (05 §6.2:
        // "Restart is always the safe failure. A writer in any doubt MUST
        // restart.").
        ResumeResult.MustRestart DiscardTail()
        {
            CryptographicOperations.ZeroMemory(blobKey);
            CryptographicOperations.ZeroMemory(scratch);
            return Discard("spool_tail_unauthenticated");
        }

        Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
        Span<byte> aad = stackalloc byte[RecordAad.Length];

        while (offset < spoolBytes.Length)
        {
            if (offset + RecordHeader.Length > spoolBytes.Length)
            {
                return DiscardTail();
            }

            RecordHeader header;
            try
            {
                header = RecordHeader.Parse(spoolBytes.AsSpan(offset, RecordHeader.Length));
            }
            catch (RecordFormatException)
            {
                return DiscardTail();
            }

            // Ordinals are dense and ascending (05 §3.1, 04 §2.1) — and the
            // ordinal is the nonce, so a gap would name a nonce this key
            // never covered.
            if (header.Ordinal != entries.Count)
            {
                return DiscardTail();
            }

            // Parse already refused a stored_length past the 64 MiB limit;
            // this bounds the record against the file before allocating.
            var recordLength = RecordHeader.Length + (long)header.StoredLength + RecordCipher.TagLength;
            if (offset + recordLength > spoolBytes.Length)
            {
                return DiscardTail();
            }

            var storedLength = (int)header.StoredLength;
            if (scratch.Length < storedLength)
            {
                scratch = new byte[storedLength];
            }

            RecordNonce.Write(header.Ordinal, nonce);
            RecordAad.Write(repositoryId, envelope.FormatVersion, header.ObjectType, header.ObjectId, header.Ordinal, aad);

            // The AAD binds the repository, the object and the ordinal, so
            // this also refuses a spool belonging to another repository or a
            // record moved between ordinals (04 §4).
            if (!RecordCipher.TryOpen(
                    blobKey,
                    nonce,
                    aad,
                    spoolBytes.AsSpan(offset + RecordHeader.Length, storedLength),
                    spoolBytes.AsSpan(offset + RecordHeader.Length + storedLength, RecordCipher.TagLength),
                    scratch.AsSpan(0, storedLength)))
            {
                return DiscardTail();
            }

            entries.Add(new RecordTableEntry(
                header.ObjectId,
                header.Ordinal,
                (ulong)offset,
                header.StoredLength,
                header.LogicalLength,
                header.CompressionProfile.Value,
                header.EncryptionProfile.Value,
                header.ObjectType));

            offset += (int)recordLength;
        }

        // The plaintext was read only to prove the tag; it is nobody's output
        // and does not outlive the walk.
        CryptographicOperations.ZeroMemory(scratch);

        // Nothing is truncated. The walk either consumed the file exactly —
        // every record bounded within it — or it restarted.
        var spool = new FileStream(spoolPath, FileMode.Open, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
        spool.Seek(0, SeekOrigin.End);

        var writer = new BlobWriter(envelope, profile, encryptionProfile, repositoryId, blobKey, spoolPath, spool, current);
        writer._digest.AppendData(spoolBytes);
        writer._entries.AddRange(entries);
        writer.CurrentLength = spoolBytes.Length;

        return new ResumeResult.Resumed(writer);
    }

    /// <summary>
    /// Whether a record of <paramref name="storedLength"/> payload bytes fits
    /// without the sealed object — records, a conservative footer reserve,
    /// and the locator — exceeding the profile's maximum. A record that does
    /// not fit is sealed around, never split (specification 04 §8).
    /// </summary>
    public bool CanAppend(int storedLength)
    {
        ThrowHelper.ThrowIfNegative(storedLength);

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
        ThrowHelper.ThrowIfDisposed(_sealed, nameof(BlobWriter));

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
            _cipher,
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

        // No fsync and no sidecar rewrite here. Durability of the tail is not
        // what makes resume safe — authentication is (see TryResume) — and a
        // record that never reached the disk simply is not resumed. What the
        // per-record pair used to cost was ~128 fsyncs and ~128 whole-file
        // sidecar rewrites per 128 MiB blob, both blocking, for a guarantee
        // the tags already give (ADR-0029 §6, serial cost 1).
        return ordinal;
    }

    /// <summary>
    /// Releases handles and key material <b>without</b> deleting the spool or
    /// its checkpoint — the orderly-shutdown and crash-simulation path. The
    /// spool stays on disk for <see cref="TryResume"/>; a partial spool is
    /// still never uploaded (specification 05 §6.3).
    /// </summary>
    public async ValueTask AbandonAsync()
    {
        ThrowHelper.ThrowIfDisposed(_sealed, nameof(BlobWriter));
        _abandoned = true;

        _cipher.Dispose();
        CryptographicOperations.ZeroMemory(_blobKey);
        _digest.Dispose();
        _spool.Flush(flushToDisk: true);
        await _spool.DisposeAsync().ConfigureAwait(false);
    }

    private void WriteCheckpoint()
    {
        var checkpoint = new SpoolCheckpoint(
            _envelope.FormatVersion,
            _envelope.BlobClass,
            _envelope.KeyGeneration,
            _envelope.BlobSalt.ToArray(),
            _envelope.WriterId,
            _envelope.BlobCounter,
            _envelope.BlobId,
            _pinned!);

        // Written once, at create. Every field is fixed for the blob's life
        // (05 §6.2), so there is nothing to keep current. Still replaced
        // atomically: a crash mid-write leaves no torn sidecar.
        var checkpointPath = SpoolCheckpoint.PathFor(_spoolPath);
        var temporary = checkpointPath + ".tmp";
        File.WriteAllBytes(temporary, checkpoint.Serialize());
        File.Move(temporary, checkpointPath, overwrite: true);
    }

    /// <summary>
    /// Seals the blob (specification 05 §5): encrypts the record table under
    /// the blob key with the reserved footer nonce, appends footer and
    /// locator, flushes durably, and returns the immutable result.
    /// </summary>
    public async ValueTask<SealedBlob> SealAsync(CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfDisposed(_sealed, nameof(BlobWriter));
        _sealed = true;

        var table = BlobFooter.EncodeRecordTable(_entries);

        var footer = new byte[BlobFooter.HeaderLength + table.Length + RecordCipher.TagLength];
        BlobFooter.WriteHeader((uint)_entries.Count, (uint)table.Length, footer.AsSpan(0, BlobFooter.HeaderLength));

        Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
        RecordNonce.WriteFooterNonce(nonce);
        Span<byte> aad = stackalloc byte[FooterAad.Length];
        FooterAad.Write(_repositoryId, _envelope.FormatVersion, _envelope.BlobId, (uint)_entries.Count, aad);

        RecordCipher.Seal(
            _cipher,
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
        _cipher.Dispose();
        CryptographicOperations.ZeroMemory(_blobKey);

        // A sealed blob is no longer resumable state; the sidecar goes.
        if (_pinned is not null)
        {
            File.Delete(SpoolCheckpoint.PathFor(_spoolPath));
        }

        return new SealedBlob(_spoolPath, _envelope.BlobId, _envelope.BlobClass, CurrentLength, digest, _entries);
    }

    /// <summary>
    /// Zeroes key material and, when the writer was abandoned before
    /// sealing, discards the partial spool — a partial spool is never
    /// uploaded under any circumstances (specification 05 §6.3).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cipher.Dispose();
        CryptographicOperations.ZeroMemory(_blobKey);

        if (_abandoned)
        {
            return;
        }

        _digest.Dispose();

        if (!_sealed)
        {
            await _spool.DisposeAsync().ConfigureAwait(false);
            if (File.Exists(_spoolPath))
            {
                File.Delete(_spoolPath);
            }

            var checkpointPath = SpoolCheckpoint.PathFor(_spoolPath);
            if (File.Exists(checkpointPath))
            {
                File.Delete(checkpointPath);
            }
        }
    }
}

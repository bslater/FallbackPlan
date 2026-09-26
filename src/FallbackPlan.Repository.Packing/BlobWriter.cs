using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing.Resources;

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
    // Format 2's per-blob content key; in a format-3 sealed data blob the
    // same slot holds the record-key SEED instead — one 32-byte secret per
    // blob either way, which is what lets the checkpoint keep carrying one
    // (05 §6.2). Nothing derives a record key from a seed but the writer.
    private readonly byte[]? _contentKey;

    // Format 3 keys each record on its own (03 §5.4), so a writer holds what
    // the derivation needs rather than one cipher: the class key a
    // structure-plane record expands over its own identity, or — in the
    // sealed data plane — the public key each record's share is sealed to,
    // the seed being _contentKey above. Both null in every format-2 writer,
    // where one key covers the whole blob.
    private readonly byte[]? _recordClassKey;
    private readonly byte[]? _recordSealingPublicKey;

    // One key schedule per blob rather than per record. The keys do not
    // change for the writer's life, and AesGcm is not safe for concurrent use —
    // which is fine, because record sealing sits inside the ordered stage
    // (ADR-0029 §1) by construction. A v1 blob has one cipher for records and
    // footer alike; a sealed v2 data blob has two — records under the random
    // content key, the footer under the structure-derived blob key
    // (ADR-0042 §2) — and _footerCipher is the same instance as _cipher
    // exactly when the blob is not sealed-content. A format-3 blob has no
    // record cipher at all: its key changes with every record, so _cipher is
    // null and each append builds its own schedule.
    private readonly AesGcm? _cipher;
    private readonly AesGcm _footerCipher;
    private readonly string _spoolPath;
    private readonly FileStream _spool;
    private readonly IncrementalHash _digest;
    private readonly BlobMerkleAccumulator _merkle;
    private readonly List<RecordTableEntry> _entries = [];
    private readonly SpoolPinnedConfiguration? _pinned;
    private readonly ILogger _log;
    private bool _sealed;
    private bool _abandoned;
    private bool _spoolClosed;

    // Internal, not private: the abandon-path tests need to hand in a spool
    // stream whose durable flush fails, and a real FileStream cannot be made
    // to fail on cue. Product code constructs writers only through Create,
    // CreateSealed and TryResume, which own the spool open flags.
    internal BlobWriter(
        BlobEnvelope envelope,
        BlobWriteProfile profile,
        EncryptionProfile encryptionProfile,
        RepositoryId repositoryId,
        byte[] blobKey,
        string spoolPath,
        FileStream spool,
        SpoolPinnedConfiguration? pinned,
        byte[]? contentKey = null,
        ILogger? logger = null,
        IncrementalHash? digest = null,
        BlobMerkleAccumulator? merkle = null,
        byte[]? recordClassKey = null,
        byte[]? recordSealingPublicKey = null)
    {
        _log = logger ?? NullLogger.Instance;
        _envelope = envelope;
        _profile = profile;
        _encryptionProfile = encryptionProfile;
        _repositoryId = repositoryId;
        _blobKey = blobKey;
        _contentKey = contentKey;
        _recordClassKey = recordClassKey;
        _recordSealingPublicKey = recordSealingPublicKey;

        // The question the envelope answers, not the caller: a format-3 blob
        // keys per record, so there is no blob-wide record cipher to build,
        // and the footer — which stays under the derived blob key in every
        // version — gets a schedule of its own.
        _cipher = FormatVersions.HasRelocatableRecords(envelope.FormatVersion)
            ? null
            : new AesGcm(contentKey ?? blobKey, RecordCipher.TagLength);
        _footerCipher = _cipher is not null && contentKey is null
            ? _cipher
            : new AesGcm(blobKey, RecordCipher.TagLength);
        _spoolPath = spoolPath;
        _spool = spool;
        _pinned = pinned;

        // A resume hands over the hash its streaming walk already built, and
        // the writer owns it from here — re-reading the spool to rebuild one
        // would spend exactly the I/O the streaming walk saves.
        _digest = digest ?? IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // The Merkle commitment rides the same hand-over for the same reason
        // (05 §5): it is fed by exactly the calls that feed the digest, so
        // the tree costs no second pass over the sealed bytes, and a resume
        // adopts the one its walk rebuilt rather than re-reading the spool.
        _merkle = merkle ?? new BlobMerkleAccumulator();
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
        ushort formatVersion,
        ReadOnlySpan<byte> blobSalt = default,
        SpoolPinnedConfiguration? pinned = null,
        ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNull(encryptionProfile);
        ThrowHelper.ThrowIfNull(profile);
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolDirectory);

        if (encryptionProfile != EncryptionProfile.Aes256GcmV1)
        {
            throw new ArgumentException(Strings.BlobWriter_FormatVersionAdmitsOneRecord,
                nameof(encryptionProfile));
        }

        // Two containers come out of here and no others: the symmetric one
        // format 1 defined and format 2 kept byte for byte, and format 3's
        // relocatable one. A format-3 DATA blob is not among them — its
        // records carry a key each, which needs a public key this overload is
        // never given (05 §2.2).
        if (formatVersion != FormatLimits.SymmetricFormatVersion &&
            formatVersion != FormatVersions.RelocatableRecords)
        {
            throw new ArgumentException(
                Strings.FormatBlobWriter_FormatVersionNotWritable(
                    FormatLimits.SymmetricFormatVersion, FormatVersions.RelocatableRecords, formatVersion),
                nameof(formatVersion));
        }

        if (FormatVersions.SealsContentPerRecord(formatVersion, blobClass == BlobClass.Data))
        {
            throw new ArgumentException(Strings.BlobWriter_DataBlobSealsPerRecord, nameof(blobClass));
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
            throw new ArgumentException(Strings.FormatBlobWriter_BlobSaltExactlyBytes(BlobKeyDeriver.BlobSaltLength), nameof(blobSalt));
        }

        var envelope = new BlobEnvelope(
            formatVersion,
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

        // A format-3 record expands the class key over its own identity, so
        // the writer keeps a copy; format 1 and 2 seal every record under the
        // one derived blob key and keep none.
        var recordClassKey = FormatVersions.HasRelocatableRecords(formatVersion) ? classKey.ToArray() : null;

        var writer = new BlobWriter(
            envelope, profile, encryptionProfile, repositoryId, blobKey, spoolPath, spool, pinned, logger: logger,
            recordClassKey: recordClassKey);
        WriteEnvelopeAndCheckpoint(writer, envelope, pinned);
        return writer;
    }

    /// <summary>
    /// Creates a writer for a format-v2 <b>sealed data blob</b> (ADR-0042
    /// §2): records encrypt under a fresh random content key that only the
    /// repository's derived scalar can recover, while the footer encrypts
    /// under the structure plane — <paramref name="structureKey"/> is the
    /// METADATA class key — so a write-only holder still opens the blob's
    /// record table. The envelope carries the content key sealed to
    /// <paramref name="sealingPublicKey"/>, pinned to this repository and
    /// blob.
    /// </summary>
    public static BlobWriter CreateSealed(
        RepositoryId repositoryId,
        WriterId writerId,
        KeyGeneration keyGeneration,
        ReadOnlySpan<byte> structureKey,
        ReadOnlySpan<byte> sealingPublicKey,
        ulong blobCounter,
        EncryptionProfile encryptionProfile,
        BlobWriteProfile profile,
        string spoolDirectory,
        ushort formatVersion,
        ReadOnlySpan<byte> blobSalt = default,
        SpoolPinnedConfiguration? pinned = null,
        ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNull(encryptionProfile);
        ThrowHelper.ThrowIfNull(profile);
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolDirectory);

        if (encryptionProfile != EncryptionProfile.Aes256GcmV1)
        {
            throw new ArgumentException(Strings.BlobWriter_FormatVersionAdmitsOneRecord,
                nameof(encryptionProfile));
        }

        // Named as the two containers this overload can emit, never as the
        // version the product happens to create at. They were the same number
        // while creation stayed at format 2, and writing the guard against
        // the creation default made it a time bomb: moving that default to 3
        // turned this into a refusal of the sealed data container itself, so
        // every format-2 repository — every repository written before the
        // move — would have stopped accepting backups on the build that made
        // it.
        if (formatVersion != FormatVersions.SealedDataPlane &&
            formatVersion != FormatVersions.RelocatableRecords)
        {
            throw new ArgumentException(
                Strings.FormatBlobWriter_FormatVersionNotWritable(
                    FormatVersions.SealedDataPlane, FormatVersions.RelocatableRecords, formatVersion),
                nameof(formatVersion));
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
            throw new ArgumentException(Strings.FormatBlobWriter_BlobSaltExactlyBytes(BlobKeyDeriver.BlobSaltLength), nameof(blobSalt));
        }

        var blobId = BlobId.FromWriterCounter(writerId, blobCounter);
        var relocatable = FormatVersions.HasRelocatableRecords(formatVersion);

        // One 32-byte secret per blob in both formats, meaning two different
        // things. Format 2 seals it into the envelope and every record uses
        // it; format 3 keeps it as the SEED each record's own key is expanded
        // from, seals those keys one per record, and puts nothing in the
        // envelope — which is why a format-3 data envelope is the same 88
        // bytes a metadata one is (05 §2).
        var contentKey = RandomNumberGenerator.GetBytes(32);

        var envelope = relocatable
            ? new BlobEnvelope(
                formatVersion,
                BlobClass.Data,
                keyGeneration,
                blobId,
                salt,
                blobCounter,
                writerId)
            : new BlobEnvelope(
                formatVersion,
                BlobClass.Data,
                keyGeneration,
                blobId,
                salt,
                blobCounter,
                writerId,
                SealedContentKey.Seal(sealingPublicKey, contentKey, repositoryId, blobId));

        var blobKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(structureKey, salt, writerId, blobCounter, blobKey);

        Directory.CreateDirectory(spoolDirectory);
        var spoolPath = Path.Combine(spoolDirectory, $"blob-{envelope.BlobId}.spool");
        var spool = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);

        var writer = new BlobWriter(
            envelope, profile, encryptionProfile, repositoryId, blobKey, spoolPath, spool, pinned, contentKey, logger,
            recordSealingPublicKey: relocatable ? sealingPublicKey.ToArray() : null);
        WriteEnvelopeAndCheckpoint(writer, envelope, pinned);
        return writer;
    }

    private static void WriteEnvelopeAndCheckpoint(BlobWriter writer, BlobEnvelope envelope, SpoolPinnedConfiguration? pinned)
    {
        Span<byte> envelopeBytes = stackalloc byte[BlobEnvelope.MaxLength];
        var written = envelopeBytes[..envelope.EnvelopeLength];
        envelope.WriteTo(written);
        writer._spool.Write(written);
        writer._digest.AppendData(written);
        writer._merkle.Append(written);
        writer.CurrentLength = envelope.EnvelopeLength;

        if (pinned is not null)
        {
            writer._spool.Flush(flushToDisk: true);
            writer.WriteCheckpoint();
        }
    }

    /// <summary>
    /// Deletes spool files no resume can ever reach: a <c>blob-*.spool</c>
    /// with no checkpoint sidecar, and a sidecar with no spool. Sealing
    /// deletes the sidecar before the upload collects the spool, and a
    /// metadata blob never writes one — so a kill in either window leaves a
    /// file <see cref="TryResume"/> cannot see and nothing referenced. The
    /// caller must own the spool directory exclusively (one writer per state
    /// directory, ADR-0028 §2), which is what makes an unpaired file garbage
    /// rather than another session's work in flight.
    /// </summary>
    /// <param name="spoolDirectory">The writer's spool directory.</param>
    /// <param name="logger">Where the count swept is recorded.</param>
    public static void SweepUnresumable(string spoolDirectory, ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolDirectory);

        if (!Directory.Exists(spoolDirectory))
        {
            return;
        }

        var swept = 0;

        foreach (var spool in Directory.EnumerateFiles(spoolDirectory, "blob-*.spool"))
        {
            if (!File.Exists(SpoolCheckpoint.PathFor(spool)))
            {
                File.Delete(spool);
                swept++;
            }
        }

        foreach (var checkpoint in Directory.EnumerateFiles(spoolDirectory, "blob-*.spool.checkpoint"))
        {
            if (!File.Exists(checkpoint[..^".checkpoint".Length]))
            {
                File.Delete(checkpoint);
                swept++;
            }
        }

        // Only when something was actually swept. A clean start does this on
        // every publication and finds nothing; recording that would be a line
        // per backup saying no work was needed.
        if (swept > 0)
        {
            Log.UnresumableSwept(logger ?? NullLogger.Instance, swept);
        }
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
    /// <para>
    /// The walk takes the lexicographically first checkpoint in the
    /// directory, which is safe only because one session owns the directory
    /// at a time — the writer role serialises processes (ADR-0028 §2), and
    /// within the service the backup enqueue gate serialises runs per set
    /// (ADR-0047 Amendment 3), so the writer pool's workers never meet in
    /// one set's spool directory.
    /// Nothing here enforces that ownership; a second concurrent session
    /// over one spool directory is a caller bug.
    /// </para>
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
        SpoolPinnedConfiguration current,
        ushort expectedFormatVersion = FormatLimits.SymmetricFormatVersion,
        ILogger? logger = null,
        ReadOnlySpan<byte> sealingPublicKey = default)
    {
        logger ??= NullLogger.Instance;

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
            // Every restart passes through here, so this is the one place the
            // reason has to be recorded. A restart is invisible from outside —
            // the job still completes and the snapshot is still correct — and
            // "why did the nightly get slower" is otherwise unanswerable.
            Log.SpoolDiscarded(logger, reason);

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

        if (checkpoint.FormatVersion != expectedFormatVersion)
        {
            return Discard("format_version_changed");
        }

        // A sealed data blob's records authenticate only under the secret the
        // checkpoint carries (ADR-0042 §3); for it, classKey is the STRUCTURE
        // (metadata) key the footer will seal under. In format 2 that secret
        // is the blob's one content key; in format 3 it is the seed each
        // record's key is expanded from. A sealed checkpoint without it is
        // unreadable state either way.
        var sealedContent =
            FormatVersions.SealsContent(checkpoint.FormatVersion, checkpoint.BlobClass == BlobClass.Data);
        if (sealedContent && checkpoint.ContentKey is null)
        {
            return Discard("content_key_missing");
        }

        var relocatable = FormatVersions.HasRelocatableRecords(checkpoint.FormatVersion);

        // A format-3 data blob seals a key into every record it appends, so a
        // writer resumed over one must be able to go on doing that. Refused
        // rather than discarded: a caller that forgot the key has damaged
        // nothing, and restarting would throw away work that is still good.
        if (relocatable && sealedContent && sealingPublicKey.IsEmpty)
        {
            throw new ArgumentException(Strings.BlobWriter_ResumeNeedsSealingKey, nameof(sealingPublicKey));
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
        //
        // The walk reads forward one record at a time and never holds the
        // file. It used to read the whole spool into one array, which put the
        // memory bound at FormatLimits.MaxBlobSize — 512 MiB, twice
        // NFR-PERF-001's agent budget, reached by nothing more exotic than a
        // crash during a large blob. No step below needs a byte it has
        // already passed, so the bound is one record plus this stream's
        // buffer (FR-ARCH-002; `SpoolCheckpointTests` holds it).
        var spoolLength = new FileInfo(spoolPath).Length;
        if (spoolLength < BlobEnvelope.Length)
        {
            return Discard("spool_shorter_than_envelope");
        }

        using var spoolRead = new FileStream(
            spoolPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);

        // A restart from here on deletes the spool this handle still holds,
        // and Windows refuses to delete a file open without FileShare.Delete:
        // the restart would throw where it should start the blob over. Every
        // one closes the reader first, as the append handle below does before
        // it claims the file.
        ResumeResult.MustRestart DiscardOpen(string reason)
        {
            spoolRead.Dispose();
            return Discard(reason);
        }

        // Enough for the longest envelope shape; Parse refuses a v2 data
        // envelope that is shorter than its sealed key, exactly as it did
        // when the whole file was in hand.
        var prefix = new byte[(int)Math.Min(BlobEnvelope.MaxLength, spoolLength)];
        spoolRead.ReadExactly(prefix);

        BlobEnvelope envelope;
        try
        {
            envelope = BlobEnvelope.Parse(prefix);
        }
        catch (BlobFormatException)
        {
            return DiscardOpen("envelope_unreadable");
        }

        if (envelope.BlobId != checkpoint.BlobId ||
            envelope.BlobCounter != checkpoint.BlobCounter ||
            envelope.WriterId != checkpoint.WriterId ||
            envelope.KeyGeneration != checkpoint.KeyGeneration ||
            envelope.BlobClass != checkpoint.BlobClass ||
            envelope.FormatVersion != checkpoint.FormatVersion ||
            !envelope.BlobSalt.SequenceEqual(checkpoint.BlobSalt.Span))
        {
            return DiscardOpen("envelope_checkpoint_mismatch");
        }

        // Derived before the walk rather than after it: the key is now walk
        // input, because authenticating a record is what proves it. For a
        // sealed blob the walk key is the checkpointed content key — the
        // authentication that follows is also what proves the checkpoint and
        // the spool belong together — while the derived key seals the footer.
        var blobKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, envelope.BlobSalt, envelope.WriterId, envelope.BlobCounter, blobKey);
        var contentKey = sealedContent ? checkpoint.ContentKey!.Value.ToArray() : null;
        var walkKey = contentKey ?? blobKey;

        // Format 3 has no blob-wide walk key: each record's is expanded from
        // the class key or from the seed, exactly as the appends that wrote
        // them did (03 §5.4), so the walk re-derives per record.
        var recordClassKey = relocatable && !sealedContent ? classKey.ToArray() : null;
        var recordPrefixLength = RecordFraming.PrefixLength(envelope.FormatVersion, envelope.BlobClass);

        // Accumulated as the walk goes, then adopted by the writer below: a
        // streaming walk cannot hand the bytes back afterwards, and re-reading
        // the spool to hash it would spend the I/O this change saves.
        var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var merkle = new BlobMerkleAccumulator();
        digest.AppendData(prefix.AsSpan(0, envelope.EnvelopeLength));
        merkle.Append(prefix.AsSpan(0, envelope.EnvelopeLength));
        spoolRead.Position = envelope.EnvelopeLength;

        var entries = new List<RecordTableEntry>();
        var offset = (long)envelope.EnvelopeLength;
        var headerBytes = new byte[RecordHeader.Length];
        var sealedBytes = Array.Empty<byte>();
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
            if (contentKey is not null)
            {
                CryptographicOperations.ZeroMemory(contentKey);
            }

            if (recordClassKey is not null)
            {
                CryptographicOperations.ZeroMemory(recordClassKey);
            }

            CryptographicOperations.ZeroMemory(scratch);
            CryptographicOperations.ZeroMemory(sealedBytes);
            digest.Dispose();
            merkle.Dispose();
            return DiscardOpen("spool_tail_unauthenticated");
        }

        Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
        Span<byte> aad = stackalloc byte[RecordAad.Length];
        Span<byte> recordKey = stackalloc byte[RecordKeyDeriver.RecordKeyLength];

        while (offset < spoolLength)
        {
            if (offset + RecordHeader.Length > spoolLength)
            {
                return DiscardTail();
            }

            spoolRead.ReadExactly(headerBytes);

            RecordHeader header;
            try
            {
                header = RecordHeader.Parse(headerBytes);
            }
            catch (RecordFormatException)
            {
                return DiscardTail();
            }

            // Ordinals are dense and ascending in every format (05 §3.1,
            // 04 §2.1). Under format 2 that is also a nonce rule — the
            // ordinal IS the nonce, so a gap would name a nonce this key
            // never covered; under format 3 the nonce is carried and the
            // density is the table's invariant alone. Held in both, because
            // a footer whose entries are not dense is damage either way.
            if (header.Ordinal != entries.Count)
            {
                return DiscardTail();
            }

            // Parse already refused a stored_length past the 64 MiB limit;
            // this bounds the record against the file before allocating.
            var recordLength = RecordFraming.RecordLength(
                envelope.FormatVersion, envelope.BlobClass, header.StoredLength);
            if (offset + recordLength > spoolLength)
            {
                return DiscardTail();
            }

            var storedLength = (int)header.StoredLength;
            var bodyLength = recordPrefixLength + storedLength + RecordCipher.TagLength;
            if (sealedBytes.Length < bodyLength)
            {
                sealedBytes = new byte[bodyLength];
            }

            if (scratch.Length < storedLength)
            {
                scratch = new byte[storedLength];
            }

            spoolRead.ReadExactly(sealedBytes.AsSpan(0, bodyLength));

            scoped ReadOnlySpan<byte> aadForRecord;
            scoped ReadOnlySpan<byte> keyForRecord;

            if (recordPrefixLength == 0)
            {
                RecordNonce.Write(header.Ordinal, nonce);
                RecordAad.Write(
                    repositoryId, envelope.FormatVersion, header.ObjectType, header.ObjectId, header.Ordinal, aad);
                aadForRecord = aad;
                keyForRecord = walkKey;
            }
            else
            {
                // The prefix the append wrote is the walk's input: the nonce
                // it drew, read back rather than recomputed, and — for a data
                // record — a sealed share this writer does not open, because
                // the seed reproduces the same key directly (05 §6.2).
                sealedBytes.AsSpan(RecordFraming.NonceOffset, RecordNonce.AesGcmLength).CopyTo(nonce);
                RecordAad.WriteRelocatable(
                    repositoryId, envelope.FormatVersion, header.ObjectType, header.ObjectId,
                    aad[..RecordAad.RelocatableLength]);
                aadForRecord = aad[..RecordAad.RelocatableLength];

                if (recordClassKey is not null)
                {
                    RecordKeyDeriver.Derive(recordClassKey, header.ObjectType, header.ObjectId, recordKey);
                }
                else
                {
                    RecordKeyDeriver.DeriveFromSeed(contentKey!, header.ObjectId, recordKey);
                }

                keyForRecord = recordKey;
            }

            // The AAD binds the repository and the object — and, in format 2,
            // the ordinal — so this also refuses a spool belonging to another
            // repository (04 §4). What it no longer refuses under format 3 is
            // a record at a different ordinal, which is the point: the
            // density check above is what holds the table together there.
            if (!RecordCipher.TryOpen(
                    keyForRecord,
                    nonce,
                    aadForRecord,
                    sealedBytes.AsSpan(recordPrefixLength, storedLength),
                    sealedBytes.AsSpan(recordPrefixLength + storedLength, RecordCipher.TagLength),
                    scratch.AsSpan(0, storedLength)))
            {
                return DiscardTail();
            }

            // Hashed only once the tag has passed, so the digest covers
            // exactly the bytes the walk accepted.
            digest.AppendData(headerBytes);
            digest.AppendData(sealedBytes.AsSpan(0, bodyLength));
            merkle.Append(headerBytes);
            merkle.Append(sealedBytes.AsSpan(0, bodyLength));

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
        // and does not outlive the walk. The ciphertext buffer goes with it —
        // it is the one place a whole record still sits in memory.
        CryptographicOperations.ZeroMemory(scratch);
        CryptographicOperations.ZeroMemory(sealedBytes);
        CryptographicOperations.ZeroMemory(recordKey);

        // Released before the append handle below claims the file: that one
        // takes FileShare.None, which this read handle would refuse.
        spoolRead.Dispose();

        // Nothing is truncated. The walk either consumed the file exactly —
        // every record bounded within it — or it restarted.
        var spool = new FileStream(spoolPath, FileMode.Open, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
        spool.Seek(0, SeekOrigin.End);

        var writer = new BlobWriter(
            envelope, profile, encryptionProfile, repositoryId, blobKey, spoolPath, spool, current, contentKey, logger,
            digest,
            merkle,
            recordClassKey,
            relocatable && sealedContent ? sealingPublicKey.ToArray() : null);
        writer._entries.AddRange(entries);
        writer.CurrentLength = spoolLength;

        Log.SpoolResumed(logger, envelope.BlobId, entries.Count, spoolLength);

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

        // Through RecordFraming so the bound counts a format-3 record's
        // prefix: a blob sized as though its records were bare would pass
        // this check and then exceed the profile's maximum by twelve bytes a
        // record, or ninety-two in the sealed data plane.
        var recordSize = RecordFraming.RecordLength(_envelope.FormatVersion, _envelope.BlobClass, (uint)storedLength);
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
            throw new InvalidOperationException(Strings.BlobWriter_RecordDoesNotFitBlob);
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

        var prefixLength = RecordFraming.PrefixLength(_envelope.FormatVersion, _envelope.BlobClass);
        var record = new byte[RecordHeader.Length + prefixLength + storedPayload.Length + RecordCipher.TagLength];
        header.WriteTo(record.AsSpan(0, RecordHeader.Length));

        var ciphertext = record.AsSpan(RecordHeader.Length + prefixLength, storedPayload.Length);
        var tag = record.AsSpan(
            RecordHeader.Length + prefixLength + storedPayload.Length, RecordCipher.TagLength);

        if (prefixLength == 0)
        {
            Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
            RecordNonce.Write(ordinal, nonce);
            Span<byte> aad = stackalloc byte[RecordAad.Length];
            RecordAad.Write(_repositoryId, _envelope.FormatVersion, objectType, objectId, ordinal, aad);

            RecordCipher.Seal(_cipher!, nonce, aad, storedPayload.Span, ciphertext, tag);
        }
        else
        {
            // Format 3. The nonce is drawn per record and carried in the
            // prefix rather than being the ordinal, and the key is the
            // object's rather than the blob's — which together are the whole
            // of what lets these bytes be copied into another blob and still
            // open there (ADR-0052 Amendment 1, 04 §3). A data record's key
            // is sealed after the nonce, so the share travels with it too.
            var nonce = record.AsSpan(RecordHeader.Length + RecordFraming.NonceOffset, RecordNonce.AesGcmLength);
            RecordNonce.DrawRandom(nonce);

            Span<byte> aad = stackalloc byte[RecordAad.RelocatableLength];
            RecordAad.WriteRelocatable(_repositoryId, _envelope.FormatVersion, objectType, objectId, aad);

            Span<byte> recordKey = stackalloc byte[RecordKeyDeriver.RecordKeyLength];
            try
            {
                DeriveRecordKey(objectType, objectId, recordKey);

                if (_recordSealingPublicKey is not null)
                {
                    SealedRecordKey.Seal(_recordSealingPublicKey, recordKey, _repositoryId, objectId)
                        .CopyTo(record.AsSpan(
                            RecordHeader.Length + RecordFraming.SealedKeyOffset, SealedRecordKey.SealedLength));
                }

                RecordCipher.Seal(recordKey, nonce, aad, storedPayload.Span, ciphertext, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recordKey);
            }
        }

        await _spool.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        _digest.AppendData(record);
        _merkle.Append(record);

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
    /// Copies one already-sealed format-3 record into this blob verbatim,
    /// re-framing only its 54-byte header with the ordinal it now carries —
    /// the relocation primitive a compactor is built from
    /// ([ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md) §4).
    /// </summary>
    /// <remarks>
    /// The sealed bytes are never opened, and this writer holds no key that
    /// would open them: the record's key is its object's and its nonce
    /// travels in its prefix, so it authenticates here exactly as it did
    /// where it came from. That is the point of format 3, and it is why a
    /// keyless compactor becomes possible — though none is built.
    /// </remarks>
    /// <param name="source">The source blob's record-table entry.</param>
    /// <param name="sealedRecord">
    /// Everything after the record's header — prefix, ciphertext and tag —
    /// exactly as the source blob holds it.
    /// </param>
    /// <param name="cancellationToken">Cancels the spool write.</param>
    /// <returns>The ordinal the record now carries in this blob.</returns>
    /// <exception cref="InvalidOperationException">The writer is sealed, the record does not fit, or this blob is not format 3.</exception>
    /// <exception cref="ArgumentException">The bytes are not the length the entry declares, or name a profile this writer does not implement.</exception>
    public async ValueTask<uint> AppendSealedRecordAsync(
        RecordTableEntry source,
        ReadOnlyMemory<byte> sealedRecord,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfDisposed(_sealed, nameof(BlobWriter));

        if (!FormatVersions.HasRelocatableRecords(_envelope.FormatVersion))
        {
            throw new InvalidOperationException(Strings.BlobWriter_RelocationNeedsFormatThree);
        }

        if (!CompressionProfile.TryFromValue(source.CompressionProfileValue, out var compressionProfile) ||
            !EncryptionProfile.TryFromValue(source.EncryptionProfileValue, out var encryptionProfile) ||
            encryptionProfile != EncryptionProfile.Aes256GcmV1)
        {
            throw new ArgumentException(Strings.BlobWriter_RelocatedProfileUnsupported, nameof(source));
        }

        var prefixLength = RecordFraming.PrefixLength(_envelope.FormatVersion, _envelope.BlobClass);
        var expected = prefixLength + (int)source.StoredLength + RecordCipher.TagLength;
        if (sealedRecord.Length != expected)
        {
            throw new ArgumentException(
                Strings.FormatBlobWriter_RelocatedRecordLength(sealedRecord.Length, expected), nameof(sealedRecord));
        }

        if (!CanAppend((int)source.StoredLength))
        {
            throw new InvalidOperationException(Strings.BlobWriter_RecordDoesNotFitBlob);
        }

        // Everything but the ordinal is the source record's, because
        // everything but the ordinal is bound into the AAD the sealed bytes
        // were made under (04 §4). The ordinal is not, which is exactly why
        // it may be renumbered here.
        var ordinal = (uint)_entries.Count;
        var header = new RecordHeader(
            source.ObjectType,
            compressionProfile!,
            encryptionProfile,
            ordinal,
            source.LogicalLength,
            source.StoredLength,
            source.ObjectId);

        var record = new byte[RecordHeader.Length + sealedRecord.Length];
        header.WriteTo(record.AsSpan(0, RecordHeader.Length));
        sealedRecord.Span.CopyTo(record.AsSpan(RecordHeader.Length));

        await _spool.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        _digest.AppendData(record);
        _merkle.Append(record);

        _entries.Add(new RecordTableEntry(
            source.ObjectId,
            ordinal,
            (ulong)CurrentLength,
            source.StoredLength,
            source.LogicalLength,
            source.CompressionProfileValue,
            source.EncryptionProfileValue,
            source.ObjectType));

        CurrentLength += record.Length;
        return ordinal;
    }

    // Which of format 3's two derivations a record takes is the blob's
    // question and not the record's (03 §5.4): a structure-plane record
    // expands the class key over its own identity, so any reader holding the
    // class key reaches it, while a sealed data record expands the blob's
    // seed — a key no reader ever derives, because every reader opens the
    // share the writer sealed from it instead.
    private void DeriveRecordKey(ObjectType objectType, ObjectId objectId, Span<byte> destination)
    {
        if (_recordClassKey is not null)
        {
            RecordKeyDeriver.Derive(_recordClassKey, objectType, objectId, destination);
        }
        else
        {
            RecordKeyDeriver.DeriveFromSeed(_contentKey!, objectId, destination);
        }
    }

    /// <summary>
    /// Releases handles and key material <b>without</b> deleting the spool or
    /// its checkpoint — the orderly-shutdown and crash-simulation path. The
    /// spool stays on disk for <see cref="TryResume"/>; a partial spool is
    /// still never uploaded (specification 05 §6.3).
    /// </summary>
    /// <remarks>
    /// Never throws for state reasons: the caller is an unwind that may
    /// already be propagating the exception that interrupted the session,
    /// and an abandon that threw would replace it — a cancelled job would
    /// then report a failure instead of its cancellation. A writer whose
    /// seal was interrupted mid-write arrives here marked sealed with the
    /// spool still open; what it leaves on disk is judged by the next
    /// session's resume walk, whose authentication restarts on doubt
    /// (05 §6.2) — nothing this unwind could decide.
    /// </remarks>
    public async ValueTask AbandonAsync()
    {
        if (_abandoned)
        {
            return;
        }

        _abandoned = true;

        DisposeCiphers();
        _digest.Dispose();
        _merkle.Dispose();

        if (!_spoolClosed)
        {
            // The flush is best-effort: abandon runs while a failure is
            // already unwinding, and the resume walk authenticates whatever
            // reached the disk, restarting on doubt (05 §6.2) — but the
            // handle release is not optional. A spool left open under
            // FileShare.None turns every later run's resume read into a
            // sharing violation for the life of the process.
            try
            {
                _spool.Flush(flushToDisk: true);
            }
            catch (IOException)
            {
                // Whatever did not reach the disk is the resume walk's to judge.
            }
            finally
            {
                _spoolClosed = true;
                try
                {
                    await _spool.DisposeAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The close-time flush can fail for the same reasons; the
                    // stream still closes its handle in its own unwind.
                }
            }
        }
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
            _pinned!,
            _contentKey?.ToArray());

        // Written once, at create. Every field is fixed for the blob's life
        // (05 §6.2), so there is nothing to keep current. Still replaced
        // atomically: a crash mid-write leaves no torn sidecar.
        var checkpointPath = SpoolCheckpoint.PathFor(_spoolPath);
        var temporary = checkpointPath + ".tmp";
        File.WriteAllBytes(temporary, checkpoint.Serialize());
        File.Move(temporary, checkpointPath, overwrite: true);

        Log.SpoolCheckpointed(_log, _envelope.BlobId);
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

        // The structure plane's cipher: identical to the record cipher for
        // every blob except a sealed v2 data blob, whose footer stays
        // readable to the write bundle while its records do not.
        RecordCipher.Seal(
            _footerCipher,
            nonce,
            aad,
            table,
            footer.AsSpan(BlobFooter.HeaderLength, table.Length),
            footer.AsSpan(BlobFooter.HeaderLength + table.Length, RecordCipher.TagLength));

        await _spool.WriteAsync(footer, cancellationToken).ConfigureAwait(false);
        _digest.AppendData(footer);
        _merkle.Append(footer);
        var footerOffset = (ulong)CurrentLength;
        CurrentLength += footer.Length;

        // The digest covers every sealed byte before the locator — the
        // locator's own prefix cannot be part of its preimage (see
        // FooterLocator's erratum note).
        var digest = new byte[32];
        _digest.GetHashAndReset(digest);
        var merkleRoot = _merkle.GetRootAndReset();

        var locatorBytes = new byte[FooterLocator.Length];
        new FooterLocator(footerOffset, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest))
            .WriteTo(locatorBytes);
        await _spool.WriteAsync(locatorBytes, cancellationToken).ConfigureAwait(false);
        CurrentLength += FooterLocator.Length;

        _spool.Flush(flushToDisk: true);
        await _spool.DisposeAsync().ConfigureAwait(false);
        _spoolClosed = true;
        DisposeCiphers();

        // A sealed blob is no longer resumable state; the sidecar goes.
        if (_pinned is not null)
        {
            File.Delete(SpoolCheckpoint.PathFor(_spoolPath));
        }

        Log.BlobSealed(
            _log, _envelope.BlobClass, _envelope.BlobId, _entries.Count, CurrentLength,
            _envelope.FormatVersion);

        return new SealedBlob(
            _spoolPath, _envelope.BlobId, _envelope.BlobClass, _envelope.BlobCounter, CurrentLength, digest,
            merkleRoot, _entries);
    }

    /// <summary>
    /// Zeroes key material and, when the writer was abandoned before
    /// sealing, discards the partial spool — a partial spool is never
    /// uploaded under any circumstances (specification 05 §6.3).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        DisposeCiphers();

        if (_abandoned)
        {
            return;
        }

        _digest.Dispose();
        _merkle.Dispose();

        // A writer whose seal was interrupted mid-write is marked sealed
        // with the spool handle still open — closed here, its file left on
        // disk for the resume walk to judge, exactly as AbandonAsync leaves
        // it. Only a never-sealed spool is discarded below.
        if (!_spoolClosed)
        {
            _spoolClosed = true;
            await _spool.DisposeAsync().ConfigureAwait(false);
        }

        if (!_sealed)
        {
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

    private void DisposeCiphers()
    {
        _cipher?.Dispose();
        if (!ReferenceEquals(_footerCipher, _cipher))
        {
            _footerCipher.Dispose();
        }

        CryptographicOperations.ZeroMemory(_blobKey);
        if (_contentKey is not null)
        {
            CryptographicOperations.ZeroMemory(_contentKey);
        }

        if (_recordClassKey is not null)
        {
            CryptographicOperations.ZeroMemory(_recordClassKey);
        }
    }
}

using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// The sealed v2 data blob (ADR-0042 §2, §3; FR-WOR-001, FR-WOR-003):
/// records under a random content key only the derived scalar recovers, the
/// footer on the structure plane so a write-only holder opens the record
/// table with the metadata key alone, honest <c>ContentSealed</c> refusals
/// without a grant, and spool resume under the checkpointed content key.
/// </summary>
[TestClass]
public sealed class SealedBlobTests : IDisposable
{
    private static readonly RepositoryId Repo = RepositoryId.FromBytes(Convert.FromHexString("1112131415161718191a1b1c1d1e1f20"));
    private static readonly WriterId Writer = WriterId.FromBytes(Convert.FromHexString("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"));
    private static readonly byte[] ContentIdKey = new byte[32];

    private static readonly Argon2Parameters TinyParameters =
        new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fbp-sealed-blob-tests", Guid.NewGuid().ToString("n"));

    private string SpoolDirectory => Path.Combine(_root, "spool");

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private static RepositoryReadAuthority DeriveAuthority(string passphraseText)
    {
        using var passphrase = Passphrase.Create(passphraseText);
        var salt = Enumerable.Repeat((byte)0x5A, KekDerivation.SaltLength).ToArray();
        return WriteOnlyDerivation.Derive(
            passphrase, TinyParameters, salt, KdfValidationMode.OpenRepository);
    }

    /// <summary>
    /// The write-only holder's key view: the metadata class key, and a loud
    /// refusal if anything asks for a data key — a sealed blob's structure
    /// must never need one.
    /// </summary>
    private static Func<BlobClass, KeyGeneration, byte[]> StructureKeys(RepositoryWriteCredential credential) =>
        (blobClass, generation) => blobClass == BlobClass.Metadata
            ? credential.DeriveMetadataKey(generation)
            : throw new InvalidOperationException("a write-only holder was asked for a data key");

    private static Func<BlobEnvelope, byte[]> Grant(RepositoryReadAuthority authority) =>
        envelope => SealedContentKey.Open(
            authority.SealingPrivateKey, envelope.SealedContentKey, Repo, envelope.BlobId);

    private static ObjectId IdFor(byte[] plaintext, ObjectIdDeriver deriver) =>
        deriver.Derive(ObjectType.SegmentRecord, ContentHasher.Hash(plaintext));

    private BlobWriter CreateSealedWriter(
        RepositoryReadAuthority authority, ulong counter = 7, SpoolPinnedConfiguration? pinned = null)
    {
        var structureKey = authority.Credential.DeriveMetadataKey(KeyGeneration.Zero);
        try
        {
            return BlobWriter.CreateSealed(
                Repo,
                Writer,
                KeyGeneration.Zero,
                structureKey,
                authority.Credential.SealingPublicKey,
                counter,
                EncryptionProfile.Aes256GcmV1,
                BlobWriteProfile.LocalDefault,
                SpoolDirectory,
                pinned: pinned);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(structureKey);
        }
    }

    private async Task<(ObjectKey Key, long Length, List<byte[]> Payloads)> WriteAndUploadAsync(
        LocalFileSystemObjectStore store, RepositoryReadAuthority authority, int recordCount)
    {
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        await using var writer = CreateSealedWriter(authority);
        var payloads = new List<byte[]>();

        for (var i = 0; i < recordCount; i++)
        {
            var payload = Enumerable.Repeat((byte)(i + 0x21), 700 + i).ToArray();
            payloads.Add(payload);
            await writer.AppendRecordAsync(
                ObjectType.SegmentRecord, IdFor(payload, deriver), CompressionProfile.None,
                (ulong)payload.Length, payload, CancellationToken.None);
        }

        await using var sealedBlob = await writer.SealAsync(CancellationToken.None);
        using var keyDeriver = new StoreBlobKeyDeriver(new byte[32]);
        var key = BlobStoreKeys.ForBlob(sealedBlob.BlobClass, keyDeriver.Derive(sealedBlob.BlobId));
        var put = await store.PutAsync(key, sealedBlob.OpenContentAsync, PutConditions.IfNotExists, CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);

        return (key, sealedBlob.Length, payloads);
    }

    [TestMethod]
    public async Task SealedBlob_OpenedWithAGrant_RoundTripsEveryRecord()
    {
        using var authority = DeriveAuthority("the write-only passphrase!!");
        var store = CreateStore();
        var (key, length, payloads) = await WriteAndUploadAsync(store, authority, recordCount: 4);

        using var deriver = new ObjectIdDeriver(ContentIdKey);
        using var reader = await BlobReader.OpenAsync(
            store, key, length, Repo, StructureKeys(authority.Credential), deriver,
            CancellationToken.None, Grant(authority));

        // The envelope says what it is, the structure opened under the
        // metadata key alone (the provider throws on a data-key ask), and
        // every record — content key unsealed by the grant — round-trips
        // through 04 §6 step 7 included.
        Assert.AreEqual(FormatLimits.SealedFormatVersion, reader.Envelope.FormatVersion);
        Assert.AreEqual(FallbackPlan.Repository.Crypto.ContentSealing.SealedLength, reader.Envelope.SealedContentKey.Length);
        Assert.AreEqual(payloads.Count, reader.RecordTable.Count);

        foreach (var (entry, expected) in reader.RecordTable.Zip(payloads))
        {
            var result = await reader.ReadRecordAsync(entry, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, result.Outcome);
            SequenceAssert.AreEqual(expected, result.Plaintext);
        }
    }

    [TestMethod]
    public async Task SealedBlob_OpenedWithoutAGrant_ServesStructureAndRefusesContentByName()
    {
        using var authority = DeriveAuthority("the write-only passphrase!!");
        var store = CreateStore();
        var (key, length, payloads) = await WriteAndUploadAsync(store, authority, recordCount: 3);

        using var deriver = new ObjectIdDeriver(ContentIdKey);
        using var reader = await BlobReader.OpenAsync(
            store, key, length, Repo, StructureKeys(authority.Credential), deriver, CancellationToken.None);

        // FR-WOR-003's split, at the blob: the record table is whole — the
        // write-only hub can rebuild, verify structure, sweep — and every
        // content read says exactly why it cannot happen, as a stated
        // refusal rather than a damage finding.
        Assert.AreEqual(payloads.Count, reader.RecordTable.Count);
        var refusal = await reader.ReadRecordAsync(reader.RecordTable[0], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.ContentSealed, refusal.Outcome);
        Assert.Contains("restore grant", refusal.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SealedBlob_TheWrongAuthority_IsRefusedIndistinguishably()
    {
        using var authority = DeriveAuthority("the write-only passphrase!!");
        using var wrong = DeriveAuthority("an entirely different secret!");
        var store = CreateStore();
        var (key, length, _) = await WriteAndUploadAsync(store, authority, recordCount: 1);

        // Wrong authority and tampered share land in the SAME typed refusal —
        // BlobFormatException, the shape every skip path contains — because
        // callers prove the authority against the descriptor before an
        // opener ever exists, so at this level the two are one damage class.
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var refusal = await Assert.ThrowsExactlyAsync<BlobFormatException>(async () =>
        {
            using var reader = await BlobReader.OpenAsync(
                store, key, length, Repo, StructureKeys(authority.Credential), deriver,
                CancellationToken.None, Grant(wrong));
        });
        Assert.Contains("does not open", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SealedBlob_ATamperedSealedShare_IsOneBlobsDamageNotALoadFailure()
    {
        using var authority = DeriveAuthority("the write-only passphrase!!");
        var store = CreateStore();
        var (key, length, _) = await WriteAndUploadAsync(store, authority, recordCount: 2);

        // Flip one byte inside the stored envelope's 80-byte sealed share —
        // the wrapped-content-key half, offset 88 (v1 fields) + 32
        // (ephemeral share) + 5 into the blob.
        var path = Path.Combine(_root, "store", key.Value.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[88 + 32 + 5] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes);

        using var deriver = new ObjectIdDeriver(ContentIdKey);

        // Under a VALID grant the refusal is this blob's own damage — the
        // typed BlobFormatException that LoadBlobsAsync demotes to ONE
        // SkippedBlob — never a SealedContentException that would abort the
        // whole load (ADR-0042 §7).
        var refusal = await Assert.ThrowsExactlyAsync<BlobFormatException>(async () =>
        {
            using var reader = await BlobReader.OpenAsync(
                store, key, length, Repo, StructureKeys(authority.Credential), deriver,
                CancellationToken.None, Grant(authority));
        });
        Assert.Contains("does not open", refusal.Message, StringComparison.Ordinal);

        // The structure plane never touches the share: a write-only holder
        // still reads the tampered blob's whole record table.
        using var structural = await BlobReader.OpenAsync(
            store, key, length, Repo, StructureKeys(authority.Credential), deriver, CancellationToken.None);
        Assert.AreEqual(2, structural.RecordTable.Count);
    }

    [TestMethod]
    public async Task SealedBlob_AnInterruptedSpool_ResumesUnderTheCheckpointedContentKey()
    {
        using var authority = DeriveAuthority("the write-only passphrase!!");
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var pinned = new SpoolPinnedConfiguration(
            1, 65_536, 0, 0, CompressionProfile.None.Value, "none", EncryptionProfile.Aes256GcmV1.Value);

        var first = Enumerable.Repeat((byte)0x31, 800).ToArray();
        var second = Enumerable.Repeat((byte)0x32, 900).ToArray();
        var writer = CreateSealedWriter(authority, pinned: pinned);
        await writer.AppendRecordAsync(
            ObjectType.SegmentRecord, IdFor(first, deriver), CompressionProfile.None,
            (ulong)first.Length, first, CancellationToken.None);
        await writer.AppendRecordAsync(
            ObjectType.SegmentRecord, IdFor(second, deriver), CompressionProfile.None,
            (ulong)second.Length, second, CancellationToken.None);
        await writer.AbandonAsync();
        await writer.DisposeAsync();

        // A build expecting v1 spools restarts — the pinned version is a
        // pinned field like any other…
        var structureKey = authority.Credential.DeriveMetadataKey(KeyGeneration.Zero);
        var mismatched = BlobWriter.TryResume(
            SpoolDirectory, Repo, Writer, KeyGeneration.Zero, BlobClass.Data, structureKey,
            EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, pinned);
        Assert.IsInstanceOfType<ResumeResult.MustRestart>(mismatched, out var restart);
        Assert.AreEqual("format_version_changed", restart.Reason);

        // …so the spool is gone and a fresh sealed writer starts clean; the
        // resume walk under the right expectation is proven by writing the
        // whole blob again, interrupting, and resuming with version 2.
        writer = CreateSealedWriter(authority, counter: 8, pinned: pinned);
        await writer.AppendRecordAsync(
            ObjectType.SegmentRecord, IdFor(first, deriver), CompressionProfile.None,
            (ulong)first.Length, first, CancellationToken.None);
        await writer.AppendRecordAsync(
            ObjectType.SegmentRecord, IdFor(second, deriver), CompressionProfile.None,
            (ulong)second.Length, second, CancellationToken.None);
        await writer.AbandonAsync();
        await writer.DisposeAsync();

        var resumed = BlobWriter.TryResume(
            SpoolDirectory, Repo, Writer, KeyGeneration.Zero, BlobClass.Data, structureKey,
            EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, pinned,
            expectedFormatVersion: FormatLimits.SealedFormatVersion);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(structureKey);
        Assert.IsInstanceOfType<ResumeResult.Resumed>(resumed, out var outcome);

        // The resumed writer holds both authenticated records, continues at
        // the next ordinal, and the sealed result reads back whole under a
        // grant — the record the resumed session appended included.
        await using var resumedWriter = outcome.Writer;
        Assert.AreEqual(2, resumedWriter.RecordCount);
        var third = Enumerable.Repeat((byte)0x33, 1_000).ToArray();
        await resumedWriter.AppendRecordAsync(
            ObjectType.SegmentRecord, IdFor(third, deriver), CompressionProfile.None,
            (ulong)third.Length, third, CancellationToken.None);

        await using var sealedBlob = await resumedWriter.SealAsync(CancellationToken.None);
        var store = CreateStore();
        using var keyDeriver = new StoreBlobKeyDeriver(new byte[32]);
        var key = BlobStoreKeys.ForBlob(sealedBlob.BlobClass, keyDeriver.Derive(sealedBlob.BlobId));
        await store.PutAsync(key, sealedBlob.OpenContentAsync, PutConditions.IfNotExists, CancellationToken.None);

        using var reader = await BlobReader.OpenAsync(
            store, key, sealedBlob.Length, Repo, StructureKeys(authority.Credential), deriver,
            CancellationToken.None, Grant(authority));
        Assert.AreEqual(3, reader.RecordTable.Count);
        var payloads = new[] { first, second, third };
        foreach (var (entry, expected) in reader.RecordTable.Zip(payloads))
        {
            var result = await reader.ReadRecordAsync(entry, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, result.Outcome);
            SequenceAssert.AreEqual(expected, result.Plaintext);
        }
    }

    [TestMethod]
    public async Task SealedBlob_AShareSealedForAnotherBlob_IsRefusedThroughThePackingLayer()
    {
        using var authority = DeriveAuthority("the write-only passphrase!!");
        var store = CreateStore();
        var (key, length, _) = await WriteAndUploadAsync(store, authority, recordCount: 1);

        // A perfectly valid share — right public key, wrong blob identity —
        // spliced over the stored one. The AAD (repository_id ‖ blob_id,
        // 05 §2.1) is what refuses the transplant, proven through the
        // packing layer rather than only at the ContentSealing unit.
        var foreignShare = SealedContentKey.Seal(
            authority.Credential.SealingPublicKey,
            Enumerable.Repeat((byte)0x66, 32).ToArray(),
            Repo,
            BlobId.FromWriterCounter(Writer, 999));

        var path = Path.Combine(_root, "store", key.Value.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        foreignShare.CopyTo(bytes.AsSpan(88, 80));
        await File.WriteAllBytesAsync(path, bytes);

        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var refusal = await Assert.ThrowsExactlyAsync<BlobFormatException>(async () =>
        {
            using var reader = await BlobReader.OpenAsync(
                store, key, length, Repo, StructureKeys(authority.Credential), deriver,
                CancellationToken.None, Grant(authority));
        });
        Assert.Contains("does not open", refusal.Message, StringComparison.Ordinal);
    }

    private static readonly SpoolPinnedConfiguration Pinned = new(
        1, 65_536, 0, 0, CompressionProfile.None.Value, "none", EncryptionProfile.Aes256GcmV1.Value);

    private async Task WriteAbandonedSealedSpoolAsync(RepositoryReadAuthority authority, ulong counter = 9)
    {
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var payload = Enumerable.Repeat((byte)0x41, 900).ToArray();
        var writer = CreateSealedWriter(authority, counter, Pinned);
        await writer.AppendRecordAsync(
            ObjectType.SegmentRecord, IdFor(payload, deriver), CompressionProfile.None,
            (ulong)payload.Length, payload, CancellationToken.None);
        await writer.AbandonAsync();
        await writer.DisposeAsync();
    }

    [TestMethod]
    public async Task SealedSpool_ARestart_SaysWhyInTheLogRatherThanOnlyInTheReturn()
    {
        // A restart is invisible from outside: the job still completes and the
        // snapshot is still correct, so the cost — lost spooled work — shows up
        // only as a slower nightly. The reason has to reach the log, or the
        // question "why did this get slower" has no answer (ADR-0043).
        using var authority = DeriveAuthority("the write-only passphrase!!");
        await WriteAbandonedSealedSpoolAsync(authority);

        var sidecar = await File.ReadAllBytesAsync(SidecarPath());
        sidecar[^1] ^= 0x01;
        await File.WriteAllBytesAsync(SidecarPath(), sidecar);

        var logger = new RecordingLogger();
        Assert.IsInstanceOfType<ResumeResult.MustRestart>(ResumeSealed(authority, logger), out var restart);

        var recorded = Assert.ContainsSingle(logger.Records);
        Assert.AreEqual(1601, recorded.EventId, "the discard carries its allocated event id");
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Warning, recorded.Level);
        Assert.AreEqual(restart.Reason, recorded.Value("Reason"), "the logged reason is the returned reason");
        Assert.Contains("checkpoint_unreadable", recorded.Message, StringComparison.Ordinal);
    }

    private string SidecarPath() => Directory.GetFiles(SpoolDirectory, "*.checkpoint").Single();

    private ResumeResult ResumeSealed(
        RepositoryReadAuthority authority, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var structureKey = authority.Credential.DeriveMetadataKey(KeyGeneration.Zero);
        try
        {
            return BlobWriter.TryResume(
                SpoolDirectory, Repo, Writer, KeyGeneration.Zero, BlobClass.Data, structureKey,
                EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, Pinned,
                expectedFormatVersion: FormatLimits.SealedFormatVersion,
                logger: logger);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(structureKey);
        }
    }

    private static void RewriteTrailingHash(byte[] sidecar) =>
        System.Security.Cryptography.SHA256.HashData(sidecar.AsSpan(0, sidecar.Length - 32))
            .CopyTo(sidecar.AsSpan(sidecar.Length - 32));

    [TestMethod]
    public async Task SealedSpool_ATornCheckpoint_Restarts()
    {
        using var authority = DeriveAuthority("the write-only passphrase!!");
        await WriteAbandonedSealedSpoolAsync(authority);

        var sidecar = await File.ReadAllBytesAsync(SidecarPath());
        sidecar[^1] ^= 0x01;
        await File.WriteAllBytesAsync(SidecarPath(), sidecar);

        Assert.IsInstanceOfType<ResumeResult.MustRestart>(ResumeSealed(authority), out var restart);
        Assert.AreEqual("checkpoint_unreadable", restart.Reason);
    }

    [TestMethod]
    public async Task SealedSpool_ATamperedContentKeyInTheCheckpoint_FailsTheTailWalkAndRestarts()
    {
        using var authority = DeriveAuthority("the write-only passphrase!!");
        await WriteAbandonedSealedSpoolAsync(authority);

        // Flip a byte INSIDE the checkpointed content key — the 32 bytes
        // just before the trailing hash — and re-stamp the hash, so the
        // sidecar parses cleanly and the deception is only caught where it
        // must be: the tail walk's record tags under the wrong key (05
        // §6.2's authenticate-the-tail proof).
        var sidecar = await File.ReadAllBytesAsync(SidecarPath());
        sidecar[^40] ^= 0x01;
        RewriteTrailingHash(sidecar);
        await File.WriteAllBytesAsync(SidecarPath(), sidecar);

        Assert.IsInstanceOfType<ResumeResult.MustRestart>(ResumeSealed(authority), out var restart);
        Assert.AreEqual("spool_tail_unauthenticated", restart.Reason);
    }

    [TestMethod]
    public async Task SealedSpool_ASidecarStrippedOfItsContentKey_IsUnreadable()
    {
        using var authority = DeriveAuthority("the write-only passphrase!!");
        await WriteAbandonedSealedSpoolAsync(authority);

        // A v2-data sidecar without its 32 content-key bytes is a shape the
        // parser computes as impossible — even with a freshly correct hash.
        var sidecar = await File.ReadAllBytesAsync(SidecarPath());
        var stripped = sidecar.AsSpan(0, sidecar.Length - 64).ToArray()
            .Concat(sidecar.AsSpan(sidecar.Length - 32).ToArray())
            .ToArray();
        RewriteTrailingHash(stripped);
        await File.WriteAllBytesAsync(SidecarPath(), stripped);

        Assert.IsInstanceOfType<ResumeResult.MustRestart>(ResumeSealed(authority), out var restart);
        Assert.AreEqual("checkpoint_unreadable", restart.Reason);
    }

    [TestMethod]
    public async Task V1Spool_MetByAV2Expectation_Restarts()
    {
        // The reverse of the sealed test's version pin: a v1 data spool
        // offered to a session that writes sealed blobs restarts rather
        // than resuming under keys it does not have.
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var dataKey = Enumerable.Repeat((byte)0x77, 32).ToArray();
        var payload = Enumerable.Repeat((byte)0x42, 700).ToArray();
        var writer = BlobWriter.Create(
            Repo, Writer, KeyGeneration.Zero, BlobClass.Data, dataKey, blobCounter: 11,
            EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, SpoolDirectory, pinned: Pinned);
        await writer.AppendRecordAsync(
            ObjectType.SegmentRecord, IdFor(payload, deriver), CompressionProfile.None,
            (ulong)payload.Length, payload, CancellationToken.None);
        await writer.AbandonAsync();
        await writer.DisposeAsync();

        using var authority = DeriveAuthority("the write-only passphrase!!");
        Assert.IsInstanceOfType<ResumeResult.MustRestart>(ResumeSealed(authority), out var restart);
        Assert.AreEqual("format_version_changed", restart.Reason);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

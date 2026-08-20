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

        using var deriver = new ObjectIdDeriver(ContentIdKey);
        await Assert.ThrowsExactlyAsync<SealedContentException>(async () =>
        {
            using var reader = await BlobReader.OpenAsync(
                store, key, length, Repo, StructureKeys(authority.Credential), deriver,
                CancellationToken.None, Grant(wrong));
        });
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

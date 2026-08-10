using System.Runtime.CompilerServices;
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
/// Exercises the blob writer and reader together against the C2 and C3
/// acceptance criteria (specification 05; FR-ARCH-012, FR-ARCH-006,
/// FR-MAN-007): every record locatable and verifiable from the blob and keys
/// alone, the footer reached in exactly two range reads, sealing triggers,
/// never-split records, and locality of corruption.
/// </summary>
[TestClass]
public sealed class BlobWriterAndReaderTests : IDisposable
{
    private static readonly RepositoryId Repo = RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));
    private static readonly WriterId Writer = WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));
    private static readonly byte[] ClassKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] ContentIdKey = new byte[32];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fbp-packing-tests", Guid.NewGuid().ToString("n"));

    private string SpoolDirectory => Path.Combine(_root, "spool");

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private BlobWriter CreateWriter(BlobWriteProfile? profile = null, ulong counter = 42) => BlobWriter.Create(
        Repo,
        Writer,
        KeyGeneration.Zero,
        BlobClass.Data,
        ClassKey,
        counter,
        EncryptionProfile.Aes256GcmV1,
        profile ?? BlobWriteProfile.LocalDefault,
        SpoolDirectory);

    private static ObjectId IdFor(byte[] plaintext, ObjectIdDeriver deriver) =>
        deriver.Derive(ObjectType.SegmentRecord, ContentHasher.Hash(plaintext));

    private async Task<(ObjectKey Key, long Length, List<byte[]> Payloads)> WriteAndUploadAsync(
        LocalFileSystemObjectStore store, int recordCount)
    {
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        await using var writer = CreateWriter();
        var payloads = new List<byte[]>();

        for (var i = 0; i < recordCount; i++)
        {
            var payload = Enumerable.Repeat((byte)(i + 1), 500 + i).ToArray();
            payloads.Add(payload);
            await writer.AppendRecordAsync(
                ObjectType.SegmentRecord,
                IdFor(payload, deriver),
                CompressionProfile.None,
                (ulong)payload.Length,
                payload,
                CancellationToken.None);
        }

        await using var sealedBlob = await writer.SealAsync(CancellationToken.None);
        using var keyDeriver = new StoreBlobKeyDeriver(new byte[32]);
        var key = BlobStoreKeys.ForBlob(sealedBlob.BlobClass, keyDeriver.Derive(sealedBlob.BlobId));

        var put = await store.PutAsync(key, sealedBlob.OpenContentAsync, PutConditions.IfNotExists, CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);

        return (key, sealedBlob.Length, payloads);
    }

    [TestMethod]
    public async Task BlobWriter_TheSealIsCancelledMidWrite_CanStillBeAbandoned()
    {
        // A cancel that lands inside SealAsync leaves the writer marked
        // sealed with the spool still open. The session's unwind abandons
        // whatever writer it holds; an abandon that throws here would
        // REPLACE the OperationCanceledException the caller is propagating —
        // and a cancelled job would then report a failure instead of
        // Cancelled (ADR-0029 §4).
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var writer = CreateWriter();
        var payload = Enumerable.Repeat((byte)7, 500).ToArray();
        await writer.AppendRecordAsync(
            ObjectType.SegmentRecord,
            IdFor(payload, deriver),
            CompressionProfile.None,
            (ulong)payload.Length,
            payload,
            CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        try
        {
            await writer.SealAsync(cancellation.Token);
            Assert.Fail("the seal completed despite the cancelled token.");
        }
        catch (OperationCanceledException)
        {
        }

        await writer.AbandonAsync();

        // The half-sealed spool stays on disk for the next session's resume
        // walk to judge — authentication restarts on doubt (05 §6.2), which
        // is the safety, not anything the unwind could decide here.
        Assert.IsTrue(Directory.EnumerateFiles(SpoolDirectory, "blob-*.spool").Any());

        await writer.DisposeAsync();
    }

    [TestMethod]
    public async Task BlobReader_TheBlobAndKeysAlone_LocatesAndVerifiesEveryRecord()
    {
        // The C2 acceptance criterion, verbatim: no index, no catalogue, no
        // in-memory state from the writer — only the stored object and keys.
        var store = CreateStore();
        var (key, length, payloads) = await WriteAndUploadAsync(store, recordCount: 5);

        using var deriver = new ObjectIdDeriver(ContentIdKey);
        using var reader = await BlobReader.OpenAsync(
            store, key, length, Repo, (_, _) => ClassKey, deriver, CancellationToken.None);

        Assert.AreEqual(5, reader.RecordTable.Count);

        foreach (var (entry, expected) in reader.RecordTable.Zip(payloads))
        {
            var result = await reader.ReadRecordAsync(entry, CancellationToken.None);

            Assert.AreEqual(RecordReadOutcome.Ok, result.Outcome);
            SequenceAssert.AreEqual(expected, result.Plaintext);
        }
    }

    [TestMethod]
    public async Task BlobReader_OpeningABlob_ReachesTheFooterInExactlyTwoRangeReads()
    {
        var store = CreateStore();
        var (key, length, _) = await WriteAndUploadAsync(store, recordCount: 3);

        var counting = new RangeCountingStore(store);
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        using var reader = await BlobReader.OpenAsync(
            counting, key, length, Repo, (_, _) => ClassKey, deriver, CancellationToken.None);

        // Read one: the locator. Read two: the footer. The envelope's small
        // read for key-derivation selectors is deliberately third.
        Assert.AreEqual(3, counting.Ranges.Count);
        Assert.AreEqual((length - FooterLocator.Length, (long)FooterLocator.Length), counting.Ranges[0]);
        Assert.IsTrue(counting.Ranges[1].Offset >= BlobEnvelope.Length && counting.Ranges[1].Offset < length - FooterLocator.Length);
        Assert.AreEqual((0L, (long)BlobEnvelope.Length), counting.Ranges[2]);
    }

    [TestMethod]
    public async Task BlobReader_OneRecordIsDamaged_LeavesEveryOtherRecordReadable()
    {
        // Corruption is local (04 §7): flip one ciphertext byte of record 1.
        var store = CreateStore();
        var (key, length, payloads) = await WriteAndUploadAsync(store, recordCount: 3);

        using (var probeDeriver = new ObjectIdDeriver(ContentIdKey))
        using (var probe = await BlobReader.OpenAsync(store, key, length, Repo, (_, _) => ClassKey, probeDeriver, CancellationToken.None))
        {
            var victim = probe.RecordTable[1];
            var path = Directory.EnumerateFiles(Path.Combine(_root, "store"), "*", SearchOption.AllDirectories)
                .Single(f => !f.Contains(".fbp-tmp", StringComparison.Ordinal));
            var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None);
            bytes[(long)victim.PhysicalOffset + 54] ^= 0x01;
            await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);
        }

        using var deriver = new ObjectIdDeriver(ContentIdKey);
        using var reader = await BlobReader.OpenAsync(store, key, length, Repo, (_, _) => ClassKey, deriver, CancellationToken.None);

        var results = new List<RecordReadResult>();
        foreach (var entry in reader.RecordTable)
        {
            results.Add(await reader.ReadRecordAsync(entry, CancellationToken.None));
        }

        Assert.AreEqual(RecordReadOutcome.Ok, results[0].Outcome);
        Assert.AreEqual(RecordReadOutcome.AuthenticationFailed, results[1].Outcome);
        Assert.AreEqual(RecordReadOutcome.Ok, results[2].Outcome);
        SequenceAssert.AreEqual(payloads[0], results[0].Plaintext);
        SequenceAssert.AreEqual(payloads[2], results[2].Plaintext);
    }

    [TestMethod]
    public async Task BlobWriter_TargetSizeOrRecordCountReached_SignalsItShouldSeal()
    {
        var smallTarget = BlobWriteProfile.LocalDefault with { TargetSizeBytes = 1_000, MaximumSizeBytes = 100_000 };
        await using (var writer = CreateWriter(smallTarget, counter: 1))
        {
            Assert.IsFalse(writer.ShouldSeal);
            using var deriver = new ObjectIdDeriver(ContentIdKey);
            var payload = new byte[2_000];
            payload[0] = 1;
            await writer.AppendRecordAsync(
                ObjectType.SegmentRecord, IdFor(payload, deriver), CompressionProfile.None,
                (ulong)payload.Length, payload, CancellationToken.None);

            Assert.IsTrue(writer.ShouldSeal);
        }

        var twoRecords = BlobWriteProfile.LocalDefault with { MaximumRecordCount = 2 };
        await using (var writer = CreateWriter(twoRecords, counter: 2))
        {
            using var deriver = new ObjectIdDeriver(ContentIdKey);
            for (var i = 0; i < 2; i++)
            {
                var payload = new byte[100 + i];
                payload[0] = (byte)(i + 1);
                await writer.AppendRecordAsync(
                    ObjectType.SegmentRecord, IdFor(payload, deriver), CompressionProfile.None,
                    (ulong)payload.Length, payload, CancellationToken.None);
            }

            Assert.IsTrue(writer.ShouldSeal);
            Assert.IsFalse(writer.CanAppend(10));
        }
    }

    [TestMethod]
    public async Task BlobWriter_ARecordWouldExceedTheMaximum_IsRefusedRatherThanSplit()
    {
        var tiny = BlobWriteProfile.LocalDefault with { TargetSizeBytes = 1_000, MaximumSizeBytes = 2_000 };
        await using var writer = CreateWriter(tiny, counter: 3);
        using var deriver = new ObjectIdDeriver(ContentIdKey);

        var tooBig = new byte[5_000];
        tooBig[0] = 1;

        Assert.IsFalse(writer.CanAppend(tooBig.Length));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await writer.AppendRecordAsync(
                ObjectType.SegmentRecord, IdFor(tooBig, deriver), CompressionProfile.None,
                (ulong)tooBig.Length, tooBig, CancellationToken.None));
    }

    [TestMethod]
    public async Task BlobReader_TheLocatorIsTruncated_RefusesToOpen()
    {
        var store = CreateStore();
        var (key, length, _) = await WriteAndUploadAsync(store, recordCount: 2);

        using var deriver = new ObjectIdDeriver(ContentIdKey);

        // Opening with a length 16 bytes short models a truncated object: the
        // "locator" read lands on footer bytes and must refuse.
        await Assert.ThrowsExactlyAsync<BlobFormatException>(async () =>
            await BlobReader.OpenAsync(store, key, length - 16, Repo, (_, _) => ClassKey, deriver, CancellationToken.None));
    }

    [TestMethod]
    public async Task BlobWriter_TwoWritersSharingACounter_AreSeparatedByDistinctSalts()
    {
        await using var first = CreateWriter(counter: 7);
        await using var second = BlobWriter.Create(
            Repo, Writer, KeyGeneration.Zero, BlobClass.Data, ClassKey, 7,
            EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, SpoolDirectory + "-second");

        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var payload = "identical payload"u8.ToArray();
        var id = IdFor(payload, deriver);

        await first.AppendRecordAsync(ObjectType.SegmentRecord, id, CompressionProfile.None, (ulong)payload.Length, payload, CancellationToken.None);
        await second.AppendRecordAsync(ObjectType.SegmentRecord, id, CompressionProfile.None, (ulong)payload.Length, payload, CancellationToken.None);

        await using var sealedFirst = await first.SealAsync(CancellationToken.None);
        await using var sealedSecond = await second.SealAsync(CancellationToken.None);

        // Same key inputs except the CSPRNG salt: the sealed bytes must differ.
        Assert.AreNotEqual(sealedFirst.Digest, sealedSecond.Digest);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Counts the ranged reads an open performs.</summary>
    private sealed class RangeCountingStore(IObjectStore inner) : IObjectStore
    {
        public List<(long Offset, long Length)> Ranges { get; } = [];

        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
        {
            if (range is { } r)
            {
                Ranges.Add((r.Offset, r.Length));
            }

            return inner.OpenReadAsync(key, range, cancellationToken);
        }

        public ValueTask<PutResult> PutAsync(ObjectKey key, Func<CancellationToken, ValueTask<Stream>> openContent, PutConditions conditions, CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}

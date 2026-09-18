using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// The format-3 blob plane
/// ([ADR-0052](../../../docs/adr/0052-relocatable-records-format-v3.md)
/// Amendment 1; specification 04 §2–§4, 05 §2.2, §6.2): a record carries its
/// own nonce and — in the sealed data plane — its own sealed key, and is
/// keyed by the object rather than by the container, so the sealed bytes can
/// be copied into another blob and still open there. Establishes FR-ARCH-012
/// and FR-MAN-007 for format 3 and FR-WOR-003 for its sealed plane; the
/// relocation cases are the positive twin of the format-2 negative
/// <c>RecordCipherTests</c> keeps.
/// </summary>
/// <remarks>
/// Does not establish FR-ARCH-011: spool resume under the record-key seed is
/// exercised here, but the interruption matrix that requirement rests on is
/// <c>InterruptionTests</c>'.
/// </remarks>
[TestClass]
public sealed class RelocatableBlobTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("2122232425262728292a2b2c2d2e2f30"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("c0c1c2c3c4c5c6c7c8c9cacbcccdcecf"));

    private static readonly byte[] ClassKey = Enumerable.Range(0, 32).Select(value => (byte)(value ^ 0x5C)).ToArray();
    private static readonly byte[] ContentIdKey = new byte[32];

    private static readonly Argon2Parameters TinyParameters =
        new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

    private static readonly SpoolPinnedConfiguration Pinned =
        new(1, 65_536, 0, 0, CompressionProfile.None.Value, "none", EncryptionProfile.Aes256GcmV1.Value);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-relocatable-tests", Guid.NewGuid().ToString("n"));

    private string SpoolDirectory => Path.Combine(_root, "spool");

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private static RepositoryReadAuthority DeriveAuthority()
    {
        using var passphrase = Passphrase.Create("a relocatable repository!!");
        var salt = Enumerable.Repeat((byte)0x3C, KekDerivation.SaltLength).ToArray();
        return WriteOnlyDerivation.Derive(passphrase, TinyParameters, salt, KdfValidationMode.OpenRepository);
    }

    private static ObjectId IdFor(byte[] plaintext, ObjectIdDeriver deriver) =>
        deriver.Derive(ObjectType.SegmentRecord, ContentHasher.Hash(plaintext));

    private BlobWriter CreateMetadataWriter(ulong counter) => BlobWriter.Create(
        Repo,
        Writer,
        KeyGeneration.Zero,
        BlobClass.Metadata,
        ClassKey,
        counter,
        EncryptionProfile.Aes256GcmV1,
        BlobWriteProfile.LocalDefault,
        SpoolDirectory,
        formatVersion: FormatVersions.RelocatableRecords);

    private BlobWriter CreateDataWriter(
        RepositoryReadAuthority authority, ulong counter, SpoolPinnedConfiguration? pinned = null)
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
                pinned: pinned,
                formatVersion: FormatVersions.RelocatableRecords);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(structureKey);
        }
    }

    /// <summary>The write-only holder's key view: a data-key ask is a loud failure, as it is for format 2.</summary>
    private static Func<BlobClass, KeyGeneration, byte[]> StructureKeys(RepositoryWriteCredential credential) =>
        (blobClass, generation) => blobClass == BlobClass.Metadata
            ? credential.DeriveMetadataKey(generation)
            : throw new InvalidOperationException("a write-only holder was asked for a data key");

    private static async Task<byte[]> BytesOfAsync(SealedBlob blob)
    {
        await using var content = await blob.OpenContentAsync(CancellationToken.None);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, CancellationToken.None);
        return buffer.ToArray();
    }

    private static byte[] SealedBytesOf(byte[] blob, RecordTableEntry entry, int prefixLength) =>
        blob.AsSpan(
            (int)entry.PhysicalOffset + RecordHeader.Length,
            prefixLength + (int)entry.StoredLength + RecordCipher.TagLength).ToArray();

    private static async Task<(ObjectKey Key, long Length)> UploadAsync(LocalFileSystemObjectStore store, SealedBlob blob)
    {
        using var keyDeriver = new StoreBlobKeyDeriver(new byte[32]);
        var key = BlobStoreKeys.ForBlob(blob.BlobClass, keyDeriver.Derive(blob.BlobId));
        var put = await store.PutAsync(key, blob.OpenContentAsync, PutConditions.IfNotExists, CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);
        return (key, blob.Length);
    }

    private static async ValueTask AppendAsync(BlobWriter writer, byte[] payload, ObjectIdDeriver deriver) =>
        await writer.AppendRecordAsync(
            ObjectType.SegmentRecord,
            IdFor(payload, deriver),
            CompressionProfile.None,
            (ulong)payload.Length,
            payload,
            CancellationToken.None);

    [TestMethod]
    public async Task MetadataBlob_InFormatThree_RoundTripsUnderTheClassKeyAlone()
    {
        var store = CreateStore();
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var payloads = new[]
        {
            Enumerable.Repeat((byte)0x11, 700).ToArray(),
            Enumerable.Repeat((byte)0x22, 800).ToArray(),
        };

        await using var writer = CreateMetadataWriter(counter: 1);
        foreach (var payload in payloads)
        {
            await AppendAsync(writer, payload, deriver);
        }

        await using var blob = await writer.SealAsync(CancellationToken.None);
        var (key, length) = await UploadAsync(store, blob);

        using var readDeriver = new ObjectIdDeriver(ContentIdKey);
        using var reader = await BlobReader.OpenAsync(
            store, key, length, Repo, (_, _) => ClassKey, readDeriver, CancellationToken.None);

        Assert.AreEqual(FormatVersions.RelocatableRecords, reader.Envelope.FormatVersion);
        Assert.HasCount(2, reader.RecordTable);

        foreach (var (entry, expected) in reader.RecordTable.Zip(payloads))
        {
            var result = await reader.ReadRecordAsync(entry, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, result.Outcome);
            SequenceAssert.AreEqual(expected, result.Plaintext);
        }
    }

    [TestMethod]
    public async Task DataEnvelope_InFormatThree_CarriesNoShareBecauseEveryRecordCarriesItsOwn()
    {
        // 05 §2: a format-2 data envelope is 168 bytes because it carries one
        // sealed key for the whole blob. A format-3 one is the same 88 bytes a
        // metadata envelope is: the keys moved into the records.
        using var authority = DeriveAuthority();
        using var deriver = new ObjectIdDeriver(ContentIdKey);

        await using var writer = CreateDataWriter(authority, counter: 2);
        await AppendAsync(writer, Enumerable.Repeat((byte)0x33, 600).ToArray(), deriver);
        await using var blob = await writer.SealAsync(CancellationToken.None);

        var bytes = await BytesOfAsync(blob);
        var envelope = BlobEnvelope.Parse(bytes);

        Assert.AreEqual(FormatVersions.RelocatableRecords, envelope.FormatVersion);
        Assert.AreEqual(BlobClass.Data, envelope.BlobClass);
        Assert.AreEqual(BlobEnvelope.Length, envelope.EnvelopeLength);
        Assert.IsTrue(envelope.SealedContentKey.IsEmpty, "a format-3 data envelope carries no sealed share");
    }

    [TestMethod]
    public async Task DataBlob_InFormatThree_ReadsUnderAGrantAndRefusesHonestlyWithout()
    {
        var store = CreateStore();
        using var authority = DeriveAuthority();
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var payload = Enumerable.Repeat((byte)0x44, 900).ToArray();

        await using var writer = CreateDataWriter(authority, counter: 3);
        await AppendAsync(writer, payload, deriver);
        await using var blob = await writer.SealAsync(CancellationToken.None);
        var (key, length) = await UploadAsync(store, blob);

        using var readDeriver = new ObjectIdDeriver(ContentIdKey);
        using var grant = new SealedContentKeyOpener(authority.SealingPrivateKey, Repo);
        using var granted = await BlobReader.OpenAsync(
            store, key, length, Repo, StructureKeys(authority.Credential), readDeriver, CancellationToken.None,
            grant);

        var read = await granted.ReadRecordAsync(granted.RecordTable[0], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        SequenceAssert.AreEqual(payload, read.Plaintext);

        // FR-WOR-003's split, unchanged by the keys moving into the records:
        // the write-only holder opens the whole record table and every
        // content read says exactly why it cannot happen.
        using var structural = await BlobReader.OpenAsync(
            store, key, length, Repo, StructureKeys(authority.Credential), readDeriver, CancellationToken.None);
        Assert.HasCount(1, structural.RecordTable);
        var refusal = await structural.ReadRecordAsync(structural.RecordTable[0], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.ContentSealed, refusal.Outcome);
    }

    [TestMethod]
    public async Task MetadataRecord_CopiedIntoAnotherBlob_OpensBecauseTheKeyIsTheObjects()
    {
        // The whole point of format 3, executable: the same sealed bytes,
        // under a different blob's salt, counter and derived blob key, at a
        // different ordinal — and they open, because nothing about the
        // container entered the key, the nonce or the AAD.
        var store = CreateStore();
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var travelling = Enumerable.Repeat((byte)0x55, 750).ToArray();
        var resident = Enumerable.Repeat((byte)0x66, 640).ToArray();

        byte[] sealedRecord;
        RecordTableEntry source;
        await using (var origin = CreateMetadataWriter(counter: 10))
        {
            await AppendAsync(origin, resident, deriver);
            await AppendAsync(origin, travelling, deriver);
            await using var originBlob = await origin.SealAsync(CancellationToken.None);
            var bytes = await BytesOfAsync(originBlob);
            source = originBlob.RecordTable[1];
            sealedRecord = SealedBytesOf(
                bytes, source, RecordFraming.PrefixLength(FormatVersions.RelocatableRecords, BlobClass.Metadata));
        }

        await using var destination = CreateMetadataWriter(counter: 11);
        await AppendAsync(destination, resident, deriver);
        var ordinal = await destination.AppendSealedRecordAsync(source, sealedRecord, CancellationToken.None);
        Assert.AreEqual(1u, ordinal, "the relocated record is renumbered for its new container");

        await using var destinationBlob = await destination.SealAsync(CancellationToken.None);
        var (key, length) = await UploadAsync(store, destinationBlob);

        using var readDeriver = new ObjectIdDeriver(ContentIdKey);
        using var reader = await BlobReader.OpenAsync(
            store, key, length, Repo, (_, _) => ClassKey, readDeriver, CancellationToken.None);

        var relocated = await reader.ReadRecordAsync(reader.RecordTable[1], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, relocated.Outcome);
        SequenceAssert.AreEqual(travelling, relocated.Plaintext);
    }

    [TestMethod]
    public async Task DataRecord_CopiedIntoAnotherBlob_OpensBecauseItsShareTravelled()
    {
        // The sealed plane's half of the same property: the share is pinned
        // to the repository and the object, never to the blob, so it opens in
        // its new container under the same grant (05 §2.2).
        var store = CreateStore();
        using var authority = DeriveAuthority();
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var travelling = Enumerable.Repeat((byte)0x77, 820).ToArray();

        byte[] sealedRecord;
        RecordTableEntry source;
        await using (var origin = CreateDataWriter(authority, counter: 20))
        {
            await AppendAsync(origin, travelling, deriver);
            await using var originBlob = await origin.SealAsync(CancellationToken.None);
            var bytes = await BytesOfAsync(originBlob);
            source = originBlob.RecordTable[0];
            sealedRecord = SealedBytesOf(
                bytes, source, RecordFraming.PrefixLength(FormatVersions.RelocatableRecords, BlobClass.Data));
        }

        await using var destination = CreateDataWriter(authority, counter: 21);
        await AppendAsync(destination, Enumerable.Repeat((byte)0x88, 500).ToArray(), deriver);
        await destination.AppendSealedRecordAsync(source, sealedRecord, CancellationToken.None);
        await using var destinationBlob = await destination.SealAsync(CancellationToken.None);
        var (key, length) = await UploadAsync(store, destinationBlob);

        using var readDeriver = new ObjectIdDeriver(ContentIdKey);
        using var grant = new SealedContentKeyOpener(authority.SealingPrivateKey, Repo);
        using var reader = await BlobReader.OpenAsync(
            store, key, length, Repo, StructureKeys(authority.Credential), readDeriver, CancellationToken.None,
            grant);

        var relocated = await reader.ReadRecordAsync(reader.RecordTable[1], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, relocated.Outcome);
        SequenceAssert.AreEqual(travelling, relocated.Plaintext);
    }

    [TestMethod]
    public async Task AShareSealedForAnotherObject_FailsThatRecordAndNoOther()
    {
        // A share follows its record and refuses any other. Transplanted onto
        // a sibling it opens nowhere — and the damage is the record's, not the
        // blob's, which is the difference from format 2's one share per blob.
        var store = CreateStore();
        using var authority = DeriveAuthority();
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var first = Enumerable.Repeat((byte)0x99, 700).ToArray();
        var second = Enumerable.Repeat((byte)0xAA, 700).ToArray();

        await using (var writer = CreateDataWriter(authority, counter: 30))
        {
            await AppendAsync(writer, first, deriver);
            await AppendAsync(writer, second, deriver);
            await using var blob = await writer.SealAsync(CancellationToken.None);
            await UploadAsync(store, blob);
        }

        var path = Directory.EnumerateFiles(Path.Combine(_root, "store"), "*", SearchOption.AllDirectories)
            .Single(file => !file.Contains(".fbp-tmp", StringComparison.Ordinal));
        var raw = await File.ReadAllBytesAsync(path, CancellationToken.None);
        var length = raw.LongLength;
        var key = BlobStoreKeys.ForBlob(
            BlobClass.Data, new StoreBlobKeyDeriver(new byte[32]).Derive(BlobEnvelope.Parse(raw).BlobId));

        using var readDeriver = new ObjectIdDeriver(ContentIdKey);
        using var grant = new SealedContentKeyOpener(authority.SealingPrivateKey, Repo);

        int firstShare, secondShare;
        using (var probe = await BlobReader.OpenAsync(
                   store, key, length, Repo, StructureKeys(authority.Credential), readDeriver,
                   CancellationToken.None, grant))
        {
            firstShare = (int)probe.RecordTable[0].PhysicalOffset + RecordHeader.Length + RecordFraming.SealedKeyOffset;
            secondShare = (int)probe.RecordTable[1].PhysicalOffset + RecordHeader.Length + RecordFraming.SealedKeyOffset;
        }

        raw.AsSpan(secondShare, SealedRecordKey.SealedLength).CopyTo(raw.AsSpan(firstShare));
        await File.WriteAllBytesAsync(path, raw, CancellationToken.None);

        using var reader = await BlobReader.OpenAsync(
            store, key, length, Repo, StructureKeys(authority.Credential), readDeriver, CancellationToken.None,
            grant);

        var transplanted = await reader.ReadRecordAsync(reader.RecordTable[0], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.AuthenticationFailed, transplanted.Outcome);
        Assert.Contains("does not open", transplanted.Detail!, StringComparison.Ordinal);

        var untouched = await reader.ReadRecordAsync(reader.RecordTable[1], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, untouched.Outcome);
        SequenceAssert.AreEqual(second, untouched.Plaintext);
    }

    [TestMethod]
    public async Task TwoRecordsSwappedWithoutTheirTable_AreRefusedByTheHeaderCrossCheck()
    {
        // Format 3 drops the ordinal from the record AAD, so what stops a
        // reorder inside one blob is the footer: its table names each record's
        // ordinal, identifier and lengths, and the header at the offset must
        // agree (05 §3.1). Swap two records' bytes and leave the table alone.
        var store = CreateStore();
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var first = Enumerable.Repeat((byte)0xBB, 640).ToArray();
        var second = Enumerable.Repeat((byte)0xCC, 640).ToArray();

        ObjectKey key;
        long length;
        await using (var writer = CreateMetadataWriter(counter: 40))
        {
            await AppendAsync(writer, first, deriver);
            await AppendAsync(writer, second, deriver);
            await using var blob = await writer.SealAsync(CancellationToken.None);
            (key, length) = await UploadAsync(store, blob);
        }

        var path = Directory.EnumerateFiles(Path.Combine(_root, "store"), "*", SearchOption.AllDirectories)
            .Single(file => !file.Contains(".fbp-tmp", StringComparison.Ordinal));
        var raw = await File.ReadAllBytesAsync(path, CancellationToken.None);

        using var readDeriver = new ObjectIdDeriver(ContentIdKey);
        int recordLength, firstOffset, secondOffset;
        using (var probe = await BlobReader.OpenAsync(
                   store, key, length, Repo, (_, _) => ClassKey, readDeriver, CancellationToken.None))
        {
            firstOffset = (int)probe.RecordTable[0].PhysicalOffset;
            secondOffset = (int)probe.RecordTable[1].PhysicalOffset;
            recordLength = (int)RecordFraming.RecordLength(
                FormatVersions.RelocatableRecords, BlobClass.Metadata, probe.RecordTable[0].StoredLength);
        }

        var held = raw.AsSpan(firstOffset, recordLength).ToArray();
        raw.AsSpan(secondOffset, recordLength).CopyTo(raw.AsSpan(firstOffset));
        held.CopyTo(raw.AsSpan(secondOffset));
        await File.WriteAllBytesAsync(path, raw, CancellationToken.None);

        using var reader = await BlobReader.OpenAsync(
            store, key, length, Repo, (_, _) => ClassKey, readDeriver, CancellationToken.None);

        foreach (var entry in reader.RecordTable)
        {
            var result = await reader.ReadRecordAsync(entry, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.FormatViolation, result.Outcome);
        }
    }

    [TestMethod]
    public async Task ARecordBelowFormatThree_IsRefusedRelocationByName()
    {
        // The primitive is refused outright where the bytes could not survive
        // the move: a format-2 record's key is its blob's and its nonce is its
        // ordinal, so copying it anywhere is copying something unreadable.
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        await using var writer = BlobWriter.Create(
            Repo, Writer, KeyGeneration.Zero, BlobClass.Metadata, ClassKey, 50,
            EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, SpoolDirectory);

        await AppendAsync(writer, Enumerable.Repeat((byte)0xDD, 500).ToArray(), deriver);

        var refusal = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await writer.AppendSealedRecordAsync(
                writer.Entries[0], new byte[500 + RecordCipher.TagLength], CancellationToken.None));
        Assert.Contains("format 3", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ADataBlobAskedOfTheSymmetricCreator_IsRefusedByName()
    {
        var refusal = Assert.ThrowsExactly<ArgumentException>(() => BlobWriter.Create(
            Repo, Writer, KeyGeneration.Zero, BlobClass.Data, ClassKey, 60,
            EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, SpoolDirectory,
            formatVersion: FormatVersions.RelocatableRecords));
        Assert.Contains("seals a key per record", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AnInterruptedFormatThreeSpool_ResumesUnderTheSeedAndReEmitsItsBytes()
    {
        using var authority = DeriveAuthority();
        using var deriver = new ObjectIdDeriver(ContentIdKey);
        var first = Enumerable.Repeat((byte)0x12, 800).ToArray();
        var second = Enumerable.Repeat((byte)0x34, 900).ToArray();

        var writer = CreateDataWriter(authority, counter: 70, Pinned);
        await AppendAsync(writer, first, deriver);
        await AppendAsync(writer, second, deriver);
        await writer.AbandonAsync();
        await writer.DisposeAsync();

        var spoolPath = Directory.GetFiles(SpoolDirectory, "blob-*.spool").Single();
        var spooled = await File.ReadAllBytesAsync(spoolPath, CancellationToken.None);

        // The seed is the whole resume state: the nonces were random and are
        // not recomputable, so a walk that could not re-derive each record's
        // key would have to restart (05 §6.2).
        Assert.IsInstanceOfType<ResumeResult.Resumed>(Resume(authority), out var outcome);
        await using var resumed = outcome.Writer;
        Assert.AreEqual(2, resumed.RecordCount);

        var third = Enumerable.Repeat((byte)0x56, 700).ToArray();
        await AppendAsync(resumed, third, deriver);

        var store = CreateStore();
        await using var blob = await resumed.SealAsync(CancellationToken.None);
        var sealedBytes = await BytesOfAsync(blob);
        SequenceAssert.AreEqual(
            spooled, sealedBytes.AsSpan(0, spooled.Length).ToArray());

        var (key, length) = await UploadAsync(store, blob);
        using var readDeriver = new ObjectIdDeriver(ContentIdKey);
        using var grant = new SealedContentKeyOpener(authority.SealingPrivateKey, Repo);
        using var reader = await BlobReader.OpenAsync(
            store, key, length, Repo, StructureKeys(authority.Credential), readDeriver, CancellationToken.None,
            grant);

        Assert.HasCount(3, reader.RecordTable);
        foreach (var (entry, expected) in reader.RecordTable.Zip(new[] { first, second, third }))
        {
            var result = await reader.ReadRecordAsync(entry, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, result.Outcome);
            SequenceAssert.AreEqual(expected, result.Plaintext);
        }
    }

    [TestMethod]
    public async Task ATamperedSeedInTheCheckpoint_FailsTheWalkAndRestarts()
    {
        using var authority = DeriveAuthority();
        using var deriver = new ObjectIdDeriver(ContentIdKey);

        var writer = CreateDataWriter(authority, counter: 71, Pinned);
        await AppendAsync(writer, Enumerable.Repeat((byte)0x78, 800).ToArray(), deriver);
        await writer.AbandonAsync();
        await writer.DisposeAsync();

        var sidecarPath = Directory.GetFiles(SpoolDirectory, "*.checkpoint").Single();
        var sidecar = await File.ReadAllBytesAsync(sidecarPath, CancellationToken.None);
        sidecar[sidecar.Length - 40] ^= 0x01;
        SHA256.HashData(sidecar.AsSpan(0, sidecar.Length - 32)).CopyTo(sidecar.AsSpan(sidecar.Length - 32));
        await File.WriteAllBytesAsync(sidecarPath, sidecar, CancellationToken.None);

        Assert.IsInstanceOfType<ResumeResult.MustRestart>(Resume(authority), out var restart);
        Assert.AreEqual("spool_tail_unauthenticated", restart.Reason);
    }

    [TestMethod]
    public async Task ATornFormatThreeTail_Restarts()
    {
        using var authority = DeriveAuthority();
        using var deriver = new ObjectIdDeriver(ContentIdKey);

        var writer = CreateDataWriter(authority, counter: 72, Pinned);
        await AppendAsync(writer, Enumerable.Repeat((byte)0x9A, 800).ToArray(), deriver);
        await writer.AbandonAsync();
        await writer.DisposeAsync();

        var spoolPath = Directory.GetFiles(SpoolDirectory, "blob-*.spool").Single();
        var spooled = await File.ReadAllBytesAsync(spoolPath, CancellationToken.None);
        await File.WriteAllBytesAsync(spoolPath, spooled[..^9], CancellationToken.None);

        Assert.IsInstanceOfType<ResumeResult.MustRestart>(Resume(authority), out var restart);
        Assert.AreEqual("spool_tail_unauthenticated", restart.Reason);
    }

    [TestMethod]
    public async Task AFormatThreeDataResumeWithoutTheSealingKey_IsRefusedRatherThanDiscardingTheSpool()
    {
        // Every further record on this blob seals a key of its own, so a
        // resumed writer that could not do that is useless — but the spool is
        // still good, and discarding it would throw away work for a caller's
        // omission.
        using var authority = DeriveAuthority();
        using var deriver = new ObjectIdDeriver(ContentIdKey);

        var writer = CreateDataWriter(authority, counter: 73, Pinned);
        await AppendAsync(writer, Enumerable.Repeat((byte)0xBC, 800).ToArray(), deriver);
        await writer.AbandonAsync();
        await writer.DisposeAsync();

        var structureKey = authority.Credential.DeriveMetadataKey(KeyGeneration.Zero);
        var refusal = Assert.ThrowsExactly<ArgumentException>(() => BlobWriter.TryResume(
            SpoolDirectory, Repo, Writer, KeyGeneration.Zero, BlobClass.Data, structureKey,
            EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, Pinned,
            expectedFormatVersion: FormatVersions.RelocatableRecords));
        CryptographicOperations.ZeroMemory(structureKey);

        Assert.Contains("sealing public key", refusal.Message, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Directory.GetFiles(SpoolDirectory, "blob-*.spool").Single()));
    }

    private ResumeResult Resume(RepositoryReadAuthority authority)
    {
        var structureKey = authority.Credential.DeriveMetadataKey(KeyGeneration.Zero);
        try
        {
            return BlobWriter.TryResume(
                SpoolDirectory, Repo, Writer, KeyGeneration.Zero, BlobClass.Data, structureKey,
                EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, Pinned,
                expectedFormatVersion: FormatVersions.RelocatableRecords,
                sealingPublicKey: authority.Credential.SealingPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(structureKey);
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

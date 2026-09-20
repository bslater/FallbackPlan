using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// The keyless compactor
/// ([ADR-0067](../../../docs/adr/0067-the-keyless-compactor.md);
/// [ADR-0052](../../../docs/adr/0052-relocatable-records-format-v3.md) §4):
/// a partly dead format-3 blob is rewritten into a dense one by copying its
/// live records' sealed bytes verbatim, without opening one of them and
/// without holding a key that could. Establishes FR-MAN-019 for the rewrite
/// and FR-WOR-003 for what the compactor is not entitled to see.
/// </summary>
/// <remarks>
/// Does not establish FR-GC-011: which blobs are worth rewriting is
/// <c>CompactionPolicy</c>'s, and what happens to the sources afterwards —
/// the intent, the supersession delta, the tombstone — is the pass's.
/// </remarks>
[TestClass]
public sealed class BlobCompactionTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("3132333435363738393a3b3c3d3e3f40"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("d0d1d2d3d4d5d6d7d8d9dadbdcdddedf"));

    private static readonly Argon2Parameters TinyParameters =
        new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-compaction-tests", Guid.NewGuid().ToString("n"));

    private string SpoolDirectory => Path.Combine(_root, "spool");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public async Task Compaction_CarriesTheLiveRecords_AndLeavesTheDeadBehind()
    {
        using var authority = DeriveAuthority();
        using var keys = RepositoryKeySet.FromWriteCredential(authority.Credential);
        var store = CreateStore();

        var payloads = Enumerable.Range(0, 5)
            .Select(index => Enumerable.Repeat((byte)(0x40 + index), 512 + index).ToArray())
            .ToList();
        var source = await WriteBlobAsync(store, keys, authority, payloads, counter: 1);

        // Records 1 and 3 survive; the other three are what the pass is
        // reclaiming.
        var live = new[] { source.Table[1], source.Table[3] };

        var produced = await CompactAsync(store, keys, authority, [
            new CompactionSource(source.Key, source.BlobId, live)
        ]);

        var only = Assert.ContainsSingle(produced);
        Assert.HasCount(2, only.Sealed.RecordTable);
        CollectionAssert.AreEquivalent(
            live.Select(entry => entry.ObjectId).ToList(),
            only.Sealed.RecordTable.Select(entry => entry.ObjectId).ToList());
        Assert.AreEqual(source.BlobId, Assert.ContainsSingle(only.Drained));

        // And they still open — under the passphrase's grant, which is the
        // only thing that ever could, and which the compactor never held.
        var moved = await UploadAsync(store, keys, only.Sealed);
        var read = await ReadPayloadsAsync(store, keys, authority, moved.Key, moved.Length);
        CollectionAssert.AreEquivalent(
            new[] { payloads[1], payloads[3] }.Select(Convert.ToHexString).ToList(),
            read.Select(Convert.ToHexString).ToList());

        // Re-framed, and only there: the records carried ordinals 1 and 3
        // where they came from and carry 0 and 1 here, which is the one
        // field of the 54-byte header a relocation may touch — it is the one
        // field the AAD does not bind (04 §4).
        CollectionAssert.AreEqual(
            new uint[] { 1, 3 }, live.Select(entry => entry.Ordinal).ToArray());
        CollectionAssert.AreEqual(
            new uint[] { 0, 1 }, only.Sealed.RecordTable.Select(entry => entry.Ordinal).ToArray());
    }

    [TestMethod]
    public async Task Compaction_CopiesTheSealedBytes_ByteForByte()
    {
        using var authority = DeriveAuthority();
        using var keys = RepositoryKeySet.FromWriteCredential(authority.Credential);
        var store = CreateStore();

        var payloads = new[] { Enumerable.Repeat((byte)0x77, 900).ToArray() };
        var source = await WriteBlobAsync(store, keys, authority, payloads, counter: 1);
        var before = SealedBytesOf(source.Bytes, source.Table[0]);

        var produced = await CompactAsync(store, keys, authority, [
            new CompactionSource(source.Key, source.BlobId, [source.Table[0]])
        ]);

        var only = Assert.ContainsSingle(produced);
        var after = SealedBytesOf(await BytesOfAsync(only.Sealed), only.Sealed.RecordTable[0]);

        // The whole claim of format 3, in one assertion: prefix, ciphertext
        // and tag are the source's, unchanged. Only the 54-byte header was
        // re-framed, with the ordinal this blob gives it.
        CollectionAssert.AreEqual(before, after);
    }

    [TestMethod]
    public async Task Compaction_PublishesSupersessions_NamingTheNewBlob()
    {
        using var authority = DeriveAuthority();
        using var keys = RepositoryKeySet.FromWriteCredential(authority.Credential);
        var store = CreateStore();

        var payloads = new[] { new byte[600], Enumerable.Repeat((byte)0x5A, 600).ToArray() };
        var source = await WriteBlobAsync(store, keys, authority, payloads, counter: 1);

        var produced = await CompactAsync(store, keys, authority, [
            new CompactionSource(source.Key, source.BlobId, [source.Table[0]])
        ]);

        var entry = Assert.ContainsSingle(Assert.ContainsSingle(produced).Superseding);

        // Supersession, not insertion: the reader must be moved off the old
        // location rather than offered a second one and left to a tie-break
        // (07 §3, FR-MAN-015).
        Assert.AreEqual(IndexEntryType.Supersession, entry.EntryType);
        Assert.AreEqual(produced[0].Sealed.BlobId, entry.BlobId);
        Assert.AreEqual(source.Table[0].ObjectId, entry.ObjectId);
        Assert.AreEqual(produced[0].Sealed.RecordTable[0].PhysicalOffset, entry.PhysicalOffset);
    }

    [TestMethod]
    public async Task Compaction_PacksSeveralSources_AndStartsAnotherBlobWhenOneIsFull()
    {
        using var authority = DeriveAuthority();
        using var keys = RepositoryKeySet.FromWriteCredential(authority.Credential);
        var store = CreateStore();

        var sources = new List<WrittenBlob>();
        for (var index = 0; index < 3; index++)
        {
            sources.Add(await WriteBlobAsync(
                store,
                keys,
                authority,
                [Enumerable.Repeat((byte)(0x10 + index), 700 + index).ToArray()],
                counter: (ulong)index + 1));
        }

        // Two records to a blob, so three sources produce two outputs: the
        // point of compaction is fewer and denser blobs, and the bound is the
        // write profile's, not the compactor's.
        var produced = await CompactAsync(
            store,
            keys,
            authority,
            [.. sources.Select(blob => new CompactionSource(blob.Key, blob.BlobId, [blob.Table[0]]))],
            CapturePolicy.Default with
            {
                BlobWriteProfile = BlobWriteProfile.LocalDefault with { MaximumRecordCount = 2 },
            });

        Assert.HasCount(2, produced);
        Assert.HasCount(2, produced[0].Sealed.RecordTable);
        Assert.HasCount(1, produced[1].Sealed.RecordTable);
        Assert.HasCount(3, produced.SelectMany(blob => blob.Drained).Distinct().ToList());
    }

    [TestMethod]
    public async Task Compaction_HoldsNoContentKey_AndWhatItProducesIsStillSealed()
    {
        using var authority = DeriveAuthority();
        using var keys = RepositoryKeySet.FromWriteCredential(authority.Credential);
        var store = CreateStore();

        var payloads = new[] { Enumerable.Repeat((byte)0x33, 800).ToArray() };
        var source = await WriteBlobAsync(store, keys, authority, payloads, counter: 1);

        // The compactor is handed a key set and a store and completes. The
        // proof that it opened nothing is on the other side: a reader with
        // the same entitlement — structure only, no grant — reads the
        // produced blob's table and is refused its content.
        var produced = await CompactAsync(store, keys, authority, [
            new CompactionSource(source.Key, source.BlobId, [source.Table[0]])
        ]);
        var moved = await UploadAsync(store, keys, Assert.ContainsSingle(produced).Sealed);

        using var deriver = new ObjectIdDeriver(keys.ContentIdKey);
        using var reader = await BlobReader.OpenAsync(
            store, moved.Key, moved.Length, Repo, StructureKeys(authority.Credential), deriver,
            CancellationToken.None, sealedContentKeyOpener: null);

        Assert.HasCount(1, reader.RecordTable);
        var read = await reader.ReadRecordAsync(reader.RecordTable[0], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.ContentSealed, read.Outcome);
    }

    [TestMethod]
    public async Task Compaction_ASourceRecordDisagreeingWithItsTable_IsRefusedRatherThanRelocated()
    {
        using var authority = DeriveAuthority();
        using var keys = RepositoryKeySet.FromWriteCredential(authority.Credential);
        var store = CreateStore();

        var payloads = new[] { Enumerable.Repeat((byte)0x66, 700).ToArray() };
        var source = await WriteBlobAsync(store, keys, authority, payloads, counter: 1);

        // Flip a byte inside the record's header — its ordinal — so that the
        // header and the footer's table disagree. A compactor that carried
        // this anyway would launder the damage into a blob nothing suspects.
        var damaged = source.Bytes.ToArray();
        damaged[(int)source.Table[0].PhysicalOffset + RecordHeader.Length - 1] ^= 0xFF;
        await OverwriteAsync(store, source.Key, damaged);

        var failure = await Assert.ThrowsExactlyAsync<BlobFormatException>(async () =>
            await CompactAsync(store, keys, authority, [
                new CompactionSource(source.Key, source.BlobId, [source.Table[0]])
            ]));

        Assert.Contains("refused rather than relocated", failure.Message, StringComparison.Ordinal);
    }

    private sealed record WrittenBlob(
        ObjectKey Key, BlobId BlobId, long Length, byte[] Bytes, IReadOnlyList<RecordTableEntry> Table);

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private static RepositoryReadAuthority DeriveAuthority()
    {
        using var passphrase = Passphrase.Create("a compactable repository!!");
        var salt = Enumerable.Repeat((byte)0x2B, KekDerivation.SaltLength).ToArray();
        return WriteOnlyDerivation.Derive(passphrase, TinyParameters, salt, KdfValidationMode.OpenRepository);
    }

    private static Func<BlobClass, KeyGeneration, byte[]> StructureKeys(RepositoryWriteCredential credential) =>
        (blobClass, generation) => blobClass == BlobClass.Metadata
            ? credential.DeriveMetadataKey(generation)
            : throw new InvalidOperationException("a write-only holder was asked for a data key");

    private async ValueTask<IReadOnlyList<CompactedBlob>> CompactAsync(
        LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        RepositoryReadAuthority authority,
        IReadOnlyList<CompactionSource> candidates,
        CapturePolicy? policy = null)
    {
        _ = authority;
        using var compactor = new BlobCompactor(
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            policy ?? CapturePolicy.Default,
            store,
            new SequentialCounters(),
            SpoolDirectory,
            FormatVersions.RelocatableRecords);

        return await compactor.CompactAsync(candidates, CancellationToken.None);
    }

    private async ValueTask<WrittenBlob> WriteBlobAsync(
        LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        RepositoryReadAuthority authority,
        IReadOnlyList<byte[]> payloads,
        ulong counter)
    {
        using var deriver = new ObjectIdDeriver(keys.ContentIdKey);
        var structureKey = authority.Credential.DeriveMetadataKey(KeyGeneration.Zero);
        SealedBlob sealedBlob;
        try
        {
            var writer = BlobWriter.CreateSealed(
                Repo,
                Writer,
                KeyGeneration.Zero,
                structureKey,
                authority.Credential.SealingPublicKey,
                counter,
                EncryptionProfile.Aes256GcmV1,
                BlobWriteProfile.LocalDefault,
                SpoolDirectory,
                formatVersion: FormatVersions.RelocatableRecords);

            await using (writer.ConfigureAwait(false))
            {
                foreach (var payload in payloads)
                {
                    await writer.AppendRecordAsync(
                        ObjectType.SegmentRecord,
                        deriver.Derive(ObjectType.SegmentRecord, ContentHasher.Hash(payload)),
                        CompressionProfile.None,
                        (ulong)payload.Length,
                        payload,
                        CancellationToken.None);
                }

                sealedBlob = await writer.SealAsync(CancellationToken.None);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(structureKey);
        }

        var bytes = await BytesOfAsync(sealedBlob);
        var uploaded = await UploadAsync(store, keys, sealedBlob);
        return new WrittenBlob(
            uploaded.Key, sealedBlob.BlobId, uploaded.Length, bytes, [.. sealedBlob.RecordTable]);
    }

    private static async Task<List<byte[]>> ReadPayloadsAsync(
        LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        RepositoryReadAuthority authority,
        ObjectKey key,
        long length)
    {
        using var deriver = new ObjectIdDeriver(keys.ContentIdKey);
        using var opener = new SealedContentKeyOpener(authority.SealingPrivateKey, Repo);
        using var reader = await BlobReader.OpenAsync(
            store, key, length, Repo, StructureKeys(authority.Credential), deriver,
            CancellationToken.None, sealedContentKeyOpener: opener);

        var payloads = new List<byte[]>();
        foreach (var entry in reader.RecordTable)
        {
            var read = await reader.ReadRecordAsync(entry, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
            payloads.Add(read.Plaintext!);
        }

        return payloads;
    }

    private static byte[] SealedBytesOf(byte[] blob, RecordTableEntry entry) =>
        blob.AsSpan(
            (int)entry.PhysicalOffset + RecordHeader.Length,
            RecordFraming.PrefixLength(FormatVersions.RelocatableRecords, BlobClass.Data)
                + (int)entry.StoredLength + RecordCipher.TagLength).ToArray();

    private static async Task<byte[]> BytesOfAsync(SealedBlob blob)
    {
        await using var content = await blob.OpenContentAsync(CancellationToken.None);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, CancellationToken.None);
        return buffer.ToArray();
    }

    private static async Task<(ObjectKey Key, long Length)> UploadAsync(
        LocalFileSystemObjectStore store, RepositoryKeySet keys, SealedBlob blob)
    {
        using var keyDeriver = new StoreBlobKeyDeriver(keys.KeyIdKey);
        var key = BlobStoreKeys.ForBlob(blob.BlobClass, keyDeriver.Derive(blob.BlobId));
        var put = await store.PutAsync(key, blob.OpenContentAsync, PutConditions.IfNotExists, CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);
        return (key, blob.Length);
    }

    private static async Task OverwriteAsync(LocalFileSystemObjectStore store, ObjectKey key, byte[] bytes)
    {
        await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);
        var put = await store.PutAsync(
            key,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
            PutConditions.IfNotExists,
            CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);
    }

    private sealed class SequentialCounters : IBlobCounterAllocator
    {
        private ulong _next = 1000;

        public ulong AllocateNext() => ++_next;

        public void MarkAccounted(ulong blobCounter)
        {
        }
    }
}

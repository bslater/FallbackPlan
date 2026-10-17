using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// The publisher refuses an index object no reader would open (FR-MAN-008;
/// specification 00 §8, 07 §2). A delta or a checkpoint is a standalone
/// metadata object, and the framing refuses one over 16 MiB at read, before
/// a byte of it is allocated. Written, it would sit in the store looking
/// published until the day it was needed. So the publisher refuses it by
/// name and puts nothing, and the number it drew is left for a void delta,
/// as any publication that fails after drawing one is.
/// </summary>
[TestClass]
public sealed class OversizedIndexObjectTests : IDisposable
{
    // About 68 bytes an entry on the wire, so 300 000 entries are 20 MB:
    // over the limit by a margin no framing overhead could account for.
    private const int OverLimitEntries = 300_000;

    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-oversized-index-tests", Guid.NewGuid().ToString("n"));

    private static List<IndexEntry> Entries(int count)
    {
        var entries = new List<IndexEntry>(count);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            entries.Add(new IndexEntry(
                ObjectId.FromBytes(SHA256.HashData(BitConverter.GetBytes(ordinal))),
                BlobId.FromBytes(SHA256.HashData(BitConverter.GetBytes(ordinal / 512)).AsSpan(0, 16)),
                PhysicalOffset: (ulong)ordinal * 4_096,
                StoredLength: 65_536,
                CompressionProfileValue: 0x0001,
                EncryptionProfileValue: 0x0001,
                IndexEntryType.Insertion));
        }

        return entries;
    }

    [TestMethod]
    public async Task ADeltaOverTheMetadataLimit_IsRefused_AndNothingIsPut()
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "store"));
        using var credential = TestAuthority.Shared.Credential.Clone();
        var sequence = new WriterSequence(new FileSequenceStateStore(Path.Combine(_root, "delta-sequence.txt")));
        using var publisher = new IndexPublisher(store, Repo, Writer, credential, sequence, highestIndexGenerationSeen: 0);

        var refusal = await Assert.ThrowsExactlyAsync<IndexFormatException>(async () =>
            await publisher.PublishDeltaAsync(0, [], Entries(OverLimitEntries), CancellationToken.None));

        Assert.Contains("16 MiB", refusal.Message, StringComparison.Ordinal);
        Assert.IsEmpty(await ListAsync(store));

        // The number it drew is still owed, so the next publication voids it.
        Assert.IsNotEmpty(sequence.OutstandingObligations);
    }

    [TestMethod]
    public async Task ACheckpointOverTheMetadataLimit_IsRefused_AndNothingIsPut()
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "checkpoint-store"));
        using var credential = TestAuthority.Shared.Credential.Clone();
        var sequence = new WriterSequence(new FileSequenceStateStore(Path.Combine(_root, "checkpoint-sequence.txt")));
        using var publisher = new IndexPublisher(store, Repo, Writer, credential, sequence);

        await Assert.ThrowsExactlyAsync<IndexFormatException>(async () =>
            await publisher.PublishCheckpointAsync(
                0, subsumedDeltaIds: [], writerWatermarks: [], Entries(OverLimitEntries), predecessor: null,
                CancellationToken.None));

        Assert.IsEmpty(await ListAsync(store));
    }

    [TestMethod]
    public async Task ADeltaAtTheLimitsEdge_IsPublished_AndReadsBack()
    {
        // The refusal is the reader's own bound, not a stricter one: what
        // the publisher puts, the loader opens.
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "edge-store"));
        using var credential = TestAuthority.Shared.Credential.Clone();
        var sequence = new WriterSequence(new FileSequenceStateStore(Path.Combine(_root, "edge-sequence.txt")));
        using (var publisher = new IndexPublisher(store, Repo, Writer, credential, sequence, highestIndexGenerationSeen: 0))
        {
            await publisher.PublishDeltaAsync(0, [], Entries(240_000), CancellationToken.None);
        }

        using var loader = new IndexLoader(store, Repo, credential);
        var state = await loader.LoadAsync(
            currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null, blobState: null,
            CancellationToken.None);

        Assert.IsEmpty(state.Findings);
        Assert.HasCount(240_000, state.Resolved);
    }

    private static async Task<List<ObjectKey>> ListAsync(LocalFileSystemObjectStore store)
    {
        var keys = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("index/"), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key);
        }

        return keys;
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

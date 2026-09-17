using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// The journal half of the observed head on its own (NFR-SEC-005): how far a
/// writer had got according to a store's journal keys alone — which is the
/// question a fan-out pass can put to a destination that holds no index to
/// load and for which the caller holds no credential.
/// </summary>
[TestClass]
public sealed class ObservedHeadTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-observed-head", Guid.NewGuid().ToString("n"));

    private static readonly WriterId Writer = WriterId.FromBytes(Enumerable.Repeat((byte)0x11, 16).ToArray());
    private static readonly WriterId Other = WriterId.FromBytes(Enumerable.Repeat((byte)0x22, 16).ToArray());

    [TestMethod]
    public async Task JournalHead_IsTheHighestSequenceInTheKeysAlone()
    {
        // Three journal objects whose payloads are noise: the sequence is
        // read from the key, never the record, so the answer costs a listing
        // and no key material.
        var store = new LocalFileSystemObjectStore(_root);
        await PutAsync(store, MetadataStoreKeys.Journal(Writer, 5));
        await PutAsync(store, MetadataStoreKeys.Journal(Writer, 12));
        await PutAsync(store, MetadataStoreKeys.Journal(Writer, 7));

        Assert.AreEqual(12UL, await ObservedHead.JournalHeadAsync(store, Writer, CancellationToken.None));
    }

    [TestMethod]
    public async Task JournalHead_AnotherWritersKeys_AreNotThisWriters()
    {
        // Two writers into one repository is a supported shape (ADR-0008);
        // a second device's history must never read as this device's
        // allocation state having rolled back.
        var store = new LocalFileSystemObjectStore(_root);
        await PutAsync(store, MetadataStoreKeys.Journal(Other, 40));
        await PutAsync(store, MetadataStoreKeys.Journal(Writer, 3));

        Assert.AreEqual(3UL, await ObservedHead.JournalHeadAsync(store, Writer, CancellationToken.None));
        Assert.AreEqual(40UL, await ObservedHead.JournalHeadAsync(store, Other, CancellationToken.None));
    }

    [TestMethod]
    public async Task JournalHead_AKeyThatDoesNotParse_IsSkippedNotGuessed()
    {
        var store = new LocalFileSystemObjectStore(_root);
        await PutAsync(store, MetadataStoreKeys.Journal(Writer, 9));
        var prefix = MetadataStoreKeys.Journal(Writer, 0).Value[..^MetadataStoreKeys.Decimal16Length];
        await PutAsync(store, ObjectKey.Parse(prefix + "not-a-sequence"));

        Assert.AreEqual(9UL, await ObservedHead.JournalHeadAsync(store, Writer, CancellationToken.None));
    }

    [TestMethod]
    public async Task JournalHead_NothingForTheWriter_IsZero()
    {
        var store = new LocalFileSystemObjectStore(_root);

        Assert.AreEqual(0UL, await ObservedHead.JournalHeadAsync(store, Writer, CancellationToken.None));
    }

    private static async Task PutAsync(LocalFileSystemObjectStore store, ObjectKey key)
    {
        var result = await store.PutAsync(
            key, _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3])),
            PutConditions.IfNotExists, CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, result.Outcome);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

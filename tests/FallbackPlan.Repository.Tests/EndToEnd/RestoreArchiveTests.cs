using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A plan names what the store holds only in an archival tier, before any
/// byte moves (FR-RST-003; architecture 08 §2; ADR-0103). An object a
/// lifecycle rule moved there is listed and sized as any other, and is read
/// only once it is rehydrated, which takes hours. A run that met one would
/// fail that file part-way through; the plan says which files will, apart
/// from those whose content is gone, because rehydrating them is something a
/// person can do.
/// </summary>
/// <remarks>
/// <para>
/// A plan reads around an archived copy as a run does: a blob another copy of
/// the set holds readable is neither archived nor missing, and the run reads
/// it from there. Only what every copy holds archived is named.
/// </para>
/// <para>
/// A file with content lost is missing whatever else of it is archived,
/// because no rehydration brings it back.
/// </para>
/// </remarks>
[TestClass]
public sealed class RestoreArchiveTests : ArchiveTestHarness
{
    private const string Own = "the staging archive";

    private string SpareRoot => Path.Combine(StoreRoot, "..", "spare");

    [TestMethod]
    public async Task Plan_FilesWhoseContentIsHeldOnlyInAnArchivalTier_AreNamedArchived_NotMissing()
    {
        var (plan, catalogue, store, keys) = await PublishAsync(TwoFiles(), 0xD1);
        using (keys)
        using (catalogue)
        {
            var probed = await RestoreBlobSet.ResolveAsync(
                catalogue, plan, new ArchivingObjectStore(store, IsData), Repo, keys, CancellationToken.None);

            Assert.IsEmpty(probed.Missing);
            CollectionAssert.AreEquivalent(new[] { "docs/a.bin", "docs/b.bin" }, probed.Archived.ToArray());

            // What the run cannot read is not counted as read, envelopes and
            // all: the figure is the one for the same tree with its content
            // gone.
            Directory.Delete(Path.Combine(StoreRoot, "blobs", "data"), recursive: true);
            var lost = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);
            Assert.HasCount(2, lost.Missing);
            Assert.AreEqual(lost.ReadBytes, probed.ReadBytes);
        }
    }

    [TestMethod]
    public async Task Plan_ContentOnlyAnotherCopyHolds_ArchivedThere_IsNamedArchived()
    {
        var (plan, catalogue, store, keys) = await PublishAsync(TwoFiles(), 0xD8);
        using (keys)
        using (catalogue)
        {
            var spare = await MirrorAsync(store, SpareRoot);
            Directory.Delete(Path.Combine(StoreRoot, "blobs", "data"), recursive: true);

            var probed = await RestoreBlobSet.ResolveAsync(
                catalogue, plan, store, [Serve("destination 'spare'", new ArchivingObjectStore(spare, IsData))], Repo, keys,
                CancellationToken.None);

            Assert.IsEmpty(probed.Missing, "the spare holds it, archived");
            Assert.HasCount(2, probed.Archived);
        }
    }

    [TestMethod]
    public async Task Plan_ManifestsHeldOnlyInAnArchivalTier_NameEveryItemThatNeedsThem()
    {
        var (plan, catalogue, store, keys) = await PublishAsync(TwoFiles(), 0xD2);
        using (keys)
        using (catalogue)
        {
            var probed = await RestoreBlobSet.ResolveAsync(
                catalogue, plan, new ArchivingObjectStore(store, IsMetadata), Repo, keys, CancellationToken.None);

            Assert.IsEmpty(probed.Missing);
            CollectionAssert.AreEquivalent(new[] { "docs", "docs/a.bin", "docs/b.bin" }, probed.Archived.ToArray());
        }
    }

    [TestMethod]
    public async Task Plan_ContentAnotherCopyHoldsReadable_IsNeitherArchivedNorMissing()
    {
        var (plan, catalogue, store, keys) = await PublishAsync(TwoFiles(), 0xD3);
        using (keys)
        using (catalogue)
        {
            var whole = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);
            var spare = await MirrorAsync(store, SpareRoot);

            var probed = await RestoreBlobSet.ResolveAsync(
                catalogue, plan, new ArchivingObjectStore(store, IsData), [Serve("destination 'spare'", spare)], Repo, keys,
                CancellationToken.None);

            Assert.IsEmpty(probed.Missing);
            Assert.IsEmpty(probed.Archived);
            Assert.AreEqual(whole.ReadBytes, probed.ReadBytes, "the run reads it from the copy that holds it readable");
        }
    }

    [TestMethod]
    public async Task Plan_ContentArchivedAtEveryCopy_IsNamedArchived()
    {
        var (plan, catalogue, store, keys) = await PublishAsync(TwoFiles(), 0xD4);
        using (keys)
        using (catalogue)
        {
            var spare = await MirrorAsync(store, SpareRoot);

            var probed = await RestoreBlobSet.ResolveAsync(
                catalogue, plan, new ArchivingObjectStore(store, IsData),
                [Serve("destination 'spare'", new ArchivingObjectStore(spare, IsData))], Repo, keys,
                CancellationToken.None);

            Assert.IsEmpty(probed.Missing);
            Assert.HasCount(2, probed.Archived);
        }
    }

    [TestMethod]
    public async Task Plan_AFileWithContentLost_IsMissing_HoweverMuchElseOfItIsArchived()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/big.bin", Random(700_000, 7));
        var (plan, catalogue, store, keys) = await PublishAsync(source, 0xD5);
        using (keys)
        using (catalogue)
        {
            // The blob that holds the file's last segment is the one lost, so
            // the probe meets archived segments before the lost one.
            using var reader = new RepositoryReader(Repo, keys, store, Authority);
            reader.UseLocationSource(catalogue.ResolveLocation);
            var item = plan.Items.Single(candidate => candidate.Path == "docs/big.bin");
            var segments = FileVersionManifestCodec.Decode(
                (await reader.ReadSegmentAsync(item.ObjectId, CancellationToken.None)).Plaintext).SegmentReferences;
            var first = catalogue.ResolveLocation(segments[0].ObjectId)!.BlobId;
            var last = catalogue.ResolveLocation(segments[^1].ObjectId)!;
            Assert.AreNotEqual(first, last.BlobId, "the file spans several data blobs");
            File.Delete(Path.Combine(StoreRoot, BlobStoreKeys.ForBlob(BlobClass.Data, last.StoreBlobKey!.Value).Value));

            var probed = await RestoreBlobSet.ResolveAsync(
                catalogue, plan, new ArchivingObjectStore(store, IsData), Repo, keys, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "docs/big.bin" }, probed.Missing.ToArray());
            Assert.IsEmpty(probed.Archived);
        }
    }

    [TestMethod]
    public async Task Run_AFileHeldOnlyInAnArchivalTier_Fails_SayingItIsArchived()
    {
        var (plan, catalogue, store, keys) = await PublishAsync(TwoFiles(), 0xD6);
        using (keys)
        using (catalogue)
        {
            using var reader = new RepositoryReader(Repo, keys, new ArchivingObjectStore(store, IsData), Authority);
            reader.UseLocationSource(catalogue.ResolveLocation);

            var receipt = await RestoreAsync(reader, plan, Path.Combine(SpoolDirectory, "restored-d6"));

            var item = receipt.Items.Single(candidate => candidate.Path == "docs/a.bin");
            Assert.AreEqual("failed", item.Outcome);
            Assert.Contains("archival", item.Detail!, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task Run_AnArchivedCopy_IsReadAround_FromACopyThatHoldsItReadable()
    {
        var (plan, catalogue, store, keys) = await PublishAsync(TwoFiles(), 0xD7);
        using (keys)
        using (catalogue)
        {
            var spare = await MirrorAsync(store, SpareRoot);
            using var reader = new RepositoryReader(Repo, keys, new ArchivingObjectStore(store, IsData), Authority);
            reader.UseLocationSource(catalogue.ResolveLocation);
            reader.UseOtherCopies(Own, [Serve("destination 'spare'", spare)]);

            var receipt = await RestoreAsync(reader, plan, Path.Combine(SpoolDirectory, "restored-d7"));

            Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
            CollectionAssert.AreEqual(
                Random(150_000, 1), await File.ReadAllBytesAsync(Path.Combine(SpoolDirectory, "restored-d7", "docs", "a.bin")));
        }
    }

    private static bool IsData(ObjectKey key) => key.Value.StartsWith("blobs/data/", StringComparison.Ordinal);

    private static bool IsMetadata(ObjectKey key) => key.Value.StartsWith("blobs/meta/", StringComparison.Ordinal);

    private static FakeFileSystemSource TwoFiles()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Random(150_000, 1));
        source.AddFile("docs/b.bin", Random(150_000, 2));
        return source;
    }

    private static CopySource Serve(string name, IObjectStore store) =>
        new(name, _ => ValueTask.FromResult<IObjectStore?>(store));

    private static async Task<RestoreReceipt> RestoreAsync(RepositoryReader reader, RestorePlan plan, string output) =>
        await new RestoreExecutor(reader, RestoreTargetProfile.ForLocalPlatform()).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "archive-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

    private async Task<(RestorePlan Plan, CatalogueDb Catalogue, LocalFileSystemObjectStore Store, RepositoryKeySet Keys)>
        PublishAsync(FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"archive-{seed:x2}.db"), Repo);

        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
                SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "restore-archive-tests/1.0",
                },
                CancellationToken.None);

        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, RestoreTargetProfile.ForLocalPlatform());
        return (plan, catalogue, store, keys);
    }

    private static async Task<LocalFileSystemObjectStore> MirrorAsync(LocalFileSystemObjectStore from, string root)
    {
        Directory.CreateDirectory(root);
        var mirror = new LocalFileSystemObjectStore(root);
        await foreach (var entry in from.ListAsync(ObjectPrefix.All, ListOptions.Default, CancellationToken.None))
        {
            using var read = await from.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            var bytes = new byte[entry.Length];
            await read.Content!.ReadExactlyAsync(bytes, CancellationToken.None);
            await mirror.PutAsync(
                entry.Key,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
                PutConditions.None,
                CancellationToken.None);
        }

        return mirror;
    }

    private static byte[] Random(int length, byte seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>
    /// A store whose chosen objects a lifecycle rule has moved to an archival
    /// tier: listed and sized as before, said to be archived, and refused when
    /// read, as the providers that have such a tier refuse them.
    /// </summary>
    private sealed class ArchivingObjectStore(IObjectStore inner, Func<ObjectKey, bool> archived) : IObjectStore
    {
        public StoreCapabilities Capabilities => inner.Capabilities with { ArchivalTiers = true };

        public async ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            var held = await inner.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            return held.Found && archived(key) ? new GetMetadataResult(held.Metadata! with { Archived = true }) : held;
        }

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
        {
            if (archived(key))
            {
                throw new StoreObjectArchivedException($"'{key.Value}' is held only in an archival tier");
            }

            return inner.OpenReadAsync(key, range, cancellationToken);
        }

        public ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}

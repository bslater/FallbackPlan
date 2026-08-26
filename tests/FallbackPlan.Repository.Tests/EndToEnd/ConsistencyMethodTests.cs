using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The snapshot manifest's <c>consistency_method</c> (key 10) reaches the
/// catalogue, by both routes that populate one.
/// </summary>
/// <remarks>
/// <para>
/// Specification 06 §6 says the value is "recorded and surfaced" because
/// "best-effort live capture" and "application-consistent" are materially
/// different promises and a person restoring a database needs to know which
/// one they hold. It was recorded from the beginning and surfaced nowhere:
/// the projector decoded the manifest and dropped the field, so no client
/// could answer the question without reading a manifest per snapshot.
/// </para>
/// <para>
/// Both routes are covered on purpose. <see cref="CatalogueProjector"/> runs
/// on the ordinary publication path; <see cref="ForensicRebuilder"/> runs when
/// the catalogue is gone and everything must be recovered from the store
/// alone. A field carried by one and dropped by the other would be a value
/// that quietly disappears the first time somebody needs a rebuild — which is
/// exactly the moment they are least able to notice.
/// </para>
/// </remarks>
[TestClass]
public sealed class ConsistencyMethodTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x91, 16)];

    [TestMethod]
    public async Task ThePublicationPath_CarriesTheConsistencyMethodIntoTheCatalogue()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "projected.db"), Repo);

        await PublishAsync(store, keys, hierarchy, catalogue);

        var row = Assert.ContainsSingle(catalogue.EnumerateSnapshots());

        // Every capture this build takes is live, and saying so is the point:
        // the value was always 1 and there was previously no way for anyone to
        // learn that without decoding the manifest themselves.
        Assert.AreEqual((byte)1, row.ConsistencyMethod);
    }

    [TestMethod]
    public async Task AForensicRebuild_RecoversTheConsistencyMethodFromTheStoreAlone()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        using (var original = CatalogueDb.Open(Path.Combine(SpoolDirectory, "original.db"), Repo))
        {
            await PublishAsync(store, keys, hierarchy, original);
        }

        // A catalogue that never saw the publication, rebuilt from the store's
        // own objects — the path a person takes after losing the cache.
        using var rebuilder = new ForensicRebuilder(store, Repo, hierarchy);
        using var rebuilt = CatalogueDb.Open(Path.Combine(SpoolDirectory, "rebuilt.db"), Repo);
        var report = await rebuilder.RebuildAsync(
            rebuilt, new ForensicTarget.Everything(), CancellationToken.None);

        Assert.IsTrue(report.TargetSatisfied);

        var row = Assert.ContainsSingle(
            candidate => candidate.SnapshotId.Span.SequenceEqual(SnapshotId), rebuilt.EnumerateSnapshots());
        Assert.AreEqual((byte)1, row.ConsistencyMethod);
    }

    private async Task PublishAsync(
        Storage.Local.LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        KeyHierarchy hierarchy,
        CatalogueDb catalogue)
    {
        var source = new FakeFileSystemSource();
        source.AddFile("ledger.bin", BuildTestFile(regions: 4));

        await new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = SnapshotId,
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "consistency-method-tests/1.0",
                },
                CancellationToken.None);
    }
}

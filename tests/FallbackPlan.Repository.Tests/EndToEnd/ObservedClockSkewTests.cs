using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format;
using FallbackPlan.Repository.Index;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The skew a capture observed reaches its snapshot's manifest
/// (<c>observed_clock_skew_ms</c>, specification 06 §6 key 14) and every
/// route that fills a catalogue — the capture's own projection, the
/// projector a rebuild runs and the forensic rebuild — so it is queryable per
/// snapshot (NFR-TIME-002, ADR-0077).
/// </summary>
/// <remarks>
/// <para>
/// Absent stays absent. A capture that had no reference records nothing, and
/// every route reads that back as null, never as 0: "in step with its peer"
/// and "nothing to compare with" are different findings about a clock.
/// </para>
/// <para>
/// The capture's own projection and a rebuild's agree on the capture time
/// too. The rebuild has always read the manifest's completion stamp; the
/// capture wrote its start, so a snapshot's time moved the first time its
/// catalogue was rebuilt. Both now write the manifest's value.
/// </para>
/// </remarks>
[TestClass]
public sealed class ObservedClockSkewTests : ArchiveTestHarness
{
    private const ulong CaptureStarted = 1_722_600_000_000;
    private const long ThreeHoursBehind = 10_800_000;

    private static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x92, 16)];

    [TestMethod]
    public async Task ThePublicationPath_RecordsTheSkewItWasHanded_InTheManifestAndTheCatalogue()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var live = CatalogueDb.Open(Path.Combine(SpoolDirectory, "live.db"), Repo);

        await PublishAsync(store, keys, credential, live, observedSkew: ThreeHoursBehind);

        Assert.AreEqual(ThreeHoursBehind, Assert.ContainsSingle(live.EnumerateSnapshots()).ObservedClockSkewMs);
        Assert.AreEqual(
            ThreeHoursBehind, (await ProjectAsync(store, keys, credential)).ObservedClockSkewMs,
            "the projector reads the manifest, so this is key 14 as signed");
    }

    [TestMethod]
    public async Task ACaptureWithNoReference_RecordsNone_NotZero()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var live = CatalogueDb.Open(Path.Combine(SpoolDirectory, "live.db"), Repo);

        await PublishAsync(store, keys, credential, live, observedSkew: null);

        Assert.IsNull(Assert.ContainsSingle(live.EnumerateSnapshots()).ObservedClockSkewMs);
        Assert.IsNull((await ProjectAsync(store, keys, credential)).ObservedClockSkewMs);
    }

    [TestMethod]
    public async Task AForensicRebuild_RecoversTheSkewFromTheStoreAlone()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using (var original = CatalogueDb.Open(Path.Combine(SpoolDirectory, "original.db"), Repo))
        {
            await PublishAsync(store, keys, credential, original, observedSkew: -ThreeHoursBehind);
        }

        using var rebuilder = new ForensicRebuilder(store, Repo, credential);
        using var rebuilt = CatalogueDb.Open(Path.Combine(SpoolDirectory, "forensic.db"), Repo);
        var report = await rebuilder.RebuildAsync(rebuilt, new ForensicTarget.Everything(), CancellationToken.None);

        Assert.IsTrue(report.TargetSatisfied);
        Assert.AreEqual(
            -ThreeHoursBehind,
            Assert.ContainsSingle(row => row.SnapshotId.Span.SequenceEqual(SnapshotId), rebuilt.EnumerateSnapshots())
                .ObservedClockSkewMs);
    }

    [TestMethod]
    public async Task TheCapturesOwnRow_AndARebuiltOne_AgreeOnWhenTheCaptureWasTaken()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var live = CatalogueDb.Open(Path.Combine(SpoolDirectory, "live.db"), Repo);

        await PublishAsync(store, keys, credential, live, observedSkew: null, completedAt: CaptureStarted + 5_000);

        var capturedLive = Assert.ContainsSingle(live.EnumerateSnapshots()).CapturedAt;
        Assert.AreEqual(CaptureStarted + 5_000, capturedLive, "the manifest's completion stamp, as a rebuild reads it");
        Assert.AreEqual(capturedLive, (await ProjectAsync(store, keys, credential)).CapturedAt);
    }

    private async Task<CatalogueSnapshot> ProjectAsync(
        Storage.Local.LocalFileSystemObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential)
    {
        using var rebuilt = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"projected-{Guid.NewGuid():n}.db"), Repo);
        await new CatalogueRebuilder(new IndexLoader(store, Repo, credential)).RebuildAsync(
            rebuilt, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null, CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        await CatalogueProjector.ProjectAsync(rebuilt, reader, store, Repo, keys, credential, CancellationToken.None);

        return Assert.ContainsSingle(rebuilt.EnumerateSnapshots());
    }

    private async Task PublishAsync(
        Storage.Local.LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        RepositoryWriteCredential credential,
        CatalogueDb catalogue,
        long? observedSkew,
        ulong? completedAt = null)
    {
        var source = new FakeFileSystemSource();
        source.AddFile("ledger.bin", BuildTestFile(regions: 4));

        await new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, FormatVersions.RelocatableRecords, observer: null, catalogue: catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = SnapshotId,
                    NowUnixMilliseconds = CaptureStarted,
                    Clock = completedAt is { } at ? () => at : null,
                    ObservedClockSkewMs = observedSkew,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "observed-clock-skew-tests/1.0",
                },
                CancellationToken.None);
    }
}

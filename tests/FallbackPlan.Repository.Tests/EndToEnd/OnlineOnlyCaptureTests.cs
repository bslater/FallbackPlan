using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Online-only files, whose content a cloud provider keeps and fetches when
/// they are read (ADR-0185, FR-SNP-009). Once the provider fails to deliver
/// one, the run fetches no other online-only file: each is named as not
/// fetched, with the file that failed and why, and the next backup fetches it
/// again. An unchanged online-only file is never fetched at all
/// (NFR-PERF-003). A file gone or refused, or a file on this device that will
/// not read, is not the provider failing and stops nothing.
/// </summary>
/// <remarks>
/// The fake source is what makes the provider's failure assertable on every
/// platform: a real one needs a cloud provider on the machine, which no CI
/// runner has.
/// </remarks>
[TestClass]
public sealed class OnlineOnlyCaptureTests : ArchiveTestHarness
{
    private const string TimedOut = "The cloud operation was not completed before the time-out period expired.";

    [TestMethod]
    public async Task AnOnlineOnlyFileNotDelivered_StopsTheRunFetchingTheRest()
    {
        var source = new FakeFileSystemSource();
        var first = Online(source, "a.bin", 1);
        first.OpenFailure = new IOException(TimedOut);
        var second = Online(source, "b.bin", 2);
        source.AddFile("local.bin", Deterministic(4_000, 3));
        var below = Online(source, "sub/c.bin", 4);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        // The provider was asked once. Asked for each file, it would have
        // spent its whole time-out on every one of them.
        Assert.AreEqual(1, first.OpenAttempts);
        Assert.AreEqual(0, second.OpenAttempts, "b.bin was not fetched");
        Assert.AreEqual(0, below.OpenAttempts, "sub/c.bin was not fetched");

        // The file on this device is still backed up.
        Assert.AreEqual("local.bin", Assert.ContainsSingle(published.Files).RelativePath);

        Assert.HasCount(3, published.Failures);
        var failed = Detail(published, "a.bin");
        Assert.Contains("online-only", failed, StringComparison.Ordinal);
        Assert.Contains(TimedOut, failed, StringComparison.Ordinal);
        foreach (var path in new[] { "b.bin", "sub/c.bin" })
        {
            var notFetched = Detail(published, path);
            Assert.Contains("not fetched", notFetched, StringComparison.Ordinal);
            Assert.Contains("a.bin", notFetched, StringComparison.Ordinal);
            Assert.Contains(TimedOut, notFetched, StringComparison.Ordinal);
            Assert.AreEqual(CaptureFailureReason.IoError, ReasonFor(published, path));
        }
    }

    [TestMethod]
    public async Task AFetchThatFailsPartWay_StopsTheRunToo()
    {
        // A provider that starts to deliver and stops is failing as surely as
        // one that never starts.
        var source = new FakeFileSystemSource();
        var first = Online(source, "a.bin", 1, length: 200_000);
        first.FailReadAfterBytes = 8 * 1024;
        first.ReadFailure = new IOException(TimedOut);
        var second = Online(source, "b.bin", 2);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        Assert.AreEqual(0, second.OpenAttempts, "b.bin was not fetched");
        Assert.IsEmpty(published.Files);
        Assert.Contains("a.bin", Detail(published, "b.bin"), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AFileGoneOrRefusedOrOnThisDevice_StopsNoFetching()
    {
        // None of these is the provider failing: a file deleted since the
        // scan, a file this account may not read, and a file on this device
        // that will not read. Each fails alone, as it always has.
        var source = new FakeFileSystemSource();
        Online(source, "a-gone.bin", 1).OpenFailure = new FileNotFoundException("no longer there");
        Online(source, "b-denied.bin", 2).OpenFailure = new UnauthorizedAccessException("denied");
        source.AddFile("c-local.bin", Deterministic(4_000, 3)).OpenFailure = new IOException("a bad sector");
        var fetched = Online(source, "d.bin", 4);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        Assert.AreEqual(1, fetched.Opens);
        Assert.AreEqual("d.bin", Assert.ContainsSingle(published.Files).RelativePath);
        Assert.HasCount(3, published.Failures);
        Assert.AreEqual("a bad sector", Detail(published, "c-local.bin"));
        foreach (var failure in published.Failures)
        {
            Assert.DoesNotContain("not fetched", failure.Detail, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task AFileNotFetched_IsFetchedByTheNextBackup()
    {
        var source = new FakeFileSystemSource();
        var first = Online(source, "a.bin", 1);
        first.OpenFailure = new IOException(TimedOut);
        var second = Online(source, "b.bin", 2);
        source.AddFile("local.bin", Deterministic(4_000, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        // The provider answers again.
        first.OpenFailure = null;
        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        Assert.IsEmpty(published.Failures);
        var byPath = published.Files.ToDictionary(file => file.RelativePath);
        Assert.IsFalse(byPath["a.bin"].Reused);
        Assert.IsFalse(byPath["b.bin"].Reused);
        Assert.IsTrue(byPath["local.bin"].Reused);
        Assert.AreEqual(1, first.Opens);
        Assert.AreEqual(1, second.Opens);
    }

    [TestMethod]
    public async Task AnUnchangedOnlineOnlyFile_IsNeverFetchedAgain()
    {
        // What makes fetching on demand affordable: a file is fetched when it
        // is new or changed, and once (NFR-PERF-003).
        var source = new FakeFileSystemSource();
        var kept = Online(source, "kept.bin", 1);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        Assert.AreEqual(1, kept.OpenAttempts);

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        Assert.AreEqual(1, kept.OpenAttempts, "an unchanged online-only file is not fetched again");
        Assert.IsTrue(Assert.ContainsSingle(published.Files).Reused);
    }

    private static FakeFileSystemSource.Node Online(
        FakeFileSystemSource source, string path, byte seed, int length = 4_000)
    {
        var node = source.AddFile(path, Deterministic(length, seed));
        node.OnlineOnly = true;
        return node;
    }

    private static string Detail(PublishedTreeSnapshot published, string path) =>
        published.Failures.Single(failure => PathOf(failure) == path).Detail;

    private static CaptureFailureReason ReasonFor(PublishedTreeSnapshot published, string path) =>
        published.Failures.Single(failure => PathOf(failure) == path).Reason;

    private static string PathOf(CaptureFailure failure) =>
        string.Join('/', failure.PathComponents.Select(component => Encoding.UTF8.GetString(component.Span)));

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, CatalogueDb catalogue) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            FormatVersions.SealedDataPlane,
            observer: null,
            catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now = 1_722_600_000_000) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }
}

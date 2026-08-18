using FallbackPlan.Domain;
using FallbackPlan.Repository.Catalogue;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Restoring to somewhere that does not exist yet (FR-RST-002).
/// </summary>
/// <remarks>
/// <para>
/// The surveyed changelog records a restore failing when the containing
/// folder had not itself been restored and, four years later, a rewritten
/// restore path not creating a missing folder structure — the same defect,
/// twice, either side of a rewrite.
/// </para>
/// <para>
/// It recurs because the happy path hides it. A restore to the original
/// location finds every directory already there, and so does a test that
/// creates the output directory before calling the executor. The gap only
/// opens on the path that matters most: restoring to a fresh machine, into an
/// empty directory, which is the disaster-recovery case and the one nobody
/// rehearses. Every restore test in this suite before this class created its
/// destination directory first.
/// </para>
/// </remarks>
[TestClass]
public sealed class RestoreIntoMissingStructureTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private CatalogueDb OpenCatalogue(string name) =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, CatalogueDb catalogue, string spoolName)
    {
        var spool = Path.Combine(SpoolDirectory, spoolName);
        Directory.CreateDirectory(spool);

        return new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool, observer: null, catalogue);
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-structure-tests/1.0",
    };

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)(seed + index * 31);
        }

        return data;
    }

    /// <summary>
    /// Publishes a small tree several directories deep and returns a plan for
    /// the whole of it.
    /// </summary>
    private async Task<(RestorePlan Plan, RestoreTargetProfile Target, Storage.Local.LocalFileSystemObjectStore Store, RepositoryKeySet Keys)>
        PublishDeepTreeAsync(string name, byte seed)
    {
        var source = new FakeFileSystemSource();
        source.AddFile("shallow.bin", Deterministic(600, 1), fileId: 9_100);
        source.AddFile("one/two/three/deep.bin", Deterministic(2_000, 2), fileId: 9_101);
        source.AddFile("one/two/sibling.bin", Deterministic(900, 3), fileId: 9_102);
        source.AddFile("one/other/leaf.bin", Deterministic(1_500, 4), fileId: 9_103);

        var store = CreateStore();
        var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue(name);
        await CreateOrchestrator(store, keys, hierarchy, catalogue, name)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
        return (plan, target, store, keys);
    }

    /// <summary>
    /// The disaster-recovery case: an output directory that does not exist at
    /// all, and a tree several levels deep inside it.
    /// </summary>
    [TestMethod]
    public async Task RestoreExecution_TheOutputDirectoryDoesNotExist_CreatesTheWholeStructure()
    {
        var (plan, target, store, keys) = await PublishDeepTreeAsync("missing-root", 0xF1);
        using var _ = keys;

        // Deliberately NOT created — this is the whole test.
        var output = Path.Combine(SpoolDirectory, "no-such-directory", "nor-this-one");
        Assert.IsFalse(Directory.Exists(output));

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = ExistingDestinationPolicy.Fail,
                RunId = "missing-root-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);

        foreach (var relative in new[]
                 {
                     "shallow.bin",
                     Path.Combine("one", "two", "three", "deep.bin"),
                     Path.Combine("one", "two", "sibling.bin"),
                     Path.Combine("one", "other", "leaf.bin"),
                 })
        {
            Assert.IsTrue(File.Exists(Path.Combine(output, relative)), $"{relative} was not restored");
        }
    }

    /// <summary>
    /// And the bytes are right, not merely the paths — a restore that creates
    /// the tree and then writes the wrong thing into it is no better.
    /// </summary>
    [TestMethod]
    public async Task RestoreExecution_IntoAnEmptyDirectory_RestoresEveryFileByteIdentically()
    {
        var (plan, target, store, keys) = await PublishDeepTreeAsync("empty-target", 0xF2);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "empty-target-out");
        Directory.CreateDirectory(output);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = ExistingDestinationPolicy.Fail,
                RunId = "empty-target-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        SequenceAssert.AreEqual(
            Deterministic(2_000, 2),
            File.ReadAllBytes(Path.Combine(output, "one", "two", "three", "deep.bin")));
        SequenceAssert.AreEqual(
            Deterministic(1_500, 4),
            File.ReadAllBytes(Path.Combine(output, "one", "other", "leaf.bin")));
    }

    /// <summary>
    /// A partially present structure is the awkward middle: some directories
    /// exist, some do not, and the restore must neither fail on the ones that
    /// are there nor skip the ones that are not.
    /// </summary>
    [TestMethod]
    public async Task RestoreExecution_SomeDirectoriesAlreadyExist_FillsInOnlyTheMissingOnes()
    {
        var (plan, target, store, keys) = await PublishDeepTreeAsync("partial-structure", 0xF3);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "partial-structure-out");
        Directory.CreateDirectory(Path.Combine(output, "one", "two"));

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = ExistingDestinationPolicy.Fail,
                RunId = "partial-structure-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.IsTrue(File.Exists(Path.Combine(output, "one", "two", "sibling.bin")), "an existing directory blocked a file");
        Assert.IsTrue(File.Exists(Path.Combine(output, "one", "two", "three", "deep.bin")), "a missing directory was not created");
        Assert.IsTrue(File.Exists(Path.Combine(output, "one", "other", "leaf.bin")), "a missing directory was not created");
    }

    /// <summary>
    /// Restoring a single deep file — not the whole tree — must still create
    /// the directories above it. This is the common case of "get me back that
    /// one document", and the one where a missing parent is most likely,
    /// because nothing else in the restore has any reason to create it.
    /// </summary>
    [TestMethod]
    public async Task RestoreExecution_OneDeepFileOnly_CreatesTheDirectoriesAboveIt()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("one/two/three/deep.bin", Deterministic(2_000, 2), fileId: 9_201);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("single-deep");
        await CreateOrchestrator(store, keys, hierarchy, catalogue, "single-deep")
            .PublishAsync(Job(source, 0xF4), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xF4, 16).ToArray(), "one/two/three/deep.bin", target);

        var output = Path.Combine(SpoolDirectory, "single-deep-out");
        Assert.IsFalse(Directory.Exists(output));

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = ExistingDestinationPolicy.Fail,
                RunId = "single-deep-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        SequenceAssert.AreEqual(
            Deterministic(2_000, 2),
            File.ReadAllBytes(Path.Combine(output, "one", "two", "three", "deep.bin")));
    }
}

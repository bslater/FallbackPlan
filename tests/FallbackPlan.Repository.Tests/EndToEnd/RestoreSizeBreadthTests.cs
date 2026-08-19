using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Size breadth through the real executor to real disk (FR-RST-004). The
/// coverage survey found the suite's structural blind spot: the harness
/// segments at 64 KiB, and only one test had ever pushed a multi-segment
/// file through <see cref="RestoreExecutor"/> with its bytes compared — at
/// 80 KB, barely over the line. Everything larger lived at the engine level,
/// restored into a <see cref="MemoryStream"/>. These tests walk a size
/// ladder from the empty file across both the segment boundary and the blob
/// boundary in one snapshot, and every restored byte is compared.
/// </summary>
[TestClass]
public sealed class RestoreSizeBreadthTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private const int Segment = 64 * 1024;

    /// <summary>
    /// The ladder: empty, one byte, a byte either side of the segment
    /// boundary and the boundary itself, five segments (crossing the
    /// 256 KiB blob target), and ~2 MiB across several blobs.
    /// </summary>
    private static readonly (string Path, int Length)[] Ladder =
    [
        ("sizes/empty.bin", 0),
        ("sizes/one-byte.bin", 1),
        ("sizes/under-segment.bin", Segment - 1),
        ("sizes/exact-segment.bin", Segment),
        ("sizes/over-segment.bin", Segment + 1),
        ("sizes/five-segments.bin", 5 * Segment),
        ("sizes/many-blobs.bin", 2 * 1024 * 1024),
    ];

    [TestMethod]
    public async Task RestoreExecution_TheWholeSizeLadder_LandsByteIdenticalOnDisk()
    {
        var (contents, plan, target, store, keys) = await PublishLadderAsync("ladder", 0xD1);
        using var _ = keys;

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var output = Path.Combine(SpoolDirectory, "ladder-out");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "ladder-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        // Every rung compared, not just counted: the boundary cases are
        // exactly where an off-by-one in segment reassembly would hide
        // behind an existence check.
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        foreach (var (path, content) in contents)
        {
            var landed = Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar));
            SequenceAssert.AreEqual(content, await File.ReadAllBytesAsync(landed));
        }

        // The empty file is a restored item like any other — present on
        // disk at zero bytes, accounted for as restored, never skipped.
        var empty = Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "sizes/empty.bin"));
        Assert.AreEqual("restored", empty.Outcome);
        Assert.AreEqual(0ul, empty.Bytes);
        Assert.AreEqual(0, new FileInfo(Path.Combine(output, "sizes", "empty.bin")).Length);
    }

    [TestMethod]
    public async Task RestoreExecution_ASelectionMixingSizes_RestoresOnlyItAndStillByteIdentical()
    {
        // Size breadth composed with multi-select (ADR-0041): the largest
        // and the smallest rung picked by prefix from across the tree, one
        // run, and the unpicked rungs never land.
        var (contents, _, target, store, keys) = await PublishLadderAsync("pick", 0xD2);
        using var _ = keys;

        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue-pick.db"), Repo);
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xD2, 16).ToArray(),
            ["sizes/many-blobs.bin", "sizes/empty.bin"], target);
        SequenceAssert.AreEqual(
            new[] { "sizes/empty.bin", "sizes/many-blobs.bin" },
            [.. plan.Items.Select(item => item.Path)]);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var output = Path.Combine(SpoolDirectory, "pick-out");
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "pick-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        SequenceAssert.AreEqual(
            contents["sizes/many-blobs.bin"],
            await File.ReadAllBytesAsync(Path.Combine(output, "sizes", "many-blobs.bin")));
        Assert.AreEqual(0, new FileInfo(Path.Combine(output, "sizes", "empty.bin")).Length);
        Assert.IsFalse(File.Exists(Path.Combine(output, "sizes", "five-segments.bin")),
            "an unselected rung must not restore");
    }

    private async Task<(
        Dictionary<string, byte[]> Contents,
        RestorePlan Plan,
        RestoreTargetProfile Target,
        Storage.Local.LocalFileSystemObjectStore Store,
        RepositoryKeySet Keys)> PublishLadderAsync(string name, byte seed)
    {
        var contents = new Dictionary<string, byte[]>();
        var source = new FakeFileSystemSource();
        var fileId = 9_001ul;
        foreach (var (path, length) in Ladder)
        {
            var content = Deterministic(length, (byte)(seed + contents.Count));
            contents[path] = content;
            source.AddFile(path, content, fileId: fileId++);
        }

        var store = CreateStore();
        var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);

        var spool = Path.Combine(SpoolDirectory, name);
        Directory.CreateDirectory(spool);
        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool, observer: null, catalogue);
        await orchestrator.PublishAsync(Job(source, seed), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
        return (contents, plan, target, store, keys);
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
        ClientVersion = "restore-size-breadth-tests/1.0",
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

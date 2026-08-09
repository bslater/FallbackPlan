using System.Security.Cryptography;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The interruption matrix over the multi-file path (phase-1 wave T1): a
/// kill at each publication step of a <see cref="SnapshotJob"/> leaves the
/// previously committed snapshot restorable, and a fresh process completes
/// the tree job afterwards — the same guarantees as the single-stream rows,
/// proven over the scanner-driven pipeline. Wave V grows this into the full
/// row-by-row matrix.
/// </summary>
[TestClass]
public sealed class TreeSnapshotInterruptionTests : InterruptionHarness
{
    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "fbp-tree-interruption", Guid.NewGuid().ToString("n"));

    private Dictionary<string, byte[]> BuildSourceTree()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["a.bin"] = BuildFile(seed: 11, regions: 2),
            ["nested/b.bin"] = BuildFile(seed: 12, regions: 1),
            ["nested/deep/c.bin"] = BuildFile(seed: 13, regions: 1),
        };

        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_sourceRoot, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }

        return files;
    }

    private SnapshotJob TreeJob(byte snapshotSeed) => new()
    {
        Source = new LocalFileSystemSource(),
        RootPath = _sourceRoot,
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "interruption-tests/1.0",
    };

    // An explicit initializer rather than a collection expression: Visual
    // Studio's analyzer lowers the expression through a path that trips
    // CA1825 (observed on Windows), and warnings are errors everywhere.
    public static IEnumerable<object[]> KillPoints() =>
    [
        [PublicationStep.PublishIntent],
        [PublicationStep.UploadBlobs],
        [PublicationStep.PublishIndexDeltas],
        [PublicationStep.PublishSnapshot],
        [PublicationStep.RetireIntent],
    ];

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    public async Task PublishTree_AtAnyConcurrency_ProducesAnIdenticalSnapshot(int concurrency)
    {
        var files = BuildSourceTree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var published = await CreateOrchestrator(store, keys, hierarchy, concurrency: concurrency)
            .PublishAsync(TreeJob(0xC4), CancellationToken.None);

        Assert.AreEqual(files.Count, published.Files.Count);
        Assert.IsEmpty(published.Failures);

        // NFR-PERF-002 over the scanner-driven path. The single-stream theory
        // in ConcurrentUploadTests covers one file; this is the multi-file case,
        // which is the one where a session's blob continuity, its dedup set and
        // its ordinal sequence are all shared across files rather than reset.
        foreach (var file in published.Files)
        {
            var archive = file.Archive;
            Assert.IsNotNull(archive);

            var content = files[file.RelativePath.Replace('\\', '/')];
            SequenceAssert.AreEqual(SHA256.HashData(content), archive.WholeFileHash);
            Assert.AreEqual(content.Length, archive.LogicalLength);

            // 06 §3.2: the encoded references must ascend by logical offset and
            // tile the file exactly. Producing segments concurrently is allowed;
            // emitting them out of order is not.
            long expectedOffset = 0;
            foreach (var reference in archive.SegmentReferences)
            {
                Assert.AreEqual(expectedOffset, reference.LogicalOffset);
                expectedOffset += reference.LogicalLength;
            }

            Assert.AreEqual(content.Length, expectedOffset);
        }
    }

    [TestMethod]
    [DynamicData(nameof(KillPoints))]
    public async Task PublishTree_KilledAtAnyStep_LeavesTheCommittedSnapshotIntactAndCompletesOnRerun(
        PublicationStep killAfter)
    {
        BuildSourceTree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // A committed single-stream baseline predates the tree job.
        var baseline = BuildFile(seed: 1);
        using (var source = new MemoryStream(baseline))
        {
            await CreateOrchestrator(store, keys, hierarchy)
                .PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        // The tree publication dies between steps.
        await Assert.ThrowsExactlyAsync<PublicationKilledException>(async () =>
            await CreateOrchestrator(store, keys, hierarchy, new KillAfter(killAfter))
                .PublishAsync(TreeJob(0xB2), CancellationToken.None));

        // The committed snapshot is untouched by the wreckage.
        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, snapshotSeed: 0xA1));

        // A fresh process completes the same tree job end to end.
        var published = await CreateOrchestrator(store, keys, hierarchy)
            .PublishAsync(TreeJob(0xB3), CancellationToken.None);

        Assert.AreEqual(3, published.Files.Count);
        Assert.IsEmpty(published.Failures);

        // And the baseline still restores after completion.
        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, snapshotSeed: 0xA1));
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_sourceRoot))
        {
            Directory.Delete(_sourceRoot, recursive: true);
        }

        base.Dispose(disposing);
    }
}

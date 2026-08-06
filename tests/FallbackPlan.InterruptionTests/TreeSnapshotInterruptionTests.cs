using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The interruption matrix over the multi-file path (phase-1 wave T1): a
/// kill at each publication step of a <see cref="SnapshotJob"/> leaves the
/// previously committed snapshot restorable, and a fresh process completes
/// the tree job afterwards — the same guarantees as the single-stream rows,
/// proven over the scanner-driven pipeline. Wave V grows this into the full
/// row-by-row matrix.
/// </summary>
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
    public static TheoryData<PublicationStep> KillPoints() => new()
    {
        PublicationStep.PublishIntent,
        PublicationStep.UploadBlobs,
        PublicationStep.PublishIndexDeltas,
        PublicationStep.PublishSnapshot,
        PublicationStep.RetireIntent,
    };

    [Theory]
    [MemberData(nameof(KillPoints))]
    public async Task A_killed_tree_publication_never_harms_the_committed_snapshot_and_a_fresh_process_completes(
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
        await Assert.ThrowsAsync<PublicationKilledException>(async () =>
            await CreateOrchestrator(store, keys, hierarchy, new KillAfter(killAfter))
                .PublishAsync(TreeJob(0xB2), CancellationToken.None));

        // The committed snapshot is untouched by the wreckage.
        Assert.Equal(baseline, await RestoreSnapshotAsync(store, keys, snapshotSeed: 0xA1));

        // A fresh process completes the same tree job end to end.
        var published = await CreateOrchestrator(store, keys, hierarchy)
            .PublishAsync(TreeJob(0xB3), CancellationToken.None);

        Assert.Equal(3, published.Files.Count);
        Assert.Empty(published.Failures);

        // And the baseline still restores after completion.
        Assert.Equal(baseline, await RestoreSnapshotAsync(store, keys, snapshotSeed: 0xA1));
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

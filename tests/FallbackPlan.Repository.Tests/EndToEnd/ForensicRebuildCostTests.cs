using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Index;

namespace FallbackPlan.Repository.Tests.EndToEnd;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;
using Counting = FallbackPlan.TestSupport.CountingObjectStore;
using FallbackPlan.TestSupport;

/// <summary>
/// What a forensic rebuild costs the store (NFR-PERF-012, NFR-PERF-015):
/// the assertion half of the rate budget, beside the figure
/// <c>PerformanceTests/RebuildRateBenchmark</c> publishes.
/// </summary>
/// <remarks>
/// <para>
/// A rate is the wrong thing to assert. A stopwatch in a unit suite is a
/// flake, and a rate target is unreachable — for a reason no faster disk
/// can fix — if the scan re-reads what it already holds. So what is
/// asserted here is the <b>work</b>: how many ranged reads and prefix
/// listings a rebuild issues, which is scale-invariant and is what a rate
/// is downstream of.
/// </para>
/// <para>
/// Writing it found the rebuild paying four ranged reads per walked record
/// where one would do: the target walk opened a fresh
/// <c>BlobReader</c> for every record it read, re-issuing the locator,
/// footer and envelope reads and re-decrypting and re-decoding a record
/// table that may hold 65 536 entries, then scanning that table linearly
/// for the one record it wanted. The blob count did not move between 10
/// files and 40 while the read count nearly quadrupled. The marginal case
/// below is what notices, and it is the reason this suite exists rather
/// than a stopwatch.
/// </para>
/// </remarks>
[TestClass]
public sealed class ForensicRebuildCostTests : ArchiveTestHarness
{
    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "fbp-rebuild-cost", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public async Task ARebuildListsEachPrefixExactlyOnce()
    {
        var (counting, _) = await RebuildAsync(fileCount: 20);

        // Three listings, each opened once: the bounded metadata class, the
        // snapshot prefix the target is resolved from, and the data class
        // that phase 2 stops scanning the moment the target is satisfied.
        // A fourth, or a repeat, would mean a pass that re-enumerates.
        Assert.HasCount(3, counting.Listings);
        SequenceAssert.AreEqual(
            new[] { "blobs/meta/", "snapshots/", "blobs/data/" },
            counting.Listings);
    }

    [TestMethod]
    public async Task AWalkedRecordCostsOneReadAndNotOneFooter()
    {
        // The marginal cost, which is the scale-invariant form of the claim
        // and the one that survives a bigger repository. Doubling the files
        // in a snapshot adds one metadata record to walk per file; each
        // should cost the one ranged read that fetches its bytes, not that
        // read plus a fresh three-read footer open of the blob the walk is
        // already standing in.
        var (small, smallFiles) = await RebuildAsync(fileCount: 20);
        var (large, largeFiles) = await RebuildAsync(fileCount: 40, reuseSource: false);

        var marginal = (large.Reads - small.Reads) / (double)(largeFiles - smallFiles);

        // Measured 1.1 reads per added file with the reader held across the
        // walk, against 4.4 when it was opened per record.
        Assert.IsLessThan(
            2.0,
            marginal,
            $"{small.Reads} reads at {smallFiles} files, {large.Reads} at {largeFiles}: {marginal:F1} per file");
    }

    private async Task<(Counting Store, int Files)> RebuildAsync(int fileCount, bool reuseSource = true)
    {
        var root = reuseSource ? _sourceRoot : _sourceRoot + "-b";
        var spool = Path.Combine(SpoolDirectory, reuseSource ? "a" : "b");
        Directory.CreateDirectory(spool);

        var random = new Random(11);
        for (var index = 0; index < fileCount; index++)
        {
            var content = new byte[2_000];
            random.NextBytes(content);
            var full = Path.Combine(root, $"dir-{index / 8:D2}", $"file-{index:D3}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, content);
        }

        var storeRoot = Path.Combine(StoreRoot, reuseSource ? "a" : "b");
        Directory.CreateDirectory(storeRoot);
        var store = new Storage.Local.LocalFileSystemObjectStore(storeRoot);
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool,
            FormatVersions.SealedDataPlane);

        await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = new LocalFileSystemSource(),
                Roots = [new ScanRoot(root)],
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = Enumerable.Repeat((byte)0x11, 16).ToArray(),
                NowUnixMilliseconds = 1_722_600_000_000,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "fallbackplan-tests/1.0",
            },
            CancellationToken.None);

        // The E2 premise the rate is measured under: the index plane is gone
        // and the footers are all there is.
        foreach (var file in Directory
            .EnumerateFiles(Path.Combine(storeRoot, "index"), "*", SearchOption.AllDirectories)
            .ToList())
        {
            File.Delete(file);
        }

        var counting = new Counting(store);
        using var rebuilder = new ForensicRebuilder(counting, Repo, credential);
        using var catalogue = Catalogue.Open(Path.Combine(spool, "forensic.db"), Repo);

        var report = await rebuilder.RebuildAsync(
            catalogue,
            new ForensicTarget.Snapshot(Enumerable.Repeat((byte)0x11, 16).ToArray()),
            CancellationToken.None);

        Assert.IsTrue(report.TargetSatisfied, string.Join("; ", report.Findings.Select(finding => finding.Detail)));
        return (counting, fileCount);
    }

    [TestCleanup]
    public void CleanupSources()
    {
        foreach (var root in new[] { _sourceRoot, _sourceRoot + "-b" })
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

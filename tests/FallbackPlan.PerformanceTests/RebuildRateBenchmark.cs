using System.Diagnostics;
using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.PerformanceTests;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;

/// <summary>
/// NFR-PERF-012 forensic rebuild rate: a tree published, its whole index
/// plane deleted, and the repository rebuilt from recovery footers alone —
/// the E2 premise, timed. Reports blobs per second against the stated
/// >= 500 blobs/s, and beside it the ranged reads per blob and per record,
/// which is what a rate is downstream of: a scan that re-reads cannot
/// reach any rate, on any disk.
/// </summary>
/// <remarks>
/// Not a BenchmarkDotNet case. It publishes a repository and deletes files
/// from it, which is setup far too heavy for an iterated micro-benchmark,
/// and the figure wanted is one end-to-end pass rather than a per-call
/// mean. Run with <c>dotnet run -c Release -- rebuild-rate [files]</c>.
/// </remarks>
public static class RebuildRateBenchmark
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    /// <summary>Publishes, deletes the index plane, and times the rebuild.</summary>
    public static async Task<int> RunAsync(int files)
    {
        var root = Path.Combine(Path.GetTempPath(), "fbp-bench-rebuild", Guid.NewGuid().ToString("n"));
        var source = Path.Combine(root, "source");
        var store = Path.Combine(root, "store");
        var spool = Path.Combine(root, "spool");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(store);
        Directory.CreateDirectory(spool);

        try
        {
            var random = new Random(11);
            for (var index = 0; index < files; index++)
            {
                var content = new byte[8_000];
                random.NextBytes(content);
                var full = Path.Combine(source, $"dir-{index / 64:D4}", $"file-{index:D6}.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllBytesAsync(full, content);
            }

            var objectStore = new LocalFileSystemObjectStore(store);
            using var authority = TestAuthority.Create();
            using var keys = RepositoryKeySet.FromWriteCredential(authority.Credential);

            var orchestrator = new PublicationOrchestrator(
                CapturePolicyFor(), Repo, Writer, KeyGeneration.Zero, keys, authority.Credential, objectStore,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool,
                FormatVersions.SealedDataPlane);

            var publish = Stopwatch.StartNew();
            await orchestrator.PublishAsync(
                new SnapshotJob
                {
                    Source = new LocalFileSystemSource(),
                    Roots = [new ScanRoot(source)],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat((byte)0x11, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "fallbackplan-bench/1.0",
                },
                CancellationToken.None);
            publish.Stop();

            // The E2 premise: every index object is gone and the footers are
            // all there is.
            foreach (var file in Directory
                .EnumerateFiles(Path.Combine(store, "index"), "*", SearchOption.AllDirectories)
                .ToList())
            {
                File.Delete(file);
            }

            var counting = new CountingObjectStore(objectStore);
            using var rebuilder = new ForensicRebuilder(counting, Repo, authority.Credential);
            using var catalogue = Catalogue.Open(Path.Combine(spool, "forensic.db"), Repo);

            var rebuild = Stopwatch.StartNew();
            var report = await rebuilder.RebuildAsync(
                catalogue, new ForensicTarget.Everything(), CancellationToken.None);
            rebuild.Stop();

            var blobs = report.MetadataBlobsScanned + report.DataBlobsScanned;
            var seconds = rebuild.Elapsed.TotalSeconds;

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Forensic rebuild rate (NFR-PERF-012) — {files:N0} files, published in {publish.Elapsed.TotalSeconds:F1} s"));
            Console.WriteLine();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  blobs scanned      {blobs:N0} ({report.MetadataBlobsScanned:N0} metadata, {report.DataBlobsScanned:N0} data)"));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  records indexed    {report.RecordsIndexed:N0}"));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  elapsed            {seconds:F2} s"));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  records/s          {report.RecordsIndexed / Math.Max(seconds, 1e-9):N0}"));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  blobs/s            {blobs / Math.Max(seconds, 1e-9):F1}  (target >= 500)"));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  ranged reads       {counting.Reads:N0} ({counting.Reads / (double)Math.Max(blobs, 1):N1} per blob, "
                + $"{counting.Reads / (double)Math.Max(report.RecordsIndexed, 1):F2} per record)"));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  prefix listings    {counting.Listings.Count} [{string.Join(", ", counting.Listings)}]"));
            Console.WriteLine();
            Console.WriteLine(
                "The blob is the wrong unit, and this run shows why: a blob holds up to 65 536 records, so");
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"blobs/s varies with how full the blobs are — {report.RecordsIndexed / (double)Math.Max(blobs, 1):N0} records per blob here."));
            Console.WriteLine(
                "The work a rebuild does is per record; records/s is the figure that carries across scales.");
            Console.WriteLine();
            Console.WriteLine(
                "scale caveat: the target is stated for local NVMe on the reference machine and a scale-M");
            Console.WriteLine(
                "repository; this is a reduced-scale pass on whatever disk it was run on.");

            return report.TargetSatisfied ? 0 : 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static CapturePolicy CapturePolicyFor() =>
        CapturePolicy.Default with
        {
            DedupTrustDomain = DedupTrustDomain.Device,
        };
}

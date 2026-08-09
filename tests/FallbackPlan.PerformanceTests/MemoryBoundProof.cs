using System.Diagnostics;
using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;

using FallbackPlan.TestSupport;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// The NFR-PERF-001 shape at phase-0 scale: a multi-GiB synthetic stream —
/// never materialised — through the full <see cref="FileArchiver"/> pipeline
/// to a discarding store, with managed-heap and working-set samples taken
/// throughout. Memory must be a function of segment size and blob buffers,
/// not of input length; the proof is that the peak does not grow with the
/// gigabytes. Run with <c>dotnet run -c Release -- membound [gibibytes]</c>.
/// </summary>
public static class MemoryBoundProof
{
    private const int SampleEveryMs = 250;

    public static async Task<int> RunAsync(long gibibytes)
    {
        var totalBytes = gibibytes * 1024L * 1024 * 1024;
        var policy = CapturePolicy.Default with
        {
            SegmentationProfile = Domain.Profiles.SegmentationProfile.CdcV1,
            CdcParameters = CdcParameters.Default,
        };

        var spool = Path.Combine(Path.GetTempPath(), "fbp-membound-spool", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(spool);

        using var keys = RepositoryKeySet.FromMasterKey([.. Enumerable.Range(0, 32).Select(value => (byte)value)]);
        var store = new DiscardingObjectStore();
        var archiver = new FileArchiver(
            policy,
            RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10")),
            WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf")),
            KeyGeneration.Zero,
            keys,
            store,
            new MonotonicBlobCounterAllocator(1),
            spool);

        // Two cadences: the fast sampler watches the raw heap (which includes
        // garbage not yet collected — an allocation-rate number, not a
        // retention number) and the working set; the slow sampler forces a
        // collection and records the LIVE set, which is what NFR-PERF-001
        // actually bounds.
        var peakHeap = 0L;
        var peakWorkingSet = 0L;
        var peakLive = 0L;
        using var sampler = new Timer(_ =>
        {
            peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(forceFullCollection: false));
            peakWorkingSet = Math.Max(peakWorkingSet, Process.GetCurrentProcess().WorkingSet64);
        }, null, 0, SampleEveryMs);
        using var liveSampler = new Timer(_ =>
            peakLive = Math.Max(peakLive, GC.GetTotalMemory(forceFullCollection: true)),
            null, 5000, 5000);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var source = new SyntheticStream(totalBytes, seed: 42);
            var result = await archiver.ArchiveAsync(source, CancellationToken.None);
            stopwatch.Stop();

            var throughput = totalBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001) / (1024 * 1024);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"input:            {gibibytes} GiB ({totalBytes} bytes, synthetic, never materialised)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"elapsed:          {stopwatch.Elapsed.TotalSeconds:f1} s ({throughput:f0} MiB/s)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"segments:         {result.SegmentReferences.Count}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"blobs sealed:     {result.Blobs.Count} ({store.BytesConsumed} stored bytes discarded)"));
            peakLive = Math.Max(peakLive, GC.GetTotalMemory(forceFullCollection: true));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"peak live set:    {peakLive / (1024.0 * 1024):f1} MiB (forced-collection samples)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"peak raw heap:    {peakHeap / (1024.0 * 1024):f1} MiB (includes uncollected garbage)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"peak working set: {peakWorkingSet / (1024.0 * 1024):f1} MiB"));

            // The bound NFR-PERF-001 demands at scale: the live set stays
            // under 256 MiB regardless of how many GiB streamed by.
            var bound = 256L * 1024 * 1024;
            if (peakLive > bound)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"FAIL: peak live set {peakLive} exceeds the {bound} bound"));
                return 1;
            }

            Console.WriteLine("PASS: memory bounded by pipeline buffers, not input size");
            return 0;
        }
        finally
        {
            await sampler.DisposeAsync();
            if (Directory.Exists(spool))
            {
                Directory.Delete(spool, recursive: true);
            }
        }
    }
}

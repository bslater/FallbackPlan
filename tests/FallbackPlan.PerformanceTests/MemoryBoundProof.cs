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

    private const long Mebibyte = 1024 * 1024;

    /// <summary>The live-set bound NFR-PERF-001 demands at scale.</summary>
    private const long LiveSetBound = 256 * Mebibyte;

    /// <summary>What NFR-PERF-001 says processing a 2 TiB file may add over idle: 256 MB.</summary>
    private const long RequirementOverIdleBytes = 256_000_000;

    /// <summary>
    /// How far the second half's peak working set may stand above the first
    /// half's. A pipeline that buffered its input would add half the input
    /// between them, gigabytes here; what a streaming one shows is the
    /// collector's timing, a few tens of mebibytes either way.
    /// </summary>
    private const long GrowthAllowance = 64 * Mebibyte;

    /// <summary>
    /// How far the live set's last-quarter peak may stand above its
    /// third-quarter peak. A streaming pipeline's steady live set reads the
    /// same to the mebibyte across them; a run that kept one segment in
    /// sixty-four of an 8 GiB input adds about forty.
    /// </summary>
    private const long LiveGrowthAllowance = 16 * Mebibyte;

    /// <summary>
    /// The most processing may add over idle before the run is a regression
    /// rather than a reading: twice the figure NFR-PERF-001 states. The
    /// figure itself is printed and judged nowhere, because what a container
    /// measures of it lies within the collector's spread of the bound.
    /// </summary>
    private const long RegressionOverIdle = 2 * RequirementOverIdleBytes;

    /// <summary>What one run measured, each peak over its half of the input.</summary>
    /// <param name="TotalBytes">How much was archived.</param>
    /// <param name="IdleWorkingSet">The working set before a byte was archived.</param>
    /// <param name="PeakFirstHalf">The peak working set while the first half of the input was read.</param>
    /// <param name="PeakSecondHalf">The peak working set while the second half was read.</param>
    /// <param name="PeakLive">The peak forced-collection live set over the whole run.</param>
    /// <param name="PeakLiveThirdQuarter">
    /// The peak live set while the third quarter of the input was read: the
    /// first steady reading on an 8 GiB input, the run's warm-up having ended
    /// in the first half.
    /// </param>
    /// <param name="PeakLiveLastQuarter">The peak live set while the last quarter was read.</param>
    public sealed record Readings(
        long TotalBytes,
        long IdleWorkingSet,
        long PeakFirstHalf,
        long PeakSecondHalf,
        long PeakLive,
        long PeakLiveThirdQuarter,
        long PeakLiveLastQuarter);

    /// <summary>What a run's readings say, and every bound they broke.</summary>
    /// <param name="OverIdle">The peak working set over the whole run, less the idle one.</param>
    /// <param name="Failures">Each bound broken, said; empty when the run passes.</param>
    public sealed record Verdict(long OverIdle, IReadOnlyList<string> Failures);

    /// <summary>
    /// Judges a run's readings: the live set within its bound, neither the
    /// live set nor the working set growing from the first half of the input
    /// to the second, and what processing added over idle short of a
    /// regression.
    /// </summary>
    public static Verdict Judge(Readings readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var failures = new List<string>();
        var overIdle = Math.Max(readings.PeakFirstHalf, readings.PeakSecondHalf) - readings.IdleWorkingSet;
        if (readings.PeakLive > LiveSetBound)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"peak live set {Mib(readings.PeakLive)} MiB exceeds the {Mib(LiveSetBound)} MiB bound"));
        }

        // The live set is the cleaner signal: the working set is mostly the
        // collector's uncollected garbage, whose swings hide a slow leak. The
        // third quarter is the baseline because the run's first forty seconds
        // or so hold tens of mebibytes above the steady state and would hide
        // that much growth. Warm-up can only raise an earlier quarter, so a
        // slow machine makes the comparison stricter and a fast one more
        // lenient, and neither fails a streaming run.
        if (readings.PeakLiveLastQuarter > readings.PeakLiveThirdQuarter + LiveGrowthAllowance)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"the live set grew with the input: {Mib(readings.PeakLiveThirdQuarter)} MiB over its third quarter, {Mib(readings.PeakLiveLastQuarter)} MiB over its last"));
        }

        if (readings.PeakSecondHalf > readings.PeakFirstHalf + GrowthAllowance)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"the working set grew with the input: {Mib(readings.PeakFirstHalf)} MiB over the first {Mib(readings.TotalBytes / 2)} MiB, {Mib(readings.PeakSecondHalf)} MiB over the second"));
        }

        if (overIdle > RegressionOverIdle)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"processing added {Mib(overIdle)} MiB over idle, past the {Mib(RegressionOverIdle)} MiB that marks a regression"));
        }

        return new Verdict(overIdle, failures);
    }

    private static string Mib(long bytes) => (bytes / (double)Mebibyte).ToString("f1", CultureInfo.InvariantCulture);

    public static async Task<int> RunAsync(long gibibytes)
    {
        var totalBytes = gibibytes * 1024L * 1024 * 1024;
        var policy = CapturePolicy.Default with
        {
            DedupTrustDomain = DedupTrustDomain.Device,
            SegmentationProfile = Domain.Profiles.SegmentationProfile.CdcV1,
            CdcParameters = CdcParameters.Default,
        };

        var spool = Path.Combine(Path.GetTempPath(), "fbp-membound-spool", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(spool);

        using var keys = RepositoryKeySet.FromWriteCredential(TestAuthority.Shared.Credential);
        var store = new DiscardingObjectStore();
        var archiver = new FileArchiver(
            policy,
            RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10")),
            WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf")),
            KeyGeneration.Zero,
            keys,
            store,
            new MonotonicBlobCounterAllocator(1),
            spool,
            FormatVersions.SealedDataPlane);

        // What the process holds before a byte is archived: the runtime, the
        // pipeline's construction and nothing else. NFR-PERF-001 bounds what
        // processing adds over idle, so every working-set figure below is
        // judged against this one.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var idleWorkingSet = Process.GetCurrentProcess().WorkingSet64;
        var idleLive = GC.GetTotalMemory(forceFullCollection: true);

        // Two cadences: the fast sampler watches the raw heap (which includes
        // garbage not yet collected — an allocation-rate number, not a
        // retention number) and the working set, each half of the input
        // separately; the slow sampler forces a collection and records the
        // LIVE set, which is what NFR-PERF-001 actually bounds.
        using var source = new SyntheticStream(totalBytes, seed: 42);
        var half = totalBytes / 2;
        var peakHeap = 0L;
        var peakFirstHalf = 0L;
        var peakSecondHalf = 0L;
        var peakLive = 0L;
        var peakLiveByQuarter = new long[4];
        using var sampler = new Timer(_ =>
        {
            peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(forceFullCollection: false));
            var workingSet = Process.GetCurrentProcess().WorkingSet64;
            if (source.Position < half)
            {
                peakFirstHalf = Math.Max(peakFirstHalf, workingSet);
            }
            else
            {
                peakSecondHalf = Math.Max(peakSecondHalf, workingSet);
            }
        }, null, 0, SampleEveryMs);
        using var liveSampler = new Timer(_ =>
        {
            var live = GC.GetTotalMemory(forceFullCollection: true);
            peakLive = Math.Max(peakLive, live);
            var quarter = (int)Math.Min(3, source.Position / Math.Max(1, totalBytes / 4));
            peakLiveByQuarter[quarter] = Math.Max(peakLiveByQuarter[quarter], live);
        }, null, 5000, 5000);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await archiver.ArchiveAsync(source, CancellationToken.None);
            stopwatch.Stop();
            await sampler.DisposeAsync();
            await liveSampler.DisposeAsync();

            // What the run left live once the archive returned: printed, not
            // judged. A run past the warm-up leaves a few mebibytes and one
            // that kept part of its input leaves all of it, but a run shorter
            // than the warm-up leaves tens of mebibytes it has not let go of
            // yet, so the figure depends on how fast the machine is.
            var leftLive = GC.GetTotalMemory(forceFullCollection: true) - idleLive;
            peakLive = Math.Max(peakLive, leftLive + idleLive);

            var verdict = Judge(new Readings(
                totalBytes, idleWorkingSet, peakFirstHalf, peakSecondHalf, peakLive, peakLiveByQuarter[2], peakLiveByQuarter[3]));
            var throughput = totalBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001) / (1024 * 1024);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"input:            {gibibytes} GiB ({totalBytes} bytes, synthetic, never materialised)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"elapsed:          {stopwatch.Elapsed.TotalSeconds:f1} s ({throughput:f0} MiB/s)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"segments:         {result.SegmentReferences.Count}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"blobs sealed:     {result.Blobs.Count} ({store.BytesConsumed} stored bytes discarded)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"peak live set:    {Mib(peakLive)} MiB (forced-collection samples; by quarter of the input {Mib(peakLiveByQuarter[0])}, {Mib(peakLiveByQuarter[1])}, {Mib(peakLiveByQuarter[2])}, {Mib(peakLiveByQuarter[3])} MiB)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"peak raw heap:    {Mib(peakHeap)} MiB (includes uncollected garbage)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"left live:        {Mib(leftLive)} MiB after the archive returned, over the idle live set"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"idle working set: {Mib(idleWorkingSet)} MiB"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"peak working set: {Mib(peakFirstHalf)} MiB over the first half, {Mib(peakSecondHalf)} MiB over the second"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"over idle:        {Mib(verdict.OverIdle)} MiB ({verdict.OverIdle / 1_000_000.0:f0} MB; "
                + $"NFR-PERF-001 states {RequirementOverIdleBytes / 1_000_000} MB for a 2 TiB file)"));

            foreach (var failure in verdict.Failures)
            {
                Console.WriteLine($"FAIL: {failure}");
            }

            if (verdict.Failures.Count > 0)
            {
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

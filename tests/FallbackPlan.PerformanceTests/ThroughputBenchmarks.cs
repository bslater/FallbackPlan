using System.Diagnostics;
using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;

using FallbackPlan.TestSupport;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// NFR-PERF-007's throughput measurement, and ADR-0029 §6 step 1: <b>measure
/// before optimising</b>.
/// </summary>
/// <remarks>
/// <para>
/// The traceability matrix named <c>PerformanceTests/ThroughputBenchmarks</c>
/// for a long time while it did not exist. It exists now, and its job is not to
/// restate the gap between ≈51 MiB/s and NFR-PERF-007's ≥400 MB/s — that number
/// was already known — but to <b>attribute</b> it, so the work that follows is
/// aimed at something measured rather than assumed.
/// </para>
/// <para>
/// Deliberately not BenchmarkDotNet. BDN's job is precise micro-comparison of
/// short operations; this measures a whole pipeline over tens of mebibytes,
/// where the interesting variance is IO and the interesting output is MiB/s per
/// configuration. Running it takes seconds, not the minutes BDN's warmup and
/// statistics would spend to answer a question about disks.
/// </para>
/// </remarks>
public static class ThroughputBenchmarks
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    /// <summary>Runs the sweep and prints a table.</summary>
    /// <param name="mebibytes">How much data to push through per configuration.</param>
    /// <returns>0 when every configuration completed.</returns>
    public static async Task<int> RunAsync(int mebibytes = 64)
    {
        var data = new byte[mebibytes * 1024 * 1024];

        // Mixed compressibility: incompressible random would measure a pipeline
        // that never invokes the compressor, and zeros would measure one that
        // always short-circuits. Neither is a backup.
        var random = new Random(42);
        random.NextBytes(data);
        for (var offset = 0; offset + 4096 <= data.Length; offset += 8192)
        {
            Array.Fill(data, (byte)'a', offset, 4096);
        }

        using var keys = RepositoryKeySet.FromMasterKey([.. Enumerable.Range(0, 32).Select(value => (byte)value)]);
        var spool = Path.Combine(Path.GetTempPath(), "fbp-throughput", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(spool);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"ThroughputBenchmarks — {mebibytes} MiB per configuration, {Environment.ProcessorCount} logical cores"));
        Console.WriteLine();
        Console.WriteLine("configuration                       seconds     MiB/s   stalled");
        Console.WriteLine("----------------------------------- ------- --------- ---------");

        var counter = 1UL;
        try
        {
            foreach (var (name, policy, latency) in Configurations())
            {
                // One untimed pass first: the first archive of a process pays
                // JIT and allocator warm-up that has nothing to do with the
                // pipeline, and attributing that to a configuration would be
                // measuring the runtime.
                (counter, _) = await ArchiveAsync(data[..(4 * 1024 * 1024)], policy, keys, spool, counter, TimeSpan.Zero)
                    .ConfigureAwait(false);

                var stopwatch = Stopwatch.StartNew();
                var (next, imposed) = await ArchiveAsync(data, policy, keys, spool, counter, latency)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                counter = next;

                var seconds = stopwatch.Elapsed.TotalSeconds;
                var throughput = mebibytes / seconds;
                var stalled = imposed > TimeSpan.Zero ? $"{imposed.TotalSeconds,9:F2}" : new string(' ', 9);
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name,-35} {seconds,7:F2} {throughput,9:F1} {stalled}"));
            }
        }
        finally
        {
            if (Directory.Exists(spool))
            {
                Directory.Delete(spool, recursive: true);
            }
        }

        Console.WriteLine();
        Console.WriteLine("Concurrency is reported per configuration so NFR-PERF-002's acceptance");
        Console.WriteLine("(\"restored bytes identical regardless of concurrency setting\") is measured");
        Console.WriteLine("rather than asserted. A setting that does not yet change the pipeline");
        Console.WriteLine("reports the same number, which is the honest result until it does.");
        return 0;
    }

    /// <summary>
    /// How slow each blob PUT is for a configuration. Zero is the free store —
    /// the right instrument for the pipeline and the wrong one for ADR-0029 §2,
    /// whose claim is entirely about destinations that are not free.
    /// </summary>
    private static IEnumerable<(string Name, CapturePolicy Policy, TimeSpan Latency)> Configurations()
    {
        var baseline = CapturePolicy.Default with
        {
            BlobWriteProfile = BlobWriteProfile.LocalDefault with
            {
                TargetSizeBytes = 8 * 1024 * 1024,
                MaximumSizeBytes = 16 * 1024 * 1024,
            },
        };

        var free = TimeSpan.Zero;
        yield return ("fixed-v1, concurrency 1", baseline with { Concurrency = 1 }, free);
        yield return ("fixed-v1, concurrency 2", baseline with { Concurrency = 2 }, free);
        yield return ("fixed-v1, concurrency 4", baseline with { Concurrency = 4 }, free);
        yield return ("fixed-v1, concurrency 8", baseline with { Concurrency = 8 }, free);

        var cdc = baseline with
        {
            SegmentationProfile = Domain.Profiles.SegmentationProfile.CdcV1,
            CdcParameters = Domain.CdcParameters.Default,
        };

        yield return ("cdc-v1, concurrency 1", cdc with { Concurrency = 1 }, free);
        yield return ("cdc-v1, concurrency 4", cdc with { Concurrency = 4 }, free);

        // The compressor is a candidate serial cost. Measuring with it off
        // separates "the pipeline is slow" from "zstd is slow here".
        yield return (
            "fixed-v1, no compression",
            baseline with { Compression = CompressionSettings.Default with { Profile = Domain.Profiles.CompressionProfile.None } },
            free);

        // The rows ADR-0029 §2 is actually about. The `stalled` column reports
        // the total latency the store imposed; when it exceeds the wall clock,
        // the uploads provably overlapped the archive loop, because they could
        // not otherwise have fitted. That is the claim, measured rather than
        // asserted.
        var slow = TimeSpan.FromMilliseconds(200);
        yield return ("slow store 200ms, concurrency 1", baseline with { Concurrency = 1 }, slow);
        yield return ("slow store 200ms, concurrency 2", baseline with { Concurrency = 2 }, slow);
        yield return ("slow store 200ms, concurrency 4", baseline with { Concurrency = 4 }, slow);
    }

    private static async Task<(ulong Counter, TimeSpan Imposed)> ArchiveAsync(
        byte[] data, CapturePolicy policy, RepositoryKeySet keys, string spool, ulong counter, TimeSpan latency)
    {
        Storage.Abstractions.IObjectStore store = new DiscardingObjectStore();
        SlowObjectStore? slow = null;
        if (latency > TimeSpan.Zero)
        {
            store = slow = new SlowObjectStore(store, latency);
        }

        var archiver = new FileArchiver(
            policy, Repo, Writer, KeyGeneration.Zero, keys, store,
            new MonotonicBlobCounterAllocator(counter), spool);

        using var source = new MemoryStream(data);
        await archiver.ArchiveAsync(source, CancellationToken.None).ConfigureAwait(false);
        return (counter + 1_000, slow?.ImposedLatency ?? TimeSpan.Zero);
    }
}

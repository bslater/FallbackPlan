using System.Diagnostics;
using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Segmentation;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// The format-v1 freeze-gate item 1 evidence run (roadmap; architecture
/// 02 §3.3): <c>fixed-v1</c> versus <c>cdc-v1</c> on a synthetic corpus of
/// representative edit patterns, reporting deduplication ratio, per-backup
/// storage growth, metadata overhead, and CPU cost. Deterministic seeds, so
/// the published numbers reproduce exactly. Run with
/// <c>dotnet run -c Release -- dedup</c>.
/// </summary>
/// <remarks>
/// Dedup identity is the segment content id (SHA-256 of plaintext), the same
/// identity the engine's <c>segment_dedup</c> catalogue table uses — so a
/// "stored byte" here is a byte no prior segment of the series already
/// covers. Encryption and compression sit downstream of that identity and do
/// not change the ratio; they are excluded so the run isolates segmentation.
/// </remarks>
public static class DedupCorpusBenchmark
{
    private sealed record Profile(string Name, Func<Stream, ISegmentReader> Open, int MaxSegment);

    private sealed record VersionStats(long LogicalBytes, long NewBytes, int Segments, int NewSegments);

    private sealed record ScenarioResult(
        string Scenario,
        string Profile,
        long LogicalTotal,
        long StoredTotal,
        long StoredAfterFirst,
        long LogicalAfterFirst,
        int SegmentsTotal,
        double Seconds);

    /// <summary>Per-segment format overhead: record header (54) + tag (16) + record-table entry (~60) + index entry (~70) + manifest reference (~40).</summary>
    private const int PerSegmentOverheadBytes = 240;

    public static async Task<int> RunAsync()
    {
        var profiles = new Profile[]
        {
            new("fixed-v1 1MiB", stream => new FixedSegmentReader(stream, SegmentSize.Create(1024 * 1024)), 1024 * 1024),
            new("cdc-v1 1MiB", stream => new CdcSegmentReader(stream, CdcParameters.Default), CdcParameters.Default.MaxSize),
            new("cdc-v1 256KiB", stream => new CdcSegmentReader(
                stream, CdcParameters.Create(256 * 1024, 64 * 1024, 2 * 1024 * 1024)), 2 * 1024 * 1024),
        };

        var scenarios = new (string Name, Func<int, byte[][]> Build)[]
        {
            ("log-append", BuildLogAppend),
            ("doc-insert", BuildDocInsert),
            ("db-inplace", BuildDbInPlace),
            ("rewrite-shift", BuildRewriteShift),
            ("media-unique", BuildMediaUnique),
        };

        var results = new List<ScenarioResult>();

        foreach (var (name, build) in scenarios)
        {
            var versions = build(11);
            foreach (var profile in profiles)
            {
                results.Add(await MeasureAsync(name, profile, versions).ConfigureAwait(false));
            }

            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("| Scenario | Profile | Logical (all versions) | Stored | Dedup ratio | Incremental ratio (v1..vN) | Segments | Overhead est. | MiB/s |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var result in results)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {result.Scenario} | {result.Profile} | {Mib(result.LogicalTotal)} | {Mib(result.StoredTotal)} | " +
                $"{(double)result.StoredTotal / result.LogicalTotal:P1} | " +
                $"{(double)result.StoredAfterFirst / Math.Max(result.LogicalAfterFirst, 1):P1} | " +
                $"{result.SegmentsTotal} | {Mib((long)result.SegmentsTotal * PerSegmentOverheadBytes)} | " +
                $"{result.LogicalTotal / Math.Max(result.Seconds, 0.001) / (1024 * 1024):F0} |"));
        }

        return 0;
    }

    private static string Mib(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):F1} MiB");

    private static async Task<ScenarioResult> MeasureAsync(string scenario, Profile profile, byte[][] versions)
    {
        var seen = new HashSet<ContentId>();
        var perVersion = new List<VersionStats>();
        var buffer = new byte[profile.MaxSegment];
        var stopwatch = Stopwatch.StartNew();

        foreach (var version in versions)
        {
            long newBytes = 0;
            var segments = 0;
            var newSegments = 0;

            using var source = new MemoryStream(version);
            var reader = profile.Open(source);

            while (await reader.ReadNextAsync(buffer, CancellationToken.None).ConfigureAwait(false) is { } segment)
            {
                segments++;
                if (seen.Add(ContentHasher.Hash(buffer.AsSpan(0, segment.Length))))
                {
                    newSegments++;
                    newBytes += segment.Length;
                }
            }

            perVersion.Add(new VersionStats(version.Length, newBytes, segments, newSegments));
        }

        stopwatch.Stop();

        var logicalTotal = perVersion.Sum(version => version.LogicalBytes);
        var storedTotal = perVersion.Sum(version => version.NewBytes);
        var storedAfterFirst = perVersion.Skip(1).Sum(version => version.NewBytes);
        var logicalAfterFirst = perVersion.Skip(1).Sum(version => version.LogicalBytes);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{scenario,-14} {profile.Name,-14} stored {Mib(storedTotal),12} of {Mib(logicalTotal),12} " +
            $"({(double)storedTotal / logicalTotal:P1}); incremental {(double)storedAfterFirst / Math.Max(logicalAfterFirst, 1):P1}"));

        return new ScenarioResult(
            scenario, profile.Name, logicalTotal, storedTotal, storedAfterFirst, logicalAfterFirst,
            perVersion.Sum(version => version.Segments), stopwatch.Elapsed.TotalSeconds);
    }

    // ---------------------------------------------------------------- corpus

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>An append-only file (logs, mail spools): 32 MiB + 2 MiB per version.</summary>
    private static byte[][] BuildLogAppend(int count)
    {
        var random = new Random(101);
        var versions = new byte[count][];
        var current = RandomBytes(random, 32 * 1024 * 1024);
        versions[0] = current;

        for (var i = 1; i < count; i++)
        {
            var appended = new byte[current.Length + 2 * 1024 * 1024];
            current.CopyTo(appended, 0);
            random.NextBytes(appended.AsSpan(current.Length));
            versions[i] = current = appended;
        }

        return versions;
    }

    /// <summary>
    /// A document with mid-file edits (office files, mail folders): per
    /// version, three 64 KiB insertions, one 16 KiB deletion, three 4 KiB
    /// in-place modifications — everything after each insertion point shifts.
    /// </summary>
    private static byte[][] BuildDocInsert(int count)
    {
        var random = new Random(202);
        var versions = new byte[count][];
        var current = RandomBytes(random, 64 * 1024 * 1024);
        versions[0] = current;

        for (var i = 1; i < count; i++)
        {
            var working = new List<byte>(current);
            for (var insertion = 0; insertion < 3; insertion++)
            {
                working.InsertRange(random.Next(working.Count), RandomBytes(random, 64 * 1024));
            }

            working.RemoveRange(random.Next(working.Count - 16 * 1024), 16 * 1024);
            for (var edit = 0; edit < 3; edit++)
            {
                var at = random.Next(working.Count - 4096);
                var patch = RandomBytes(random, 4096);
                for (var j = 0; j < patch.Length; j++)
                {
                    working[at + j] = patch[j];
                }
            }

            versions[i] = current = [.. working];
        }

        return versions;
    }

    /// <summary>A database-style file: 128 MiB, 200 random 4 KiB pages overwritten in place per version — no shift.</summary>
    private static byte[][] BuildDbInPlace(int count)
    {
        var random = new Random(303);
        var versions = new byte[count][];
        var current = RandomBytes(random, 128 * 1024 * 1024);
        versions[0] = current;

        for (var i = 1; i < count; i++)
        {
            var next = (byte[])current.Clone();
            for (var page = 0; page < 200; page++)
            {
                var at = random.Next(next.Length / 4096) * 4096;
                random.NextBytes(next.AsSpan(at, 4096));
            }

            versions[i] = current = next;
        }

        return versions;
    }

    /// <summary>Pure-shift saves: 64 MiB, five 4 KiB insertions per version, nothing else changes.</summary>
    private static byte[][] BuildRewriteShift(int count)
    {
        var random = new Random(404);
        var versions = new byte[count][];
        var current = RandomBytes(random, 64 * 1024 * 1024);
        versions[0] = current;

        for (var i = 1; i < count; i++)
        {
            var working = new List<byte>(current);
            for (var insertion = 0; insertion < 5; insertion++)
            {
                working.InsertRange(random.Next(working.Count), RandomBytes(random, 4096));
            }

            versions[i] = current = [.. working];
        }

        return versions;
    }

    /// <summary>The control: 32 MiB of fresh content every version — no dedup exists to find.</summary>
    private static byte[][] BuildMediaUnique(int count)
    {
        var random = new Random(505);
        var versions = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            versions[i] = RandomBytes(random, 32 * 1024 * 1024);
        }

        return versions;
    }
}

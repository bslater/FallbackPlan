using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// NFR-PERF-004 path-and-version lookup at reduced scale (wave V2): a
/// catalogue v2 seeded with 100 000 tree entries across a realistic
/// directory shape — 1/10 of the requirement's 1M-file scale, honestly
/// labelled — measuring the exact SQL <c>ls</c>, the incremental
/// short-circuit run, and the walk of a snapshot's leaves in path order a
/// source comparison and a snapshot diff make. Numbers land in
/// docs/phase-1-benchmarks.md with the scale caveat spelled out.
/// </summary>
public static class PathLookupBenchmark
{
    private const int Files = 100_000;
    private const int FilesPerDirectory = 50;
    private const int Probes = 20_000;
    private const int Passes = 10;

    public static int Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "fbp-bench-paths", Guid.NewGuid().ToString("n") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var repo = RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));
        var snapshotId = Enumerable.Repeat((byte)0x11, 16).ToArray();

        using var catalogue = Catalogue.Open(path, repo);

        static ObjectId ObjectFrom(int seed) => ObjectId.FromBytes(SHA256.HashData(BitConverter.GetBytes(seed)));

        // Seed: dirs of 50 files, 40 dirs per parent, through the same
        // RecordTreeEntry/RecordFileVersion path live publication uses.
        var paths = new string[Files];
        var seed = new Stopwatch();
        seed.Start();
        for (var index = 0; index < Files; index++)
        {
            var directory = index / FilesPerDirectory;
            var parent = $"data/dir-{directory / 40:d4}/sub-{directory % 40:d2}";
            var filePath = $"{parent}/file-{index % FilesPerDirectory:d3}.bin";
            paths[index] = filePath;

            catalogue.RecordTreeEntry(snapshotId, filePath, EntryKind.File, ObjectFrom(index));
            catalogue.RecordFileVersion(
                ObjectFrom(index), 4096,
                modifiedAt: 1_722_600_000_000, identityDevice: 7, identityFileId: (ulong)index);
        }

        seed.Stop();

        var stopwatch = new Stopwatch();

        // The NFR-PERF-004 lookup: one path resolved with its file-version
        // columns — the query the incremental short-circuit issues per file.
        stopwatch.Start();
        var found = 0;
        for (var probe = 0; probe < Probes; probe++)
        {
            if (catalogue.LookupPath(snapshotId, paths[probe * (Files / Probes)]) is not null)
            {
                found++;
            }
        }

        stopwatch.Stop();
        var lookupMicros = stopwatch.Elapsed.TotalMicroseconds / Probes;

        // The `ls` query: one directory's immediate children.
        stopwatch.Restart();
        var listed = 0;
        for (var probe = 0; probe < Probes / 10; probe++)
        {
            var directory = probe * 7 % (Files / FilesPerDirectory);
            listed += catalogue.ListDirectory(
                snapshotId, $"data/dir-{directory / 40:d4}/sub-{directory % 40:d2}").Count;
        }

        stopwatch.Stop();
        var listMicros = stopwatch.Elapsed.TotalMicroseconds / (Probes / 10);

        // The baseline a source comparison and a snapshot diff walk: every
        // file of the snapshot, in path order.
        stopwatch.Restart();
        var enumerated = 0;
        for (var pass = 0; pass < Passes; pass++)
        {
            enumerated += catalogue.EnumerateLeaves(snapshotId).Count;
        }

        stopwatch.Stop();
        var leavesMillis = stopwatch.Elapsed.TotalMilliseconds / Passes;

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"seeded {Files} tree entries + file versions in {seed.Elapsed.TotalSeconds:f1} s"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"LookupPath   {lookupMicros,8:f1} us/op over {Probes} probes ({found} found)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"ListDirectory{listMicros,8:f1} us/op over {Probes / 10} probes ({listed} entries listed)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"EnumerateLeaves{leavesMillis,6:f1} ms/op over {Passes} passes ({enumerated / Passes} files each)"));
        Console.WriteLine("scale caveat: 100k files is 1/10 of NFR-PERF-004's stated scale; single-machine, tmpfs-adjacent.");

        File.Delete(path);
        return found == Probes && enumerated == Files * Passes ? 0 : 1;
    }
}

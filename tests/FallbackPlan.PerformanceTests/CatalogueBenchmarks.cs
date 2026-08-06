using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Index;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// Catalogue lookup latency at reduced scale (NFR-PERF-004 path resolution,
/// NFR-PERF-010 dedup lookup): a catalogue seeded with 100 000 object
/// locations and 100 000 dedup rows — 1/500 of scale M — measured on the
/// exact SQL the engine runs. The per-operation numbers land in
/// docs/phase-0-benchmarks.md with the scale caveat spelled out.
/// </summary>
[MemoryDiagnoser]
public class CatalogueBenchmarks
{
    /// <summary>Rows seeded into object_locations and segment_dedup.</summary>
    [Params(100_000)]
    public int Rows { get; set; }

    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private Catalogue _catalogue = null!;
    private string _path = string.Empty;
    private ObjectId[] _probeObjects = [];
    private ContentId[] _probeContents = [];
    private int _next;

    private static ObjectId ObjectFrom(int seed) =>
        ObjectId.FromBytes(SHA256.HashData(BitConverter.GetBytes(seed)));

    private static ContentId ContentFrom(int seed) =>
        ContentId.FromBytes(SHA256.HashData(BitConverter.GetBytes(~seed)));

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), "fbp-bench-catalogue", Guid.NewGuid().ToString("n") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _catalogue = Catalogue.Open(_path, Repo);

        // Seed through the same ApplyDelta path the engine uses, in delta-sized
        // batches so the applied-delta ledger stays realistic.
        const int batch = 1000;
        for (var start = 0; start < Rows; start += batch)
        {
            var entries = Enumerable.Range(start, Math.Min(batch, Rows - start))
                .Select(index => new IndexEntry(
                    ObjectFrom(index),
                    BlobId.FromBytes(SHA256.HashData(BitConverter.GetBytes(index / 64)).AsSpan(0, 16)),
                    PhysicalOffset: 88 + (ulong)(index % 64) * 4096,
                    StoredLength: 4096,
                    CompressionProfileValue: 0x0001,
                    EncryptionProfileValue: 0x0001,
                    IndexEntryType.Insertion))
                .ToList();

            _catalogue.ApplyDelta(
                DeltaId.FromBytes(SHA256.HashData(BitConverter.GetBytes(start)).AsSpan(0, 16).ToArray()),
                new IndexDelta
                {
                    WriterId = Writer,
                    Sequence = (ulong)(start / batch) + 1,
                    Generation = 0,
                    Entries = entries,
                });
        }

        for (var index = 0; index < Rows; index++)
        {
            _catalogue.RecordSegmentDedup(ContentFrom(index), ObjectFrom(index));
        }

        _probeObjects = [.. Enumerable.Range(0, 4096).Select(index => ObjectFrom(index * (Rows / 4096)))];
        _probeContents = [.. Enumerable.Range(0, 4096).Select(index => ContentFrom(index * (Rows / 4096)))];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _catalogue.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Benchmark]
    public ResolvedLocation? ResolveLocation()
    {
        var probe = _probeObjects[_next++ & 4095];
        return _catalogue.ResolveLocation(probe);
    }

    [Benchmark]
    public ObjectId? DedupLookup()
    {
        var probe = _probeContents[_next++ & 4095];
        return _catalogue.LookupByContent(probe);
    }
}

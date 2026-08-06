using BenchmarkDotNet.Attributes;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// The full record path — segment, hash, compress, encrypt, pack, seal —
/// through <see cref="FileArchiver"/> to a discarding store
/// (NFR-PERF-001..003's pipeline; traceability's
/// <c>PerformanceTests/PipelineBenchmarks</c>). The store costs nothing, so
/// the number is the pipeline itself.
/// </summary>
[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private byte[] _data = [];
    private RepositoryKeySet _keys = null!;
    private string _spool = string.Empty;
    private ulong _counter = 1;

    /// <summary>Input size in bytes.</summary>
    [Params(32 * 1024 * 1024)]
    public int DataLength { get; set; }

    /// <summary>
    /// The concurrency setting (ADR-0029 §3), swept because it now reaches the
    /// record path rather than only the upload workers.
    /// </summary>
    /// <remarks>
    /// 1 is kept because it is the configuration in which the ordering barrier
    /// is trivially satisfied, and so the control against which any speedup at
    /// the other settings is read.
    /// </remarks>
    [Params(1, 2, 4)]
    public int Concurrency { get; set; }

    private static CapturePolicy BasePolicy { get; } = CapturePolicy.Default with
    {
        BlobWriteProfile = BlobWriteProfile.LocalDefault with
        {
            TargetSizeBytes = 8 * 1024 * 1024,
            MaximumSizeBytes = 16 * 1024 * 1024,
        },
    };

    private CapturePolicy FixedPolicy => BasePolicy with { Concurrency = Concurrency };

    private CapturePolicy CdcPolicy => FixedPolicy with
    {
        SegmentationProfile = Domain.Profiles.SegmentationProfile.CdcV1,
        CdcParameters = Domain.CdcParameters.Default,
    };

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[DataLength];
        new Random(42).NextBytes(_data);
        _keys = RepositoryKeySet.FromMasterKey([.. Enumerable.Range(0, 32).Select(value => (byte)value)]);
        _spool = Path.Combine(Path.GetTempPath(), "fbp-bench-spool", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_spool);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _keys.Dispose();
        if (Directory.Exists(_spool))
        {
            Directory.Delete(_spool, recursive: true);
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<int> ArchiveFixedV1() => await ArchiveAsync(FixedPolicy);

    [Benchmark]
    public async Task<int> ArchiveCdcV1() => await ArchiveAsync(CdcPolicy);

    private async Task<int> ArchiveAsync(CapturePolicy policy)
    {
        var store = new NullObjectStore();
        var archiver = new FileArchiver(
            policy, Repo, Writer, KeyGeneration.Zero, _keys, store,
            new MonotonicBlobCounterAllocator(_counter), _spool);
        _counter += 1_000;

        using var source = new MemoryStream(_data);
        var result = await archiver.ArchiveAsync(source, CancellationToken.None);
        return result.SegmentReferences.Count;
    }
}

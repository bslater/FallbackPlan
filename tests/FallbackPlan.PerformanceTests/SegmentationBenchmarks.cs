using BenchmarkDotNet.Attributes;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Segmentation;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// Raw segmenter throughput, fixed-v1 against cdc-v1 (specification 09;
/// ADR-0023): the price of content-defined boundaries is the rolling Rabin
/// window, and this measures exactly that delta over identical bytes.
/// </summary>
[MemoryDiagnoser]
public class SegmentationBenchmarks
{
    private byte[] _data = [];
    private byte[] _buffer = [];

    /// <summary>Input size in bytes.</summary>
    [Params(64 * 1024 * 1024)]
    public int DataLength { get; set; }

    private static readonly CdcParameters Cdc = CdcParameters.Default;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[DataLength];
        new Random(42).NextBytes(_data);
        _buffer = new byte[Math.Max(Cdc.MaxSize, 1024 * 1024)];
    }

    [Benchmark(Baseline = true)]
    public async Task<long> FixedV1()
    {
        using var source = new MemoryStream(_data);
        var reader = new FixedSegmentReader(source, SegmentSize.Create(1024 * 1024));
        return await DrainAsync(reader);
    }

    [Benchmark]
    public async Task<long> CdcV1()
    {
        using var source = new MemoryStream(_data);
        var reader = new CdcSegmentReader(source, Cdc);
        return await DrainAsync(reader);
    }

    private async Task<long> DrainAsync(ISegmentReader reader)
    {
        var count = 0L;
        while (await reader.ReadNextAsync(_buffer, CancellationToken.None) is not null)
        {
            count++;
        }

        return count;
    }
}

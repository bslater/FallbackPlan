using System.Globalization;
using BenchmarkDotNet.Running;
using FallbackPlan.PerformanceTests;

// Two entry points (F4): `-- membound [gibibytes]` runs the non-BenchmarkDotNet
// NFR-PERF-001 proof; anything else goes to the BenchmarkDotNet switcher
// (e.g. `--filter '*' --job short`).
if (args.Length > 0 && string.Equals(args[0], "membound", StringComparison.OrdinalIgnoreCase))
{
    var gibibytes = args.Length > 1 ? long.Parse(args[1], CultureInfo.InvariantCulture) : 3;
    return await MemoryBoundProof.RunAsync(gibibytes);
}

if (args.Length > 0 && string.Equals(args[0], "dedup", StringComparison.OrdinalIgnoreCase))
{
    return await DedupCorpusBenchmark.RunAsync();
}

if (args.Length > 0 && string.Equals(args[0], "throughput", StringComparison.OrdinalIgnoreCase))
{
    // ADR-0029 section 6 step 1: measure, and attribute, before optimising.
    var mebibytes = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 64;
    return await ThroughputBenchmarks.RunAsync(mebibytes);
}

if (args.Length > 0 && string.Equals(args[0], "pathlookup", StringComparison.OrdinalIgnoreCase))
{
    return PathLookupBenchmark.Run();
}

if (args.Length > 0 && string.Equals(args[0], "catalogue-size", StringComparison.OrdinalIgnoreCase))
{
    // NFR-PERF-011: what the schema costs per file version, by plane. Not a
    // BenchmarkDotNet case — it measures a file on disk, not a call.
    var versions = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 100_000;
    return CatalogueSizeBenchmark.Run(versions);
}

if (args.Length > 0 && string.Equals(args[0], "rebuild-rate", StringComparison.OrdinalIgnoreCase))
{
    // NFR-PERF-012: one end-to-end forensic rebuild, timed, with the reads it
    // cost beside the rate. Publishing and deleting an index plane is setup
    // far too heavy for a BenchmarkDotNet case.
    var files = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 2_000;
    return await RebuildRateBenchmark.RunAsync(files);
}

if (args.Length > 0 && string.Equals(args[0], "metadata-size", StringComparison.OrdinalIgnoreCase))
{
    // Q4's encoding-size half: what canonical CBOR costs against the two
    // alternatives ADR-0003 rejected partly on size.
    return MetadataEncodingBenchmark.Run();
}

BenchmarkSwitcher.FromAssembly(typeof(MemoryBoundProof).Assembly).Run(args);
return 0;

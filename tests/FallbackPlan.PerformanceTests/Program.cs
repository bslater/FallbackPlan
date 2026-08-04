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

BenchmarkSwitcher.FromAssembly(typeof(MemoryBoundProof).Assembly).Run(args);
return 0;

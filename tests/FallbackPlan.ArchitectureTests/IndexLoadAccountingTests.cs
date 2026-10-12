using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;

namespace FallbackPlan.ArchitectureTests;

/// <summary>
/// Every index load the product makes passes sequence accounting (FR-MAN-016,
/// [ADR-0022](../../docs/adr/0022-standalone-metadata-records-and-index-identifiers.md)
/// Decision 7, amended 2026-10).
/// </summary>
/// <remarks>
/// <para>
/// A writer spends its sequence numbers on journal records and blob counters
/// as well as on deltas, and the index plane sees only the deltas. A load
/// without the accounting therefore reads every journal record and every
/// blob a backup wrote as a gap. Once its patience is spent, each one becomes
/// a delta reported missing.
/// </para>
/// <para>
/// The rule is held here, in the compiled code, because nothing a rebuild
/// reports can show it yet. The generation a product load counts patience in
/// never advances at format 3, so every gap stays unresolved and none is ever
/// judged, accounted or not. <c>Repository.Tests/SequenceAccountingTests</c>
/// shows the accounting deciding a load whose generations have moved. This
/// test shows the product passing it to every load that judges a gap.
/// </para>
/// </remarks>
[TestClass]
public sealed class IndexLoadAccountingTests
{
    /// <summary>The loads that need no accounting of their own, and why.</summary>
    private static readonly string[] Exempt =
    [
        // Reads only the writer's own head, which unresolved gaps cannot
        // raise, and discards the load's findings.
        "FallbackPlan.Agent.ServiceRuntime::AdoptObservedHeadAsync",

        // Hands its caller's accounting to the loader unchanged.
        "FallbackPlan.Repository.Catalogue.CatalogueRebuilder::RebuildAsync",
    ];

    /// <summary>The product's rebuilds: the service's one and the CLI's rebuild-index.</summary>
    private static readonly string[] Rebuilds =
    [
        "FallbackPlan.Agent.CatalogueRebuild::RebuildIntoAsync",
        "FallbackPlan.Cli.CliApplication::RunAsync",
    ];

    [TestMethod]
    public void EveryIndexLoadTheProductMakes_PassesSequenceAccounting()
    {
        var load = typeof(IndexLoader).GetMethod(nameof(IndexLoader.LoadAsync))!;
        var rebuild = typeof(CatalogueRebuilder).GetMethod(nameof(CatalogueRebuilder.RebuildAsync))!;
        var accounted = typeof(SequenceAccounting).GetMethod(nameof(SequenceAccounting.IsAccountedAsync))!;

        var loads = new SortedSet<string>(StringComparer.Ordinal);
        var accounting = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var assembly in DependencyRuleTests.AllSourceAssemblies)
        {
            loads.UnionWith(CallSites.Of(load, assembly));
            loads.UnionWith(CallSites.Of(rebuild, assembly));
            accounting.UnionWith(CallSites.Of(accounted, assembly));
        }

        // The scan has to find the loads the product is known to make, or
        // finding none unaccounted proves nothing.
        foreach (var known in Rebuilds.Concat(Exempt))
        {
            Assert.Contains(
                known, loads,
                $"the scan did not find a load the product makes; it found: {string.Join(", ", loads)}");
        }

        var unaccounted = loads.Except(Exempt).Where(caller => !accounting.Contains(caller)).ToList();
        Assert.IsEmpty(
            unaccounted,
            "these load the index without sequence accounting, so every journal record and blob a writer "
            + "spent a number on reads as a missing delta once the patience is spent: "
            + string.Join(", ", unaccounted));
    }
}

using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace FallbackPlan.ArchitectureTests;

/// <summary>
/// Enforces the dependency rules in docs/architecture/11-solution-structure.md section 2.
///
/// These are tests rather than documentation because a rule that is only written
/// down is a rule that erodes. Each test names the rule it enforces and why the
/// rule exists, so a future reader deciding whether to relax one knows what they
/// would be giving up.
/// </summary>
public sealed class DependencyRuleTests
{
    private static Assembly Domain => typeof(Domain.AssemblyMarker).Assembly;
    private static Assembly Format => typeof(Repository.Format.AssemblyMarker).Assembly;
    private static Assembly Crypto => typeof(Repository.Crypto.AssemblyMarker).Assembly;
    private static Assembly StorageAbstractions => typeof(Storage.Abstractions.AssemblyMarker).Assembly;
    private static Assembly ImportAbstractions => typeof(Import.Abstractions.AssemblyMarker).Assembly;

    private static void AssertPasses(TestResult result, string rule)
    {
        Assert.True(
            result.IsSuccessful,
            $"{rule}\nOffending types:\n  " +
            string.Join("\n  ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// Domain is the one project everything else may depend on, so it must
    /// depend on nothing. A single infrastructure reference here would make the
    /// whole layering advisory.
    /// </summary>
    [Fact]
    public void Domain_has_no_infrastructure_dependencies()
    {
        AssertPasses(
            Types.InAssembly(Domain)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Repository",
                    "FallbackPlan.Storage",
                    "FallbackPlan.Import",
                    "Microsoft.Data.Sqlite",
                    "System.Formats.Cbor",
                    "ZstdSharp")
                .GetResult(),
            "Domain must have no infrastructure dependencies.");
    }

    /// <summary>
    /// The standalone recovery tool consumes Repository.Format and must build and
    /// run on a clean machine with the fewest possible moving parts. A provider
    /// SDK or a host dependency reaching into the format layer would defeat that,
    /// and the recovery tool is the last line of defence when everything else has
    /// failed. See docs/architecture/08-restore-and-recovery.md section 5.
    /// </summary>
    [Fact]
    public void Repository_Format_has_no_provider_or_host_dependencies()
    {
        AssertPasses(
            Types.InAssembly(Format)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Storage.Local",
                    "FallbackPlan.Repository.Catalogue",
                    "FallbackPlan.Cli",
                    "Microsoft.Data.Sqlite",
                    "Microsoft.AspNetCore")
                .GetResult(),
            "Repository.Format must be consumable by the standalone recovery tool.");
    }

    /// <summary>
    /// The CrashPlan importer is optional, separately packaged, and parses hostile
    /// input. Nothing in the core may reference it: doing so would couple the
    /// project's licence to the importer's dependencies (ADR-0001, ADR-0015) and
    /// pull a parser for untrusted archives inside the trust boundary (T-15).
    /// </summary>
    [Fact]
    public void Core_does_not_reference_import_implementations()
    {
        foreach (var assembly in new[] { Domain, Format, Crypto, StorageAbstractions })
        {
            AssertPasses(
                Types.InAssembly(assembly)
                    .ShouldNot()
                    .HaveDependencyOn("FallbackPlan.Import.CrashPlan")
                    .GetResult(),
                $"{assembly.GetName().Name} must not reference any import implementation.");
        }
    }

    /// <summary>
    /// The neutral legacy model exists so that importers for CrashPlan, restic,
    /// Kopia and others can feed the same pipeline without any of them reaching
    /// into the core. It must therefore know nothing about the repository engine.
    /// </summary>
    [Fact]
    public void Import_Abstractions_depends_only_on_Domain()
    {
        AssertPasses(
            Types.InAssembly(ImportAbstractions)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Repository",
                    "FallbackPlan.Storage")
                .GetResult(),
            "Import.Abstractions must depend only on Domain.");
    }

    /// <summary>
    /// Provider-specific behaviour must not leak into repository semantics
    /// (NFR-COMP-005). A repository written to one provider must restore
    /// identically from another, which is only true if the abstraction knows
    /// nothing about any concrete provider.
    /// </summary>
    [Fact]
    public void Storage_Abstractions_knows_no_concrete_provider()
    {
        AssertPasses(
            Types.InAssembly(StorageAbstractions)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Storage.Local",
                    "FallbackPlan.Storage.Peer",
                    "FallbackPlan.Storage.AzureBlob",
                    "FallbackPlan.Storage.S3",
                    "Azure.Storage",
                    "Amazon.S3")
                .GetResult(),
            "Storage.Abstractions must not depend on any concrete provider.");
    }

    /// <summary>
    /// Cryptography stays in one place. Scattering key derivation or AEAD calls
    /// across projects is how a nonce construction quietly diverges from its
    /// specification, which is the one class of defect this project cannot
    /// recover from (ADR-0005).
    /// </summary>
    [Fact]
    public void Crypto_does_not_depend_on_higher_layers()
    {
        AssertPasses(
            Types.InAssembly(Crypto)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Repository.Packing",
                    "FallbackPlan.Repository.Index",
                    "FallbackPlan.Repository.Catalogue",
                    "FallbackPlan.Cli")
                .GetResult(),
            "Repository.Crypto must not depend on higher layers.");
    }
}

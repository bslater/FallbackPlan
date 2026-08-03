using System.Reflection;
using System.Runtime.CompilerServices;
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
///
/// An honest caveat, stated here rather than discovered: the IL-level rules in
/// this file are currently VACUOUS. Every src assembly contains only an
/// AssemblyMarker, so an assertion that empty assembly X does not reference Y
/// cannot fail under any circumstances today. The rules exist so that the first
/// real code lands under enforcement rather than before it — but green here
/// does not yet mean "verified", and the two project-file canary tests at the
/// bottom are the only assertions in this file with teeth right now.
/// </summary>
public sealed class DependencyRuleTests
{
    private static Assembly Domain => typeof(Domain.AssemblyMarker).Assembly;
    private static Assembly Format => typeof(Repository.Format.AssemblyMarker).Assembly;
    private static Assembly Crypto => typeof(Repository.Crypto.AssemblyMarker).Assembly;
    private static Assembly Segmentation => typeof(Repository.Segmentation.AssemblyMarker).Assembly;
    private static Assembly Packing => typeof(Repository.Packing.AssemblyMarker).Assembly;
    private static Assembly Index => typeof(Repository.Index.AssemblyMarker).Assembly;
    private static Assembly Catalogue => typeof(Repository.Catalogue.AssemblyMarker).Assembly;
    private static Assembly RepositoryRootAssembly => typeof(Repository.AssemblyMarker).Assembly;
    private static Assembly StorageAbstractions => typeof(Storage.Abstractions.AssemblyMarker).Assembly;
    private static Assembly StorageLocal => typeof(Storage.Local.AssemblyMarker).Assembly;
    private static Assembly ImportAbstractions => typeof(Import.Abstractions.AssemblyMarker).Assembly;

    /// <summary>
    /// The Cli project has no AssemblyMarker — it is an executable, not a
    /// library — so it is loaded by name from the test output directory, where
    /// its ProjectReference guarantees it has been copied.
    /// </summary>
    private static Assembly Cli => Assembly.Load("FallbackPlan.Cli");

    /// <summary>
    /// Every src assembly. Containment rules iterate this list rather than a
    /// hand-picked subset, because a subset is how Repository.Packing acquired
    /// a Bodu reference with no rule covering it.
    /// </summary>
    private static IEnumerable<Assembly> AllSourceAssemblies =>
        [Domain, Format, Crypto, Segmentation, Packing, Index, Catalogue,
         RepositoryRootAssembly, StorageAbstractions, StorageLocal, ImportAbstractions, Cli];

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

    /// <summary>
    /// FallbackPlan needs exactly two cryptographic primitives .NET does not
    /// provide — Argon2id and XChaCha20-Poly1305 — so both come from a third
    /// party and neither inherits the platform's audit posture
    /// (specification 03 section 6.1, ADR-0019).
    ///
    /// That exposure is bounded by keeping it in one project. Repository.Crypto
    /// is the only assembly permitted to reference Bodu.Security.Cryptography;
    /// everywhere else, a call reaching an unaudited primitive would be a
    /// dependency nobody chose and nobody reviewed.
    ///
    /// The rule covers EVERY src assembly except Repository.Crypto, not a
    /// hand-picked subset — an earlier version listed four assemblies and
    /// silently left Repository.Packing (which already references Bodu.Core),
    /// Index, Catalogue, the engine root, Storage.Local and Cli uncovered.
    /// Note especially that Repository.Format is covered: it is what the
    /// standalone recovery tool links, and its dependency closure has to stay
    /// small enough to build and run on a clean machine when everything else
    /// has already failed (NFR-PORT-001).
    /// </summary>
    [Fact]
    public void Only_Repository_Crypto_may_reference_third_party_cryptography()
    {
        foreach (var assembly in AllSourceAssemblies.Where(a => a != Crypto))
        {
            AssertPasses(
                Types.InAssembly(assembly)
                    .ShouldNot()
                    .HaveDependencyOn("Bodu.Security.Cryptography")
                    .GetResult(),
                $"{assembly.GetName().Name} must not reference third-party cryptography. " +
                "Argon2id and XChaCha20-Poly1305 are confined to Repository.Crypto (ADR-0019).");
        }
    }

    /// <summary>
    /// Konscious exists in this repository for exactly one purpose: as the
    /// independent oracle Argon2idCrossVerificationTests checks Bodu against.
    /// It is a test-only dependency and is never shipped — a claim that was,
    /// until this rule, enforced by nothing but a comment in
    /// Directory.Packages.props. Two Argon2id implementations in production
    /// would double the unaudited surface for zero gain and make "which one
    /// derived this repository's KEK" a per-callsite accident.
    /// </summary>
    [Fact]
    public void No_source_assembly_references_the_test_only_argon2id_oracle()
    {
        foreach (var assembly in AllSourceAssemblies)
        {
            AssertPasses(
                Types.InAssembly(assembly)
                    .ShouldNot()
                    .HaveDependencyOn("Konscious.Security.Cryptography")
                    .GetResult(),
                $"{assembly.GetName().Name} must not reference Konscious — it is the " +
                "test-only cross-verification oracle, never a production dependency.");
        }
    }

    /// <summary>
    /// The containment rule above is only meaningful if the reference it
    /// contains actually exists. A prohibition that passes because nothing
    /// anywhere uses the library is a test that will keep passing after
    /// somebody removes the containment it claims to enforce.
    ///
    /// This asserts the other half: Repository.Crypto is where the third-party
    /// cryptography lives. If Argon2id moves — to the platform, or to another
    /// project — this fails, and whoever moved it has to decide deliberately
    /// whether the rule above still says what they want.
    ///
    /// It reads the project file rather than the compiled assembly's reference
    /// list on purpose. Repository.Crypto currently contains only an assembly
    /// marker, so the compiler emits no reference to a library no code has
    /// called yet — an assembly-level assertion would fail today for a reason
    /// that has nothing to do with the rule. The package reference (from the
    /// committed external/packages feed, ADR-0021) is the containment that
    /// exists right now, so it is the thing to pin.
    /// </summary>
    [Fact]
    public void Repository_Crypto_is_where_third_party_cryptography_actually_lives()
    {
        AssertProjectReferences("FallbackPlan.Repository.Crypto", "<PackageReference Include=\"Bodu.Security.Cryptography\" />");
    }

    /// <summary>
    /// Same canary pattern for the other Bodu reference: Repository.Packing
    /// is where Bodu.Core lives (ADR-0021). If that reference moves or is
    /// removed, this fails, and whoever made the change decides deliberately
    /// whether the containment rules above still cover what they should.
    /// </summary>
    [Fact]
    public void Repository_Packing_is_where_the_bodu_utility_library_actually_lives()
    {
        AssertProjectReferences("FallbackPlan.Repository.Packing", "<PackageReference Include=\"Bodu.Core\" />");
    }

    private static void AssertProjectReferences(string projectName, string expectedReference)
    {
        var project = Path.Combine(RepositoryRoot(), "src", projectName, projectName + ".csproj");

        Assert.True(File.Exists(project), $"Expected project file at {project}.");

        Assert.Contains(expectedReference, File.ReadAllText(project), StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from this source file to the repository root. Anchored to the
    /// source path rather than the test host's working directory, which varies
    /// between `dotnet test`, an IDE runner, and CI.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FallbackPlan.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}

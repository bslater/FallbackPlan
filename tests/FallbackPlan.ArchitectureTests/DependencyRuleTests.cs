using System.Reflection;
using System.Runtime.CompilerServices;
using NetArchTest.Rules;
using Xunit;

namespace FallbackPlan.ArchitectureTests;

/// <summary>
/// Enforces the dependency rules in docs/architecture/11-solution-structure.md
/// section 2 (NFR-PORT-002).
///
/// These are tests rather than documentation because a rule that is only written
/// down is a rule that erodes. Each test names the rule it enforces and why the
/// rule exists, so a future reader deciding whether to relax one knows what they
/// would be giving up.
///
/// This file used to carry a caveat saying the IL-level rules were VACUOUS,
/// because every src assembly then held nothing but an AssemblyMarker and an
/// assertion that an empty assembly references nothing cannot fail. That has not
/// been true for a long time: the assemblies these rules scan are full, so the
/// rules now fail when they are broken, which is what lets NFR-PORT-002 —
/// storage, cryptography, compression, segmentation, catalogue and legacy import
/// separated behind tested interfaces — cite this file as proof rather than as
/// intent. The caveat is recorded here rather than silently deleted, because a
/// test whose strength changed is worth saying so once.
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
    private static Assembly Filesystem => typeof(FallbackPlan.Filesystem.AssemblyMarker).Assembly;
    private static Assembly FilesystemLocal => typeof(FallbackPlan.Filesystem.Local.AssemblyMarker).Assembly;
    private static Assembly Restore => typeof(FallbackPlan.Restore.AssemblyMarker).Assembly;
    private static Assembly Application => typeof(FallbackPlan.Application.AssemblyMarker).Assembly;

    /// <summary>
    /// The Cli project has no AssemblyMarker — it is an executable, not a
    /// library — so it is loaded by name from the test output directory, where
    /// its ProjectReference guarantees it has been copied.
    /// </summary>
    private static Assembly Cli => Assembly.Load("FallbackPlan.Cli");

    /// <summary>The standalone recovery tool — also an executable, loaded by name.</summary>
    private static Assembly Recovery => Assembly.Load("FallbackPlan.Recovery");

    /// <summary>The Agent host — an executable, loaded by name.</summary>
    private static Assembly Agent => Assembly.Load("FallbackPlan.Agent");

    /// <summary>The client contract (ADR-0028 §7).</summary>
    private static Assembly Api => typeof(FallbackPlan.Api.ContractVersion).Assembly;

    /// <summary>The platform keystores (ADR-0028 §9).</summary>
    private static Assembly Keystore => Assembly.Load("FallbackPlan.Keystore");

    /// <summary>
    /// Every src assembly. Containment rules iterate this list rather than a
    /// hand-picked subset, because a subset is how Repository.Packing acquired
    /// a Bodu reference with no rule covering it.
    /// </summary>
    private static IEnumerable<Assembly> AllSourceAssemblies =>
        [Domain, Format, Crypto, Segmentation, Packing, Index, Catalogue,
         RepositoryRootAssembly, StorageAbstractions, StorageLocal, ImportAbstractions,
         Filesystem, FilesystemLocal, Restore, Application, Api, Keystore, Cli, Recovery, Agent];

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
    /// The filesystem layer describes what exists on disk; it never decides
    /// what happens to it. Capture policy, packing, indexing and storage all
    /// live above it, so a scanner that reached into the engine or a storage
    /// provider would invert the layering in 11 §2 — the scanner is a source
    /// the orchestrator consumes, not a client of the repository. Both the
    /// contracts assembly and its local implementation are held to Domain +
    /// Repository.Format only (Format for EntryMetadata/SourceFilesystem,
    /// the shapes the scanner emits).
    /// </summary>
    [Fact]
    public void Filesystem_depends_only_on_Domain_and_Format()
    {
        foreach (var assembly in new[] { Filesystem, FilesystemLocal })
        {
            AssertPasses(
                Types.InAssembly(assembly)
                    .ShouldNot()
                    .HaveDependencyOnAny(
                        "FallbackPlan.Repository.Crypto",
                        "FallbackPlan.Repository.Segmentation",
                        "FallbackPlan.Repository.Packing",
                        "FallbackPlan.Repository.Index",
                        "FallbackPlan.Repository.Catalogue",
                        "FallbackPlan.Storage",
                        "FallbackPlan.Import",
                        "FallbackPlan.Cli",
                        "Microsoft.Data.Sqlite")
                    .GetResult(),
                $"{assembly.GetName().Name} must depend only on Domain and Repository.Format (11 §2).");
        }
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

    /// <summary>
    /// Schedule arithmetic is the Application layer's business (ADR-0027 §1),
    /// and the recurrence engine that performs it belongs there with it.
    /// Containment matters less here than for cryptography — the library is
    /// pure occurrence math, not an unaudited primitive — but the reason to
    /// pin it is the same: a dependency that spreads by accident is one
    /// nobody chose. An engine or provider reaching for a schedule type is a
    /// signal that a policy decision has leaked out of the layer that owns
    /// it, so it should fail here and be made deliberately.
    /// </summary>
    [Fact]
    public void Only_Application_may_reference_the_recurrence_engine()
    {
        foreach (var assembly in AllSourceAssemblies.Where(a => a != Application))
        {
            AssertPasses(
                Types.InAssembly(assembly)
                    .ShouldNot()
                    .HaveDependencyOn("Bodu.Globalization.Recurrence")
                    .GetResult(),
                $"{assembly.GetName().Name} must not reference the recurrence engine. " +
                "Schedule arithmetic lives in Application (ADR-0027 §1).");
        }
    }

    /// <summary>
    /// The other half of the rule above, in the canary pattern the two
    /// cryptography rules already use: a prohibition that passes because
    /// nothing anywhere references the library is a test that keeps passing
    /// after somebody removes the containment it claims to enforce. If the
    /// recurrence reference moves out of Application, this fails and the
    /// move becomes a decision rather than a drift.
    /// </summary>
    [Fact]
    public void Application_is_where_the_recurrence_engine_actually_lives()
    {
        AssertProjectReferences(
            "FallbackPlan.Application", "<PackageReference Include=\"Bodu.Globalization.Recurrence\" />");
    }

    /// <summary>
    /// Application is the use-case layer: it "depends on domain
    /// abstractions, never on provider implementations" (11 §2). Hosts —
    /// the CLI and the Agent — compose engines and providers and pass
    /// facts in; the layer itself stays pure functions over Domain, which
    /// is what makes schedule arithmetic and status derivation testable
    /// without a repository, a clock, or a filesystem.
    ///
    /// "Only Domain" is about FallbackPlan's own layers. The recurrence
    /// engine (ADR-0027 §1) is the single third-party exception, and it is
    /// admitted precisely because it preserves the property this rule
    /// protects: it is pure occurrence arithmetic that forbids itself the
    /// wall clock and the machine time zone, so it cannot be the thing that
    /// makes this layer need a clock. The rule above pins it to this
    /// project.
    /// </summary>
    [Fact]
    public void Application_depends_only_on_Domain()
    {
        AssertPasses(
            Types.InAssembly(Application)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Repository",
                    "FallbackPlan.Storage",
                    "FallbackPlan.Filesystem",
                    "FallbackPlan.Import",
                    "FallbackPlan.Cli",
                    "Microsoft.Data.Sqlite")
                .GetResult(),
            "FallbackPlan.Application must depend only on Domain (11 §2).");
    }

    /// <summary>
    /// The recovery tool is the last line of defence: it must build and run
    /// on a clean machine with the fewest moving parts (architecture 08 §5,
    /// 11 §2; NFR-PORT-001). Its dependency closure is format, crypto,
    /// packing, and storage — no engine, no catalogue, no SQLite, no
    /// scanner, no UI. Enforced at both levels: the IL (no reference to the
    /// excluded assemblies) and the project file (an exact whitelist, so a
    /// transitive smuggle via a new ProjectReference fails loudly).
    /// </summary>
    [Fact]
    public void Recovery_depends_on_format_crypto_packing_and_storage_only()
    {
        AssertPasses(
            Types.InAssembly(Recovery)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Repository.Catalogue",
                    "FallbackPlan.Repository.Segmentation",
                    "FallbackPlan.Filesystem",
                    "FallbackPlan.Import",
                    "FallbackPlan.Cli",
                    "Microsoft.Data.Sqlite",
                    "System.CommandLine")
                .GetResult(),
            "FallbackPlan.Recovery must stay restorable on a clean machine (11 §2).");

        var project = Path.Combine(RepositoryRoot(), "src", "FallbackPlan.Recovery", "FallbackPlan.Recovery.csproj");
        var references = System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(project), "ProjectReference Include=\"[^\"]*\\\\([^\"\\\\]+)\\.csproj\"")
            .Select(match => match.Groups[1].Value)
            .Order()
            .ToArray();

        Assert.Equal(
            ["FallbackPlan.Repository.Crypto", "FallbackPlan.Repository.Format",
             "FallbackPlan.Repository.Packing", "FallbackPlan.Storage.Abstractions", "FallbackPlan.Storage.Local"],
            references);
    }

    private static void AssertProjectReferences(string projectName, string expectedReference)
    {
        var project = Path.Combine(RepositoryRoot(), "src", projectName, projectName + ".csproj");

        Assert.True(File.Exists(project), $"Expected project file at {project}.");

        Assert.Contains(expectedReference, File.ReadAllText(project), StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up to the repository root. Anchored to the test binary's own
    /// location first — ContinuousIntegrationBuild maps
    /// <see cref="CallerFilePathAttribute"/> to <c>/_/…</c>, which does not
    /// exist on a CI runner — with the source path as the fallback for
    /// runners that relocate binaries.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        var root = LocateRoot(AppContext.BaseDirectory) ?? LocateRoot(Path.GetDirectoryName(sourceFile));
        Assert.NotNull(root);
        return root;
    }

    private static string? LocateRoot(string? start)
    {
        if (string.IsNullOrEmpty(start))
        {
            return null;
        }

        var directory = new DirectoryInfo(start);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FallbackPlan.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }

    /// <summary>
    /// 11 §2: user interfaces depend on the client contract, never on the
    /// engine. The rule is enforceable only because <c>Api</c> references
    /// Domain and nothing else — a front end that could reach the engine
    /// through the contract would be a second writer, which
    /// <c>04-concurrency-and-publication.md</c> §9 forbids.
    /// </summary>
    [Fact]
    public void Api_reaches_nothing_below_domain()
    {
        AssertPasses(
            Types.InAssembly(Api)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Application",
                    "FallbackPlan.Repository",
                    "FallbackPlan.Storage",
                    "FallbackPlan.Filesystem",
                    "FallbackPlan.Keystore",
                    "Microsoft.Data.Sqlite")
                .GetResult(),
            "FallbackPlan.Api must reference Domain and nothing else (11 §2).");
    }

    /// <summary>
    /// 11 §2: <c>Recovery</c> speaks to no service in any topology
    /// (NFR-OPS-005). A recovery tool that needed a running service would not
    /// be a recovery tool.
    /// </summary>
    [Fact]
    public void Recovery_references_neither_the_contract_nor_the_application_layer()
    {
        AssertPasses(
            Types.InAssembly(Recovery)
                .ShouldNot()
                .HaveDependencyOnAny("FallbackPlan.Api", "FallbackPlan.Application", "FallbackPlan.Keystore")
                .GetResult(),
            "FallbackPlan.Recovery must run from repository plus kit alone (11 §2, NFR-OPS-005).");
    }

    /// <summary>
    /// 11 §2: the CLI is a client too, with one exception — its direct mode
    /// takes the writer role when no service is running, so it alone among the
    /// front ends may reference <c>Application</c>. That exception is why this
    /// is the one place the rule must be checked rather than assumed.
    /// </summary>
    [Fact]
    public void The_cli_is_the_only_front_end_permitted_the_application_layer()
    {
        var reference = Types.InAssembly(Cli)
            .That()
            .HaveDependencyOn("FallbackPlan.Application")
            .GetTypes()
            .ToList();

        Assert.True(
            reference.Count > 0,
            "The CLI's direct-mode exception exists only while it actually uses Application; if this canary "
            + "stops holding, the exception should be removed rather than left as dead permission.");
    }

    /// <summary>
    /// The keystore holds unlocked key material for the service account and
    /// must not become a route to anything else (NFR-SEC-009).
    /// </summary>
    [Fact]
    public void The_keystore_knows_nothing_about_repositories()
    {
        AssertPasses(
            Types.InAssembly(Keystore)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Domain",
                    "FallbackPlan.Repository",
                    "FallbackPlan.Api",
                    "FallbackPlan.Application",
                    "FallbackPlan.Storage")
                .GetResult(),
            "FallbackPlan.Keystore stores a passphrase for an account; it must not reach the repository.");
    }

}

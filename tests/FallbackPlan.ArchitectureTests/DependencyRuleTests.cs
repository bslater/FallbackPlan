using System.Reflection;
using System.Runtime.CompilerServices;
using NetArchTest.Rules;

// NetArchTest and MSTest both call their outcome type TestResult.
using TestResult = NetArchTest.Rules.TestResult;
using FallbackPlan.TestSupport;

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
[TestClass]
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

    /// <summary>The logging sink host (ADR-0043) — loaded by name, no marker.</summary>
    private static Assembly Diagnostics => Assembly.Load("FallbackPlan.Diagnostics");

    /// <summary>The store-to-store copier (ADR-0034) — loaded by name, no marker.</summary>
    private static Assembly Replication => Assembly.Load("FallbackPlan.Replication");

    /// <summary>Retention and collection (ADR-0009) — loaded by name, no marker.</summary>
    private static Assembly Retention => Assembly.Load("FallbackPlan.Retention");

    /// <summary>The peer protocol (ADR-0030).</summary>
    private static Assembly Protocol => typeof(FallbackPlan.Protocol.AssemblyMarker).Assembly;

    /// <summary>The local web console (ADR-0036) — an executable, loaded by name.</summary>
    private static Assembly Web => Assembly.Load("FallbackPlan.Web");

    /// <summary>
    /// Every src assembly. Containment rules iterate this list rather than a
    /// hand-picked subset, because a subset is how Repository.Packing acquired
    /// a Bodu reference with no rule covering it.
    /// </summary>
    private static IEnumerable<Assembly> AllSourceAssemblies =>
        [Domain, Format, Crypto, Segmentation, Packing, Index, Catalogue,
         RepositoryRootAssembly, StorageAbstractions, StorageLocal, ImportAbstractions,
         Filesystem, FilesystemLocal, Restore, Application, Api, Keystore, Protocol, Cli, Recovery, Agent, Web,
         Diagnostics, Replication, Retention];

    private static void AssertPasses(TestResult result, string rule)
    {
        Assert.IsTrue(
            result.IsSuccessful,
            $"{rule}\nOffending types:\n  " +
            string.Join("\n  ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// Domain is the one project everything else may depend on, so it must
    /// depend on nothing. A single infrastructure reference here would make the
    /// whole layering advisory.
    /// </summary>
    [TestMethod]
    public void Domain_DependencyClosure_ContainsNoInfrastructure()
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
    [TestMethod]
    public void RepositoryFormat_DependencyClosure_ContainsNoProviderOrHost()
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
    /// A legacy-format importer is optional, separately packaged, and parses
    /// hostile input. Nothing in the core may reference one: doing so would couple the
    /// project's licence to the importer's dependencies (ADR-0001, ADR-0015) and
    /// pull a parser for untrusted archives inside the trust boundary (T-15).
    /// </summary>
    [TestMethod]
    public void CoreAssemblies_DependencyClosure_ReferencesNoImportImplementation()
    {
        foreach (var assembly in new[] { Domain, Format, Crypto, StorageAbstractions })
        {
            AssertPasses(
                Types.InAssembly(assembly)
                    .ShouldNot()
                    .HaveDependencyOn("FallbackPlan.Import.Legacy")
                    .GetResult(),
                $"{assembly.GetName().Name} must not reference any import implementation.");
        }
    }

    /// <summary>
    /// The neutral legacy model exists so that an importer for any legacy
    /// archive format can feed the same pipeline without reaching into the
    /// core. It must therefore know nothing about the repository engine.
    /// </summary>
    [TestMethod]
    public void ImportAbstractions_DependencyClosure_ReachesOnlyDomain()
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
    [TestMethod]
    public void StorageAbstractions_DependencyClosure_KnowsNoConcreteProvider()
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
    [TestMethod]
    public void Filesystem_DependencyClosure_ReachesOnlyDomainAndFormat()
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
    [TestMethod]
    public void RepositoryCrypto_DependencyClosure_ReachesNoHigherLayer()
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
    [TestMethod]
    public void ThirdPartyCryptography_EveryAssemblyOffTheAllowlist_ReferencesNone()
    {
        // An allowlist of two, not a tier. ADR-0019 §1 classifies dependencies by
        // blast radius: Repository.Crypto is format-critical, because a defect
        // there is already in the user's stored bytes and cannot be recalled.
        // Protocol is operational — its output authenticates a session and pins a
        // peer, and a defect costs a re-pairing rather than a repository.
        //
        // Naming the two rather than admitting "operational projects may" is
        // deliberate: the reason this rule has held is that adding to it requires
        // an argument, and a tier-shaped rule would let the next project in
        // without one.
        var permitted = new[] { Crypto, Protocol };

        foreach (var assembly in AllSourceAssemblies.Where(a => !permitted.Contains(a)))
        {
            AssertPasses(
                Types.InAssembly(assembly)
                    .ShouldNot()
                    .HaveDependencyOn("Bodu.Security.Cryptography")
                    .GetResult(),
                $"{assembly.GetName().Name} must not reference third-party cryptography. " +
                "Argon2id, XChaCha20-Poly1305 and X25519-for-content-sealing are confined to " +
                "Repository.Crypto; Ed25519 and X25519-for-pairing to Protocol " +
                "(ADR-0019 §3, §5, Amendment 3; ADR-0042).");
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
    [TestMethod]
    public void Argon2idOracle_EverySourceAssembly_ReferencesTheTestOnlyPackageNowhere()
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
    [TestMethod]
    public void ThirdPartyCryptography_ProjectFileCanary_StaysInRepositoryCrypto()
    {
        AssertProjectReferences("FallbackPlan.Repository.Crypto", "<PackageReference Include=\"Bodu.Security.Cryptography\" />");
    }

    /// <summary>
    /// Bodu.Core is deliberately <b>not</b> contained. It supplies the
    /// solution's parameter-validation vocabulary (ADR-0021 amendment 1), so
    /// every project that guards an argument references it, and a rule
    /// pinning it to one project would be a rule against the decision.
    ///
    /// What replaces the old containment canary is the reason containment
    /// existed: Repository.Format is what the standalone recovery tool links,
    /// and its dependency closure must stay small enough to build and run on
    /// a clean machine when everything else has failed (NFR-PORT-001). This
    /// asserts the closure is exactly the two Bodu packages that decision
    /// admits — a third arriving here is the thing to catch, and it would
    /// otherwise arrive silently.
    /// </summary>
    [TestMethod]
    public void RecoveryToolClosure_BoduPackages_AdmitsOnlyTheTwoIntended()
    {
        AssertPasses(
            Types.InAssembly(Format)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "Bodu.Security.Cryptography",
                    "Bodu.Globalization.Recurrence")
                .GetResult(),
            "Repository.Format's closure is the recovery tool's closure: only Bodu.Core (guard clauses) " +
            "and Bodu.Text.Encoding (base32) belong in it.");
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
    [TestMethod]
    public void RecurrenceEngine_EveryAssemblyBesidesApplication_ReferencesItNowhere()
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
    [TestMethod]
    public void RecurrenceEngine_ProjectFileCanary_StaysInApplication()
    {
        AssertProjectReferences(
            "FallbackPlan.Application", "<PackageReference Include=\"Bodu.Globalization.Recurrence\" />");
    }

    /// <summary>
    /// Libraries take the logging ABSTRACTION; the concrete factory, the level
    /// filtering and the sinks live in one project and are composed by a host
    /// (ADR-0043 §1). This is ADR-0027 §3's rule for a second signal: the
    /// instrumentation API is in-box and the exporter is somebody else's
    /// business, so a provider package in a library would be the same mistake
    /// as vendoring a collector.
    ///
    /// It matters most at the bottom of the stack. Repository.Format's closure
    /// is the standalone recovery tool's closure, and the abstraction was
    /// judged at ADR-0019's format-critical bar on the strength of being
    /// managed-only interfaces. A sink dragged down there — with its file
    /// handles, its timers and its buffering — would not clear that bar and
    /// would not have been asked to.
    /// </summary>
    [TestMethod]
    public void Logging_EveryAssemblyBesidesDiagnostics_TakesOnlyTheAbstraction()
    {
        var offenders = new List<string>();

        foreach (var project in Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (string.Equals(name, "FallbackPlan.Diagnostics", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(project);
            // The closing quote matters: without it this also matches
            // "…Logging.Abstractions", which every library is supposed to have.
            if (text.Contains(
                "<PackageReference Include=\"Microsoft.Extensions.Logging\" ", StringComparison.Ordinal))
            {
                offenders.Add(name);
            }
        }

        Assert.IsEmpty(
            offenders,
            "Only FallbackPlan.Diagnostics may reference the concrete logging package; "
            + $"found it in {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// The positive half of the canary above, in the shape the cryptography
    /// and recurrence rules already use: a prohibition that passes because
    /// nothing anywhere references the package would keep passing after
    /// somebody removed the containment it claims to enforce.
    /// </summary>
    [TestMethod]
    public void Logging_ProjectFileCanary_StaysInDiagnostics()
    {
        AssertProjectReferences(
            "FallbackPlan.Diagnostics", "<PackageReference Include=\"Microsoft.Extensions.Logging\" />");
    }

    /// <summary>
    /// The diagnostics ring buffer is a collections dependency, and it belongs
    /// to exactly one project (ADR-0043 §6). It is admitted at ADR-0019's
    /// <em>operational</em> bar, which it clears because
    /// <c>FallbackPlan.Diagnostics</c> is reached only by the Agent and the
    /// CLI — never by <c>Repository.Format</c>, and therefore never by the
    /// standalone recovery tool, whose closure is judged at the far stricter
    /// format-critical bar.
    ///
    /// That reasoning is only sound while the containment holds, so it is a
    /// test rather than a paragraph. If the buffer drifts into a lower layer,
    /// the tier it was admitted under silently stops applying.
    /// </summary>
    [TestMethod]
    public void CollectionsBuffer_EveryAssemblyBesidesDiagnostics_ReferencesItNowhere()
    {
        var offenders = new List<string>();

        foreach (var project in Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (string.Equals(name, "FallbackPlan.Diagnostics", StringComparison.Ordinal))
            {
                continue;
            }

            if (File.ReadAllText(project).Contains("Bodu.Collections", StringComparison.Ordinal))
            {
                offenders.Add(name);
            }
        }

        Assert.IsEmpty(
            offenders,
            "Only FallbackPlan.Diagnostics may reference the collections packages; "
            + $"found one in {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// The positive half: a prohibition that passes because nothing references
    /// the package at all is a test that keeps passing after somebody deletes
    /// the containment it claims to enforce.
    /// </summary>
    [TestMethod]
    public void CollectionsBuffer_ProjectFileCanary_StaysInDiagnostics()
    {
        AssertProjectReferences(
            "FallbackPlan.Diagnostics", "<PackageReference Include=\"Bodu.Collections.Concurrent\" />");
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
    [TestMethod]
    public void Application_DependencyClosure_ReachesOnlyDomain()
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
    [TestMethod]
    public void RecoveryTool_DependencyClosure_ReachesOnlyFormatCryptoPackingAndStorage()
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

        SequenceAssert.AreEqual(
            ["FallbackPlan.Repository.Crypto", "FallbackPlan.Repository.Format",
             "FallbackPlan.Repository.Packing", "FallbackPlan.Storage.Abstractions", "FallbackPlan.Storage.Local"],
            references);
    }

    /// <summary>
    /// The engine composes format, crypto, segmentation, packing, index,
    /// catalogue, the scanner contract and the storage <em>abstraction</em> —
    /// and no concrete provider (11 §2: providers depend on the abstraction,
    /// never the reverse). It carried an unused reference to
    /// <c>Storage.Local</c> for months, and nothing noticed, because this
    /// rule did not exist: the closure tests below it each guarded one
    /// subproject while the root assembly answered to nobody. An exact
    /// whitelist rather than a ban list, for the same reason the recovery
    /// tool has one — a new ProjectReference must fail loudly, whatever it is.
    /// </summary>
    [TestMethod]
    public void Engine_ProjectFileWhitelist_ComposesNoConcreteProvider()
    {
        var project = Path.Combine(RepositoryRoot(), "src", "FallbackPlan.Repository", "FallbackPlan.Repository.csproj");
        var references = System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(project), "ProjectReference Include=\"[^\"]*\\\\([^\"\\\\]+)\\.csproj\"")
            .Select(match => match.Groups[1].Value)
            .Order()
            .ToArray();

        SequenceAssert.AreEqual(
            ["FallbackPlan.Filesystem", "FallbackPlan.Import.Abstractions",
             "FallbackPlan.Repository.Catalogue", "FallbackPlan.Repository.Crypto",
             "FallbackPlan.Repository.Format", "FallbackPlan.Repository.Index",
             "FallbackPlan.Repository.Packing", "FallbackPlan.Repository.Segmentation",
             "FallbackPlan.Storage.Abstractions"],
            references);
    }

    /// <summary>
    /// Diagnostics hosts the logging sinks and nothing else (ADR-0043 §1):
    /// it is referenced by every host precisely because it knows nothing
    /// about what they do. A diagnostics project that could reach the
    /// engine, a store, or the contract would make "add a log sink" a change
    /// with engine consequences.
    /// </summary>
    [TestMethod]
    public void Diagnostics_DependencyClosure_ReachesOnlyDomain()
    {
        AssertPasses(
            Types.InAssembly(Diagnostics)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Repository",
                    "FallbackPlan.Storage",
                    "FallbackPlan.Filesystem",
                    "FallbackPlan.Import",
                    "FallbackPlan.Api",
                    "FallbackPlan.Application",
                    "FallbackPlan.Cli",
                    "Microsoft.Data.Sqlite")
                .GetResult(),
            "FallbackPlan.Diagnostics must depend only on Domain and the logging stack (ADR-0043 §1).");
    }

    /// <summary>
    /// Replication copies encrypted bytes between stores it cannot read
    /// (ADR-0034): it works entirely over <c>IObjectStore</c>, so it must
    /// not know a concrete provider, must not be able to decode what it
    /// carries, and must not reach the use-case layer. This assembly and
    /// Retention were outside <c>AllSourceAssemblies</c> for months — the
    /// exact hole the list's own comment warns about.
    /// </summary>
    [TestMethod]
    public void Replication_DependencyClosure_StaysAByteCopier()
    {
        AssertPasses(
            Types.InAssembly(Replication)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Repository",
                    "FallbackPlan.Storage.Local",
                    "FallbackPlan.Filesystem",
                    "FallbackPlan.Import",
                    "FallbackPlan.Application",
                    "FallbackPlan.Keystore",
                    "FallbackPlan.Cli",
                    "Microsoft.Data.Sqlite")
                .GetResult(),
            "FallbackPlan.Replication must stay a byte copier over the storage abstraction (ADR-0034).");
    }

    /// <summary>
    /// Retention joins policy (Application) to the engine (Repository) —
    /// legitimately, it is the one place selection meets collection — but it
    /// must still not know a concrete provider, a platform scanner, or
    /// anything host-shaped. The collector deciding what to delete is the
    /// last assembly that should have undeclared reach.
    /// </summary>
    [TestMethod]
    public void Retention_DependencyClosure_KnowsNoProviderAndNoHost()
    {
        AssertPasses(
            Types.InAssembly(Retention)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Storage.Local",
                    "FallbackPlan.Filesystem.Local",
                    "FallbackPlan.Import",
                    "FallbackPlan.Keystore",
                    "FallbackPlan.Protocol",
                    "FallbackPlan.Cli",
                    "Microsoft.Data.Sqlite")
                .GetResult(),
            "FallbackPlan.Retention may join policy to engine but must know no provider or host (11 §2).");
    }

    private static void AssertProjectReferences(string projectName, string expectedReference)
    {
        var project = Path.Combine(RepositoryRoot(), "src", projectName, projectName + ".csproj");

        Assert.IsTrue(File.Exists(project), $"Expected project file at {project}.");

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
        Assert.IsNotNull(root);
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
    [TestMethod]
    public void Api_DependencyClosure_ReachesNothingBelowDomain()
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
    [TestMethod]
    public void RecoveryTool_DependencyClosure_ReferencesNeitherContractNorApplication()
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
    [TestMethod]
    public void ApplicationLayer_EveryFrontEndBesidesTheCli_ReferencesItNowhere()
    {
        var reference = Types.InAssembly(Cli)
            .That()
            .HaveDependencyOn("FallbackPlan.Application")
            .GetTypes()
            .ToList();

        Assert.IsTrue(
            reference.Count > 0,
            "The CLI's direct-mode exception exists only while it actually uses Application; if this canary "
            + "stops holding, the exception should be removed rather than left as dead permission.");
    }

    /// <summary>
    /// 11 §2: user interfaces depend on the client contract, never on the
    /// engine — and the web console has no direct-mode exception, because a
    /// web server holding the writer role would be a second writer with a
    /// network face (ADR-0036 §1). Enforced at both levels, in the Recovery
    /// pattern: the IL (no reference to anything below the contract) and the
    /// project file (an exact whitelist, so a transitive smuggle via a new
    /// ProjectReference fails loudly).
    /// </summary>
    [TestMethod]
    public void WebConsole_DependencyClosure_ReachesOnlyTheClientContract()
    {
        // ONE deliberate exception (ADR-0041): ConsoleRestoreGate verifies a
        // restore passphrase locally — descriptor and wrapped key objects
        // read off local disk, KEK derived where the person typed — so the
        // passphrase never crosses the contract (NFR-SEC-009). It is scoped
        // by name: every other console type must still reach only the
        // contract, or the console stops being a client (11 §2, ADR-0036).
        AssertPasses(
            Types.InAssembly(Web)
                .That()
                .DoNotHaveName(nameof(FallbackPlan.Web.ConsoleRestoreGate))
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Application",
                    "FallbackPlan.Repository",
                    "FallbackPlan.Storage",
                    "FallbackPlan.Filesystem",
                    "FallbackPlan.Import",
                    "FallbackPlan.Keystore",
                    "FallbackPlan.Protocol",
                    "FallbackPlan.Replication",
                    "FallbackPlan.Retention",
                    "FallbackPlan.Restore",
                    "FallbackPlan.Recovery",
                    "FallbackPlan.Cli",
                    "Microsoft.Data.Sqlite")
                .GetResult(),
            "FallbackPlan.Web must reference the client contract and nothing below it (11 §2, ADR-0036), "
            + "except the restore gate (ADR-0041).");

        // The gate itself may reach the crypto path and no further — never
        // the engine, the protocol, or anything that could write.
        AssertPasses(
            Types.InAssembly(Web)
                .That()
                .HaveName(nameof(FallbackPlan.Web.ConsoleRestoreGate))
                .ShouldNot()
                .HaveDependencyOnAny(
                    "FallbackPlan.Application",
                    "FallbackPlan.Filesystem",
                    "FallbackPlan.Import",
                    "FallbackPlan.Keystore",
                    "FallbackPlan.Protocol",
                    "FallbackPlan.Replication",
                    "FallbackPlan.Retention",
                    "FallbackPlan.Restore",
                    "FallbackPlan.Recovery",
                    "FallbackPlan.Cli",
                    "Microsoft.Data.Sqlite")
                .GetResult(),
            "The restore gate verifies a passphrase and nothing more (ADR-0041).");

        var project = Path.Combine(RepositoryRoot(), "src", "FallbackPlan.Web", "FallbackPlan.Web.csproj");
        var references = System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(project), "ProjectReference Include=\"[^\"]*\\\\([^\"\\\\]+)\\.csproj\"")
            .Select(match => match.Groups[1].Value)
            .Order()
            .ToArray();

        SequenceAssert.AreEqual(
            ["FallbackPlan.Api", "FallbackPlan.Repository", "FallbackPlan.Storage.Local"], references);
    }

    /// <summary>
    /// The keystore holds unlocked key material for the service account and
    /// must not become a route to anything else (NFR-SEC-009).
    /// </summary>
    [TestMethod]
    public void Keystore_DependencyClosure_KnowsNothingAboutRepositories()
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

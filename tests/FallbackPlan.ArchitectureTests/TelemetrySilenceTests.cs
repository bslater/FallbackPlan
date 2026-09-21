using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;

namespace FallbackPlan.ArchitectureTests;

/// <summary>
/// NFR-PRIV-001 — "no telemetry shall leave the device without explicit
/// opt-in" — is the one requirement of its family with no falsifier. Its two
/// siblings are traced to suites; this one has been true since the product
/// was written and true only because nobody has broken it.
///
/// The claim has two halves and they are proved differently. This file is the
/// structural half: the product contains no MEANS of transmitting — no HTTP
/// client, no exporter, no analytics package, and the outbound-capable types
/// confined to the five assemblies that are the peer protocol and the
/// loopback IPC. The other half — that a default run in fact transmits
/// nothing — is observed rather than argued, in Hosts.Tests.
///
/// Neither half is sufficient alone, and saying so is the point. An
/// in-process capture sees one run of one process; a build whose package list
/// cannot grow in silence and whose libraries cannot reach the network is
/// what makes that one run worth generalising from.
///
/// Establishes NFR-PRIV-001.
/// </summary>
[TestClass]
public sealed class TelemetrySilenceTests
{
    /// <summary>
    /// The five assemblies that are allowed to reach the network, and what
    /// each of them is for. Nothing here is a telemetry path: an operator
    /// configured every one of these connections, to an endpoint they named.
    ///
    /// <list type="bullet">
    /// <item>Api — the loopback IPC. LocalServiceListener binds
    /// AddressFamily.Unix to a UnixDomainSocketEndPoint, which is a filesystem
    /// path and never an IP address; PeerCredentials reads the calling uid
    /// across it (ADR-0028).</item>
    /// <item>Protocol — the peer dial (PeerTlsConnection), to an identity
    /// pinned at pairing (ADR-0030).</item>
    /// <item>Agent — the peer listener, bound only to an interface an
    /// administrator names; the rest of its network surface is
    /// SocketException handling.</item>
    /// <item>Cli — RemotePeer, the --connect dial.</item>
    /// <item>Web — WebConsoleHost binds Kestrel to IPAddress.Loopback
    /// (ADR-0036).</item>
    /// </list>
    ///
    /// The rule names assemblies rather than files deliberately. The
    /// outbound-capable types appear in thirteen files across these five, most
    /// of them only catching SocketException, and a file list would need
    /// editing every time one of them gained a catch block — which is how a
    /// rule stops being read and starts being suppressed.
    /// </summary>
    private static Assembly[] NetworkOwners =>
    [
        DependencyRuleTests.Api,
        DependencyRuleTests.Protocol,
        DependencyRuleTests.Agent,
        DependencyRuleTests.Cli,
        DependencyRuleTests.Web,
    ];

    /// <summary>
    /// Every package any src project references. Twelve names, not one of
    /// which is an exporter, an HTTP library or an analytics SDK.
    ///
    /// This is pinned as a set rather than prohibited by pattern because the
    /// thing being defended against has no pattern. An update check, a crash
    /// reporter and a usage beacon do not share a name; what they share is
    /// that somebody added a dependency. So the test fails on ANY change to
    /// the list, names what appeared, and makes adding a package a decision
    /// rather than a diff nobody reads.
    /// </summary>
    private static readonly string[] PinnedPackages =
    [
        "Bodu.Collections.Concurrent",
        "Bodu.Core",
        "Bodu.Globalization.Recurrence",
        "Bodu.Security.Cryptography",
        "Bodu.Text.Encoding",
        "Microsoft.Data.Sqlite",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Logging.Abstractions",
        "System.CommandLine",
        "System.Formats.Cbor",
        "System.ServiceProcess.ServiceController",
        "ZstdSharp.Port",
    ];

    /// <summary>
    /// An HTTP client is how telemetry is written. Every plausible shape of
    /// the thing this requirement forbids — an update check, a crash
    /// reporter, a usage beacon, an OTLP exporter over http/protobuf — is a
    /// request from a process to a server the operator never named, and in
    /// .NET that is System.Net.Http.
    ///
    /// The prohibition is global, including over the five assemblies that are
    /// allowed sockets, because none of them needs it and each of them is
    /// where such a call would be most natural to add: the peer protocol is
    /// raw TLS over TCP (ADR-0030), the console is served by Kestrel rather
    /// than calling out, and the IPC is a Unix domain socket. There is no
    /// legitimate HTTP client in this product, so the rule needs no allowlist
    /// — and an allowlist is what would have to be edited, visibly, if that
    /// ever stopped being true.
    /// </summary>
    [TestMethod]
    public void Telemetry_EverySourceAssembly_ReachesForNoHttpClient()
    {
        foreach (var assembly in DependencyRuleTests.AllSourceAssemblies)
        {
            DependencyRuleTests.AssertPasses(
                Types.InAssembly(assembly)
                    .ShouldNot()
                    .HaveDependencyOnAny("System.Net.Http", "System.Net.WebClient")
                    .GetResult(),
                $"{assembly.GetName().Name} must not reference an HTTP client. " +
                "Nothing in this product speaks HTTP outbound: the peer protocol is TLS over " +
                "TCP, the console is served rather than calling out, and the service IPC is a " +
                "Unix domain socket. An HTTP client here is how telemetry, an update check or " +
                "a crash reporter would be written (NFR-PRIV-001).");
        }
    }

    /// <summary>
    /// The engine must not be able to open a socket at all. Storage,
    /// retention, replication, the catalogue, the packing layer, the
    /// diagnostics sinks — none of them has any business resolving a name or
    /// connecting to an address, and confining the capability is what makes
    /// "the default build transmits nothing" a property of the build rather
    /// than of this quarter's code review.
    ///
    /// NameResolution is in the prohibited list although nothing in the
    /// product references it today. That absence is not evidence: a hostname
    /// dial through Socket.ConnectAsync(host, port) resolves without naming
    /// System.Net.Dns in IL. The entry is there to keep the namespace absent,
    /// not to prove anything about the present.
    ///
    /// This rule does NOT forbid the peer protocol, which is the point of
    /// naming its owners. NFR-PRIV-001 has never been about connections an
    /// operator asked for; pretending the product never opens a socket would
    /// make the rule a lie that somebody would eventually have to relax.
    /// </summary>
    [TestMethod]
    public void Network_EveryAssemblyOffTheDeclaredOwners_ReachesNoNetworkNamespace()
    {
        var owners = NetworkOwners;

        foreach (var assembly in DependencyRuleTests.AllSourceAssemblies.Where(a => !owners.Contains(a)))
        {
            DependencyRuleTests.AssertPasses(
                Types.InAssembly(assembly)
                    .ShouldNot()
                    .HaveDependencyOnAny(
                        "System.Net.Sockets",
                        "System.Net.Security",
                        "System.Net.NameResolution",
                        "System.Net.Dns",
                        "System.Net.IPAddress",
                        "System.Net.IPEndPoint")
                    .GetResult(),
                $"{assembly.GetName().Name} must not reach the network. Outbound capability is " +
                "confined to Api (the loopback IPC), Protocol (the peer dial), Agent (the peer " +
                "listener), Cli (--connect) and Web (Kestrel on loopback) — every one of them a " +
                "connection an operator configured to an endpoint they named. A socket anywhere " +
                "else is how the default build would stop transmitting nothing (NFR-PRIV-001).");
        }
    }

    /// <summary>
    /// The route that needs no package at all. EngineDiagnostics publishes
    /// through the in-box Meter and ActivitySource, and its own comment says
    /// exporters are a host concern — so the way telemetry leaves this
    /// product without a dependency appearing anywhere is a MeterListener or
    /// an ActivityListener attached inside it, reading those instruments and
    /// sending them on.
    ///
    /// Listening is the giveaway, not publishing: an instrument that nobody
    /// subscribes to costs a few nanoseconds and tells nobody anything, which
    /// is exactly the posture architecture 10 §5 describes. A listener is the
    /// first line of code that would change that, and it would change it
    /// without touching a project file, which is why the package pin below
    /// does not cover this case and this rule exists beside it.
    /// </summary>
    [TestMethod]
    public void InBoxTelemetry_EverySourceAssembly_AttachesNoListener()
    {
        foreach (var assembly in DependencyRuleTests.AllSourceAssemblies)
        {
            DependencyRuleTests.AssertPasses(
                Types.InAssembly(assembly)
                    .ShouldNot()
                    .HaveDependencyOnAny(
                        "System.Diagnostics.Metrics.MeterListener",
                        "System.Diagnostics.ActivityListener",
                        "System.Net.HttpListener")
                    .GetResult(),
                $"{assembly.GetName().Name} must not attach a listener to the in-box " +
                "instruments. Publishing to a Meter or an ActivitySource that nobody subscribes " +
                "to tells nobody anything; a listener is the line that turns it into telemetry, " +
                "and it needs no package reference to appear (ADR-0027 §3, ADR-0043 §1, " +
                "NFR-PRIV-001).");
        }
    }

    /// <summary>
    /// The list of things this product ships, pinned so it cannot grow in
    /// silence.
    ///
    /// It walks the project files rather than the compiled assemblies for the
    /// reason the cryptography canary already gives: a package a project
    /// references but has not called yet emits no assembly reference, so an
    /// IL-level assertion would miss exactly the commit that adds one. The
    /// project file is the containment that exists at the moment the decision
    /// is taken.
    ///
    /// The assertion is set equality in both directions. A package appearing
    /// is the case this requirement is about; a package disappearing means a
    /// containment rule elsewhere in these tests has quietly become vacuous,
    /// which is the failure the cryptography and recurrence canaries were
    /// written to catch and is worth catching here in one place for all of
    /// them.
    /// </summary>
    [TestMethod]
    public void Packages_TheSourceTree_ReferencesExactlyWhatIsPinned()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var project in Directory.EnumerateFiles(
            Path.Combine(DependencyRuleTests.RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(
                File.ReadAllText(project), "PackageReference Include=\"([^\"]+)\""))
            {
                found.Add(match.Groups[1].Value);
            }
        }

        var appeared = found.Except(PinnedPackages, StringComparer.Ordinal).ToArray();
        var vanished = PinnedPackages.Except(found, StringComparer.Ordinal).ToArray();

        Assert.IsTrue(
            appeared.Length == 0 && vanished.Length == 0,
            "The set of packages the product ships is pinned (NFR-PRIV-001): none of them is an "
            + "exporter, an HTTP library or an analytics SDK, and that is a property of the list "
            + "rather than of any one name.\n"
            + $"Appeared: {(appeared.Length == 0 ? "(none)" : string.Join(", ", appeared))}\n"
            + $"Vanished: {(vanished.Length == 0 ? "(none)" : string.Join(", ", vanished))}\n"
            + "If a package was added deliberately, add it here and say in the commit message "
            + "what it talks to.");
    }
}

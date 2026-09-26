using System.CommandLine;
using System.Net;
using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// NFR-PRIV-001 — "no telemetry shall leave the device without explicit
/// opt-in" — with its own acceptance criterion run rather than argued:
/// "default build transmits nothing; verified by network capture". A default
/// installation is set up, backs a source tree up to a local path, is asked
/// for its status and snapshots, restores a file and plans a retention pass,
/// all through the same loopback transport an operator's terminal and the web
/// console use — and the runtime's own network instrumentation records what
/// actually crossed a socket while it happened.
///
/// The companion is ArchitectureTests/TelemetrySilenceTests, and neither half
/// is sufficient alone. This suite observes one run of one process: it is not
/// a packet capture and it cannot see a child process. What makes one
/// observed run worth generalising from is the other half — a build whose
/// package list cannot grow in silence and whose libraries cannot reach the
/// network at all.
///
/// The third test is the one that keeps the first two honest. A listener that
/// never fires is indistinguishable from silence, so the instrument is shown
/// recording the product's one legitimate outbound connection — a peer
/// destination the operator configured, dialled over TLS to an address they
/// named — before it is trusted to report that everything else was quiet.
///
/// Establishes NFR-PRIV-001.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DefaultBuildSilenceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-silence-peer", Guid.NewGuid().ToString("n"));

    private const ulong PairedAt = 1_722_600_000_000;

    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private PeerGrantStore? _destinationGrants;
    private IPEndPoint? _endpoint;

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _listenerKeypair?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();

        try
        {
            if (Directory.Exists(_destinationState))
            {
                Directory.Delete(_destinationState, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public async Task ADefaultRun_BackingUpToALocalPath_ReachesNoAddressOnAnIpNetwork()
    {
        using var silence = new NetworkSilence();

        await RunTheDefaultWorkloadAsync();

        var reached = silence.Connects.Where(e => e.IsInternet).ToArray();
        Assert.IsEmpty(
            reached,
            "a default run reached an address on an IP network, which is what NFR-PRIV-001 forbids:\n"
            + string.Join("\n", reached.Select(e => "  " + e.Describe()))
            + "\neverything recorded:\n" + silence.Report());

        // A connect whose endpoint cannot be read is not evidence of silence.
        // Failing closed here is the difference between "nothing left the
        // machine" and "nothing this test could interpret left the machine".
        var unreadable = silence.Connects.Where(e => e.Family is null).ToArray();
        Assert.IsEmpty(
            unreadable,
            "a connect was recorded whose endpoint could not be read:\n"
            + string.Join("\n", unreadable.Select(e => "  " + e.Describe())));

        Assert.IsEmpty(
            silence.Resolutions,
            "a default run resolved a host name:\n" + silence.Report());
        Assert.IsEmpty(
            silence.HttpRequests,
            "a default run made an HTTP request:\n" + silence.Report());
    }

    [TestMethod]
    [PlatformCondition(
        TestPlatforms.Posix,
        "the service's local transport is a Unix domain socket here and a named pipe on Windows, "
        + "which is not a socket and writes no socket events")]
    public async Task ADefaultRun_OnAPosixHost_DialsOnlyTheServicesOwnSocketFile()
    {
        using var silence = new NetworkSilence();

        await RunTheDefaultWorkloadAsync();

        // The liveness half. The assertion above is only worth having if the
        // instrument records anything at all, and on this platform the run's
        // own IPC is the proof that it does — every command the CLI sent
        // crossed a socket, and the capture saw each one.
        Assert.IsNotEmpty(
            silence.Connects,
            "the capture recorded no connect at all, so it proves nothing about the ones it did not "
            + "record:\n" + silence.Report());

        // The service's own socket is the address it and every client derive
        // from the state directory: inside it when that fits sun_path, and at
        // LocalEndpoint's short fallback under /tmp when it does not — which
        // is every macOS runner, whose temp root alone is most of the limit.
        var serviceSocket = LocalEndpoint.AddressFor(_harness.StateDirectory);
        foreach (var connect in silence.Connects)
        {
            Assert.AreEqual(
                "Unix", connect.Family, $"a connect left the filesystem: {connect.Describe()}");
            Assert.IsNotNull(connect.UnixPath, connect.Describe());
            Assert.AreEqual(serviceSocket, connect.UnixPath, connect.Describe());
        }
    }

    [TestMethod]
    public async Task APeerDestination_TheOneConnectionAnOperatorConfigures_IsSeenByTheCapture()
    {
        // Not a violation, and the suite says so rather than leaving a reader
        // to wonder: a peer destination is an address the operator typed, to a
        // device they paired with. NFR-PRIV-001 is about what leaves WITHOUT
        // being asked for. What this case proves is that the instrument sees an
        // IP connect when there is one to see — without it, the two tests above
        // would pass just as well against a listener wired to nothing.
        using var silence = new NetworkSilence();

        var fingerprint = await StartPeerDestinationAsync();
        WritePeerConfiguration(fingerprint);
        _harness.WriteSourceFile("notes.txt", "shipped to a friend's house");

        await _harness.SetupAsync();
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            Timeout);

        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

        var dialled = silence.Connects.Where(e => e.IsInternet).ToArray();
        Assert.IsNotEmpty(
            dialled,
            "the peer was shipped to and the capture saw no IP connect, so it would not have seen an "
            + "unasked-for one either:\n" + silence.Report());
        // Decoded, not pattern-matched: the endpoint recorded is the one the
        // configuration named, which is what makes this a positive control for
        // the instrument rather than for the word "InterNetwork".
        Assert.Contains(
            _endpoint!,
            dialled.Select(e => e.InternetEndpoint).OfType<IPEndPoint>().ToArray(),
            "the capture recorded an IP connect, but not to the destination that was configured:\n"
            + silence.Report());
    }

    /// <summary>
    /// A default installation doing the ordinary things, through the transport
    /// an operator's terminal and the web console both use: the CLI in client
    /// mode against a listening service (ADR-0028 §3). Driving the command
    /// handler in process — which most suites here do, because they are about
    /// what the service decides — would open no socket at all, and a capture
    /// around it would record nothing and prove nothing.
    /// </summary>
    private async Task RunTheDefaultWorkloadAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the words worth keeping");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            Timeout);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var backup = await RunCliAsync("backup", "--set", "docs");
        Assert.AreEqual(0, backup.ExitCode, backup.All);

        foreach (var verb in new[] { "status", "snapshots" })
        {
            var read = await RunCliAsync(verb);
            Assert.AreEqual(0, read.ExitCode, $"{verb}: {read.All}");
        }

        var snapshots = await RunCliAsync("snapshots");
        Assert.AreEqual(0, snapshots.ExitCode, snapshots.All);

        var restore = await RunCliAsync(
            "restore", NewestSnapshotId(snapshots.All),
            "--output", Path.Combine(_harness.WorkPath, "restored"));
        Assert.AreEqual(0, restore.ExitCode, restore.All);

        // A dry run: a genuine default operation, and the destructive half
        // adds nothing to the question this suite asks.
        var retention = await RunCliAsync("retention");
        Assert.AreEqual(0, retention.ExitCode, retention.All);
    }

    private static string NewestSnapshotId(string listing)
    {
        foreach (var line in listing.Split('\n'))
        {
            var token = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token is { Length: 32 } && token.All(Uri.IsHexDigit))
            {
                return token;
            }
        }

        Assert.Fail("the snapshot listing named no snapshot:\n" + listing);
        return string.Empty;
    }

    private Task<HostHarness.Invocation> RunCliAsync(params string[] verbAndArguments) =>
        HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            [
                .. verbAndArguments,
                "--repo", _harness.RepositoryPath,
                "--passphrase-env", _harness.PassphraseVariable,
                "--state", _harness.StateDirectory,
            ]);

    private async Task<string> StartPeerDestinationAsync()
    {
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);

        _destinationGrants = PeerGrantStore.Open(_destinationState);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));

        var sourceGrants = PeerGrantStore.Open(_harness.StateDirectory);
        sourceGrants.Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0),
            "fallbackplan-agent/test", log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;

        await Task.CompletedTask;
        return destinationKeypair.Identity.Fingerprint;
    }

    private void WritePeerConfiguration(string fingerprint) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = fingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
                Priority = 5,
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 1h",
                Destinations = [new SetDestinationReference { Ref = "friend" }],
                DirectShip = true,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }
}

using System.CommandLine;
using System.Net;
using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Cli;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The shipped CLI driving a paired service over the remote binding (ADR-0028
/// §5; ADR-0030): the same <c>restore</c>, <c>snapshots</c>, <c>ls</c> and
/// <c>status</c> verbs a local operator uses, now carried to a remote service by
/// <c>--connect host:port --fingerprint … --state …</c>. This is the exit
/// criterion — a restore commanded from a console writes on the service's
/// machine — reached through the real command surface rather than the client
/// directly, and the refusal paths a human would hit.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RemoteConsoleTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private const ulong PairedAt = 1_722_600_000_000;

    /// <summary>Runs the real CLI with the given arguments, capturing both streams.</summary>
    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(params string[] args)
    {
        var output = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var error = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);

        var exit = await CliApplication.RunAsync(
            args, new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false });

        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Stands up a service with the remote binding bound on loopback, pairing the
    /// console's persisted identity both ways unless <paramref name="pairConsole"/>
    /// is false (the unpaired case). Returns what a CLI needs to reach it.
    /// </summary>
    private async Task<(IPEndPoint Endpoint, string ServiceFingerprint, string ConsoleState, IAsyncDisposable Stop)>
        StartServiceAsync(bool pairConsole = true)
    {
        var runtime = await StartRuntimeAsync();
        var serviceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        var serviceGrants = PeerGrantStore.Open(_harness.StateDirectory);

        // The console's identity is the one the CLI will read from this state
        // directory — persisted, not in-memory — so the service pins that.
        var consoleState = Path.Combine(_harness.WorkPath, "console");
        using (var consoleKeypair = PeerKeypairStore.Open(consoleState))
        {
            if (pairConsole)
            {
                serviceGrants.Pin(new PeerGrant(
                    consoleKeypair.Identity, "console", PeerRole.StoresForUs, PeerTerms.None, PairedAt));
            }
        }

        // The console always pins the service (it dialled and confirmed it once).
        var consoleGrants = PeerGrantStore.Open(consoleState);
        consoleGrants.Pin(new PeerGrant(
            serviceKeypair.Identity, "service", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        var remote = RemoteServiceListener.Start(
            serviceKeypair, serviceGrants,
            new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test");
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.On(remote.Endpoint.ToString()));
        remote.Bind(handler);

        // The listener signs every handshake with this keypair, so it must stay
        // alive for the life of the service — the stopper disposes it last.
        var fingerprint = serviceKeypair.Identity.Fingerprint;

        var stop = new Stopper(remote, runtime, serviceKeypair);
        return (remote.Endpoint, fingerprint, consoleState, stop);
    }

    [TestMethod]
    public async Task Restore_CommandedByAPairedConsoleOverConnect_WritesOnTheServiceMachine()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello from the service");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        var (endpoint, fingerprint, consoleState, stop) = await StartServiceAsync();
        await using var _ = stop;

        var address = $"{endpoint.Address}:{endpoint.Port}";

        // List the service's snapshots through the CLI, and take the id from the
        // first column — the same way an operator would read it before restoring.
        var listed = await RunCliAsync(
            "snapshots", "--connect", address, "--state", consoleState, "--fingerprint", fingerprint);
        Assert.AreEqual(0, listed.ExitCode, listed.Error);
        var snapshotId = listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Split(' ')[0];
        Assert.AreEqual(32, snapshotId.Length, $"expected a hex snapshot id, saw '{snapshotId}'");

        var destination = Path.Combine(_harness.WorkPath, "service-side-restore");
        var restored = await RunCliAsync(
            "restore", snapshotId, "--output", destination,
            "--connect", address, "--state", consoleState, "--fingerprint", fingerprint);

        Assert.AreEqual(0, restored.ExitCode, restored.Error);
        Assert.Contains("restored", restored.Output, StringComparison.Ordinal);

        // The files are on this (the service's) disk — the console received a
        // count and a path over the wire, never the content.
        var written = Directory.EnumerateFiles(destination, "notes.txt", SearchOption.AllDirectories).Single();
        Assert.AreEqual("hello from the service", await File.ReadAllTextAsync(written, _timeout.Token));
    }

    [TestMethod]
    public async Task SnapshotsLsStatus_OverConnect_ReturnTheServicesAnswers()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        var (endpoint, fingerprint, consoleState, stop) = await StartServiceAsync();
        await using var _ = stop;
        var address = $"{endpoint.Address}:{endpoint.Port}";

        var snapshots = await RunCliAsync(
            "snapshots", "--connect", address, "--state", consoleState, "--fingerprint", fingerprint);
        Assert.AreEqual(0, snapshots.ExitCode, snapshots.Error);
        var snapshotId = snapshots.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Split(' ')[0];

        var listing = await RunCliAsync(
            "ls", snapshotId, "--connect", address, "--state", consoleState, "--fingerprint", fingerprint);
        Assert.AreEqual(0, listing.ExitCode, listing.Error);
        Assert.Contains("notes.txt", listing.Output, StringComparison.Ordinal);

        var status = await RunCliAsync(
            "status", "--connect", address, "--state", consoleState, "--fingerprint", fingerprint);
        Assert.AreEqual(0, status.ExitCode, status.Error);
        // The status came from the service: its mode line names the machine it
        // speaks for, on stderr.
        Assert.Contains("remote", status.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Connect_ToAServiceThatNeverPairedThisConsole_IsRefusedCleanly()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        // The service does not pin the console, so the handshake refuses it.
        var (endpoint, fingerprint, consoleState, stop) = await StartServiceAsync(pairConsole: false);
        await using var _ = stop;
        var address = $"{endpoint.Address}:{endpoint.Port}";

        var refused = await RunCliAsync(
            "snapshots", "--connect", address, "--state", consoleState, "--fingerprint", fingerprint);

        Assert.AreEqual(1, refused.ExitCode);
        Assert.Contains("could not reach the paired service", refused.Error, StringComparison.Ordinal);
    }

    private async Task<ServiceRuntime> StartRuntimeAsync()
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                RepositoryPath = _harness.RepositoryPath,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }

    private sealed class Stopper(RemoteServiceListener remote, ServiceRuntime runtime, PeerKeypair keypair)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await remote.DisposeAsync();
            await runtime.DisposeAsync();
            keypair.Dispose();
        }
    }

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }
}

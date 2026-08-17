using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;
using PeerIdentity = FallbackPlan.Protocol.PeerIdentity;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The remote binding end to end against a real service (ADR-0028 §5;
/// ADR-0030): a paired console authenticates and opens a session over a real
/// TCP+TLS socket, an unpaired one is refused, and neither touches the local
/// binding — which is always present and answers regardless.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RemoteBindingTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private readonly PeerKeypair _console = PeerKeypair.Generate();

    [TestMethod]
    public async Task RemoteBinding_AnUnpairedConsole_IsRefusedAndTheLocalBindingStillAnswers()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        using var serviceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        var serviceGrants = PeerGrantStore.Open(_harness.StateDirectory);

        var log = new List<string>();
        await using var remote = RemoteServiceListener.Start(
            serviceKeypair, serviceGrants,
            new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            line => { lock (log) { log.Add(line); } });
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.On(remote.Endpoint.ToString()));
        remote.Bind(handler);

        // The local binding is up too and unaffected by any of this.
        await using var local = LocalServiceListener.Start(handler, _harness.StateDirectory);

        // The console pins the service, but the service has no grant for the
        // console. Dialling never opens a session — the exit criterion, over a
        // real socket: an unpaired client cannot reach the service.
        var consoleGrants = PeerGrantStore.Open(Path.Combine(_harness.WorkPath, "console"));
        consoleGrants.Pin(new PeerGrant(
            serviceKeypair.Identity, "service", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));

        await using (var connection = await PeerTlsConnection.DialAsync(
            remote.Endpoint.Address.ToString(), remote.Endpoint.Port, DateTimeOffset.UtcNow, _timeout.Token))
        {
            // The console is refused. It sees the service's relayed refusal or,
            // if the service closes first, a dropped connection — either is a
            // refusal, and no session opens.
            await Assert.ThrowsAsync<Exception>(async () =>
                await ThrowIfSessionOpensAsync(
                    connection, consoleGrants, serviceKeypair.Identity));
        }

        // The service side is deterministic: it refused this dialler as not
        // paired, which is the guarantee the exit criterion names.
        await WaitForAsync(() =>
        {
            lock (log)
            {
                return log.Any(line => line.Contains("NotPaired", StringComparison.Ordinal));
            }
        });

        // The local binding still answers: refusing a stranger at the remote
        // binding changed nothing about the service a local caller talks to.
        await using var client = await LocalServiceClient.ConnectAsync(_harness.StateDirectory, "test", _timeout.Token);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await client.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);
        Assert.IsTrue(description.RemoteBindingEnabled);
    }

    [TestMethod]
    public async Task RemoteBinding_APairedConsole_AuthenticatesAndOpensASession()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        using var serviceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        var serviceGrants = PeerGrantStore.Open(_harness.StateDirectory);

        // Both sides pin the other — the state a completed pairing leaves.
        serviceGrants.Pin(new PeerGrant(
            _console.Identity, "console", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));
        var consoleGrants = PeerGrantStore.Open(Path.Combine(_harness.WorkPath, "console"));
        consoleGrants.Pin(new PeerGrant(
            serviceKeypair.Identity, "service", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));

        await using var remote = RemoteServiceListener.Start(
            serviceKeypair, serviceGrants,
            new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test");
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.On(remote.Endpoint.ToString()));
        remote.Bind(handler);

        await using var connection = await PeerTlsConnection.DialAsync(
            remote.Endpoint.Address.ToString(), remote.Endpoint.Port, DateTimeOffset.UtcNow, _timeout.Token);
        var session = await PeerSessionDriver.DialAsync(
            connection, _console, consoleGrants, serviceKeypair.Identity, "console/test", null,
            cancellationToken: _timeout.Token);

        // The console reached an open session with the service it dialled.
        Assert.AreEqual(serviceKeypair.Identity, session.Peer.Identity);
        Assert.AreEqual(PeerSessionNegotiation.CurrentVersion, session.Version);
    }

    [TestMethod]
    public async Task RemoteRestore_CommandedByAPairedConsole_WritesOnTheServiceMachine()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello from the service");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        using var serviceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        var serviceGrants = PeerGrantStore.Open(_harness.StateDirectory);
        serviceGrants.Pin(new PeerGrant(
            _console.Identity, "console", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));

        var consoleState = Path.Combine(_harness.WorkPath, "console");
        var consoleGrants = PeerGrantStore.Open(consoleState);
        consoleGrants.Pin(new PeerGrant(
            serviceKeypair.Identity, "service", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));

        await using var remote = RemoteServiceListener.Start(
            serviceKeypair, serviceGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test");
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.On(remote.Endpoint.ToString()));
        remote.Bind(handler);
        await using var local = LocalServiceListener.Start(handler, _harness.StateDirectory);

        await using var console = await Cli.RemoteServiceClient.ConnectAsync(
            remote.Endpoint.Address.ToString(), remote.Endpoint.Port,
            _console, consoleGrants, serviceKeypair.Identity, "console", _timeout.Token);

        // The remote console gets the same answer a local caller would (ADR-0028
        // §6: commands and results cross the remote binding freely).
        Assert.IsInstanceOfType<SnapshotsResult>(
            await console.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        var snapshot = Assert.ContainsSingle(snapshots.Snapshots);

        // A restore commanded from the console writes on the SERVICE's machine
        // (ADR-0028 §6): the destination is a path here, the console is told
        // what happened and where — never sent the files. This is the second
        // exit criterion, over a real socket.
        var destination = Path.Combine(_harness.WorkPath, "service-side-restore");
        Assert.IsInstanceOfType<RestoreResult>(
            await console.ExecuteAsync(new RunRestoreCommand(snapshot.SnapshotId, null, destination), _timeout.Token),
            out var restored);

        Assert.AreEqual(1, restored.Restored);
        Assert.AreEqual(0, restored.Failed);
        Assert.AreEqual("complete", restored.Outcome);

        // The files are on this (the service's) disk, under the reported path —
        // the console received counts and a path string, not content.
        Assert.StartsWith(destination, restored.OutputDirectory, StringComparison.Ordinal);
        Assert.AreEqual(
            "hello from the service",
            await File.ReadAllTextAsync(Path.Combine(restored.OutputDirectory, "notes.txt"), _timeout.Token));
    }

    private async Task ThrowIfSessionOpensAsync(
        PeerTlsConnection connection, PeerGrantStore consoleGrants, PeerIdentity serviceIdentity)
    {
        await PeerSessionDriver.DialAsync(
            connection, _console, consoleGrants, serviceIdentity, "console/test", null,
            cancellationToken: _timeout.Token);
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            _timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, _timeout.Token);
        }
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }

    public void Dispose()
    {
        _console.Dispose();
        _timeout.Dispose();
        _harness.Dispose();
    }
}

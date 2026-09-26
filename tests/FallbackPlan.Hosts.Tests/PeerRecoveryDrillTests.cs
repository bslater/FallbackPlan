using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The scheduled drill against a peer's replica (FR-DRL-002;
/// [ADR-0054](../../docs/adr/0054-scheduled-restore-drills.md) Amendment 3):
/// a peer is drilled only when the source's operator states a cadence for it,
/// the drill reads over the retrieval session the peer already serves, and
/// the bytes one drill may pull are capped. Does not establish FR-DRL-001.
/// </summary>
/// <remarks>
/// <para>
/// The consent problem ADR-0054 §6 named is answered on the source side: the
/// peer agreed to serve restores when it granted retrieval, and a drill is a
/// small restore — but a cadence is a standing cost, so it is never defaulted
/// onto a peer the way it is onto a local path. Absent means never, stated.
/// </para>
/// <para>
/// Every set here is write-only (the only shape setup produces), so a passing
/// drill states the sealed-content limit exactly as it does at a local path —
/// what changes is only where the bytes come from.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class PeerRecoveryDrillTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-drill", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));

    private const ulong PairedAt = 1_722_600_000_000;

    private CancellationToken Timeout => _timeout.Token;

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private PeerGrantStore? _destinationGrants;

    [TestMethod]
    public async Task Drill_APeerWithNoStatedCadence_IsNeverDue()
    {
        // A local path defaults to a thirty-day cadence; a peer does not. The
        // peer's bandwidth is somebody else's, and a drill nobody asked for
        // would spend it on a schedule the operator never wrote down.
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, drillIntervalDays: null);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");

        await using var runtime = await StartAsync();

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, pass.Ran);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record?.LastSuccessAt, "the run must have reached the peer, or the case proves nothing");
        Assert.IsNull(record.DrilledAt, "a peer with no stated cadence must not be drilled");
        Assert.IsNull(record.DrillFailure, "and must not be blamed for it either");
    }

    [TestMethod]
    public async Task Drill_APeerWithAStatedCadence_IsDrilledOverTheWireAndStatesTheSealedLimit()
    {
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, drillIntervalDays: 7);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");
        _harness.WriteSourceFile("docs/second.txt", "a shorter one");

        await using var runtime = await StartAsync();

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, pass.Ran);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.IsNotNull(record.DrilledAt, $"a peer with a stated cadence is due its first drill: error={record.DrillFailure}");
        Assert.IsNull(record.DrillFailure, record.DrillFailure);
        Assert.IsGreaterThan(0, record.DrillFiles, "a drill that reached no file over the wire has proved nothing");
        Assert.IsNotNull(record.DrillLimit);
        Assert.Contains("sealed", record.DrillLimit, StringComparison.OrdinalIgnoreCase);
        Assert.IsEmpty(
            runtime.Notices.Notices.Where(notice => notice.Key.StartsWith("drill-failed:", StringComparison.Ordinal)));

        // Inside its interval it is left alone, exactly as a local path is.
        var soon = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddDays(2), Timeout);
        await soon.Transfers.WaitAsync(Timeout);
        await soon.Drills.WaitAsync(Timeout);
        Assert.AreEqual(record.DrilledAt, runtime.DestinationSync.Find(_harness.DocsSetId, "friend")!.DrilledAt);
    }

    [TestMethod]
    public async Task Drill_ThePeersReplicaHasRotted_FailsAndRaisesTheNotice()
    {
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, drillIntervalDays: 7);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");

        await using var runtime = await StartAsync();

        var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await first.Transfers.WaitAsync(Timeout);
        await first.Drills.WaitAsync(Timeout);
        Assert.IsNull(
            runtime.DestinationSync.Find(_harness.DocsSetId, "friend")?.DrillFailure,
            "the clean drill must pass, or the damaged one below proves nothing");

        // The challenge samples a range; the drill walks the whole road back.
        // Length-preserving rot in every data blob the peer holds breaks the
        // container before the content plane, which a write-only drill must
        // still fail on — a limit is not a licence to pass over damage.
        TamperEveryDataBlob(await ReplicaPathAsync());

        var later = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddDays(40), Timeout);
        await later.Transfers.WaitAsync(Timeout);
        await later.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.IsNotNull(record.DrillFailure, "a drill that restored nothing over the wire is not a drill that passed");
        Assert.AreEqual(0, record.DrillFiles);
        Assert.IsNotEmpty(
            runtime.Notices.Notices.Where(notice => notice.Key.StartsWith("drill-failed:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Drill_EveryFileIsOverThePeersPerFileCap_SamplesNothingAndSaysSoWithoutFailing()
    {
        // The cap is what makes a peer drill a bounded cost on somebody
        // else's link. A file over it is not chosen; when nothing is left to
        // choose the drill has still opened the replica and listed the
        // snapshot over the wire, and says what it did not sample rather than
        // calling the peer broken.
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, drillIntervalDays: 7);
        _harness.WriteSourceFile("docs/large.txt", new string('l', 90_000));

        await using var runtime = await StartAsync();
        await ShipAsync(runtime);

        var outcome = await RecoveryDrillJob.RunAsync(
            runtime, runtime.Configuration.BackupSets[0], "friend",
            (ulong)DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            new RecoveryDrillJob.SampleBudget(FileCap: 1024, TotalCap: 1024 * 1024), Timeout);

        Assert.IsNull(outcome.Failure, outcome.Failure);
        Assert.AreEqual(0, outcome.Files);
        Assert.IsNotNull(outcome.Limit, "a drill that skipped every file must say so");
        Assert.Contains("cap", outcome.Limit, StringComparison.OrdinalIgnoreCase);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record?.DrilledAt);
        Assert.IsNull(record.DrillFailure);
        Assert.IsEmpty(
            runtime.Notices.Notices.Where(notice => notice.Key.StartsWith("drill-failed:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Drill_TheSampleWouldExceedThePeersTotalCap_StopsChoosingAndSaysSo()
    {
        // Two files that each fit under the per-file cap and together do not
        // fit under the drill's total: one is proved, the other is left, and
        // the limit names what was left.
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, drillIntervalDays: 7);
        _harness.WriteSourceFile("docs/one.txt", new string('1', 40_000));
        _harness.WriteSourceFile("docs/two.txt", new string('2', 40_000));

        await using var runtime = await StartAsync();
        await ShipAsync(runtime);

        var outcome = await RecoveryDrillJob.RunAsync(
            runtime, runtime.Configuration.BackupSets[0], "friend",
            (ulong)DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            new RecoveryDrillJob.SampleBudget(FileCap: 1024 * 1024, TotalCap: 50_000), Timeout);

        Assert.IsNull(outcome.Failure, outcome.Failure);
        Assert.AreEqual(1, outcome.Files, "one file fits the total; the second would exceed it");
        Assert.IsNotNull(outcome.Limit);
        Assert.Contains("cap", outcome.Limit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sealed", outcome.Limit, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Drill_ALocalPathIsNotCapped()
    {
        // The cap is a peer's, not a drill's: a local path is this machine's
        // own disk, and a file of any size is restored from it as before.
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, drillIntervalDays: null, withVault: true);
        _harness.WriteSourceFile("docs/large.txt", new string('l', 90_000));

        await using var runtime = await StartAsync();
        await ShipAsync(runtime);

        var outcome = await RecoveryDrillJob.RunAsync(
            runtime, runtime.Configuration.BackupSets[0], "vault",
            (ulong)DateTimeOffset.Now.ToUnixTimeMilliseconds(), Timeout);

        Assert.IsNull(outcome.Failure, outcome.Failure);
        Assert.AreEqual(1, outcome.Files);
        Assert.DoesNotContain("cap", outcome.Limit ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A direct-ship run, which is what lands the set at the peer.</summary>
    private async Task ShipAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        Assert.IsNotNull(runtime.DestinationSync.Find(_harness.DocsSetId, "friend")?.LastSuccessAt);
    }

    private static void TamperEveryDataBlob(string replicaRoot)
    {
        var files = Directory.GetFiles(
            Path.Combine(replicaRoot, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(files, "the peer must hold data blobs for this to test anything");

        foreach (var path in files)
        {
            var bytes = File.ReadAllBytes(path);
            for (var i = 200; i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            File.WriteAllBytes(path, bytes);
        }
    }

    private async Task<string> ReplicaPathAsync()
    {
        var replicas = Path.Combine(_destinationState, "replicas");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var directories = Directory.Exists(replicas) ? Directory.GetDirectories(replicas) : [];
            if (directories.Length == 1
                && File.Exists(Path.Combine(directories[0], "repository-format"))
                && Directory.Exists(Path.Combine(directories[0], "blobs", "data")))
            {
                return directories[0];
            }

            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "the peer never took a whole replica");
            await Task.Delay(100, Timeout);
        }
    }

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private void WriteConfiguration(string fingerprint, int? drillIntervalDays, bool withVault = false)
    {
        List<DestinationConfiguration> destinations =
        [
            new()
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = fingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
                DrillIntervalDays = drillIntervalDays,
            },
        ];
        List<SetDestinationReference> references = [new() { Ref = "friend" }];
        if (withVault)
        {
            Directory.CreateDirectory(Vault);
            destinations.Add(new()
            {
                Id = new string('2', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            });
            references.Add(new() { Ref = "vault" });
        }

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = destinations,
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = references,
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume — the vault is
                // told apart by name, the compliant install's shape.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }

    private async Task<string> StartDestinationAsync()
    {
        await _harness.SetupAsync();
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);

        _destinationGrants = PeerGrantStore.Open(_destinationState);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_harness.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;
        return destinationKeypair.Identity.Fingerprint;
    }

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }

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
}

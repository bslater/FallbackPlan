using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A granted run beside a restore of the same set, at a peer (FR-GC-010;
/// ADR-0172 Amendment 5). A restore reads around to a peer's copy, and a
/// peer drops what an instruction signed under a grant tells it to. While
/// the service has a restore of the set under way, no peer is instructed:
/// the run says so, the peer keeps its whole copy, and the next granted run
/// converges it. A restore in direct mode, which holds a marker of its
/// repository in the service's state directory, is held for the same way
/// (ADR-0172 Amendment 6).
/// </summary>
[TestClass]
public sealed class PeerConvergenceBesideARestoreTests : IDisposable
{
    private const string Run = "0123456789abcdef";

    private readonly HostHarness _source = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-beside-restore", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    private const ulong PairedAt = 1_722_600_000_000;

    private CancellationToken Timeout => _timeout.Token;

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private Protocol.PeerIdentity? _destinationIdentity;

    /// <summary>What has a restore of the set under way beside a granted run.</summary>
    public enum Beside
    {
        /// <summary>Nothing.</summary>
        Nothing,

        /// <summary>A run of the set the service holds under way.</summary>
        AServiceRun,

        /// <summary>A restore in direct mode, by its marker in the service's state directory.</summary>
        ARunInDirectMode,
    }

    [TestMethod]
    [DataRow(Beside.AServiceRun)]
    [DataRow(Beside.ARunInDirectMode)]
    public async Task ARetentionApply_WhileARestoreOfTheSetIsUnderWay_InstructsNoPeer_AndTheNextApplyDoes(Beside restoring)
    {
        StartDestination();
        await _source.SetupAsync();
        WriteConfiguration();

        // A scheduled pass holds no grant, so the peer takes whole copies of
        // all three days and drops nothing (ADR-0055 §6).
        for (var day = 0; day < 3; day++)
        {
            await BackUpDayAsync(day);
        }

        var replica = Directory.GetDirectories(Path.Combine(_destinationState, "replicas")).Single();
        var whole = StoredKeys(replica);
        Assert.HasCount(3, whole.Where(key => key.StartsWith("snapshots/", StringComparison.Ordinal)));

        var held = await ApplyRetentionAsync(restoring);
        Assert.Contains(
            line => line.Contains("peer 'friend' held beside a restore", StringComparison.Ordinal),
            held,
            string.Join(" | ", held));
        Assert.IsFalse(
            held.Any(line => line.Contains("converged under the grant", StringComparison.Ordinal)),
            string.Join(" | ", held));
        SequenceAssert.AreEqual(whole, StoredKeys(replica));

        var converged = await ApplyRetentionAsync(Beside.Nothing);
        Assert.Contains(
            line => line.Contains("peer 'friend' converged under the grant", StringComparison.Ordinal),
            converged,
            string.Join(" | ", converged));
        Assert.ContainsSingle(StoredKeys(replica).Where(key => key.StartsWith("snapshots/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The granted collection run a console sends, which is what instructs a
    /// peer (ADR-0055 §6), with a restore of the set under way beside it or not.
    /// </summary>
    private async Task<IReadOnlyList<string>> ApplyRetentionAsync(Beside restoring)
    {
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _source.ArchivesRoot, StateDirectory = _source.StateDirectory },
            Timeout);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _source.ReclaimGrantAsync(handler, Timeout);
        IDisposable? direct = null;
        if (restoring == Beside.ARunInDirectMode)
        {
            direct = RestoresUnderWay.Hold(
                _source.StateDirectory,
                (await runtime.ExistingArchiveAsync(_source.DocsSetId, Timeout))!.Repository.RepositoryId,
                Run);
        }

        if (restoring == Beside.AServiceRun)
        {
            runtime.RestoreRuns.Begin(new RestoreRunRecord
            {
                Run = Run,
                SetName = runtime.Configuration.BackupSets.Single().Name,
                SnapshotId = new string('e', 32),
                OutputDirectory = "/srv/restored",
                BeganAt = 1_000,
            });
        }

        var applied = await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout);
        Assert.IsInstanceOfType<RetentionResult>(applied, out var result, (applied as ServiceError)?.Message ?? applied.GetType().Name);
        if (restoring == Beside.AServiceRun)
        {
            runtime.RestoreRuns.Leave(Run);
        }

        direct?.Dispose();

        return result.Lines;
    }

    private async Task BackUpDayAsync(int day)
    {
        File.WriteAllText(Path.Combine(_source.SourceRoot, "a.txt"), $"day {day} content");
        var pass = await AgentPass.RunAsync(
            _source.ArchivesRoot, _source.StateDirectory,
            new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero).AddDays(day), Timeout);
        Assert.AreEqual(1, pass.Ran, string.Join("; ", pass.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private static List<string> StoredKeys(string path) =>
        [.. Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = _destinationIdentity!.Fingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _source.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _source.SourceRoot }],
                Schedule = "every 4h",
                Destinations =
                [
                    new SetDestinationReference
                    {
                        Ref = "friend",
                        Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    },
                ],
            },
        ],
    }.Save(Path.Combine(_source.StateDirectory, "config.json"));

    private void StartDestination()
    {
        using var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        _destinationIdentity = destinationKeypair.Identity;

        var destinationGrants = PeerGrantStore.Open(_destinationState);
        destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_source.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;
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
        _source.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_destinationState))
            {
                Directory.Delete(_destinationState, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

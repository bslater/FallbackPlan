using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A backup is proven at each peer it lands at (FR-VER-002, ADR-0199
/// Amendment 1), as it is at a local path and a store: every blob it needs
/// that the backup last proven there did not, a share a pass. A staging set's
/// peer proves them by the challenge the push already makes; a set whose
/// content is only at the peer, by the read-back over the retrieval session.
/// </summary>
/// <remarks>
/// A blob the peer has lost is sent again by the next push, so each case
/// rots one in place, where the peer still declares holding it.
/// </remarks>
[TestClass]
public sealed class PeerNewBackupProofTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly string _peersRoot = Path.Combine(Path.GetTempPath(), "fbp-peer-proof", Guid.NewGuid().ToString("n"));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;
    private LoopbackPeer? _friend;

    private CancellationToken Timeout => _timeout.Token;

    private LoopbackPeer Friend => _friend ?? throw new InvalidOperationException("no peer started");

    [TestMethod]
    public async Task AStagingSetsPeer_IsProvenThroughTheBackupItsPushSent()
    {
        await using var runtime = await StartAsync(directShip: false);

        await PassAsync(runtime);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.AreEqual(Assert.ContainsSingle(StagingRecords(runtime)), row.ProvenSnapshot);
        Assert.IsNull(row.ProvingSnapshot);
    }

    [TestMethod]
    public async Task AStagingSetsPeer_ABlobOfTheBackupRottedThere_IsFoundByTheProofCarriedOverPasses()
    {
        // The rotation asks only about the record, and the proof one blob a
        // pass: the rotted blob is reached by the proof alone.
        FanOut.ReadBackBudget = 1;
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync(directShip: false);
        await PassAsync(runtime);
        Assert.AreEqual(2, Row(runtime).VerifiedObjects, "the premise: the pass challenged the record and one blob of the backup");

        var rotted = Rot(NextAfter(Row(runtime).ProvingCursor));
        await SyncAsync(runtime);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.Failed, row.State, row.LastError);
        Assert.Contains(rotted, row.LastError!, StringComparison.Ordinal);
        Assert.IsNull(row.ProvenSnapshot, "a backup with a blob that would not prove was called proven");
        Assert.AreEqual(rotted, row.ProvingFailedKey, "the challenge's failure did not name the blob of the backup under proof");
        Assert.Contains($"'{rotted}' belongs to the backup of", row.LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task APeerOnlySetsPeer_IsProvenThroughTheBackupItsRunSent()
    {
        await using var runtime = await StartAsync(directShip: true);

        await PassAsync(runtime);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.AreEqual(Assert.ContainsSingle(Friend.Snapshots()), row.ProvenSnapshot);
        Assert.IsNull(row.ProvingSnapshot);
    }

    [TestMethod]
    public async Task APeerOnlySetsPeer_ABlobOfTheBackupRottedThere_IsFoundByTheProofCarriedOverPasses()
    {
        // The rotation asks about one key a pass, drawn at random: the proof
        // reaches the rotted blob whatever it draws.
        FanOut.ReadBackBudget = 1;
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync(directShip: true);
        await PassAsync(runtime);
        Assert.IsNull(Row(runtime).ProvenSnapshot, "the premise: the backup needs more than one blob");

        var rotted = Rot(NextAfter(Row(runtime).ProvingCursor));
        await SyncAsync(runtime);

        var row = Row(runtime);
        Assert.AreNotEqual(DestinationSyncState.InSync, row.State, "a pass that met a rotted blob of the backup called the peer in sync");
        Assert.Contains(rotted, row.LastError!, StringComparison.Ordinal);
        Assert.IsNull(row.ProvenSnapshot, "a backup with a blob that would not prove was called proven");
        Assert.AreEqual(rotted, row.ProvingFailedKey, "the read-back's failure did not name the blob of the backup under proof");
        Assert.Contains($"'{rotted}' belongs to the backup of", row.LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task APeerOnlySetsPeer_ProvesALargerBackupOverThePassesAfterIt()
    {
        // The rotation asks about one key a pass, drawn at random, and the
        // proof about one blob: every pass moves the proof on by its own ask.
        FanOut.ReadBackBudget = 1;
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync(directShip: true);
        await PassAsync(runtime);
        var newest = Assert.ContainsSingle(Friend.Snapshots());
        var first = Row(runtime);
        Assert.AreEqual(newest, first.ProvingSnapshot);
        Assert.IsNotNull(first.ProvingCursor, "the pass that sent the backup proved none of its blobs");

        var cursor = first.ProvingCursor;
        var passes = 0;
        while (Row(runtime).ProvenSnapshot is null)
        {
            Assert.IsLessThan(20, ++passes, "the proof never finished");
            await SyncAsync(runtime);
            var row = Row(runtime);
            if (row.ProvenSnapshot is null)
            {
                Assert.IsGreaterThan(0, string.CompareOrdinal(row.ProvingCursor, cursor), "a pass did not move the proof on");
                cursor = row.ProvingCursor;
            }
        }

        Assert.AreEqual(newest, Row(runtime).ProvenSnapshot);
        Assert.IsGreaterThan(0, passes);
    }

    /// <summary>The first blob the peer holds after <paramref name="cursor"/>, in key order: the one the proof asks about next.</summary>
    private string NextAfter(string? cursor)
    {
        Assert.IsNotNull(cursor, "the premise: the proof of the backup is under way");
        return Friend.Blobs()
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(key => string.CompareOrdinal(key, cursor) > 0)
            ?? throw new AssertFailedException("the premise: the peer holds a blob after the proof's cursor");
    }

    /// <summary>Rots a blob the peer holds, keeping its length, so the peer still declares it.</summary>
    private string Rot(string key)
    {
        var path = Path.Combine(
            Assert.ContainsSingle(Directory.GetDirectories(Path.Combine(Friend.State, "replicas"))),
            key.Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(path);
        for (var at = 0; at < bytes.Length; at++)
        {
            bytes[at] ^= 0xA5;
        }

        File.WriteAllBytes(path, bytes);
        return key;
    }

    private async Task<ServiceRuntime> StartAsync(bool directShip)
    {
        await _harness.SetupAsync();
        _friend = LoopbackPeer.Start("friend", Path.Combine(_peersRoot, "friend"));
        PeerGrantStore.Open(_harness.StateDirectory).Pin(
            new PeerGrant(Friend.Identity, "friend", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));
        Friend.Admit(_harness.StateDirectory);
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('1', 32),
                    Name = "friend",
                    Kind = DestinationKind.Peer,
                    Fingerprint = Friend.Fingerprint,
                    Endpoint = $"{Friend.Endpoint.Address}:{Friend.Endpoint.Port}",
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
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));

        return await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            Timeout);
    }

    private async Task PassAsync(ServiceRuntime runtime)
    {
        var pass = await Scheduler.RunPassAsync(runtime, _start, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var answer = await handler.ExecuteAsync(new SyncCommand("docs", "friend"), Timeout);
        Assert.IsInstanceOfType<SyncResult>(answer, (answer as ServiceError)?.Message);
    }

    private DestinationSyncRecord Row(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, "friend") ?? throw new AssertFailedException("no ledger row for the peer");

    private List<string> StagingRecords(ServiceRuntime runtime)
    {
        var root = runtime.ArchivePath(_harness.DocsSetId);
        var folder = Path.Combine(root, "snapshots");
        return Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))]
            : [];
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_friend is not null)
        {
            await _friend.DisposeAsync();
        }

        _timeout.Dispose();
        _harness.Dispose();
        try
        {
            if (Directory.Exists(_peersRoot))
            {
                Directory.Delete(_peersRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }
}

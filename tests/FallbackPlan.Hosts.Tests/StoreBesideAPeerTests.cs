using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A direct-ship run beside a peer alone sends its blobs on to the set's store
/// as it goes (FR-DEST-021), from a copy of each it keeps on this machine
/// until the store has it. Beside a local path the run reads a blob back from
/// there; beside a peer alone there is nothing on this machine to read, and
/// reading the blob back from the peer would cross the peer's link twice.
/// </summary>
/// <remarks>
/// <para>
/// The copies take a bounded room. A blob that finds it full is left to the
/// sync after the run, which reads it from the peer (ADR-0198), and the run
/// goes on either way. Each case holds the run after its commit until the
/// store holds what it should, so that what reached the store is known to
/// have gone up while the run went on, before its end could cut an upload
/// short.
/// </para>
/// <para>
/// A store brought current from the metadata here, with the peer away, is
/// proven by no pass that did so: the backup's proof is left owed, and the
/// next pass proves it (FR-VER-002, ADR-0199 Amendment 3).
/// </para>
/// </remarks>
[TestClass]
public sealed class StoreBesideAPeerTests : IAsyncDisposable
{
    private const string Bucket = "family-backups";

    private readonly HostHarness _harness = new();
    private readonly string _peersRoot = Path.Combine(Path.GetTempPath(), "fbp-beside-peer", Guid.NewGuid().ToString("n"));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly S3CompatibleTestServer _store = new();
    private LoopbackPeer? _friend;

    private CancellationToken Timeout => _timeout.Token;

    private string Room => Path.Combine(_harness.StateDirectory, "write-behind");

    [TestMethod]
    public async Task TheRun_SendsItsBlobsOnToTheStoreAsItGoes_AndKeepsNoCopyOfThemAfter()
    {
        // Held after its commit, the run is still going: every blob reaches
        // the store, and each copy goes once the store has it, before the
        // run's end would empty the room anyway.
        var everyBlobWentUp = false;
        var everyCopyWent = false;
        await using var runtime = await StartAsync(
            roomBytes: null,
            committed: async () =>
            {
                everyBlobWentUp = await EventuallyAsync(() =>
                    Friend.Blobs().Count > 0 && !Friend.Blobs().Except(StoreBlobs(), StringComparer.Ordinal).Any());
                everyCopyWent = await EventuallyAsync(() => CopiesKept().Count == 0);
                return everyBlobWentUp;
            });

        await RunAloneAsync(runtime);

        Assert.IsTrue(everyBlobWentUp, "every blob the peer took reached the store while the run went on");
        Assert.IsTrue(everyCopyWent, "each copy went once the store had it, while the run went on");
        var others = _store.KeysIn(Bucket).Where(key => !key.Contains("/blobs/", StringComparison.Ordinal)).ToList();
        Assert.IsEmpty(others, $"only blobs go up with the run: {string.Join(", ", others)}");
        var cloud = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Behind, cloud.State, cloud.LastError);
        Assert.Contains("went up as the run went", cloud.LastError!, StringComparison.Ordinal);
        Assert.IsEmpty(CopiesKept(), "the copies kept for the store are gone once the run ends");
    }

    [TestMethod]
    public async Task ARoomThatHoldsOneBlob_SendsTheFirst_AndLeavesTheRestToTheSync()
    {
        // The room is a single byte: the first blob is kept, since the room
        // was empty, and every blob after it finds the room full. The store
        // takes no upload until the run has committed, so the first copy
        // still holds its room when the blobs after it come.
        _store.HoldPuts();
        await using var runtime = await StartAsync(
            roomBytes: 1,
            committed: () =>
            {
                _store.ReleasePuts();
                return EventuallyAsync(() => StoreBlobs().Count > 0);
            });

        await RunAloneAsync(runtime);

        Assert.HasCount(1, StoreBlobs(), "one blob went up with the run");
        Assert.IsGreaterThan(1, Friend.Blobs().Count, "the premise: the run wrote more than one blob");
        var cloud = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Behind, cloud.State, cloud.LastError);
        Assert.Contains("room", cloud.LastError!, StringComparison.Ordinal);
        Assert.IsEmpty(CopiesKept(), "the copies kept for the store are gone once the run ends");

        await SyncAsync(runtime);

        var missing = Friend.Blobs().Except(StoreBlobs(), StringComparer.Ordinal).ToList();
        Assert.IsEmpty(missing, $"the sync left the store without: {string.Join(", ", missing)}");
        cloud = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.InSync, cloud.State, cloud.LastError);
    }

    [TestMethod]
    public async Task NoRoom_SendsNothingWithTheRun_AndTheRunGoesOn()
    {
        await using var runtime = await StartAsync(roomBytes: 0, committed: () => Task.FromResult(true));

        await RunAloneAsync(runtime);

        Assert.IsEmpty(StoreBlobs(), "with no room, nothing went up with the run");
        Assert.IsNotEmpty(Friend.Blobs(), "the premise: the run wrote to the peer");
        var cloud = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Behind, cloud.State, cloud.LastError);
        Assert.Contains("room", cloud.LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AStoreThatTakesNoUpload_NeverHoldsTheRun_AndNoCopyOutlivesIt()
    {
        // The store holds every upload unanswered, and the run is held after
        // its commit until one is being held: the run ends with copies still
        // waiting for the store, and they go with it.
        _store.HoldPuts();
        try
        {
            await using var runtime = await StartAsync(roomBytes: null, committed: () => HeldAsync(_store.APutIsHeld));

            await RunAloneAsync(runtime);

            Assert.IsTrue(_store.APutIsHeld.IsCompleted, "the run sent the store a blob as it went");
            Assert.IsEmpty(CopiesKept(), "no copy outlives the run that kept it");
            var cloud = Row(runtime, "cloud");
            Assert.AreEqual(DestinationSyncState.Behind, cloud.State, cloud.LastError);
        }
        finally
        {
            _store.ReleasePuts();
        }
    }

    [TestMethod]
    public async Task AStoreTheRunSentEveryBlob_IsBroughtCurrentByTheSync_WithThePeerAway()
    {
        // ADR-0198 Amendment 1: the store holds every blob the set's index
        // locates, so the snapshot and the index are all it lacks, and those
        // are on this machine. The peer is not needed to bring it current.
        await using var runtime = await StartAsync(roomBytes: null, committed: EveryBlobAtTheStoreAsync);
        await RunAloneAsync(runtime);

        await Friend.StopAsync();
        await SyncAsync(runtime);

        var cloud = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.InSync, cloud.State, cloud.LastError);
        Assert.IsNotEmpty(StoreSnapshots(), "the store holds the backup's snapshot");
        var missing = Friend.Blobs().Except(StoreBlobs(), StringComparer.Ordinal).ToList();
        Assert.IsEmpty(missing, $"the store lacks blobs the backup needs: {string.Join(", ", missing)}");
        Assert.IsNull(cloud.VerifiedAt, "nothing read on this machine proves the store's blobs, and none is claimed");
        Assert.IsNull(cloud.ProvenSnapshot, "a pass that proved nothing called the store proven through the backup (ADR-0199)");

        // ADR-0199 Amendment 3: the backup's proof is left owed, counted
        // from none, so the pair is due until a pass proves it.
        Assert.AreEqual(Assert.ContainsSingle(Friend.Snapshots()), cloud.ProvingSnapshot, "the backup's proof was not left owed");
        Assert.IsNull(cloud.ProvingCursor);
        Assert.AreEqual(0, cloud.ProvingBlobsProven);
        Assert.IsGreaterThan(0, cloud.ProvingBlobsLeft ?? 0, "the proof owed counted no blobs");
    }

    [TestMethod]
    public async Task ABackupBroughtToTheStoreWithThePeerAway_IsProvenThereByTheNextPass_ThoughNoChallengeIsDue()
    {
        // The store was proven through the first backup by a pass that read
        // the peer, so no challenge is due for a while. The second reaches
        // it from the metadata here, with the peer away, and that pass
        // proves nothing: only the proof it leaves owed makes the pair due,
        // and the next pass, reading the store whole by the ledger, proves
        // the backup there.
        await using var runtime = await StartAsync(roomBytes: null, committed: EveryBlobAtTheStoreAsync);
        await RunAloneAsync(runtime);
        await SyncAsync(runtime);
        var first = Assert.ContainsSingle(Friend.Snapshots());
        Assert.AreEqual(first, Row(runtime, "cloud").ProvenSnapshot, "the premise: the store is proven through the first backup");
        var blobsBefore = Friend.Blobs();

        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await RunAloneAsync(runtime);
        var second = Assert.ContainsSingle(Friend.Snapshots().Except([first], StringComparer.Ordinal));
        var added = Friend.Blobs().Except(blobsBefore, StringComparer.Ordinal).Count();
        await Friend.StopAsync();
        await SyncAsync(runtime);

        var owed = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.InSync, owed.State, owed.LastError);
        Assert.AreEqual(first, owed.ProvenSnapshot, "a pass that proved nothing moved the store's proven backup");
        Assert.AreEqual(second, owed.ProvingSnapshot, "the second backup's proof was not left owed");
        Assert.AreEqual(0, owed.ProvingBlobsProven);
        Assert.AreEqual(added, owed.ProvingBlobsLeft, "the proof owed counted blobs the first backup had needed too");

        // Not a backup's hour, and long before a challenge is due.
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddMinutes(1), Timeout);
        await pass.Transfers.WaitAsync(Timeout);

        var proven = Row(runtime, "cloud");
        Assert.IsGreaterThan(owed.LastAttemptAt, proven.LastAttemptAt, "a pair owed its backup's proof was not due");
        Assert.AreEqual(DestinationSyncState.InSync, proven.State, proven.LastError);
        Assert.AreEqual(second, proven.ProvenSnapshot, "the next pass did not prove the second backup");
        Assert.IsNull(proven.ProvingSnapshot);
        Assert.IsGreaterThanOrEqualTo(added, proven.VerifiedObjects, "the pass called the backup proven without asking about its blobs");
    }

    [TestMethod]
    public async Task ASecondRun_ThatSentEveryBlob_IsBroughtCurrentByTheSync_WithThePeerAway()
    {
        await using var runtime = await StartAsync(roomBytes: null, committed: EveryBlobAtTheStoreAsync);
        await RunAloneAsync(runtime);
        await SyncAsync(runtime);
        Assert.AreEqual(DestinationSyncState.InSync, Row(runtime, "cloud").State, "the premise: the first backup is at the store");

        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await RunAloneAsync(runtime);
        await Friend.StopAsync();
        await SyncAsync(runtime);

        var cloud = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.InSync, cloud.State, cloud.LastError);
        Assert.HasCount(2, StoreSnapshots(), "the store holds both backups' snapshots");
        var missing = Friend.Blobs().Except(StoreBlobs(), StringComparer.Ordinal).ToList();
        Assert.IsEmpty(missing, $"the store lacks blobs the backups need: {string.Join(", ", missing)}");
    }

    [TestMethod]
    public async Task AStoreMissingABlob_IsLeftBehind_WithThePeerAway()
    {
        // One blob went up with the run and the rest found no room: the
        // store lacks blobs the backup needs, and with the peer away nothing
        // this machine reads can supply them.
        _store.HoldPuts();
        await using var runtime = await StartAsync(
            roomBytes: 1,
            committed: () =>
            {
                _store.ReleasePuts();
                return EventuallyAsync(() => StoreBlobs().Count > 0);
            });
        await RunAloneAsync(runtime);

        await Friend.StopAsync();
        await SyncAsync(runtime);

        var cloud = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Behind, cloud.State, cloud.LastError);
        Assert.Contains("'friend'", cloud.LastError!, StringComparison.Ordinal);
        Assert.IsEmpty(StoreSnapshots(), "a snapshot reached the store without its blobs");
    }

    [TestMethod]
    public async Task AStoreTheRunBeforeSentNothing_IsLeftBehind_ThoughThisRunSentItEveryBlob()
    {
        // The store refuses every upload of the first run, which is not
        // synced, and takes every blob the second run writes. It still lacks
        // the first run's, which the second run did not write again.
        _store.FailFrom(servedFirst: 0, 403, "AccessDenied", method: "PUT");
        var firstRun = true;
        await using var runtime = await StartAsync(
            roomBytes: null,
            committed: () => firstRun
                ? EventuallyAsync(() => _store.Requests.Any(request => request.Method == "PUT" && request.Status == 403))
                : EventuallyAsync(() => CopiesKept().Count == 0));
        await RunAloneAsync(runtime);
        Assert.IsEmpty(StoreBlobs(), "the premise: the store took nothing from the first run");

        _store.Recover();
        firstRun = false;
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await RunAloneAsync(runtime);
        Assert.IsNotEmpty(StoreBlobs(), "the premise: the second run sent the store its blobs");
        await Friend.StopAsync();
        await SyncAsync(runtime);

        var cloud = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Behind, cloud.State, cloud.LastError);
        Assert.Contains("'friend'", cloud.LastError!, StringComparison.Ordinal);
        Assert.IsEmpty(StoreSnapshots(), "a snapshot reached the store without the first run's blobs");
    }

    private LoopbackPeer Friend => _friend ?? throw new InvalidOperationException("no peer started");

    private async Task<ServiceRuntime> StartAsync(long? roomBytes, Func<Task<bool>> committed)
    {
        _friend = LoopbackPeer.Start("friend", Path.Combine(_peersRoot, "friend"));
        WriteConfiguration();
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));
        await _harness.SetupAsync();
        PeerGrantStore.Open(_harness.StateDirectory).Pin(
            new PeerGrant(Friend.Identity, "friend", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));
        Friend.Admit(_harness.StateDirectory);
        _store.CreateBucket(Bucket);

        var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                WriteBehindRoomOverride = roomBytes,
                EnteredCheckpointing = async (_, _) => await committed(),
            },
            Timeout);
        await StoreCredentialAsync(runtime);
        return runtime;
    }

    private void WriteConfiguration()
    {
        DestinationConfiguration[] destinations =
        [
            new()
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = Friend.Fingerprint,
                Endpoint = $"{Friend.Endpoint.Address}:{Friend.Endpoint.Port}",
                Priority = 5,
            },
            new()
            {
                Id = "c10dc10dc10dc10dc10dc10dc10dc10d",
                Name = "cloud",
                Kind = DestinationKind.S3,
                Endpoint = _store.Origin.ToString(),
                Bucket = Bucket,
                Region = S3CompatibleTestServer.DefaultRegion,
                Prefix = "site-a",
            },
        ];
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [.. destinations],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [.. destinations.Select(destination => new SetDestinationReference { Ref = destination.Name })],
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
    }

    private async Task StoreCredentialAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), Timeout), out var description);
        var envelope = WriteOnlyProvisioning.SealAccessKeySecret(
            Convert.FromHexString(description.RestoreGrantRecipient!), "cloud", _store.AccessKeyId, _store.SecretAccessKey);
        var stored = await handler.ExecuteAsync(
            new SetDestinationCredentialsCommand("cloud", _store.AccessKeyId, Convert.ToHexStringLower(envelope)), Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(stored, (stored as ServiceError)?.Message);
    }

    /// <summary>The run alone, with no sync after it.</summary>
    private async Task RunAloneAsync(ServiceRuntime runtime)
    {
        var outcome = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var answer = await handler.ExecuteAsync(new SyncCommand("docs", "cloud"), Timeout);
        Assert.IsInstanceOfType<SyncResult>(answer, (answer as ServiceError)?.Message);
    }

    private DestinationSyncRecord Row(ServiceRuntime runtime, string name) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, name) ?? throw new AssertFailedException($"no ledger row for '{name}'");

    private List<string> StoreBlobs() =>
        [.. _store.KeysIn(Bucket)
            .Where(key => key.Contains("/blobs/", StringComparison.Ordinal))
            .Select(key => key[(key.IndexOf("/blobs/", StringComparison.Ordinal) + 1)..])];

    private List<string> StoreSnapshots() =>
        [.. _store.KeysIn(Bucket).Where(key => key.Contains("/snapshots/", StringComparison.Ordinal))];

    /// <summary>Holds a committed run until the store has every blob the peer took and no copy is left.</summary>
    private Task<bool> EveryBlobAtTheStoreAsync() =>
        EventuallyAsync(() =>
            Friend.Blobs().Count > 0
            && !Friend.Blobs().Except(StoreBlobs(), StringComparer.Ordinal).Any()
            && CopiesKept().Count == 0);

    private List<string> CopiesKept() =>
        Directory.Exists(Room) ? [.. Directory.GetFiles(Room, "*", SearchOption.AllDirectories)] : [];

    private static async Task<bool> HeldAsync(Task held)
    {
        try
        {
            await held.WaitAsync(TimeSpan.FromSeconds(20));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                return false;
            }

            await Task.Delay(25, Timeout);
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_friend is { } friend)
        {
            await friend.DisposeAsync();
        }

        await _store.DisposeAsync();
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

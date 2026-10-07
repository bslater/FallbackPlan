using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.S3;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// An S3-compatible destination, served end to end against a store that
/// speaks the API on this machine (FR-DEST-005, FR-REP-002, ADR-0091): a
/// staging set's archive lands under the destination's prefix and the
/// repository's id, read back and proven as a local path's is, but with part
/// of the sample drawn at random because a provider is on the far side
/// (FR-VER-001); a direct-ship set's run leaves it to the sync that follows;
/// a restore and a drill read from it, and the schedule drills it only on a
/// cadence somebody stated (FR-DRL-002); the deep sweep reads it back whole
/// on a cadence somebody stated, or when a person asks, and replaces what the
/// store altered from a sound copy (FR-VER-002, FR-VER-004, FR-VER-007,
/// FR-VER-008); and what goes wrong is told apart — no access key, a store
/// that refuses the signature, a store that does not answer.
/// </summary>
/// <remarks>
/// The access key reaches the service only as an envelope sealed to its
/// recipient key (NFR-SEC-009), lives owner-only in its state directory
/// (NFR-SEC-012), and is in nothing the service says back: not the listing,
/// not the configuration or its export (NFR-OPS-003), not a diagnostic
/// bundle (NFR-SEC-006).
/// </remarks>
[TestClass]
public sealed class S3DestinationTests : IAsyncDisposable
{
    private const string Bucket = "family-backups";
    private const string Prefix = "site-a";
    private const string CloudId = "c10dc10dc10dc10dc10dc10dc10dc10d";

    private readonly HostHarness _harness = new();
    private readonly S3CompatibleTestServer _store = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    public S3DestinationTests() => _store.CreateBucket(Bucket);

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Sync_ToAnS3Destination_LandsTheArchiveUnderItsPrefix_InSyncAndProven()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("in-sync", cloud.State, cloud.Detail);
        Assert.AreEqual("proven", cloud.Verification, "read back off the store, never taken on the write's word");
        Assert.AreEqual("independent", cloud.FailureDomain);

        var archive = await runtime.ExistingArchiveAsync(_harness.DocsSetId, Timeout);
        var root = $"{Prefix}/{archive!.Repository.RepositoryId}/";
        var keys = _store.KeysIn(Bucket);
        Assert.IsNotEmpty(keys);
        Assert.IsTrue(keys.All(key => key.StartsWith(root, StringComparison.Ordinal)), string.Join(", ", keys));
        Assert.Contains(root + Repository.RepositoryLifecycle.DescriptorKey.Value, keys);

        var writes = _store.Requests.Where(request => request.Method == "PUT").ToList();
        Assert.IsNotEmpty(writes);
        Assert.IsTrue(
            writes.All(request => request.Headers.GetValueOrDefault("if-none-match") == "*"),
            "every write is a create: nothing at the store is ever overwritten");
    }

    [TestMethod]
    public async Task Sync_AgainWithNothingNew_WritesNothing()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);
        var writesBefore = _store.Requests.Count(request => request.Method == "PUT");

        await SyncAsync(runtime);

        Assert.AreEqual("in-sync", (await RowAsync(runtime, "cloud")).State);
        Assert.AreEqual(
            writesBefore, _store.Requests.Count(request => request.Method == "PUT"),
            "a store that already holds everything is sent nothing");
    }

    [TestMethod]
    public async Task Sync_WithNoAccessKeyStored_IsFailed_AndSaysHowToStoreOne()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await BackUpAsync(runtime);

        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("failed", cloud.State);
        Assert.Contains("no access key", cloud.Detail!, StringComparison.Ordinal);
        Assert.Contains("destination-credentials", cloud.Detail!, StringComparison.Ordinal);
        Assert.IsEmpty(_store.Requests, "nothing is sent to a store the service cannot sign for");
    }

    [TestMethod]
    public async Task Sync_ToAnEndpointNothingAnswers_IsUnavailable_NotFailed()
    {
        // Unavailable is the gap closing itself when the store comes back
        // (FR-DEST-003); nothing here needs a person.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closed = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        WriteConfiguration(directShip: false, endpoint: $"http://127.0.0.1:{closed}");
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("unavailable", cloud.State, cloud.Detail);
    }

    [TestMethod]
    public async Task Sync_WithAKeyTheStoreRefuses_IsFailed_InTheStoresWords_AndNeverTheSecret()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();
        const string wrongSecret = "not/the+secret=this/store/knows/0000000";

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime, secret: wrongSecret);
        await BackUpAsync(runtime);

        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("failed", cloud.State, "a refused signature needs a person, not a retry");
        Assert.Contains("SignatureDoesNotMatch", cloud.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain(wrongSecret, cloud.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restore_AfterStagingIsLost_FindsTheReplicaUnderThePrefix_AndBringsTheFilesBack()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        string snapshotId;
        await using (var runtime = await StartAsync())
        {
            await StoreAccessKeyAsync(runtime);
            await BackUpAsync(runtime);
            Assert.AreEqual("in-sync", (await RowAsync(runtime, "cloud")).State);

            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var staging = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout);
            snapshotId = Assert.ContainsSingle(staging.Snapshots).SnapshotId;
            await handler.ExecuteAsync(new CloseRestoreSourceCommand(staging.SourceId), Timeout);
        }

        // Nothing local maps the set to its repository any more: the replica
        // is found by listing what the prefix holds.
        Directory.Delete(_harness.ArchivesRoot, recursive: true);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await using (var runtime = await StartAsync())
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var replica = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", "cloud", Timeout);
            Assert.AreEqual("cloud", replica.Location);
            Assert.AreEqual(snapshotId, Assert.ContainsSingle(replica.Snapshots).SnapshotId);

            var restored = Path.Combine(_harness.WorkPath, "from-cloud");
            Assert.IsInstanceOfType<RestoreResult>(
                await handler.ExecuteAsync(
                    new RunRestoreCommand(snapshotId, null, restored, Source: replica.SourceId, InPlace: true),
                    Timeout),
                out var restore);
            Assert.AreEqual("complete", restore.Outcome);
            Assert.AreEqual("alpha", File.ReadAllText(Path.Combine(restored, "docs", "a.txt")));
            Assert.AreEqual(new string('b', 40_000), File.ReadAllText(Path.Combine(restored, "docs", "b.txt")));
        }
    }

    [TestMethod]
    public async Task DirectShip_TheRunLeavesTheS3Destination_ToTheSyncThatFollowsIt()
    {
        // The run writes through the destinations it can ship to and leaves
        // the store behind rather than failed; the catch-up after it copies
        // what the run shipped, so the store is current when the pass ends.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        Assert.AreEqual("in-sync", (await RowAsync(runtime, "vault")).State);
        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("in-sync", cloud.State, cloud.Detail);
        Assert.IsTrue(cloud.HoldsNewest, "the store holds the newest backup whole");
        Assert.IsNotEmpty(_store.KeysIn(Bucket));
    }

    [TestMethod]
    public async Task Drill_OnAnS3Destination_BringsASampleBackFromTheStore()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<DrillResult>(
            await handler.ExecuteAsync(new RunDrillCommand("docs", "cloud"), Timeout), out var drilled);

        Assert.AreEqual(0, drilled.Failed, string.Join(" | ", drilled.Lines));
        Assert.AreEqual(0, drilled.NotDrilled, string.Join(" | ", drilled.Lines));
        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "cloud");
        Assert.IsNotNull(record?.DrilledAt);
        Assert.IsNull(record.DrillFailure, record.DrillFailure);
    }

    [TestMethod]
    public async Task Drill_TheScheduleLeavesAnS3DestinationWithNoStatedCadence_Alone()
    {
        // A drill reads from the store, and every read is a request the
        // provider may charge for: a standing cost the person who declared
        // the store chooses, so it is never defaulted onto one the way it is
        // onto a local path. Absent means never.
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "cloud");
        Assert.IsNotNull(record?.LastSuccessAt, "the sync must have reached the store, or the case proves nothing");
        Assert.IsNull(record.DrilledAt, "a store with no stated cadence must not be drilled");
        Assert.IsNull(record.DrillFailure, "and must not be blamed for it either");
    }

    [TestMethod]
    public async Task Drill_AnS3DestinationWithAStatedCadence_IsDrilledByTheSchedule_ThenLeftAloneInsideIt()
    {
        WriteConfiguration(directShip: false, drillIntervalDays: 7);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "cloud");
        Assert.IsNotNull(record?.DrilledAt, $"a store with a stated cadence is due its first drill: error={record?.DrillFailure}");
        Assert.IsNull(record.DrillFailure, record.DrillFailure);
        Assert.IsGreaterThan(0, record.DrillFiles, "a drill that brought no file back from the store has proved nothing");

        var soon = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddDays(2), Timeout);
        await soon.Transfers.WaitAsync(Timeout);
        await soon.Drills.WaitAsync(Timeout);
        Assert.AreEqual(record.DrilledAt, runtime.DestinationSync.Find(_harness.DocsSetId, "cloud")!.DrilledAt);
    }

    [TestMethod]
    public async Task ReadBack_DrawsPartOfTheStoresSampleAtRandom_WhereALocalPathFollowsTheRotationAlone()
    {
        // A provider can see which objects a read-back asks for, and a
        // rotation is predictable from the one before: a store that kept only
        // what it expected to be asked about would pass every pass. So the
        // store's sample spends a share drawn at random from the whole
        // replica, as a peer's does, and its rotation walks that much less
        // far per pass than a local path's, which has nobody on the other
        // side to game it. Both pairs start the second pass at the beginning
        // of the key space, so how far each rotation got is the share's
        // measure.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false, withVault: true);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);

        await PassAsync(runtime, DateTimeOffset.Now);
        Assert.IsNull(Pair(runtime, "cloud").SampleCursor, "the premise: one pass reads the whole first backup back");
        Assert.IsNull(Pair(runtime, "vault").SampleCursor, "the premise: one pass reads the whole first backup back");

        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await PassAsync(runtime, DateTimeOffset.Now.AddHours(2));

        var store = Pair(runtime, "cloud").SampleCursor;
        var local = Pair(runtime, "vault").SampleCursor;
        Assert.IsNotNull(store, "the second backup must hold more than one pass's rotation, or the case proves nothing");
        Assert.IsTrue(
            local is null || string.CompareOrdinal(store, local) < 0,
            $"the store's rotation must stop short of the local path's: store at {store}, local path at {local ?? "the end"}");
    }

    [TestMethod]
    public async Task Sweep_TheScheduleLeavesAnS3DestinationWithNoStatedCadence_Unread()
    {
        // Re-reading a whole replica is a request per object its provider may
        // charge for: a standing cost the person who declared the store
        // chooses, so absent means never, as for a peer (ADR-0091
        // Amendment 1).
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        var record = Pair(runtime, "cloud");
        Assert.IsNotNull(record.LastSuccessAt, "the sync must have reached the store, or the case proves nothing");
        Assert.IsNull(record.SweptAt, "a store with no stated cadence must not be swept");
    }

    [TestMethod]
    public async Task Sweep_AnS3DestinationWithAStatedCadence_ReadsTheWholeReplica_ThenLeavesItAloneInsideIt()
    {
        WriteConfiguration(directShip: false, deepVerifyIntervalDays: 30);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        var record = Pair(runtime, "cloud");
        Assert.IsNotNull(record.SweepCompletedAt, $"a store with a stated cadence is due its first circuit: {record.LastError}");
        Assert.IsNull(record.SweepCursor, "a small replica closes its circuit in one segment");
        Assert.AreEqual(DestinationSyncState.InSync, record.State, record.LastError);

        var soon = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddDays(2), Timeout);
        await soon.Transfers.WaitAsync(Timeout);
        Assert.AreEqual(record.SweptAt, Pair(runtime, "cloud").SweptAt, "inside its cadence the store is not read again");
    }

    [TestMethod]
    public async Task VerifyDestination_OnAnS3Destination_ReadsEveryObjectWhenAPersonAsks()
    {
        // No cadence is stated: a person asking is consent for the reads, as
        // it is at a peer (ADR-0035 Amendment 2).
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        var line = await VerifyAsync(runtime);
        Assert.Contains("object(s) confirmed", line, StringComparison.Ordinal);
        Assert.Contains("every stored object has now been checked", line, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task VerifyDestination_APartialReadOfAStoreWithNoCadence_NamesVerifyDestination_NotTheSweep()
    {
        // Nothing scheduled carries on a read of a store nobody stated a
        // cadence for, so the line names what does, and calls it what it is.
        ReplicaSweepJob.SegmentBudget = 1;
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<VerifyDestinationResult>(
            await handler.ExecuteAsync(new VerifyDestinationCommand("docs", "cloud", Full: false), Timeout),
            out var verified);

        var line = Assert.ContainsSingle(verified.Lines);
        Assert.Contains("more remain", line, StringComparison.Ordinal);
        Assert.Contains("nothing sweeps this destination on a schedule", line, StringComparison.Ordinal);
        Assert.Contains("verify-destination", line, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task VerifyDestination_AnObjectTheStoreAltered_IsFound_AndReplacedFromASoundCopy()
    {
        // The repair a local path gets (FR-VER-007): what the store holds is
        // replaced by deleting it and creating it again from a copy proved
        // sound, so no write overwrites anything at the store.
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        var archive = await runtime.ExistingArchiveAsync(_harness.DocsSetId, Timeout);
        var root = $"{Prefix}/{archive!.Repository.RepositoryId}/";
        var key = _store.KeysIn(Bucket).First(key => key.StartsWith(root + "blobs/data/", StringComparison.Ordinal));
        var sound = _store.ObjectIn(Bucket, key)!;
        var altered = (byte[])sound.Clone();
        altered[altered.Length / 2] ^= 0x5A;
        _store.Overwrite(Bucket, key, altered);

        var line = await VerifyAsync(runtime);
        Assert.Contains("1 damaged object(s)", line, StringComparison.Ordinal);
        Assert.Contains("each was replaced from a sound copy and re-verified", line, StringComparison.Ordinal);
        CollectionAssert.AreEqual(sound, _store.ObjectIn(Bucket, key), "the store holds what was sealed again");

        // Nothing this service writes alters an object at a store, so the
        // notice points at what can.
        var notice = runtime.Notices.Unacknowledged.Single(notice =>
            notice.Key.StartsWith("deep-verify-failed:", StringComparison.Ordinal)).Message;
        Assert.Contains("holds a key to the bucket", notice, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task VerifyDestination_OfAStoreWithNoAccessKeyStored_SaysWhyNothingWasRead()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await BackUpAsync(runtime);

        var line = await VerifyAsync(runtime);
        Assert.Contains("its replica could not be read", line, StringComparison.Ordinal);
        Assert.Contains("no access key", line, StringComparison.Ordinal);
        Assert.IsEmpty(_store.Requests, "nothing is sent to a store the service cannot sign for");
    }

    [TestMethod]
    public async Task Sweep_OfAStoreThatStoppedAnswering_IsUnavailable_NotAStall()
    {
        // A store that does not answer is a gap that closes itself, as at a
        // sync (FR-DEST-003): recorded as unavailable, never counted as a
        // stall on a blob nothing was read from.
        var dying = new S3CompatibleTestServer();
        dying.CreateBucket(Bucket);
        WriteConfiguration(directShip: false, endpoint: dying.Endpoint.ToString(), deepVerifyIntervalDays: 30);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);
        Assert.IsNotNull(Pair(runtime, "cloud").SweepCompletedAt, "the premise: the store was read while it answered");

        await dying.DisposeAsync();
        var set = Assert.ContainsSingle(runtime.Configuration.BackupSets);
        var segment = await ReplicaSweepJob.SweepAsync(
            runtime, set, "cloud", (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), userInitiated: false, Timeout);

        Assert.IsNotNull(segment.Unreadable, "nothing was read");
        var record = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Unavailable, record.State, record.LastError);
        Assert.AreEqual(0, record.SweepStalls, "a store that does not answer has stalled on nothing");
    }

    [TestMethod]
    public async Task Sweep_AStoreLastFoundUnreachable_IsNotAskedForItsSweep()
    {
        // A request to a store that does not answer holds the one transfer
        // worker through every retry, so a store the fan-out last found
        // unreachable waits for a sync to reach it before its sweep goes on,
        // as a peer does.
        WriteConfiguration(directShip: false, deepVerifyIntervalDays: 30);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        // A circuit under way, whose next segment is due at once, on a pair
        // the last sync found unreachable.
        var now = DateTimeOffset.Now;
        var nowMs = (ulong)now.ToUnixTimeMilliseconds();
        runtime.DestinationSync.RecordSweep(
            _harness.DocsSetId, "cloud", cursor: "blobs/", examined: 1, completedCircuit: false, nowMs);
        runtime.DestinationSync.RecordFailure(
            _harness.DocsSetId, "cloud", DestinationSyncState.Unavailable, "not answering", nowMs);

        var asked = _store.Requests.Count;
        var pass = await Scheduler.RunPassAsync(runtime, now.AddMinutes(1), Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        Assert.AreEqual(asked, _store.Requests.Count, "nothing reads a store last found unreachable for its sweep");
    }

    [TestMethod]
    public async Task Sweep_OfAStoreThatRefusesTheRead_StallsUnderTheBackOff_RatherThanAskingEveryPass()
    {
        // A refusal lasts until a person changes something, and every attempt
        // is a request the provider may charge for. So it is what a listing
        // that fails is at a local path: a stall with no blob to name, waited
        // out under the back-off rather than met again on every pass.
        WriteConfiguration(directShip: false, deepVerifyIntervalDays: 30);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);
        Assert.IsNotNull(Pair(runtime, "cloud").SweepCompletedAt, "the premise: the store was read while it took the key");

        await StoreAccessKeyAsync(runtime, secret: "not/the+secret=this/store/knows/0000000");
        var due = DateTimeOffset.Now.AddDays(31);
        var pass = await Scheduler.RunPassAsync(runtime, due, Timeout);
        await pass.Transfers.WaitAsync(Timeout);

        var refused = Pair(runtime, "cloud");
        Assert.AreEqual(1, refused.SweepStalls, $"the refusal is counted as a stall: {refused.LastError}");
        Assert.IsNull(refused.SweepStalledOn, "nothing was read, so no blob is named");

        var asked = _store.Requests.Count;
        var next = await Scheduler.RunPassAsync(runtime, due.AddMinutes(1), Timeout);
        await next.Transfers.WaitAsync(Timeout);
        Assert.AreEqual(asked, _store.Requests.Count, "inside the back-off nothing asks the store again");
        Assert.AreEqual(1, Pair(runtime, "cloud").SweepStalls);
    }

    [TestMethod]
    public async Task Sweep_OfAStoreWhoseReplicaHasGone_WaitsOutTheBackOff_RatherThanAskingEveryPass()
    {
        // A local path's missing directory costs nothing to look for again.
        // At a store each look is a request, so until the sync puts the
        // replica back a scheduled segment waits the back-off between looks.
        WriteConfiguration(directShip: false, deepVerifyIntervalDays: 30);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        await BackUpAsync(runtime);

        var archive = await runtime.ExistingArchiveAsync(_harness.DocsSetId, Timeout);
        var root = $"{Prefix}/{archive!.Repository.RepositoryId}/";
        foreach (var key in _store.KeysIn(Bucket).Where(key => key.StartsWith(root, StringComparison.Ordinal)))
        {
            _store.Remove(Bucket, key);
        }

        var set = Assert.ContainsSingle(runtime.Configuration.BackupSets);
        var segment = await ReplicaSweepJob.SweepAsync(
            runtime, set, "cloud", (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), userInitiated: false, Timeout);

        Assert.AreEqual(0, segment.Examined);
        var record = Pair(runtime, "cloud");
        Assert.AreEqual(1, record.SweepStalls, "the next look waits the back-off");
        Assert.IsNull(record.SweepStalledOn, "nothing was read, so no blob is named");
    }

    [TestMethod]
    public async Task SetDestinationCredentials_HoldsTheKeyOwnerOnly_AndNothingTheServiceSaysCarriesIt()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false, withVault: true);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<DestinationsResult>(
            await handler.ExecuteAsync(new ListDestinationsCommand(), Timeout), out var before);
        Assert.IsFalse(before.Destinations.Single(destination => destination.Name == "cloud").AccessKeyStored);

        await StoreAccessKeyAsync(runtime);

        Assert.IsInstanceOfType<DestinationsResult>(
            await handler.ExecuteAsync(new ListDestinationsCommand(), Timeout), out var after);
        var cloud = after.Destinations.Single(destination => destination.Name == "cloud");
        Assert.IsTrue(cloud.AccessKeyStored);
        Assert.AreEqual(Bucket, cloud.Bucket);
        Assert.AreEqual(_store.Region, cloud.Region);
        Assert.AreEqual(Prefix, cloud.Prefix);
        Assert.IsNull(
            after.Destinations.Single(destination => destination.Name == "vault").AccessKeyStored,
            "a kind that signs nothing holds no key, and is not said to lack one");

        var held = Path.Combine(_harness.StateDirectory, "destination-credentials", $"{CloudId}.json");
        Assert.IsTrue(File.Exists(held), "held in the service's own state directory, by the destination's id");
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(held));
        }

        var secret = _store.SecretAccessKey;
        Assert.DoesNotContain(secret, JsonSerializer.Serialize<ServiceResult>(after, FrameCodec.SerializerOptions), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(_harness.StateDirectory, "config.json")), StringComparison.Ordinal);
        Assert.IsInstanceOfType<ConfigurationResult>(
            await handler.ExecuteAsync(new ExportConfigurationCommand(), Timeout), out var exported);
        Assert.DoesNotContain(secret, exported.Json, StringComparison.Ordinal);

        Assert.IsInstanceOfType<DiagnosticBundleResult>(
            await handler.ExecuteAsync(new ExportDiagnosticsCommand(IncludePaths: true), Timeout), out var bundle);
        BundleInspection.AssertNowhere(
            "the secret access key", secret, bundle.FileName,
            BundleInspection.Open(Convert.FromBase64String(bundle.ContentBase64)));
    }

    [TestMethod]
    public async Task SetDestinationCredentials_WhatCannotBeStored_IsRefusedByName()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false, withVault: true);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var recipient = await RecipientAsync(handler);
        var sealedForCloud = Convert.ToHexStringLower(WriteOnlyProvisioning.SealAccessKeySecret(
            recipient, "cloud", _store.AccessKeyId, _store.SecretAccessKey));

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand("nowhere", _store.AccessKeyId, sealedForCloud), Timeout),
            out var unknown);
        Assert.AreEqual(ServiceErrorReason.NotFound, unknown.Reason);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand(
                    "vault", _store.AccessKeyId,
                    Convert.ToHexStringLower(WriteOnlyProvisioning.SealAccessKeySecret(
                        recipient, "vault", _store.AccessKeyId, _store.SecretAccessKey))),
                Timeout),
            out var local);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, local.Reason);
        Assert.Contains("vault", local.Message, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand("cloud", _store.AccessKeyId, "not hex"), Timeout),
            out var notHex);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, notHex.Reason);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new SetDestinationCredentialsCommand("cloud", "ANOTHERKEYID", sealedForCloud), Timeout),
            out var misbound);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, misbound.Reason);
        Assert.Contains("does not open", misbound.Message, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new SetDestinationCredentialsCommand("cloud", " ", sealedForCloud), Timeout),
            out var blank);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, blank.Reason);

        Assert.IsFalse(
            Directory.Exists(Path.Combine(_harness.StateDirectory, "destination-credentials"))
            && Directory.EnumerateFiles(Path.Combine(_harness.StateDirectory, "destination-credentials")).Any(),
            "nothing refused was stored");
    }

    [TestMethod]
    public async Task DeleteDestination_ForgetsItsAccessKey()
    {
        WriteConfiguration(directShip: false, referenced: false);

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        var held = Path.Combine(_harness.StateDirectory, "destination-credentials", $"{CloudId}.json");
        Assert.IsTrue(File.Exists(held));

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(new DeleteDestinationCommand("cloud"), Timeout), out var deleted);

        Assert.IsFalse(File.Exists(held), "a key for a destination nobody declares is a key nobody needs");
        Assert.IsTrue(
            deleted.Lines.Any(line => line.Contains("access key", StringComparison.Ordinal)),
            string.Join(" | ", deleted.Lines));
    }

    [TestMethod]
    public void AddressDefect_TheConfigurationAndTheProvider_AgreeOnEveryAddress()
    {
        // The configuration judges an address without the provider, which it
        // cannot reference; the provider judges it again before it dials.
        // Two rules that disagreed would let a declaration pass the one and
        // fail the other only when a backup ran.
        string[] endpoints =
        [
            "https://objects.example.net", "https://objects.example.net:8443", "http://objects.example.net",
            "http://127.0.0.1:9000", "http://localhost:9000", "http://[::1]:9000", "objects.example.net",
            "https://objects.example.net/backups", "https://objects.example.net?x=1", "ftp://objects.example.net",
            "https://user:pass@objects.example.net", "https://objects.example.net/",
        ];
        string[] buckets = ["family-backups", "ab", "No_Capitals", "a.b-c.d", "-leading", "trailing-", "x".PadRight(64, 'x')];
        string?[] regions = [null, "eu-test-1", "Eu West", "us-east-1", ""];
        string?[] prefixes = [null, "site-a", "site-a/host_1", "/site-a", "site-a/", "site-a//b", "site-a/.hidden", "a b"];

        foreach (var endpoint in endpoints)
        {
            foreach (var bucket in buckets)
            {
                foreach (var region in regions)
                {
                    foreach (var prefix in prefixes)
                    {
                        var declared = new DestinationConfiguration
                        {
                            Id = CloudId, Name = "cloud", Kind = DestinationKind.S3,
                            Endpoint = endpoint, Bucket = bucket, Region = region, Prefix = prefix,
                        };
                        var provider = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                            ? S3Location.DefectOf(uri, bucket, declared.EffectiveRegion, prefix)
                            : "not an absolute URI";

                        Assert.AreEqual(
                            provider is null, declared.AddressDefect is null,
                            $"{endpoint} {bucket} {region ?? "(none)"} {prefix ?? "(none)"}: configuration says "
                            + $"'{declared.AddressDefect ?? "fine"}', provider says '{provider ?? "fine"}'");
                    }
                }
            }
        }
    }

    private void WriteFiles()
    {
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));
    }

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
    }

    private async Task PassAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var pass = await Scheduler.RunPassAsync(runtime, now, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        Assert.AreEqual(DestinationSyncState.InSync, Pair(runtime, "cloud").State, Pair(runtime, "cloud").LastError);
        Assert.AreEqual(DestinationSyncState.InSync, Pair(runtime, "vault").State, Pair(runtime, "vault").LastError);
    }

    private DestinationSyncRecord Pair(ServiceRuntime runtime, string name) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, name)
        ?? throw new AssertFailedException($"no ledger row for '{name}'");

    private async Task<string> VerifyAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<VerifyDestinationResult>(
            await handler.ExecuteAsync(new VerifyDestinationCommand("docs", "cloud", Full: true), Timeout),
            out var verified);
        return Assert.ContainsSingle(verified.Lines);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<SyncResult>(await handler.ExecuteAsync(new SyncCommand("docs", "cloud"), Timeout));
    }

    private static async Task<byte[]> RecipientAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), CancellationToken.None), out var description);
        return Convert.FromHexString(description.RestoreGrantRecipient!);
    }

    /// <summary>Stores the access key the way every client does: sealed to the service, where it was typed.</summary>
    private async Task StoreAccessKeyAsync(ServiceRuntime runtime, string? secret = null)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var envelope = WriteOnlyProvisioning.SealAccessKeySecret(
            await RecipientAsync(handler), "cloud", _store.AccessKeyId, secret ?? _store.SecretAccessKey);
        var stored = await handler.ExecuteAsync(
            new SetDestinationCredentialsCommand("cloud", _store.AccessKeyId, Convert.ToHexStringLower(envelope)),
            Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(stored, (stored as ServiceError)?.Message);
    }

    private async Task<DestinationStatusDescriptor> RowAsync(ServiceRuntime runtime, string name)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        return Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations.Where(row => row.Name == name));
    }

    private void WriteConfiguration(
        bool directShip,
        string? endpoint = null,
        bool withVault = false,
        bool referenced = true,
        int? drillIntervalDays = null,
        int? deepVerifyIntervalDays = null)
    {
        List<DestinationConfiguration> destinations =
        [
            new DestinationConfiguration
            {
                Id = CloudId,
                Name = "cloud",
                Kind = DestinationKind.S3,
                Endpoint = endpoint ?? _store.Endpoint.ToString(),
                Bucket = Bucket,
                Region = _store.Region,
                Prefix = Prefix,
                DrillIntervalDays = drillIntervalDays,
                DeepVerifyIntervalDays = deepVerifyIntervalDays,
            },
        ];
        List<SetDestinationReference> references = referenced ? [new SetDestinationReference { Ref = "cloud" }] : [];

        // A local path beside the store, which the set copies to as well: a
        // set that does not reference the store still protects something.
        if (directShip || withVault || !referenced)
        {
            destinations.Add(new DestinationConfiguration
            {
                Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            });
            references.Insert(0, new SetDestinationReference { Ref = "vault" });
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
                    DirectShip = directShip,
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
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// An object-store destination, served end to end against a store that
/// speaks its API on this machine (FR-DEST-005, FR-REP-002, ADR-0091,
/// ADR-0093): every case here runs once for each store API the product
/// speaks, so a bucket and a container are held to one behaviour rather than
/// two that might drift. A staging set's archive lands under the
/// destination's prefix and the repository's id, read back and proven as a
/// local path's is, but with part of the sample drawn at random because a
/// provider is on the far side (FR-VER-001); a direct-ship set's run leaves
/// it to the sync that follows; a restore and a drill read from it, and the
/// schedule drills it only on a cadence somebody stated (FR-DRL-002); the
/// deep sweep reads it back whole on a cadence somebody stated, or when a
/// person asks, and replaces what the store altered from a sound copy
/// (FR-VER-002, FR-VER-004, FR-VER-007, FR-VER-008); a probe asks it for one
/// key, leaves nothing there and records no success (FR-DEST-009); and what
/// goes wrong is told apart, by a sync, a sweep and a probe alike — no
/// credential, a store that refuses it, a store that does not answer, one too
/// busy to serve (FR-QUOTA-001) — through the faults the store contract
/// names: a link cut partway, a credential refused partway, and listings that
/// lag the store's writes.
/// </summary>
/// <remarks>
/// The credential reaches the service only as an envelope sealed to its
/// recipient key (NFR-SEC-009), lives owner-only in its state directory
/// (NFR-SEC-012), and is in nothing the service says back: not the listing,
/// not the configuration or its export (NFR-OPS-003), not a diagnostic
/// bundle (NFR-SEC-006).
/// </remarks>
public abstract class ObjectStoreDestinationTests : IAsyncDisposable
{
    /// <summary>The bucket or container the destination writes to.</summary>
    protected const string Namespace = "family-backups";

    /// <summary>Where in it the destination writes.</summary>
    protected const string Prefix = "site-a";

    /// <summary>The destination's id, which names its credential's file.</summary>
    protected const string CloudId = "c10dc10dc10dc10dc10dc10dc10dc10d";

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    /// <summary>Starts the suite against a store that already holds the bucket or container.</summary>
    /// <param name="store">The store, speaking the API the subclass is about.</param>
    protected ObjectStoreDestinationTests(ObjectStoreTestServer store) => Store = store;

    /// <summary>The service's harness.</summary>
    protected HostHarness Harness { get; } = new();

    /// <summary>The store the destination names.</summary>
    protected ObjectStoreTestServer Store { get; }

    /// <summary>The test's deadline.</summary>
    protected CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(Harness.WorkPath, "vault");

    /// <summary>The credential the store accepts, as it was typed: what nothing the service says may carry.</summary>
    protected abstract string Secret { get; }

    /// <summary>A credential the store refuses, as it was typed.</summary>
    protected abstract string RefusedSecret { get; }

    /// <summary>The store's word for a credential it refuses.</summary>
    protected abstract string RefusalCode { get; }

    /// <summary>The store's word for a request it is too busy to serve: a throttle, answered with a 503.</summary>
    protected abstract string BusyCode { get; }

    /// <summary>What a destination with no credential stored is told it lacks.</summary>
    protected abstract string MissingCredentialWords { get; }

    /// <summary>What a deletion's answer calls the credential it forgot.</summary>
    protected abstract string CredentialNoun { get; }

    /// <summary>What the notice about an altered object says about who could have altered it.</summary>
    protected abstract string AlterationWords { get; }

    /// <summary>Starts another store that speaks the same API and holds the same bucket or container.</summary>
    protected abstract ObjectStoreTestServer StartAnotherStore();

    /// <summary>The endpoint a destination names for a store listening at <paramref name="origin"/>.</summary>
    /// <param name="origin">The store's scheme, host and port.</param>
    protected abstract string EndpointAt(Uri origin);

    /// <summary>The destination's declaration: its kind, its address, and the cadences given.</summary>
    /// <param name="endpoint">Its endpoint.</param>
    /// <param name="drillIntervalDays">Its drill cadence, if one is stated.</param>
    /// <param name="deepVerifyIntervalDays">Its sweep cadence, if one is stated.</param>
    protected abstract DestinationConfiguration DeclareStore(string endpoint, int? drillIntervalDays, int? deepVerifyIntervalDays);

    /// <summary>Stores the credential the way every client does: sealed to the service, where it was typed.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="secret">The secret to store; the one the store accepts when null.</param>
    protected abstract Task StoreCredentialAsync(ServiceRuntime runtime, string? secret = null);

    /// <summary>Asserts the listing names the destination's address, as the kind spells it.</summary>
    /// <param name="listed">The destination as the listing describes it.</param>
    protected abstract void AssertAddressListed(DestinationDescriptor listed);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        _timeout.Dispose();
        Harness.Dispose();
        GC.SuppressFinalize(this);
    }

    [TestMethod]
    public async Task Sync_ToTheStore_LandsTheArchiveUnderItsPrefix_InSyncAndProven()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);

        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("in-sync", cloud.State, cloud.Detail);
        Assert.AreEqual("proven", cloud.Verification, "read back off the store, never taken on the write's word");
        Assert.AreEqual("independent", cloud.FailureDomain);

        var archive = await runtime.ExistingArchiveAsync(Harness.DocsSetId, Timeout);
        var root = $"{Prefix}/{archive!.Repository.RepositoryId}/";
        var keys = Store.KeysIn(Namespace);
        Assert.IsNotEmpty(keys);
        Assert.IsTrue(keys.All(key => key.StartsWith(root, StringComparison.Ordinal)), string.Join(", ", keys));
        Assert.Contains(root + Repository.RepositoryLifecycle.DescriptorKey.Value, keys);

        var writes = Store.Requests.Where(request => request.Method == "PUT").ToList();
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
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);
        var writesBefore = Store.Requests.Count(request => request.Method == "PUT");

        await SyncAsync(runtime);

        Assert.AreEqual("in-sync", (await RowAsync(runtime, "cloud")).State);
        Assert.AreEqual(
            writesBefore, Store.Requests.Count(request => request.Method == "PUT"),
            "a store that already holds everything is sent nothing");
    }

    [TestMethod]
    public async Task Sync_WithNoCredentialStored_IsFailed_AndSaysHowToStoreOne()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await BackUpAsync(runtime);

        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("failed", cloud.State);
        Assert.Contains(MissingCredentialWords, cloud.Detail!, StringComparison.Ordinal);
        Assert.Contains("destination-credentials", cloud.Detail!, StringComparison.Ordinal);
        Assert.IsEmpty(Store.Requests, "nothing is sent to a store the service cannot sign for");
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

        WriteConfiguration(directShip: false, endpoint: EndpointAt(new Uri($"http://127.0.0.1:{closed}")));
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);

        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("unavailable", cloud.State, cloud.Detail);
    }

    [TestMethod]
    public async Task Sync_WithACredentialTheStoreRefuses_IsFailed_InTheStoresWords_AndNeverTheSecret()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime, RefusedSecret);
        await BackUpAsync(runtime);

        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("failed", cloud.State, "a refused credential needs a person, not a retry");
        Assert.Contains(RefusalCode, cloud.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain(RefusedSecret, cloud.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Probe_OfAStoreThatServes_IsViable_LeavesNothingThere_AndRecordsNoSuccess()
    {
        // FR-DEST-009: the store could take a backup, confirmed without
        // sending one, and reaching it is not syncing to it.
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var line = await ProbeAsync(runtime);

        Assert.Contains("the credential is taken", line, StringComparison.Ordinal);
        Assert.IsNotEmpty(Store.Listings, "the probe asked the store");
        Assert.IsEmpty(Store.KeysIn(Namespace), "a probe leaves nothing there");
        Assert.IsNull(runtime.DestinationSync.Find(Harness.DocsSetId, "cloud")?.LastSuccessAt);
    }

    [TestMethod]
    public async Task Probe_WithNoCredentialStored_IsFailed_AndSaysHowToStoreOne()
    {
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        var line = await ProbeAsync(runtime);

        Assert.Contains(MissingCredentialWords, line, StringComparison.Ordinal);
        Assert.Contains("destination-credentials", line, StringComparison.Ordinal);
        Assert.AreEqual(DestinationSyncState.Failed, Pair(runtime, "cloud").State);
        Assert.IsEmpty(Store.Requests, "nothing is sent to a store the service cannot sign for");
    }

    [TestMethod]
    public async Task Probe_OfAnEndpointNothingAnswers_IsUnavailable_NotFailed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closed = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        WriteConfiguration(directShip: false, endpoint: EndpointAt(new Uri($"http://127.0.0.1:{closed}")));

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var line = await ProbeAsync(runtime);

        Assert.Contains("could not reach", line, StringComparison.Ordinal);
        var record = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Unavailable, record.State, record.LastError);
    }

    [TestMethod]
    public async Task Probe_OfAStoreTooBusyToServe_IsUnavailable_NotFailed()
    {
        // The probe records what the sync would (ADR-0035): a store too busy
        // to serve is a gap that closes itself (FR-DEST-003, FR-QUOTA-001),
        // and nobody is sent looking for a fault to fix.
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        Store.FailFrom(servedFirst: 0, 503, BusyCode);
        var line = await ProbeAsync(runtime);

        Assert.Contains("did not serve the probe", line, StringComparison.Ordinal);
        Assert.Contains(BusyCode, line, StringComparison.Ordinal);
        var record = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Unavailable, record.State, record.LastError);
    }

    [TestMethod]
    public async Task Probe_WithACredentialTheStoreRefuses_IsFailed_InTheStoresWords_AndNeverTheSecret()
    {
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime, RefusedSecret);
        var line = await ProbeAsync(runtime);

        Assert.Contains(RefusalCode, line, StringComparison.Ordinal);
        Assert.DoesNotContain(RefusedSecret, line, StringComparison.Ordinal);
        var record = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Failed, record.State, "a refused credential needs a person, not a retry");
    }

    [TestMethod]
    public async Task Restore_AfterStagingIsLost_FindsTheReplicaUnderThePrefix_AndBringsTheFilesBack()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        string snapshotId;
        await using (var runtime = await StartAsync())
        {
            await StoreCredentialAsync(runtime);
            await BackUpAsync(runtime);
            Assert.AreEqual("in-sync", (await RowAsync(runtime, "cloud")).State);

            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var staging = await Harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout);
            snapshotId = Assert.ContainsSingle(staging.Snapshots).SnapshotId;
            await handler.ExecuteAsync(new CloseRestoreSourceCommand(staging.SourceId), Timeout);
        }

        // Nothing local maps the set to its repository any more: the replica
        // is found by listing what the prefix holds.
        Directory.Delete(Harness.ArchivesRoot, recursive: true);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await using (var runtime = await StartAsync())
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var replica = await Harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", "cloud", Timeout);
            Assert.AreEqual("cloud", replica.Location);
            Assert.AreEqual(snapshotId, Assert.ContainsSingle(replica.Snapshots).SnapshotId);

            var restored = Path.Combine(Harness.WorkPath, "from-cloud");
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
    public async Task DirectShip_TheRunLeavesTheStore_ToTheSyncThatFollowsIt()
    {
        // The run writes through the destinations it can ship to and leaves
        // the store behind rather than failed; the catch-up after it copies
        // what the run shipped, so the store is current when the pass ends.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);

        Assert.AreEqual("in-sync", (await RowAsync(runtime, "vault")).State);
        var cloud = await RowAsync(runtime, "cloud");
        Assert.AreEqual("in-sync", cloud.State, cloud.Detail);
        Assert.IsTrue(cloud.HoldsNewest, "the store holds the newest backup whole");
        Assert.IsNotEmpty(Store.KeysIn(Namespace));
    }

    [TestMethod]
    public async Task Drill_OnTheStore_BringsASampleBackFromIt()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<DrillResult>(
            await handler.ExecuteAsync(new RunDrillCommand("docs", "cloud"), Timeout), out var drilled);

        Assert.AreEqual(0, drilled.Failed, string.Join(" | ", drilled.Lines));
        Assert.AreEqual(0, drilled.NotDrilled, string.Join(" | ", drilled.Lines));
        var record = runtime.DestinationSync.Find(Harness.DocsSetId, "cloud");
        Assert.IsNotNull(record?.DrilledAt);
        Assert.IsNull(record.DrillFailure, record.DrillFailure);
    }

    [TestMethod]
    public async Task Drill_TheScheduleLeavesAStoreWithNoStatedCadence_Alone()
    {
        // A drill reads from the store, and every read is a request the
        // provider may charge for: a standing cost the person who declared
        // the store chooses, so it is never defaulted onto one the way it is
        // onto a local path. Absent means never.
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(Harness.DocsSetId, "cloud");
        Assert.IsNotNull(record?.LastSuccessAt, "the sync must have reached the store, or the case proves nothing");
        Assert.IsNull(record.DrilledAt, "a store with no stated cadence must not be drilled");
        Assert.IsNull(record.DrillFailure, "and must not be blamed for it either");
    }

    [TestMethod]
    public async Task Drill_AStoreWithAStatedCadence_IsDrilledByTheSchedule_ThenLeftAloneInsideIt()
    {
        WriteConfiguration(directShip: false, drillIntervalDays: 7);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(Harness.DocsSetId, "cloud");
        Assert.IsNotNull(record?.DrilledAt, $"a store with a stated cadence is due its first drill: error={record?.DrillFailure}");
        Assert.IsNull(record.DrillFailure, record.DrillFailure);
        Assert.IsGreaterThan(0, record.DrillFiles, "a drill that brought no file back from the store has proved nothing");

        var soon = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddDays(2), Timeout);
        await soon.Transfers.WaitAsync(Timeout);
        await soon.Drills.WaitAsync(Timeout);
        Assert.AreEqual(record.DrilledAt, runtime.DestinationSync.Find(Harness.DocsSetId, "cloud")!.DrilledAt);
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
        await StoreCredentialAsync(runtime);

        await PassAsync(runtime, DateTimeOffset.Now);
        Assert.IsNull(Pair(runtime, "cloud").SampleCursor, "the premise: one pass reads the whole first backup back");
        Assert.IsNull(Pair(runtime, "vault").SampleCursor, "the premise: one pass reads the whole first backup back");

        Harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await PassAsync(runtime, DateTimeOffset.Now.AddHours(2));

        var store = Pair(runtime, "cloud").SampleCursor;
        var local = Pair(runtime, "vault").SampleCursor;
        Assert.IsNotNull(store, "the second backup must hold more than one pass's rotation, or the case proves nothing");
        Assert.IsTrue(
            local is null || string.CompareOrdinal(store, local) < 0,
            $"the store's rotation must stop short of the local path's: store at {store}, local path at {local ?? "the end"}");
    }

    [TestMethod]
    public async Task Sweep_TheScheduleLeavesAStoreWithNoStatedCadence_Unread()
    {
        // Re-reading a whole replica is a request per object its provider may
        // charge for: a standing cost the person who declared the store
        // chooses, so absent means never, as for a peer (ADR-0091
        // Amendment 1).
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);

        var record = Pair(runtime, "cloud");
        Assert.IsNotNull(record.LastSuccessAt, "the sync must have reached the store, or the case proves nothing");
        Assert.IsNull(record.SweptAt, "a store with no stated cadence must not be swept");
    }

    [TestMethod]
    public async Task Sweep_AStoreWithAStatedCadence_ReadsTheWholeReplica_ThenLeavesItAloneInsideIt()
    {
        WriteConfiguration(directShip: false, deepVerifyIntervalDays: 30);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
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
    public async Task VerifyDestination_OnTheStore_ReadsEveryObjectWhenAPersonAsks()
    {
        // No cadence is stated: a person asking is consent for the reads, as
        // it is at a peer (ADR-0035 Amendment 2).
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
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
        await StoreCredentialAsync(runtime);
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
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);

        var archive = await runtime.ExistingArchiveAsync(Harness.DocsSetId, Timeout);
        var root = $"{Prefix}/{archive!.Repository.RepositoryId}/";
        var key = Store.KeysIn(Namespace).First(key => key.StartsWith(root + "blobs/data/", StringComparison.Ordinal));
        var sound = Store.ObjectIn(Namespace, key)!;
        var altered = (byte[])sound.Clone();
        altered[altered.Length / 2] ^= 0x5A;
        Store.Overwrite(Namespace, key, altered);

        var line = await VerifyAsync(runtime);
        Assert.Contains("1 damaged object(s)", line, StringComparison.Ordinal);
        Assert.Contains("each was replaced from a sound copy and re-verified", line, StringComparison.Ordinal);
        CollectionAssert.AreEqual(sound, Store.ObjectIn(Namespace, key), "the store holds what was sealed again");

        // Nothing this service writes alters an object at a store, so the
        // notice points at what can.
        var notice = runtime.Notices.Unacknowledged.Single(notice =>
            notice.Key.StartsWith("deep-verify-failed:", StringComparison.Ordinal)).Message;
        Assert.Contains(AlterationWords, notice, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task VerifyDestination_OfAStoreWithNoCredentialStored_SaysWhyNothingWasRead()
    {
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await BackUpAsync(runtime);

        var line = await VerifyAsync(runtime);
        Assert.Contains("its replica could not be read", line, StringComparison.Ordinal);
        Assert.Contains(MissingCredentialWords, line, StringComparison.Ordinal);
        Assert.IsEmpty(Store.Requests, "nothing is sent to a store the service cannot sign for");
    }

    [TestMethod]
    public async Task Sweep_OfAStoreThatStoppedAnswering_IsUnavailable_NotAStall()
    {
        // A store that does not answer is a gap that closes itself, as at a
        // sync (FR-DEST-003): recorded as unavailable, never counted as a
        // stall on a blob nothing was read from.
        var dying = StartAnotherStore();
        WriteConfiguration(directShip: false, endpoint: EndpointAt(dying.Origin), deepVerifyIntervalDays: 30);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
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
    public async Task Sweep_OfAStoreTooBusyToServe_IsUnavailable_NotAStall()
    {
        // A store that answers every attempt that it is busy is a gap that
        // closes itself just as one that does not answer is (FR-DEST-003,
        // FR-QUOTA-001): never a stall waited out under the back-off, as a
        // refusal is.
        WriteConfiguration(directShip: false, deepVerifyIntervalDays: 30);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);
        Assert.IsNotNull(Pair(runtime, "cloud").SweepCompletedAt, "the premise: the store was read while it served");

        Store.FailFrom(servedFirst: 0, 503, BusyCode);
        var set = Assert.ContainsSingle(runtime.Configuration.BackupSets);
        var segment = await ReplicaSweepJob.SweepAsync(
            runtime, set, "cloud", (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), userInitiated: false, Timeout);

        Assert.IsNotNull(segment.Unreadable, "nothing was read");
        var record = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Unavailable, record.State, record.LastError);
        Assert.Contains(BusyCode, record.LastError!, StringComparison.Ordinal);
        Assert.AreEqual(0, record.SweepStalls, "a store too busy to serve has stalled on nothing");
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
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);

        // A circuit under way, whose next segment is due at once, on a pair
        // the last sync found unreachable.
        var now = DateTimeOffset.Now;
        var nowMs = (ulong)now.ToUnixTimeMilliseconds();
        runtime.DestinationSync.RecordSweep(
            Harness.DocsSetId, "cloud", cursor: "blobs/", examined: 1, completedCircuit: false, nowMs);
        runtime.DestinationSync.RecordFailure(
            Harness.DocsSetId, "cloud", DestinationSyncState.Unavailable, "not answering", nowMs);

        var asked = Store.Requests.Count;
        var pass = await Scheduler.RunPassAsync(runtime, now.AddMinutes(1), Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        Assert.AreEqual(asked, Store.Requests.Count, "nothing reads a store last found unreachable for its sweep");
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
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);
        Assert.IsNotNull(Pair(runtime, "cloud").SweepCompletedAt, "the premise: the store was read while it took the credential");

        await StoreCredentialAsync(runtime, RefusedSecret);
        var due = DateTimeOffset.Now.AddDays(31);
        var pass = await Scheduler.RunPassAsync(runtime, due, Timeout);
        await pass.Transfers.WaitAsync(Timeout);

        var refused = Pair(runtime, "cloud");
        Assert.AreEqual(1, refused.SweepStalls, $"the refusal is counted as a stall: {refused.LastError}");
        Assert.IsNull(refused.SweepStalledOn, "nothing was read, so no blob is named");

        var asked = Store.Requests.Count;
        var next = await Scheduler.RunPassAsync(runtime, due.AddMinutes(1), Timeout);
        await next.Transfers.WaitAsync(Timeout);
        Assert.AreEqual(asked, Store.Requests.Count, "inside the back-off nothing asks the store again");
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
        await StoreCredentialAsync(runtime);
        await BackUpAsync(runtime);

        var archive = await runtime.ExistingArchiveAsync(Harness.DocsSetId, Timeout);
        var root = $"{Prefix}/{archive!.Repository.RepositoryId}/";
        foreach (var key in Store.KeysIn(Namespace).Where(key => key.StartsWith(root, StringComparison.Ordinal)))
        {
            Store.Remove(Namespace, key);
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
    public async Task SetDestinationCredentials_HoldsTheCredentialOwnerOnly_AndNothingTheServiceSaysCarriesIt()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false, withVault: true);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<DestinationsResult>(
            await handler.ExecuteAsync(new ListDestinationsCommand(), Timeout), out var before);
        Assert.IsFalse(before.Destinations.Single(destination => destination.Name == "cloud").AccessKeyStored);

        await StoreCredentialAsync(runtime);

        Assert.IsInstanceOfType<DestinationsResult>(
            await handler.ExecuteAsync(new ListDestinationsCommand(), Timeout), out var after);
        var cloud = after.Destinations.Single(destination => destination.Name == "cloud");
        Assert.IsTrue(cloud.AccessKeyStored);
        Assert.AreEqual(Prefix, cloud.Prefix);
        AssertAddressListed(cloud);
        Assert.IsNull(
            after.Destinations.Single(destination => destination.Name == "vault").AccessKeyStored,
            "a kind that signs nothing holds no credential, and is not said to lack one");

        var held = Path.Combine(Harness.StateDirectory, "destination-credentials", $"{CloudId}.json");
        Assert.IsTrue(File.Exists(held), "held in the service's own state directory, by the destination's id");
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(held));
        }

        Assert.DoesNotContain(Secret, JsonSerializer.Serialize<ServiceResult>(after, FrameCodec.SerializerOptions), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, File.ReadAllText(Path.Combine(Harness.StateDirectory, "config.json")), StringComparison.Ordinal);
        Assert.IsInstanceOfType<ConfigurationResult>(
            await handler.ExecuteAsync(new ExportConfigurationCommand(), Timeout), out var exported);
        Assert.DoesNotContain(Secret, exported.Json, StringComparison.Ordinal);

        Assert.IsInstanceOfType<DiagnosticBundleResult>(
            await handler.ExecuteAsync(new ExportDiagnosticsCommand(IncludePaths: true), Timeout), out var bundle);
        BundleInspection.AssertNowhere(
            "the credential", Secret, bundle.FileName,
            BundleInspection.Open(Convert.FromBase64String(bundle.ContentBase64)));
    }

    [TestMethod]
    public async Task DeleteDestination_ForgetsItsCredential()
    {
        WriteConfiguration(directShip: false, referenced: false);

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var held = Path.Combine(Harness.StateDirectory, "destination-credentials", $"{CloudId}.json");
        Assert.IsTrue(File.Exists(held));

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(new DeleteDestinationCommand("cloud"), Timeout), out var deleted);

        Assert.IsFalse(File.Exists(held), "a credential for a destination nobody declares is one nobody needs");
        Assert.IsTrue(
            deleted.Lines.Any(line => line.Contains(CredentialNoun, StringComparison.Ordinal)),
            string.Join(" | ", deleted.Lines));
    }

    [TestMethod]
    public async Task Sync_WhileTheStoreKeepsAskingToSlowDown_IsUnavailable_AndTheNextSyncFinishesIt()
    {
        // A store throttling every attempt of a request is still a store
        // asking to be asked later: a gap that closes itself (FR-DEST-003),
        // told apart from a refusal a person must act on (FR-QUOTA-001).
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        Store.FailFrom(servedFirst: 0, 503, BusyCode);
        await BackUpAsync(runtime);

        var busy = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Unavailable, busy.State, busy.LastError);
        Assert.Contains(BusyCode, busy.LastError!, StringComparison.Ordinal);

        Store.Recover();
        await SyncAsync(runtime);
        var synced = await RowAsync(runtime, "cloud");
        Assert.AreEqual("in-sync", synced.State, synced.Detail);
    }

    [TestMethod]
    public async Task Sync_CutOffPartway_IsUnavailable_AndTheNextSyncFinishesTheReplica()
    {
        // A link that goes down in the middle of a copy leaves the objects
        // already put, each one whole, because a put lands whole or not at
        // all; the next sync puts the rest and the replica proves (Phase 3's
        // "large interrupted uploads resume or safely restart").
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        Store.DropFrom(servedFirst: 2, method: "PUT");
        await BackUpAsync(runtime);

        var cut = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Unavailable, cut.State, cut.LastError);
        Assert.HasCount(2, Store.Requests.Where(Landed), "the premise: two objects were put before the link went down");

        Store.Recover();
        await SyncAsync(runtime);
        var synced = await RowAsync(runtime, "cloud");
        Assert.AreEqual("in-sync", synced.State, synced.Detail);
        Assert.AreEqual("proven", synced.Verification);
    }

    [TestMethod]
    public async Task Sync_TheCredentialRefusedPartway_FailsNamingTheRefusal_AndTheNextSyncFinishesIt()
    {
        // A credential the store stops taking in the middle of a sync, as one
        // revoked or lapsed then would be: what was put stays, whole, and the
        // refusal is a failure, not a gap, because only a person can store a
        // credential the store takes (ADR-0012's credential expiry
        // mid-operation).
        WriteConfiguration(directShip: false);
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        Store.FailFrom(servedFirst: 2, 403, RefusalCode, method: "PUT");
        await BackUpAsync(runtime);

        var refused = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Failed, refused.State, refused.LastError);
        Assert.Contains(RefusalCode, refused.LastError!, StringComparison.Ordinal);
        Assert.HasCount(2, Store.Requests.Where(Landed), "the premise: two objects were put before the refusals began");

        Store.Recover();
        await SyncAsync(runtime);
        var synced = await RowAsync(runtime, "cloud");
        Assert.AreEqual("in-sync", synced.State, synced.Detail);
    }

    [TestMethod]
    public async Task Sync_WhileTheStoresListingsLagItsWrites_DeletesNothingAKeptSnapshotNeeds()
    {
        // A store whose listings lag its writes (ADR-0012's eventual
        // visibility). The sync deletes only what the store's listing shows
        // and the staging archive's keep-set rejects, so an object the
        // listing hides is offered again rather than judged absent. Hidden
        // here: the snapshots alone, the one shape in which a keep-set read
        // off the store's own listing would take a new snapshot's blobs for
        // garbage.
        WriteConfiguration(directShip: false, retention: new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 });
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var now = DateTimeOffset.Now;
        await BackUpAsync(runtime, now);

        var archive = await runtime.ExistingArchiveAsync(Harness.DocsSetId, Timeout);
        var snapshots = $"{Prefix}/{archive!.Repository.RepositoryId}/snapshots/";
        var before = Store.KeysIn(Namespace).Count(key => key.StartsWith(snapshots, StringComparison.Ordinal));
        Store.LagListings(under: snapshots);

        Harness.WriteSourceFile("docs/c.txt", "written after the first backup, so held by the second snapshot alone");
        var mark = Store.Requests.Count;
        await BackUpAsync(runtime, now.AddHours(1));

        var lagged = Pair(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.InSync, lagged.State, lagged.LastError);
        Assert.IsTrue(
            Store.Requests.Skip(mark).Any(request => request.Method == "DELETE"),
            "the premise: the policy deleted at the store while its listing lagged");
        Assert.IsTrue(
            Store.KeysIn(Namespace).Count(key => key.StartsWith(snapshots, StringComparison.Ordinal)) >= before,
            "the premise: the second snapshot reached the store, where no listing showed it");

        // Caught up, the store is read back whole: a blob the second snapshot
        // names that the sync had deleted would be found missing here.
        Store.CatchUpListings();
        var line = await VerifyAsync(runtime);
        Assert.Contains("object(s) confirmed", line, StringComparison.Ordinal);
        Assert.DoesNotContain("damaged", line, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sweep_AWholeCircuit_ListsFromItsCursor_NotTheWholeStoreForEachSegment()
    {
        // A segment reads a few blobs. One that listed every blob to find
        // where it resumes would make a circuit over n blobs ask for n
        // listings of n entries, and at a store each page of a listing is a
        // request its owner pays for: the cost would grow with the square of
        // the archive.
        ReplicaSweepJob.SegmentBudget = 1;
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var now = DateTimeOffset.Now;
        for (var backup = 0; backup < 5; backup++)
        {
            Harness.WriteSourceFile($"docs/part-{backup}.bin", new string((char)('k' + backup), 20_000 + backup));
            await BackUpAsync(runtime, now.AddHours(backup));
        }

        var blobs = Store.KeysIn(Namespace).Count(key => key.Contains("/blobs/", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(5, blobs, "the premise: a replica of several blobs, each a segment");

        // Pages of three: room for a segment's one blob, the key past it that
        // says the circuit goes on, and the key a store that starts a listing
        // at the cursor itself answers with.
        Store.ListPageLimit = 3;
        var mark = Store.Listings.Count;
        var set = Assert.ContainsSingle(runtime.Configuration.BackupSets);
        var circuit = await ReplicaSweepJob.RunFullAsync(
            runtime, set, "cloud", (ulong)now.ToUnixTimeMilliseconds(), Timeout);
        Assert.AreEqual(blobs, circuit.Examined, "the circuit read every blob");

        // A segment per blob and one that finds the circuit closed. Each
        // looks for the replica (a page) and lists from its cursor (a page):
        // the cost grows with the segments, not with segments times blobs.
        var listings = Store.Listings.Count - mark;
        Assert.IsLessThanOrEqualTo(2 * (blobs + 1), listings, $"{listings} listing page(s) for {blobs} blob(s)");
    }

    /// <summary>Writes the set's two source files.</summary>
    protected void WriteFiles()
    {
        Harness.WriteSourceFile("docs/a.txt", "alpha");
        Harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));
    }

    /// <summary>Runs a pass that backs the set up and waits for its transfers.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="at">The pass clock; now when not given.</param>
    protected async Task BackUpAsync(ServiceRuntime runtime, DateTimeOffset? at = null)
    {
        var pass = await Scheduler.RunPassAsync(runtime, at ?? DateTimeOffset.Now, Timeout);
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

    /// <summary>Whether a request was a put the store took.</summary>
    /// <param name="request">The request as the store answered it.</param>
    private static bool Landed(ObjectStoreTestServer.RecordedRequest request) =>
        request.Method == "PUT" && request.Status is >= 200 and < 300;

    /// <summary>The ledger's row for the set and the named destination.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="name">The destination.</param>
    protected DestinationSyncRecord Pair(ServiceRuntime runtime, string name) =>
        runtime.DestinationSync.Find(Harness.DocsSetId, name)
        ?? throw new AssertFailedException($"no ledger row for '{name}'");

    /// <summary>Reads the store's replica back whole, as <c>verify-destination --full</c> does, and returns what it said.</summary>
    /// <param name="runtime">The service.</param>
    protected async Task<string> VerifyAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<VerifyDestinationResult>(
            await handler.ExecuteAsync(new VerifyDestinationCommand("docs", "cloud", Full: true), Timeout),
            out var verified);
        return Assert.ContainsSingle(verified.Lines);
    }

    /// <summary>Probes the store, as <c>verify-destination --probe</c> does, and returns what it said.</summary>
    /// <param name="runtime">The service.</param>
    private async Task<string> ProbeAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<VerifyDestinationResult>(
            await handler.ExecuteAsync(new VerifyDestinationCommand("docs", "cloud", Full: false, Probe: true), Timeout),
            out var probed);
        return Assert.ContainsSingle(probed.Lines);
    }

    /// <summary>Syncs the set to the store at a person's asking.</summary>
    /// <param name="runtime">The service.</param>
    protected async Task SyncAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<SyncResult>(await handler.ExecuteAsync(new SyncCommand("docs", "cloud"), Timeout));
    }

    /// <summary>The service's published recipient key, which every client seals a credential to.</summary>
    /// <param name="handler">The service's command handler.</param>
    protected static async Task<byte[]> RecipientAsync(ServiceCommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), CancellationToken.None), out var description);
        return Convert.FromHexString(description.RestoreGrantRecipient!);
    }

    /// <summary>The set's status row for the named destination.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="name">The destination.</param>
    protected async Task<DestinationStatusDescriptor> RowAsync(ServiceRuntime runtime, string name)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        return Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations.Where(row => row.Name == name));
    }

    /// <summary>Writes a configuration with one set referencing the store, and a local path beside it where asked.</summary>
    /// <param name="directShip">Whether the set ships directly.</param>
    /// <param name="endpoint">The store's endpoint, when not the suite's own store's.</param>
    /// <param name="withVault">Whether the set copies to a local path as well.</param>
    /// <param name="referenced">Whether the set references the store at all.</param>
    /// <param name="drillIntervalDays">The store's drill cadence, if one is stated.</param>
    /// <param name="deepVerifyIntervalDays">The store's sweep cadence, if one is stated.</param>
    /// <param name="retention">The retention policy the set keeps at the store, if any.</param>
    protected void WriteConfiguration(
        bool directShip,
        string? endpoint = null,
        bool withVault = false,
        bool referenced = true,
        int? drillIntervalDays = null,
        int? deepVerifyIntervalDays = null,
        RetentionConfiguration? retention = null)
    {
        List<DestinationConfiguration> destinations =
        [
            DeclareStore(endpoint ?? EndpointAt(Store.Origin), drillIntervalDays, deepVerifyIntervalDays),
        ];
        List<SetDestinationReference> references =
            referenced ? [new SetDestinationReference { Ref = "cloud", Retention = retention }] : [];

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
                    Id = Harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = Harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = references,
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(Harness.StateDirectory, "config.json"));
    }

    /// <summary>Starts the service over the harness.</summary>
    protected async Task<ServiceRuntime> StartAsync()
    {
        await Harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = Harness.ArchivesRoot,
                StateDirectory = Harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }
}

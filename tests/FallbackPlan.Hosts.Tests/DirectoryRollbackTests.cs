using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The destination as the rollback witness
/// ([ADR-0062](../../docs/adr/0062-the-destination-is-the-rollback-witness.md);
/// FR-DEST-018, NFR-SEC-005): a whole state directory restored from an older
/// copy rolls the catalogue, the sequence file, the sync ledger and a
/// direct-ship set's metadata store back together, so nothing local can
/// notice — but the destination still holds the newer history, and the next
/// fan-out pass asks it. Does not establish FR-DRL-001: nothing here is a
/// recovery of content; it is the machine's own allocation state being put
/// right from what it published.
/// </summary>
/// <remarks>
/// <para>
/// The witness slice 3.2 built (<see cref="ObservedHeadAdoptionTests"/>) asks
/// the set's own archive at open. For a direct-ship set that archive's
/// metadata plane IS the state directory, so a rollback of the whole
/// directory takes the witness with it: the archive attests exactly what the
/// rolled-back sequence file says. The one thing that did not roll back is
/// the destination.
/// </para>
/// <para>
/// The harm, without this: the pass converges the destination down to the
/// keep-set its rolled-back metadata can see, deleting the newest backup
/// from the only place that holds it; and the next backup hands out sequence
/// numbers the destination already holds.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class DirectoryRollbackTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");
    private string OlderState => Path.Combine(_harness.WorkPath, "older-state");
    private string OlderArchives => Path.Combine(_harness.WorkPath, "older-archives");
    private string NoticeKey => $"destination-ahead:{_harness.DocsSetId}:vault";

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ARolledBackStateDirectory_IsNoticedFromTheDestination_AndNothingThereIsDeleted()
    {
        // Under KeepDaily=1 the second sync already converged the destination
        // to the newest snapshot: what the destination holds is the second
        // backup and nothing else — and the rolled-back state directory has
        // never heard of it.
        var replica = await TwoBackupsThenRollBackAsync(directShip: true);
        var before = ReplicaKeys(replica);
        Assert.ContainsSingle(SnapshotObjects(replica));
        var sequenceBefore = await SequenceFileAsync();

        await using var runtime = await StartAsync();
        await SyncAsync(runtime);

        // Said: the destination attests a writer sequence the state directory
        // has never heard of, and that is a rollback of the state directory,
        // not a fault at the destination.
        var notice = Assert.ContainsSingle(runtime.Notices.Unacknowledged.Where(n => n.Key == NoticeKey));
        Assert.Contains("restored from an older copy", notice.Message, StringComparison.Ordinal);

        // Protected: KeepDaily=1 would have converged the destination down to
        // the one snapshot the rolled-back metadata can see, deleting the
        // newest backup from the only place that holds it. Every object that
        // was there is still there.
        var after = ReplicaKeys(replica);
        Assert.IsEmpty(before.Except(after, StringComparer.Ordinal), "the detecting pass must delete nothing at the destination");

        // And the writer moved past what the destination holds, before
        // anything else happened: the allocation state is put right first.
        Assert.AreNotEqual(sequenceBefore, await SequenceFileAsync(), "the sequence file must move past the destination's head");

        // Healed: every piece of metadata the destination holds is back in
        // the set's metadata store, the catalogue lists both backups again,
        // and the notice says so.
        Assert.Contains("copied back", notice.Message, StringComparison.Ordinal);
        var metadata = Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId);
        foreach (var key in after.Where(IsMetadataKey))
        {
            Assert.IsTrue(File.Exists(Path.Combine(metadata, key)), $"{key} was not copied back from the destination");
        }

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        Assert.HasCount(2, listed.Snapshots);

        // The proof the heal was the right shape: a third backup publishes
        // rather than colliding with its own history, and is listed with it.
        _harness.WriteSourceFile("docs/a.txt", "third content");
        await BackUpAsync(runtime);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var relisted);
        Assert.HasCount(3, relisted.Snapshots);
    }

    [TestMethod]
    public async Task AFailedCopyBack_LeavesTheDestinationUntouched_AndTheNextPassHealsAgain()
    {
        // The metadata store made unwritable where the copy would land: the
        // heal fails, and what must hold is that the destination is not
        // touched, the destination is not recorded as synced, the notice
        // says why — and the next pass, the obstacle gone, heals without
        // being asked. The sequence moved on the first pass and will never
        // detect again, so the retry must not depend on it.
        var replica = await TwoBackupsThenRollBackAsync(directShip: true);
        var before = ReplicaKeys(replica);
        var journal = Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId, "journal");
        var writerDirectory = Assert.ContainsSingle(Directory.GetDirectories(journal));
        Directory.Delete(writerDirectory, recursive: true);
        await File.WriteAllTextAsync(writerDirectory, "not a directory", Timeout);

        await using var runtime = await StartAsync();
        await SyncAsync(runtime);

        var notice = Assert.ContainsSingle(runtime.Notices.Unacknowledged.Where(n => n.Key == NoticeKey));
        Assert.Contains("could not be copied back", notice.Message, StringComparison.Ordinal);
        Assert.IsEmpty(before.Except(ReplicaKeys(replica), StringComparer.Ordinal));
        var row = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.AreEqual(DestinationSyncState.Failed, row!.State);
        Assert.Contains("could not be copied back", row.LastError ?? "", StringComparison.Ordinal);

        File.Delete(writerDirectory);
        await SyncAsync(runtime);

        Assert.AreEqual(DestinationSyncState.InSync, runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!.State);
        foreach (var key in before.Where(IsMetadataKey))
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId, key)),
                $"{key} was not copied back on the retry");
        }

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        Assert.HasCount(2, listed.Snapshots);
    }

    private static bool IsMetadataKey(string key) =>
        !key.StartsWith("blobs/", StringComparison.Ordinal)
        && !key.StartsWith("tombstones/", StringComparison.Ordinal)
        && !key.StartsWith("leases/", StringComparison.Ordinal);

    [TestMethod]
    public async Task TheCurrentStateDirectory_RaisesNothing()
    {
        // The cry-wolf guard: a writer is ordinarily AHEAD of what any
        // destination holds — numbers are allocated before the objects
        // accounting for them ship — so an ordinary pass must never read as
        // a rollback.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/a.txt", "day one content");

        await using var runtime = await StartAsync();
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/a.txt", "second content");
        await BackUpAsync(runtime);
        await SyncAsync(runtime);

        Assert.IsEmpty(runtime.Notices.Unacknowledged.Where(n => n.Key.StartsWith("destination-ahead:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AStagingSet_RolledBackWithItsArchive_IsProtectedAndSaysSo()
    {
        // A staging set's archive lives outside the state directory, so a
        // rollback of the state directory alone is the case slice 3.2 already
        // handles at open. A machine restored whole — state directory and
        // archives root together — is the case only the destination can
        // witness, and the pass protects it the same way; it does not heal a
        // staging set, because the newer history it would copy back is
        // content, not metadata, and the notice says where it is.
        // A staging replica holds its keep-set — the newest snapshot alone
        // under KeepDaily=1 — so what must survive is what is there, not a
        // count: the second backup, which only the destination now holds.
        var replica = await TwoBackupsThenRollBackAsync(directShip: false);
        var before = ReplicaKeys(replica);
        Assert.ContainsSingle(SnapshotObjects(replica));

        await using var runtime = await StartAsync();
        await SyncAsync(runtime);

        var notice = Assert.ContainsSingle(runtime.Notices.Unacknowledged.Where(n => n.Key == NoticeKey));
        Assert.Contains("staging", notice.Message, StringComparison.Ordinal);
        Assert.IsEmpty(before.Except(ReplicaKeys(replica), StringComparer.Ordinal), "the detecting pass must delete nothing at the destination");
    }

    /// <summary>
    /// Two backups to the vault under a keep-newest policy, the state
    /// directory (and, for a staging set, the archives root) copied aside
    /// between them and put back afterwards: the shape of a machine restored
    /// from an image taken after its first backup.
    /// </summary>
    /// <returns>The replica directory at the vault.</returns>
    private async Task<string> TwoBackupsThenRollBackAsync(bool directShip)
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip);
        _harness.WriteSourceFile("docs/a.txt", "day one content");

        await using (var runtime = await StartAsync())
        {
            await BackUpAsync(runtime);
            await SyncAsync(runtime);
        }

        CopyDirectory(_harness.StateDirectory, OlderState);
        if (!directShip)
        {
            CopyDirectory(_harness.ArchivesRoot, OlderArchives);
        }

        await using (var runtime = await StartAsync())
        {
            _harness.WriteSourceFile("docs/a.txt", "second content");
            await BackUpAsync(runtime);
            await SyncAsync(runtime);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_harness.StateDirectory, recursive: true);
        CopyDirectory(OlderState, _harness.StateDirectory);
        if (!directShip)
        {
            Directory.Delete(_harness.ArchivesRoot, recursive: true);
            CopyDirectory(OlderArchives, _harness.ArchivesRoot);
        }

        return Assert.ContainsSingle(Directory.GetDirectories(Vault));
    }

    // One clock for every run and every sync, advancing five minutes per
    // operation: a sync stamped earlier than the backup before it reads to
    // the sink as a destination that missed a run.
    private DateTimeOffset _clock = DateTimeOffset.Now;

    private DateTimeOffset Tick() => _clock = _clock.AddMinutes(5);

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, Tick(), userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var queued = FanOut.EnqueueAll(
            runtime, runtime.Configuration.BackupSets.Single(), Tick(), userInitiated: true);
        await Task.WhenAll(queued).WaitAsync(Timeout);
    }

    private async Task<string> SequenceFileAsync()
    {
        var path = Assert.ContainsSingle(Directory.GetFiles(_harness.StateDirectory, "sequence-*.txt"));
        return await File.ReadAllTextAsync(path, Timeout);
    }

    private static List<string> SnapshotObjects(string replica) =>
        [.. Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories)];

    private static List<string> ReplicaKeys(string replica) =>
        [.. Directory.GetFiles(replica, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(replica, path))
            .Where(relative => !relative.StartsWith(".fbp-tmp", StringComparison.Ordinal))];

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private void WriteConfiguration(bool directShip) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 4h",
                Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                Destinations = [new SetDestinationReference { Ref = "vault" }],
                DirectShip = directShip,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            Timeout);
    }
}

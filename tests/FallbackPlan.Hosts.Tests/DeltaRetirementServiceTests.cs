using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service retires the deltas a set's own plane holds in checkpoints
/// (FR-MAN-017; specification 07 §7). A backup run, once its snapshot and
/// checkpoint are published, deletes from the set's staging archive (or a
/// direct-ship set's metadata store) each delta a checkpoint there carries
/// whose writer has published since. The run never waits for the set: while
/// a sync, a sweep or retention holds it, the run retires nothing and a later
/// run does. Nothing that copies a set's metadata from one store to another
/// is undone by a delta retired under it, and retiring a staging archive
/// takes a delta the metadata store retired for the delta it is.
/// </summary>
/// <remarks>The threshold is set low through the service's options; the default is sixty-four deltas.</remarks>
[TestClass]
public sealed class DeltaRetirementServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string StagingPath => Path.Combine(_harness.ArchivesRoot, _harness.DocsSetId);

    private string MetadataRoot => Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId);

    // A local destination holds each repository it receives under its own directory.
    private string Replica => Assert.ContainsSingle(Directory.GetDirectories(Vault));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task AStagingSet_RetiresTheDeltasASettledCheckpointCarries_AndLoadsAllItHeld()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync(checkpointThreshold: 2);
        var set = runtime.Configuration.BackupSets.Single();
        await BackUpAsync(runtime, set, "first");
        await BackUpAsync(runtime, set, "second");

        var before = await LoadAsync(StagingPath);
        var carried = CarriedBy(before);
        Assert.IsNotEmpty(carried, "The second run checkpoints the first two runs' deltas.");
        Assert.IsTrue(
            carried.IsSubsetOf(await DeltasAtTheirKeysAsync(StagingPath)),
            "A checkpoint published by this run is not settled until its writer publishes again.");

        await BackUpAsync(runtime, set, "third");

        Assert.IsFalse((await DeltasAtTheirKeysAsync(StagingPath)).Overlaps(carried));
        var after = await LoadAsync(StagingPath);
        Assert.IsTrue(before.AllEntries.ToHashSet().IsSubsetOf(after.AllEntries));
    }

    [TestMethod]
    public async Task ADirectShipSet_RetiresInItsMetadataStore()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);

        await using var runtime = await StartAsync(checkpointThreshold: 2);
        var set = runtime.Configuration.BackupSets.Single();
        await BackUpAsync(runtime, set, "first");
        await BackUpAsync(runtime, set, "second");
        var before = await LoadAsync(MetadataRoot);
        var carried = CarriedBy(before);
        Assert.IsNotEmpty(carried);

        await BackUpAsync(runtime, set, "third");

        Assert.IsFalse((await DeltasAtTheirKeysAsync(MetadataRoot)).Overlaps(carried));
        Assert.IsTrue(before.AllEntries.ToHashSet().IsSubsetOf((await LoadAsync(MetadataRoot)).AllEntries));
    }

    [TestMethod]
    public async Task WhileTheSetIsBusy_ARunRetiresNothing_AndALaterRunDoes()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync(checkpointThreshold: 2);
        var set = runtime.Configuration.BackupSets.Single();
        await BackUpAsync(runtime, set, "first");
        await BackUpAsync(runtime, set, "second");
        var carried = CarriedBy(await LoadAsync(StagingPath));

        // A sync, a sweep or retention apply holds the set while it reads or
        // trims the staging archive. The backup does not wait for it.
        var gate = runtime.SetGate(set.Id);
        await gate.WaitAsync(Timeout);
        try
        {
            await BackUpAsync(runtime, set, "third");
        }
        finally
        {
            gate.Release();
        }

        Assert.IsTrue(carried.IsSubsetOf(await DeltasAtTheirKeysAsync(StagingPath)));

        await BackUpAsync(runtime, set, "fourth");

        Assert.IsFalse((await DeltasAtTheirKeysAsync(StagingPath)).Overlaps(carried));
    }

    [TestMethod]
    public async Task RetireStaging_ADeltaTheMetadataStoreRetired_IsNotTakenForOneThatNeverArrived()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);

        // ---- The staging era, below the threshold: one run fanned out, and a
        // second whose delta only staging holds.
        string historySnapshot;
        await using (var runtime = await StartAsync(checkpointThreshold: null))
        {
            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
            Assert.AreEqual(1, pass.Ran);
            await pass.Transfers.WaitAsync(Timeout);

            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var first = await SnapshotIdsAsync(handler);
            await BackUpAsync(runtime, runtime.Configuration.BackupSets.Single(), "second-era");
            historySnapshot = Assert.ContainsSingle((await SnapshotIdsAsync(handler)).Except(first));
        }

        // ---- The flip. The first direct-ship run migrates staging's metadata
        // and checkpoints every delta; the next settles that checkpoint, and
        // the metadata store retires them all, the staging-only one among
        // them.
        WriteConfiguration(directShip: true);
        await using (var runtime = await StartAsync(checkpointThreshold: 2))
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var set = runtime.Configuration.BackupSets.Single();
            await BackUpAsync(runtime, set, "third");
            var carried = CarriedBy(await LoadAsync(MetadataRoot));
            await BackUpAsync(runtime, set, "fourth");
            Assert.IsFalse((await DeltasAtTheirKeysAsync(MetadataRoot)).Overlaps(carried));

            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddMinutes(2), Timeout);
            await pass.Transfers.WaitAsync(Timeout);

            Assert.IsInstanceOfType<ConfigurationChangeResult>(
                await handler.ExecuteAsync(new RetireStagingCommand("docs"), Timeout), out var retired);
            Assert.IsTrue(retired.Lines.Any(line => line.Contains("retired", StringComparison.Ordinal)));
            Assert.IsFalse(Directory.Exists(StagingPath));

            // The history whose delta only staging held restores from the
            // destination alone: its entries are in the checkpoint there.
            var output = Path.Combine(_harness.WorkPath, "restored");
            var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout);
            Assert.IsInstanceOfType<Api.RestoreResult>(
                await handler.ExecuteAsync(new RunRestoreCommand(historySnapshot, null, output, Source: source.SourceId), Timeout),
                out var restored);
            Assert.AreEqual("complete", restored.Outcome);
            var recovered = Assert.ContainsSingle(Directory.GetFiles(output, "second-era.txt", SearchOption.AllDirectories));
            Assert.Contains("second-era", await File.ReadAllTextAsync(recovered, Timeout), StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task RetireStaging_ADeltaOnlyStagingHolds_ThatNoCheckpointThereCarries_HoldsTheArchive()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        await using (var runtime = await StartAsync(checkpointThreshold: null))
        {
            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
            Assert.AreEqual(1, pass.Ran);
            await pass.Transfers.WaitAsync(Timeout);
            await BackUpAsync(runtime, runtime.Configuration.BackupSets.Single(), "second-era");
        }

        var stagingOnly = Assert.ContainsSingle(
            (await DeltasAtTheirKeysAsync(StagingPath)).Except(await DeltasAtTheirKeysAsync(Replica)));

        WriteConfiguration(directShip: true);
        await using (var runtime = await StartAsync(checkpointThreshold: null))
        {
            // The first open after the flip migrates; then the migrated copy
            // goes, as no completed migration loses it, and nothing at the
            // metadata store carries it.
            Assert.IsNotNull(await runtime.ExistingArchiveAsync(runtime.Configuration.BackupSets.Single().Id, Timeout));
            var metadata = new LocalFileSystemObjectStore(MetadataRoot);
            var deleted = 0;
            await foreach (var entry in metadata.ListAsync(ObjectPrefix.Parse("index/delta/"), ListOptions.Default, Timeout))
            {
                if (MetadataStoreKeys.TryParseIndexDelta(entry.Key, out var deltaId) && deltaId == stagingOnly)
                {
                    Assert.AreEqual(
                        DeleteOutcome.Deleted, (await metadata.DeleteAsync(entry.Key, DeleteConditions.None, Timeout)).Outcome);
                    deleted++;
                }
            }

            Assert.AreEqual(1, deleted, "the flip's migration did not carry the staging-only delta");
            Assert.IsEmpty(CarriedBy(await LoadAsync(MetadataRoot)));

            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            Assert.IsInstanceOfType<ServiceError>(
                await handler.ExecuteAsync(new RetireStagingCommand("docs"), Timeout), out var refused);
            Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
            Assert.Contains("index/delta/", refused.Message, StringComparison.Ordinal);
            Assert.IsTrue(Directory.Exists(StagingPath), "a refused retirement must not touch the archive");
        }
    }

    [TestMethod]
    public async Task ALocalPathDestination_RetiresInItsSync_WhatThePlaneRetired_AndRestoresFromItAlone()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync(checkpointThreshold: 2);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var set = runtime.Configuration.BackupSets.Single();
        await BackUpAndSyncAsync(runtime, set, "first");
        var firstSnapshot = Assert.ContainsSingle(await SnapshotIdsAsync(handler));
        await BackUpAndSyncAsync(runtime, set, "second");
        var before = await LoadAsync(Replica);
        var carried = CarriedBy(before);
        Assert.IsTrue(carried.IsSubsetOf(await DeltasAtTheirKeysAsync(Replica)));

        await BackUpAndSyncAsync(runtime, set, "third");

        Assert.IsFalse((await DeltasAtTheirKeysAsync(StagingPath)).Overlaps(carried));
        Assert.IsFalse(
            (await DeltasAtTheirKeysAsync(Replica)).Overlaps(carried),
            "The sync retires at the destination what the plane retired.");
        Assert.AreEqual(DestinationSyncState.InSync, runtime.DestinationSync.Find(set.Id, "vault")!.State);
        Assert.IsTrue(before.AllEntries.ToHashSet().IsSubsetOf((await LoadAsync(Replica)).AllEntries));

        // The first snapshot's delta is gone from both; the destination
        // restores it from the checkpoint it holds.
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", "vault", Timeout);
        var output = Path.Combine(_harness.WorkPath, "restored");
        Assert.IsInstanceOfType<Api.RestoreResult>(
            await handler.ExecuteAsync(new RunRestoreCommand(firstSnapshot, null, output, Source: source.SourceId), Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        var recovered = Assert.ContainsSingle(Directory.GetFiles(output, "first.txt", SearchOption.AllDirectories));
        Assert.Contains("first: the run's own file", await File.ReadAllTextAsync(recovered, Timeout), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ADestination_RetiresNothingThePlaneStillHolds()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);

        await using var runtime = await StartAsync(checkpointThreshold: 2);
        var set = runtime.Configuration.BackupSets.Single();
        await BackUpAndSyncAsync(runtime, set, "first");
        await BackUpAndSyncAsync(runtime, set, "second");
        var carried = CarriedBy(await LoadAsync(StagingPath));

        // The third run cannot retire at staging, so staging still lists
        // what the checkpoint carries. Were the destination to retire it, the
        // next sync would copy it straight back.
        var gate = runtime.SetGate(set.Id);
        await gate.WaitAsync(Timeout);
        try
        {
            await BackUpAsync(runtime, set, "third");
        }
        finally
        {
            gate.Release();
        }

        await SyncAsync(runtime, set);

        Assert.IsTrue(carried.IsSubsetOf(await DeltasAtTheirKeysAsync(StagingPath)));
        Assert.IsTrue(carried.IsSubsetOf(await DeltasAtTheirKeysAsync(Replica)));

        await BackUpAndSyncAsync(runtime, set, "fourth");

        Assert.IsFalse((await DeltasAtTheirKeysAsync(Replica)).Overlaps(carried));
    }

    [TestMethod]
    public async Task ADirectShipRun_RetiresAtTheDestinationsItWrote()
    {
        // The run leaves each destination level with the metadata store, so
        // the sync after it has nothing to read and skips: the run itself
        // retires at the destinations it wrote, as it did in its own store.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);

        await using var runtime = await StartAsync(checkpointThreshold: 2);
        var set = runtime.Configuration.BackupSets.Single();
        await BackUpAsync(runtime, set, "first");
        await BackUpAsync(runtime, set, "second");
        var before = await LoadAsync(Replica);
        var carried = CarriedBy(before);
        Assert.IsNotEmpty(carried);

        await BackUpAsync(runtime, set, "third");

        Assert.IsFalse((await DeltasAtTheirKeysAsync(MetadataRoot)).Overlaps(carried));
        Assert.IsFalse((await DeltasAtTheirKeysAsync(Replica)).Overlaps(carried));
        Assert.IsTrue(before.AllEntries.ToHashSet().IsSubsetOf((await LoadAsync(Replica)).AllEntries));
    }

    [TestMethod]
    public async Task ASetUpgradedPastThisBuild_RetiresNothingAtItsDestination()
    {
        // Which of a newer format's objects may go is not this build's to
        // judge (ADR-0066 Amendment 1): its sync still copies, and deletes
        // nothing on its reading of the format.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);

        HashSet<DeltaId> carried;
        await using (var runtime = await StartAsync(checkpointThreshold: 2))
        {
            var set = runtime.Configuration.BackupSets.Single();
            await BackUpAndSyncAsync(runtime, set, "first");
            await BackUpAndSyncAsync(runtime, set, "second");
            carried = CarriedBy(await LoadAsync(Replica));
            await BackUpAsync(runtime, set, "third");
            Assert.IsFalse((await DeltasAtTheirKeysAsync(StagingPath)).Overlaps(carried));
            Assert.IsTrue(carried.IsSubsetOf(await DeltasAtTheirKeysAsync(Replica)));
        }

        using (var provisioning = new InstallationCredentialStore(_harness.StateDirectory).TryLoad())
        {
            Assert.IsNotNull(provisioning, "the installation is not set up");
            await NewerBuildUpgrade.PlantAsync(
                new LocalFileSystemObjectStore(StagingPath), provisioning.Credential,
                Domain.FormatLimits.LatestFormatVersion, Timeout);
        }

        // A day on, so the pass is the reconciling one: the record sits under
        // no prefix the copy's named phases walk (ADR-0056).
        await using (var runtime = await StartAsync(checkpointThreshold: 2))
        {
            await SyncAsync(runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now.AddDays(1));
        }

        Assert.IsTrue(
            carried.IsSubsetOf(await DeltasAtTheirKeysAsync(Replica)),
            "The destination's copies are kept, though staging let them go before the upgrade.");
    }

    [TestMethod]
    public async Task ACopyBack_ADeltaRetiredUnderIt_IsSkipped_AndTheCheckpointPublishedSinceTheListingIsCopied()
    {
        var from = new LocalFileSystemObjectStore(Path.Combine(_harness.WorkPath, "from"));
        var to = new LocalFileSystemObjectStore(Path.Combine(_harness.WorkPath, "to"));
        var retired = MetadataStoreKeys.IndexDelta(0, DeltaId.FromBytes(Filled(0xA1)));
        var kept = MetadataStoreKeys.IndexDelta(0, DeltaId.FromBytes(Filled(0xB2)));
        var carrier = MetadataStoreKeys.IndexCheckpoint(0, CheckpointId.FromBytes(Filled(0xC3)));
        await PutAsync(from, retired);
        await PutAsync(from, kept);

        // The checkpoint lands, and the delta goes, after the copy listed
        // the store and before it reads the delta.
        var racing = new InterleavingOnReadObjectStore(from, retired, async () =>
        {
            await PutAsync(from, carrier);
            await from.DeleteAsync(retired, DeleteConditions.None, Timeout);
        });

        _ = await ServiceRuntime.CopyBackAsync(racing, to, admitBlob: null, Timeout);

        Assert.IsTrue(racing.Fired);
        Assert.IsFalse((await to.GetMetadataAsync(retired, Timeout)).Found);
        Assert.IsTrue((await to.GetMetadataAsync(kept, Timeout)).Found);
        Assert.IsTrue((await to.GetMetadataAsync(carrier, Timeout)).Found, "The checkpoint published after the listing carries the delta that went.");
    }

    [TestMethod]
    public async Task ACopyBack_AnyOtherObjectThatVanishesUnderIt_StillFailsIt()
    {
        var from = new LocalFileSystemObjectStore(Path.Combine(_harness.WorkPath, "from"));
        var to = new LocalFileSystemObjectStore(Path.Combine(_harness.WorkPath, "to"));
        var journal = MetadataStoreKeys.Journal(WriterId.FromBytes(Filled(0xD4)), 1);
        await PutAsync(from, journal);

        var racing = new InterleavingOnReadObjectStore(from, journal, async () =>
            _ = await from.DeleteAsync(journal, DeleteConditions.None, Timeout));

        _ = await Assert.ThrowsExactlyAsync<IOException>(
            async () => await ServiceRuntime.CopyBackAsync(racing, to, admitBlob: null, Timeout));
    }

    private static byte[] Filled(byte value) => Enumerable.Repeat(value, 16).ToArray();

    private static HashSet<DeltaId> CarriedBy(IndexState state) =>
        state.Checkpoints.SelectMany(checkpoint => checkpoint.Checkpoint.SubsumedDeltaIds).ToHashSet();

    private static async Task<HashSet<DeltaId>> DeltasAtTheirKeysAsync(string plane)
    {
        var present = new HashSet<DeltaId>();
        await foreach (var entry in new LocalFileSystemObjectStore(plane)
            .ListAsync(ObjectPrefix.Parse("index/delta/"), ListOptions.Default, CancellationToken.None))
        {
            Assert.IsTrue(MetadataStoreKeys.TryParseIndexDelta(entry.Key, out var deltaId));
            present.Add(deltaId);
        }

        return present;
    }

    private async Task<IndexState> LoadAsync(string plane)
    {
        var store = new LocalFileSystemObjectStore(plane);
        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, Timeout);
        using (repository)
        using (authority)
        using (var loader = new IndexLoader(store, repository.RepositoryId, repository.Credential))
        {
            var state = await loader.LoadAsync(
                currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null,
                blobState: null, Timeout);
            Assert.IsEmpty(state.Findings, string.Join(" | ", state.Findings.Select(finding => finding.Detail)));
            return state;
        }
    }

    private async Task BackUpAsync(ServiceRuntime runtime, BackupSetConfiguration set, string name)
    {
        _harness.WriteSourceFile($"docs/{name}.txt", $"{name}: the run's own file");
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task BackUpAndSyncAsync(ServiceRuntime runtime, BackupSetConfiguration set, string name)
    {
        await BackUpAsync(runtime, set, name);
        await SyncAsync(runtime, set);
    }

    // On the clock as it stands after the run unless told otherwise: a
    // direct-ship set leaves out of its next run a destination whose last
    // success predates its last.
    private async Task SyncAsync(ServiceRuntime runtime, BackupSetConfiguration set, DateTimeOffset? at = null)
    {
        var sync = FanOut.Enqueue(runtime, set, "vault", at ?? DateTimeOffset.Now, userInitiated: true);
        Assert.IsNotNull(sync);
        await sync.WaitAsync(Timeout);
        var row = runtime.DestinationSync.Find(set.Id, "vault")!;
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
    }

    private async Task<HashSet<string>> SnapshotIdsAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        return listed.Snapshots.Select(snapshot => snapshot.SnapshotId).ToHashSet(StringComparer.Ordinal);
    }

    private async Task PutAsync(LocalFileSystemObjectStore store, ObjectKey key) =>
        Assert.AreEqual(
            PutOutcome.Created,
            (await store.PutAsync(
                key, _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false)),
                PutConditions.IfNotExists, Timeout)).Outcome);

    private void WriteConfiguration(bool directShip) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
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
                Destinations = [new SetDestinationReference { Ref = "vault" }],
                DirectShip = directShip,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task<ServiceRuntime> StartAsync(int? checkpointThreshold)
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
                CheckpointThresholdOverride = checkpointThreshold,
            },
            Timeout);
    }
}

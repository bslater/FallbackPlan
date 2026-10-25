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

using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service checkpoints a format-5 set's index after a backup (FR-MAN-008,
/// FR-MAN-009; ADR-0097). Once enough of the set's deltas sit outside every
/// checkpoint, the backup that found them carries them into one, in the set's
/// own plane and, for a direct-ship set, through the same run to its
/// destinations. A load there then reads the checkpoint rather than every
/// delta. A set below format 5 never publishes one, because a reader that
/// predates the form would report it as damage.
/// </summary>
/// <remarks>
/// The threshold is set low through the service's options; the default is
/// sixty-four deltas. Nothing here deletes a delta.
/// This class does not establish FR-MAN-017.
/// </remarks>
[TestClass]
public sealed class IndexCheckpointServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string MetadataRoot => Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId);

    // A local destination holds each repository it receives under its own directory.
    private string Replica => Assert.ContainsSingle(Directory.GetDirectories(Vault));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task AFormatFiveSet_AfterEnoughBackups_CheckpointsItsDeltas_HereAndAtItsDestination()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();

        await using (var runtime = await StartAsync(checkpointThreshold: 2))
        {
            var set = runtime.Configuration.BackupSets.Single();
            _harness.WriteSourceFile("docs/first.txt", "the first backup's file");
            await RunOnAsync(runtime, set);
            _harness.WriteSourceFile("docs/second.txt", "the second backup's file");
            await RunOnAsync(runtime, set);
        }

        foreach (var plane in new[] { MetadataRoot, Replica })
        {
            var (state, deltaReads) = await LoadAsync(plane);
            Assert.IsNotEmpty(state.Checkpoints, $"No checkpoint reached {plane}.");
            Assert.IsTrue(
                state.Checkpoints.All(checkpoint => checkpoint.Checkpoint.SubsumedDeltas.Count > 0),
                "A checkpoint the service publishes carries its deltas.");
            Assert.AreEqual(0, deltaReads, $"A load at {plane} still fetched every delta.");
        }
    }

    [TestMethod]
    public async Task AFormatFourSet_NeverCheckpoints()
    {
        ServiceRuntime.ArchiveFormatVersion = FormatVersions.IndexGenerations;
        Directory.CreateDirectory(Vault);
        WriteConfiguration();

        await using (var runtime = await StartAsync(checkpointThreshold: 1))
        {
            var set = runtime.Configuration.BackupSets.Single();
            _harness.WriteSourceFile("docs/first.txt", "the first backup's file");
            await RunOnAsync(runtime, set);
            _harness.WriteSourceFile("docs/second.txt", "the second backup's file");
            await RunOnAsync(runtime, set);
        }

        foreach (var plane in new[] { MetadataRoot, Replica })
        {
            var (state, _) = await LoadAsync(plane);
            Assert.IsEmpty(state.Checkpoints, $"A format-4 set published a checkpoint at {plane}.");
        }
    }

    /// <summary>Loads a plane's index and counts how many delta objects the load fetched.</summary>
    private async Task<(IndexState State, int DeltaReads)> LoadAsync(string plane)
    {
        var store = new CountingObjectStore(new LocalFileSystemObjectStore(plane));
        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, Timeout);
        using (repository)
        using (authority)
        using (var loader = new IndexLoader(store, repository.RepositoryId, repository.Credential))
        {
            var before = store.ReadKeys.Count;
            var state = await loader.LoadAsync(
                currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null,
                blobState: null, Timeout);
            Assert.IsEmpty(state.Findings, string.Join(" | ", state.Findings.Select(finding => finding.Detail)));
            var deltaReads = store.ReadKeys.Skip(before).Count(key => key.StartsWith("index/delta/", StringComparison.Ordinal));
            return (state, deltaReads);
        }
    }

    private async Task RunOnAsync(ServiceRuntime runtime, BackupSetConfiguration set)
    {
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private void WriteConfiguration() => new ClientConfiguration
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
                Destinations = [new SetDestinationReference { Ref = "vault" }],
                DirectShip = true,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task<ServiceRuntime> StartAsync(int checkpointThreshold)
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

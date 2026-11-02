using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A local destination whose drive fills part way through reads as one the
/// free-space floor turned away does: unavailable, in the store's words, a
/// gap that closes itself once room is made (FR-QUOTA-001, FR-DEST-003;
/// ADR-0012 Amendment 7). Before, it read failed, as a fault a person must
/// fix.
/// </summary>
/// <remarks>
/// The store's own refusal of a full volume is shown on a real one
/// (<c>Storage.ContractTests/LocalStoreFullTests</c>). Here a store that
/// answers as a full one does stands in through
/// <see cref="ServiceOptions.ReplicaStoreDecorator"/>, past the floor, which
/// the probe says the volume clears.
/// </remarks>
[TestClass]
public sealed class LocalDestinationFullTests : IDisposable
{
    private const string NoRoom = "No room on the destination for the object.";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ASync_ToALocalDestinationThatFills_ReadsUnavailable_InTheStoresWords()
    {
        await using var runtime = await StartAsync(directShip: false);
        var set = runtime.Configuration.BackupSets.Single();
        var captured = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", captured.Outcome, captured.Detail);

        var sync = FanOut.Enqueue(runtime, set, "vault-b", DateTimeOffset.Now, userInitiated: true);
        Assert.IsNotNull(sync);
        await sync.WaitAsync(Timeout);

        var row = runtime.DestinationSync.Find(set.Id, "vault-b");
        Assert.IsNotNull(row);
        Assert.AreEqual(DestinationSyncState.Unavailable, row.State, row.LastError);
        Assert.Contains(NoRoom, row.LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ADirectShipRun_WhoseLocalDestinationFills_DropsIt_AsUnavailable_InTheStoresWords()
    {
        await using var runtime = await StartAsync(directShip: true);
        var set = runtime.Configuration.BackupSets.Single();

        // vault-a carries the run; vault-b is dropped from it.
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

        var row = runtime.DestinationSync.Find(set.Id, "vault-b");
        Assert.IsNotNull(row);
        Assert.AreEqual(DestinationSyncState.Unavailable, row.State, row.LastError);
        Assert.Contains(NoRoom, row.LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ADirectShipRun_WhoseLocalDestinationIsFullBeforeItsFirstByte_DropsIt_AsUnavailable()
    {
        // Full from the start: the run's seeding of the repository's
        // descriptor is refused, before any blob is.
        await using var runtime = await StartAsync(directShip: true, fullFromTheStart: true);
        var set = runtime.Configuration.BackupSets.Single();

        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

        var row = runtime.DestinationSync.Find(set.Id, "vault-b");
        Assert.IsNotNull(row);
        Assert.AreEqual(DestinationSyncState.Unavailable, row.State, row.LastError);
        Assert.Contains(NoRoom, row.LastError!, StringComparison.Ordinal);
    }

    private async Task<ServiceRuntime> StartAsync(bool directShip, bool fullFromTheStart = false)
    {
        var vaultA = Directory.CreateDirectory(Path.Combine(_harness.WorkPath, "vault-a")).FullName;
        var vaultB = Directory.CreateDirectory(Path.Combine(_harness.WorkPath, "vault-b")).FullName;
        _harness.WriteSourceFile("docs/report.txt", new string('r', 200_000) + "more than the drive has room for");
        WriteConfiguration(vaultA, vaultB, directShip);
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
                AvailableBytesProbe = _ => long.MaxValue,
                ReplicaStoreDecorator = (name, store) =>
                    string.Equals(name, "vault-b", StringComparison.Ordinal) ? new FullStore(store, fullFromTheStart) : store,
            },
            Timeout);
    }

    private void WriteConfiguration(string vaultA, string vaultB, bool directShip) =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new() { Id = new string('1', 32), Name = "vault-a", Kind = DestinationKind.LocalPath, Path = vaultA },
                new() { Id = new string('2', 32), Name = "vault-b", Kind = DestinationKind.LocalPath, Path = vaultB },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations =
                    [
                        new SetDestinationReference { Ref = "vault-a" },
                        new SetDestinationReference { Ref = "vault-b" },
                    ],
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    /// <summary>
    /// A local store whose volume has no room for any blob, or for anything
    /// at all where <paramref name="refuseEverything"/>: it answers as a full
    /// one does.
    /// </summary>
    private sealed class FullStore(IObjectStore inner, bool refuseEverything) : IObjectStore
    {
        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public ValueTask<PutResult> PutAsync(
            ObjectKey key, Func<CancellationToken, ValueTask<Stream>> openContent, PutConditions conditions, CancellationToken cancellationToken) =>
            refuseEverything || key.Value.StartsWith("blobs/", StringComparison.Ordinal)
                ? throw new StoreFullException(NoRoom, new IOException("There is not enough space on the disk."))
                : inner.PutAsync(key, openContent, conditions, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}

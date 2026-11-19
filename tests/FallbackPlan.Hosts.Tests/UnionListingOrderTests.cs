using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The stores made of other stores list in ordinal key order, as every store
/// does (docs/architecture/05-storage-providers.md §2.3; NFR-PORT-004;
/// ADR-0012 Amendment 8): the archive with a peer's blobs behind
/// it, which a sync reads where only a peer holds the newest backup, and a
/// direct-ship set's archive, whose metadata is on this machine and whose
/// blobs are at its destinations. A reader that resumes a listing after the
/// last key it saw is given every key after it, and none twice.
/// </summary>
[TestClass]
public sealed class UnionListingOrderTests : IDisposable
{
    private const string PeerOnly = "blobs/data/aaaa/aaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BothHold = "blobs/data/bbbb/bbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Delta = "index/delta/0000000000000000/ugq2dinbugq2dinbugq2dinbue";
    private const string Record = "snapshots/device-a/set-a/snapshot-a";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    [TestMethod]
    public async Task TheArchiveWithAPeersBlobsBehindIt_ListsInKeyOrder_EachKeyOnce()
    {
        var store = await PeerFilledAsync();

        CollectionAssert.AreEqual(new[] { PeerOnly, BothHold, Delta, Record }, await KeysAsync(store, after: null));
    }

    [TestMethod]
    public async Task TheArchiveWithAPeersBlobsBehindIt_ResumedAfterAKey_ListsEveryKeyAfterIt()
    {
        var store = await PeerFilledAsync();

        var first = (await KeysAsync(store, after: null)).Take(2).ToList();
        var rest = await KeysAsync(store, after: first[^1]);

        CollectionAssert.AreEqual(new[] { PeerOnly, BothHold, Delta, Record }, first.Concat(rest).ToList());
    }

    [TestMethod]
    public async Task ADirectShipSetsArchive_ListsInKeyOrder_EachKeyOnce_AndResumesWhereItStopped()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));

        await _harness.SetupAsync();
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
        var archive = await runtime.ArchiveForAsync(runtime.Configuration.BackupSets.Single(), Timeout);
        Assert.IsNotNull(archive.ShipSink, "the premise: the set ships straight to its destination");

        var keys = await KeysAsync(archive.Store, after: null);
        Assert.Contains(key => key.StartsWith("blobs/", StringComparison.Ordinal), keys, "the premise: blobs are listed");
        Assert.Contains(key => key.StartsWith("snapshots/", StringComparison.Ordinal), keys, "the premise: records are listed");
        CollectionAssert.AreEqual(keys.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToList(), keys);

        var half = keys.Count / 2;
        var rest = await KeysAsync(archive.Store, after: keys[half - 1]);
        CollectionAssert.AreEqual(keys.Skip(half).ToList(), rest);
    }

    /// <summary>
    /// The archive holds a blob, an index delta and a record; the peer holds
    /// that blob too, and one the archive lacks, which sorts before every key
    /// the archive holds.
    /// </summary>
    private async Task<IObjectStore> PeerFilledAsync()
    {
        var archive = new LocalFileSystemObjectStore(Path.Combine(_harness.WorkPath, "archive"));
        var peer = new LocalFileSystemObjectStore(Path.Combine(_harness.WorkPath, "peer"));
        foreach (var key in new[] { BothHold, Delta, Record })
        {
            await PutAsync(archive, key);
        }

        foreach (var key in new[] { PeerOnly, BothHold })
        {
            await PutAsync(peer, key);
        }

        return new PeerFilledObjectStore(archive, peer, "friend");
    }

    private async Task<List<string>> KeysAsync(IObjectStore store, string? after)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse(string.Empty), new ListOptions { ResumeAfter = after }, Timeout))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    private async Task PutAsync(LocalFileSystemObjectStore store, string key) =>
        await store.PutAsync(
            ObjectKey.Parse(key),
            _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3])),
            PutConditions.None,
            Timeout);

    private void WriteConfiguration() =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('2', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
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
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    /// <inheritdoc />
    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }
}

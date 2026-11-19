using System.Runtime.CompilerServices;
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
/// last key it saw is given every key after it, and none twice. Each part
/// still answers only for what it is the store of: blobs from the peer or the
/// destinations, everything else from the archive or this machine, the
/// archive first where both hold a blob; and a peer that stops answering part
/// way through a listing is named in the failure.
/// </summary>
[TestClass]
public sealed class UnionListingOrderTests : IDisposable
{
    private const string PeerOnly = "blobs/data/aaaa/aaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BothHold = "blobs/data/bbbb/bbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Delta = "index/delta/0000000000000000/ugq2dinbugq2dinbugq2dinbue";
    private const string Record = "snapshots/device-a/set-a/snapshot-a";
    private const string PeerRecord = "snapshots/device-a/set-a/snapshot-b";

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
    public async Task TheArchiveWithAPeersBlobsBehindIt_AnswersForABlobBothHold()
    {
        var store = await PeerFilledAsync();

        var entries = await EntriesAsync(store, ObjectPrefix.All);

        Assert.AreEqual(3L, entries.Single(entry => entry.Key.Value == BothHold).Length, "the peer answered for a blob the archive holds");
    }

    [TestMethod]
    public async Task TheArchiveWithAPeersBlobsBehindIt_ListsNothingButBlobsFromThePeer()
    {
        // The peer holds a record the archive does not: a backup the set has
        // let go is not the sync's to carry back (ADR-0198).
        var store = await PeerFilledAsync();

        var records = await EntriesAsync(store, ObjectPrefix.Parse("snapshots/"));

        CollectionAssert.AreEqual(new[] { Record }, records.Select(entry => entry.Key.Value).ToList());
    }

    [TestMethod]
    public async Task APeerThatStopsAnsweringPartWayThroughAListing_IsNamedInTheFailure()
    {
        var archive = new LocalFileSystemObjectStore(Path.Combine(_harness.WorkPath, "archive"));
        var peer = new LocalFileSystemObjectStore(Path.Combine(_harness.WorkPath, "peer"));
        await PutAsync(archive, Record);
        await PutAsync(peer, PeerOnly);
        await PutAsync(peer, BothHold);
        var store = new PeerFilledObjectStore(archive, new ListingThatBreaks(peer), "friend");

        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => KeysAsync(store, after: null));

        Assert.Contains("peer 'friend'", failure.Message, StringComparison.Ordinal);
        Assert.IsInstanceOfType<IOException>(failure.InnerException, out var cause);
        Assert.AreEqual(ListingThatBreaks.Words, cause.Message);
    }

    [TestMethod]
    public async Task ADirectShipSetsArchive_ListsInKeyOrder_EachKeyOnce_AndResumesWhereItStopped()
    {
        await using var runtime = await BackedUpAsync();
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

    [TestMethod]
    public async Task ADirectShipSetsArchive_ListsBlobsFromItsDestinations_AndAllElseFromThisMachine()
    {
        await using var runtime = await BackedUpAsync();
        var archive = await runtime.ArchiveForAsync(runtime.Configuration.BackupSets.Single(), Timeout);

        // A record only the destination holds, and a metadata blob's copy
        // only this machine holds: neither is the archive's to list.
        const string StrayRecord = "snapshots/device-z/set-z/stray";
        const string StrayCopy = "blobs/meta/zzzz/zzzzzzzzzzzzzzzzzzzzzzzzzz";
        await PutAsync(new LocalFileSystemObjectStore(Directory.GetDirectories(Vault).Single()), StrayRecord);
        await PutAsync(new LocalFileSystemObjectStore(runtime.SetMetadataPath(_harness.DocsSetId)), StrayCopy);

        var everything = await EntriesAsync(archive.Store, ObjectPrefix.All);
        var records = await EntriesAsync(archive.Store, ObjectPrefix.Parse("snapshots/"));

        Assert.IsNotEmpty(records, "the premise: the backup's record is listed");
        foreach (var listing in new[] { everything, records })
        {
            Assert.DoesNotContain(StrayRecord, listing.Select(entry => entry.Key.Value).ToList(), "a record only a destination holds was listed");
            Assert.DoesNotContain(StrayCopy, listing.Select(entry => entry.Key.Value).ToList(), "a blob only this machine holds was listed");
        }
    }

    /// <summary>A direct-ship set with one local path, after one backup to it.</summary>
    private async Task<ServiceRuntime> BackedUpAsync()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));

        await _harness.SetupAsync();
        var runtime = await ServiceRuntime.StartAsync(
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
        return runtime;
    }

    /// <summary>
    /// The archive holds a blob, an index delta and a record; the peer holds
    /// that blob too, at another length, one the archive lacks, which sorts
    /// before every key the archive holds, and a record the archive lacks.
    /// </summary>
    private async Task<IObjectStore> PeerFilledAsync()
    {
        var archive = new LocalFileSystemObjectStore(Path.Combine(_harness.WorkPath, "archive"));
        var peer = new LocalFileSystemObjectStore(Path.Combine(_harness.WorkPath, "peer"));
        foreach (var key in new[] { BothHold, Delta, Record })
        {
            await PutAsync(archive, key);
        }

        await PutAsync(peer, PeerOnly);
        await PutAsync(peer, BothHold, length: 5);
        await PutAsync(peer, PeerRecord);
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

    private async Task<List<ObjectEntry>> EntriesAsync(IObjectStore store, ObjectPrefix prefix)
    {
        var entries = new List<ObjectEntry>();
        await foreach (var entry in store.ListAsync(prefix, ListOptions.Default, Timeout))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private async Task PutAsync(LocalFileSystemObjectStore store, string key, int length = 3) =>
        await store.PutAsync(
            ObjectKey.Parse(key),
            _ => ValueTask.FromResult<Stream>(new MemoryStream(new byte[length])),
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

    /// <summary>A peer whose listing answers once and then stops, as a dropped link does.</summary>
    private sealed class ListingThatBreaks(IObjectStore inner) : IObjectStore
    {
        public const string Words = "the link dropped";

        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public ValueTask<PutResult> PutAsync(
            ObjectKey key, Func<CancellationToken, ValueTask<Stream>> openContent, PutConditions conditions, CancellationToken cancellationToken) =>
            inner.PutAsync(key, openContent, conditions, cancellationToken);

        public async IAsyncEnumerable<ObjectEntry> ListAsync(
            ObjectPrefix prefix, ListOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var entry in inner.ListAsync(prefix, options, cancellationToken))
            {
                yield return entry;
                throw new IOException(Words);
            }
        }

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}

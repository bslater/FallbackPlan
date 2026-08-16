using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// That the index never names an object no blob holds — for the objects it
/// matters for (ledger O5 — index objects referencing data blobs that no
/// longer exist).
/// </summary>
/// <remarks>
/// <para>
/// Two separate claims live here, and conflating them is how this class of
/// defect survives review.
/// </para>
/// <para>
/// The first is an <em>invariant</em>: after a collection pass has really
/// deleted blobs, every object still reachable from a surviving snapshot must
/// still resolve to a blob the store holds. Publication order guarantees the
/// forward direction — blobs are durable in step 4, index entries naming them
/// are published in step 6 — and retention guarantees the reverse, because it
/// condemns only blobs holding nothing reachable. This is the property a
/// restore depends on and it is asserted directly.
/// </para>
/// <para>
/// The second is a <em>safeguard</em>: specification 07 §3 rule 3 says an
/// entry whose blob is known deleted is treated as superseded and reported as
/// damage. It has been implemented in <see cref="IndexPrecedence"/> since the
/// index plane was written, and until the commit that added this class it had
/// never executed outside a unit test — the only production rebuild caller
/// passed no blob-lifecycle knowledge at all, so the rule's own precondition
/// was never true. A safeguard that cannot fire is not a safeguard.
/// </para>
/// </remarks>
[TestClass]
public sealed class RetentionIndexIntegrityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-index-integrity-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "index-integrity-tests-passphrase";
    private static readonly string SetId = new('a', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, SetId);

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    public RetentionIndexIntegrityTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "steady.txt"), "never edited again");
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), "day one");

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32),
                    Name = "vault",
                    Kind = DestinationKind.LocalPath,
                    Path = Directory.CreateDirectory(Path.Combine(_root, "vault")).FullName,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = SetId,
                    Name = "docs",
                    Root = SourceRoot,
                    Schedule = "every 4h",
                    Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    /// <summary>
    /// The invariant, asserted after the sweep has actually removed blobs:
    /// nothing a surviving snapshot reaches points into a blob that is gone.
    /// </summary>
    [TestMethod]
    public async Task AfterCollection_EveryReachableObject_StillResolvesToABlobTheStoreHolds()
    {
        var store = await SweptArchiveAsync();

        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        Assert.IsNotEmpty(survey.Snapshots);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        Assert.IsEmpty(reader.SkippedBlobs, "a blob would not open, so the inventory below is not authoritative");

        var (reachable, unwalkable) = await StagingMark.MarkAsync(
            reader, survey.Snapshots, CancellationToken.None);
        Assert.IsEmpty(unwalkable, "the surviving closure would not walk");

        var held = reader.Blobs.Select(blob => blob.BlobId).ToHashSet();
        var located = reader.Blobs
            .SelectMany(blob => blob.Records.Select(record => (record.ObjectId, blob.BlobId)))
            .ToLookup(pair => pair.ObjectId, pair => pair.BlobId);

        foreach (var objectId in reachable)
        {
            var holders = located[objectId].ToList();
            Assert.IsNotEmpty(holders, $"the reachable object {objectId} is held by no blob in the store");

            foreach (var holder in holders)
            {
                Assert.IsTrue(held.Contains(holder), $"object {objectId} resolves to blob {holder}, which is gone");
            }
        }
    }

    /// <summary>
    /// And the same closure reads: resolution is not the whole claim, because
    /// a record table can name an offset the bytes do not support.
    /// </summary>
    [TestMethod]
    public async Task AfterCollection_EveryReachableObject_StillReads()
    {
        var store = await SweptArchiveAsync();

        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var (reachable, _) = await StagingMark.MarkAsync(reader, survey.Snapshots, CancellationToken.None);

        foreach (var objectId in reachable)
        {
            var result = await reader.ReadSegmentAsync(objectId, CancellationToken.None);
            Assert.AreEqual(
                Repository.Packing.RecordReadOutcome.Ok,
                result.Outcome,
                $"the reachable object {objectId} would not read: {result.Detail}");
        }
    }

    /// <summary>
    /// The safeguard, and the gap it sat in. Rebuilding with no
    /// blob-lifecycle knowledge — which is exactly what the production path
    /// did before this change — resolves the collected objects to their
    /// removed blobs and says nothing. Rebuilding with the store's own
    /// inventory drops those locations and reports each as damage.
    /// </summary>
    /// <remarks>
    /// Both arms run against one archive so the difference is attributable to
    /// the argument and to nothing else.
    /// </remarks>
    [TestMethod]
    public async Task ARebuildToldWhichBlobsSurvive_RefusesToServeALocationIntoOneThatDidNot()
    {
        var store = await SweptArchiveAsync();

        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var held = reader.Blobs.Select(blob => blob.BlobId).ToHashSet();

        var blind = await RebuildAsync(store, repository, blobState: null);
        var sighted = await RebuildAsync(
            store, repository, CatalogueRebuilder.KnownBlobs(held, inventoryComplete: true));

        // The archive really does still name blobs the sweep removed —
        // deletion-only collection leaves the entries behind, and removing
        // them is compaction's job (ADR-0025, phase 4). Without that this
        // test would pass vacuously.
        Assert.IsTrue(
            sighted.Entries.Any(entry => !held.Contains(entry)),
            "no index entry names a collected blob, so there is nothing for rule 3 to catch");

        Assert.IsEmpty(
            blind.Findings.Where(finding => finding.Kind == DamageKind.MissingBlob),
            "a rebuild told nothing about blob lifecycles reported a missing blob it could not have known about");

        Assert.IsNotEmpty(
            sighted.Findings.Where(finding => finding.Kind == DamageKind.MissingBlob),
            "the rebuild resolved an object into a blob the store does not hold, and said nothing");

        // Rule 3 is a refusal, not merely a complaint: the dead location must
        // not win the resolution either.
        foreach (var (objectId, winner) in sighted.Resolved)
        {
            Assert.IsTrue(
                held.Contains(winner),
                $"object {objectId} still resolves to blob {winner}, which the store does not hold");
        }
    }

    /// <summary>
    /// The guard on the guard. An inventory that could not be trusted to be
    /// exhaustive concludes nothing, because "absent from a listing with
    /// holes" and "collected" are not the same fact — and discarding a valid
    /// location on the strength of the first is a self-inflicted outage.
    /// </summary>
    [TestMethod]
    public void AnIncompleteInventory_ConcludesNothing()
    {
        var known = BlobId.FromBytes(Convert.FromHexString(new string('1', 32)));
        var stranger = BlobId.FromBytes(Convert.FromHexString(new string('2', 32)));

        var trusted = CatalogueRebuilder.KnownBlobs([known], inventoryComplete: true);
        Assert.AreEqual(BlobState.Live, trusted(known));
        Assert.AreEqual(BlobState.Deleted, trusted(stranger));

        var doubtful = CatalogueRebuilder.KnownBlobs([known], inventoryComplete: false);
        Assert.AreEqual(BlobState.Live, doubtful(known));
        Assert.AreEqual(
            BlobState.Live,
            doubtful(stranger),
            "an inventory with holes concluded a blob was deleted");
    }

    private static async Task<(IReadOnlyList<DamageFinding> Findings,
        IReadOnlyList<BlobId> Entries,
        IReadOnlyList<(ObjectId ObjectId, BlobId Winner)> Resolved)> RebuildAsync(
        LocalFileSystemObjectStore store, OpenedRepository repository, Func<BlobId, BlobState>? blobState)
    {
        var loader = new IndexLoader(store, repository.RepositoryId, repository.Hierarchy);
        var state = await loader.LoadAsync(
            Math.Max(repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value),
            gapPatienceGenerations: 2,
            isSequenceAccountedAsync: null,
            blobState,
            CancellationToken.None);

        return (state.Findings,
            [.. state.AllEntries.Select(entry => entry.Entry.BlobId).Distinct()],
            [.. state.Resolved.Select(pair => (pair.Key, pair.Value.Entry.BlobId))]);
    }

    /// <summary>
    /// An archive that has been through a full collection cycle and really
    /// lost blobs — asserted, because every test here is vacuous otherwise.
    /// </summary>
    private async Task<LocalFileSystemObjectStore> SweptArchiveAsync()
    {
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), "day two");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), "day three");
        await BackUpAsync(Day1.AddDays(2));

        var store = new LocalFileSystemObjectStore(RepoPath);

        // Tombstone, then publish past the decision so the grace runs.
        await RunAsync(store, Day1.AddDays(2).AddHours(1));
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), "day four");
        await BackUpAsync(Day1.AddDays(3));

        // Counted here, after the last publication and immediately before the
        // sweep — counting any earlier compares against a store the fourth
        // backup then added to, which can net out to no visible change.
        var before = await CountAsync(store, "blobs/");
        var swept = await RunAsync(store, Day1.AddDays(3).AddHours(1));

        Assert.IsGreaterThan(0, swept.Swept!.Deleted, "nothing was collected, so nothing is being tested");
        Assert.IsLessThan(before, await CountAsync(store, "blobs/"), "no blob was actually removed");
        return store;
    }

    private static async Task<int> CountAsync(LocalFileSystemObjectStore store, string prefix)
    {
        var count = 0;
        await foreach (var _ in store.ListAsync(
            ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            count++;
        }

        return count;
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, passphrase, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private async Task<RetentionReport> RunAsync(LocalFileSystemObjectStore store, DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, repository, new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
            [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name), _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId), apply: true,
            (ulong)now.ToUnixTimeMilliseconds(), CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}

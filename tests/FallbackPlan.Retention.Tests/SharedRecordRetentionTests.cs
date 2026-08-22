using FallbackPlan.Agent;
using ApiRestoreResult = FallbackPlan.Api.RestoreResult;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// What happens to an object two snapshots share when only one of them expires
/// (ledger O2 — durable state left inconsistent after shared metadata was
/// deleted).
/// </summary>
/// <remarks>
/// <para>
/// Deduplication makes sharing the normal case, not the exotic one. Two files
/// with the same bytes reference one segment; a file unchanged between runs
/// has one version manifest reached from two snapshots' trees; and a blob
/// packs many records belonging to many snapshots. Every one of those is a
/// place where "this snapshot expired, so its content is garbage" is false.
/// </para>
/// <para>
/// Nothing here was found broken. That is the point of writing it down: the
/// design forecloses the mechanism in two independent places —
/// <see cref="StagingMark"/> walks the protected closure into a set, so a
/// second referrer marks the same object again harmlessly, and
/// <see cref="CollectionPlanner"/> condemns a blob only when <em>every</em>
/// record in it is unreachable — and neither had a test that would fail if
/// somebody removed one. Both are asserted below, and the commit that added
/// this class carries the mutation proof.
/// </para>
/// </remarks>
[TestClass]
public sealed class SharedRecordRetentionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-shared-record-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "shared-record-tests-passphrase!!";

    /// <summary>Large enough to be its own segment rather than a rounding error in a blob.</summary>
    private static readonly string TwinContent = string.Concat(Enumerable.Repeat("shared bytes, deduplicated. ", 4096));

    private static readonly string SetId = new('a', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, SetId);

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    public SharedRecordRetentionTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);

        // The twins deduplicate against each other; steady.txt is never
        // touched again, so its version manifest is inherited by every later
        // snapshot; churn.txt is what makes the snapshots differ at all.
        File.WriteAllText(Path.Combine(SourceRoot, "twin-a.txt"), TwinContent);
        File.WriteAllText(Path.Combine(SourceRoot, "twin-b.txt"), TwinContent);
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
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = "every 4h",
                    Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    /// <summary>
    /// The plan is the place to look, because it is what authorises every
    /// later deletion: no blob holding a reachable record may be condemned,
    /// whoever else references it.
    /// </summary>
    [TestMethod]
    public async Task NoCondemnedBlob_HoldsAnythingTheSurvivingSnapshotReaches()
    {
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        var (plan, reachable, reader) = await PlanAsync(store, Day1.AddDays(2).AddHours(1));
        using (reader)
        {
            Assert.IsTrue(plan.Deletable, string.Join("; ", plan.Vetoes));
            Assert.IsNotEmpty(plan.DeletableBlobs, "nothing was condemned, so the assertion below proves nothing");

            var condemned = plan.DeletableBlobs.Select(blob => blob.BlobId).ToHashSet();
            foreach (var (_, blobId, records) in reader.Blobs.Where(blob => condemned.Contains(blob.BlobId)))
            {
                foreach (var record in records)
                {
                    Assert.IsFalse(
                        reachable.Contains(record.ObjectId),
                        $"blob {blobId} was condemned while holding {record.ObjectId}, which the kept snapshot reaches");
                }
            }
        }
    }

    /// <summary>
    /// The twins' shared segment is one object reached through two different
    /// files. Expiring two of the three snapshots must not touch it, and the
    /// blob that holds it must not be condemned.
    /// </summary>
    [TestMethod]
    public async Task ASegmentTwoFilesShare_SurvivesTheExpiryOfEveryOtherSnapshot()
    {
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        var (plan, reachable, reader) = await PlanAsync(store, Day1.AddDays(2).AddHours(1));
        using (reader)
        {
            // The twins are byte-identical, so the surviving snapshot's two
            // file versions name the same segments. Establishing that is the
            // test's control: if dedup did not happen there is nothing shared
            // to protect and the rest of this proves nothing.
            var shared = await SharedSegmentsAsync(store, reader);
            Assert.IsNotEmpty(
                shared,
                "the twin files did not deduplicate, so this test is not exercising sharing");

            var condemned = plan.DeletableBlobs.Select(blob => blob.BlobId).ToHashSet();
            foreach (var segment in shared)
            {
                Assert.IsTrue(reachable.Contains(segment), $"the shared segment {segment} was not marked reachable");

                var holders = reader.Blobs
                    .Where(blob => blob.Records.Any(record => record.ObjectId.Equals(segment)))
                    .ToList();
                Assert.IsNotEmpty(holders, $"no blob holds the shared segment {segment}");

                foreach (var holder in holders)
                {
                    Assert.IsFalse(
                        condemned.Contains(holder.BlobId),
                        $"blob {holder.BlobId} was condemned while holding the shared segment {segment}");
                }
            }
        }
    }

    /// <summary>
    /// The property underneath all of it, checked where it is easiest to get
    /// wrong: a blob holding one live record and several dead ones is retained
    /// whole, counted as compaction backlog, and never condemned.
    /// </summary>
    [TestMethod]
    public async Task ABlobHoldingOneLiveRecord_IsRetainedRatherThanCondemned()
    {
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        var (plan, reachable, reader) = await PlanAsync(store, Day1.AddDays(2).AddHours(1));
        using (reader)
        {
            var mixed = reader.Blobs
                .Where(blob => blob.Records.Any(record => reachable.Contains(record.ObjectId))
                    && blob.Records.Any(record => !reachable.Contains(record.ObjectId)))
                .ToList();

            Assert.IsNotEmpty(
                mixed,
                "no blob mixes live and dead records, so the retained-partial branch is untested here");
            Assert.AreEqual(mixed.Count, plan.RetainedPartialBlobs);

            var condemned = plan.DeletableBlobs.Select(blob => blob.BlobId).ToHashSet();
            foreach (var blob in mixed)
            {
                Assert.IsFalse(condemned.Contains(blob.BlobId), $"the partly live blob {blob.BlobId} was condemned");
            }
        }
    }

    /// <summary>
    /// And the whole point of the exercise, through the real machinery: after
    /// the sweep has actually deleted things, the surviving snapshot restores
    /// every file byte-identical — the twins included.
    /// </summary>
    [TestMethod]
    public async Task AfterTheSweep_TheSurvivingSnapshotStillRestoresEveryFile()
    {
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        // Tombstone, publish past the decision, then sweep for real.
        await RunAsync(store, apply: true, now: Day1.AddDays(2).AddHours(1));
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), "day four");
        await BackUpAsync(Day1.AddDays(3));
        var swept = await RunAsync(store, apply: true, now: Day1.AddDays(3).AddHours(1));
        Assert.IsGreaterThan(0, swept.Swept!.Deleted, "nothing was deleted, so nothing was put at risk");

        var survivor = await NewestSnapshotIdAsync(store);
        var target = Path.Combine(_root, "restored");

        var restored = await RestoreAsync(survivor, target);
        Assert.AreEqual(0, restored.Failed, restored.Outcome);
        Assert.IsGreaterThan(0, restored.Restored, restored.Outcome);

        // Located by name rather than by an assembled path: where inside the
        // output root the executor places a run is the containment feature's
        // contract, tested where that lives. What this test is entitled to
        // assert is that the bytes came back.
        foreach (var name in new[] { "twin-a.txt", "twin-b.txt", "steady.txt", "churn.txt" })
        {
            var found = Directory.GetFiles(target, name, SearchOption.AllDirectories);
            Assert.ContainsSingle(found, $"{name} did not restore");

            var expected = await File.ReadAllBytesAsync(Path.Combine(SourceRoot, name));
            var actual = await File.ReadAllBytesAsync(found[0]);
            Assert.IsTrue(expected.AsSpan().SequenceEqual(actual), $"{name} did not restore byte-identical");
        }
    }

    /// <summary>
    /// The inherited path, stated on its own: a file untouched across runs has
    /// one version manifest that several snapshots' trees reach. The mark
    /// visits it once — the dedup early-return in the walk — and that must
    /// leave it protected, not skipped.
    /// </summary>
    [TestMethod]
    public async Task AVersionManifestSeveralSnapshotsInherit_IsMarkedNotSkipped()
    {
        await BackUpThreeDaysAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        Assert.HasCount(3, survey.Snapshots);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        // Walk the oldest and the newest separately. steady.txt was never
        // edited, so whatever object the walks share is inherited lineage —
        // and it must appear in a mark taken over the newest alone.
        var (oldest, _) = await StagingMark.MarkAsync(reader, [survey.Snapshots[^1]], CancellationToken.None);
        var (newest, _) = await StagingMark.MarkAsync(reader, [survey.Snapshots[0]], CancellationToken.None);

        var inherited = oldest.Intersect(newest).ToList();
        Assert.IsNotEmpty(
            inherited,
            "no object is shared between the oldest and newest snapshots, so nothing is inherited to protect");

        // Marking both together yields exactly the union — the early-return
        // dedups the visit, never the protection.
        var (both, _) = await StagingMark.MarkAsync(
            reader, [survey.Snapshots[0], survey.Snapshots[^1]], CancellationToken.None);
        Assert.IsTrue(
            oldest.Union(newest).ToHashSet().SetEquals(both),
            "walking two snapshots together protected less than walking them apart");
    }

    /// <summary>
    /// The segment objects the two twin files both name in the surviving
    /// snapshot — sharing established structurally, from the manifests, rather
    /// than inferred from a byte count.
    /// </summary>
    private static async Task<IReadOnlyList<ObjectId>> SharedSegmentsAsync(
        LocalFileSystemObjectStore store, RepositoryReader reader)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);

        var root = await DecodeAsync(reader, survey.Snapshots[0].Manifest.RootTree, TreeManifestCodec.Decode);
        Assert.IsNotNull(root, "the surviving snapshot's root tree would not read");

        var twinA = await SegmentsOfAsync(reader, root, "twin-a.txt");
        var twinB = await SegmentsOfAsync(reader, root, "twin-b.txt");

        Assert.IsNotEmpty(twinA, "twin-a.txt referenced no segments");
        Assert.IsNotEmpty(twinB, "twin-b.txt referenced no segments");

        return [.. twinA.Intersect(twinB)];
    }

    private static async Task<IReadOnlyList<ObjectId>> SegmentsOfAsync(
        RepositoryReader reader, TreeManifest tree, string name)
    {
        var entry = tree.Entries.Single(candidate =>
            System.Text.Encoding.UTF8.GetString(candidate.Name.Span) == name);
        var version = await DecodeAsync(reader, entry.ObjectId, FileVersionManifestCodec.Decode);
        Assert.IsNotNull(version, $"{name}'s version manifest would not read");

        return [.. version.SegmentReferences.Select(reference => reference.ObjectId)];
    }

    private static async Task<T?> DecodeAsync<T>(
        RepositoryReader reader, ObjectId objectId, Func<ReadOnlyMemory<byte>, T> decode)
        where T : class
    {
        var result = await reader.ReadSegmentAsync(objectId, CancellationToken.None);
        return result is { Outcome: RecordReadOutcome.Ok, Plaintext: { } plaintext } ? decode(plaintext) : null;
    }

    /// <summary>
    /// Runs the planning half of a pass and hands back the plan, the mark set
    /// and the loaded reader — the three things every assertion here needs.
    /// </summary>
    private static async Task<(CollectionPlan Plan, HashSet<ObjectId> Reachable, RepositoryReader Reader)> PlanAsync(
        LocalFileSystemObjectStore store, DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        var selection = RetentionPlanner.Select(
            [.. survey.Snapshots.Select(snapshot => snapshot.Fact)], Policy, now);
        Assert.IsNotEmpty(selection.Expire, "nothing expired, so nothing could have been over-collected");

        var gate = ReplicationGate.Apply(
            selection.Expire, ["vault"], _ => null, keptBy: (_, _) => false, null,
            (ulong)now.ToUnixTimeMilliseconds());

        var protectedIds = selection.Keep.Select(keep => keep.Snapshot.SnapshotId)
            .Concat(gate.Held.Select(held => held.Snapshot.SnapshotId))
            .ToHashSet(StringComparer.Ordinal);

        var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var (reachable, unwalkable) = await StagingMark.MarkAsync(
            reader,
            [.. survey.Snapshots.Where(snapshot => protectedIds.Contains(snapshot.Fact.SnapshotId))],
            CancellationToken.None);

        var sealingGeneration = Math.Max(
            repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value);
        IReadOnlyList<JournalRecord> records;
        int unparseable;
        using (var journal = new JournalReader(store, repository.RepositoryId, repository.Hierarchy))
        {
            (records, unparseable, _) = await journal.LoadAsync(sealingGeneration, CancellationToken.None);
        }

        var intents = IntentSurveyor.Survey(
            records, unparseable, sealingGeneration, (ulong)now.ToUnixTimeMilliseconds(), skewMarginMs: 300_000);

        return (CollectionPlanner.Plan(survey, selection, gate, reader, reachable, unwalkable, intents),
            reachable, reader);
    }

    private static RetentionConfiguration Policy => new() { KeepDaily = 1, MinGenerations = 1 };

    private async Task<ApiRestoreResult> RestoreAsync(string snapshotId, string outputDirectory)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = ArchivesRoot, StateDirectory = StateDirectory },
            passphrase, CancellationToken.None);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var result = await handler.ExecuteAsync(
            new RunRestoreCommand(snapshotId, null, outputDirectory), CancellationToken.None);

        return result as ApiRestoreResult
            ?? throw new InvalidOperationException($"restore refused: {result}");
    }

    private static async Task<string> NewestSnapshotIdAsync(LocalFileSystemObjectStore store)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        return survey.Snapshots[0].Fact.SnapshotId;
    }

    private async Task BackUpThreeDaysAsync()
    {
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), "day two");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "churn.txt"), "day three");
        await BackUpAsync(Day1.AddDays(2));
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, passphrase, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private async Task<RetentionReport> RunAsync(LocalFileSystemObjectStore store, bool apply, DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, repository, Policy, [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name), _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId), apply,
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

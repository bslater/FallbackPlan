using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// Which neighbouring snapshots lost a path (FR-GC-014, ADR-0094), read from
/// the archive's own sealed trees, never the catalogue, which is a cache. The
/// newer of two snapshots lost a path when it has nothing at a path the older
/// holds, at any depth, or has something of another kind there. A changed or
/// added file is not a deletion.
/// </summary>
/// <remarks>
/// Over a real archive, the retention pass, a destination converged under its
/// own policy and the direct-ship spare each keep the last snapshot holding a
/// deleted file, and the first without it, until the duration since that
/// first one has run. The trees are
/// read with what the service holds, without the passphrase's authority,
/// because that is all a scheduled pass has.
/// </remarks>
[TestClass]
public sealed class DeletedFileSurveyTests : IDisposable
{
    private const string PassphraseText = "deleted-file-survey-passphrase!!";

    private static readonly string SetId = new('e', 32);

    /// <summary>The first pass's clock, which decides only what is due: a capture is dated by the machine's own clock.</summary>
    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-deleted-file-tests", Guid.NewGuid().ToString("n"));

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, SetId);

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    private string NarrowPath => Path.Combine(_root, "narrow");

    private string SpoolDirectory => Directory.CreateDirectory(Path.Combine(_root, "spool")).FullName;

    /// <summary>
    /// The floor and the duration alone: captures dated by the machine's
    /// clock land seconds apart, so a daily window would change what it keeps
    /// whenever a run straddled midnight.
    /// </summary>
    private static RetentionConfiguration Policy => new() { MinGenerations = 1, KeepDeletedDays = 3 };

    public DeletedFileSurveyTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(Path.Combine(SourceRoot, "docs", "deep", "inner"));
        Directory.CreateDirectory(NarrowPath);

        File.WriteAllText(Path.Combine(SourceRoot, "steady.txt"), "never touched again");
        File.WriteAllText(Path.Combine(SourceRoot, "edited.txt"), "day one");
        File.WriteAllText(Path.Combine(SourceRoot, "gone.txt"), "deleted on day three");
        File.WriteAllText(Path.Combine(SourceRoot, "docs", "deep", "inner", "buried.txt"), "deleted on day four");
        File.WriteAllText(Path.Combine(SourceRoot, "docs", "deep", "inner", "stays.txt"), "kept");

        // The set itself declares no retention, so the staging archive keeps
        // every snapshot for the comparisons below. The narrow destination
        // converges under its own policy as each pass fans out (FR-GC-010).
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
                new DestinationConfiguration
                {
                    Id = new string('c', 32),
                    Name = "narrow",
                    Kind = DestinationKind.LocalPath,
                    Path = NarrowPath,
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
                    Destinations =
                    [
                        new SetDestinationReference { Ref = "vault" },
                        new SetDestinationReference { Ref = "narrow", Retention = Policy },
                    ],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    [TestMethod]
    public async Task CompareAsync_ExactlyThePairsThatLostAPath_CountAsDeletions()
    {
        // The second pass edits a file and adds one. The third deletes a file
        // at the top, the fourth one three folders down, and the fifth
        // replaces a file with a folder of the same name.
        await BackUpAsync(Day(1));
        File.WriteAllText(Path.Combine(SourceRoot, "edited.txt"), "day two");
        File.WriteAllText(Path.Combine(SourceRoot, "added.txt"), "new on day two");
        await BackUpAsync(Day(2));
        File.Delete(Path.Combine(SourceRoot, "gone.txt"));
        await BackUpAsync(Day(3));
        File.Delete(Path.Combine(SourceRoot, "docs", "deep", "inner", "buried.txt"));
        await BackUpAsync(Day(4));
        File.Delete(Path.Combine(SourceRoot, "steady.txt"));
        Directory.CreateDirectory(Path.Combine(SourceRoot, "steady.txt"));
        File.WriteAllText(Path.Combine(SourceRoot, "steady.txt", "inside.txt"), "a folder where a file was");
        await BackUpAsync(Day(5));

        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        Assert.HasCount(5, survey.Snapshots);

        // Newest first, as the survey lists them.
        var (fifth, fourth, third, second, first) = (
            survey.Snapshots[0].Fact, survey.Snapshots[1].Fact, survey.Snapshots[2].Fact,
            survey.Snapshots[3].Fact, survey.Snapshots[4].Fact);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var pairs = RetentionPlanner.DeletedFilePairs(
            [.. survey.Snapshots.Select(snapshot => snapshot.Fact)], 30, CapturedAt(fifth));
        Assert.HasCount(4, pairs);

        var deleted = await DeletedFileSurvey.CompareAsync(reader, survey.Snapshots, pairs, CancellationToken.None);

        Assert.IsFalse(deleted.Between(first, second), "an edited file and an added one are no deletion");
        Assert.IsTrue(deleted.Between(second, third), "a file deleted at the top was not found");
        Assert.IsTrue(deleted.Between(third, fourth), "a file deleted three folders down was not found");
        Assert.IsTrue(deleted.Between(fourth, fifth), "a file replaced by a folder of the same name was not found");
    }

    [TestMethod]
    public async Task CompareAsync_ATreeThatWillNotRead_CountsAsHavingLostAPath()
    {
        // What an unreadable tree held cannot be known, so the pair keeps
        // both snapshots rather than calling the loss nothing. The two real
        // snapshots lost nothing between them, which is the control.
        await BackUpAsync(Day(1));
        File.WriteAllText(Path.Combine(SourceRoot, "edited.txt"), "day two");
        await BackUpAsync(Day(2));

        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        var (newer, older) = (survey.Snapshots[0], survey.Snapshots[1]);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var pair = new AdjacentSnapshots(older.Fact, newer.Fact);

        var sound = await DeletedFileSurvey.CompareAsync(reader, [newer, older], [pair], CancellationToken.None);
        Assert.IsFalse(sound.Between(older.Fact, newer.Fact), "an edit was read as a loss, so the case below proves nothing");

        var missing = ObjectId.FromBytes(Enumerable.Repeat((byte)0x5c, 32).ToArray());
        foreach (var (olderSide, newerSide) in new[]
        {
            (older, newer with { Manifest = newer.Manifest with { RootTree = missing } }),
            (older with { Manifest = older.Manifest with { RootTree = missing } }, newer),
        })
        {
            var unreadable = await DeletedFileSurvey.CompareAsync(
                reader, [newerSide, olderSide], [pair], CancellationToken.None);
            Assert.IsTrue(unreadable.Between(older.Fact, newer.Fact));
        }
    }

    [TestMethod]
    public async Task CompareAsync_AFolderSplitAcrossManifests_IsReadToItsLastPart()
    {
        // A folder too wide for one manifest continues in others
        // (specification 06 §9). A file deleted from its last part is a
        // deletion like any other, and one added there is not.
        await BackUpAsync(Day(1));
        await BackUpAsync(Day(2));

        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        var (newer, older) = (survey.Snapshots[0], survey.Snapshots[1]);

        string[] names = [.. Enumerable.Range(0, 40).Select(i => $"file-{i:D3}.txt")];
        var folders = await WriteFoldersAsync(store, repository, [names, names[..^1], [.. names, "file-040.txt"]]);
        var (wide, lastGone, oneMore) = (folders[0], folders[1], folders[2]);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var head = await reader.ReadSegmentAsync(wide, CancellationToken.None);
        Assert.IsNotNull(
            TreeManifestCodec.Decode(head.Plaintext!).Continuation, "the folder fits one manifest, so the case below proves nothing");

        var pair = new AdjacentSnapshots(older.Fact, newer.Fact);
        async Task<bool> LostAsync(ObjectId olderRoot, ObjectId newerRoot) =>
            (await DeletedFileSurvey.CompareAsync(
                reader,
                [
                    newer with { Manifest = newer.Manifest with { RootTree = newerRoot } },
                    older with { Manifest = older.Manifest with { RootTree = olderRoot } },
                ],
                [pair],
                CancellationToken.None)).Between(older.Fact, newer.Fact);

        Assert.IsFalse(await LostAsync(wide, oneMore), "a file added to the folder's last part was read as a loss");
        Assert.IsTrue(await LostAsync(wide, lastGone), "a file deleted from the folder's last part was not found");
    }

    [TestMethod]
    public async Task RunAsync_ADestinationsOwnDuration_HoldsWhatItKeeps_UntilItHasIt()
    {
        // FR-GC-009 with FR-GC-010: staging lets a snapshot go only once
        // every destination that keeps it holds it. A destination never
        // synced keeps the two snapshots either side of the deletion by its
        // own duration, which the set's policy does not declare, so those
        // two are held and the one between them is not.
        var (holder, dater, between, newest) = await BackUpADeletionAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var sync = DestinationSyncStore.Open(StateDirectory);
        var report = await RetentionRunner.RunAsync(
            store, opened.Repository, new RetentionConfiguration { MinGenerations = 1 },
            [new SetDestinationReference { Ref = "offline", Retention = Policy }],
            name => sync.Find(SetId, name), _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId), apply: false,
            (ulong)CapturedAt(newest).ToUnixTimeMilliseconds(), CancellationToken.None);

        var said = string.Join(Environment.NewLine, report.Lines);
        Assert.Contains($"  held {holder.SnapshotId[..12]}… — awaiting offline", report.Lines, said);
        Assert.Contains($"  held {dater.SnapshotId[..12]}… — awaiting offline", report.Lines, said);
        Assert.IsFalse(report.Lines.Any(line => line.Contains(between.SnapshotId[..12], StringComparison.Ordinal)), said);
    }

    [TestMethod]
    public async Task RunAsync_KeepsTheLastSnapshotHoldingADeletedFile_AndSaysWhy_UntilTheDurationRuns()
    {
        var (holder, dater, between, newest) = await BackUpADeletionAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);

        // Under the floor alone only the newest stays. The file went between
        // the first two captures, so for three days from the second the
        // snapshot holding its last version stays too, and so does the one
        // that dates its deletion. The third lost nothing.
        var until = CapturedAt(dater).AddDays(3);
        var report = await RunAsync(store, until.AddDays(-1));
        var said = string.Join(Environment.NewLine, report.Lines);
        Assert.Contains("protected: 3 snapshot(s)", report.Lines, said);
        Assert.Contains($"  keep {holder.SnapshotId[..12]}… — {RetentionPlanner.DeletedFilesReason(until)}", report.Lines, said);
        Assert.Contains(
            $"  keep {dater.SnapshotId[..12]}… — {RetentionPlanner.FirstWithoutDeletedFilesReason(until)}", report.Lines, said);
        Assert.Contains($"  keep {newest.SnapshotId[..12]}… — min-generations", report.Lines, said);
        Assert.IsFalse(report.Lines.Any(line => line.Contains(between.SnapshotId[..12], StringComparison.Ordinal)), said);

        // Three days after the second capture the duration has run, and the
        // floor alone decides.
        var after = await RunAsync(store, until);
        var saidAfter = string.Join(Environment.NewLine, after.Lines);
        Assert.Contains("protected: 1 snapshot(s)", after.Lines, saidAfter);
        Assert.IsFalse(after.Lines.Any(line => line.Contains(holder.SnapshotId[..12], StringComparison.Ordinal)), saidAfter);
    }

    [TestMethod]
    public async Task FanOut_ADestinationUnderItsOwnDuration_KeepsTheLastSnapshotHoldingADeletedFile()
    {
        // The narrow destination converges as each pass fans out, under its
        // own policy: by the fourth it has dropped the third snapshot, which
        // lost nothing, and kept the two either side of the deletion.
        var (holder, dater, between, newest) = await BackUpADeletionAsync();

        var replica = new LocalFileSystemObjectStore(Directory.GetDirectories(NarrowPath).Single());
        var held = await SnapshotsAsync(replica);

        CollectionAssert.AreEquivalent(
            new[] { holder.SnapshotId, dater.SnapshotId, newest.SnapshotId },
            held.Select(snapshot => snapshot.SnapshotId).ToArray(),
            $"not {between.SnapshotId}");
    }

    [TestMethod]
    public async Task ComputeSpares_ADestinationNeverSynced_IsOwedWhatItsOwnDurationKeeps_AndNothingMore()
    {
        // FR-GC-009's direct-ship spare holds back, at every sibling, what a
        // destination's keep-set wants and it has not received. That keep-set
        // reads the deleted-file rule like any other, so the snapshots either
        // side of the deletion are owed and the one between them is not.
        var (holder, dater, between, newest) = await BackUpADeletionAsync();
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None);
        var keyOf = survey.Snapshots.ToDictionary(
            snapshot => snapshot.Fact.SnapshotId, snapshot => snapshot.StoreKey.Value, StringComparer.Ordinal);

        var spare = await DestinationConvergence.ComputeSparesAsync(
            store, opened.Repository,
            [new SetDestinationReference { Ref = "slow", Retention = Policy }],
            setPolicy: null,
            recordFor: _ => null,
            (ulong)CapturedAt(newest).ToUnixTimeMilliseconds(),
            CancellationToken.None);

        Assert.IsNotNull(spare.Spares, spare.Fingerprint);
        Assert.IsTrue(spare.Spares(keyOf[holder.SnapshotId]), "the snapshot holding the deleted file is not spared");
        Assert.IsTrue(spare.Spares(keyOf[dater.SnapshotId]), "the first snapshot without it is not spared");
        Assert.IsTrue(spare.Spares(keyOf[newest.SnapshotId]), "the floor's snapshot is not spared");
        Assert.IsFalse(spare.Spares(keyOf[between.SnapshotId]), "a snapshot that lost nothing is owed to nobody");
    }

    /// <summary>
    /// Four passes' captures, a file deleted between the first two and a file
    /// edited before each of the last two.
    /// </summary>
    /// <returns>The snapshots in capture order: the holder, the dater, the one between and the newest.</returns>
    private async Task<(SnapshotFact Holder, SnapshotFact Dater, SnapshotFact Between, SnapshotFact Newest)> BackUpADeletionAsync()
    {
        await BackUpAsync(Day(1));
        File.Delete(Path.Combine(SourceRoot, "gone.txt"));
        await BackUpAsync(Day(2));
        File.WriteAllText(Path.Combine(SourceRoot, "edited.txt"), "day three");
        await BackUpAsync(Day(3));
        File.WriteAllText(Path.Combine(SourceRoot, "edited.txt"), "day four");
        await BackUpAsync(Day(4));

        var snapshots = await SnapshotsAsync(new LocalFileSystemObjectStore(RepoPath));
        Assert.HasCount(4, snapshots);
        return (snapshots[3], snapshots[2], snapshots[1], snapshots[0]);
    }

    /// <summary>
    /// Appends a root folder of files for each list of names, every one split
    /// across as many manifests as a small budget makes of it, under a writer
    /// of its own.
    /// </summary>
    /// <returns>Each folder's head manifest, in the order given.</returns>
    private async Task<IReadOnlyList<ObjectId>> WriteFoldersAsync(
        IObjectStore store, OpenedRepository repository, IReadOnlyList<IReadOnlyList<string>> folders)
    {
        var generation = new KeyGeneration((uint)Math.Max(
            repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value));
        var builder = new ManifestBuilder(
            repository.RepositoryId, WriterId.FromBytes(Enumerable.Repeat((byte)0x77, 16).ToArray()), generation,
            repository.Keys, store, new MonotonicBlobCounterAllocator(1), SpoolDirectory,
            BlobWriteProfile.LocalDefault, repository.EffectiveFormatVersion);

        var heads = new List<ObjectId>();
        await using (builder.ConfigureAwait(false))
        {
            foreach (var names in folders)
            {
                heads.Add(await TreeChainWriter.WriteAsync(
                    builder,
                    [.. names.Select(name => new TreeEntry(
                        System.Text.Encoding.UTF8.GetBytes(name), ObjectId.FromBytes(new byte[32]), EntryKind.File))],
                    "/"u8.ToArray(), NameNormalisation.Unknown, EntryMetadata.Empty, CancellationToken.None,
                    shardBudget: 256));
            }

            await builder.FlushAsync(CancellationToken.None);
        }

        return heads;
    }

    /// <summary>The snapshots a store holds, newest first.</summary>
    private static async Task<IReadOnlyList<SnapshotFact>> SnapshotsAsync(IObjectStore store)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None);
        Assert.IsEmpty(survey.Undecodable);
        return [.. survey.Snapshots.Select(snapshot => snapshot.Fact)];
    }

    private static DateTimeOffset CapturedAt(SnapshotFact snapshot) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)snapshot.CapturedAtUnixMilliseconds);

    /// <summary>Pass <paramref name="day"/>'s clock, a day after the one before so each pass finds its set due.</summary>
    private static DateTimeOffset Day(int day) => Day1.AddDays(day - 1);

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private async Task<RetentionReport> RunAsync(LocalFileSystemObjectStore store, DateTimeOffset now)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, opened.Repository, Policy, [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name), _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId), apply: false,
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

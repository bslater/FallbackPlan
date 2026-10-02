using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index;
using FallbackPlan.Retention;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A deletion a person asked for reaches the local destinations by the
/// ordinary fan-out (FR-GC-013, ADR-0080). A destination with retention rules
/// already converges, and its keep-set leaves the requested snapshot out. One
/// without rules gets a whole copy and never drops anything (ADR-0034 §6), so
/// while a request is pending it converges as well, under a keep-set of
/// everything but what was requested. A converge records the staging sequence
/// it began at, which is what tells the replication gate the destination has
/// been brought in line since the request.
/// </summary>
[TestClass]
public sealed class SnapshotDeletionFanOutTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-deletion-fanout", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "deletion-fanout-passphrase!!";
    private static readonly string SetId = new('c', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string RepoPath => Path.Combine(ArchivesRoot, SetId);
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");
    private string VaultPath => Path.Combine(_root, "vault");
    private string LatePath => Path.Combine(_root, "late");

    public SnapshotDeletionFanOutTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);
        Directory.CreateDirectory(VaultPath);
        Directory.CreateDirectory(LatePath);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day one content");
        WriteConfiguration(withLateDestination: false);
    }

    [TestMethod]
    public async Task FanOut_ADestinationWithNoRulesWhileADeletionIsPending_DropsTheSnapshotAndRecordsTheConverge()
    {
        await BackUpThreeAsync();
        var vault = Replica(VaultPath);
        Assert.HasCount(3, await ListAsync(vault, "snapshots/"));

        var (request, target) = await RequestMiddleAsync();

        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day four content");
        await BackUpAsync(Day1.AddDays(3));

        // The new snapshot arrived and the requested one went, by the same
        // pass, and what is left is still a whole archive.
        var held = await ListAsync(vault, "snapshots/");
        Assert.HasCount(3, held);
        Assert.DoesNotContain(target.StoreKey.Value, held);
        await AssertWalksCleanAsync(vault, expectedSnapshots: 3);

        var row = DestinationSyncStore.Open(StateDirectory).Find(SetId, "vault");
        Assert.IsNotNull(row);
        Assert.IsGreaterThanOrEqualTo(request.Generation, row.ConvergedSequence);
    }

    [TestMethod]
    public async Task FanOut_ADestinationWithNoRulesAndNothingPending_GetsTheWholeCopyAsBefore()
    {
        // No rule, no request: nothing is ever dropped there, and nothing
        // claims a converge that did not happen.
        await BackUpThreeAsync();

        Assert.HasCount(3, await ListAsync(Replica(VaultPath), "snapshots/"));
        Assert.AreEqual(0UL, DestinationSyncStore.Open(StateDirectory).Find(SetId, "vault")!.ConvergedSequence);
    }

    [TestMethod]
    public async Task FanOut_ADestinationThatNeverHeldTheSnapshot_IsNotSentItWhileTheDeletionIsPending()
    {
        await BackUpThreeAsync();
        var (_, target) = await RequestMiddleAsync();

        // A destination added after the request: a whole copy would carry the
        // snapshot a person has asked to delete to somewhere it never was.
        WriteConfiguration(withLateDestination: true);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day four content");
        await BackUpAsync(Day1.AddDays(3));

        var late = Replica(LatePath);
        var held = await ListAsync(late, "snapshots/");
        Assert.HasCount(3, held);
        Assert.DoesNotContain(target.StoreKey.Value, held);
        await AssertWalksCleanAsync(late, expectedSnapshots: 3);
    }

    [TestMethod]
    public async Task Retention_OnceTheDestinationHasConverged_TakesTheSnapshotOutOfStaging()
    {
        await BackUpThreeAsync();
        var (_, target) = await RequestMiddleAsync();

        // Held while the vault may still hold it.
        var before = await RunAsync(apply: true, Day1.AddDays(2).AddHours(2));
        Assert.IsTrue(Assert.ContainsSingle(before.Held).DeletionPending);
        Assert.HasCount(3, await ListAsync(new LocalFileSystemObjectStore(RepoPath), "snapshots/"));

        // The next sync converges the vault, and the next pass lets it go.
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day four content");
        await BackUpAsync(Day1.AddDays(3));
        var after = await RunAsync(apply: true, Day1.AddDays(3).AddHours(1));

        Assert.IsEmpty(after.Held);
        var staging = await ListAsync(new LocalFileSystemObjectStore(RepoPath), "snapshots/");
        Assert.HasCount(3, staging);
        Assert.DoesNotContain(target.StoreKey.Value, staging);
    }

    private async Task<(SnapshotDeletionRequest Request, SurveyedSnapshot Target)> RequestMiddleAsync()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var target = (await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None)).Snapshots[1];
        var sequence = new WriterSequence(new FileSequenceStateStore(Path.Combine(
            StateDirectory, $"sequence-{Convert.ToHexStringLower(opened.Repository.RepositoryId.ToArray())}.txt")));

        var request = await SnapshotDeletion.RequestAsync(
            store, opened.Repository, Writer, sequence, [target], "cli",
            (ulong)Day1.AddDays(2).AddHours(1).ToUnixTimeMilliseconds(), CancellationToken.None, opened.Reclaim);
        return (request, target);
    }

    private async Task<RetentionReport> RunAsync(bool apply, DateTimeOffset now)
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, opened.Repository, policy: null,
            [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name),
            _ => TrimVerification.None,
            Writer, apply, (ulong)now.ToUnixTimeMilliseconds(), CancellationToken.None, "docs",
            reclaim: opened.Reclaim);
    }

    private WriterId Writer => WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId);

    private void WriteConfiguration(bool withLateDestination) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = VaultPath,
            },
            new DestinationConfiguration
            {
                Id = new string('2', 32), Name = "late", Kind = DestinationKind.LocalPath, Path = LatePath,
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
                Destinations = withLateDestination
                    ? [new SetDestinationReference { Ref = "vault" }, new SetDestinationReference { Ref = "late" }]
                    : [new SetDestinationReference { Ref = "vault" }],
            },
        ],
    }.Save(Path.Combine(StateDirectory, "config.json"));

    private async Task BackUpThreeAsync()
    {
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(Day1.AddDays(2));
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private static LocalFileSystemObjectStore Replica(string destinationPath) =>
        new(Directory.GetDirectories(destinationPath).Single());

    private static async Task AssertWalksCleanAsync(LocalFileSystemObjectStore replica, int expectedSnapshots)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(replica, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;

        var survey = await StagingMark.SurveyAsync(replica, repository, CancellationToken.None);
        Assert.HasCount(expectedSnapshots, survey.Snapshots);
        Assert.IsEmpty(survey.Undecodable);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, replica, opened.Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var (_, unwalkable) = await StagingMark.MarkAsync(reader, survey.Snapshots, CancellationToken.None);
        Assert.IsEmpty(unwalkable);
    }

    private static async Task<List<string>> ListAsync(IObjectStore store, string prefix)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
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

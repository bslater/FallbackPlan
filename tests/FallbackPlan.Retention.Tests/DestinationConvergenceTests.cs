using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Replication;
using FallbackPlan.Retention;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// Retention against local-path destinations (FR-GC-010, ADR-0009 Amendment
/// 4): two destinations of one set hold different snapshot ranges under
/// different overrides, each a valid archive, converged by the same fan-out
/// pass that copies — so replication and retention cannot disagree.
/// </summary>
[TestClass]
public sealed class DestinationConvergenceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-convergence-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "convergence-tests-passphrase!!";
    private static readonly string SetId = new('a', 32);

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string RepoPath => Path.Combine(ArchivesRoot, SetId);
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");
    private string WidePath => Path.Combine(_root, "wide");
    private string NarrowPath => Path.Combine(_root, "narrow");

    public DestinationConvergenceTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        Directory.CreateDirectory(WidePath);
        Directory.CreateDirectory(NarrowPath);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "convergence fodder");

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('1', 32), Name = "wide",
                    Kind = DestinationKind.LocalPath, Path = WidePath,
                },
                new DestinationConfiguration
                {
                    Id = new string('2', 32), Name = "narrow",
                    Kind = DestinationKind.LocalPath, Path = NarrowPath,
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
                    Destinations =
                    [
                        new SetDestinationReference { Ref = "wide" },
                        new SetDestinationReference
                        {
                            Ref = "narrow",
                            Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                        },
                    ],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    [TestMethod]
    public async Task FanOut_OneSetTwoPolicies_EachDestinationHoldsItsOwnKeepSetAndBothStayValid()
    {
        // Three days, three snapshots, each pass fanning out to both
        // destinations — the narrow one converging under its override as it
        // goes, dropping what its policy no longer keeps.
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(day1.AddDays(2));

        var repositoryId = Directory.GetDirectories(WidePath).Single();
        var wide = new LocalFileSystemObjectStore(repositoryId);
        var narrow = new LocalFileSystemObjectStore(Directory.GetDirectories(NarrowPath).Single());

        // The wide replica holds the whole archive; the narrow one exactly
        // its keep-set — different ranges from one staging archive, which is
        // FR-GC-010's acceptance shape.
        Assert.HasCount(3, await ListAsync(wide, "snapshots/"));
        Assert.HasCount(1, await ListAsync(narrow, "snapshots/"));

        // Nothing staging-only crossed: destinations are converged, never
        // collected, and a tombstone at a replica would be an instruction
        // nobody there may act on.
        Assert.IsEmpty(await ListAsync(wide, "tombstones/"));

        // Both replicas are valid archives: they open with the passphrase
        // and every snapshot's closure walks clean.
        await AssertWalksCleanAsync(wide, expectedSnapshots: 3);
        await AssertWalksCleanAsync(narrow, expectedSnapshots: 1);

        // Convergence is idempotent and never re-pushes what the policy
        // dropped: a second pass over the same state moves and removes
        // nothing (the watermark the plan asked for, by construction).
        using var passphrase = Passphrase.Create(PassphraseText);
        using var staging = await RepositoryLifecycle.OpenAsync(
            new LocalFileSystemObjectStore(RepoPath), passphrase, CancellationToken.None);
        var keeps = await DestinationConvergence.ComputeKeepsAsync(
            new LocalFileSystemObjectStore(RepoPath), staging,
            new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
            day1.AddDays(2).AddHours(1), CancellationToken.None);
        Assert.IsNotNull(keeps);

        var again = await StoreToStoreCopier.ConvergeAsync(
            new LocalFileSystemObjectStore(RepoPath), narrow, keeps, CancellationToken.None);
        Assert.AreEqual(0, again.Copied);
        Assert.AreEqual(0, again.Deleted);
    }

    private static async Task AssertWalksCleanAsync(LocalFileSystemObjectStore replica, int expectedSnapshots)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(replica, passphrase, CancellationToken.None);

        var survey = await StagingMark.SurveyAsync(replica, repository, CancellationToken.None);
        Assert.HasCount(expectedSnapshots, survey.Snapshots);
        Assert.IsEmpty(survey.Undecodable);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, replica);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var (_, unwalkable) = await StagingMark.MarkAsync(reader, survey.Snapshots, CancellationToken.None);
        Assert.IsEmpty(unwalkable);
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, passphrase, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private static async Task<List<string>> ListAsync(LocalFileSystemObjectStore store, string prefix)
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

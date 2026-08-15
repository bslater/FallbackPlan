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
        var convergence = await DestinationConvergence.ComputeKeepsAsync(
            new LocalFileSystemObjectStore(RepoPath), staging,
            new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
            day1.AddDays(2).AddHours(1), CancellationToken.None);
        Assert.IsNull(convergence.Refusal);
        Assert.IsNotNull(convergence.Keeps);

        var again = await StoreToStoreCopier.ConvergeAsync(
            new LocalFileSystemObjectStore(RepoPath), narrow, convergence.Keeps, CancellationToken.None);
        Assert.AreEqual(0, again.Copied);
        Assert.AreEqual(0, again.Deleted);
    }

    [TestMethod]
    public async Task Converge_AKeyOnlyTheDestinationHolds_SurvivesBecauseStagingCannotVouchForIt()
    {
        // After a staging trim the replica holds data blobs staging no longer
        // lists — possibly the only copies anywhere. A keep filter computed
        // from staging answers "drop" for a key it has never heard of, so the
        // drop half must condemn only keys the source itself lists
        // (ADR-0034 §6).
        var source = new LocalFileSystemObjectStore(Path.Combine(_root, "converge-source"));
        var destination = new LocalFileSystemObjectStore(Path.Combine(_root, "converge-destination"));

        await PlantAsync(source, "repository-format");
        await PlantAsync(source, "blobs/data/aa/kept");
        await PlantAsync(source, "blobs/data/bb/dropped");
        await PlantAsync(destination, "repository-format");
        await PlantAsync(destination, "blobs/data/aa/kept");
        await PlantAsync(destination, "blobs/data/bb/dropped");
        await PlantAsync(destination, "blobs/data/cc/trimmed");

        // The filter keeps one blob; both other keys answer "drop" — the
        // source listing is the only thing standing between the trimmed key
        // and deletion.
        static bool Keeps(string key) =>
            !key.StartsWith("blobs/", StringComparison.Ordinal) || key is "blobs/data/aa/kept";

        var outcome = await StoreToStoreCopier.ConvergeAsync(source, destination, Keeps, CancellationToken.None);

        // Exactly the source-listed dropped key went; the trimmed one stays.
        Assert.AreEqual(1, outcome.Deleted);
        var held = await ListAsync(destination, "blobs/");
        CollectionAssert.Contains(held, "blobs/data/aa/kept");
        CollectionAssert.Contains(held, "blobs/data/cc/trimmed");
        CollectionAssert.DoesNotContain(held, "blobs/data/bb/dropped");
    }

    private static async Task PlantAsync(LocalFileSystemObjectStore store, string key)
    {
        var put = await store.PutAsync(
            ObjectKey.Parse(key),
            _ => ValueTask.FromResult<Stream>(new MemoryStream("planted"u8.ToArray())),
            PutConditions.None,
            CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);
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

    [TestMethod]
    public async Task ComputeKeeps_AnUndecodableSnapshot_SaysWhyRatherThanAnsweringNull()
    {
        // The whole copy is the right answer here — a keep decision resting on
        // a graph nobody can walk would drop objects it merely could not see —
        // but it used to be spelled exactly like "this destination has no
        // retention rules", so the fault was invisible.
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(day1);

        // Scribble over a snapshot object: present, listed, and undecodable.
        var snapshot = Directory
            .EnumerateFiles(Path.Combine(RepoPath, "snapshots"), "*", SearchOption.AllDirectories)
            .First();
        await File.WriteAllBytesAsync(snapshot, new byte[64]);

        using var passphrase = Passphrase.Create(PassphraseText);
        using var staging = await RepositoryLifecycle.OpenAsync(
            new LocalFileSystemObjectStore(RepoPath), passphrase, CancellationToken.None);

        var convergence = await DestinationConvergence.ComputeKeepsAsync(
            new LocalFileSystemObjectStore(RepoPath), staging,
            new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
            day1.AddHours(1), CancellationToken.None);

        Assert.IsNull(convergence.Keeps, "a damaged graph must still take the conservative whole copy");
        Assert.AreEqual(ConvergenceRefusal.UndecodableSnapshots, convergence.Refusal);
    }

    [TestMethod]
    public async Task FanOut_AStagingGraphThatWillNotWalk_NamesTheDestinationAndClearsWhenRepaired()
    {
        // The consequence worth telling someone about: the destination's
        // retention policy silently stops being applied, so it keeps history
        // it was configured to drop — and keeps keeping it, every pass, until
        // staging is repaired.
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(day1);

        var snapshot = Directory
            .EnumerateFiles(Path.Combine(RepoPath, "snapshots"), "*", SearchOption.AllDirectories)
            .First();
        var original = await File.ReadAllBytesAsync(snapshot);
        await File.WriteAllBytesAsync(snapshot, new byte[64]);

        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two");
        await BackUpAsync(day1.AddDays(1));

        var notices = NoticeStore.Open(StateDirectory);
        var raised = notices.Unacknowledged.Where(notice =>
            notice.Key.StartsWith("convergence-unavailable:", StringComparison.Ordinal)).ToList();
        Assert.IsNotEmpty(raised, "a destination that silently stopped converging must be named");
        Assert.Contains("narrow", string.Join(" ", raised.Select(notice => notice.Message)), StringComparison.Ordinal);
        Assert.Contains(
            "not being applied", string.Join(" ", raised.Select(notice => notice.Message)), StringComparison.Ordinal);

        // Repaired: the next pass converges normally and withdraws the notice
        // rather than leaving a warning about a problem that has gone.
        await File.WriteAllBytesAsync(snapshot, original);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three");
        await BackUpAsync(day1.AddDays(2));

        Assert.IsEmpty(
            NoticeStore.Open(StateDirectory).Unacknowledged.Where(notice =>
                notice.Key.StartsWith("convergence-unavailable:", StringComparison.Ordinal)),
            "the notice must not outlive the condition");
    }

    [TestMethod]
    public async Task FanOut_ADestinationWipedBetweenPasses_IsNamedRatherThanQuietlyReSeeded()
    {
        // The failure fan-out was blind to. The destination declares what it
        // holds, we push the difference, the pass reports success — so a
        // destination that ate the backup is simply re-filled and its next
        // ledger row is indistinguishable from a healthy one.
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(day1);

        var replicaRoot = Directory.GetDirectories(WidePath).Single();
        Assert.IsNotEmpty(Directory.GetFileSystemEntries(replicaRoot));
        Directory.Delete(replicaRoot, recursive: true);

        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two");
        await BackUpAsync(day1.AddDays(1));

        var notice = NoticeStore.Open(StateDirectory).Unacknowledged.SingleOrDefault(entry =>
            entry.Key.StartsWith("destination-shortfall:", StringComparison.Ordinal));
        Assert.IsNotNull(notice, "a destination that lost the backup must say so");
        Assert.Contains("wide", notice.Message, StringComparison.Ordinal);
        Assert.Contains("lost this set's data", notice.Message, StringComparison.Ordinal);

        // Re-seeded, so the copy is current again — the ledger is not marked
        // failed, because failing it would start the back-off that delays the
        // very sync that repairs things.
        var record = DestinationSyncStore.Open(StateDirectory).Find(SetId, "wide")!;
        Assert.AreEqual(DestinationSyncState.InSync, record.State);
        Assert.AreEqual(0, record.ConsecutiveFailures);
    }

    [TestMethod]
    public async Task FanOut_AnOrdinaryIncrementalPass_RaisesNoShortfall()
    {
        // The false positive that killed the obvious signal: an ordinary
        // second pass copies objects while the destination keeps everything it
        // already had. "Objects copied" is not evidence of loss; "held
        // nothing" is.
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two");
        await BackUpAsync(day1.AddDays(1));

        Assert.IsEmpty(NoticeStore.Open(StateDirectory).Unacknowledged.Where(entry =>
            entry.Key.StartsWith("destination-shortfall:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FanOut_TheVeryFirstPass_RaisesNoShortfall()
    {
        // A destination that has never held anything has lost nothing. The
        // check rests on there having been a prior success.
        await BackUpAsync(new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero));

        Assert.IsEmpty(NoticeStore.Open(StateDirectory).Unacknowledged.Where(entry =>
            entry.Key.StartsWith("destination-shortfall:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AgentPass_AReplicaBlobThatRottedSinceTheLastSync_IsFoundByTheSweep()
    {
        // The gap this closes. Nothing read these bytes between syncs: the
        // range challenge samples a handful of objects at sync time and says
        // nothing afterwards, so rot sat undiscovered until a restore needed
        // the object — the worst possible moment to learn of it.
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(day1);

        var replicaRoot = Directory.GetDirectories(WidePath).Single();
        var victim = Directory
            .EnumerateFiles(Path.Combine(replicaRoot, "blobs"), "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .First();
        var bytes = await File.ReadAllBytesAsync(victim);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(victim, bytes);

        // A pass a fortnight later: past the sweep's default cadence, with no
        // new backup and therefore no sync and no challenge. The sweep is the
        // only thing that looks.
        await BackUpAsync(day1.AddDays(14));

        var notice = NoticeStore.Open(StateDirectory).Unacknowledged.SingleOrDefault(entry =>
            entry.Key.StartsWith("deep-verify-failed:", StringComparison.Ordinal));
        Assert.IsNotNull(notice, "a rotted replica object must be found without anyone asking");
        Assert.Contains("no longer match what was sealed", notice.Message, StringComparison.Ordinal);

        var record = DestinationSyncStore.Open(StateDirectory).Find(SetId, "wide")!;
        Assert.AreEqual(DestinationSyncState.Failed, record.State, "a damaged destination stops counting as protection");
        Assert.IsNotNull(record.SweptAt);
    }

    [TestMethod]
    public async Task AgentPass_AnIntactReplica_RecordsACompletedCircuitAndNoFinding()
    {
        // The other half: the sweep must be able to say "every stored object
        // was confirmed, as of this moment" — which is the claim that makes it
        // worth running at all.
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(day1);
        await BackUpAsync(day1.AddDays(14));

        Assert.IsEmpty(NoticeStore.Open(StateDirectory).Unacknowledged.Where(entry =>
            entry.Key.StartsWith("deep-verify-failed:", StringComparison.Ordinal)));

        var record = DestinationSyncStore.Open(StateDirectory).Find(SetId, "wide")!;
        Assert.IsNotNull(record.SweepCompletedAt, "a small archive finishes its circuit in one segment");
        Assert.IsNull(record.SweepCursor, "a closed circuit starts over rather than resuming");
        Assert.AreEqual(DestinationSyncState.InSync, record.State);
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

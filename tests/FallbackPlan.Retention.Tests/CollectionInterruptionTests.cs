using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A collection pass over the staging archive, cut in front of each write it
/// makes, leaves every snapshot still listed restoring whole, and the pass
/// run again leaves exactly the store an uncut pass leaves (FR-GC-006,
/// ADR-0009).
/// </summary>
/// <remarks>
/// <para>
/// The pass measured here does every kind of write a pass does. It tombstones
/// a newly expired snapshot and the blobs only it reaches, deletes a snapshot
/// an earlier pass condemned and the blobs only that snapshot reached, and
/// clears the tombstones of objects deleted a generation ago. Five daily
/// backups with a pass after the third and the fourth lay that world down,
/// and each day's backup carries a file of its own content, so each snapshot
/// reaches a data blob no other snapshot does.
/// </para>
/// <para>
/// A cut is a death, not a refusal (<see cref="DiesBeforeWriteStore"/>). The
/// pass is cut in front of its first write, its second, and so on to its
/// last, each time from the same world. Two things must hold at every cut.
/// Every snapshot the store still lists restores byte for byte, through a
/// reader built after the cut. And the next pass, which is what the service
/// runs after a restart, finishes the work, leaving the store the uncut pass
/// leaves, with nothing stranded and nothing deleted twice.
/// </para>
/// </remarks>
[TestClass]
public sealed class CollectionInterruptionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-collection-interruption-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "collection-cut-tests-passphrase!";
    private static readonly string SetId = new('c', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MeasuredPassAt = Day1.AddDays(4).AddHours(1);

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, SetId);

    private string PristinePath => Path.Combine(_root, "pristine");

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    public CollectionInterruptionTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);

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
                    Retention = Policy,
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    [TestMethod]
    public async Task ACollectionPassCutInFrontOfEachWrite_LeavesEveryListedSnapshotRestorable_AndTheNextPassFinishesIt()
    {
        var days = await LiveUpToTheMeasuredPassAsync();
        CutOracle.CopyDirectory(RepoPath, PristinePath);

        var (uncut, asked) = await RunPassAsync(cut: int.MaxValue);
        var finished = CutOracle.StoredKeys(RepoPath);

        // The premise: this one pass writes every kind of thing a pass
        // writes, so its cuts reach every kind of step. It also puts again
        // the tombstones an earlier pass laid for what it still condemns,
        // which change nothing and are cut in front of all the same.
        Assert.IsGreaterThanOrEqualTo(2, uncut!.TombstonesWritten, "the pass condemned nothing new");
        Assert.HasCount(1, uncut.Swept!.DeletedSnapshots, "the pass deleted no snapshot");
        Assert.IsGreaterThanOrEqualTo(2, uncut.Swept.Deleted, "the pass deleted no blob beside the snapshot");
        Assert.IsGreaterThanOrEqualTo(2, uncut.Swept.TombstonesCleared, "the pass cleared no tombstone");
        Assert.IsTrue(
            asked.All(write => write.StartsWith("put tombstones/", StringComparison.Ordinal)
                || write.StartsWith("delete snapshots/", StringComparison.Ordinal)
                || write.StartsWith("delete blobs/", StringComparison.Ordinal)
                || write.StartsWith("delete tombstones/", StringComparison.Ordinal)),
            "the pass made a write this test does not account for: " + string.Join(" | ", asked));

        // Where the store settles: the uncut pass and one more after it. A
        // tombstone's tail runs from its own eligibility, not from the
        // delete, so a pass that finds the object gone clears it at once,
        // and a cut after the delete brings that clear forward a pass.
        await RunPassAsync(cut: int.MaxValue);
        var settled = CutOracle.StoredKeys(RepoPath);

        for (var cut = 1; cut <= asked.Count; cut++)
        {
            var at = $"cut in front of write {cut} of {asked.Count}, {asked[cut - 1]}";
            CutOracle.ResetTo(PristinePath, RepoPath);

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await RunPassAsync(cut), at);
            await CutOracle.AssertEveryListedSnapshotRestoresAsync(
                RepoPath, StateDirectory, PassphraseText, days, Files, Path.Combine(_root, "restored"), at);

            // The next pass finishes the work: every snapshot and blob the
            // uncut pass deleted is gone, nothing else is, and no tombstone
            // is left that the uncut pass would not have left.
            var (resumed, _) = await RunPassAsync(cut: int.MaxValue);
            Assert.IsEmpty(
                resumed!.Swept!.Findings.Where(finding => !finding.StartsWith("deferred:", StringComparison.Ordinal)),
                $"{at}: the next pass found {string.Join(" | ", resumed.Swept.Findings)}");
            var left = CutOracle.StoredKeys(RepoPath);
            AssertSameStore(Objects(finished), Objects(left), $"{at}: the next pass left other objects");
            Assert.IsEmpty(
                Tombstones(left).Except(Tombstones(finished)),
                $"{at}: the next pass left a tombstone the uncut pass did not");

            await RunPassAsync(cut: int.MaxValue);
            AssertSameStore(settled, CutOracle.StoredKeys(RepoPath), $"{at}: the store did not settle where the uncut one did");
        }
    }

    private static IEnumerable<string> Tombstones(IEnumerable<string> keys) =>
        keys.Where(key => key.StartsWith("tombstones/", StringComparison.Ordinal));

    private static List<string> Objects(IEnumerable<string> keys) =>
        [.. keys.Where(key => !key.StartsWith("tombstones/", StringComparison.Ordinal))];

    private static void AssertSameStore(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string message) =>
        Assert.IsTrue(
            actual.SequenceEqual(expected),
            $"{message}. Only here: {string.Join(", ", actual.Except(expected))}. Missing: {string.Join(", ", expected.Except(actual))}");

    private static RetentionConfiguration Policy => new() { KeepDaily = 1, MinGenerations = 1 };

    private WriterId Writer => WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId);

    /// <summary>
    /// Five daily backups, with a pass after the third that condemns the
    /// first two and one after the fourth that deletes them and condemns the
    /// third. The pass this test measures, after the fifth, then condemns the
    /// fourth, deletes the third, and clears what the first two left.
    /// </summary>
    /// <returns>Each snapshot's day, by snapshot id.</returns>
    private async Task<Dictionary<string, int>> LiveUpToTheMeasuredPassAsync()
    {
        var days = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var day = 0; day < 5; day++)
        {
            days[await BackUpAsync(day)] = day;

            if (day is 2 or 3)
            {
                await RunPassAsync(int.MaxValue, Day1.AddDays(day).AddHours(1));
            }
        }

        return days;
    }

    /// <summary>A day's backup, returning the snapshot it published.</summary>
    private async Task<string> BackUpAsync(int day)
    {
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), Text(day));
        File.WriteAllBytes(Path.Combine(SourceRoot, "bulk.bin"), Bulk(day));

        var before = Directory.Exists(RepoPath) ? await CutOracle.ListedSnapshotsAsync(RepoPath, PassphraseText) : [];
        using (var passphrase = Passphrase.Create(PassphraseText))
        {
            var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, Day1.AddDays(day), CancellationToken.None);
            Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
        }

        return (await CutOracle.ListedSnapshotsAsync(RepoPath, PassphraseText)).Except(before).Single();
    }

    /// <summary>One apply pass over the archive, through a store that dies in front of write <paramref name="cut"/>.</summary>
    private async Task<(RetentionReport? Report, IReadOnlyList<string> Asked)> RunPassAsync(int cut, DateTimeOffset? now = null)
    {
        var plain = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(plain, PassphraseText, CancellationToken.None);
        using var process = new CancellationTokenSource();
        var store = new DiesBeforeWriteStore(plain, cut, process);

        var sync = DestinationSyncStore.Open(StateDirectory);
        var report = await RetentionRunner.RunAsync(
            store, opened.Repository, Policy, [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name), _ => TrimVerification.None, Writer, apply: true,
            (ulong)(now ?? MeasuredPassAt).ToUnixTimeMilliseconds(), process.Token, reclaim: opened.Reclaim);

        return (report, store.Asked);
    }

    private static IReadOnlyDictionary<string, byte[]> Files(int day) => new Dictionary<string, byte[]>
    {
        ["a.txt"] = System.Text.Encoding.UTF8.GetBytes(Text(day)),
        ["bulk.bin"] = Bulk(day),
    };

    private static string Text(int day) => $"day {day} content";

    private static byte[] Bulk(int day)
    {
        var bytes = new byte[200 * 1024];
        new Random(day + 1).NextBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

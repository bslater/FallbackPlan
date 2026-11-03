using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// How the service tells a backup what changed since the set's last one,
/// from the change journal (ADR-0188, FR-SNP-012). A backup keeps where the
/// journal stood before it walked; the next asks the journal from there, and
/// walks only what it names. It walks in full where it has nothing to ask
/// from, where the journal cannot say, where the set's folders changed, where
/// a full walk was asked for, and once a day whatever the journal says. A
/// backup that kept no place, or whose kept place is damaged or names an
/// earlier backup than the set's last, leaves the next nothing to ask from.
/// </summary>
/// <remarks>
/// The journal here is scripted, so this runs on every platform: what the
/// service does with an answer is the same whichever journal gives it. The
/// Windows journal itself is held by <c>WindowsChangeJournalTests</c>.
/// </remarks>
[TestClass]
public sealed class ChangeJournalRunTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly ScriptedJournal _journal = new();

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task AFirstBackup_WalksInFull_AndAsksNothing()
    {
        WriteTree();
        await using var runtime = await StartAsync();

        var first = await BackUpAsync(runtime, DateTimeOffset.Now);

        Assert.IsEmpty(_journal.Asked);
        Assert.AreEqual("2 file(s), 0 unchanged", first.Detail);
    }

    [TestMethod]
    public async Task ABackupAfterAnother_AsksFromWhereThatOneStarted_AndWalksOnlyWhatChanged()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        var kept = _journal.Given.Single();

        _harness.WriteSourceFile("docs/a.txt", "alpha, changed");
        _journal.Answer = _ => [new ChangedFolder(_harness.SourceRoot, "docs")];
        var second = await BackUpAsync(runtime, start.AddHours(1));

        Assert.AreEqual(kept, Assert.ContainsSingle(_journal.Asked), "from where the first backup stood before it walked");
        Assert.AreEqual("1 file(s), 0 unchanged, 1 folder(s) not walked", second.Detail);
    }

    [TestMethod]
    public async Task AChangeTheJournalDoesNotName_WaitsForAFullWalk()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        // Told nothing changed, the run takes photos as it was: that is the
        // trust a journal is given, and why a full walk stays on schedule.
        _harness.WriteSourceFile("photos/b.jpg", "bravo, changed where the journal says nothing");
        var told = await BackUpAsync(runtime, start.AddHours(1));
        Assert.AreEqual("0 file(s), 0 unchanged, 2 folder(s) not walked", told.Detail);

        // A day after the last full walk, not after the last backup: a set
        // backed up hourly is still walked in full daily.
        var walked = await BackUpAsync(runtime, start.AddDays(1).AddMinutes(30));
        Assert.AreEqual("2 file(s), 1 unchanged", walked.Detail, "the full walk finds it");
    }

    [TestMethod]
    public async Task AJournalThatCannotSay_WalksInFull()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        _harness.WriteSourceFile("docs/a.txt", "alpha, changed");
        _journal.Answer = _ => null;
        var second = await BackUpAsync(runtime, start.AddHours(1));

        Assert.HasCount(1, _journal.Asked);
        Assert.AreEqual("2 file(s), 1 unchanged", second.Detail);
    }

    [TestMethod]
    public async Task ADayAfterTheLastFullWalk_WalksInFull_AndAsksAgainAfter()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        var due = await BackUpAsync(runtime, start.AddDays(1));
        Assert.IsEmpty(_journal.Asked, "a full walk is due, whatever the journal would say");
        Assert.AreEqual("2 file(s), 2 unchanged", due.Detail);

        var after = await BackUpAsync(runtime, start.AddDays(1).AddHours(1));
        Assert.AreEqual(_journal.Given[1], Assert.ContainsSingle(_journal.Asked), "from where the full walk stood");
        Assert.AreEqual("0 file(s), 0 unchanged, 2 folder(s) not walked", after.Detail);
    }

    [TestMethod]
    public async Task ABackupAskedForInFull_WalksInFull()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        var full = await BackUpAsync(runtime, start.AddHours(1), full: true);

        Assert.IsEmpty(_journal.Asked);
        Assert.AreEqual("2 file(s), 0 unchanged", full.Detail, "a full backup reads every file again");
    }

    [TestMethod]
    public async Task AVolumeKeepingNoJournal_WalksInFullEveryTime()
    {
        WriteTree();
        _journal.Keeps = false;
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        var second = await BackUpAsync(runtime, start.AddHours(1));

        Assert.IsEmpty(_journal.Asked);
        Assert.AreEqual("2 file(s), 2 unchanged", second.Detail);
        var kept = Path.Combine(_harness.StateDirectory, "change-journal");
        Assert.IsFalse(Directory.Exists(kept) && Directory.EnumerateFiles(kept).Any(), "nothing is kept where no place was found");
    }

    [TestMethod]
    public async Task ABackupThatKeptNoPlace_LeavesTheNextNothingToAskFrom()
    {
        WriteTree();
        await using (var runtime = await StartAsync())
        {
            await BackUpAsync(runtime, DateTimeOffset.Now);
        }

        // The backup between finds no place of its own, though it may still
        // ask from the one the first kept, which is its last backup's.
        var start = DateTimeOffset.Now.AddHours(1);
        _journal.Keeps = false;
        await using (var runtime = await StartAsync())
        {
            await BackUpAsync(runtime, start);
        }

        // The place the first backup kept is older than the backup between,
        // which kept none, so it no longer says what changed since the last.
        var asked = _journal.Asked.Count;
        _journal.Keeps = true;
        await using (var runtime = await StartAsync())
        {
            await BackUpAsync(runtime, start.AddHours(1));
        }

        Assert.HasCount(asked, _journal.Asked, "the third backup had nothing to ask from");
    }

    [TestMethod]
    public async Task APlaceKeptForAnEarlierBackup_IsNotUsed()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        var kept = Assert.ContainsSingle(Directory.GetFiles(Path.Combine(_harness.StateDirectory, "change-journal")));
        var earlier = File.ReadAllText(kept);
        await BackUpAsync(runtime, start.AddHours(1));
        var asked = _journal.Asked.Count;

        // As a backup another writer took would leave it: the place names a
        // backup that is no longer the set's last.
        File.WriteAllText(kept, earlier);
        var third = await BackUpAsync(runtime, start.AddHours(2));

        Assert.HasCount(asked, _journal.Asked, "nothing asked from a place kept for another backup");
        Assert.AreEqual("2 file(s), 2 unchanged", third.Detail);
    }

    [TestMethod]
    public async Task ADamagedKeptPlace_IsNoPlace()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        var kept = Assert.ContainsSingle(Directory.GetFiles(Path.Combine(_harness.StateDirectory, "change-journal")));
        File.WriteAllText(kept, "{ not what was kept");
        var second = await BackUpAsync(runtime, start.AddHours(1));

        Assert.IsEmpty(_journal.Asked);
        Assert.AreEqual("2 file(s), 2 unchanged", second.Detail, "walked in full, and failed nothing");
    }

    [TestMethod]
    public async Task ASetWhoseFoldersChanged_WalksInFull()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        var other = Path.Combine(_harness.WorkPath, "other");
        Directory.CreateDirectory(other);
        var path = Path.Combine(_harness.StateDirectory, "config.json");
        var configuration = ClientConfiguration.Load(path);
        (configuration with
        {
            BackupSets =
            [
                configuration.BackupSets.Single() with
                {
                    Roots =
                    [
                        new BackupRootConfiguration { Path = _harness.SourceRoot, Label = "Source" },
                        new BackupRootConfiguration { Path = other, Label = "Other" },
                    ],
                },
            ],
        }).Save(path);
        await BackUpAsync(runtime, start.AddHours(1));

        Assert.IsEmpty(_journal.Asked, "no place was kept for the folder the set now holds");
    }

    [TestMethod]
    public void SnapshotPath_InASetOfOneFolder_IsThePathBelowIt()
    {
        ScanRoot[] roots = [new ScanRoot(@"C:\Data")];

        Assert.AreEqual("docs/q3", ChangeHints.SnapshotPath(roots, new ChangedFolder(@"C:\Data", "docs/q3")));
        Assert.AreEqual(string.Empty, ChangeHints.SnapshotPath(roots, new ChangedFolder(@"C:\Data", string.Empty)));
    }

    [TestMethod]
    public void SnapshotPath_InASetOfSeveralFolders_BeginsWithTheFoldersLabel()
    {
        ScanRoot[] roots = [new ScanRoot(@"C:\Data", "Documents"), new ScanRoot(@"D:\Pictures", "Photos")];

        Assert.AreEqual("Photos/2026/june", ChangeHints.SnapshotPath(roots, new ChangedFolder(@"D:\Pictures", "2026/june")));
        Assert.AreEqual("Documents", ChangeHints.SnapshotPath(roots, new ChangedFolder(@"C:\Data", string.Empty)));
    }

    private void WriteTree()
    {
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("photos/b.jpg", "bravo");
    }

    private async Task<BackupOutcome> BackUpAsync(ServiceRuntime runtime, DateTimeOffset now, bool full = false)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var job = runtime.Jobs.Begin(set.Id, (ulong)now.ToUnixTimeMilliseconds());
        var outcome = await BackupRunner.RunAsync(
            runtime, set, job.Id, now, userInitiated: true, full: full, cancellationToken: Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        return outcome;
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                ChangeJournal = _journal,
            },
            Timeout);
    }

    /// <summary>A journal that answers as the test says, and remembers what it gave and was asked.</summary>
    private sealed class ScriptedJournal : IChangeJournal
    {
        private long _next = 100;

        /// <summary>Whether the volume keeps a journal at all.</summary>
        public bool Keeps { get; set; } = true;

        /// <summary>What it says changed since a place; nothing, unless told.</summary>
        public Func<JournalPosition, IReadOnlyList<ChangedFolder>?> Answer { get; set; } = _ => [];

        /// <summary>Every place it gave, in order.</summary>
        public List<JournalPosition> Given { get; } = [];

        /// <summary>Every place it was asked from, in order.</summary>
        public List<JournalPosition> Asked { get; } = [];

        public JournalPosition? PositionOf(string folder)
        {
            if (!Keeps)
            {
                return null;
            }

            var place = new JournalPosition(@"\\?\Volume{test}\", 7, _next++);
            Given.Add(place);
            return place;
        }

        public IReadOnlyList<ChangedFolder>? ChangedSince(JournalPosition since, IReadOnlyCollection<string> roots)
        {
            Asked.Add(since);
            return Answer(since);
        }
    }
}

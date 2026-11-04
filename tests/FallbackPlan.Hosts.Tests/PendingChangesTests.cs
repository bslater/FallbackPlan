using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// How many changed files a set's next backup will take, as its status says
/// (ADR-0192, FR-SNP-013): counted over the folders the change journal named
/// since the set's last backup, judged as that backup will judge them. Where
/// the next backup will walk in full it says so and why, and counts nothing:
/// no backup yet, no place kept, the set's full walk due, or a journal that
/// cannot say. A set whose backups ask no journal says nothing. A count moves
/// no place the backup asks from. The status gives the last count and asks
/// for another once it is a minute old, never two at once. The files the last
/// backup could not read are waiting too, since the next backup walks their
/// folders to try them again, and where its failures will not read it walks
/// in full and says why; they are read once for a snapshot, not once a count
/// (ADR-0192 Amendment 1).
/// </summary>
/// <remarks>
/// The journal is scripted, so this runs on every platform.
/// </remarks>
[TestClass]
public sealed class PendingChangesTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly ScriptedJournal _journal = new();
    private bool _delivers = true;

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task AFileChangedAndOneNew_InAFolderTheJournalNames_AreWaiting()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        _harness.WriteSourceFile("docs/a.txt", "alpha, changed");
        _harness.WriteSourceFile("docs/new.txt", "new");
        _harness.WriteSourceFile("photos/c.jpg", "not named, so not counted");
        _journal.Answer = _ => [new ChangedFolder(_harness.SourceRoot, "docs")];
        var pending = await CountAsync(runtime, start.AddMinutes(10));

        Assert.AreEqual(2L, pending!.Files);
        Assert.IsNull(pending.WalksInFullBecause);
        Assert.AreEqual(Ms(start.AddMinutes(10)), pending.CountedAt);
    }

    [TestMethod]
    public async Task NothingNamed_NothingIsWaiting()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        var pending = await CountAsync(runtime, start.AddMinutes(10));

        Assert.AreEqual(0L, pending!.Files);
    }

    [TestMethod]
    public async Task ASetNeverBackedUp_WalksInFullNext()
    {
        WriteTree();
        await using var runtime = await StartAsync();

        var pending = await CountAsync(runtime, DateTimeOffset.Now);

        Assert.IsNull(pending!.Files);
        Assert.Contains("no backup yet", pending.WalksInFullBecause!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ASetWhoseFullWalkIsDue_WalksInFullNext()
    {
        WriteTree();
        WalkInFull("every 2h");
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        var pending = await CountAsync(runtime, start.AddHours(3));

        Assert.IsNull(pending!.Files);
        Assert.Contains("full walk is due", pending.WalksInFullBecause!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AJournalThatCannotSay_WalksInFullNext()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        _journal.Answer = _ => null;
        var pending = await CountAsync(runtime, start.AddMinutes(10));

        Assert.IsNull(pending!.Files);
        Assert.Contains("cannot say", pending.WalksInFullBecause!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ASetWhoseBackupsAskNoJournal_SaysNothing()
    {
        WriteTree();
        await _harness.SetupAsync();
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory, ChangeJournal = null },
            Timeout);
        await BackUpAsync(runtime, DateTimeOffset.Now);

        Assert.IsNull(await CountAsync(runtime, DateTimeOffset.Now));
        Assert.IsNull((await StatusAsync(runtime)).PendingChanges);
    }

    [TestMethod]
    public async Task ACount_MovesNoPlaceTheNextBackupAsksFrom()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        var kept = _journal.Given.First();

        _harness.WriteSourceFile("docs/a.txt", "alpha, changed");
        _journal.Answer = _ => [new ChangedFolder(_harness.SourceRoot, "docs")];
        await CountAsync(runtime, start.AddMinutes(10));
        var next = await BackUpAsync(runtime, start.AddHours(1));

        Assert.AreEqual(kept, _journal.Asked.Last(), "the backup asks from where the last one stood, as before the count");
        Assert.AreEqual("1 file(s), 0 unchanged, 1 folder(s) not walked", next.Detail);
    }

    [TestMethod]
    public async Task TheStatus_GivesTheLastCount_AndAsksForAnotherOnceItIsAMinuteOld()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        var set = runtime.Configuration.BackupSets.Single();
        _journal.Answer = _ => [new ChangedFolder(_harness.SourceRoot, "docs")];

        // Nothing counted yet: the status says so, and a count begins.
        Assert.IsNull(runtime.PendingChanges.Describe(set, start));
        var first = runtime.PendingChanges.Counting(set.Id) ?? throw new AssertFailedException("a count began");
        Assert.AreSame(first, runtime.PendingChanges.Counting(set.Id));
        runtime.PendingChanges.Describe(set, start);
        Assert.AreSame(first, runtime.PendingChanges.Counting(set.Id), "never two at once");
        await first.WaitAsync(Timeout);
        var asked = _journal.Asked.Count;

        // Within the minute the count stands.
        var described = runtime.PendingChanges.Describe(set, start.AddSeconds(30))!;
        Assert.AreEqual(0L, described.Files);
        Assert.IsNull(runtime.PendingChanges.Counting(set.Id), "a fresh count is not asked for again");

        // A minute on it is asked for again, and the old one is given meanwhile.
        _harness.WriteSourceFile("docs/a.txt", "alpha, changed");
        Assert.AreEqual(0L, runtime.PendingChanges.Describe(set, start.AddSeconds(61))!.Files);
        await (runtime.PendingChanges.Counting(set.Id) ?? throw new AssertFailedException("a new count began")).WaitAsync(Timeout);
        Assert.AreEqual(asked + 1, _journal.Asked.Count);
        Assert.AreEqual(1L, runtime.PendingChanges.Describe(set, start.AddSeconds(62))!.Files);

        // And the service's status carries it.
        Assert.AreEqual(1L, (await StatusAsync(runtime)).PendingChanges!.Files);
    }

    [TestMethod]
    public async Task FilesTheLastBackupCouldNotRead_AreWaiting_ThoughNothingIsNamed()
    {
        WriteTree();
        _harness.WriteSourceFile("docs/doc.cloud", "delta");
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        // A changed file the provider does not deliver, and a new one it never
        // does: the backup fails both, and keeps the version before of the one.
        _harness.WriteSourceFile("docs/doc.cloud", "delta, changed");
        _harness.WriteSourceFile("docs/new.cloud", "new");
        _journal.Answer = _ => [new ChangedFolder(_harness.SourceRoot, "docs")];
        _delivers = false;
        await BackUpAsync(runtime, start.AddHours(1));

        _journal.Answer = _ => [];
        var pending = await CountAsync(runtime, start.AddHours(1).AddMinutes(10));
        Assert.AreEqual(2L, pending!.Files, "both are tried again by the next backup");

        // Which takes them, and walks no folder the count did not.
        _delivers = true;
        var next = await BackUpAsync(runtime, start.AddHours(2));
        Assert.AreEqual("3 file(s), 1 unchanged, 1 folder(s) not walked", next.Detail);
    }

    [TestMethod]
    public async Task FailuresThatWillNotRead_WalkInFullNext()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        // The snapshot's metadata is gone from the store, its failures with
        // it: the next backup cannot tell which folders to try again.
        Directory.Delete(Path.Combine(_harness.RepositoryPath, "blobs", "meta"), recursive: true);
        var pending = await CountAsync(runtime, start.AddMinutes(10));

        Assert.IsNull(pending!.Files);
        Assert.Contains("failures would not read", pending.WalksInFullBecause!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ASnapshotsFailures_AreReadOnceForIt_NotOnceACount()
    {
        // A snapshot never changes, and its store may be a destination billed
        // by the request: a count a minute while someone watches reads its
        // failures once.
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        Assert.AreEqual(0L, (await CountAsync(runtime, start.AddMinutes(10)))!.Files);

        Directory.Delete(Path.Combine(_harness.RepositoryPath, "blobs", "meta"), recursive: true);

        Assert.AreEqual(0L, (await CountAsync(runtime, start.AddMinutes(20)))!.Files, "read once for the snapshot");
    }

    private static ulong Ms(DateTimeOffset at) => (ulong)at.ToUnixTimeMilliseconds();

    private ValueTask<PendingChangesDescriptor?> CountAsync(ServiceRuntime runtime, DateTimeOffset now) =>
        runtime.PendingChanges.RefreshAsync(runtime.Configuration.BackupSets.Single(), now, Timeout);

    private void WalkInFull(string schedule)
    {
        var path = Path.Combine(_harness.StateDirectory, "config.json");
        var configuration = ClientConfiguration.Load(path);
        (configuration with
        {
            BackupSets = [configuration.BackupSets.Single() with { FullWalk = schedule }],
        }).Save(path);
    }

    private async Task<BackupSetStatusDescriptor> StatusAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        return status.Sets.Single();
    }

    private void WriteTree()
    {
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("photos/b.jpg", "bravo");
    }

    private async Task<BackupOutcome> BackUpAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var job = runtime.Jobs.Begin(set.Id, Ms(now));
        var outcome = await BackupRunner.RunAsync(
            runtime, set, job.Id, now, userInitiated: true, full: false, cancellationToken: Timeout);
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
                WrapSource = source => new CloudStandIn(source, () => _delivers),
            },
            Timeout);
    }

    /// <summary>A journal that answers as the test says, and remembers what it gave and was asked.</summary>
    private sealed class ScriptedJournal : IChangeJournal
    {
        private long _next = 100;

        /// <summary>What it says changed since a place; nothing, unless told.</summary>
        public Func<JournalPosition, IReadOnlyList<ChangedFolder>?> Answer { get; set; } = _ => [];

        /// <summary>Every place it gave, in order.</summary>
        public List<JournalPosition> Given { get; } = [];

        /// <summary>Every place it was asked from, in order.</summary>
        public List<JournalPosition> Asked { get; } = [];

        public JournalPosition? PositionOf(string folder)
        {
            var place = new JournalPosition(@"\\?\Volume{test}\", 7, _next++);
            Given.Add(place);
            return place;
        }

        public IReadOnlyList<ChangedFolder>? ChangedSince(JournalPosition since, IReadOnlyCollection<string> roots)
        {
            lock (Asked)
            {
                Asked.Add(since);
            }

            return Answer(since);
        }
    }
}

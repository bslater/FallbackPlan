using System.CommandLine;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A set may leave its online-only files to their cloud provider (ADR-0195,
/// FR-SNP-015). Its backups fetch none: a new or changed one is left out,
/// fails nothing, and the backup completes saying how many it left. Its status
/// says that it leaves them, and the command line's status line does too; a
/// set that fetches them says nothing. Its changes waiting count none it will
/// leave. A set that stops leaving them walks in full at its next backup,
/// which takes what earlier ones left, though the change journal names
/// nothing.
/// </summary>
/// <remarks>
/// No runner has a cloud provider, so a stand-in source marks the files
/// named <c>.cloud</c> online-only and counts each fetch of one; the journal
/// is scripted. The rest of the backup is the service's own.
/// </remarks>
[TestClass]
public sealed class LeaveOnlineOnlyTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly ScriptedJournal _journal = new();
    private int _fetches;

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ASetThatLeavesThem_FetchesNone_FailsNothing_AndItsBackupSaysHowManyItLeft()
    {
        WriteTree();
        await using var runtime = await StartAsync(journal: null);
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        var fetched = _fetches;
        Assert.AreEqual(2, fetched, "a set that fetches them fetched both");

        _harness.WriteSourceFile("docs/a.cloud", "alpha, changed");
        _harness.WriteSourceFile("docs/new.cloud", "new, and never fetched");
        Leave(true);
        var left = await BackUpAsync(runtime, start.AddHours(1));

        Assert.AreEqual(fetched, _fetches, "no online-only file was fetched");
        var job = runtime.Jobs.Jobs.Last(job => job.BackupSetId == runtime.Configuration.BackupSets.Single().Id);
        Assert.AreEqual(Domain.Jobs.JobState.Complete, job.State, job.Detail);
        Assert.AreEqual("2 file(s), 2 unchanged; 2 online-only file(s) left to their cloud provider", left.Detail);
        var status = await StatusAsync(runtime);
        Assert.IsTrue(status.LeavesOnlineOnly);
        Assert.IsNull(status.NotFetched, "a file left is no file the backup could not fetch");
        Assert.DoesNotContain(warning => warning.Contains("PARTIAL", StringComparison.Ordinal), status.Status.Warnings);
    }

    [TestMethod]
    public async Task ASetThatFetchesThem_SaysNothingOfLeavingThem()
    {
        WriteTree();
        await using var runtime = await StartAsync(journal: null);

        var backup = await BackUpAsync(runtime, DateTimeOffset.Now);

        Assert.AreEqual("3 file(s), 0 unchanged", backup.Detail);
        Assert.IsNull((await StatusAsync(runtime)).LeavesOnlineOnly);
    }

    [TestMethod]
    public async Task ASetThatStopsLeavingThem_WalksInFull_AndTakesWhatItLeft_ThoughTheJournalNamesNothing()
    {
        WriteTree();
        Leave(true);
        await using var runtime = await StartAsync(_journal);
        var start = DateTimeOffset.Now;
        var first = await BackUpAsync(runtime, start);
        Assert.AreEqual("1 file(s), 0 unchanged; 2 online-only file(s) left to their cloud provider", first.Detail);

        // Told nothing changed, the next takes the set as the last held it,
        // and what that one left stays left.
        var second = await BackUpAsync(runtime, start.AddHours(1));
        Assert.AreEqual("0 file(s), 0 unchanged, 1 folder(s) not walked", second.Detail);

        Leave(false);
        var third = await BackUpAsync(runtime, start.AddHours(2));

        Assert.AreEqual("3 file(s), 1 unchanged", third.Detail, "walked in full, it fetched the two it had left");
        Assert.AreEqual(2, _fetches);
    }

    [TestMethod]
    public async Task ASetThatLeavesThem_IsToldWhatChanged_AsAnyOtherIs()
    {
        WriteTree();
        Leave(true);
        await using var runtime = await StartAsync(_journal);
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        _harness.WriteSourceFile("docs/c.txt", "charlie, changed");
        _journal.Answer = _ => [new ChangedFolder(_harness.SourceRoot, "docs")];
        var second = await BackUpAsync(runtime, start.AddHours(1));

        Assert.AreEqual("1 file(s), 0 unchanged; 2 online-only file(s) left to their cloud provider", second.Detail);
        Assert.IsNotEmpty(_journal.Asked, "the backup asked the journal, and walked only what it named");
    }

    [TestMethod]
    public async Task TheChangesWaiting_OfASetThatLeavesThem_CountNoneItWillLeave()
    {
        WriteTree();
        Leave(true);
        await using var runtime = await StartAsync(_journal);
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        _harness.WriteSourceFile("docs/c.txt", "charlie, changed");
        _harness.WriteSourceFile("docs/new.cloud", "new");
        _journal.Answer = _ => [new ChangedFolder(_harness.SourceRoot, "docs")];
        var pending = await runtime.PendingChanges.RefreshAsync(
            runtime.Configuration.BackupSets.Single(), start.AddMinutes(10), Timeout);

        Assert.AreEqual(1L, pending!.Files, "c.txt; the next backup will leave new.cloud, a.cloud and b.cloud");
        Assert.AreEqual(0L, pending.FilesToFetch);
    }

    [TestMethod]
    public void TheJournalPlacesKept_SayWhetherTheSetLeftAny_AndOnesKeptBeforeTheyCouldSaySayNot()
    {
        var store = new JournalPlaceStore(_harness.StateDirectory);
        var setId = new string('a', 32);
        var path = Path.Combine(_harness.StateDirectory, "change-journal", $"{setId}.json");
        var places = new KeptJournalPlaces("ab", 1_000, [("/data", new JournalPosition(@"\\?\Volume{test}\", 7, 100))]);

        store.Keep(setId, places with { LeftOnlineOnly = true });
        Assert.IsTrue(store.Read(setId)!.LeftOnlineOnly);

        store.Keep(setId, places);
        Assert.IsFalse(store.Read(setId)!.LeftOnlineOnly);
        Assert.DoesNotContain("leftOnlineOnly", File.ReadAllText(path), StringComparison.Ordinal, "written only where some were left");
    }

    [TestMethod]
    public async Task TheCommandLinesStatus_SaysTheSetLeavesThem()
    {
        WriteTree();
        Leave(true);
        await using var runtime = await StartAsync(journal: null);
        await BackUpAsync(runtime, DateTimeOffset.Now);

        await using var listener = LocalServiceListener.Start(
            new ServiceCommandHandler(runtime, RemoteBindingState.Off), _harness.StateDirectory);
        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "status", "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.Contains("  online-only:left-to-provider", result.All, StringComparison.Ordinal);
    }

    private void Leave(bool leave)
    {
        var path = Path.Combine(_harness.StateDirectory, "config.json");
        var configuration = ClientConfiguration.Load(path);
        (configuration with
        {
            BackupSets = [configuration.BackupSets.Single() with { LeaveOnlineOnly = leave }],
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
        _harness.WriteSourceFile("docs/a.cloud", "alpha");
        _harness.WriteSourceFile("docs/b.cloud", "bravo");
        _harness.WriteSourceFile("docs/c.txt", "charlie, on the device");
    }

    private async Task<BackupOutcome> BackUpAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var job = runtime.Jobs.Begin(set.Id, (ulong)now.ToUnixTimeMilliseconds());
        var outcome = await BackupRunner.RunAsync(
            runtime, set, job.Id, now, userInitiated: true, full: false, cancellationToken: Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        return outcome;
    }

    private async Task<ServiceRuntime> StartAsync(IChangeJournal? journal)
    {
        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                ChangeJournal = journal,
                WrapSource = source => new CloudStandIn(source, () =>
                {
                    Interlocked.Increment(ref _fetches);
                    return true;
                }),
            },
            Timeout);
    }

    /// <summary>A journal that answers as the test says, and remembers what it was asked.</summary>
    private sealed class ScriptedJournal : IChangeJournal
    {
        private long _next = 100;

        /// <summary>What it says changed since a place; nothing, unless told.</summary>
        public Func<JournalPosition, IReadOnlyList<ChangedFolder>?> Answer { get; set; } = _ => [];

        /// <summary>Every place it was asked from, in order.</summary>
        public List<JournalPosition> Asked { get; } = [];

        public JournalPosition? PositionOf(string folder) =>
            new(@"\\?\Volume{test}\", 7, Interlocked.Increment(ref _next));

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

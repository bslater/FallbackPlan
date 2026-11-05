using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using FallbackPlan.Agent;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// On macOS the service asks the volume's event history what changed in a
/// set's folders since the set's last backup (ADR-0191, FR-SNP-012): a
/// backup after another walks only the folders that changed, and takes a
/// change made long enough before it that the history gives it back as
/// history rather than as it happens. Each of several backups asked for in
/// turn takes the file written the moment before it, with no quiet
/// before the first (ADR-0191 Amendment 1). The runtime gives every backup
/// that history unless a harness gives its own.
/// </summary>
/// <remarks>
/// The history is real here, so this runs where macOS does; what the service
/// does with any journal's answer is held on every platform by
/// <c>ChangeJournalRunTests</c>. The system numbers a change a moment after
/// it is made, so the set's files are left until the history is quiet
/// before the first backup takes its place.
/// </remarks>
[TestClass]
public sealed partial class MacChangeHistoryRunTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public void TheRuntime_OnMacOs_GivesBackupsTheVolumesHistory() =>
        Assert.IsInstanceOfType<MacChangeJournal>(new ServiceOptions { ArchivesRoot = "/a", StateDirectory = "/s" }.ChangeJournal);

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public async Task ABackupAfterAnother_WalksOnlyTheFoldersTheHistoryNamed()
    {
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("photos/b.jpg", "bravo");
        await _harness.SetupAsync();
        Quiet(_harness.SourceRoot);
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            _timeout.Token);
        var start = DateTimeOffset.Now;

        var first = await BackUpAsync(runtime, start);
        Assert.AreEqual("2 file(s), 0 unchanged", first.Detail, "the first backup has no place to ask from");

        _harness.WriteSourceFile("docs/a.txt", "alpha, changed");
        var second = await BackUpAsync(runtime, start.AddHours(1));

        Assert.AreEqual("1 file(s), 0 unchanged, 1 folder(s) not walked", second.Detail);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public async Task ABackupAfterAnother_TakesAChangeMadeWellBeforeIt()
    {
        // As a set is backed up hourly: the change is long numbered by the
        // time the next backup asks.
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("photos/b.jpg", "bravo");
        await _harness.SetupAsync();
        Quiet(_harness.SourceRoot);
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            _timeout.Token);
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        _harness.WriteSourceFile("docs/new.txt", "written well before the next backup");
        await Task.Delay(TimeSpan.FromSeconds(5), _timeout.Token);
        var second = await BackUpAsync(runtime, start.AddHours(1));

        Assert.AreEqual("2 file(s), 1 unchanged, 1 folder(s) not walked", second.Detail, "docs walked, photos taken as it was");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "the history is FSEvents'")]
    [PlatformTrait(TestPlatforms.MacOs)]
    [SupportedOSPlatform("macos")]
    public async Task EachBackup_TakesTheFileWrittenTheMomentBeforeIt()
    {
        // As PeerDeepSweepTests met once on macOS: files written as the
        // service starts, a backup, then a file written and a backup asked
        // for at once. Each backup takes one file it has not seen; whatever
        // else the history names is taken as it was.
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("photos/b.jpg", "bravo");
        await _harness.SetupAsync();
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            _timeout.Token);
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        for (var round = 1; round <= 5; round++)
        {
            _harness.WriteSourceFile($"docs/new-{round}.txt", "written the moment before the backup");
            var outcome = await BackUpAsync(runtime, start.AddHours(round));

            var counted = Counted().Match(outcome.Detail ?? string.Empty);
            Assert.IsTrue(counted.Success, outcome.Detail);
            Assert.AreEqual(
                1,
                int.Parse(counted.Groups[1].Value, CultureInfo.InvariantCulture) - int.Parse(counted.Groups[2].Value, CultureInfo.InvariantCulture),
                $"round {round}: {outcome.Detail}");
        }
    }

    // Until a place is found from which nothing under the folder is still to
    // be numbered: what the backups are asked after is only what they do.
    [SupportedOSPlatform("macos")]
    private static void Quiet(string folder)
    {
        var journal = new MacChangeJournal();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var since = journal.PositionOf(folder) ?? throw new AssertFailedException($"no place for {folder}");
            Thread.Sleep(100);
            if (journal.ChangedSince(since, [folder], out _) is { Count: 0 })
            {
                return;
            }
        }

        throw new AssertFailedException($"the folder {folder} never went quiet");
    }

    [GeneratedRegex(@"^(\d+) file\(s\), (\d+) unchanged")]
    private static partial Regex Counted();

    private async Task<BackupOutcome> BackUpAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var job = runtime.Jobs.Begin(set.Id, (ulong)now.ToUnixTimeMilliseconds());
        var outcome = await BackupRunner.RunAsync(
            runtime, set, job.Id, now, userInitiated: true, full: false, cancellationToken: _timeout.Token);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        return outcome;
    }
}

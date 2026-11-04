using System.Runtime.Versioning;
using FallbackPlan.Agent;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// On macOS the service asks the volume's event history what changed in a
/// set's folders since the set's last backup (ADR-0191, FR-SNP-012): a
/// backup after another walks only the folders that changed. The runtime
/// gives every backup that history unless a harness gives its own.
/// </summary>
/// <remarks>
/// The history is real here, so this runs where macOS does; what the service
/// does with any journal's answer is held on every platform by
/// <c>ChangeJournalRunTests</c>. The system numbers a change a moment after
/// it is made, so the set's files are left until the history is quiet
/// before the first backup takes its place.
/// </remarks>
[TestClass]
public sealed class MacChangeHistoryRunTests : IDisposable
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

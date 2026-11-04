using System.Runtime.Versioning;
using FallbackPlan.Agent;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// On Linux the service watches a set's folders itself and tells each backup
/// what the watch named since the set's last (ADR-0190, FR-SNP-012): a
/// backup after another walks only the folders that changed. The runtime
/// gives every backup the process's one watch unless a harness gives its own.
/// </summary>
/// <remarks>
/// The watch is real here, so this runs where Linux does; what the service
/// does with any journal's answer is held on every platform by
/// <c>ChangeJournalRunTests</c>.
/// </remarks>
[TestClass]
public sealed class LinuxChangeWatchRunTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "the watch is inotify's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public void TheRuntime_OnLinux_GivesBackupsTheProcesssWatch() =>
        Assert.AreSame(LinuxChangeJournal.Shared, new ServiceOptions { ArchivesRoot = "/a", StateDirectory = "/s" }.ChangeJournal);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "the watch is inotify's")]
    [PlatformTrait(TestPlatforms.Linux)]
    [SupportedOSPlatform("linux")]
    public async Task ABackupAfterAnother_WalksOnlyTheFoldersTheWatchNamed()
    {
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("photos/b.jpg", "bravo");
        await _harness.SetupAsync();
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            _timeout.Token);
        var start = DateTimeOffset.Now;

        var first = await BackUpAsync(runtime, start);
        Assert.AreEqual("2 file(s), 0 unchanged", first.Detail, "the first backup walks in full and starts the watch");

        _harness.WriteSourceFile("docs/a.txt", "alpha, changed");
        var second = await BackUpAsync(runtime, start.AddHours(1));

        Assert.AreEqual("1 file(s), 0 unchanged, 1 folder(s) not walked", second.Detail);
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

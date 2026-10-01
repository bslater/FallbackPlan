using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A capture taken while the clock read the wrong year is flagged, listed,
/// noticed and kept, at the hub and at its destination (FR-GC-012,
/// ADR-0078). It runs through the real pipeline. The capture clock is the
/// only thing the test moves, which is what a firmware reset does to a
/// machine.
/// </summary>
[TestClass]
public sealed class ImplausibleCaptureServiceTests : IDisposable
{
    private static readonly DateTimeOffset ClockReset = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private TimeSpan _clockOffset;
    private int _runs;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string NoticeKey => $"capture-time-implausible:{_harness.DocsSetId}";

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ACaptureUnderAClockSetBack_IsListedReportedNoticedAndKeptAtTheDestination()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        await BackUpAsync(runtime, "before the reset");
        _clockOffset = ClockReset - DateTimeOffset.UtcNow;
        await BackUpAsync(runtime, "during the reset");
        _clockOffset = TimeSpan.Zero;
        await BackUpAsync(runtime, "after the reset");

        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var misdated = Assert.ContainsSingle(
            listed.Snapshots.Where(snapshot => snapshot.CapturedAt < (ulong)ClockReset.AddYears(1).ToUnixTimeMilliseconds()));
        Assert.AreEqual("behind", misdated.ImplausibleCaptureTime);
        Assert.IsTrue(
            listed.Snapshots.Where(snapshot => snapshot != misdated).All(snapshot => snapshot.ImplausibleCaptureTime is null),
            "a capture taken under the right clock was flagged");

        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: false), Timeout), out var report);
        Assert.Contains(
            line => line.Contains(
                $"keep {misdated.SnapshotId[..12]}… — implausible capture time (dated before earlier publications)",
                StringComparison.Ordinal),
            report.Lines,
            string.Join(" | ", report.Lines));

        var notice = Assert.ContainsSingle(runtime.Notices.Unacknowledged.Where(candidate => candidate.Key == NoticeKey));
        Assert.Contains(misdated.SnapshotId[..12], notice.Message, StringComparison.Ordinal);

        // The destination converges to its keep-set. Of three captures, the
        // newest real one represents today and fills the floor, the first
        // shares its day and goes, and the misdated one stays. Taken at its
        // word it was twenty-five years old and went too.
        FanOut.EnqueueAll(runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now, userInitiated: true);
        await WaitForAsync(() => !runtime.Queue.IsActive(FanOut.JobIdFor(_harness.DocsSetId, "vault")), Timeout);
        var replica = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        Assert.HasCount(2, Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task AFindingAPersonAcknowledged_IsNotRaisedAgainWhileItIsUnchanged()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        await BackUpAsync(runtime, "before the reset");
        _clockOffset = ClockReset - DateTimeOffset.UtcNow;
        await BackUpAsync(runtime, "during the reset");

        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: false), Timeout));
        var notice = Assert.ContainsSingle(runtime.Notices.Unacknowledged.Where(candidate => candidate.Key == NoticeKey));
        Assert.IsTrue(runtime.Notices.Acknowledge(notice.Id, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        // The same snapshots are still out of step, and nothing about them
        // has changed. A person who has looked is not asked again.
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: false), Timeout));
        Assert.IsFalse(
            runtime.Notices.Unacknowledged.Any(candidate => candidate.Key == NoticeKey),
            "an acknowledged finding was raised again unchanged");
    }

    private async Task BackUpAsync(ServiceRuntime runtime, string content)
    {
        _harness.WriteSourceFile("docs/a.txt", content);
        var set = runtime.Configuration.BackupSets.Single();
        var when = DateTimeOffset.Now.AddMinutes(5 * ++_runs);
        var outcome = await Scheduler.Enqueue(runtime, set, when, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        Directory.CreateDirectory(Vault);
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('1', 32),
                    Name = "vault",
                    Kind = DestinationKind.LocalPath,
                    Path = Vault,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 4h",
                    Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

        await _harness.SetupAsync();

        // Set before the runtime starts, so its capture jobs inherit it; each
        // capture reads the offset when it completes.
        BackupRunner.CaptureClock = () => (ulong)(DateTimeOffset.UtcNow + _clockOffset).ToUnixTimeMilliseconds();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            Timeout);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(25, cancellationToken);
        }
    }
}

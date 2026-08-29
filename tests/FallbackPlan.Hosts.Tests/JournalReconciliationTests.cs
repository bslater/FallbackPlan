using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The job journal tells the truth about a fresh process (FR-SVC-017;
/// ADR-0049). The journal is durable and the queue is not, so a row left
/// unsettled — by a stop mid-run, a kill, or a fault outside the runner's
/// catch list — used to load back claiming to run forever: the console
/// rendered a live card, cancel refused it ("no job is queued or running"),
/// and deleting the set was refused with "cancel it first" — a closed loop
/// whose only exit was hand-editing jobs.json. These pin the three doors
/// out: the startup sweep, the cancel that settles a run the queue no
/// longer knows, and a set deletion that only defers to jobs the queue is
/// actually running.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class JournalReconciliationTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task StartAsync_AJournalCarryingLiveRows_SettlesThemAndRaisesANotice()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        // The rows a dead process left behind: one mid-publication, one that
        // never left the queue.
        var planted = JobStateStore.Open(_harness.StateDirectory);
        var interrupted = planted.Begin(_harness.DocsSetId, 1_000);
        planted.Transition(interrupted.Id, JobState.Publishing, 1_100);
        planted.Begin(_harness.DocsSetId, 2_000);

        await using var runtime = await StartAsync();

        Assert.IsTrue(
            runtime.Jobs.Jobs.All(job => job.State == JobState.FailedRecoverable),
            "a fresh process runs nothing, so no loaded row may claim otherwise");
        Assert.Contains(
            "interrupted",
            runtime.Jobs.Jobs.Single(job => job.Id == interrupted.Id).Detail ?? string.Empty,
            StringComparison.Ordinal);

        // Not silent: the 3 a.m. interruption is a durable notice at breakfast.
        Assert.IsTrue(
            runtime.Notices.Unacknowledged.Any(notice =>
                notice.Message.Contains("interrupted", StringComparison.OrdinalIgnoreCase)),
            "settling somebody's run must leave a notice saying so");
    }

    [TestMethod]
    public async Task CancelJob_ARowTheQueueNeverKnew_SettlesItAsCancelled()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync();

        // The orphan class that needs no restart: a journal row whose queue
        // job is gone (a fault outside the runner's catch list). Begun here
        // without ever entering the queue — the same observable state.
        var set = runtime.Configuration.BackupSets.Single();
        var orphan = runtime.Jobs.Begin(set.Id, (ulong)DateTimeOffset.Now.ToUnixTimeMilliseconds());

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new CancelJobCommand(orphan.Id), _timeout.Token),
            "cancelling a run that is no longer live is the operator's remedy, not an error");

        var row = runtime.Jobs.Jobs.Single(job => job.Id == orphan.Id);
        Assert.AreEqual(JobState.Cancelled, row.State);
        Assert.Contains("no longer live", row.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DeleteBackupSet_AnOrphanedRow_NoLongerBlocksDeletion()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync();

        var set = runtime.Configuration.BackupSets.Single();
        runtime.Jobs.Begin(set.Id, (ulong)DateTimeOffset.Now.ToUnixTimeMilliseconds());

        // The same rule the enqueue guard states (ADR-0047 Amendment 3):
        // only a run the queue is actually running defers a deletion.
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(new DeleteBackupSetCommand("docs"), _timeout.Token));
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }
}

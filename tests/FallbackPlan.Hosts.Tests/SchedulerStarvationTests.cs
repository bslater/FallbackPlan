using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The scheduler pass must never be hostage to the transfer lane (ADR-0029
/// Amendment 4). Before it, a multi-hour destination copy meant no pass ran
/// at all: due-ness was never evaluated, so every set's scheduled
/// incrementals silently stopped — not queued, not refused, simply never
/// considered — until the copy finished and the missed slots coalesced into
/// one catch-up.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchedulerStarvationTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task RunPass_ATransferOccupyingTheLane_StillReturnsAndRunsTheDueCapture()
    {
        await using var runtime = await StartAsync();
        var t0 = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

        // Seed: one full pass — capture, sync, sweep — completes normally.
        var first = await Scheduler.RunPassAsync(runtime, t0, Timeout);
        Assert.AreEqual(1, first.Ran);
        await first.Transfers.WaitAsync(Timeout);

        // A long transfer now owns the lane, and the set falls due again.
        var (done, release) = Occupy(runtime, JobLane.Transfer);
        _harness.WriteSourceFile("more/new.txt", "fresh bytes for the second capture");

        // The assertion this suite exists for: the pass returns, and the due
        // capture RAN — the writer lane is free, and the pass no longer waits
        // for the transfer phase before answering.
        var second = await Scheduler.RunPassAsync(runtime, t0.AddHours(2), Timeout)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.AreEqual(1, second.Ran, "the due capture must run while the transfer lane is busy");

        release();
        await done.WaitAsync(Timeout);
        await second.Transfers.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task RunPass_TheDecoupledSyncs_StillConvergeAfterThePassReturns()
    {
        await using var runtime = await StartAsync();
        var t0 = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

        var first = await Scheduler.RunPassAsync(runtime, t0, Timeout);
        await first.Transfers.WaitAsync(Timeout);
        var seeded = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNotNull(seeded?.LastSuccessAt);

        // Fresh bytes, a busy lane, a pass that returns without waiting —
        // and the sync still lands once the lane frees: fire-and-forget must
        // not mean fire-and-lose.
        var (done, release) = Occupy(runtime, JobLane.Transfer);
        _harness.WriteSourceFile("more/second.txt", "bytes the vault does not hold yet");
        var second = await Scheduler.RunPassAsync(runtime, t0.AddHours(2), Timeout)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.AreEqual(1, second.Ran);

        release();
        await done.WaitAsync(Timeout);
        await second.Transfers.WaitAsync(Timeout);

        var synced = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.AreEqual(DestinationSyncState.InSync, synced!.State);
        Assert.IsTrue(
            synced.LastSuccessAt > seeded!.LastSuccessAt,
            "the deferred sync must have converged the second capture");
    }

    [TestMethod]
    public async Task RunPass_AManualBackupStillInFlight_DoesNotQueueASecondCaptureForTheSet()
    {
        // ADR-0027 §1: one run per set at a time. Structural before only
        // because the serial pass never looked while a capture ran; a pass
        // that ticks during long captures needs the rule stated.
        await using var runtime = await StartAsync();
        var t0 = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

        var (done, release) = Occupy(runtime, JobLane.Writer);
        var set = runtime.Configuration.BackupSets.Single();
        var manual = Scheduler.Enqueue(runtime, set, t0, userInitiated: true);

        var pass = await Scheduler.RunPassAsync(runtime, t0, Timeout)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        var outcome = Assert.ContainsSingle(pass.Sets);
        Assert.AreEqual("already-running", outcome.Outcome);
        Assert.AreEqual(
            1,
            runtime.Jobs.Jobs.Count(job => job.BackupSetId == set.Id),
            "the pass must not have begun a second journal row for a set whose run is still queued");

        release();
        await done.WaitAsync(Timeout);
        Assert.AreEqual("ran", (await manual.WaitAsync(Timeout)).Outcome);
        await pass.Transfers.WaitAsync(Timeout);
    }

    /// <summary>
    /// Parks one lane worker until released, honouring cancellation so
    /// disposal never hangs behind it.
    /// </summary>
    private static (Task Done, Action Release) Occupy(ServiceRuntime runtime, JobLane lane)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        runtime.Queue.Enqueue(new QueuedJob(
            "occupy-" + Guid.NewGuid().ToString("n"),
            lane,
            UserInitiated: true,
            $"occupy the {lane} lane",
            async cancellationToken =>
            {
                try
                {
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown reached the blocker first; that is release enough.
                }

                done.TrySetResult();
            }));

        return (done.Task, () => release.TrySetResult());
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            Timeout);
    }
}

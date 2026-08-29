using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The completed-job drill-down (ADR-0050): a settled run can be asked what
/// it did. The summary rides the job row itself; the details — what changed
/// against the previous snapshot, and which files failed and why — are read
/// from the repository on demand, so they survive the sacrificial journal.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class JobDrilldownTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    /// <summary>
    /// The journal grows for the life of the installation and the frame codec
    /// caps a reply at 8 MiB, so the client may bound its ask. The newest rows
    /// are the ones a history view shows; the output order stays oldest-first,
    /// the documented order of the unbounded form.
    /// </summary>
    [TestMethod]
    public async Task ListJobs_WithLimit_ReturnsTheNewestRowsOldestFirst()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync();

        var set = runtime.Configuration.BackupSets.Single();
        for (var i = 0; i < 5; i++)
        {
            var job = runtime.Jobs.Begin(set.Id, (ulong)(1_000 + i));
            runtime.Jobs.Transition(job.Id, JobState.Complete, (ulong)(2_000 + i));
        }

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<JobsResult>(
            await handler.ExecuteAsync(new ListJobsCommand(ActiveOnly: false, Limit: 2), _timeout.Token),
            out var bounded);

        Assert.HasCount(2, bounded.Jobs);
        Assert.AreEqual(2_003UL, bounded.Jobs[0].UpdatedAt, "the bound keeps the newest rows, not the oldest");
        Assert.IsTrue(
            bounded.Jobs[0].StartedAt < bounded.Jobs[1].StartedAt,
            "a bounded listing keeps the oldest-first order the unbounded one documents");

        Assert.IsInstanceOfType<JobsResult>(
            await handler.ExecuteAsync(new ListJobsCommand(ActiveOnly: false), _timeout.Token),
            out var unbounded);
        Assert.HasCount(5, unbounded.Jobs, "no limit still means everything — the pre-1.22 meaning");
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

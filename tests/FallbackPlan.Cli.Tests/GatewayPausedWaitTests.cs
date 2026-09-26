using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The gateway's await loop treats <see cref="JobState.Paused"/> as
/// in-flight (ADR-0047 Amendment 1): a suspended run resumes unattended, so
/// a waiting <c>backup</c> keeps polling through the suspension instead of
/// exiting mid-run with a "PAUSED" verdict. Pins the deliberate omission of
/// Paused from the gateway's settled set (FR-SVC-014's client half).
/// </summary>
[TestClass]
public sealed class GatewayPausedWaitTests
{
    [TestMethod]
    public async Task ABackupThatPausesMidRun_IsAwaitedThroughToCompletion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new ScriptedClient(
        [
            JobState.Scanning,
            JobState.Paused,
            JobState.Paused,
            JobState.Publishing,
            JobState.Complete,
        ]);
        var gateway = new ServiceGateway(client, "scripted", client);

        var report = await gateway.RunBackupAsync(
            new BackupRequest { SetName = "docs" }, timeout.Token);

        Assert.IsTrue(report.Ok, string.Join(" | ", report.Lines));
        Assert.IsTrue(
            client.ListCalls >= 5,
            $"the loop must poll through every Paused answer, not settle on one (polled {client.ListCalls})");
    }

    /// <summary>
    /// Answers run_backup with a job id and each list_jobs with the next
    /// scripted state, holding the last one once the script runs out.
    /// </summary>
    private sealed class ScriptedClient(IReadOnlyList<JobState> states) : IFallbackPlanClient
    {
        private int _listed;

        public int ListCalls => _listed;

        public ContractVersion ServiceContractVersion => ContractVersion.Current;

        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
        {
            if (command is RunBackupCommand)
            {
                return ValueTask.FromResult<ServiceResult>(new JobAcceptedResult("job-1"));
            }

            var index = Math.Min(_listed, states.Count - 1);
            _listed++;
            var state = states[index];
            return ValueTask.FromResult<ServiceResult>(new JobsResult(
            [
                new JobDescriptor(
                    "job-1", new string('a', 32), state, 1, (ulong)(2 + index),
                    state == JobState.Complete ? new string('f', 32) : null,
                    state == JobState.Paused ? "suspended for a higher-priority run" : "3 file(s), 0 unchanged"),
            ]));
        }

        // The await loop under test never watches; an empty stream keeps the
        // fake honest about that.
        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<JobProgressEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

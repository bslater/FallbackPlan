using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The per-set gate between a destructive retention apply and a running sync
/// (ADR-0029 Amendment 2). The staging trim made staging mutable while the
/// transfer lane still assumes it is not; without this exclusion a trim that
/// verified a replica holds a blob and a convergence about to drop that blob
/// there can, together, delete its last copy. Fan-out waits; retention's
/// apply defers and says so.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RetentionSyncInterlockTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    [TestMethod]
    public async Task Retention_WhileASyncHoldsTheSetGate_DefersApplyAndSaysSo()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // A sync is mid-flight for this set: the gate is held. The retention
        // pass must not run its destructive half against a moving target —
        // it reports, defers, and names why.
        var gate = runtime.SetGate(_harness.DocsSetId);
        Assert.IsTrue(gate.Wait(0));
        try
        {
            Assert.IsInstanceOfType<RetentionResult>(
                await handler.ExecuteAsync(new RetentionCommand(Apply: true), _timeout.Token), out var report);

            Assert.Contains(
                line => line.Contains("apply deferred", StringComparison.Ordinal)
                    && line.Contains("sync", StringComparison.Ordinal),
                report.Lines);
        }
        finally
        {
            gate.Release();
        }

        // With the gate free, the same command applies — no deferral line.
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true), _timeout.Token), out var applied);
        Assert.IsFalse(applied.Lines.Any(line => line.Contains("apply deferred", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Sync_WhileARetentionApplyHoldsTheSetGate_WaitsAndThenConverges()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // Retention's apply half holds the gate: the sync must queue behind
        // it, not converge a set whose staging is mid-mutation.
        var gate = runtime.SetGate(_harness.DocsSetId);
        Assert.IsTrue(gate.Wait(0));

        var sync = handler.ExecuteAsync(new SyncCommand(null, null), _timeout.Token).AsTask();
        await Task.Delay(300, _timeout.Token);
        Assert.IsFalse(sync.IsCompleted, "the sync ran while the set gate was held");

        gate.Release();
        Assert.IsInstanceOfType<SyncResult>(await sync, out var report);
        Assert.Contains(
            line => line.Contains("in sync", StringComparison.Ordinal), report.Lines);
    }

    [TestMethod]
    public async Task SyncVerb_WhileAServiceHoldsTheWriterRole_FailsCleanlyNamingTheHolder()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        // A running service holds the writer role; the one-shot verb must be
        // refused with the holder named (FR-SVC-002) — never a stack trace.
        await using var runtime = await StartAsync();

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "sync", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(1, result.ExitCode, result.All);
        Assert.Contains("writer role", result.Error, StringComparison.Ordinal);

        var retention = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "retention", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(1, retention.ExitCode, retention.All);
        Assert.Contains("writer role", retention.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sync_APairAlreadyQueued_IsReportedNotDoubled()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // Something already occupies the (set, destination) identity on the
        // transfer lane: the command must coalesce and say so, not double
        // the work and not pretend a fresh sync ran.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(runtime.Queue.Enqueue(new QueuedJob(
            FanOut.JobIdFor(_harness.DocsSetId, "vault"), JobLane.Transfer, UserInitiated: true,
            "hold the pair", async _ => await release.Task)));

        try
        {
            Assert.IsInstanceOfType<SyncResult>(
                await handler.ExecuteAsync(new SyncCommand(null, null), _timeout.Token), out var report);
            Assert.Contains(
                line => line.Contains("already syncing", StringComparison.Ordinal), report.Lines);
        }
        finally
        {
            release.SetResult();
        }
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

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }
}

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

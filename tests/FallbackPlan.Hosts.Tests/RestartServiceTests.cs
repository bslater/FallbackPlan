using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The restart verb at the handler (FR-SVC-017; ADR-0049): local callers
/// only, and only where a host is present to recycle — the handler's half
/// of the contract, below the Owner gate <c>AuthenticationGateTests</c>
/// pins and above the whole-process proof in
/// <c>AgentServiceLifetimeTests</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RestartServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Restart_FromTheRemoteScope_IsRefusedByName()
    {
        await using var runtime = await StartAsync();

        // A paired console must not cut a machine it cannot see (ADR-0028
        // §6) — the set_log_level posture, and for a harder reason: the
        // restart would sever the very connection carrying the refusal.
        var handler = new ServiceCommandHandler(
            runtime, RemoteBindingState.On("127.0.0.1:9999"), CallerScope.Remote, requestRestart: () => { });

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RestartServiceCommand(), _timeout.Token), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("local", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Restart_WithNoHostToRecycle_IsRefusedWithTheReason()
    {
        await using var runtime = await StartAsync();

        // --once runs and bare handler hosts have nobody to rebuild the
        // runtime; the refusal says so instead of acknowledging a restart
        // that will never happen.
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RestartServiceCommand(), _timeout.Token), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("host", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Restart_WithAHost_AcknowledgesAndSignals()
    {
        await using var runtime = await StartAsync();

        var signalled = 0;
        var handler = new ServiceCommandHandler(
            runtime, RemoteBindingState.Off, CallerScope.Local, requestRestart: () => signalled++);

        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new RestartServiceCommand(), _timeout.Token));
        Assert.AreEqual(1, signalled, "the host must have been asked to recycle");
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

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

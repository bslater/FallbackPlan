using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Diagnostics;
using FallbackPlan.Repository.Crypto;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service's trace tier at the command seam (event ids 3758, 3760–3761):
/// every verb crossing <c>ExecuteAsync</c> leaves one line naming the command
/// and result types, and the setup verbs say which classification a ceremony
/// met. Per ADR-0043's "a call site is not a logger", each record is asserted
/// arriving in the ring through a real dispatch.
/// </summary>
[TestClass]
public sealed class CommandTraceLoggingTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));
    private LoggingComposition? _logging;

    public void Dispose()
    {
        _logging?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        _logging = LoggingComposition.Create(new LoggingOptions
        {
            Default = LogLevel.Trace,
            RingCapacity = 64,
        });

        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                Logging = _logging,
            },
            passphrase,
            _timeout.Token);
    }

    private Diagnostics.LogRecord? Record(int eventId) =>
        _logging!.Ring.Read(0, 64, LogLevel.Trace).Records
            .LastOrDefault(record => record.EventId == eventId);

    [TestMethod]
    public async Task ExecuteAsync_AnyCommand_LeavesOneLineNamingCommandAndResult()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<DiagnosticsResult>(
            await handler.ExecuteAsync(new GetDiagnosticsCommand(), _timeout.Token));

        var executed = Record(3758);
        Assert.IsNotNull(executed, "the command seam (3758) is what makes a trace read as a conversation");
        var values = executed.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.AreEqual(nameof(GetDiagnosticsCommand), values["Command"]);
        Assert.AreEqual(nameof(DiagnosticsResult), values["Result"]);
    }

    [TestMethod]
    public async Task Provision_RefusedRemotely_SaysWhichClassificationItMet()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off, CallerScope.Remote);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ProvisionInstallationCommand("00"), _timeout.Token));

        var outcome = Record(3760);
        Assert.IsNotNull(outcome, "the provisioning verb must say how it classified the ceremony");
        Assert.AreEqual(
            nameof(ServiceErrorReason.Refused),
            outcome.Values.Single(pair => pair.Key == "Outcome").Value);
    }

    [TestMethod]
    public async Task ConfirmRecoveryKit_BeforeSetup_SaysItWasRefused()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ConfirmRecoveryKitCommand(new string('a', 64)), _timeout.Token));

        var outcome = Record(3761);
        Assert.IsNotNull(outcome);
        Assert.AreEqual(
            nameof(ServiceErrorReason.Refused),
            outcome.Values.Single(pair => pair.Key == "Outcome").Value);
    }
}

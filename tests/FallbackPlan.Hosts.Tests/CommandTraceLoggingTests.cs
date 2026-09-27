using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service's trace tier at the command seam (event ids 3784–3785;
/// FR-SVC-010): every verb crossing <c>ExecuteAsync</c> leaves one line naming
/// the command and result types, and the setup verb says which classification
/// a ceremony met. Per ADR-0043's "a call site is not a logger", each record
/// is asserted arriving in the ring through a real dispatch.
/// </summary>
/// <remarks>
/// Ported from the line merged in at 9fb5ab6, where the same records carried
/// ids 3758 and 3760; this branch had already spent those on other messages,
/// so the records took the next free ids in the Agent's range (ADR-0043 §3).
/// That line's third case, a recovery-kit confirmation refused before setup,
/// has no counterpart: the verb was withdrawn with the kit (ADR-0060).
/// </remarks>
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

        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                Logging = _logging,
            },
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

        var executed = Record(3784);
        Assert.IsNotNull(executed, "the command seam (3784) is what makes a trace read as a conversation");
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

        var outcome = Record(3785);
        Assert.IsNotNull(outcome, "the provisioning verb must say how it classified the ceremony");
        Assert.AreEqual(
            nameof(ServiceErrorReason.Refused),
            outcome.Values.Single(pair => pair.Key == "Outcome").Value);
    }
}

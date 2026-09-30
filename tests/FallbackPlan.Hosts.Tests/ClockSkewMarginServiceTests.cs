using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service's collector surveys write intents under the installation's
/// configured clock skew margin (NFR-TIME-002, ADR-0009 Amendment 7), read
/// afresh for each retention pass as the background window and the limits
/// are; and the settings verb, which does not carry the margin, keeps it.
/// </summary>
/// <remarks>
/// What a pass deletes cannot show the margin while the key generation never
/// advances, so these read the margin the pass says it surveyed with.
/// </remarks>
[TestClass]
public sealed class ClockSkewMarginServiceTests : IDisposable
{
    private const int IntentsSurveyed = 2904;

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));
    private LoggingComposition? _logging;

    private CancellationToken Timeout => _timeout.Token;

    private string ConfigurationPath => Path.Combine(_harness.StateDirectory, "config.json");

    public void Dispose()
    {
        _logging?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ARetentionPass_SurveysIntentsUnderTheConfiguredMargin_ReadAfreshEachPass()
    {
        _harness.WriteConfiguration("every 1h");
        SetMargin(hours: 6);
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await BackUpAsync(runtime);

        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: false), Timeout));
        Assert.AreEqual(TimeSpan.FromHours(6), Assert.ContainsSingle(SurveyedMargins()));

        // A change applies from the next pass, with no restart: nothing about
        // the margin is sized when the service starts.
        SetMargin(hours: 36);
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: false), Timeout));
        Assert.AreEqual(TimeSpan.FromHours(36), SurveyedMargins()[^1]);
    }

    [TestMethod]
    public async Task ARetentionPass_WhereTheFileStatesNoMargin_SurveysUnderADay()
    {
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await BackUpAsync(runtime);

        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: false), Timeout));
        Assert.AreEqual(TimeSpan.FromDays(1), Assert.ContainsSingle(SurveyedMargins()));
    }

    [TestMethod]
    public async Task TheSettingsVerb_WhichDoesNotCarryTheMargin_KeepsIt()
    {
        // The rule FR-SVC-020 states for a destination's fields holds for the
        // installation's: a field the wire does not carry is kept by an edit,
        // never dropped by it.
        _harness.WriteConfiguration("every 1h");
        SetMargin(hours: 6);
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(new UpdateServiceSettingsCommand("22:00-06:00", null, null), Timeout));

        var saved = ClientConfiguration.Load(ConfigurationPath);
        Assert.AreEqual("22:00-06:00", saved.BackgroundWindow, "the control: the update landed");
        Assert.AreEqual(6, saved.ClockSkewMarginHours);
    }

    private void SetMargin(int hours) =>
        (ClientConfiguration.Load(ConfigurationPath) with { ClockSkewMarginHours = hours }).Save(ConfigurationPath);

    private List<object?> SurveyedMargins() =>
    [
        .. _logging!.Ring.Read(0, 512, LogLevel.Trace).Records
            .Where(record => record.EventId == IntentsSurveyed)
            .Select(record => record.Values.First(value => value.Key == "ClockSkewMargin").Value),
    ];

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        _logging = LoggingComposition.Create(new LoggingOptions
        {
            Default = LogLevel.Debug,
            Directory = Path.Combine(_harness.StateDirectory, "logs"),
            RingCapacity = 512,
        });

        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                Logging = _logging,
            },
            Timeout);
    }
}

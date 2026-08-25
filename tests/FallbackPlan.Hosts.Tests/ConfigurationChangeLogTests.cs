using FallbackPlan.Agent;
using FallbackPlan.Diagnostics;
using FallbackPlan.Repository.Crypto;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// What the Information tier says about the configuration: that it changed,
/// once per change — never that it was re-read, every pass (ADR-0043).
/// </summary>
/// <remarks>
/// The 2026-08-24/25 service log is the motivating exhibit: the scheduler
/// re-reads the configuration every pass, each read logged "Configuration
/// loaded" at Information, and that one message was 347 of the log's 353
/// Information records — a tier an operator reads, saying nothing had
/// happened. The load record survives at Debug (<c>ClientConfigurationTests</c>
/// pins that); what this suite pins is the half that replaces it: the runtime
/// notices change, and only change, at Information.
/// </remarks>
[TestClass]
public sealed class ConfigurationChangeLogTests : IDisposable
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
            Directory = Path.Combine(_harness.StateDirectory, "logs"),
            RingCapacity = 256,
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

    private IReadOnlyList<Diagnostics.LogRecord> Records(int eventId) =>
        [.. _logging!.Ring.Read(0, 256, LogLevel.Trace).Records.Where(record => record.EventId == eventId)];

    [TestMethod]
    public async Task AnUnchangedConfiguration_IsAnnouncedOnceHoweverOftenItIsRead()
    {
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync();

        runtime.LoadConfiguration();
        runtime.LoadConfiguration();
        runtime.LoadConfiguration();

        var announced = Assert.ContainsSingle(Records(3742));
        Assert.AreEqual(LogLevel.Information, announced.Level);
    }

    [TestMethod]
    public async Task AChangedConfiguration_IsAnnouncedAgain()
    {
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync();

        runtime.LoadConfiguration();
        runtime.LoadConfiguration();

        _harness.WriteConfiguration("every 4h");
        runtime.LoadConfiguration();
        runtime.LoadConfiguration();

        Assert.HasCount(2, Records(3742));
    }

    [TestMethod]
    public async Task EveryRead_StillLeavesItsDebugRecord()
    {
        // "What was in force when this pass ran" stays answerable — one
        // record per read, at the level of routine mechanics rather than the
        // operator's tier.
        _harness.WriteConfiguration("every 1h");
        await using var runtime = await StartAsync();

        runtime.LoadConfiguration();
        runtime.LoadConfiguration();

        var loads = Records(3400);
        Assert.HasCount(2, loads);
        Assert.IsTrue(loads.All(record => record.Level == LogLevel.Debug));
    }
}

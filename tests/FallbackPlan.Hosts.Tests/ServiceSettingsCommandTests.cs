using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The installation's own settings over the command contract (contract 1.44):
/// the background window, the background read limit and the backup pool's
/// width, read and written through the service rather than only in the
/// configuration file. A setting the request leaves out keeps its value; an
/// empty text or a zero width clears one; every refusal is asserted on its
/// reason, on the parser's own defect, on the absence of the file's path, and
/// on the file left as it was.
/// </summary>
[TestClass]
public sealed class ServiceSettingsCommandTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    private string ConfigurationPath => Path.Combine(_harness.StateDirectory, "config.json");

    [TestMethod]
    public async Task GetServiceSettings_WhereTheFileSetsNone_ReportsNoneAndTheWidthThePoolRuns()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceSettingsResult>(
            await handler.ExecuteAsync(new GetServiceSettingsCommand(), _timeout.Token), out var settings);
        Assert.IsNull(settings.BackgroundWindow);
        Assert.IsNull(settings.BackgroundReadLimit);
        Assert.IsNull(settings.MaxConcurrentBackups);
        Assert.AreEqual(2, settings.EffectiveMaxConcurrentBackups, "absent, the pool runs at its default width");
    }

    [TestMethod]
    public async Task UpdateServiceSettings_SettingAllThree_WritesTheFileAndSaysWhenEachApplies()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(
                new UpdateServiceSettingsCommand("22:00-06:00", "40 MiB/s", 3), _timeout.Token),
            out var change);

        var saved = ClientConfiguration.Load(ConfigurationPath);
        Assert.AreEqual("22:00-06:00", saved.BackgroundWindow);
        Assert.AreEqual("40 MiB/s", saved.BackgroundReadLimit);
        Assert.AreEqual(3, saved.MaxConcurrentBackups);

        // Each change is said back with its value. The pool's width is fixed
        // when the service starts, so its line says so rather than implying
        // a running pool just grew.
        Assert.IsTrue(change.Lines.Any(line => line.Contains("22:00-06:00", StringComparison.Ordinal)), string.Join(" | ", change.Lines));
        Assert.IsTrue(change.Lines.Any(line => line.Contains("40 MiB/s", StringComparison.Ordinal)), string.Join(" | ", change.Lines));
        Assert.IsTrue(
            change.Lines.Any(line => line.Contains("max_concurrent_backups", StringComparison.Ordinal)
                && line.Contains("restart", StringComparison.Ordinal)),
            string.Join(" | ", change.Lines));

        Assert.IsInstanceOfType<ServiceSettingsResult>(
            await handler.ExecuteAsync(new GetServiceSettingsCommand(), _timeout.Token), out var settings);
        Assert.AreEqual("22:00-06:00", settings.BackgroundWindow);
        Assert.AreEqual("40 MiB/s", settings.BackgroundReadLimit);
        Assert.AreEqual(3, settings.MaxConcurrentBackups);
        Assert.AreEqual(2, settings.EffectiveMaxConcurrentBackups, "the running pool keeps its width until a restart");
    }

    [TestMethod]
    public async Task UpdateServiceSettings_TheWindow_IsOnStatusAtOnce()
    {
        // Nothing caches the configuration, so the window a person just set
        // is the window status reports, without waiting for a pass.
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpdateServiceSettingsCommand("01:00-02:00", null, null), _timeout.Token));

        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), _timeout.Token), out var status);
        Assert.AreEqual("01:00-02:00", status.BackgroundWindow?.Text);
    }

    [TestMethod]
    public async Task UpdateServiceSettings_SayingNothing_LeavesTheFileAsItWas()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsNotInstanceOfType<ServiceError>(await handler.ExecuteAsync(
            new UpdateServiceSettingsCommand("22:00-06:00", "40 MiB/s", 3), _timeout.Token));
        var before = await File.ReadAllBytesAsync(ConfigurationPath, _timeout.Token);

        var result = await handler.ExecuteAsync(new UpdateServiceSettingsCommand(null, null, null), _timeout.Token);

        Assert.IsNotInstanceOfType<ServiceError>(result);
        CollectionAssert.AreEqual(
            before, await File.ReadAllBytesAsync(ConfigurationPath, _timeout.Token),
            "a request that names no setting must not touch the file");
    }

    [TestMethod]
    public async Task UpdateServiceSettings_EmptyTextAndAZeroWidth_ClearEach()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsNotInstanceOfType<ServiceError>(await handler.ExecuteAsync(
            new UpdateServiceSettingsCommand("22:00-06:00", "40 MiB/s", 3), _timeout.Token));

        Assert.IsNotInstanceOfType<ServiceError>(await handler.ExecuteAsync(
            new UpdateServiceSettingsCommand("", "", 0), _timeout.Token));

        var saved = ClientConfiguration.Load(ConfigurationPath);
        Assert.IsNull(saved.BackgroundWindow, "an empty window clears it: background work may start at any hour");
        Assert.IsNull(saved.BackgroundReadLimit, "an empty read limit clears it: reads are unlimited");
        Assert.IsNull(saved.MaxConcurrentBackups, "a zero width clears it: the pool takes its default");
    }

    [TestMethod]
    [DataRow("25:00-06:00", null, null, "background_window")]
    [DataRow("22:00-22:00", null, null, "background_window")]
    [DataRow(null, "10 MB/s", null, "background_read_limit")]
    [DataRow(null, "fast", null, "background_read_limit")]
    [DataRow(null, null, 6, "max_concurrent_backups")]
    [DataRow(null, null, -1, "max_concurrent_backups")]
    public async Task UpdateServiceSettings_AnUnreadableValue_IsRefusedNamingItButNotTheFile(
        string? window, string? readLimit, int? width, string field)
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var before = await File.ReadAllBytesAsync(ConfigurationPath, _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new UpdateServiceSettingsCommand(window, readLimit, width), _timeout.Token),
            out var error);

        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
        // A paired remote console may send this; the service's own paths are
        // not its business, so the refusal speaks of the value alone.
        Assert.DoesNotContain(_harness.StateDirectory, error.Message, StringComparison.Ordinal);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(ConfigurationPath, _timeout.Token));
    }

    [TestMethod]
    public async Task UpdateServiceSettings_ADecimalRate_IsRefusedNamingTheBinaryUnit()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new UpdateServiceSettingsCommand(null, "10 MB/s", null), _timeout.Token),
            out var error);
        Assert.Contains("MiB/s", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task UpdateServiceSettings_OneRefusedValue_AppliesNoneOfTheOthers()
    {
        // All or nothing: a request is one decision, and half of it landing
        // would leave the operator guessing which half.
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var before = await File.ReadAllBytesAsync(ConfigurationPath, _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(await handler.ExecuteAsync(
            new UpdateServiceSettingsCommand("22:00-06:00", "10 MB/s", 3), _timeout.Token));

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(ConfigurationPath, _timeout.Token));
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            _timeout.Token);
    }
}

using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Diagnostics;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Repository.Crypto;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Contract 1.15's diagnostics verbs (ADR-0043 §6, FR-SVC-010): the ring the
/// engine has been filling becomes readable, and a level becomes changeable
/// without a restart.
/// </summary>
/// <remarks>
/// The local/remote split is what most of this suite is about, and it is two
/// rules rather than one. A paired console may <b>read</b> the log, redacted,
/// because plaintext paths stay on the machine that already holds the files. It
/// may not <b>set</b> a level at all, because what this machine writes to its
/// own disk is a local decision (ADR-0028 §6).
/// </remarks>
[TestClass]
public sealed class DiagnosticsCommandTests : IDisposable
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

    private async Task<ServiceRuntime> StartAsync(LogLevel level = LogLevel.Trace)
    {
        _logging = LoggingComposition.Create(new LoggingOptions
        {
            Default = level,
            Directory = Path.Combine(_harness.StateDirectory, "logs"),
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

    private static ServiceCommandHandler Handler(ServiceRuntime runtime, CallerScope scope) =>
        new(runtime, RemoteBindingState.Off, scope);

    private void LogOne(LogLevel level, string message)
    {
#pragma warning disable CA1848, CA2254 // A test writes one record; the sink is what is under test.
        _logging!.Factory.CreateLogger("FallbackPlan.Test").Log(level, new EventId(4242), message);
#pragma warning restore CA1848, CA2254
    }

    [TestMethod]
    public async Task GetDiagnostics_OnAServiceWithLogging_ReportsTheLevelsAndTheRingButNoPath()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync(LogLevel.Debug);

        Assert.IsInstanceOfType<DiagnosticsResult>(
            await Handler(runtime, CallerScope.Local)
                .ExecuteAsync(new GetDiagnosticsCommand(), _timeout.Token),
            out var diagnostics);

        Assert.AreEqual("debug", diagnostics.DefaultLevel);
        Assert.IsTrue(diagnostics.DurableSink, "A directory was configured, so a durable sink exists.");
        Assert.AreEqual(64, diagnostics.RingCapacity);

        // Threat T-16: the service exposes no filesystem access to clients, so
        // it does not begin by telling one where it writes. Whoever may read
        // the file already knows the state directory.
        var rendered = System.Text.Json.JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain(_harness.StateDirectory, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("logs", rendered, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ReadLog_ByCursor_PagesForwardWithoutRepeatingARecord()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync();
        var handler = Handler(runtime, CallerScope.Local);

        for (var i = 0; i < 5; i++)
        {
            LogOne(LogLevel.Warning, $"record {i}");
        }

        Assert.IsInstanceOfType<LogRecordsResult>(
            await handler.ExecuteAsync(new ReadLogCommand(0, 2), _timeout.Token), out var first);
        Assert.HasCount(2, first.Records);
        Assert.IsFalse(first.Dropped);

        Assert.IsInstanceOfType<LogRecordsResult>(
            await handler.ExecuteAsync(new ReadLogCommand(first.NextSequence, 10), _timeout.Token),
            out var second);

        // The pages must not overlap: a cursor that re-serves its own last
        // record makes a client's "since I last looked" a lie.
        var firstSequences = first.Records.Select(record => record.Sequence).ToArray();
        Assert.IsEmpty(
            second.Records.Where(record => firstSequences.Contains(record.Sequence)),
            "The second page repeated a record the first already served.");
    }

    [TestMethod]
    public async Task ReadLog_AMinimumLevel_SkipsWhatIsBeneathIt()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync();
        var handler = Handler(runtime, CallerScope.Local);

        LogOne(LogLevel.Debug, "beneath");
        LogOne(LogLevel.Error, "above");

        Assert.IsInstanceOfType<LogRecordsResult>(
            await handler.ExecuteAsync(new ReadLogCommand(0, 50, "error"), _timeout.Token), out var page);

        Assert.ContainsSingle(page.Records);
        Assert.AreEqual("error", page.Records[0].Level);
    }

    [TestMethod]
    public async Task ReadLog_AskingForMoreThanAPageHolds_IsClampedRatherThanRefused()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync();

        // A client asking for too many is asking for a page, not making an
        // error — the cursor is how it gets the rest.
        Assert.IsInstanceOfType<LogRecordsResult>(
            await Handler(runtime, CallerScope.Local)
                .ExecuteAsync(new ReadLogCommand(0, int.MaxValue), _timeout.Token),
            out _);
    }

    [TestMethod]
    public async Task ReadLog_ALevelNobodyRecognises_IsRefusedNamingTheOnesThatExist()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync();

        Assert.IsInstanceOfType<ServiceError>(
            await Handler(runtime, CallerScope.Local)
                .ExecuteAsync(new ReadLogCommand(0, 10, "shouty"), _timeout.Token),
            out var error);

        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        StringAssert.Contains(error.Message, "shouty");
        foreach (var name in LogLevels.Names)
        {
            StringAssert.Contains(error.Message, name);
        }
    }

    [TestMethod]
    public async Task SetLogLevel_FromALocalCaller_TakesEffectWithoutARestart()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync(LogLevel.Error);
        var handler = Handler(runtime, CallerScope.Local);

        LogOne(LogLevel.Debug, "before the change");

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(new SetLogLevelCommand(null, "debug"), _timeout.Token), out _);

        LogOne(LogLevel.Debug, "after the change");

        Assert.IsInstanceOfType<LogRecordsResult>(
            await handler.ExecuteAsync(new ReadLogCommand(0, 50), _timeout.Token), out var page);

        var messages = page.Records.Select(record => record.Message).ToArray();
        Assert.IsEmpty(
            messages.Where(message => message.Contains("before the change", StringComparison.Ordinal)),
            "The level was Error when that record was offered; it must not have been kept.");
        Assert.ContainsSingle(
            messages.Where(message => message.Contains("after the change", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SetLogLevel_ForOneCategory_LeavesTheDefaultAlone()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync(LogLevel.Warning);
        var handler = Handler(runtime, CallerScope.Local);

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(
                new SetLogLevelCommand("FallbackPlan.Test", "trace"), _timeout.Token), out _);

        Assert.IsInstanceOfType<DiagnosticsResult>(
            await handler.ExecuteAsync(new GetDiagnosticsCommand(), _timeout.Token), out var diagnostics);

        Assert.AreEqual("warning", diagnostics.DefaultLevel);
        Assert.AreEqual("trace", diagnostics.CategoryLevels["FallbackPlan.Test"]);
    }

    [TestMethod]
    public async Task SetLogLevel_FromAPairedRemoteConsole_IsRefused()
    {
        // A console may read this service's log; it may not decide what goes
        // into it (ADR-0028 §6).
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync();

        Assert.IsInstanceOfType<ServiceError>(
            await Handler(runtime, CallerScope.Remote)
                .ExecuteAsync(new SetLogLevelCommand(null, "trace"), _timeout.Token),
            out var error);

        Assert.AreEqual(ServiceErrorReason.Refused, error.Reason);
    }

    [TestMethod]
    public async Task ReadLog_FromAPairedRemoteConsole_IsServedButRedacted()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync();

        // A path is the thing that must not cross. LogPath renders in full
        // locally and as a hash remotely (ADR-0043 §4).
        var secret = Path.Combine(_harness.StateDirectory, "a-telling-folder-name");
#pragma warning disable CA1848, CA2254
        _logging!.Factory.CreateLogger("FallbackPlan.Test")
            .Log(LogLevel.Warning, new EventId(4242), "touched {Path}", new LogPath(secret));
#pragma warning restore CA1848, CA2254

        Assert.IsInstanceOfType<LogRecordsResult>(
            await Handler(runtime, CallerScope.Remote)
                .ExecuteAsync(new ReadLogCommand(0, 50), _timeout.Token),
            out var remote);

        Assert.ContainsSingle(remote.Records);
        Assert.DoesNotContain(
            "a-telling-folder-name", remote.Records[0].Message, StringComparison.Ordinal);

        // The same record, locally, is served whole — the boundary is what
        // redacts, not the capture.
        Assert.IsInstanceOfType<LogRecordsResult>(
            await Handler(runtime, CallerScope.Local)
                .ExecuteAsync(new ReadLogCommand(0, 50), _timeout.Token),
            out var local);

        StringAssert.Contains(local.Records[0].Message, "a-telling-folder-name");
    }

    [TestMethod]
    public async Task DescribeService_OnAServiceWithLogging_CarriesTheEffectiveLevel()
    {
        await _harness.CreateRepositoryAsync();
        await using var runtime = await StartAsync(LogLevel.Warning);

        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await Handler(runtime, CallerScope.Local)
                .ExecuteAsync(new DescribeServiceCommand(), _timeout.Token),
            out var description);

        Assert.AreEqual("warning", description.LogLevel);
    }

    [TestMethod]
    public async Task Diagnostics_OnAServiceComposedWithoutLogging_SaySoRatherThanInventZeroes()
    {
        await _harness.CreateRepositoryAsync();
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);

        var handler = Handler(runtime, CallerScope.Local);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new GetDiagnosticsCommand(), _timeout.Token), out var diagnostics);
        Assert.AreEqual(ServiceErrorReason.Failed, diagnostics.Reason);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ReadLogCommand(0, 10), _timeout.Token), out _);

        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);
        Assert.IsNull(description.LogLevel, "No composition means no level to report, not a guessed one.");
    }
}

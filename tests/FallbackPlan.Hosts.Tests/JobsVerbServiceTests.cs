using System.CommandLine;
using System.Globalization;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The <c>jobs</c> verb against a running service (FR-SVC-018; ADR-0050): the
/// history newest first, one run's report read off its journal row, and
/// <c>jobs &lt;id&gt; --changes --failures</c> printing exactly what the
/// drill-down verbs answer — the requirement's "answers the same from the
/// CLI", held to the service's own answer rather than to a description of it.
/// </summary>
/// <remarks>
/// <c>Cli.Tests/JobsVerbTests</c> holds the other half: the verb's wiring, and
/// its refusals with no service listening. Everything here needs the service,
/// because the journal is the running service's to write and the details are
/// read from the repository on demand.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class JobsVerbServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Jobs_AgainstARunningService_ListsTheHistoryNewestFirstAndReportsARunFromItsRow()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("one.txt", "1");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        var first = await RunBackupAsync(runtime, handler);
        _harness.WriteSourceFile("two.txt", "2");
        var second = await RunBackupAsync(runtime, handler);

        var history = await JobsAsync();
        Assert.AreEqual(0, history.ExitCode, history.All);
        var newer = history.Output.IndexOf(second.Id, StringComparison.Ordinal);
        var older = history.Output.IndexOf(first.Id, StringComparison.Ordinal);
        Assert.IsTrue(newer >= 0 && older >= 0, $"both runs belong in the history: {history.Output}");
        Assert.IsTrue(newer < older, $"the history reads newest first: {history.Output}");

        // One run's report, in the words and numbers of its own row.
        var report = await JobsAsync(second.Id);
        Assert.AreEqual(0, report.ExitCode, report.All);
        var planned = second.TotalFiles is { } total ? Invariant($" of {total} planned") : string.Empty;
        foreach (var line in new[]
        {
            Invariant($"state          {second.State}"),
            Invariant($"files          {second.FilesDone ?? 0}{planned} ({second.FilesReused ?? 0} unchanged, {second.FilesFailed ?? 0} failed)"),
            Invariant($"bytes          {second.BytesSeen ?? 0} read, {second.BytesStored ?? 0} newly stored"),
            Invariant($"snapshot       {second.SnapshotId}"),
            Invariant($"detail         {second.Detail}"),
        })
        {
            Assert.Contains(line, report.Output, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task Jobs_ChangesAndFailures_PrintWhatTheDrilldownVerbsAnswer()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("keep.txt", "stays the same");
        var change = _harness.WriteSourceFile("change.txt", "first content");
        var gone = _harness.WriteSourceFile("gone.txt", "will be deleted");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        var first = await RunBackupAsync(runtime, handler);

        // One of each kind of change, and more arrivals than a sample holds,
        // so the line owning up to the rest is printed too.
        File.WriteAllText(change, "second content, longer than the first");
        File.Delete(gone);
        for (var i = 0; i < 22; i++)
        {
            _harness.WriteSourceFile($"fresh-{i:d2}.txt", "newly arrived");
        }

        var second = await RunBackupAsync(runtime, handler);

        Assert.IsInstanceOfType<JobChangesResult>(
            await handler.ExecuteAsync(new JobChangesCommand(second.Id), _timeout.Token), out var changes);
        Assert.IsInstanceOfType<JobFailuresResult>(
            await handler.ExecuteAsync(new JobFailuresCommand(second.Id), _timeout.Token), out var failures);
        Assert.IsTrue(
            changes.New.Count > changes.New.Sample.Count,
            "the case needs more arrivals than the default sample shows");

        var drilldown = await JobsAsync(second.Id, "--changes", "--failures");
        Assert.AreEqual(0, drilldown.ExitCode, drilldown.All);
        var printed = drilldown.Output;

        Assert.Contains(
            Invariant($"vs {changes.BaselineSnapshotId}: {changes.Unchanged} unchanged"), printed, StringComparison.Ordinal);
        foreach (var (label, bucket) in new[] { ("new", changes.New), ("changed", changes.Changed), ("removed", changes.Removed) })
        {
            Assert.Contains(Invariant($"{bucket.Count} {label}"), printed, StringComparison.Ordinal);
            foreach (var path in bucket.Sample)
            {
                Assert.Contains($"  {path}", printed, StringComparison.Ordinal);
            }
        }

        Assert.Contains(
            Invariant($"  … and {changes.New.Count - changes.New.Sample.Count} more"), printed, StringComparison.Ordinal);
        Assert.Contains(Invariant($"{failures.Failures} failure(s)"), printed, StringComparison.Ordinal);

        // The set's first run has no predecessor, and says so instead of
        // diffing against nothing.
        var firstRun = await JobsAsync(first.Id, "--changes");
        Assert.AreEqual(0, firstRun.ExitCode, firstRun.All);
        Assert.Contains("the set's first backup — everything is new", firstRun.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Jobs_AnIdTheJournalDoesNotHold_IsRefusedNamingIt()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var history = await JobsAsync();
        Assert.AreEqual(0, history.ExitCode, history.All);
        Assert.Contains("no jobs recorded yet.", history.Output, StringComparison.Ordinal);

        var unknown = new string('0', 32);
        var refused = await JobsAsync(unknown);
        Assert.AreNotEqual(0, refused.ExitCode);
        Assert.Contains($"No job '{unknown}' is in the journal", refused.All, StringComparison.Ordinal);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private Task<HostHarness.Invocation> JobsAsync(params string[] arguments) =>
        HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            ["jobs", .. arguments, "--state", _harness.StateDirectory]);

    private async Task<JobDescriptor> RunBackupAsync(ServiceRuntime runtime, ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand(null, Full: false), _timeout.Token),
            out var accepted);

        while (!runtime.Jobs.Jobs.Any(job =>
            job.Id == accepted.JobId && JobStateStore.HasSettled(job.State)))
        {
            await Task.Delay(25, _timeout.Token);
        }

        Assert.IsInstanceOfType<JobsResult>(
            await handler.ExecuteAsync(new ListJobsCommand(ActiveOnly: false), _timeout.Token), out var jobs);
        return jobs.Jobs.Single(job => job.Id == accepted.JobId);
    }

    private async Task<ServiceRuntime> StartAsync()
    {
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

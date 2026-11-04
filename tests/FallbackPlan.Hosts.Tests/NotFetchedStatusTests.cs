using System.CommandLine;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A set's status says which online-only files its last backup could not
/// fetch (ADR-0193, FR-SNP-014): how many, how many kept the version backed
/// up before, the file whose fetch failed and the provider's words, and since
/// when backups have been missing them. The time is the first backup that
/// missed any, kept while each after it misses some; a backup that fetches
/// every one clears it.
/// </summary>
/// <remarks>
/// No runner has a cloud provider, so a stand-in source marks the files
/// named <c>.cloud</c> online-only and fails their fetch while told to; the
/// rest of the backup is the service's own.
/// </remarks>
[TestClass]
public sealed class NotFetchedStatusTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private bool _delivers = true;

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task FilesNotFetched_AreNamedInTheSetsStatus_WithHowManyKeptTheirVersionBefore()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        Assert.IsNull((await StatusAsync(runtime)).NotFetched, "a backup that fetched every one names none");

        _harness.WriteSourceFile("docs/a.cloud", "alpha, changed");
        _harness.WriteSourceFile("docs/b.cloud", "bravo, changed");
        _harness.WriteSourceFile("docs/new.cloud", "new, and never fetched");
        _delivers = false;
        await BackUpAsync(runtime, start.AddHours(1));

        var missed = (await StatusAsync(runtime)).NotFetched!;
        Assert.AreEqual(3L, missed.Files);
        Assert.AreEqual(2L, missed.KeptEarlier, "a.cloud and b.cloud keep the versions backed up before");
        Assert.AreEqual("docs/a.cloud", missed.Path);
        Assert.Contains(CloudStandIn.TimedOut, missed.Reason, StringComparison.Ordinal);
        Assert.AreEqual(Ms(start.AddHours(1)), missed.Since);
    }

    [TestMethod]
    public async Task WhileBackupsGoOnMissingThem_TheStatusKeepsWhenTheFirstDid()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        _harness.WriteSourceFile("docs/a.cloud", "alpha, changed");
        _delivers = false;
        await BackUpAsync(runtime, start.AddHours(1));

        await BackUpAsync(runtime, start.AddHours(2));

        var missed = (await StatusAsync(runtime)).NotFetched!;
        Assert.AreEqual(1L, missed.Files);
        Assert.AreEqual(Ms(start.AddHours(1)), missed.Since, "since the first backup that missed it");
    }

    [TestMethod]
    public async Task ABackupThatFetchesThemAll_ClearsIt()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        _harness.WriteSourceFile("docs/a.cloud", "alpha, changed");
        _delivers = false;
        await BackUpAsync(runtime, start.AddHours(1));

        _delivers = true;
        await BackUpAsync(runtime, start.AddHours(2));

        Assert.IsNull((await StatusAsync(runtime)).NotFetched);
    }

    [TestMethod]
    public async Task TheCommandLinesStatus_PrintsHowManyOnTheSetsLine()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        _harness.WriteSourceFile("docs/a.cloud", "alpha, changed");
        _harness.WriteSourceFile("docs/b.cloud", "bravo, changed");
        _delivers = false;
        await BackUpAsync(runtime, start.AddHours(1));

        await using var listener = LocalServiceListener.Start(
            new ServiceCommandHandler(runtime, RemoteBindingState.Off), _harness.StateDirectory);
        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "status", "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.Contains("  not-fetched:2", result.All, StringComparison.Ordinal);
    }

    private static ulong Ms(DateTimeOffset at) => (ulong)at.ToUnixTimeMilliseconds();

    private async Task<BackupSetStatusDescriptor> StatusAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        return status.Sets.Single();
    }

    private void WriteTree()
    {
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("docs/a.cloud", "alpha");
        _harness.WriteSourceFile("docs/b.cloud", "bravo");
        _harness.WriteSourceFile("docs/c.txt", "charlie, on the device");
    }

    private async Task BackUpAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var job = runtime.Jobs.Begin(set.Id, Ms(now));
        var outcome = await BackupRunner.RunAsync(
            runtime, set, job.Id, now, userInitiated: true, full: false, cancellationToken: Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                ChangeJournal = null,
                WrapSource = source => new CloudStandIn(source, () => _delivers),
            },
            Timeout);
    }
}

using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A drill's sampled restores are the service's own, never a person's, so
/// they leave nothing a person's restore leaves (FR-RST-008, FR-RST-004,
/// FR-DRL-002, ADR-0115 Amendment 1). A drill of a write-only set restores
/// each sample only as far as its sealed content, an end the drill counts as
/// passing and a person's restore counts as failed, so each sample was kept
/// as a run left unfinished, said in a notice naming a resume of the
/// drill's scratch folder, and its receipt kept among a person's. A drill's
/// restore is now under way while it runs, so a retention pass holds the
/// set's blobs as it does for any restore, but it is never on record: it is
/// not written, listed, noticed or kept, whether the drill was the pass's or
/// a person's, and nothing of it is said when the service starts again.
/// </summary>
/// <remarks>
/// The installation is set up, so the set is write-only and every drill's
/// samples end at their sealed content.
/// </remarks>
[TestClass]
public sealed class DrillRestoresOffTheRecordTests : IAsyncDisposable
{
    private const string NoticeKind = "restore-unfinished:";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;
    private readonly List<(bool UnderWay, int OnRecord, string Written)> _begun = [];

    /// <summary>The service a restore runs in, so the hook it is begun through can look at it while it runs.</summary>
    private ServiceRuntime? _running;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string RunsFile => Path.Combine(_harness.StateDirectory, "restore-runs.json");

    [TestMethod]
    public async Task ThePassesDrill_PassesAndLeavesNoRunNoticeReceiptOrParts()
    {
        await using var runtime = await StartAsync();

        await PassAsync(runtime);

        var row = Row(runtime);
        Assert.IsNotNull(row.DrilledAt, "the premise: the pass drilled the vault");
        Assert.IsNull(row.DrillFailure, row.DrillFailure);
        Assert.IsGreaterThan(0, row.DrillFiles, "the premise: the drill restored samples");
        await AssertNothingLeftAsync(runtime);
    }

    [TestMethod]
    public async Task ADrillAPersonAsks_LeavesNoRunNoticeReceiptOrPartsEither()
    {
        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();
        var ran = await Scheduler.Enqueue(runtime, set, _start, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", ran.Outcome, ran.Detail);
        var sync = FanOut.Enqueue(runtime, set, "vault", DateTimeOffset.Now, userInitiated: true);
        Assert.IsNotNull(sync);
        await sync.WaitAsync(Timeout);
        Assert.IsNull(Row(runtime).DrilledAt, "the premise: nothing drilled the vault before the person asked");

        var answer = await new ServiceCommandHandler(runtime, RemoteBindingState.Off)
            .ExecuteAsync(new RunDrillCommand("docs", "vault"), Timeout);

        Assert.IsInstanceOfType<DrillResult>(answer, out var drilled, (answer as ServiceError)?.Message);
        Assert.AreEqual(0, drilled.Failed, string.Join(" | ", drilled.Lines));
        await AssertNothingLeftAsync(runtime);
    }

    [TestMethod]
    public async Task ADrillsRestore_IsUnderWayWhileItRuns_ButIsNeverOnRecord()
    {
        await using var runtime = await StartAsync();
        _running = runtime;

        await PassAsync(runtime);

        Assert.IsNotEmpty(_begun, "the premise: the drill restored its samples");
        foreach (var (underWay, onRecord, written) in _begun)
        {
            Assert.IsTrue(underWay, "a retention pass would not hold the set's blobs while the drill restored from them");
            Assert.AreEqual(0, onRecord, "the drill's restore was on record");
            Assert.DoesNotContain("\"run\":", written, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task AServiceStartingAgainAfterADrill_SaysNothingOfItsRestores()
    {
        await using (var runtime = await StartAsync())
        {
            await PassAsync(runtime);
            Assert.IsNotNull(Row(runtime).DrilledAt, "the premise: the pass drilled the vault");
        }

        await using var again = await RestartAsync();

        Assert.IsEmpty(again.RestoreRuns.Runs);
        Assert.IsEmpty(again.Notices.Unacknowledged.Where(notice => notice.Key.StartsWith(NoticeKind, StringComparison.Ordinal)));
    }

    private async Task AssertNothingLeftAsync(ServiceRuntime runtime)
    {
        Assert.IsEmpty(runtime.RestoreRuns.Runs, string.Join(" | ", runtime.RestoreRuns.Runs.Select(run => run.OutputDirectory)));
        Assert.IsEmpty(
            runtime.Notices.Unacknowledged.Where(notice => notice.Key.StartsWith(NoticeKind, StringComparison.Ordinal)),
            "a drill's restore was said not to have finished");
        Assert.IsEmpty(FilesUnder(runtime.ReceiptsRoot), "a drill's restore kept a receipt among a person's");
        Assert.IsEmpty(FilesUnder(runtime.RestorePartsRoot), "a drill's restore kept parts");

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<RestoreRunsResult>(await handler.ExecuteAsync(new ListRestoreRunsCommand(), Timeout), out var runs);
        Assert.IsEmpty(runs.Runs);
        Assert.IsInstanceOfType<RestoreReceiptsResult>(
            await handler.ExecuteAsync(new ListRestoreReceiptsCommand(), Timeout), out var receipts);
        Assert.IsEmpty(receipts.Receipts);
    }

    private static string[] FilesUnder(string root) =>
        Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];

    private async Task PassAsync(ServiceRuntime runtime)
    {
        var pass = await Scheduler.RunPassAsync(runtime, _start, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
    }

    private DestinationSyncRecord Row(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, "vault") ?? throw new AssertFailedException("no ledger row for the vault");

    private async Task<ServiceRuntime> StartAsync()
    {
        Directory.CreateDirectory(Vault);
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('2', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));

        await _harness.SetupAsync();
        return await RestartAsync();
    }

    private async Task<ServiceRuntime> RestartAsync() =>
        await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,

                // Asked once a restore is under way, before it writes: what the
                // service says of that restore while it runs.
                RestoreBegun = _ =>
                {
                    if (_running is { } runtime)
                    {
                        lock (_begun)
                        {
                            _begun.Add((
                                runtime.RestoreRuns.IsUnderWayFor("docs"),
                                runtime.RestoreRuns.Runs.Count,
                                File.Exists(RunsFile) ? File.ReadAllText(RunsFile) : string.Empty));
                        }
                    }
                },
            },
            Timeout);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _timeout.Dispose();
        _harness.Dispose();
        return ValueTask.CompletedTask;
    }
}

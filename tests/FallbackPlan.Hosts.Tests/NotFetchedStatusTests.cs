using System.CommandLine;
using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A set's status says which online-only files its last backup could not
/// fetch (ADR-0193, FR-SNP-014): how many, how many kept the version backed
/// up before, the file whose fetch failed and the provider's words, and since
/// when backups have been missing them. The time is the first backup that
/// missed any, kept while each after it misses some; a backup that fetches
/// every one clears it. A backup whose every failure is such a file that
/// kept its version before completes behind, and the set is not said to be
/// partial; one that missed a file new to the set still completes with
/// failures (ADR-0193 Amendment 1). A folder whose listing did not come is
/// counted with them, with how many kept what was backed up before and
/// whether the failure it names was a listing, kept by the service, while a
/// record kept before says nothing of folders; a backup whose only failure is
/// such a folder that kept its tree completes behind (ADR-0193 Amendment 3).
/// </summary>
/// <remarks>
/// No runner has a cloud provider, so a stand-in source marks the files
/// named <c>.cloud</c> online-only and fails their fetch while told to; the
/// rest of the backup is the service's own. A folder named <c>.cloud</c> is
/// one whose listing its provider keeps, named unlisted while told to.
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
    public async Task ABackupThatKeptTheEarlierVersionOfEachFileItMissed_CompletesBehind_AndTheSetIsNotPartial()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        _harness.WriteSourceFile("docs/a.cloud", "alpha, changed");
        _harness.WriteSourceFile("docs/b.cloud", "bravo, changed");
        _delivers = false;

        await BackUpAsync(runtime, start.AddHours(1));

        var job = LastJob(runtime);
        Assert.AreEqual(Domain.Jobs.JobState.Complete, job.State, job.Detail);
        Assert.Contains("behind: 2 online-only file(s) not downloaded keep the version backed up before", job.Detail!, StringComparison.Ordinal);
        var status = await StatusAsync(runtime);
        Assert.DoesNotContain(warning => warning.Contains("PARTIAL", StringComparison.Ordinal), status.Status.Warnings);
        Assert.AreEqual(2L, status.NotFetched!.KeptEarlier, "the set still says what it is behind on");
    }

    [TestMethod]
    public async Task ABackupThatMissedAFileNewToTheSet_StillCompletesWithFailures_AndTheSetIsPartial()
    {
        WriteTree();
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        _harness.WriteSourceFile("docs/a.cloud", "alpha, changed");
        _harness.WriteSourceFile("docs/new.cloud", "new, and never fetched");
        _delivers = false;

        await BackUpAsync(runtime, start.AddHours(1));

        var job = LastJob(runtime);
        Assert.AreEqual(Domain.Jobs.JobState.CompletedWithFailures, job.State, job.Detail);
        Assert.Contains(warning => warning.Contains("PARTIAL", StringComparison.Ordinal), (await StatusAsync(runtime)).Status.Warnings);
    }

    [TestMethod]
    public async Task AFolderNotListed_IsNamedInTheSetsStatus_AndABackupThatKeptIt_CompletesBehind()
    {
        WriteTree();
        _harness.WriteSourceFile("docs/drive.cloud/d.txt", "delta, kept by the provider's folder");
        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        // Nothing changed, so no file is fetched; only the folder's listing
        // is asked for, and does not come.
        _delivers = false;
        await BackUpAsync(runtime, start.AddHours(1));

        var job = LastJob(runtime);
        Assert.AreEqual(Domain.Jobs.JobState.Complete, job.State, job.Detail);
        Assert.Contains(
            "behind: 1 online-only folder(s) not listed keep what was backed up before", job.Detail!, StringComparison.Ordinal);
        var status = await StatusAsync(runtime);
        Assert.DoesNotContain(warning => warning.Contains("PARTIAL", StringComparison.Ordinal), status.Status.Warnings);
        var missed = status.NotFetched!;
        Assert.AreEqual(0L, missed.Files);
        Assert.AreEqual(1L, missed.Folders);
        Assert.AreEqual(1L, missed.FoldersKeptEarlier);
        Assert.AreEqual("docs/drive.cloud", missed.Path);
        Assert.IsTrue(missed.Listing, "the failure it names was a folder's listing");
        Assert.Contains(CloudStandIn.TimedOut, missed.Reason, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheRecord_KeepsTheFoldersNotListed_AndOneKeptBeforeItDidReadsNone()
    {
        var setId = new string('b', 32);
        var store = new NotFetchedStore(_harness.StateDirectory);
        byte[] snapshot = [.. Enumerable.Repeat((byte)0xB2, 16)];

        store.Keep(
            setId,
            new Repository.NotFetchedFiles(Files: 1, KeptEarlier: 1, Path: "docs/drive.cloud", Reason: "timed out", Folders: 2, FoldersKept: 1, Listing: true),
            1_000, snapshot, isBehind: false);
        var read = store.Read(setId)!;
        Assert.AreEqual(2L, read.Folders);
        Assert.AreEqual(1L, read.FoldersKeptEarlier);
        Assert.IsTrue(read.Listing);

        File.WriteAllText(
            Path.Combine(_harness.StateDirectory, "not-fetched", $"{setId}.json"),
            """{"files":1,"keptEarlier":1,"since":1000,"path":"docs/a.cloud","reason":"timed out"}""");
        var older = store.Read(setId)!;
        Assert.IsNull(older.Folders);
        Assert.IsNull(older.FoldersKeptEarlier);
        Assert.IsNull(older.Listing);
    }

    [TestMethod]
    public void TheRecordOfABackupBehind_SaysSoOnlyOfItsOwnSnapshot_AndAnOlderRecordSaysNot()
    {
        var setId = new string('a', 32);
        var store = new NotFetchedStore(_harness.StateDirectory);
        byte[] behind = [.. Enumerable.Repeat((byte)0xB2, 16)];
        byte[] later = [.. Enumerable.Repeat((byte)0xC3, 16)];
        var missed = new Repository.NotFetchedFiles(Files: 1, KeptEarlier: 1, Path: "docs/a.cloud", Reason: CloudStandIn.TimedOut);

        store.Keep(setId, missed, 1_000, behind, isBehind: true);
        Assert.IsTrue(store.IsBehind(setId, behind));
        Assert.IsFalse(store.IsBehind(setId, later), "a later snapshot, as another writer's, is not said to be behind");

        store.Keep(setId, missed with { KeptEarlier = 0 }, 2_000, later, isBehind: false);
        Assert.IsFalse(store.IsBehind(setId, later));

        // A record kept before it said which snapshot it was of says none is behind.
        File.WriteAllText(
            Path.Combine(_harness.StateDirectory, "not-fetched", $"{setId}.json"),
            """{"files":1,"keptEarlier":1,"since":1000,"path":"docs/a.cloud","reason":"timed out"}""");
        Assert.IsFalse(store.IsBehind(setId, behind));
        Assert.IsNotNull(store.Read(setId), "and still reads as what it names");
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

    private static JobRecord LastJob(ServiceRuntime runtime) =>
        runtime.Jobs.Jobs.Last(job => job.BackupSetId == runtime.Configuration.BackupSets.Single().Id);

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

using System.CommandLine;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A restore cut short is named, and resumed by its name alone (FR-RST-008,
/// ADR-0115). The service keeps each run it began until a run of it
/// finishes: one stopped, one that failed files, and one the service itself
/// did not see end. Each is listed without the backup's file names, and
/// noticed, so a person learns of it however it ended, and once a notice is
/// acknowledged a restart does not say it again. A resume names only the
/// run, and the service runs it as it was asked, under the name its result
/// gives and the caller's session, refusing one that will not fit unless the
/// command line says to go on; one that finishes resolves the notice. A run
/// let go is named no more, and a run not on record is refused before
/// anything is written. A resume is planned as well (FR-RST-003, ADR-0120),
/// for the run it continues or by the run's name alone: measured where the
/// run landed, saying how many files it already landed, and running nothing.
/// A run keeps the parts of the files it was cut short in under its name
/// (ADR-0129), and a resume takes up those that prove, reading none of them
/// from the store again, under whatever name it runs. A run's parts go when
/// it finishes or is let go, and those of a run no longer on record when the
/// service starts. The result counts the files a resume took up from its
/// parts, the receipt says where each was resumed (FR-RST-004), and the
/// command line says so (ADR-0130), as the listing of kept receipts does
/// (ADR-0134). Each run listed says what its parts
/// hold, and the command line says it too (ADR-0133). A plan of a resume counts nothing among
/// what the run reads of a file its run landed but its manifest, nor what
/// its parts prove (ADR-0132). What its parts prove is room already taken,
/// so neither the plan of a resume nor the resume needs it again, and a
/// resume runs where whole files would not fit (FR-RST-003, ADR-0136).
/// The command line lets every run left unfinished go at once, and the
/// parts of each with it (ADR-0162), or only those begun longer ago than it
/// says, keeping the rest and their parts (ADR-0165). A resume is refused
/// where a folder would open a file wider than it was, as its run was, unless
/// it says to go on (FR-RST-003, ADR-0178 Amendment 1).
/// </summary>
[TestClass]
public sealed partial class RestoreUnfinishedServiceTests : IDisposable
{
    private const string NoSuchRun = "0123456789abcdef";
    private const string NoticeKind = "restore-unfinished:";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    /// <summary>Cancelled as the run measures its room, so it stops before its first file.</summary>
    private CancellationTokenSource? _cutShortAt;

    /// <summary>What the volume a run writes to has free, as the probe says it; null for unknown.</summary>
    private long? _available;

    [TestMethod]
    public async Task ARunThatFinishes_LeavesNoRunNamed_AndNothingNoticed()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var restored = await RestoreAsync(handler, Destination, _timeout.Token);

        Assert.AreEqual("complete", restored.Outcome);
        Assert.IsEmpty(await RunsAsync(handler));
        Assert.IsFalse(runtime.Notices.Notices.Any(notice => notice.Key.StartsWith(NoticeKind, StringComparison.Ordinal)));
        Assert.AreEqual(0L, restored.ResumedWithin, "a run that resumes nothing says it resumed nothing within");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a FIFO is a POSIX entry kind, captured and never materialised")]
    public async Task ARunThatSkipsOnlyWhatTheTargetCannotHold_IsFinished()
    {
        // A partial run skipped what this target cannot hold, which a resume
        // would skip again (ADR-0110), so it is not left unfinished.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        if (MkFifo(Path.Combine(_harness.SourceRoot, "pipe"), 0x1B6 /* 0666 */) != 0)
        {
            return; // filesystem refuses FIFOs (some CI mounts) — nothing to assert
        }

        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");
        await _harness.SetupAsync();
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var restored = await RestoreAsync(handler, Destination, _timeout.Token);

        Assert.AreEqual("partial", restored.Outcome);
        Assert.IsEmpty(await RunsAsync(handler));
        Assert.IsFalse(runtime.Notices.Notices.Any(notice => notice.Key.StartsWith(NoticeKind, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ARunCutShort_IsNamed_AndNoticed_AndResumedByItsNameAlone()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var cut = await CutShortAsync(handler);

        var named = Assert.ContainsSingle(await RunsAsync(handler));
        Assert.AreEqual(cut.Run, named.Run);
        Assert.AreEqual(await SnapshotIdAsync(handler), named.SnapshotId);
        Assert.AreEqual("docs", named.Set);
        Assert.AreEqual(Destination, named.OutputDirectory);
        Assert.IsGreaterThan(0UL, named.BeganAt);
        var notice = Assert.ContainsSingle(candidate => candidate.Key == NoticeKind + cut.Run, runtime.Notices.Unacknowledged);
        Assert.Contains($"restore-resume {cut.Run}", notice.Message, StringComparison.Ordinal);

        var resumed = await ResumeAsync(handler, cut.Run!);

        Assert.AreEqual("complete", resumed.Outcome);
        Assert.AreEqual(cut.Run, resumed.Run, "a quarantined run is resumed in its own folder, under its own name");
        Assert.AreEqual(cut.OutputDirectory, resumed.OutputDirectory);
        Assert.AreEqual("deeper", await File.ReadAllTextAsync(Path.Combine(resumed.OutputDirectory, "nested", "deeper.txt"), _timeout.Token));
        Assert.IsEmpty(await RunsAsync(handler), "a run that finished is named no more");
        Assert.IsFalse(runtime.Notices.Unacknowledged.Any(notice => notice.Key == NoticeKind + cut.Run), "and no longer noticed");
    }

    [TestMethod]
    public async Task ARunThatFailedFiles_IsNamed_AndResumedAsItWasAsked_UntilARunOfItFinishes()
    {
        // The console's shape, in place under rename, kept with the run and
        // asked again by the resume that names it: a file in the way stays,
        // and the restored copy is written beside it.
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var notes = Path.Combine(Destination, "notes.txt");
        Directory.CreateDirectory(Destination);
        await File.WriteAllTextAsync(notes, "the live file", _timeout.Token);
        var sound = Tamper(_harness.RepositoryPath);

        var failed = await RestoreAsync(handler, Destination, _timeout.Token, existing: "rename", inPlace: true);
        Assert.AreEqual("failed", failed.Outcome);
        var named = Assert.ContainsSingle(await RunsAsync(handler));
        Assert.AreEqual(failed.Run, named.Run);
        Assert.AreEqual("rename", named.Existing);
        Assert.IsTrue(named.InPlace);
        Assert.ContainsSingle(candidate => candidate.Key == NoticeKind + failed.Run, runtime.Notices.Unacknowledged);

        foreach (var (path, bytes) in sound)
        {
            await File.WriteAllBytesAsync(path, bytes, _timeout.Token);
        }

        var resumed = await ResumeAsync(handler, failed.Run!);

        Assert.AreEqual("complete", resumed.Outcome);
        Assert.AreEqual("the live file", await File.ReadAllTextAsync(notes, _timeout.Token), "the run is asked as it was: under rename");
        Assert.AreEqual(
            "hello",
            await File.ReadAllTextAsync(Assert.ContainsSingle(Directory.EnumerateFiles(Destination, "notes (restored *).txt")), _timeout.Token));
        Assert.IsFalse(Directory.Exists(Path.Combine(Destination, ".fbp-quarantine")), "and in place");
        Assert.IsEmpty(await RunsAsync(handler));
        Assert.IsFalse(runtime.Notices.Unacknowledged.Any(notice => notice.Key.StartsWith(NoticeKind, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ARunTheServiceDidNotSeeEnd_IsNoticedWhenTheServiceStartsAgain()
    {
        // A machine that stops mid-run ends the process before it can say so:
        // the run is on record, and nothing has noticed it.
        string run;
        await using (var first = await BackUpAndStartAsync())
        {
            run = (await CutShortAsync(new ServiceCommandHandler(first, RemoteBindingState.Off))).Run!;
        }

        File.Delete(Path.Combine(_harness.StateDirectory, "notices.json"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.AreEqual(run, Assert.ContainsSingle(await RunsAsync(handler)).Run);
        var notice = Assert.ContainsSingle(candidate => candidate.Key == NoticeKind + run, runtime.Notices.Unacknowledged);
        Assert.Contains($"restore-resume {run}", notice.Message, StringComparison.Ordinal);
        Assert.AreEqual("complete", (await ResumeAsync(handler, run)).Outcome);
    }

    [TestMethod]
    public async Task ARunNoticedAndAcknowledged_IsNotSaidAgain_WhenTheServiceStarts()
    {
        string run;
        await using (var first = await BackUpAndStartAsync())
        {
            run = (await CutShortAsync(new ServiceCommandHandler(first, RemoteBindingState.Off))).Run!;
            var notice = Assert.ContainsSingle(candidate => candidate.Key == NoticeKind + run, first.Notices.Unacknowledged);
            Assert.IsTrue(first.Notices.Acknowledge(notice.Id, 1));
        }

        await using var runtime = await StartAsync();

        Assert.IsFalse(runtime.Notices.Unacknowledged.Any(notice => notice.Key == NoticeKind + run), "a person who has seen it is not asked again");
        Assert.AreEqual(run, Assert.ContainsSingle(await RunsAsync(new ServiceCommandHandler(runtime, RemoteBindingState.Off))).Run);
    }

    [TestMethod]
    public async Task AResume_IsAskedUnderTheSessionThatAsksIt()
    {
        // A source serves only the session that unlocked it (FR-WOR-007),
        // and the run a resume asks again is that session's.
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var run = (await CutShortAsync(handler)).Run!;
        var source = await _harness.OpenGrantedSourceAsync(
            (command, token) => handler.ExecuteAsync(command with { SessionId = "session-7" }, token), "docs", null, _timeout.Token);

        var resumed = await handler.ExecuteAsync(
            new ResumeRestoreCommand(run, source.SourceId) { SessionId = "session-7" }, _timeout.Token);

        Assert.IsInstanceOfType<RestoreResult>(resumed, (resumed as ServiceError)?.Message);
    }

    [TestMethod]
    public async Task AResumeThatWillNotFit_IsRefused_UnlessTheCommandLineSaysToGoOn()
    {
        // The run's own refusal of a restore that will not fit holds for its
        // resume, and restore-resume's --ignore-free-space reaches it.
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        var run = (await CutShortAsync(handler)).Run!;
        _available = 1;

        var refused = await CliAsync("restore-resume", run);
        Assert.AreNotEqual(0, refused.ExitCode, refused.Output + refused.Error);
        Assert.Contains("would not fit", refused.Output + refused.Error, StringComparison.Ordinal);
        Assert.AreEqual(run, Assert.ContainsSingle(await RunsAsync(handler)).Run, "a refused resume leaves the run as it was");

        var resumed = await CliAsync("restore-resume", run, "--ignore-free-space");
        Assert.AreEqual(0, resumed.ExitCode, resumed.Output + resumed.Error);
        Assert.IsEmpty(await RunsAsync(handler));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's DACL")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task AResumeThatAFolderWouldOpenAFileWiderIn_IsRefused_UnlessTheCommandLineSaysToGoOn()
    {
        // The run went on when told, and its resume asks again: what was
        // acknowledged once is not taken as acknowledged for every run after.
        const string Stranger = "S-1-5-21-9-9-9-4242";
        const string Users = "S-1-5-32-545";
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("private.txt", "kept to two accounts");
        WindowsAcls.KeepTo(Path.Combine(_harness.SourceRoot, "private.txt"), Stranger);
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");
        await _harness.SetupAsync();
        WindowsAcls.Sharing(Destination, Users);
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        var run = (await CutShortAsync(handler, acknowledgeWiderAccess: true)).Run!;
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ResumeRestoreCommand(run, source.SourceId), _timeout.Token), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains(Users, refused.Message, StringComparison.Ordinal);
        Assert.AreEqual(run, Assert.ContainsSingle(await RunsAsync(handler)).Run, "a refused resume leaves the run as it was");

        var resumed = await CliAsync("restore-resume", run, "--acknowledge-wider-access");
        Assert.AreEqual(0, resumed.ExitCode, resumed.Output + resumed.Error);
        Assert.IsEmpty(await RunsAsync(handler));
    }

    [TestMethod]
    public async Task ARunLetGo_IsNamedNoMore_NorNoticed()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var run = (await CutShortAsync(handler)).Run!;

        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new ForgetRestoreRunCommand(run), _timeout.Token));

        Assert.IsEmpty(await RunsAsync(handler));
        Assert.IsFalse(runtime.Notices.Unacknowledged.Any(notice => notice.Key == NoticeKind + run));
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ForgetRestoreRunCommand(run), _timeout.Token), out var again);
        Assert.AreEqual(ServiceErrorReason.NotFound, again.Reason);
    }

    [TestMethod]
    public async Task AResume_TakesUpThePartsItsRunHadAssembled_ReadingNoneOfThemAgain()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var cut = await CutShortAsync(handler);
        KeepPart(cut.Run!, "hello", "hello");
        KeepPart(cut.Run!, "deeper", "deeper");

        var resumed = await ResumeTamperedAsync(handler, cut.Run!);

        Assert.AreEqual("complete", resumed.Outcome, string.Join("; ", resumed.FailedSample ?? []));
        Assert.AreEqual("hello", await File.ReadAllTextAsync(Path.Combine(resumed.OutputDirectory, "notes.txt"), _timeout.Token));
        Assert.AreEqual("deeper", await File.ReadAllTextAsync(Path.Combine(resumed.OutputDirectory, "nested", "deeper.txt"), _timeout.Token));
        Assert.IsFalse(Directory.Exists(PartsOf(cut.Run!)), "a run that finishes keeps no parts");
        Assert.AreEqual(2L, resumed.ResumedWithin, "the result counts the files it took up from parts");
        var receipt = FallbackPlan.Restore.RestoreReceipt.ReadKept(
            Path.GetDirectoryName(resumed.ReceiptPath!)!, Path.GetFileNameWithoutExtension(resumed.ReceiptPath!))!.Receipt!;
        Assert.AreEqual(5UL, receipt.Items.Single(item => item.Path == "notes.txt").ResumedAt, "and the receipt where each was resumed");
        Assert.AreEqual(6UL, receipt.Items.Single(item => item.Path == "nested/deeper.txt").ResumedAt);
        Assert.IsInstanceOfType<RestoreReceiptsResult>(
            await handler.ExecuteAsync(new ListRestoreReceiptsCommand(), _timeout.Token), out var kept);
        Assert.AreEqual(
            2L,
            kept.Receipts.Single(listed => listed.Run == Path.GetFileNameWithoutExtension(resumed.ReceiptPath!)).ResumedWithin,
            "and its kept receipt is listed with them");
    }

    [TestMethod]
    public async Task RestoreResume_SaysHowManyFilesItTookUpFromTheirParts()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        var cut = await CutShortAsync(handler);
        KeepPart(cut.Run!, "hello", "hello");
        KeepPart(cut.Run!, "deeper", "deeper");

        var sound = Tamper(_harness.RepositoryPath);
        HostHarness.Invocation resumed;
        try
        {
            resumed = await CliAsync("restore-resume", cut.Run!);
        }
        finally
        {
            foreach (var (path, bytes) in sound)
            {
                await File.WriteAllBytesAsync(path, bytes, _timeout.Token);
            }
        }

        Assert.AreEqual(0, resumed.ExitCode, resumed.Output + resumed.Error);
        Assert.Contains("2 file(s) were resumed from what the run had assembled of them", resumed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AResume_TakesUpOnlyAPartThatProves()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var cut = await CutShortAsync(handler);
        KeepPart(cut.Run!, "hello", "jello");
        KeepPart(cut.Run!, "deeper", "deeper");

        var resumed = await ResumeTamperedAsync(handler, cut.Run!);

        Assert.AreEqual("failed", resumed.Outcome);
        Assert.Contains("notes.txt", Assert.ContainsSingle(resumed.FailedSample!), StringComparison.Ordinal);
        Assert.AreEqual("deeper", await File.ReadAllTextAsync(Path.Combine(resumed.OutputDirectory, "nested", "deeper.txt"), _timeout.Token));
    }

    [TestMethod]
    public async Task AResumeInPlace_CarriesItsRunsPartsToTheNameItRunsUnder()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var cut = await CutShortAsync(handler, inPlace: true);
        KeepPart(cut.Run!, "hello", "hello");
        KeepPart(cut.Run!, "deeper", "deeper");

        var resumed = await ResumeTamperedAsync(handler, cut.Run!);

        Assert.AreEqual("complete", resumed.Outcome, string.Join("; ", resumed.FailedSample ?? []));
        Assert.AreNotEqual(cut.Run, resumed.Run, "a run in place resumes under a name of its own");
        Assert.AreEqual("hello", await File.ReadAllTextAsync(Path.Combine(Destination, "notes.txt"), _timeout.Token));
        Assert.IsFalse(Directory.Exists(PartsOf(cut.Run!)));
        Assert.IsFalse(Directory.Exists(PartsOf(resumed.Run!)));
    }

    [TestMethod]
    public async Task ARunLetGo_TakesItsPartsWithIt()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var run = (await CutShortAsync(handler)).Run!;
        KeepPart(run, "hello", "hello");

        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new ForgetRestoreRunCommand(run), _timeout.Token));

        Assert.IsFalse(Directory.Exists(PartsOf(run)));
    }

    [TestMethod]
    public async Task RestoreResume_LettingEveryRunGo_LetsEachTheServiceLeftUnfinishedGo_AndItsPartsWithIt()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        var run = (await CutShortAsync(handler)).Run!;
        KeepPart(run, "hello", "hello");

        var letGo = await CliAsync("restore-resume", "--forget-all");

        Assert.AreEqual(0, letGo.ExitCode, letGo.Output + letGo.Error);
        Assert.Contains("let go 1 restore run the service left unfinished", letGo.Output, StringComparison.Ordinal);
        Assert.IsEmpty(await RunsAsync(handler));
        Assert.IsFalse(Directory.Exists(PartsOf(run)));
    }

    [TestMethod]
    public async Task RestoreResume_LettingRunsGoByAge_KeepsARunBegunWithinTheDays_AndItsParts()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        var run = (await CutShortAsync(handler)).Run!;
        KeepPart(run, "hello", "hello");

        var letGo = await CliAsync("restore-resume", "--forget-all", "--older-than-days", "30");

        Assert.AreEqual(0, letGo.ExitCode, letGo.Output + letGo.Error);
        Assert.Contains("1 restore run begun within 30 days stays", letGo.Output, StringComparison.Ordinal);
        Assert.AreEqual(run, Assert.ContainsSingle(await RunsAsync(handler)).Run, "a run begun within the days stays");
        Assert.IsTrue(Directory.Exists(PartsOf(run)), "and so do its parts");
    }

    [TestMethod]
    public async Task ThePartsOfARunNoLongerOnRecord_GoWhenTheServiceStarts()
    {
        string kept;
        await using (var runtime = await BackUpAndStartAsync())
        {
            kept = (await CutShortAsync(new ServiceCommandHandler(runtime, RemoteBindingState.Off))).Run!;
        }

        KeepPart(kept, "hello", "hello");
        KeepPart(NoSuchRun, "hello", "hello");

        await using var again = await StartAsync();

        Assert.IsFalse(Directory.Exists(PartsOf(NoSuchRun)), "nothing names that run, so nothing would take its parts up");
        Assert.IsTrue(Directory.Exists(PartsOf(kept)), "a run left unfinished keeps its parts for its resume");
    }

    [TestMethod]
    public async Task AResumeOfARunNotOnRecord_IsRefused_AndWritesNothing()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ResumeRestoreCommand(NoSuchRun, source.SourceId), _timeout.Token), out var refused);

        Assert.AreEqual(ServiceErrorReason.NotFound, refused.Reason);
        Assert.Contains(NoSuchRun, refused.Message, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(Destination));
    }

    [TestMethod]
    [DataRow("../../escaped")]
    [DataRow("0123456789ABCDEF")]
    public async Task AResumeOrALettingGo_NamingWhatIsNotARun_IsRefused(string named)
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ResumeRestoreCommand(named), _timeout.Token), out var resumed);
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ForgetRestoreRunCommand(named), _timeout.Token), out var forgotten);

        Assert.AreEqual(ServiceErrorReason.InvalidArgument, resumed.Reason);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, forgotten.Reason);
    }

    [TestMethod]
    public async Task APlanOfAResume_IsMeasuredWhereTheRunLanded_AndSaysWhatItLanded_AndRunsNothing()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var cut = await CutShortAsync(handler);
        Directory.CreateDirectory(cut.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(cut.OutputDirectory, "notes.txt"), "hello", _timeout.Token);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        var answer = await handler.ExecuteAsync(
            new PlanRestoreCommand(
                await SnapshotIdAsync(handler), null, source.SourceId, OutputDirectory: Destination, Resume: cut.Run),
            _timeout.Token);

        Assert.IsInstanceOfType<RestorePlanResult>(answer, out var plan, (answer as ServiceError)?.Message);
        Assert.AreEqual(2L, plan.Files);
        Assert.AreEqual(1L, plan.Landed, "the file the run landed at its length");
        Assert.IsFalse(File.Exists(Path.Combine(cut.OutputDirectory, "nested", "deeper.txt")), "a plan restores nothing");
        Assert.AreEqual(cut.Run, Assert.ContainsSingle(await RunsAsync(handler)).Run, "and leaves the run unfinished");

        var afresh = await handler.ExecuteAsync(
            new PlanRestoreCommand(await SnapshotIdAsync(handler), null, source.SourceId, OutputDirectory: Destination),
            _timeout.Token);
        Assert.IsInstanceOfType<RestorePlanResult>(afresh, out var unresumed, (afresh as ServiceError)?.Message);
        Assert.IsNull(unresumed.Landed, "a plan of a run that resumes nothing names nothing landed");
    }

    [TestMethod]
    public async Task APlanOfAResume_ReadsNothingOfWhatItsRunLanded_NorOfWhatItsPartsProve()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var cut = await CutShortAsync(handler);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);
        var snapshot = await SnapshotIdAsync(handler);
        async Task<long> ReadsAsync(string? resume)
        {
            var answer = await handler.ExecuteAsync(
                new PlanRestoreCommand(snapshot, null, source.SourceId, OutputDirectory: Destination, Resume: resume),
                _timeout.Token);
            Assert.IsInstanceOfType<RestorePlanResult>(answer, out var plan, (answer as ServiceError)?.Message);
            Assert.IsNotNull(plan.ReadBytes);
            return plan.ReadBytes.Value;
        }

        var afresh = await ReadsAsync(null);
        Assert.AreEqual(afresh, await ReadsAsync(cut.Run), "a run that landed nothing and kept no part leaves everything to read");

        Directory.CreateDirectory(cut.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(cut.OutputDirectory, "notes.txt"), "hello", _timeout.Token);
        var landedOne = await ReadsAsync(cut.Run);
        Assert.IsLessThan(afresh, landedOne, "a file the run landed at its length is not read again");

        KeepPart(cut.Run!, "deeper", "deeper");
        Assert.IsLessThan(landedOne, await ReadsAsync(cut.Run), "nor is what a part proves");

        KeepPart(cut.Run!, "deeper", "deepen");
        Assert.AreEqual(landedOne, await ReadsAsync(cut.Run), "a part that does not prove saves nothing");
        Assert.AreEqual(
            "deepen",
            await File.ReadAllTextAsync(
                Path.Combine(PartsOf(cut.Run!), $"{Convert.ToHexStringLower(SHA256.HashData("deeper"u8))}.part"), _timeout.Token),
            "and a plan leaves a part as it found it");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "quarantined")]
    [DataRow(true, DisplayName = "in place, whose parts lie under the run it continues until it carries them")]
    public async Task AResumesWorkingSpace_IsCreditedWhatItsPartsHold_SoItRunsWhereWholeFilesWouldNotFit(bool inPlace)
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var cut = await CutShortAsync(handler, inPlace);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);
        var snapshot = await SnapshotIdAsync(handler);
        async Task<long> NeedsAsync()
        {
            var answer = await handler.ExecuteAsync(
                new PlanRestoreCommand(snapshot, null, source.SourceId, OutputDirectory: Destination, InPlace: inPlace, Resume: cut.Run),
                _timeout.Token);
            Assert.IsInstanceOfType<RestorePlanResult>(answer, out var plan, (answer as ServiceError)?.Message);
            return Assert.ContainsSingle(plan.Space!).NeededBytes;
        }

        // The parts and the folder share a volume, which needs room for
        // both files where they land and for the larger still to assemble.
        var whole = await NeedsAsync();
        KeepPart(cut.Run!, "deeper", "deeper");
        Assert.AreEqual(whole, await NeedsAsync(), "the other file is as large still to assemble");
        KeepPart(cut.Run!, "hello", "hello");
        var credited = await NeedsAsync();
        Assert.AreEqual(whole - 4096, credited, "with both held, nothing is left to assemble");

        _available = credited;
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(new ResumeRestoreCommand(cut.Run!, source.SourceId), _timeout.Token),
            out var resumed);
        Assert.AreEqual("complete", resumed.Outcome, "the run is measured as its plan was");
        Assert.AreEqual(inPlace, resumed.Run != cut.Run);
        Assert.AreEqual(2L, resumed.ResumedWithin);
    }

    [TestMethod]
    public async Task AResumeWhosePartsHoldNothing_IsRefused_WhereWholeFilesWouldNotFit()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var cut = await CutShortAsync(handler);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);
        KeepPart(cut.Run!, "hello", "jello");
        KeepPart(cut.Run!, "deeper", "deepen");
        Assert.IsInstanceOfType<RestorePlanResult>(
            await handler.ExecuteAsync(
                new PlanRestoreCommand(await SnapshotIdAsync(handler), null, source.SourceId, OutputDirectory: Destination, Resume: cut.Run),
                _timeout.Token),
            out var plan);
        _available = Assert.ContainsSingle(plan.Space!).NeededBytes - 4096;

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new ResumeRestoreCommand(cut.Run!, source.SourceId), _timeout.Token),
            out var refused);
        Assert.Contains("would not fit", refused.Message, StringComparison.Ordinal);
        Assert.AreEqual(cut.Run, Assert.ContainsSingle(await RunsAsync(handler)).Run, "a part that proves nothing is credited nothing");
    }

    [TestMethod]
    public async Task ARunLeftUnfinished_SaysWhatItsPartsHold()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        var cut = await CutShortAsync(handler);
        Assert.AreEqual(0L, Assert.ContainsSingle(await RunsAsync(handler)).Held, "a run that kept no part holds nothing");

        KeepPart(cut.Run!, "hello", "hel");
        KeepPart(cut.Run!, "deeper", "deeper");

        Assert.AreEqual(9L, Assert.ContainsSingle(await RunsAsync(handler)).Held, "what its parts hold under the state directory");
        var listed = await CliAsync("restore-resume");
        Assert.AreEqual(0, listed.ExitCode, listed.Output + listed.Error);
        Assert.Contains("holding 9 bytes it had assembled", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task APlanOfARunByItsName_IsAskedUnderTheSessionThatAsksIt()
    {
        // A source serves only the session that unlocked it (FR-WOR-007).
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var run = (await CutShortAsync(handler)).Run!;
        var source = await _harness.OpenGrantedSourceAsync(
            (command, token) => handler.ExecuteAsync(command with { SessionId = "session-7" }, token), "docs", null, _timeout.Token);

        var planned = await handler.ExecuteAsync(
            new PlanResumeRestoreCommand(run, source.SourceId) { SessionId = "session-7" }, _timeout.Token);

        Assert.IsInstanceOfType<RestorePlanResult>(planned, (planned as ServiceError)?.Message);
    }

    [TestMethod]
    public async Task ARunLeftUnfinished_IsPlannedByItsNameAlone_AsItWasAsked()
    {
        // Asked in place, so what it landed is in the folder itself: a plan
        // that took the run as quarantined would look where it never wrote.
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Directory.CreateDirectory(Destination);
        var failed = await RestoreAsync(handler, Destination, _timeout.Token, inPlace: true, tampered: true);
        Assert.AreEqual("failed", failed.Outcome);
        await File.WriteAllTextAsync(Path.Combine(Destination, "notes.txt"), "hello", _timeout.Token);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        var answer = await handler.ExecuteAsync(new PlanResumeRestoreCommand(failed.Run!, source.SourceId), _timeout.Token);

        Assert.IsInstanceOfType<RestorePlanResult>(answer, out var plan, (answer as ServiceError)?.Message);
        Assert.AreEqual(1L, plan.Landed);
        Assert.IsFalse(File.Exists(Path.Combine(Destination, "nested", "deeper.txt")), "a plan restores nothing");
        Assert.AreEqual(failed.Run, Assert.ContainsSingle(await RunsAsync(handler)).Run);
    }

    [TestMethod]
    public async Task APlanOfAResumeWhoseFolderIsGone_IsRefused_AsTheResumeWouldBe()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var cut = await CutShortAsync(handler);
        if (Directory.Exists(cut.OutputDirectory))
        {
            Directory.Delete(cut.OutputDirectory, recursive: true);
        }

        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new PlanResumeRestoreCommand(cut.Run!, source.SourceId), _timeout.Token), out var refused);

        Assert.AreEqual(ServiceErrorReason.NotFound, refused.Reason);
        Assert.Contains(cut.Run!, refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task APlanOfARunNotOnRecord_OrOfWhatIsNotARun_IsRefused()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new PlanResumeRestoreCommand(NoSuchRun, source.SourceId), _timeout.Token), out var unknown);
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new PlanResumeRestoreCommand("../../escaped", source.SourceId), _timeout.Token), out var named);
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new PlanRestoreCommand(
                    await SnapshotIdAsync(handler), null, source.SourceId, OutputDirectory: Destination, Resume: "../../escaped"),
                _timeout.Token),
            out var asked);

        Assert.AreEqual(ServiceErrorReason.NotFound, unknown.Reason);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, named.Reason);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, asked.Reason);
    }

    [TestMethod]
    public async Task RestoreResume_WithAPlan_SaysWhatTheRunLanded_AndResumesNothing()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        var cut = await CutShortAsync(handler);
        Directory.CreateDirectory(cut.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(cut.OutputDirectory, "notes.txt"), "hello", _timeout.Token);

        var planned = await CliAsync("restore-resume", cut.Run!, "--plan");

        Assert.AreEqual(0, planned.ExitCode, planned.Output + planned.Error);
        Assert.Contains("resume: 1 file(s) already landed", planned.Output, StringComparison.Ordinal);
        Assert.AreEqual(cut.Run, Assert.ContainsSingle(await RunsAsync(handler)).Run, "a plan resumes nothing");
    }

    public void Dispose()
    {
        _cutShortAt?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();
    }

    private string Destination => Path.Combine(_harness.WorkPath, "restored");

    /// <summary>Runs a restore stopped once it is named, as a caller hanging up stops one.</summary>
    private async Task<RestoreResult> CutShortAsync(
        ServiceCommandHandler handler, bool inPlace = false, bool acknowledgeWiderAccess = false)
    {
        _cutShortAt = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token);
        try
        {
            var cut = await RestoreAsync(
                handler, Destination, _cutShortAt.Token, inPlace: inPlace, acknowledgeWiderAccess: acknowledgeWiderAccess);
            Assert.AreEqual("cancelled", cut.Outcome);
            Assert.IsNotNull(cut.Run);
            return cut;
        }
        finally
        {
            _cutShortAt.Dispose();
            _cutShortAt = null;
        }
    }

    private async Task<RestoreResult> RestoreAsync(
        ServiceCommandHandler handler, string destination, CancellationToken cancellationToken,
        string? existing = null, bool inPlace = false, bool tampered = false, bool acknowledgeWiderAccess = false)
    {
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);
        var snapshot = await SnapshotIdAsync(handler);
        List<(string Path, byte[] Bytes)> sound = tampered ? Tamper(_harness.RepositoryPath) : [];
        try
        {
            var answer = await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshot, null, destination, Source: source.SourceId, Existing: existing, InPlace: inPlace,
                    AcknowledgeWiderAccess: acknowledgeWiderAccess),
                cancellationToken);
            Assert.IsInstanceOfType<RestoreResult>(answer, out var restored, (answer as ServiceError)?.Message);
            return restored;
        }
        finally
        {
            foreach (var (path, bytes) in sound)
            {
                await File.WriteAllBytesAsync(path, bytes, _timeout.Token);
            }
        }
    }

    /// <summary>Runs a CLI verb as a client of the running service.</summary>
    private Task<HostHarness.Invocation> CliAsync(params string[] verbAndArguments) =>
        HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            [
                .. verbAndArguments,
                "--repo", _harness.RepositoryPath,
                "--passphrase-env", _harness.PassphraseVariable,
                "--state", _harness.StateDirectory,
            ]);

    private string PartsOf(string run) => Path.Combine(_harness.StateDirectory, "restore-parts", run);

    /// <summary>Keeps a part of the snapshot's file holding <paramref name="original"/>, as a run cut short in it would.</summary>
    private void KeepPart(string run, string original, string held)
    {
        Directory.CreateDirectory(PartsOf(run));
        File.WriteAllText(
            Path.Combine(PartsOf(run), $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original)))}.part"), held);
    }

    /// <summary>Resumes a run while no content the store holds reads, so only what a part holds can land.</summary>
    private async Task<RestoreResult> ResumeTamperedAsync(ServiceCommandHandler handler, string run)
    {
        var sound = Tamper(_harness.RepositoryPath);
        try
        {
            return await ResumeAsync(handler, run);
        }
        finally
        {
            foreach (var (path, bytes) in sound)
            {
                await File.WriteAllBytesAsync(path, bytes, _timeout.Token);
            }
        }
    }

    private async Task<RestoreResult> ResumeAsync(ServiceCommandHandler handler, string run)
    {
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);
        var answer = await handler.ExecuteAsync(new ResumeRestoreCommand(run, source.SourceId), _timeout.Token);
        Assert.IsInstanceOfType<RestoreResult>(answer, out var resumed, (answer as ServiceError)?.Message);
        return resumed;
    }

    private async Task<IReadOnlyList<RestoreRunDescriptor>> RunsAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<RestoreRunsResult>(
            await handler.ExecuteAsync(new ListRestoreRunsCommand(), _timeout.Token), out var listed);
        return listed.Runs;
    }

    private async Task<string> SnapshotIdAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        return Assert.ContainsSingle(snapshots.Snapshots).SnapshotId;
    }

    /// <summary>
    /// Flips every data blob's bytes past its envelope, so every file fails
    /// to read and none is written.
    /// </summary>
    /// <returns>Each blob's path and its sound bytes, to put back.</returns>
    private static List<(string Path, byte[] Bytes)> Tamper(string repositoryRoot)
    {
        var files = Directory.GetFiles(Path.Combine(repositoryRoot, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(files, "the archive must hold data blobs for this to test anything");

        var sound = new List<(string, byte[])>();
        foreach (var path in files)
        {
            var bytes = File.ReadAllBytes(path);
            sound.Add((path, [.. bytes]));
            for (var i = 200; i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            File.WriteAllBytes(path, bytes);
        }

        return sound;
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "mkfifo", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int MkFifo(string path, uint mode);

    private async Task<ServiceRuntime> BackUpAndStartAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteSourceFile("nested/deeper.txt", "deeper");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");
        await _harness.SetupAsync();
        return await StartAsync();
    }

    private async Task<ServiceRuntime> StartAsync() =>
        await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,

                // Asked as a run measures its room, before it writes: the
                // seam a test stops a run at, as its caller hanging up would.
                AvailableBytesProbe = _ =>
                {
                    _cutShortAt?.Cancel();
                    return _available;
                },
            },
            _timeout.Token);
}

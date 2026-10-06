using System.CommandLine;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The passphrase gate (FR-WOR-007, ADR-0089): the service names a file a
/// backup holds only to a caller who proved the set's passphrase for the
/// action at hand. The proof is a restore source opened under a verified
/// grant (FR-WOR-004), and it serves only the session that opened it.
/// Without one, listing a snapshot, planning or running a restore, and a
/// run's changes and failures are refused by name, and a set's change
/// preview answers its counts and the names on disk now while withholding
/// the names only the backup holds. The service's own drill restores
/// without a person, as <c>RecoveryDrillTests</c> go on showing.
/// </summary>
[TestClass]
public sealed class PassphraseGateTests : IDisposable
{
    private static readonly Domain.Configuration.Argon2Parameters Fast = new()
    {
        MemoryKiB = 64,
        Iterations = 1,
        Parallelism = 1,
    };

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task EveryVerbThatNamesABackupsFiles_WithoutAnUnlockedSource_IsRefusedByName()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "private notes");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var job = await RunBackupAsync(runtime, handler, "docs");
        var snapshotId = job.SnapshotId!;
        var output = Path.Combine(_harness.WorkPath, "restored");

        ServiceCommand[] askingForNames =
        [
            new ListDirectoryCommand(snapshotId, null),
            new PlanRestoreCommand(snapshotId, null),
            new RunRestoreCommand(snapshotId, null, output, InPlace: true),
            new JobChangesCommand(job.Id),
            new JobFailuresCommand(job.Id),
            new OpenRestoreSourceCommand("docs"),
        ];

        foreach (var command in askingForNames)
        {
            var refused = await handler.ExecuteAsync(command, Timeout);
            Assert.IsInstanceOfType<ServiceError>(refused, out var error, $"{command.GetType().Name} answered {refused.GetType().Name}");
            Assert.AreEqual(ServiceErrorReason.Refused, error.Reason, $"{command.GetType().Name}: {error.Message}");
            Assert.Contains("passphrase", error.Message, StringComparison.Ordinal, command.GetType().Name);
        }

        Assert.IsFalse(Directory.Exists(output), "a refused restore writes nothing");
    }

    [TestMethod]
    public async Task ASourceUnlockedWithThePassphrase_AnswersEveryOneOfThem()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "private notes");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var job = await RunBackupAsync(runtime, handler, "docs");
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout);

        Assert.IsInstanceOfType<DirectoryResult>(
            await handler.ExecuteAsync(new ListDirectoryCommand(job.SnapshotId!, null, Source: source.SourceId), Timeout),
            out var listed);
        Assert.AreEqual("notes.txt", Assert.ContainsSingle(listed.Entries).Name);

        Assert.IsInstanceOfType<RestorePlanResult>(
            await handler.ExecuteAsync(new PlanRestoreCommand(job.SnapshotId!, null, Source: source.SourceId), Timeout));

        Assert.IsInstanceOfType<JobChangesResult>(
            await handler.ExecuteAsync(new JobChangesCommand(job.Id, Source: source.SourceId), Timeout),
            out var changes);
        Assert.AreEqual("notes.txt", Assert.ContainsSingle(changes.New.Sample));

        Assert.IsInstanceOfType<JobFailuresResult>(
            await handler.ExecuteAsync(new JobFailuresCommand(job.Id, Source: source.SourceId), Timeout),
            out var failures);
        Assert.AreEqual(0L, failures.Failures);
    }

    [TestMethod]
    public async Task ASourceAnotherSessionUnlocked_ServesNothingToIt()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "private notes");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var job = await RunBackupAsync(runtime, handler, "docs");

        var accounts = Path.Combine(_harness.WorkPath, "accounts");
        Directory.CreateDirectory(accounts);
        var users = UserStore.Open(accounts, throttle: new UserStore.ThrottlePolicy(
            TimeSpan.FromMilliseconds(0.25), TimeSpan.FromMilliseconds(4)));
        users.Create("ben", "A-good-passw0rd9", parameters: Fast);
        users.Create("amy", "An0ther-passw0rd", parameters: Fast);
        var sessions = new SessionRegistry();

        var ben = await SignInAsync(handler, users, sessions, "ben", "A-good-passw0rd9");
        var amy = await SignInAsync(handler, users, sessions, "amy", "An0ther-passw0rd");
        var benAgain = await SignInAsync(handler, users, sessions, "ben", "A-good-passw0rd9");

        var source = await _harness.OpenGrantedSourceAsync(ben.ExecuteAsync, "docs", null, Timeout);

        // The passphrase was proved by ben's session for this action. Another
        // account's session, and another session of the same account, each
        // owe their own proof.
        foreach (var other in new[] { amy, benAgain })
        {
            ServiceCommand[] borrowing =
            [
                new ListDirectoryCommand(job.SnapshotId!, null, Source: source.SourceId),
                new PlanRestoreCommand(job.SnapshotId!, null, Source: source.SourceId),
                new JobChangesCommand(job.Id, Source: source.SourceId),
                new JobFailuresCommand(job.Id, Source: source.SourceId),
                new PreviewSetChangesCommand("docs", Source: source.SourceId),
            ];

            foreach (var command in borrowing)
            {
                Assert.IsInstanceOfType<ServiceError>(
                    await other.ExecuteAsync(command, Timeout), out var error, command.GetType().Name);
                Assert.AreEqual(ServiceErrorReason.Refused, error.Reason, $"{command.GetType().Name}: {error.Message}");
            }

            // Closing somebody else's source is acknowledged as closing an
            // unknown one is, and closes nothing.
            Assert.IsInstanceOfType<AcknowledgedResult>(
                await other.ExecuteAsync(new CloseRestoreSourceCommand(source.SourceId), Timeout));
        }

        Assert.IsInstanceOfType<DirectoryResult>(
            await ben.ExecuteAsync(new ListDirectoryCommand(job.SnapshotId!, null, Source: source.SourceId), Timeout),
            "the session that unlocked the source is still served by it");
    }

    [TestMethod]
    public async Task ARunsFiles_NeedTheRunsOwnSetUnlocked()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "private notes");
        _harness.WriteConfiguration("every 1h", withSecondSet: true);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var docsRun = await RunBackupAsync(runtime, handler, "docs");
        _ = await RunBackupAsync(runtime, handler, "extra");

        // Two sets may answer to two passphrases (an adopted set keeps the
        // salt it was born under), so one set's proof opens nothing of
        // another's.
        var extra = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "extra", null, Timeout);
        foreach (var command in new ServiceCommand[]
        {
            new JobChangesCommand(docsRun.Id, Source: extra.SourceId),
            new JobFailuresCommand(docsRun.Id, Source: extra.SourceId),
            new PreviewSetChangesCommand("docs", Source: extra.SourceId),
        })
        {
            Assert.IsInstanceOfType<ServiceError>(await handler.ExecuteAsync(command, Timeout), out var error);
            Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason, error.Message);
            Assert.Contains("extra", error.Message, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task AChangePreview_WithoutASource_WithholdsOnlyTheNamesTheBackupAloneHolds()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("keep.txt", "stays");
        _harness.WriteSourceFile("gone.txt", "will be deleted");
        _harness.WriteSourceFile("photos/beach.jpg", new string('p', 1_000));
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        _ = await RunBackupAsync(runtime, handler, "docs");

        // Arrival before deletion: the other way round, the new file can take
        // the deleted one's inode and read as a move.
        _harness.WriteSourceFile("fresh.txt", "newly arrived");
        File.Delete(Path.Combine(_harness.SourceRoot, "gone.txt"));

        // The set editor asks this as rules are ticked, so it is answered
        // without the passphrase: the counts, and the names on disk now,
        // which the folder picker already shows anyone signed in. What only
        // the backup holds — a deleted file, and one the draft's rules stop
        // capturing — is counted and not named.
        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await handler.ExecuteAsync(new PreviewSetChangesCommand("docs", ExcludeRules: ["photos"]), Timeout),
            out var withheld);
        Assert.IsTrue(withheld.NamesWithheld);
        Assert.AreEqual("fresh.txt", Assert.ContainsSingle(withheld.New.Sample));
        Assert.AreEqual(1L, withheld.Deleted.Count);
        Assert.IsEmpty(withheld.Deleted.Sample);
        Assert.AreEqual(1L, withheld.NoLongerIncluded.Count);
        Assert.IsEmpty(withheld.NoLongerIncluded.Sample);

        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout);
        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await handler.ExecuteAsync(
                new PreviewSetChangesCommand("docs", ExcludeRules: ["photos"], Source: source.SourceId), Timeout),
            out var named);
        Assert.IsFalse(named.NamesWithheld);
        Assert.AreEqual("gone.txt", Assert.ContainsSingle(named.Deleted.Sample));
        Assert.AreEqual("photos/beach.jpg", Assert.ContainsSingle(named.NoLongerIncluded.Sample));

        // A source that is not this session's to use is an error, never a
        // quiet fall back to the withheld answer.
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new PreviewSetChangesCommand("docs", Source: "0000000000000000"), Timeout),
            out var expired);
        Assert.AreEqual(ServiceErrorReason.NotFound, expired.Reason);

        // A set that has never backed up has nothing of a backup's to name.
        _harness.AddConfiguredSet(new string('b', 32), "fresh", "vault");
        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await handler.ExecuteAsync(new PreviewSetChangesCommand("fresh"), Timeout), out var brandNew);
        Assert.IsFalse(brandNew.NamesWithheld);
        Assert.IsNull(brandNew.BaselineSnapshotId);
    }

    [TestMethod]
    public async Task TheChangesVerb_NamesWhatOnlyTheBackupHolds_OnlyWithThePassphrase()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("keep.txt", "stays");
        _harness.WriteSourceFile("gone.txt", "will be deleted");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        _ = await RunBackupAsync(runtime, handler, "docs");
        File.Delete(Path.Combine(_harness.SourceRoot, "gone.txt"));
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        // Counted, not named, and the verb says what would name it.
        var withheld = await CliAsync("changes");
        Assert.AreEqual(0, withheld.ExitCode, withheld.All);
        Assert.Contains("1 deleted", withheld.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("gone.txt", withheld.All, StringComparison.Ordinal);
        Assert.Contains("--passphrase-env", withheld.All, StringComparison.Ordinal);

        var named = await CliAsync("changes", "--passphrase-env", _harness.PassphraseVariable);
        Assert.AreEqual(0, named.ExitCode, named.All);
        Assert.Contains("  gone.txt", named.Output, StringComparison.Ordinal);
    }

    private Task<HostHarness.Invocation> CliAsync(params string[] arguments) =>
        HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            [.. arguments, "--state", _harness.StateDirectory]);

    private static async Task<AuthenticatingService> SignInAsync(
        ServiceCommandHandler handler, UserStore users, SessionRegistry sessions, string user, string password)
    {
        var connection = new AuthenticatingService(handler, users, sessions);
        Assert.IsInstanceOfType<SessionResult>(
            await connection.ExecuteAsync(new LoginCommand(user, password), CancellationToken.None));
        return connection;
    }

    private async Task<JobDescriptor> RunBackupAsync(ServiceRuntime runtime, ServiceCommandHandler handler, string setName)
    {
        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand(setName, Full: false), Timeout), out var accepted);

        while (!runtime.Jobs.Jobs.Any(job => job.Id == accepted.JobId && JobStateStore.HasSettled(job.State)))
        {
            await Task.Delay(25, Timeout);
        }

        Assert.IsInstanceOfType<JobsResult>(
            await handler.ExecuteAsync(new ListJobsCommand(ActiveOnly: false), Timeout), out var jobs);
        var run = jobs.Jobs.Single(job => job.Id == accepted.JobId);
        Assert.IsNotNull(run.SnapshotId, $"the {setName} run committed nothing: {run.State} {run.Detail}");
        return run;
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }
}

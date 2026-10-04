using System.CommandLine;
using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// What the CLI becomes (ADR-0028 §3): a client, with an explicit direct mode
/// when no service is running.
/// Establishes FR-SVC-008, the CLI half of FR-SVC-021, the CLI half of
/// FR-VER-003's report of a circuit, and the CLI half of FR-GC-008's granted
/// collection run.
/// </summary>
[TestClass]
public sealed class ClientModeTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    [TestMethod]
    public async Task WriteCommand_NoServiceIsRunning_TakesDirectModeAndSaysSo()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");

        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "backup", _harness.SourceRoot,
            "--repo", _harness.RepositoryPath,
            "--passphrase-env", _harness.PassphraseVariable,
            "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("mode: direct", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DirectWrite_AServiceHoldsTheWriterRole_IsRefusedNamingTheHolder()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();

        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "backup", _harness.SourceRoot,
            "--repo", _harness.RepositoryPath,
            "--passphrase-env", _harness.PassphraseVariable,
            "--state", _harness.StateDirectory);

        // FR-SVC-002: it never proceeds anyway. Before the writer role existed
        // this command would have run, drawn from the same sequence space as
        // the service, and the first sign would have been a T-18 alarm.
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("writer role", result.All, StringComparison.Ordinal);
        Assert.Contains(StateDirectoryLock.ServiceRole, result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ReadCommand_AServiceIsRunning_StillSucceeds()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();

        await using var runtime = await StartServiceAsync();

        // Read paths do not take the writer role, so they are not blocked by
        // one. Refusing them would be exclusion for its own sake.
        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "snapshots",
            "--repo", _harness.RepositoryPath,
            "--passphrase-env", _harness.PassphraseVariable,
            "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode);
    }

    [TestMethod]
    public async Task RepoLessVerbs_AreAnsweredByTheServiceAlone()
    {
        // FR-SVC-016's client half: a command that names no repository is
        // service-only — the same connection the web console makes, no
        // passphrase involved — so `status` and `snapshots` against a
        // running installation need nothing but the state (and not even
        // that, when it is the shared default).
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        foreach (var verb in new[] { "status", "snapshots" })
        {
            var result = await HostHarness.RunAsync(
                (a, o, e, c) => Cli.CliApplication.RunAsync(
                    a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
                verb, "--state", _harness.StateDirectory);

            Assert.AreEqual(0, result.ExitCode, $"{verb}: {result.All}");
            Assert.Contains("mode: service", result.All, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task Status_WithABackgroundWindow_SaysWhetherItIsShut()
    {
        // The window governs every row the matrix prints, so a person asking
        // `status` gets the answer to "why is this due set not running"
        // without a second verb (contract 1.39, ADR-0069). The configuration
        // is rewritten with a window that is shut right now, against the real
        // clock rather than a fixed hour, so the case says what it means
        // wherever it runs.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        var opens = DateTimeOffset.Now.AddHours(3);
        var shut = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{opens:HH\\:mm}-{opens.AddHours(1):HH\\:mm}");
        var configuration = Path.Combine(_harness.StateDirectory, "config.json");
        (ClientConfiguration.Load(configuration) with { BackgroundWindow = shut }).Save(configuration);

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "status", "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.Contains($"background window {shut}", result.All, StringComparison.Ordinal);
        Assert.Contains("SHUT", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Status_WithNoBackgroundWindow_PrintsNoLineAtAll()
    {
        // Every installation written before schema 6 has no window, so a
        // line that appeared anyway — or printed "undefined" — would be a
        // regression visible everywhere at once.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "status", "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.DoesNotContain("background window", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Status_WithBackgroundLimits_SaysWhatIsHeldToWhat()
    {
        // A paced sync looks like a slow one, so the limits are said where
        // the window is (contract 1.43, NFR-PERF-013): above the matrix,
        // one line per limit, with the destination named.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        var configuration = Path.Combine(_harness.StateDirectory, "config.json");
        var loaded = ClientConfiguration.Load(configuration);
        (loaded with
        {
            BackgroundReadLimit = "40 MiB/s",
            Destinations = [.. loaded.Destinations.Select(destination => destination with { TransferLimit = "2 MiB/s" })],
        }).Save(configuration);

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "status", "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.Contains("background reads limited to 40 MiB/s", result.All, StringComparison.Ordinal);
        Assert.Contains("background transfers to 'vault' limited to 2 MiB/s", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Status_WithNoBackgroundLimits_PrintsNoLimitLine()
    {
        // Every installation written before schema 7 limits nothing, so a
        // line that appeared anyway would be a regression visible everywhere.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "status", "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.DoesNotContain("limited to", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Status_SaysWhenADestinationWasLastReadBackInFull_AndNeverBeforeItWas()
    {
        // Contract 1.46: the row's sweep token, from the service's own ledger.
        // Before, the matrix could not tell a replica read back last night
        // from one never read back at all.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var before = await ServiceCliAsync("status", "--state", _harness.StateDirectory);
        Assert.AreEqual(0, before.ExitCode, before.All);
        Assert.Contains("sweep:never", before.All, StringComparison.Ordinal);

        var synced = await ServiceCliAsync("sync", "--state", _harness.StateDirectory);
        Assert.AreEqual(0, synced.ExitCode, synced.All);
        var verified = await ServiceCliAsync("verify-destination", "--full", "--state", _harness.StateDirectory);
        Assert.AreEqual(0, verified.ExitCode, verified.All);

        var closed = runtime.DestinationSync.Find(runtime.Configuration.BackupSets.Single().Id, "vault")?.SweepCompletedAt;
        Assert.IsNotNull(closed, $"the control: verify-destination --full read the whole replica back — {synced.All} | {verified.All}");

        var after = await ServiceCliAsync("status", "--state", _harness.StateDirectory);
        Assert.AreEqual(0, after.ExitCode, after.All);
        Assert.Contains(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"sweep:ok@{DateTimeOffset.FromUnixTimeMilliseconds((long)closed.Value):yyyy-MM-dd}"),
            after.All,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Backup_ASetNamedWithAServiceRunning_IsRunByTheService()
    {
        // ADR-0028 §3 is unconditional: "the CLI connects to the service when
        // one is running. Every command that reads or mutates repository or
        // job state is served by the service." A backup does both, and this
        // is the one shape of it an operator most often wants from a terminal
        // — run my configured set, now.
        //
        // It used to be the one shape with no route at all. `--repo` means
        // direct mode, which this very service would refuse because it holds
        // the writer role, and `--connect` means a REMOTE service; so asking
        // your own running service to back up your own configured set
        // answered "--repo is required", naming the argument that could not
        // have helped. The read verbs above already took the service-only
        // path; backup simply never got the same branch.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the words worth keeping");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "backup", "--set", "docs", "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.Contains("mode: service", result.All, StringComparison.Ordinal);

        // The service ran it, so the service's own journal is where it shows
        // up — the proof that this was not direct mode wearing a label.
        Assert.IsInstanceOfType<JobsResult>(
            await handler.ExecuteAsync(new ListJobsCommand(ActiveOnly: false, Limit: null), _timeout.Token), out var jobs);
        Assert.IsNotEmpty(jobs.Jobs, "the service's journal records nothing, so the CLI did the work itself");
    }

    [TestMethod]
    public async Task Backup_ASetNamedWithNothingListening_RefusesWithBothWaysForward()
    {
        // The same refusal the read verbs give, and for the same reason:
        // direct mode is never a silent fallback (ADR-0028 §3), so a missing
        // service is stated rather than worked around — and the message names
        // the argument that WOULD help, which the old one did not.
        _harness.WriteConfiguration("every 1h");

        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "backup", "--set", "docs", "--state", _harness.StateDirectory);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("no service is listening", result.All, StringComparison.Ordinal);
        Assert.Contains("--repo", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ARepoLessVerb_NothingListening_RefusesWithDirections()
    {
        // Without --repo there is no direct fallback to guess at: the only
        // honest answer is a stated refusal that names both ways forward.
        var result = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "status", "--state", _harness.StateDirectory);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("no service is listening", result.All, StringComparison.Ordinal);
        Assert.Contains("--repo", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ReadCommand_AskedOfClientAndService_ReturnsTheSameAnswer()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        await using var client = await LocalServiceClient.ConnectAsync(
            _harness.StateDirectory, "test", _timeout.Token);

        Assert.IsInstanceOfType<SnapshotsResult>(await client.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var overWire);
        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var inProcess);

        Assert.AreEqual(inProcess.Snapshots.Count, overWire.Snapshots.Count);
        Assert.AreEqual(inProcess.Snapshots[0].SnapshotId, overWire.Snapshots[0].SnapshotId);
    }

    [TestMethod]
    public async Task Backup_AServiceIsListening_IsRunByTheService()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await RunBackupAsync("--set", "docs");

        // The verb that used to fail outright while a service held the role now
        // asks the service to run it — which is what "the CLI becomes a client"
        // has to mean to be worth anything (ADR-0028 §3).
        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("mode: service", result.All, StringComparison.Ordinal);
        Assert.Contains("status         complete", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AdHocRoot_AServiceHoldsTheWriterRole_IsRefusedWithRemedialAdvice()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await RunBackupAsync(_harness.SourceRoot);

        // A service runs what its configuration names. Running the ad-hoc root
        // here instead would be direct mode by the back door, against state the
        // service owns — so it is refused, and the refusal says what to do.
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--set", result.All, StringComparison.Ordinal);
        Assert.Contains("--direct", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DirectMode_AServiceHoldsTheWriterRole_IsRefused()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await RunBackupAsync("--set", "docs", "--direct");

        // --direct asks to bypass the service, not to bypass the role. Two
        // processes writing as one writer is the hazard the role exists for, and
        // an explicit flag does not make it safe (FR-SVC-002).
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("writer role", result.All, StringComparison.Ordinal);
        Assert.Contains(StateDirectoryLock.ServiceRole, result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Backup_RunDirectlyAndThroughTheService_CapturesIdentically()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        // Nothing is listening, so the CLI does the work itself.
        var direct = await RunBackupAsync("--set", "docs");
        Assert.AreEqual(0, direct.ExitCode);
        Assert.Contains("mode: direct", direct.All, StringComparison.Ordinal);

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var viaService = await RunBackupAsync("--set", "docs");
        Assert.AreEqual(0, viaService.ExitCode);
        Assert.Contains("mode: service", viaService.All, StringComparison.Ordinal);

        // What the two paths *print* differs on purpose — direct mode holds the
        // published snapshot, a client holds a job the service ran — so parity
        // is asserted where it is meant to hold: on what reached the repository.
        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);

        Assert.AreEqual(2, snapshots.Snapshots.Count);
        foreach (var snapshot in snapshots.Snapshots)
        {
            Assert.AreEqual(1, snapshot.CaptureStatus);
            Assert.AreEqual(new string('a', 32), snapshot.BackupSetId);
        }
    }

    [TestMethod]
    public async Task ReadVerb_AServiceIsListening_IsAnsweredByTheService()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        // With nothing listening the CLI reads the repository itself.
        var direct = await RunCliAsync("check");
        Assert.AreEqual(0, direct.ExitCode);
        Assert.Contains("mode: direct", direct.All, StringComparison.Ordinal);

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        // With one listening it asks. A read path never takes the writer role,
        // so this is a choice about who does the reading rather than about who
        // is permitted to — which is why it can fall back without apology.
        var routed = await RunCliAsync("check");
        Assert.AreEqual(0, routed.ExitCode);
        Assert.Contains("mode: service", routed.All, StringComparison.Ordinal);
        Assert.Contains("check: OK", routed.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restore_RoutedThroughTheService_WritesTheFiles()
    {
        // A set-up installation holds no content key (ADR-0042 §7): the CLI
        // derives the restore grant from the passphrase it was given — the
        // console's ceremony, at the shell — and the service restores under
        // it. A routed restore that sent no grant would write nothing.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        var destination = Path.Combine(_harness.WorkPath, "routed-restore");

        var result = await RunCliAsync(
            "restore", snapshots.Snapshots[0].SnapshotId, "--output", destination);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.Contains("mode: service", result.All, StringComparison.Ordinal);

        // The service wrote them, on its own machine (ADR-0028 §6) — which on
        // the local binding is this one, so the files are here to read. Under
        // the quarantine directory, per FR-RST-006, which is why the CLI has
        // to print where they went rather than echo what was asked for.
        var written = Directory.EnumerateFiles(destination, "notes.txt", SearchOption.AllDirectories).Single();
        Assert.AreEqual("hello", await File.ReadAllTextAsync(written, _timeout.Token));
        Assert.Contains(Path.GetDirectoryName(written)!, result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RetentionApply_RoutedThroughTheService_DerivesTheReclaimGrantHere()
    {
        // FR-GC-008: the service holds the key that publishes and not the key
        // that authorises a deletion (ADR-0055 §6), so the CLI derives the
        // reclaim grant from the passphrase it was given, as a routed restore
        // derives its restore grant, and the service applies under it. Before
        // this the apply went bare, and every set refused it.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await RunAgainstServiceAsync(
            "retention", "--apply", "--passphrase-env", _harness.PassphraseVariable, "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.Contains("mode: service", result.All, StringComparison.Ordinal);
        Assert.Contains("docs: ", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("reclaim grant", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RetentionApply_RoutedWithASetUnderASaltOfItsOwn_DerivesThatSetsGrantUnderIt()
    {
        // A set provisioned under a salt of its own, as a set adopted from a
        // destination keeps the salt it was born under (ADR-0061): the
        // installation's grant is not its authority, so the CLI derives one
        // per set, under the facts the service publishes for each.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h", withSecondSet: true);

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(
                new ProvisionWriteOnlySetCommand(
                    "extra",
                    SealProvision(
                        description.RestoreGrantRecipient!,
                        Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!,
                        RandomNumberGenerator.GetBytes(KekDerivation.SaltLength))),
                _timeout.Token));

        var result = await RunAgainstServiceAsync(
            "retention", "--apply", "--passphrase-env", _harness.PassphraseVariable, "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.Contains("extra: ", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("no reclaim grant", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RetentionApply_RoutedWithoutAPassphrase_SaysToNameOne_WhileADryRunNeedsNone()
    {
        // The refusal is the CLI's own, naming the flag, rather than the
        // service's, which is written for a console and names none.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var dry = await RunAgainstServiceAsync("retention", "--state", _harness.StateDirectory);
        Assert.AreEqual(0, dry.ExitCode, dry.All);

        var refused = await RunAgainstServiceAsync("retention", "--apply", "--state", _harness.StateDirectory);
        Assert.AreEqual(1, refused.ExitCode, refused.All);
        Assert.Contains("--passphrase-env", refused.All, StringComparison.Ordinal);
        Assert.Contains("authorises a deletion", refused.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RetentionApply_RoutedWithAnotherPassphrase_IsRefusedHere_AndNothingIsSent()
    {
        // The passphrase is checked against the sealing key the service
        // publishes before anything is sealed, as a routed restore checks it.
        var variable = "FBP_CLIENT_RETENTION_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "not this installation's passphrase at all");
        try
        {
            await _harness.CreateRepositoryAsync();
            _harness.WriteSourceFile("notes.txt", "hello");
            await _harness.BackUpAsync();
            _harness.WriteConfiguration("every 1h");

            await using var runtime = await StartServiceAsync();
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

            var result = await RunAgainstServiceAsync(
                "retention", "--apply", "--passphrase-env", variable, "--state", _harness.StateDirectory);

            Assert.AreEqual(1, result.ExitCode, result.All);
            Assert.Contains("does not reproduce this installation's credential", result.All, StringComparison.Ordinal);
            Assert.Contains("nothing was sent", result.All, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public async Task Verify_OneFileVersion_StaysDirectAndSaysWhy()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        // The contract's verify takes a level and sweeps the store; there is no
        // way to name one manifest. Rather than pretend the flag routes, this
        // branch stays direct and says so.
        var result = await RunCliAsync("verify", "--file", new string('0', 64));

        Assert.Contains("has no service equivalent", result.All, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    /// <summary>Runs any CLI verb against this harness.</summary>
    private Task<HostHarness.Invocation> RunCliAsync(params string[] verbAndArguments) =>
        HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            [
                .. verbAndArguments,
                "--repo", _harness.RepositoryPath,
                "--passphrase-env", _harness.PassphraseVariable,
                "--state", _harness.StateDirectory,
            ]);

    /// <summary>Runs <c>backup</c> against this harness with the given extra arguments.</summary>
    private Task<HostHarness.Invocation> RunBackupAsync(params string[] extra) =>
        RunCliAsync(["backup", .. extra]);

    [TestMethod]
    public async Task Settings_WithNoOptions_ShowsEachSettingAndTheWidthThePoolRuns()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await RunAgainstServiceAsync("settings", "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.Contains("background window: none", result.All, StringComparison.Ordinal);
        Assert.Contains("background read limit: none", result.All, StringComparison.Ordinal);
        Assert.Contains("max concurrent backups: 2 (the default)", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Settings_WithOptions_ChangesThemAndPrintsWhatTheServiceSaid()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await RunAgainstServiceAsync(
            "settings", "--state", _harness.StateDirectory,
            "--window", "22:00-06:00", "--read-limit", "40 MiB/s", "--max-backups", "3");

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.Contains("22:00-06:00", result.All, StringComparison.Ordinal);
        Assert.Contains("restart", result.All, StringComparison.Ordinal);
        var saved = ClientConfiguration.Load(Path.Combine(_harness.StateDirectory, "config.json"));
        Assert.AreEqual("22:00-06:00", saved.BackgroundWindow);
        Assert.AreEqual("40 MiB/s", saved.BackgroundReadLimit);
        Assert.AreEqual(3, saved.MaxConcurrentBackups);

        var shown = await RunAgainstServiceAsync("settings", "--state", _harness.StateDirectory);
        Assert.Contains(
            "max concurrent backups: 3 (the pool runs 2 until the service restarts)",
            shown.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Settings_AnUnreadableValue_FailsNamingTheDefect()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await RunAgainstServiceAsync("settings", "--state", _harness.StateDirectory, "--read-limit", "10 MB/s");

        Assert.AreNotEqual(0, result.ExitCode, result.All);
        Assert.Contains("MiB/s", result.All, StringComparison.Ordinal);
        Assert.IsNull(ClientConfiguration.Load(Path.Combine(_harness.StateDirectory, "config.json")).BackgroundReadLimit);
    }

    [TestMethod]
    public async Task DestinationSettings_SettingALimitAndACadence_KeepsEverythingElseTheDestinationSays()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        var configuration = Path.Combine(_harness.StateDirectory, "config.json");
        var loaded = ClientConfiguration.Load(configuration);
        (loaded with
        {
            Destinations = [.. loaded.Destinations.Select(destination => destination with { DeepVerifyIntervalDays = 5, Priority = 4 })],
        }).Save(configuration);

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await RunAgainstServiceAsync(
            "destination-settings", "vault", "--state", _harness.StateDirectory,
            "--transfer-limit", "2 MiB/s", "--drill-days", "7");

        Assert.AreEqual(0, result.ExitCode, result.All);
        var vault = ClientConfiguration.Load(configuration).Destinations.Single();
        Assert.AreEqual("2 MiB/s", vault.TransferLimit);
        Assert.AreEqual(7, vault.DrillIntervalDays);
        Assert.AreEqual(5, vault.DeepVerifyIntervalDays, "the edit must carry back what it did not change");
        Assert.AreEqual(4, vault.Priority, "the edit must carry back what it did not change");

        var shown = await RunAgainstServiceAsync("destination-settings", "vault", "--state", _harness.StateDirectory);
        Assert.AreEqual(0, shown.ExitCode, shown.All);
        Assert.Contains("transfer limit: 2 MiB/s", shown.All, StringComparison.Ordinal);
        Assert.Contains("drill every: 7 days", shown.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DestinationSettings_AnEmptyLimitAndAZeroCadence_ClearBoth()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        var configuration = Path.Combine(_harness.StateDirectory, "config.json");
        var loaded = ClientConfiguration.Load(configuration);
        (loaded with
        {
            Destinations = [.. loaded.Destinations.Select(destination => destination with { TransferLimit = "2 MiB/s", DrillIntervalDays = 3 })],
        }).Save(configuration);

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await RunAgainstServiceAsync(
            "destination-settings", "vault", "--state", _harness.StateDirectory,
            "--transfer-limit", "", "--drill-days", "0");

        Assert.AreEqual(0, result.ExitCode, result.All);
        var vault = ClientConfiguration.Load(configuration).Destinations.Single();
        Assert.IsNull(vault.TransferLimit);
        Assert.IsNull(vault.DrillIntervalDays);
    }

    [TestMethod]
    public async Task DestinationSettings_ANameNothingDeclares_FailsNamingIt()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var result = await RunAgainstServiceAsync(
            "destination-settings", "nowhere", "--state", _harness.StateDirectory, "--transfer-limit", "2 MiB/s");

        Assert.AreNotEqual(0, result.ExitCode, result.All);
        Assert.Contains("nowhere", result.All, StringComparison.Ordinal);
    }

    /// <summary>
    /// The client half of the provisioning ceremony, under a salt the caller
    /// chooses: the write bundle sealed to the service's recipient key, as hex.
    /// </summary>
    private static string SealProvision(string recipientHex, string passphraseText, byte[] salt)
    {
        var parameters = RepositoryCreationSettings.Default.KdfParameters;
        using var passphrase = Passphrase.Create(passphraseText);
        using var authority = WriteOnlyDerivation.Derive(passphrase, parameters, salt, KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealProvision(Convert.FromHexString(recipientHex), authority, salt, parameters));
    }

    /// <summary>Runs a CLI verb against the service this harness started, with no direct-mode flags.</summary>
    private static Task<HostHarness.Invocation> RunAgainstServiceAsync(params string[] args) =>
        HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            args);

    /// <summary>Runs a CLI verb as a client of the running service: no direct-mode options appended.</summary>
    private static Task<HostHarness.Invocation> ServiceCliAsync(params string[] args) =>
        HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            args);

    private async Task<ServiceRuntime> StartServiceAsync()
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

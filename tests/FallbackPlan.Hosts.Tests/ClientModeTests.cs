using System.CommandLine;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// What the CLI becomes (ADR-0028 §3): a client, with an explicit direct mode
/// when no service is running.
/// Establishes FR-SVC-008.
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
        // A format-1 archive under a passphrase-holding service, for now: a
        // restore routed through the service on a set-up installation needs
        // a restore grant (ADR-0042 §5), and the CLI does not derive one yet
        // — that lands with the CLI's own move off the passphrase service.
        await _harness.CreateFormatOneRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync(formatOne: true);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        var destination = Path.Combine(_harness.WorkPath, "routed-restore");

        var result = await RunCliAsync(
            "restore", snapshots.Snapshots[0].SnapshotId, "--output", destination);

        Assert.AreEqual(0, result.ExitCode);
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

    private async Task<ServiceRuntime> StartServiceAsync(bool formatOne = false)
    {
        using var passphrase = formatOne
            ? Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!)
            : null;
        if (!formatOne)
        {
            await _harness.SetupAsync();
        }

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }
}

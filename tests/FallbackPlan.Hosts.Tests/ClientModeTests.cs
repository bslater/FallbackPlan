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
/// </summary>
public sealed class ClientModeTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    [Fact]
    public async Task With_no_service_running_a_write_takes_direct_mode_and_says_so()
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

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("mode: direct", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_a_service_holds_the_role_a_direct_write_is_refused_naming_the_holder()
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
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("writer role", result.All, StringComparison.Ordinal);
        Assert.Contains(StateDirectoryLock.ServiceRole, result.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_command_still_works_alongside_a_running_service()
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

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_client_and_the_service_answer_the_same_question_the_same_way()
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

        var overWire = Assert.IsType<SnapshotsResult>(
            await client.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token));
        var inProcess = Assert.IsType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token));

        Assert.Equal(inProcess.Snapshots.Count, overWire.Snapshots.Count);
        Assert.Equal(inProcess.Snapshots[0].SnapshotId, overWire.Snapshots[0].SnapshotId);
    }

    [Fact]
    public async Task A_backup_is_run_by_the_service_when_one_is_listening()
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
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("mode: service", result.All, StringComparison.Ordinal);
        Assert.Contains("status         complete", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ad_hoc_root_is_refused_while_a_service_holds_the_role_and_says_what_to_do()
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
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--set", result.All, StringComparison.Ordinal);
        Assert.Contains("--direct", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_is_refused_while_a_service_holds_the_writer_role()
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
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("writer role", result.All, StringComparison.Ordinal);
        Assert.Contains(StateDirectoryLock.ServiceRole, result.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_backup_set_captures_the_same_way_through_either_path()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        // Nothing is listening, so the CLI does the work itself.
        var direct = await RunBackupAsync("--set", "docs");
        Assert.Equal(0, direct.ExitCode);
        Assert.Contains("mode: direct", direct.All, StringComparison.Ordinal);

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var viaService = await RunBackupAsync("--set", "docs");
        Assert.Equal(0, viaService.ExitCode);
        Assert.Contains("mode: service", viaService.All, StringComparison.Ordinal);

        // What the two paths *print* differs on purpose — direct mode holds the
        // published snapshot, a client holds a job the service ran — so parity
        // is asserted where it is meant to hold: on what reached the repository.
        var snapshots = Assert.IsType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token));

        Assert.Equal(2, snapshots.Snapshots.Count);
        Assert.All(snapshots.Snapshots, snapshot =>
        {
            Assert.Equal(1, snapshot.CaptureStatus);
            Assert.Equal(new string('a', 32), snapshot.BackupSetId);
        });
    }

    [Fact]
    public async Task A_read_verb_is_answered_by_the_service_when_one_is_listening()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        // With nothing listening the CLI reads the repository itself.
        var direct = await RunCliAsync("check");
        Assert.Equal(0, direct.ExitCode);
        Assert.Contains("mode: direct", direct.All, StringComparison.Ordinal);

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        // With one listening it asks. A read path never takes the writer role,
        // so this is a choice about who does the reading rather than about who
        // is permitted to — which is why it can fall back without apology.
        var routed = await RunCliAsync("check");
        Assert.Equal(0, routed.ExitCode);
        Assert.Contains("mode: service", routed.All, StringComparison.Ordinal);
        Assert.Contains("check: OK", routed.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restore_routed_through_the_service_writes_the_files()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartServiceAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);

        var snapshots = Assert.IsType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token));
        var destination = Path.Combine(_harness.WorkPath, "routed-restore");

        var result = await RunCliAsync(
            "restore", snapshots.Snapshots[0].SnapshotId, "--output", destination);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("mode: service", result.All, StringComparison.Ordinal);

        // The service wrote them, on its own machine (ADR-0028 §6) — which on
        // the local binding is this one, so the files are here to read. Under
        // the quarantine directory, per FR-RST-006, which is why the CLI has
        // to print where they went rather than echo what was asked for.
        var written = Directory.EnumerateFiles(destination, "notes.txt", SearchOption.AllDirectories).Single();
        Assert.Equal("hello", await File.ReadAllTextAsync(written, _timeout.Token));
        Assert.Contains(Path.GetDirectoryName(written)!, result.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifying_one_file_version_stays_direct_and_says_why()
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

    private async Task<ServiceRuntime> StartServiceAsync()
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                RepositoryPath = _harness.RepositoryPath,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }
}

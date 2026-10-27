using System.CommandLine;
using System.Net;
using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Restore;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A paired console reads the restore receipts a service keeps through
/// <c>restore-receipts --connect</c> (FR-RST-004, ADR-0128), not the folder
/// its own <c>--state</c> names. The listing names none of the backup's files
/// and needs no passphrase. One run's files that did not come back are read
/// through a source of the set its receipt records, whichever set the
/// passphrase opens first, which the passphrase <c>--passphrase-env</c> names
/// unlocks, and the source is closed after (FR-WOR-007). A receipt that records
/// no set, one that does not read, a run the service keeps no receipt of, and
/// the files asked without the passphrase are each refused, naming no file.
/// </summary>
[TestClass]
public sealed class RestoreReceiptsRemoteTests : IDisposable
{
    private const string Run = "0123456789abcdef";
    private const string NoSetRun = "fedcba9876543210";
    private const ulong PairedAt = 1_722_600_000_000;

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task APairedConsole_ListsTheReceiptsTheServiceKeeps_NotItsOwn_NamingNoFile()
    {
        await using var service = await StartServiceAsync();
        Keep(Path.Combine(_harness.StateDirectory, "receipts"), Run, _harness.DocsSetId);
        Keep(Path.Combine(_harness.StateDirectory, "receipts"), NoSetRun, setId: null, completedAt: 1_699_000_000_000);
        Keep(Path.Combine(service.ConsoleState, "receipts"), "aaaaaaaaaaaaaaaa", setId: null);

        var listed = await service.CliAsync("restore-receipts");
        var limited = await service.CliAsync("restore-receipts", "--limit", "1");

        Assert.AreEqual(0, listed.ExitCode, listed.Error);
        Assert.StartsWith(
            $"{Run}  partial  ended 2023-11-14 22:13:20Z  snapshot {new string('e', 32)} of docs{Environment.NewLine}"
            + $"  1 restored, 1 skipped, 1 failed, 1 degraded{Environment.NewLine}"
            + $"  written to /srv/restored{Environment.NewLine}"
            + $"{NoSetRun}  partial  ended 2023-11-03 08:26:40Z  snapshot {new string('e', 32)}{Environment.NewLine}",
            listed.Output,
            StringComparison.Ordinal,
            "the service's receipts, newest first, as a state directory's are listed");
        Assert.Contains("2 restore receipts kept by the service.", listed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("aaaaaaaaaaaaaaaa", listed.Output, StringComparison.Ordinal, "the console's own state is not the service's");
        AssertNamesNoFile(listed.Output);
        Assert.AreEqual(0, limited.ExitCode, limited.Error);
        Assert.DoesNotContain(NoSetRun, limited.Output, StringComparison.Ordinal);
        Assert.Contains("The newest 1 of 2 restore receipts kept by the service.", limited.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AServiceThatKeepsNone_SaysSo()
    {
        await using var service = await StartServiceAsync();

        var listed = await service.CliAsync("restore-receipts");

        Assert.AreEqual(0, listed.ExitCode, listed.Error);
        Assert.AreEqual($"No restore receipt is kept by the service.{Environment.NewLine}", listed.Output);
    }

    [TestMethod]
    public async Task WhatARunDidNotRestore_IsShownThroughThePassphrase_AndTheSourceClosedAfter()
    {
        await using var service = await StartServiceAsync();
        Keep(Path.Combine(_harness.StateDirectory, "receipts"), Run, _harness.DocsSetId);

        var shown = await service.CliAsync("restore-receipts", "--run", Run, "--passphrase-env", _harness.PassphraseVariable);
        var limited = await service.CliAsync(
            "restore-receipts", "--run", Run, "--limit", "1", "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(0, shown.ExitCode, shown.Error);
        Assert.AreEqual(
            $"{Run}  partial  ended 2023-11-14 22:13:20Z  snapshot {new string('e', 32)} of docs{Environment.NewLine}"
            + $"  1 restored, 1 skipped, 1 failed, 1 degraded{Environment.NewLine}"
            + $"  written to /srv/restored{Environment.NewLine}"
            + $"  skipped  kept.txt — a newer file was in the way{Environment.NewLine}"
            + $"  failed  lost.txt — no copy would read{Environment.NewLine}"
            + $"  degraded  streams.txt{Environment.NewLine}",
            shown.Output,
            "the files it did not restore, in the receipt's order, and none it did");
        Assert.AreEqual(0, limited.ExitCode, limited.Error);
        Assert.Contains("  skipped  kept.txt", limited.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("lost.txt", limited.Output, StringComparison.Ordinal);
        Assert.Contains("  …and 2 more", limited.Output, StringComparison.Ordinal);
        Assert.AreEqual(0, service.Runtime.RestoreSources.Count, "each source it opened is closed after");
    }

    [TestMethod]
    public async Task AReceiptOfTheSecondSet_IsReadThroughThatSetsOwnSource()
    {
        await using var service = await StartServiceAsync(withSecondSet: true);
        await BackUpThroughTheServiceAsync(service.Runtime, "extra");
        Keep(Path.Combine(_harness.StateDirectory, "receipts"), Run, new string('b', 32));

        var shown = await service.CliAsync("restore-receipts", "--run", Run, "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(0, shown.ExitCode, shown.Error);
        Assert.Contains("  failed  lost.txt — no copy would read", shown.Output, StringComparison.Ordinal);
        Assert.AreEqual(0, service.Runtime.RestoreSources.Count, "each source it opened is closed after");
    }

    [TestMethod]
    public async Task AReceiptThatDoesNotRead_IsListedAsUnreadable_AndItsFilesAreRefusedSayingSo()
    {
        await using var service = await StartServiceAsync();
        var receipts = Path.Combine(_harness.StateDirectory, "receipts");
        Directory.CreateDirectory(receipts);
        await File.WriteAllTextAsync(Path.Combine(receipts, $"{Run}.json"), "{ not a receipt", _timeout.Token);

        var listed = await service.CliAsync("restore-receipts");
        var refused = await service.CliAsync("restore-receipts", "--run", Run, "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(0, listed.ExitCode, listed.Error);
        Assert.StartsWith($"{Run}  unreadable: ", listed.Output, StringComparison.Ordinal);
        Assert.AreEqual(1, refused.ExitCode, refused.Output);
        Assert.Contains($"Run {Run}'s receipt does not read", refused.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ARunThatRestoredEverything_SaysSo()
    {
        await using var service = await StartServiceAsync();
        Keep(Path.Combine(_harness.StateDirectory, "receipts"), Run, _harness.DocsSetId, everything: true);

        var shown = await service.CliAsync("restore-receipts", "--run", Run, "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(0, shown.ExitCode, shown.Error);
        Assert.EndsWith($"  every file it was asked for was restored{Environment.NewLine}", shown.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WithoutThePassphrase_ARunsFilesAreRefused_ByTheFlagsName_NamingNoFile()
    {
        await using var service = await StartServiceAsync();
        Keep(Path.Combine(_harness.StateDirectory, "receipts"), Run, _harness.DocsSetId);

        var refused = await service.CliAsync("restore-receipts", "--run", Run);

        Assert.AreEqual(1, refused.ExitCode, refused.Output);
        Assert.Contains("--passphrase-env", refused.Error, StringComparison.Ordinal);
        AssertNamesNoFile(refused.Output + refused.Error);
    }

    [TestMethod]
    public async Task AReceiptThatRecordsNoSet_IsRefusedBeforeThePassphrase_AndSaysWhereItIsRead()
    {
        await using var service = await StartServiceAsync();
        Keep(Path.Combine(_harness.StateDirectory, "receipts"), NoSetRun, setId: null);

        var refused = await service.CliAsync("restore-receipts", "--run", NoSetRun);

        Assert.AreEqual(1, refused.ExitCode, refused.Output);
        Assert.Contains("restore-receipts --state", refused.Error, StringComparison.Ordinal);
        Assert.Contains($"--run {NoSetRun}", refused.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("--passphrase-env", refused.Error, StringComparison.Ordinal, "no passphrase here is its own");
        AssertNamesNoFile(refused.Output + refused.Error);
    }

    [TestMethod]
    public async Task ARunTheServiceKeepsNoReceiptOf_IsRefusedNamingIt()
    {
        await using var service = await StartServiceAsync();
        Keep(Path.Combine(service.ConsoleState, "receipts"), Run, _harness.DocsSetId);

        var refused = await service.CliAsync("restore-receipts", "--run", Run, "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(1, refused.ExitCode, refused.Output);
        Assert.Contains($"Run {Run} has no receipt the service keeps", refused.Error, StringComparison.Ordinal);
        AssertNamesNoFile(refused.Output + refused.Error);
    }

    private static void AssertNamesNoFile(string text)
    {
        foreach (var name in new[] { "kept.txt", "lost.txt", "streams.txt", "written.txt" })
        {
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
        }
    }

    private static void Keep(
        string receipts, string run, string? setId, ulong completedAt = 1_700_000_000_000, bool everything = false) =>
        new RestoreReceipt
        {
            SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
            SnapshotId = new string('e', 32),
            StartedAt = completedAt - 10_000,
            CompletedAt = completedAt,
            Items = everything
                ? [new ReceiptItem { Path = "written.txt", Outcome = "restored", Bytes = 1 }]
                :
                [
                    new ReceiptItem { Path = "written.txt", Outcome = "restored", Bytes = 1 },
                    new ReceiptItem { Path = "kept.txt", Outcome = "skipped", Bytes = 1, Detail = "a newer file was in the way" },
                    new ReceiptItem { Path = "lost.txt", Outcome = "failed", Bytes = 1, Detail = "no copy would read" },
                    new ReceiptItem { Path = "streams.txt", Outcome = "degraded", Bytes = 1 },
                ],
            Displaced = [],
            WrittenTo = "/srv/restored",
            Outcome = everything ? RestoreOutcome.Complete : RestoreOutcome.Partial,
            Set = setId is null ? null : "docs",
            SetId = setId,
        }.Keep(receipts, run);

    /// <summary>
    /// Stands up a service with a backup of the docs set and the remote binding
    /// on loopback, paired both ways with a console whose identity is in its
    /// own state directory.
    /// </summary>
    private async Task<PairedService> StartServiceAsync(bool withSecondSet = false)
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the notes to come back");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h", withSecondSet);
        await _harness.SetupAsync();

        var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            _timeout.Token);
        var serviceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        var serviceGrants = PeerGrantStore.Open(_harness.StateDirectory);
        var consoleState = Path.Combine(_harness.WorkPath, "console");
        using (var consoleKeypair = PeerKeypairStore.Open(consoleState))
        {
            serviceGrants.Pin(new PeerGrant(consoleKeypair.Identity, "console", PeerRole.StoresForUs, PeerTerms.None, PairedAt));
        }

        PeerGrantStore.Open(consoleState).Pin(
            new PeerGrant(serviceKeypair.Identity, "service", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        var remote = RemoteServiceListener.Start(
            serviceKeypair, serviceGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test");
        remote.Bind(new ServiceCommandHandler(runtime, RemoteBindingState.On(remote.Endpoint.ToString())));
        return new PairedService(runtime, remote, serviceKeypair, consoleState);
    }

    /// <summary>A set's first backup through the service, which makes its staging archive.</summary>
    private async Task BackUpThroughTheServiceAsync(ServiceRuntime runtime, string set)
    {
        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (observation.Progress.State is JobState.Complete or JobState.CompletedWithFailures)
                    {
                        return;
                    }
                }
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await new ServiceCommandHandler(runtime, RemoteBindingState.Off)
                .ExecuteAsync(new RunBackupCommand(set, Full: false), _timeout.Token));
        await watching;
    }

    private sealed class PairedService(
        ServiceRuntime runtime, RemoteServiceListener remote, PeerKeypair keypair, string consoleState) : IAsyncDisposable
    {
        public ServiceRuntime Runtime => runtime;

        public string ConsoleState => consoleState;

        /// <summary>Runs the real CLI against this service over <c>--connect</c>, capturing both streams.</summary>
        public async Task<(int ExitCode, string Output, string Error)> CliAsync(params string[] arguments)
        {
            var output = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
            var error = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
            string[] connect =
            [
                "--connect", $"{remote.Endpoint.Address}:{remote.Endpoint.Port}",
                "--state", consoleState,
                "--fingerprint", keypair.Identity.Fingerprint,
            ];

            var exit = await CliApplication.RunAsync(
                [.. arguments, .. connect],
                new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false });
            return (exit, output.ToString(), error.ToString());
        }

        // The listener signs every handshake with the keypair, so it goes last.
        public async ValueTask DisposeAsync()
        {
            await remote.DisposeAsync();
            await runtime.DisposeAsync();
            keypair.Dispose();
        }
    }
}

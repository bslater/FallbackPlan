using System.CommandLine;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Diagnostics;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Domain.Identifiers;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The diagnostic bundle (contract 1.52, ADR-0081): one file a person can send
/// to whoever is helping them, built by a running service and inspected byte
/// for byte. Establishes NFR-PRIV-003, and the CLI half of it through
/// <c>diagnostics-export</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each test plants what must not travel where a careless bundle would pick it
/// up: the passphrase and the owner's password in the environment the service
/// runs in, the installation's salt and keys behind the describe verb, a
/// repository identity and a peer's fingerprint in typed log records, and a
/// folder whose name says more about its owner than its files do — in the
/// configuration, in a real backup's own records, in an unreadable-folder
/// record whose platform message repeats the path, and in a notice. Every
/// entry is then read back whole, a JSON entry as the strings it decodes to as
/// well as its text.
/// </para>
/// <para>
/// The scratch directory's random name is in every path the harness makes, so
/// it is the canary: if it appears anywhere in a default bundle, some path
/// crossed. The opt-in test is the positive control that keeps that assertion
/// honest — the same planted paths do appear once a person asks for them.
/// </para>
/// </remarks>
[TestClass]
public sealed class DiagnosticBundleTests : IDisposable
{
    /// <summary>A folder name that could only reach a bundle as a path.</summary>
    private const string TellingFolder = "a-folder-named-for-a-diagnosis";

    private const string PlantedFingerprint = "N7DO2WYKYWPZLJFJG3EPZYAURA";

    private static readonly byte[] PlantedRepository = [.. Enumerable.Range(0x40, RepositoryId.Size).Select(value => (byte)value)];

    private static readonly string[] ExpectedEntries =
    [
        "README.txt", "manifest.json", "environment.json", "configuration.json",
        "status.json", "notices.json", "jobs.json", "log.txt",
    ];

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));
    private LoggingComposition? _logging;

    public void Dispose()
    {
        _logging?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();
    }

    /// <summary>The scratch directory's random name, which every harness path carries.</summary>
    private string Canary => Path.GetFileName(Path.GetDirectoryName(_harness.StateDirectory))!;

    private string SourceFolder => Path.Combine(_harness.SourceRoot, TellingFolder);

    private string DeniedMessage => $"Access to the path '{SourceFolder}' is denied.";

    private static ServiceCommandHandler Handler(ServiceRuntime runtime, CallerScope scope) =>
        new(runtime, RemoteBindingState.Off, scope);

    private async Task<ServiceRuntime> StartAsync(
        int ringCapacity = 1024, bool withLogging = true, bool durable = true, RetentionConfiguration? retention = null)
    {
        if (withLogging)
        {
            _logging = LoggingComposition.Create(new LoggingOptions
            {
                Default = LogLevel.Trace,
                Directory = durable ? Path.Combine(_harness.StateDirectory, "logs") : null,
                RingCapacity = ringCapacity,
            });
        }

        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile($"{TellingFolder}/scan-2026.pdf", "what the scan said");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32),
                    Name = "vault",
                    Kind = DestinationKind.LocalPath,
                    Path = Path.Combine(_harness.StateDirectory, "vault"),
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = SourceFolder, Label = "scans" }],
                    ExcludeRules = [$"**/{TellingFolder}/drafts/**"],
                    Schedule = "every 1h",
                    Retention = retention,
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                Logging = _logging,
                // The fixture's every path shares one real volume; the vault is
                // told apart by name, the compliant install's shape (ADR-0051).
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            _timeout.Token);
    }

    /// <summary>A real backup, then the records and the notice a careless bundle would carry.</summary>
    private async Task MakeHistoryAsync(ServiceRuntime runtime)
    {
        Assert.IsInstanceOfType<JobAcceptedResult>(
            await Handler(runtime, CallerScope.Local)
                .ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token),
            out _);

        while (runtime.Jobs.Jobs.Any(job => job.State is not (
            Domain.Jobs.JobState.Complete
            or Domain.Jobs.JobState.CompletedWithFailures
            or Domain.Jobs.JobState.Cancelled
            or Domain.Jobs.JobState.FailedRecoverable
            or Domain.Jobs.JobState.FailedPermanent)))
        {
            await Task.Delay(10, _timeout.Token);
        }

#pragma warning disable CA1848, CA1873, CA2254 // Planted records; the bundle is what is under test.
        var log = _logging!.Factory.CreateLogger("FallbackPlan.Test");
        log.Log(
            LogLevel.Warning, new EventId(4242), new UnauthorizedAccessException(DeniedMessage),
            "could not list {Directory} ({Reason})", new LogPath(SourceFolder), DeniedMessage);
        log.Log(
            LogLevel.Information, new EventId(4243), "peer {Fingerprint} holds {Repository}",
            LogId.Fingerprint(PlantedFingerprint), RepositoryId.FromBytes(PlantedRepository));
#pragma warning restore CA1848, CA1873, CA2254

        runtime.Notices.Raise(
            $"terms-narrowed:{PlantedFingerprint}",
            $"Peer 'vault' could not read '{SourceFolder}'.",
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Everything a careless bundle would carry, by name.</summary>
    private async Task<Dictionary<string, string>> PlantedSecretsAsync(ServiceRuntime runtime)
    {
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await Handler(runtime, CallerScope.Local).ExecuteAsync(new DescribeServiceCommand(), _timeout.Token),
            out var description);

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["passphrase"] = Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!,
            ["owner password"] = HostHarness.OwnerPassword,
            ["installation salt"] = description.KdfSalt!,
            ["sealing public key"] = description.SealingPublicKey!,
            ["grant recipient"] = description.RestoreGrantRecipient!,
            ["device id"] = description.DeviceId!,
            ["repository id"] = Convert.ToHexStringLower(PlantedRepository),
            ["peer fingerprint"] = PlantedFingerprint,
        };

        // Only a name long enough to be unmistakable is asserted on: a short
        // one like "vm" is a word any bundle might contain by chance.
        if (Environment.MachineName.Length >= 8)
        {
            secrets["machine name"] = Environment.MachineName;
        }

        return secrets;
    }

    private static Dictionary<string, string> Open(DiagnosticBundleResult bundle) =>
        BundleInspection.Open(Convert.FromBase64String(bundle.ContentBase64));

    private static void AssertNowhere(string what, string value, DiagnosticBundleResult bundle, Dictionary<string, string> entries) =>
        BundleInspection.AssertNowhere(what, value, bundle.FileName, entries);

    private async Task<DiagnosticBundleResult> ExportAsync(ServiceRuntime runtime, bool includePaths, CallerScope scope = CallerScope.Local)
    {
        Assert.IsInstanceOfType<DiagnosticBundleResult>(
            await Handler(runtime, scope).ExecuteAsync(new ExportDiagnosticsCommand(includePaths), _timeout.Token),
            out var bundle);
        return bundle;
    }

    [TestMethod]
    public async Task ExportDiagnostics_ByDefault_CarriesNoSecretNoPathAndNoIdentifierInFull()
    {
        await using var runtime = await StartAsync();
        await MakeHistoryAsync(runtime);
        var secrets = await PlantedSecretsAsync(runtime);

        var bundle = await ExportAsync(runtime, includePaths: false);
        var entries = Open(bundle);

        CollectionAssert.AreEquivalent(ExpectedEntries, entries.Keys.ToArray());
        CollectionAssert.AreEquivalent(ExpectedEntries, bundle.Entries.ToArray());
        Assert.IsFalse(bundle.IncludesPaths);

        AssertNowhere("scratch directory (so a path)", Canary, bundle, entries);
        AssertNowhere("telling folder name", TellingFolder, bundle, entries);
        foreach (var (what, value) in secrets)
        {
            AssertNowhere(what, value, bundle, entries);
        }

        // What makes the bundle worth sending survives, shortened where it
        // correlates and withheld where no type said it may cross.
        Assert.Contains("docs", entries["configuration.json"], StringComparison.Ordinal);
        Assert.Contains("vault", entries["status.json"], StringComparison.Ordinal);
        Assert.Contains("path#", entries["configuration.json"], StringComparison.Ordinal);
        Assert.Contains("terms-narrowed", entries["notices.json"], StringComparison.Ordinal);
        Assert.Contains("repo#40414243", entries["log.txt"], StringComparison.Ordinal);
        Assert.Contains("peer#N7DO2WYK", entries["log.txt"], StringComparison.Ordinal);
        Assert.Contains("System.UnauthorizedAccessException", entries["log.txt"], StringComparison.Ordinal);
        Assert.Contains(RedactedRendering.Withheld, entries["log.txt"], StringComparison.Ordinal);
        Assert.IsGreaterThan(0, bundle.LogRecords, "A real backup ran, so the ring holds its records.");

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        Assert.IsFalse(manifest.RootElement.GetProperty("includes_paths").GetBoolean());
        Assert.AreEqual(bundle.LogRecords, manifest.RootElement.GetProperty("log_records").GetInt32());
    }

    [TestMethod]
    public async Task ExportDiagnostics_WithPaths_CarriesThePathsAndStillNoSecretOrIdentifierInFull()
    {
        await using var runtime = await StartAsync();
        await MakeHistoryAsync(runtime);
        var secrets = await PlantedSecretsAsync(runtime);

        var bundle = await ExportAsync(runtime, includePaths: true);
        var entries = Open(bundle);

        Assert.IsTrue(bundle.IncludesPaths);

        // The positive control for the default test's absences: these are the
        // same planted paths, and once asked for they are there.
        Assert.Contains(SourceFolder, BundleInspection.Readable(entries, "configuration.json"), StringComparison.Ordinal);
        Assert.Contains(SourceFolder, entries["log.txt"], StringComparison.Ordinal);
        Assert.Contains(DeniedMessage, entries["log.txt"], StringComparison.Ordinal);
        Assert.Contains(SourceFolder, BundleInspection.Readable(entries, "notices.json"), StringComparison.Ordinal);

        // A path the configuration does not have is null, not a word that
        // reads like one: this set has its roots and no legacy root.
        using var configuration = JsonDocument.Parse(entries["configuration.json"]);
        Assert.AreEqual(
            JsonValueKind.Null,
            configuration.RootElement.GetProperty("backup_sets")[0].GetProperty("root").ValueKind);

        // The opt-in is to paths. Secrets never depended on it, and
        // identifiers still shorten because correlation was never offered.
        foreach (var (what, value) in secrets)
        {
            AssertNowhere(what, value, bundle, entries);
        }

        Assert.Contains("repo#40414243", entries["log.txt"], StringComparison.Ordinal);

        // And the bundle says what it is, where a recipient reads first.
        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        Assert.IsTrue(manifest.RootElement.GetProperty("includes_paths").GetBoolean());
        Assert.Contains("paths", entries["README.txt"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("included", entries["README.txt"], StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task ExportDiagnostics_FromAPairedRemoteConsole_IsRedactedAndRefusesPaths()
    {
        await using var runtime = await StartAsync();
        await MakeHistoryAsync(runtime);

        // A paired console reads the log redacted and nothing else (ADR-0043
        // §6), so a bundle with paths is not something it may ask for.
        Assert.IsInstanceOfType<ServiceError>(
            await Handler(runtime, CallerScope.Remote)
                .ExecuteAsync(new ExportDiagnosticsCommand(IncludePaths: true), _timeout.Token),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);

        var bundle = await ExportAsync(runtime, includePaths: false, CallerScope.Remote);
        var entries = Open(bundle);

        Assert.IsFalse(bundle.IncludesPaths);
        AssertNowhere("scratch directory (so a path)", Canary, bundle, entries);
        AssertNowhere("telling folder name", TellingFolder, bundle, entries);
    }

    [TestMethod]
    public async Task ExportDiagnostics_TheConfiguration_CarriesTheSetsRetention_DeletedFileDurationIncluded()
    {
        // What a set may delete, and how long it keeps a deleted file, is
        // the first thing to ask about a snapshot that was not there.
        await using var runtime = await StartAsync(
            retention: new RetentionConfiguration { KeepDaily = 7, KeepDeletedDays = 90 });

        var entries = Open(await ExportAsync(runtime, includePaths: false));

        using var configuration = JsonDocument.Parse(entries["configuration.json"]);
        var retention = configuration.RootElement.GetProperty("backup_sets")[0].GetProperty("retention");
        Assert.AreEqual(7, retention.GetProperty("keep_daily").GetInt32());
        Assert.AreEqual(90, retention.GetProperty("keep_deleted_days").GetInt32());
    }

    [TestMethod]
    public async Task ExportDiagnostics_OnAServiceWithoutLogging_IsStillBuiltAndSaysItHoldsNoLog()
    {
        await using var runtime = await StartAsync(withLogging: false);

        var bundle = await ExportAsync(runtime, includePaths: false);
        var entries = Open(bundle);

        // Configuration, status, notices and jobs answer most questions on
        // their own; refusing them because the log is missing would be the
        // wrong kind of strict.
        CollectionAssert.AreEquivalent(ExpectedEntries, entries.Keys.ToArray());
        Assert.AreEqual(0, bundle.LogRecords);
        Assert.Contains("logging", entries["log.txt"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docs", entries["configuration.json"], StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ExportDiagnostics_ALogLargerThanAFrameCarries_KeepsTheNewestAndSaysWhatItLeftOut()
    {
        await using var runtime = await StartAsync(ringCapacity: 16_384, durable: false);

        // Labels cross every rendering as written, so these lines reach the
        // bundle at full size, and random hex compresses poorly enough that
        // ten thousand of them cannot fit.
#pragma warning disable CA1848, CA1873, CA2254 // Filler records; the bundle's size is what is under test.
        var log = _logging!.Factory.CreateLogger("FallbackPlan.Test");
        for (var index = 0; index < 10_000; index++)
        {
            log.Log(
                LogLevel.Information, new EventId(4244), "filler {Index} {Noise}",
                index, LogLabel.Of(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(600))));
        }
#pragma warning restore CA1848, CA1873, CA2254

        var bundle = await ExportAsync(runtime, includePaths: false);
        var entries = Open(bundle);

        Assert.IsLessThanOrEqualTo(
            DiagnosticBundleResult.MaximumContentBytes, Convert.FromBase64String(bundle.ContentBase64).Length);
        Assert.IsGreaterThan(0, bundle.LogRecordsLeftOut);
        Assert.Contains("filler 9999 ", entries["log.txt"], StringComparison.Ordinal);
        Assert.DoesNotContain("filler 0 ", entries["log.txt"], StringComparison.Ordinal);

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        Assert.AreEqual(bundle.LogRecordsLeftOut, manifest.RootElement.GetProperty("log_records_left_out").GetInt32());
    }

    [TestMethod]
    public async Task DiagnosticsExportVerb_AgainstARunningService_WritesTheBundleAndStatesTheOptIn()
    {
        await using var runtime = await StartAsync();
        await MakeHistoryAsync(runtime);
        await using var listener = LocalServiceListener.Start(
            new ServiceCommandHandler(runtime, RemoteBindingState.Off), _harness.StateDirectory);

        Directory.CreateDirectory(_harness.WorkPath);
        var plain = Path.Combine(_harness.WorkPath, "plain.zip");
        var withPaths = Path.Combine(_harness.WorkPath, "with-paths.zip");

        var first = await RunCliAsync("diagnostics-export", plain, "--state", _harness.StateDirectory);
        Assert.AreEqual(0, first.ExitCode, first.All);
        Assert.Contains("plain.zip", first.All, StringComparison.Ordinal);
        Assert.IsFalse(ReadManifest(plain).GetProperty("includes_paths").GetBoolean());

        var second = await RunCliAsync(
            "diagnostics-export", withPaths, "--include-paths", "--state", _harness.StateDirectory);
        Assert.AreEqual(0, second.ExitCode, second.All);
        Assert.IsTrue(ReadManifest(withPaths).GetProperty("includes_paths").GetBoolean());

        // The consequence is said where the person typing the flag reads it.
        Assert.Contains("paths", second.Error, StringComparison.OrdinalIgnoreCase);

        // A file already there is somebody's; the verb does not write over it.
        var before = await File.ReadAllBytesAsync(plain);
        var third = await RunCliAsync("diagnostics-export", plain, "--state", _harness.StateDirectory);
        Assert.AreNotEqual(0, third.ExitCode);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(plain));
    }

    private static Task<HostHarness.Invocation> RunCliAsync(params string[] args) =>
        HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            args);

    private static JsonElement ReadManifest(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry("manifest.json")!.Open();
        return JsonDocument.Parse(stream).RootElement.Clone();
    }
}

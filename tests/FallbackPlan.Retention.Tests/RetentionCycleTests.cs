using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Retention;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// The whole cycle against a real staging archive (architecture 07,
/// FR-GC-001/005/006): the dry run deletes nothing, the apply tombstones and
/// still deletes nothing, the grace runs only when the writer publishes
/// again, and the sweep then removes exactly what the plan condemned while
/// the archive keeps publishing and walking clean.
/// Establishes FR-GC-002 and FR-GC-003.
/// </summary>
[TestClass]
public sealed class RetentionCycleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-retention-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "retention-cycle-passphrase!!";
    private static readonly string SetId = new('a', 32);

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string RepoPath => Path.Combine(ArchivesRoot, SetId);
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");

    public RetentionCycleTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "retention fodder");

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
                    Path = Directory.CreateDirectory(Path.Combine(_root, "vault")).FullName,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = SetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = "every 4h",
                    Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    [TestMethod]
    public async Task RetentionPass_TheFullCycle_TombstonesWaitsForAPublicationThenSweepsExactlyThePlan()
    {
        // Three snapshots on three days; each pass fans out to the vault, so
        // the gate has real synced generations to release against.
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(day1.AddDays(2));

        var store = new LocalFileSystemObjectStore(RepoPath);
        Assert.HasCount(3, await ListAsync(store, "snapshots/"));

        // Dry run: the report says what would go, and nothing goes
        // (FR-GC-001, FR-GC-005).
        var dry = await RunAsync(store, apply: false, now: day1.AddDays(2).AddHours(1));
        Assert.Contains(line => line.StartsWith("would delete: 2 snapshot", StringComparison.Ordinal), dry.Lines);
        Assert.HasCount(3, await ListAsync(store, "snapshots/"));

        // Apply: the two expired snapshots are tombstoned — and still
        // nothing is deleted, because the grace is a publication that has
        // not happened yet (FR-GC-006, spec 11 §3.1).
        var first = await RunAsync(store, apply: true, now: day1.AddDays(2).AddHours(1));
        Assert.IsGreaterThanOrEqualTo(2, first.TombstonesWritten);
        Assert.AreEqual(0, first.Swept!.Deleted);
        Assert.IsGreaterThanOrEqualTo(2, first.Swept.NotYetEligible);
        Assert.HasCount(3, await ListAsync(store, "snapshots/"));
        Assert.IsNotEmpty(await ListAsync(store, "tombstones/"));

        // The writer publishes again — the repository visibly advances past
        // the decision, which is what the grace waits for.
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day four content");
        await BackUpAsync(day1.AddDays(3));

        // Sweep: exactly the two condemned snapshots go, revalidated against
        // a world read after the grace check. Day three's snapshot is newly
        // expired and newly tombstoned — its grace starts now, so it stays.
        var second = await RunAsync(store, apply: true, now: day1.AddDays(3).AddHours(1));
        Assert.IsGreaterThanOrEqualTo(2, second.Swept!.Deleted);
        var remaining = await ListAsync(store, "snapshots/");
        Assert.HasCount(2, remaining);

        // The archive is still a working archive: a fresh pass walks clean —
        // no vetoes, no unwalkable manifests — and the next backup publishes.
        var after = await RunAsync(store, apply: false, now: day1.AddDays(3).AddHours(2));
        Assert.IsFalse(after.Lines.Any(line => line.StartsWith("NO DELETION", StringComparison.Ordinal)));

        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day five content");
        await BackUpAsync(day1.AddDays(4));
        Assert.HasCount(3, await ListAsync(store, "snapshots/"));
    }

    [TestMethod]
    public async Task RetentionPass_ALaggardDestination_HoldsExpiryAndSaysSo()
    {
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(day1.AddDays(1));

        var store = new LocalFileSystemObjectStore(RepoPath);

        // The gate consulted with the ledger emptied: the destination is
        // entitled to more history than the set keeps (a wider override,
        // FR-GC-010) and has never received any of it, so nothing may expire
        // (FR-GC-009) — held, named, and nothing tombstoned even under
        // --apply. A destination whose policy also drops the snapshot never
        // holds expiry up: pushing it would be futile, since convergence
        // would remove it again on arrival.
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(
            store, passphrase, CancellationToken.None);

        var report = await RetentionRunner.RunAsync(
            store, repository,
            new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
            [new SetDestinationReference
            {
                Ref = "vault",
                Retention = new RetentionConfiguration { MinGenerations = 10 },
            }],
            _ => null,
            _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId),
            apply: true,
            (ulong)day1.AddDays(1).AddHours(1).ToUnixTimeMilliseconds(),
            CancellationToken.None);

        Assert.AreEqual(0, report.TombstonesWritten);
        Assert.ContainsSingle(report.Held);
        Assert.Contains("vault", Assert.ContainsSingle(report.Held).AwaitingDestinations);
        Assert.IsEmpty(await ListAsync(store, "tombstones/"));
        Assert.HasCount(2, await ListAsync(store, "snapshots/"));
    }

    [TestMethod]
    public async Task RetentionVerb_DryRunThenApply_ReportsAndActsThroughTheServiceSurface()
    {
        var variable = "FBP_RETENTION_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, PassphraseText);
        try
        {
            var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
            await BackUpAsync(day1);
            File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
            await BackUpAsync(day1.AddDays(1));
            File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
            await BackUpAsync(day1.AddDays(2));

            // The default is the dry run — the verb reports and touches nothing.
            var dry = await RunVerbAsync(variable);
            Assert.AreEqual(0, dry.ExitCode, dry.Error);
            Assert.Contains("would delete: 2 snapshot", dry.Output, StringComparison.Ordinal);
            Assert.HasCount(3, await ListAsync(new LocalFileSystemObjectStore(RepoPath), "snapshots/"));

            // --apply tombstones through the same service surface a console
            // would use — on the writer lane, serialised against backups.
            var apply = await RunVerbAsync(variable, "--apply");
            Assert.AreEqual(0, apply.ExitCode, apply.Error);
            Assert.Contains("tombstoned: ", apply.Output, StringComparison.Ordinal);
            Assert.Contains("awaiting grace", apply.Output, StringComparison.Ordinal);
            Assert.IsNotEmpty(await ListAsync(new LocalFileSystemObjectStore(RepoPath), "tombstones/"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunVerbAsync(string variable, params string[] extra)
    {
        var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var error = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var exit = await AgentHost.RunAsync(
            ["retention", "--archives", ArchivesRoot, "--state", StateDirectory, "--passphrase-env", variable, .. extra],
            output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, passphrase, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private async Task<RetentionReport> RunAsync(
        LocalFileSystemObjectStore store, bool apply, DateTimeOffset now, ILogger? logger = null)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, repository,
            new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
            [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name),
            _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId),
            apply,
            (ulong)now.ToUnixTimeMilliseconds(),
            CancellationToken.None,
            "docs",
            logger);
    }

    [TestMethod]
    public async Task RetentionPass_WithALogger_ReportsWhatItPlannedAndWhatItTook()
    {
        // Deletion is the one thing a backup product does that cannot be
        // undone, and until now a pass did all of it silently. These are the
        // ids, not the report lines: the report is returned to whoever asked,
        // while the log is what survives to be read afterwards by somebody who
        // did not ask and now needs to know what went.
        const int PlanningRetention = 2900;
        const int RetentionPlanned = 2901;
        const int CollectionComplete = 2903;

        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(day1.AddDays(2));

        var log = new RecordingLogger();
        var store = new LocalFileSystemObjectStore(RepoPath);
        await RunAsync(store, apply: true, now: day1.AddDays(2).AddHours(1), logger: log);

        var planning = log.Records.Single(record => record.EventId == PlanningRetention);
        Assert.AreEqual("docs", planning.Values.First(value => value.Key == "SetName").Value);
        Assert.AreEqual(3, planning.Values.First(value => value.Key == "Snapshots").Value);

        var planned = log.Records.Single(record => record.EventId == RetentionPlanned);
        Assert.AreEqual(2, planned.Values.First(value => value.Key == "Retire").Value);

        // The apply half reached the end: an early return on the dry-run path
        // would leave this absent, which is exactly the difference worth being
        // able to see in a log.
        Assert.ContainsSingle(log.Records.Where(record => record.EventId == CollectionComplete));
    }

    private static async Task<List<string>> ListAsync(LocalFileSystemObjectStore store, string prefix)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}

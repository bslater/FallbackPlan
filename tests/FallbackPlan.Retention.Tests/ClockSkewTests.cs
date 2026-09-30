using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Retention;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// Skew between the writer's clock and the collector's changes no GC safety
/// outcome (NFR-TIME-001, architecture 04 §7, ADR-0009): a collector a day
/// ahead of the writer, a day behind, or a year ahead takes nothing a
/// tombstone still waits on, keeps the newest snapshot the floor protects,
/// and expires no write intent whose generation has not passed. What a
/// retention window keeps does follow the collector's own clock, because a
/// window is a statement about its calendar, and that is policy rather than
/// safety.
/// </summary>
/// <remarks>
/// The skew is injected as the collector's clock against a writer running on
/// the live one: a capture stamps its completion with the clock it finished
/// by, so each pass here is run at the writer's time plus or minus the skew.
/// </remarks>
[TestClass]
public sealed class ClockSkewTests : IDisposable
{
    private const string PassphraseText = "clock-skew-passphrase!!";

    private static readonly string SetId = new('a', 32);
    private static readonly DateTimeOffset PassDay = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly RetentionConfiguration Policy = new() { KeepDaily = 1, MinGenerations = 1 };

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-clock-skew-tests", Guid.NewGuid().ToString("n"));

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");

    public ClockSkewTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
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
                    Retention = Policy,
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    [TestMethod]
    public async Task ATombstone_WaitsForAPublication_HoweverFarOffTheCollectorsClockRuns()
    {
        // The grace is the repository visibly advancing past the decision,
        // never a span of time (ADR-0009 Amendment 5, specification 11 §3.1).
        await BackUpAsync(PassDay, "day one content");
        await BackUpAsync(PassDay.AddDays(1), "day two content");
        await BackUpAsync(PassDay.AddDays(2), "day three content");
        var store = new LocalFileSystemObjectStore(Path.Combine(ArchivesRoot, SetId));
        var writer = DateTimeOffset.UtcNow;

        var decided = await RunAsync(store, apply: true, writer);
        Assert.IsGreaterThan(0, decided.TombstonesWritten, "the control: the day's newest supersedes the others");
        Assert.AreEqual(0, decided.Swept!.Deleted);

        foreach (var skew in new[] { TimeSpan.FromHours(24), TimeSpan.FromHours(-24), TimeSpan.FromDays(365) })
        {
            var pass = await RunAsync(store, apply: true, writer + skew);
            Assert.AreEqual(
                0, pass.Swept!.Deleted,
                $"a collector {skew.TotalHours:+0;-0} h off the writer took what a tombstone still waits on");
        }

        Assert.HasCount(3, await ListAsync(store, "snapshots/"), "nothing went without a publication");

        // The writer publishes, and the grace has run, whatever the clock:
        // a collector a day behind the writer sweeps as one on time would.
        await BackUpAsync(PassDay.AddDays(3), "day four content");
        var swept = await RunAsync(store, apply: true, writer - TimeSpan.FromHours(24));
        Assert.IsGreaterThan(0, swept.Swept!.Deleted, "once the repository has advanced, a clock behind it does not hold the sweep");
    }

    [TestMethod]
    public async Task ACollectorAYearAhead_StillKeepsTheNewestSnapshot()
    {
        // The floor is a count (architecture 07 §2): however far past every
        // window the collector's clock runs, the newest snapshot stays, and
        // the mark keeps everything it needs.
        await BackUpAsync(PassDay, "day one content");
        await BackUpAsync(PassDay.AddDays(1), "day two content");
        var store = new LocalFileSystemObjectStore(Path.Combine(ArchivesRoot, SetId));

        var report = await RunAsync(store, apply: false, DateTimeOffset.UtcNow.AddDays(365));

        Assert.Contains(
            line => line.StartsWith("would delete: 1 snapshot", StringComparison.Ordinal), report.Lines,
            string.Join(" | ", report.Lines));
        Assert.IsFalse(
            report.Lines.Any(line => line.StartsWith("NO DELETION", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
    }

    [TestMethod]
    public void AWriteIntent_WhoseGenerationHasNotPassed_IsLive_WhateverTheCollectorsClock()
    {
        // The service declares an hour and expiry two generations on (the
        // backup runner), and a collector surveys with a five-minute margin.
        // A collector a day or a year ahead has let the hour pass many times
        // over; the intent still covers its blobs, because the generation half
        // has not passed and a clock cannot pass it.
        const ulong IssuedAt = 1_785_000_000_000;
        var writer = WriterId.FromBytes([.. Enumerable.Repeat((byte)0x77, 16)]);
        var blob = BlobId.FromBytes([.. Enumerable.Repeat((byte)0x42, 16)]);
        IReadOnlyList<JournalRecord> records =
        [
            new(JournalRecordKind.WriteIntent, writer, 1, IssuedAt,
                new JournalPayload.WriteIntent(new byte[16], [blob], 3_600_000, ExpiryGeneration: 2, IntentPurpose.Backup)),
        ];

        foreach (var skew in new[] { TimeSpan.FromHours(24), TimeSpan.FromHours(-24), TimeSpan.FromDays(365) })
        {
            var now = (ulong)((long)IssuedAt + (long)skew.TotalMilliseconds);
            var survey = IntentSurveyor.Survey(records, unparseableCount: 0, currentGeneration: 2, now, skewMarginMs: 300_000);

            Assert.IsTrue(survey.IsCovered(blob), $"a collector {skew.TotalHours:+0;-0} h off the writer expired a live intent");
        }
    }

    [TestMethod]
    public void WhichSnapshotsAWindowKeeps_FollowsTheCollectorsOwnClock()
    {
        // Policy, not safety: "keep one a day for two days" is a statement
        // about the collector's calendar, so its edge moves with the
        // collector's clock. A day ahead, yesterday falls out; a day behind,
        // it stays; and the floor, a count, does not move at all.
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var yesterday = new SnapshotFact("yesterday", (ulong)now.AddDays(-1).AddHours(-1).ToUnixTimeMilliseconds());
        var today = new SnapshotFact("today", (ulong)now.AddHours(-1).ToUnixTimeMilliseconds());
        var policy = new RetentionConfiguration { KeepDaily = 2, MinGenerations = 1 };

        Assert.IsEmpty(RetentionPlanner.Select([yesterday, today], policy, now).Expire);
        Assert.AreEqual(yesterday, Assert.ContainsSingle(RetentionPlanner.Select([yesterday, today], policy, now.AddHours(24)).Expire));
        Assert.IsEmpty(RetentionPlanner.Select([yesterday, today], policy, now.AddHours(-24)).Expire);
        Assert.Contains(
            keep => keep.Snapshot == today,
            RetentionPlanner.Select([yesterday, today], policy, now.AddDays(365)).Keep);
    }

    private async Task BackUpAsync(DateTimeOffset passTime, string content)
    {
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), content);
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, passTime, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private async Task<RetentionReport> RunAsync(LocalFileSystemObjectStore store, bool apply, DateTimeOffset collectorNow)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, opened.Repository, Policy,
            [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name),
            _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId),
            apply,
            (ulong)collectorNow.ToUnixTimeMilliseconds(),
            CancellationToken.None,
            "docs",
            reclaim: opened.Reclaim);
    }

    private static async Task<List<string>> ListAsync(LocalFileSystemObjectStore store, string prefix)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
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

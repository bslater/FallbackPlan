using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// What the sweep does when a delete will not happen (ledger O1 — a failed
/// delete leaving durable state inconsistent).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StagingSweep.SweepAsync"/> promises that it "deletes in bounded
/// order; every refusal is a finding". Two things have to hold for that to be
/// true, and neither is about the happy path.
/// </para>
/// <para>
/// The first is that one object's refusal is <em>local</em>. The sweep walks
/// tombstones in ordinal key order, so an exception escaping the loop does not
/// merely lose that object — it abandons every object sorting after it, for
/// as long as the fault persists, while the counters and findings the caller
/// was going to report are lost with the frame.
/// </para>
/// <para>
/// The second is that the counters count deletions rather than attempts. A
/// store reporting <see cref="DeleteOutcome.NotFound"/> for an object that is
/// still present is the quiet half of the same defect: the report says the
/// space was reclaimed, the operator believes it, and the object stays.
/// </para>
/// </remarks>
[TestClass]
public sealed class SweepFailureTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-sweep-failure-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "sweep-failure-tests-passphrase!!";
    private static readonly string SetId = new('a', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, SetId);

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    public SweepFailureTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "sweep fodder");

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
                    Root = SourceRoot,
                    Schedule = "every 4h",
                    Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    /// <summary>
    /// A delete that throws is a finding and the pass carries on. Today the
    /// exception escapes the sweep, so the whole retention pass fails and the
    /// objects after this one in listing order are never even considered.
    /// </summary>
    [TestMethod]
    public async Task ADeleteThatThrows_IsAFindingAndTheRestOfThePassStillRuns()
    {
        var faulting = await ArmedAtSweepAsync();

        // Every snapshot object refuses; the blobs behind them do not.
        faulting.Store.ArmThrow(key => key.StartsWith("snapshots/", StringComparison.Ordinal));

        var report = await RunAsync(faulting.Store, apply: true, now: Day1.AddDays(3).AddHours(1));

        Assert.IsNotNull(report.Swept, "the sweep did not complete — a single stubborn object aborted the pass");
        Assert.Contains(
            finding => finding.Contains("could not be deleted", StringComparison.Ordinal),
            report.Swept.Findings,
            "a delete failed and nothing said so");

        // The refused objects are all still there, and the sweep counted none
        // of them.
        Assert.HasCount(4, await ListAsync(faulting.Inner, "snapshots/"));

        // And the pass got past them: the blobs those expired snapshots held
        // alone were still condemned and still collected.
        Assert.IsGreaterThan(0, report.Swept.Deleted, "the pass stopped at the first refusal");
    }

    /// <summary>
    /// A store that reports "not found" for an object it did not remove must
    /// not be counted as a deletion. Today the outcome is discarded and the
    /// counter increments on the call.
    /// </summary>
    [TestMethod]
    public async Task ADeleteReportingNotFound_CountsNothing()
    {
        var faulting = await ArmedAtSweepAsync();
        faulting.Store.ArmNotFound();

        var report = await RunAsync(faulting.Store, apply: true, now: Day1.AddDays(3).AddHours(1));

        Assert.IsNotNull(report.Swept);
        Assert.AreEqual(
            0,
            report.Swept.Deleted,
            "the report claimed deletions the store never performed");

        // The proof that "not found" was a lie rather than a description.
        Assert.HasCount(4, await ListAsync(faulting.Inner, "snapshots/"));
    }

    /// <summary>
    /// A refusal defers; it does not condemn. The tombstone survives the
    /// failed pass, so the next pass — over a healed store — completes the
    /// collection the first one could not.
    /// </summary>
    [TestMethod]
    public async Task AnObjectRefusedOnce_IsSweptOnTheNextPass()
    {
        var faulting = await ArmedAtSweepAsync();
        faulting.Store.ArmThrow(key => key.StartsWith("snapshots/", StringComparison.Ordinal));

        var refused = await RunAsync(faulting.Store, apply: true, now: Day1.AddDays(3).AddHours(1));
        Assert.IsNotEmpty(refused.Swept!.Findings);
        Assert.HasCount(4, await ListAsync(faulting.Inner, "snapshots/"));

        // The tombstones are still standing: the decision was not withdrawn
        // and its grace clock was not restarted.
        Assert.IsNotEmpty(await ListAsync(faulting.Inner, "tombstones/"));

        faulting.Store.Heal();
        var healed = await RunAsync(faulting.Store, apply: true, now: Day1.AddDays(3).AddHours(2));

        // The two whose grace had already run go. Day three's snapshot was
        // tombstoned during the refused pass, so its own grace is still
        // waiting on the next publication — it stays, and that is the
        // resumption being correct rather than merely eventual.
        Assert.IsGreaterThanOrEqualTo(2, healed.Swept!.Deleted);
        Assert.HasCount(2, await ListAsync(faulting.Inner, "snapshots/"));
    }

    /// <summary>
    /// The findings reach the operator. <see cref="RetentionRunner"/> already
    /// appends <see cref="SweepOutcome.Findings"/> to the report — this pins
    /// that a deferral is not silently dropped between the sweep and the page
    /// a human reads.
    /// </summary>
    [TestMethod]
    public async Task ADeferredDelete_AppearsInTheOperatorsReport()
    {
        var faulting = await ArmedAtSweepAsync();
        faulting.Store.ArmThrow(key => key.StartsWith("snapshots/", StringComparison.Ordinal));

        var report = await RunAsync(faulting.Store, apply: true, now: Day1.AddDays(3).AddHours(1));

        Assert.Contains(
            line => line.Contains("could not be deleted", StringComparison.Ordinal),
            report.Lines,
            "the sweep found something and the report did not carry it");
    }

    /// <summary>
    /// The deferral posture holds for fault types no local store throws.
    /// Today's only store fails with <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>; the first remote store will
    /// fail with something else, and a catch list sized to the local
    /// filesystem would let that something escape the sweep and abort the
    /// pass — the exact defect this suite exists to prevent. A refusal is
    /// always deferrable, whatever its type: the tombstone stands and the
    /// next pass retries.
    /// </summary>
    [TestMethod]
    public async Task ADeleteThrowingAFaultTypeNoLocalStoreThrows_IsStillAFindingNotAnAbort()
    {
        var faulting = await ArmedAtSweepAsync();
        faulting.Store.ArmThrow(
            key => new TimeoutException($"Injected fault: the delete of '{key}' timed out."),
            key => key.StartsWith("snapshots/", StringComparison.Ordinal));

        var report = await RunAsync(faulting.Store, apply: true, now: Day1.AddDays(3).AddHours(1));

        Assert.IsNotNull(report.Swept, "an unlisted fault type aborted the pass");
        Assert.Contains(
            finding => finding.Contains("could not be deleted", StringComparison.Ordinal),
            report.Swept.Findings,
            "a delete failed and nothing said so");
        Assert.HasCount(4, await ListAsync(faulting.Inner, "snapshots/"));
        Assert.IsGreaterThan(0, report.Swept.Deleted, "the pass stopped at the first refusal");
    }

    /// <summary>
    /// Cancellation is not a fault to be absorbed. A cancelled sweep must
    /// still throw, or a stop request reads as a completed pass.
    /// </summary>
    [TestMethod]
    public async Task ACancelledSweep_StillThrows()
    {
        var faulting = await ArmedAtSweepAsync();

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(
            faulting.Store, passphrase, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await RetentionRunner.RunAsync(
                faulting.Store, repository, Policy, [new SetDestinationReference { Ref = "vault" }],
                _ => null, _ => TrimVerification.None, Writer, apply: true,
                (ulong)Day1.AddDays(3).AddHours(1).ToUnixTimeMilliseconds(), cancellation.Token));
    }

    private static RetentionConfiguration Policy => new() { KeepDaily = 1, MinGenerations = 1 };

    private WriterId Writer => WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId);

    /// <summary>
    /// Four daily backups, then one apply pass to lay the tombstones and one
    /// publication to run their grace out — so the next pass this test arms
    /// is a pass whose sweep has real work to refuse.
    /// </summary>
    private async Task<(DeleteFaultingObjectStore Store, LocalFileSystemObjectStore Inner)> ArmedAtSweepAsync()
    {
        await BackUpAsync(Day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(Day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(Day1.AddDays(2));

        var inner = new LocalFileSystemObjectStore(RepoPath);
        var tombstoned = await RunAsync(inner, apply: true, now: Day1.AddDays(2).AddHours(1));
        Assert.IsGreaterThanOrEqualTo(2, tombstoned.TombstonesWritten);
        Assert.AreEqual(0, tombstoned.Swept!.Deleted, "the grace cannot have run yet");

        // The publication the grace waits for.
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day four content");
        await BackUpAsync(Day1.AddDays(3));

        return (new DeleteFaultingObjectStore(inner), inner);
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, passphrase, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private async Task<RetentionReport> RunAsync(IObjectStore store, bool apply, DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, repository, Policy, [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name), _ => TrimVerification.None, Writer, apply,
            (ulong)now.ToUnixTimeMilliseconds(), CancellationToken.None);
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

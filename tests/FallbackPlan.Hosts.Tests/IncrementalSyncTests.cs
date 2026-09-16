using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A sync pass costs what has changed, not what the archive holds
/// (NFR-PERF-016, FR-REP-005, FR-DEST-003): a pair the last pass left level
/// skips on the strength of what that pass wrote down, and the writing down
/// expires so the skip cannot become permanent.
/// </summary>
/// <remarks>
/// <para>
/// The observable is <c>last_reconciled_at</c>. Only a pass that reads both
/// inventories through stamps it, so its value separates the three outcomes a
/// pass can have: unchanged means the pass skipped, moved means it read
/// through, and a moved <c>synced_sequence</c> with an unchanged stamp means
/// it carried what was new over the named phases alone.
/// </para>
/// <para>
/// The last test is the one that matters most, because it is the cost of
/// being wrong: the watermark cannot see the destination's own side, so a file
/// deleted out of the replica is invisible to it. What it may not do is stay
/// invisible.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class IncrementalSyncTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Sync_TheFirstPass_RecordsWhenItReadTheInventoriesThrough()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/content.txt", "the first thing");

        await using var runtime = await StartAsync();
        var at = DateTimeOffset.Now;

        var pass = await Scheduler.RunPassAsync(runtime, at, Timeout);
        Assert.AreEqual(1, pass.Ran);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNotNull(record);
        Assert.AreEqual(
            (ulong)at.ToUnixTimeMilliseconds(),
            record.LastReconciledAt,
            "a pass that read both inventories through has to say when, or no later pass can skip");
        Assert.IsGreaterThan(0UL, record.SyncedSequence);
    }

    [TestMethod]
    public async Task Sync_NothingPublishedSince_SkipsWithoutReadingTheInventoriesAgain()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/content.txt", "the first thing");

        await using var runtime = await StartAsync();
        var first = DateTimeOffset.Now;
        var pass = await Scheduler.RunPassAsync(runtime, first, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        var reconciledAt = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!.LastReconciledAt;
        Assert.IsNotNull(reconciledAt);

        // A sync asked for directly, the way the scheduler asks while a
        // migrating direct-ship set still has its staging archive: nothing has
        // been published in between, so there is nothing a listing could find.
        var later = first.AddMinutes(30);
        await SyncAsync(runtime, later);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!;
        Assert.AreEqual(
            reconciledAt, record.LastReconciledAt,
            "the pass read the inventories through again for a destination it already knew was level");
        Assert.AreEqual(DestinationSyncState.InSync, record.State);
        Assert.AreEqual(
            (ulong)later.ToUnixTimeMilliseconds(), record.LastAttemptAt,
            "a skipped pass is still a pass: it looked at the sequence and answered");
    }

    [TestMethod]
    public async Task Sync_ASnapshotPublishedSince_CarriesItWithoutReadingTheInventoriesThrough()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/content.txt", "the first thing");

        await using var runtime = await StartAsync();
        var first = DateTimeOffset.Now;
        var pass = await Scheduler.RunPassAsync(runtime, first, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        var level = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!;

        _harness.WriteSourceFile("docs/second.txt", "something new to carry");
        var later = first.AddHours(2);
        var second = await Scheduler.RunPassAsync(runtime, later, Timeout);
        Assert.AreEqual(1, second.Ran);
        await second.Transfers.WaitAsync(Timeout);
        await second.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!;
        Assert.IsGreaterThan(
            level.SyncedSequence, record.SyncedSequence, "the new publication must have crossed");
        Assert.AreEqual(
            level.LastReconciledAt, record.LastReconciledAt,
            "carrying what is new is not reading everything through, and must not claim to be");
    }

    [TestMethod]
    public async Task Sync_TheReadingThroughComingDue_ReadsBothInventoriesThroughAgain()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/content.txt", "the first thing");

        await using var runtime = await StartAsync();
        var first = DateTimeOffset.Now;
        var pass = await Scheduler.RunPassAsync(runtime, first, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        var reconciledAt = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!.LastReconciledAt;

        await SyncAsync(runtime, first.AddMinutes(30));
        Assert.AreEqual(
            reconciledAt,
            runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!.LastReconciledAt,
            "inside the interval the pass answers from what the last one wrote down");

        // Nothing has changed on either side. The pass reads both inventories
        // through anyway, because the watermark describes what was published
        // and not what the destination still holds, and the only thing that
        // makes that difference visible is looking.
        var due = first.AddMilliseconds(ReconciliationGate.DefaultIntervalMilliseconds + 1);
        await SyncAsync(runtime, due);

        Assert.AreEqual(
            (ulong)due.ToUnixTimeMilliseconds(),
            runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!.LastReconciledAt,
            "a reading-through comes due whether or not anything has happened");
    }

    [TestMethod]
    public async Task Sync_AnObjectLostAtTheDestination_IsNotHiddenByTheWatermark()
    {
        Directory.CreateDirectory(Vault);

        // A staging set, because this test is about a destination losing an
        // object and getting it back: a direct-ship set with one destination
        // has no second copy to get it back FROM — its replica is the only
        // holder, which is ADR-0046's shape rather than this gate's business.
        WriteConfiguration(directShip: false);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 40_000));

        await using var runtime = await StartAsync();
        var first = DateTimeOffset.Now;
        var pass = await Scheduler.RunPassAsync(runtime, first, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        // Somebody deletes a blob out of the replica. Nothing about this moves
        // a publication sequence, so the pass that follows skips the copy — and
        // this is the case the skip is dangerous in, so what happens next is
        // the whole point.
        var replicaRoot = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        var victim = Directory.GetFiles(Path.Combine(replicaRoot, "blobs"), "*", SearchOption.AllDirectories)[0];
        File.Delete(victim);

        await SyncAsync(runtime, first.AddMinutes(30));

        // The copy was skipped and the object did not come back — but the same
        // pass read bytes at the destination to prove possession, and that is
        // what noticed. The pair stops claiming to be level, which is what
        // takes the next pass off the skip path regardless of any interval.
        var afterLoss = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!;
        Assert.AreNotEqual(
            DestinationSyncState.InSync, afterLoss.State,
            "a destination that cannot answer for its bytes must not go on being called in sync");

        await SyncAsync(runtime, first.AddMinutes(60));

        Assert.IsTrue(
            File.Exists(victim),
            "a pair that is not level is read through, and reading through is what restores it");
    }

    private static async Task SyncAsync(ServiceRuntime runtime, DateTimeOffset at)
    {
        var set = runtime.Configuration.BackupSets[0];
        var sync = FanOut.Enqueue(runtime, set, "vault", at, userInitiated: true);
        Assert.IsNotNull(sync, "the sync must have been queued for this to test anything");
        await sync;
    }

    private void WriteConfiguration(bool directShip = true) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 1h",
                Destinations = [new SetDestinationReference { Ref = "vault" }],
                DirectShip = directShip,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            Timeout);
    }
}

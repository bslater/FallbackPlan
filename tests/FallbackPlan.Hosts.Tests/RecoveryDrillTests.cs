using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The scheduled restore drill (FR-DRL-002, NFR-OPS-005): the service
/// periodically restores a sampled file from a destination's own replica,
/// records what happened, and lets the answer go stale visibly.
/// </summary>
/// <remarks>
/// <para>
/// A drill is not a verification and these tests keep the two apart. A
/// challenge proves a destination still holds authentic bytes; a drill proves
/// the road from those bytes back to a file — index, catalogue, manifest,
/// segment assembly, whole-file hash — is open. They fail independently, and
/// only the second is what the product's first principle claims.
/// </para>
/// <para>
/// The replica is opened the way a stranger would open it: its own store, its
/// own repository open, a throwaway catalogue rebuilt from its own index
/// plane. Reading through the live archive would prove the live archive works
/// (<see cref="Drill_TheStagingArchiveIsRuined_StillPassesFromTheReplica"/>).
/// </para>
/// <para>
/// A drill says nothing only when the service is stopping or the pass that
/// ran it was cancelled (ADR-0054 Amendments 1 and 4). Anything else that ends
/// one is a drill that did not complete, and the tests that inject such a
/// fault do it between the drill and the service it talks to, so the fault
/// is the only thing that differs from a clean drill.
/// </para>
/// <para>
/// What the in-process drill cannot prove is stated in ADR-0054 and belongs to
/// <c>eng/recovery-drill.sh</c>: the kit file's own parse, the standalone
/// tool's dependency closure, and the machine actually being gone.
/// </para>
/// </remarks>
[TestClass]
public sealed class RecoveryDrillTests : IDisposable
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
    public async Task Drill_AWriteOnlySet_ProvesTheRoadToTheSealedContentAndSaysSo()
    {
        // The only shape setup produces: the replica's content is sealed to a
        // key the service does not hold (ADR-0042 §7), so the drill can prove
        // the road back only as far as that — and must say so as a stated
        // limit, not claim a restore it did not perform, and not report the
        // passphrase's absence as damage (ADR-0054 Amendment 2).
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");
        _harness.WriteSourceFile("docs/second.txt", "a shorter one");

        await using var runtime = await StartAsync();

        var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, first.Ran);
        await first.Transfers.WaitAsync(Timeout);
        await first.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNotNull(record);
        Assert.IsNotNull(
            record.DrilledAt,
            $"a destination holding a converged set is due its first drill: error={record.DrillFailure}");
        Assert.IsNull(record.DrillFailure, record.DrillFailure);
        Assert.IsNotNull(record.DrillLimit, "a drill that could not read the content must say what it could not prove");
        Assert.Contains("sealed", record.DrillLimit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("passphrase", record.DrillLimit, StringComparison.OrdinalIgnoreCase);
        Assert.IsGreaterThan(0, record.DrillFiles, "a drill that reached no file has proved nothing");
        Assert.AreEqual(0L, record.DrillBytes, "nothing was written, so nothing is counted as restored");

        // A limit is not a failure: the loudest notice this product raises
        // is for a recovery that would not work, not for a key it was never
        // meant to hold.
        Assert.IsEmpty(
            runtime.Notices.Notices.Where(notice => notice.Key.StartsWith("drill-failed:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Drill_NothingRestoredFromTheReplica_IsRecordedAsAFailure()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");

        await using var runtime = await StartAsync();

        var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await first.Transfers.WaitAsync(Timeout);
        await first.Drills.WaitAsync(Timeout);
        Assert.IsNull(
            runtime.DestinationSync.Find(_harness.DocsSetId, "vault")?.DrillFailure,
            "the clean drill must pass, or the damaged one below proves nothing");

        // Length-preserving rot in the content plane of the only copy there
        // is. Every segment's tag refuses, so the restore refuses, and a
        // drill that cannot bring a file back has to say so rather than
        // leaving the last good stamp standing as if it still applied.
        TamperEveryDataBlob(Assert.ContainsSingle(Directory.GetDirectories(Vault)));

        var later = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddDays(40), Timeout);
        await later.Transfers.WaitAsync(Timeout);
        await later.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNotNull(record);
        Assert.IsNotNull(record.DrillFailure, "a drill that restored nothing is not a drill that passed");
        Assert.AreEqual(0, record.DrillFiles);

        // And it is loud: a recovery that would not work is the one thing
        // this product exists to tell somebody about before they need it.
        Assert.IsNotEmpty(
            runtime.Notices.Notices.Where(notice => notice.Key.StartsWith("drill-failed:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Drill_TheStagingArchiveIsRuined_StillPassesFromTheReplica()
    {
        // A staging set, so there IS a second copy — and the drill must not be
        // reading it. Ruining the staging content plane leaves the replica
        // untouched, so a drill that opens the replica as a stranger would is
        // unaffected; one that quietly borrowed the live archive is not, and
        // would report a healthy destination as unrecoverable.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");

        await using var runtime = await StartAsync();

        var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, first.Ran);
        await first.Transfers.WaitAsync(Timeout);
        await first.Drills.WaitAsync(Timeout);

        var seeded = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNotNull(seeded?.DrilledAt, $"the staging set's drill must run too: {seeded?.DrillFailure}");
        Assert.IsNull(seeded.DrillFailure, seeded.DrillFailure);

        // Now ruin the staging copy alone. Nothing has been deleted from the
        // replica and nothing needs to be re-sent, so the copy is untouched
        // by this; only a reader pointed at the wrong store would notice.
        TamperEveryDataBlob(Path.Combine(_harness.ArchivesRoot, _harness.DocsSetId));

        var later = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddDays(40), Timeout);
        await later.Transfers.WaitAsync(Timeout);
        await later.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNotNull(record?.DrilledAt);
        Assert.AreNotEqual(seeded.DrilledAt, record.DrilledAt, "the second drill must actually have run");
        Assert.IsNull(
            record.DrillFailure,
            "a drill reads the destination's own replica; a ruined staging archive says nothing about it");
        Assert.IsGreaterThan(0, record.DrillFiles);
    }

    [TestMethod]
    public async Task Drill_TheServiceGoesAwayUnderneathIt_SaysNothingAboutTheReplica()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");

        await using var runtime = await StartAsync();

        var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await first.Transfers.WaitAsync(Timeout);
        await first.Drills.WaitAsync(Timeout);
        var drilled = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNotNull(drilled?.DrilledAt, $"the clean drill must pass first: {drilled?.DrillFailure}");

        // The service is stopping. Every command a drill in flight issues now
        // comes back cancelled, and a cancellation says nothing whatever about
        // the destination: it did not pass and it did not find damage. Writing
        // it down as a failure turns an orderly shutdown into "your backups
        // may not be restorable" — the loudest thing this product can say, and
        // in this case entirely untrue.
        await runtime.Queue.DisposeAsync();

        var outcome = await RecoveryDrillJob.RunAsync(
            runtime, runtime.Configuration.BackupSets[0], "vault",
            (ulong)DateTimeOffset.Now.AddDays(40).ToUnixTimeMilliseconds(), CancellationToken.None);

        Assert.IsNull(outcome.Failure, outcome.Failure);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.AreEqual(drilled.DrilledAt, record!.DrilledAt, "an interrupted drill must leave the last answer standing");
        Assert.IsNull(record.DrillFailure);
        Assert.IsEmpty(
            runtime.Notices.Notices.Where(notice => notice.Key.StartsWith("drill-failed:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Drill_ADisposedObjectMetWhileTheServiceRuns_IsADrillThatDidNotComplete()
    {
        // FR-DRL-002 as amended (ADR-0054 Amendment 4). A disposed object means
        // shutdown only while the service is stopping. Met while it runs, it is
        // a fault in the road back, and the drill that met it did not complete,
        // which is what it must now say. Silence here is the likeliest way the
        // SQLite pool race hid in CI, as a drill that left no trace.
        await using var runtime = await StartDrilledAsync();
        var at = DateTimeOffset.Now.AddDays(40);

        var outcome = await DrillThroughAsync(
            runtime, at, command => command is RunRestoreCommand,
            () => throw new ObjectDisposedException("SQLitePCL.sqlite3"));

        var record = Pair(runtime);
        Assert.AreEqual((ulong)at.ToUnixTimeMilliseconds(), record.DrilledAt, "a drill that did not complete was still a try");
        Assert.IsNotNull(record.DrillFailure);
        Assert.Contains("did not complete", record.DrillFailure, StringComparison.Ordinal);
        Assert.Contains("SQLitePCL.sqlite3", record.DrillFailure, StringComparison.Ordinal);
        Assert.AreEqual(outcome.Failure, record.DrillFailure);
        Assert.ContainsSingle(DrillNotices(runtime));
    }

    [TestMethod]
    public async Task Drill_ACommandCancelledWhileTheServiceRuns_IsADrillThatDidNotComplete()
    {
        // The same rule for a cancelled answer. The failure names the step
        // that came back cancelled, because "the operation was cancelled" is
        // otherwise all a reader has to go on.
        await using var runtime = await StartDrilledAsync();
        var at = DateTimeOffset.Now.AddDays(40);

        await DrillThroughAsync(runtime, at, command => command is RunRestoreCommand, Cancelled);

        var record = Pair(runtime);
        Assert.AreEqual((ulong)at.ToUnixTimeMilliseconds(), record.DrilledAt);
        Assert.IsNotNull(record.DrillFailure);
        Assert.Contains("did not complete", record.DrillFailure, StringComparison.Ordinal);
        Assert.Contains("restoring", record.DrillFailure, StringComparison.Ordinal);
        Assert.ContainsSingle(DrillNotices(runtime));
    }

    [TestMethod]
    public async Task Drill_TheServiceStopsWhileItLists_SaysNothing()
    {
        // Amendment 1's case, one command further into the drill than its own
        // test reaches. A listing that comes back cancelled is not an empty
        // folder. Read as one, a drill cut short while it chose its files
        // reported that the snapshot had nothing it could sample, and raised
        // the loudest notice there is on the way out of an orderly stop.
        await using var runtime = await StartDrilledAsync();
        var drilled = Pair(runtime);

        await DrillThroughAsync(
            runtime, DateTimeOffset.Now.AddDays(40), command => command is ListDirectoryCommand,
            async () =>
            {
                await runtime.Queue.DisposeAsync();
                return await Cancelled();
            },
            once: false);

        AssertUntouched(runtime, drilled);
    }

    [TestMethod]
    public async Task Drill_AListingCancelledWhileTheServiceRuns_IsADrillThatDidNotComplete()
    {
        // And while the service runs, the same misreading blamed the snapshot:
        // "none could be sampled" for a replica whose files were all there,
        // behind listings that never came back.
        await using var runtime = await StartDrilledAsync();

        await DrillThroughAsync(
            runtime, DateTimeOffset.Now.AddDays(40), command => command is ListDirectoryCommand, Cancelled, once: false);

        var failure = Pair(runtime).DrillFailure;
        Assert.IsNotNull(failure);
        Assert.Contains("did not complete", failure, StringComparison.Ordinal);
        Assert.Contains("listing", failure, StringComparison.Ordinal);
        Assert.ContainsSingle(DrillNotices(runtime));
    }

    [TestMethod]
    public async Task Drill_AFailureReachedWhileTheServiceStops_IsNotRecorded()
    {
        // A refusal that arrives while the service is taking itself apart is
        // about the service, not the replica. The pair keeps its last answer,
        // and the next start drills it again.
        await using var runtime = await StartDrilledAsync();
        var drilled = Pair(runtime);

        await DrillThroughAsync(
            runtime, DateTimeOffset.Now.AddDays(40), command => command is RunRestoreCommand,
            async () =>
            {
                await runtime.Queue.DisposeAsync();
                return new ServiceError(ServiceErrorReason.Failed, "the store is closing");
            });

        AssertUntouched(runtime, drilled);
    }

    [TestMethod]
    public async Task Drill_ThePassCancelledUnderneathIt_SaysNothing()
    {
        // The other half of a real stop. The pass that ran the drill runs on
        // the service's lifetime, so its cancellation is the service stopping,
        // even before the queue has heard.
        await using var runtime = await StartDrilledAsync();
        var drilled = Pair(runtime);
        using var pass = new CancellationTokenSource();

        await DrillThroughAsync(
            runtime, DateTimeOffset.Now.AddDays(40), command => command is RunRestoreCommand,
            async () =>
            {
                await pass.CancelAsync();
                return await Cancelled();
            },
            pass.Token);

        AssertUntouched(runtime, drilled);
    }

    [TestMethod]
    public async Task Drill_ADisposedObjectMetWhileTheServiceStops_SaysNothing()
    {
        // What Amendment 1 was for, kept: once the service is stopping, a
        // disposed object is the runtime taking itself apart.
        await using var runtime = await StartDrilledAsync();
        var drilled = Pair(runtime);

        await DrillThroughAsync(
            runtime, DateTimeOffset.Now.AddDays(40), command => command is RunRestoreCommand,
            async () =>
            {
                await runtime.Queue.DisposeAsync();
                throw new ObjectDisposedException("SQLitePCL.sqlite3");
            });

        AssertUntouched(runtime, drilled);
    }

    [TestMethod]
    public async Task Drill_ThatDidNotComplete_IsTriedAgainAfterAnHour_NotAtItsInterval()
    {
        // A drill that did not complete is due again an hour later, not a
        // month later (ADR-0054 Amendment 4). There is no drill-now verb to
        // clear its notice, and a fault that has passed should not stand as a
        // failed drill until the pair's interval comes round.
        await using var runtime = await StartDrilledAsync();
        var at = DateTimeOffset.Now.AddDays(40);
        await DrillThroughAsync(
            runtime, at, command => command is RunRestoreCommand,
            () => throw new ObjectDisposedException("SQLitePCL.sqlite3"));
        var incomplete = Pair(runtime);
        Assert.AreEqual(1, incomplete.ConsecutiveIncompleteDrills);

        await PassAsync(runtime, at.AddMinutes(30));
        Assert.AreEqual(incomplete.DrilledAt, Pair(runtime).DrilledAt, "inside the hour it is not due");

        var retry = at.AddMinutes(61);
        await PassAsync(runtime, retry);
        var record = Pair(runtime);
        Assert.AreEqual((ulong)retry.ToUnixTimeMilliseconds(), record.DrilledAt, "an hour on, it is drilled again");
        Assert.IsNull(record.DrillFailure, record.DrillFailure);
        Assert.AreEqual(0, record.ConsecutiveIncompleteDrills, "a drill that completes ends the back-off");
        Assert.IsEmpty(DrillNotices(runtime), "and resolves the notice");
    }

    [TestMethod]
    public async Task Drill_ThatFoundDamage_WaitsItsFullInterval()
    {
        // The back-off is for a drill that did not complete. One that
        // completed and found damage has answered, and is not asked again
        // until its interval says so (§4): drilling a broken replica every
        // hour would repeat the notice, not add evidence.
        await using var runtime = await StartDrilledAsync();
        TamperEveryDataBlob(Assert.ContainsSingle(Directory.GetDirectories(Vault)));

        var at = DateTimeOffset.Now.AddDays(40);
        await PassAsync(runtime, at);
        var failed = Pair(runtime);
        Assert.IsNotNull(failed.DrillFailure, "the damage must be found first");
        Assert.AreEqual(0, failed.ConsecutiveIncompleteDrills);

        await PassAsync(runtime, at.AddHours(2));
        Assert.AreEqual(failed.DrilledAt, Pair(runtime).DrilledAt);
    }

    [TestMethod]
    public void IncompleteRetry_StartsAtAnHour_Doubles_AndNeverWaitsPastTheInterval()
    {
        const ulong Hour = 3_600_000;
        const ulong Month = 30 * 24 * Hour;
        Assert.AreEqual(Hour, RecoveryDrillJob.IncompleteRetryMs(1, Month));
        Assert.AreEqual(2 * Hour, RecoveryDrillJob.IncompleteRetryMs(2, Month));
        Assert.AreEqual(4 * Hour, RecoveryDrillJob.IncompleteRetryMs(3, Month));
        Assert.AreEqual(512 * Hour, RecoveryDrillJob.IncompleteRetryMs(10, Month));
        Assert.AreEqual(Month, RecoveryDrillJob.IncompleteRetryMs(11, Month), "1,024 hours is past a month's interval");
        Assert.AreEqual(Month, RecoveryDrillJob.IncompleteRetryMs(int.MaxValue, Month), "and a long run of them cannot overflow");

        // A peer's cadence is its operator's, and the back-off stays under it
        // too, which is what keeps a lasting fault off somebody else's link.
        const ulong Week = 7 * 24 * Hour;
        Assert.AreEqual(Week, RecoveryDrillJob.IncompleteRetryMs(9, Week));
    }

    [TestMethod]
    public async Task Drill_ADestinationNoPassHasReached_IsNeverDue()
    {
        // Nothing has been copied there, so there is nothing to restore from.
        // Drilling would manufacture a failure about an absence that is
        // correct — the same rule the possession challenge follows.
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/content.txt", "something to capture");

        await using var runtime = await StartAsync();

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault");
        Assert.IsNull(record?.DrilledAt, "a destination nothing reached must not be drilled");
        Assert.IsNull(record?.DrillFailure, "and must not be blamed for it either");
    }

    [TestMethod]
    public async Task Drill_APairDrilledRecently_IsNotDrilledAgainOnTheNextPass()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");

        await using var runtime = await StartAsync();

        var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await first.Transfers.WaitAsync(Timeout);
        await first.Drills.WaitAsync(Timeout);
        var drilled = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!.DrilledAt;
        Assert.IsNotNull(drilled);

        // A drill rebuilds a catalogue and restores real bytes. Running one
        // every poll would make the cheapest thing in the service the most
        // expensive; the interval is what makes it affordable at all.
        var soon = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddHours(6), Timeout);
        await soon.Transfers.WaitAsync(Timeout);
        await soon.Drills.WaitAsync(Timeout);

        Assert.AreEqual(drilled, runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!.DrilledAt);
    }

    [TestMethod]
    public async Task Status_ADestinationNeverDrilled_SaysNeverRatherThanFailed()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/content.txt", "something to capture");

        await using var runtime = await StartAsync();

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);

        var row = Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations);

        // The uncounted-is-not-empty rule again (contract 1.24's measured_at):
        // a pair nobody has drilled has no answer, which a client must be able
        // to tell from an answer of "it did not work".
        Assert.IsNull(row.DrilledAt);
        Assert.IsNull(row.DrillFailure);
    }

    [TestMethod]
    public async Task Status_AfterADrill_CarriesItsAgeAndWhatItRestored()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");

        await using var runtime = await StartAsync();

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        // The premise, said with the pair's row: a pass that never drilled
        // the pair and a drill that ran without meeting the seal both leave
        // the limit empty below, and only the row tells the two apart.
        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!;
        var evidence = $"{record}; pass: {string.Join("; ", pass.Sets.Select(set => $"{set.SetName} {set.Outcome} {set.Detail}"))}";
        Assert.IsNotNull(record.DrilledAt, $"the pass did not drill the pair: {evidence}");

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);

        var row = Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations);
        Assert.AreEqual(record.DrilledAt, row.DrilledAt);
        Assert.AreEqual(record.DrillFiles, row.DrillFiles);
        Assert.IsNull(row.DrillFailure);
        Assert.AreEqual(record.DrillLimit, row.DrillLimit, "the limit reaches the matrix beside the stamp (contract 1.27)");
        Assert.IsNotNull(row.DrillLimit, $"a write-only set's drill states how far it could prove: {evidence}");
    }

    private static void TamperEveryDataBlob(string replicaRoot)
    {
        var files = Directory.GetFiles(
            Path.Combine(replicaRoot, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(files, "the destination must hold data blobs for this to test anything");

        foreach (var path in files)
        {
            var bytes = File.ReadAllBytes(path);
            for (var i = 200; i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            File.WriteAllBytes(path, bytes);
        }
    }

    private void WriteConfiguration(bool directShip)
    {
        new ClientConfiguration
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
    }

    /// <summary>A set-up installation whose one pair has converged and passed a clean drill.</summary>
    private async Task<ServiceRuntime> StartDrilledAsync()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");

        var runtime = await StartAsync();
        try
        {
            var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
            await first.Transfers.WaitAsync(Timeout);
            await first.Drills.WaitAsync(Timeout);

            var drilled = Pair(runtime);
            Assert.IsNotNull(drilled.DrilledAt, $"the clean drill must pass first: {drilled.DrillFailure}");
            Assert.IsNull(drilled.DrillFailure);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    private DestinationSyncRecord Pair(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!;

    private static IEnumerable<Notice> DrillNotices(ServiceRuntime runtime) =>
        runtime.Notices.Unacknowledged.Where(notice => notice.Key.StartsWith("drill-failed:", StringComparison.Ordinal));

    private void AssertUntouched(ServiceRuntime runtime, DestinationSyncRecord before)
    {
        var after = Pair(runtime);
        Assert.AreEqual(
            before.DrilledAt, after.DrilledAt, $"a drill cut short by a stop leaves the last answer standing: {after.DrillFailure}");
        Assert.AreEqual(before.DrillFailure, after.DrillFailure);
        Assert.AreEqual(before.DrillLimit, after.DrillLimit);
        Assert.AreEqual(before.ConsecutiveIncompleteDrills, after.ConsecutiveIncompleteDrills);
        Assert.IsEmpty(DrillNotices(runtime));
    }

    private async Task PassAsync(ServiceRuntime runtime, DateTimeOffset at)
    {
        var pass = await Scheduler.RunPassAsync(runtime, at, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
    }

    private static ValueTask<ServiceResult> Cancelled() =>
        ValueTask.FromResult<ServiceResult>(new ServiceError(ServiceErrorReason.Cancelled, "The operation was cancelled."));

    /// <summary>Drills the pair at <paramref name="at"/> with a fault put between the drill and the service.</summary>
    private Task<RecoveryDrillJob.DrillOutcome> DrillThroughAsync(
        ServiceRuntime runtime, DateTimeOffset at, Func<ServiceCommand, bool> picks,
        Func<ValueTask<ServiceResult>> fault, CancellationToken? pass = null, bool once = true) =>
        RecoveryDrillJob.RunAsync(
            runtime, new Interposed(new ServiceCommandHandler(runtime, RemoteBindingState.Off), picks, fault, once),
            runtime.Configuration.BackupSets[0], "vault", (ulong)at.ToUnixTimeMilliseconds(),
            budget: null, Random.Shared, pass ?? Timeout);

    /// <summary>
    /// The service a drill talks to, with a fault in the way: the command the
    /// predicate picks, the first one or every one, is answered by the fault
    /// instead, and everything else, the drill's closing of its source
    /// included, goes through to the service.
    /// </summary>
    private sealed class Interposed(
        IFallbackPlanService service, Func<ServiceCommand, bool> picks, Func<ValueTask<ServiceResult>> fault, bool once)
        : IFallbackPlanService
    {
        private int _struck;

        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            picks(command) && (!once || Interlocked.Exchange(ref _struck, 1) == 0)
                ? fault()
                : service.ExecuteAsync(command, cancellationToken);

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            service.WatchAsync(cancellationToken);
    }

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

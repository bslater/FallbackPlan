using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The scheduled restore drill (FR-KIT-007, NFR-OPS-005): the service
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
/// What the in-process drill cannot prove is stated in ADR-0054 and belongs to
/// <c>eng/recovery-drill.sh</c>: the kit file's own parse, the standalone
/// tool's dependency closure, and the machine actually being gone.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
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
    public async Task Drill_ADestinationHoldingTheSet_RestoresASampledFileAndRecordsIt()
    {
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
        Assert.IsGreaterThan(0, record.DrillFiles, "a drill that restored nothing has proved nothing");
        Assert.IsGreaterThan(0L, record.DrillBytes);
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

        var record = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!;
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);

        var row = Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations);
        Assert.AreEqual(record.DrilledAt, row.DrilledAt);
        Assert.AreEqual(record.DrillFiles, row.DrillFiles);
        Assert.IsNull(row.DrillFailure);
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

    private async Task<ServiceRuntime> StartAsync()
    {
        // Deliberately a passphrase-holding service over a format-1 archive,
        // for now: a scheduled drill restores content, and on a set-up
        // installation the service holds no content key (ADR-0042 §7) —
        // every sampled file reads as sealed. What a drill on a write-only
        // set proves is a decision ADR-0054 has not taken, and this fixture
        // moves when it has.
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            Timeout);
    }
}

using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The staging archive's own sweep in a running service (FR-VER-009). A
/// staging set's archive is re-read in full, a circuit carried pass after pass
/// and a week resting between circuits, every blob proven against its seal
/// and its key. Damage found there is repaired in place from a destination's
/// copy proven sound — destination repair in reverse — or, where no
/// destination holds one, left where it is, named, and held against the set
/// until it is gone. The status reports the circuit as a destination's is
/// reported (FR-VER-003, contract 1.63).
/// </summary>
/// <remarks>
/// <para>
/// Before this, nothing repaired a staging archive in place, and nothing kept
/// what was found there: a check's findings were printed and dropped, a
/// restore's read-around raised a notice saying nothing would repair it, and
/// the length a destination's sweep compared against staging blamed the
/// destination whichever copy was wrong.
/// </para>
/// <para>
/// Segments are made small with <see cref="ReplicaSweepJob.SegmentBudget"/>,
/// which sizes both sweeps, and a blob made to refuse its reads with
/// <see cref="StagingSweepJob.StagingDecorator"/>, each set before the runtime
/// starts so the class runs beside the rest of the suite.
/// </para>
/// </remarks>
[TestClass]
public sealed class StagingSweepTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string Staging => _harness.RepositoryPath;

    [TestMethod]
    public async Task StagingSweep_ARottedStagingBlob_IsReplacedFromADestinationsSoundCopy()
    {
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);
        await SyncAsync(runtime);
        var (key, victim) = Rot(Staging);
        var held = await File.ReadAllBytesAsync(PathOf(ReplicaRoot(Vault), key), Timeout);

        var outcome = await SweepStagingAsync(runtime);

        Assert.AreEqual(1, outcome.Damaged);
        Assert.AreEqual(1, outcome.Repaired);
        CollectionAssert.AreEqual(held, await File.ReadAllBytesAsync(victim, Timeout), "the destination's copy must be what landed");
        Assert.IsNull(Record(runtime).DamagedKeys, "a repaired object is not outstanding");

        var notice = StagingNotice(runtime);
        Assert.Contains("the staging archive", notice.Message, StringComparison.Ordinal);
        Assert.Contains("no longer match what was sealed", notice.Message, StringComparison.Ordinal);
        Assert.Contains("destination 'vault'", notice.Message, StringComparison.Ordinal);
        Assert.Contains("check the disk", notice.Message, StringComparison.Ordinal);

        var status = await StatusAsync(runtime);
        Assert.DoesNotContain(
            warning => warning.Contains("staging archive", StringComparison.Ordinal), status.Status.Warnings,
            "a repaired staging archive holds nothing against the set");
    }

    [TestMethod]
    public async Task StagingSweep_AValidBlobUnderAnothersKey_IsFoundByItsKey_AndReplaced()
    {
        // Staging has no copy beside it to compare a length against, so the
        // envelope naming another blob is the only witness the swap leaves.
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);
        await SyncAsync(runtime);
        var victim = FirstDataBlob(Staging);
        var other = BlobFiles(Staging).First(path => !string.Equals(path, victim, StringComparison.Ordinal));
        File.Copy(other, victim, overwrite: true);
        var key = KeyOf(Staging, victim);
        var held = await File.ReadAllBytesAsync(PathOf(ReplicaRoot(Vault), key), Timeout);

        var outcome = await SweepStagingAsync(runtime);

        Assert.AreEqual(1, outcome.Damaged);
        Assert.AreEqual(1, outcome.Repaired);
        CollectionAssert.AreEqual(held, await File.ReadAllBytesAsync(victim, Timeout));
        Assert.Contains("a different blob", StagingNotice(runtime).Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task StagingSweep_NoSoundCopyAnywhere_LeavesTheBlobWhereItIs_NamesIt_AndDegradesTheSet()
    {
        // A damaged blob still restores every record the damage did not land
        // in, so with nothing proven to replace it, it stays.
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);
        await SyncAsync(runtime);
        var (key, victim) = Rot(Staging);
        RotFile(PathOf(ReplicaRoot(Vault), key));
        var rotted = await File.ReadAllBytesAsync(victim, Timeout);

        var outcome = await SweepStagingAsync(runtime);

        Assert.AreEqual(1, outcome.Damaged);
        Assert.AreEqual(0, outcome.Repaired);
        CollectionAssert.AreEqual(rotted, await File.ReadAllBytesAsync(victim, Timeout), "nothing proven, nothing touched");
        Assert.AreEqual(key, Assert.ContainsSingle(Record(runtime).DamagedKeys!));

        var notice = StagingNotice(runtime);
        Assert.Contains("could not be", notice.Message, StringComparison.Ordinal);
        Assert.Contains("destination 'vault'", notice.Message, StringComparison.Ordinal);

        var status = await StatusAsync(runtime);
        Assert.AreEqual(ProtectionState.Degraded, status.Status.State);
        Assert.Contains(
            warning => warning.Contains("1 object(s) found damaged in the staging archive", StringComparison.Ordinal),
            status.Status.Warnings);
    }

    [TestMethod]
    public async Task StagingSweep_DamageNoCopyCouldReplace_IsTriedAgainUnderBackOff_AndRepairedOnceACopyIsSound()
    {
        // A copy that could not be found a minute ago is rarely there now,
        // and every try reads the destinations it asks; so the damage waits
        // the sync's back-off between tries, and is repaired by the first try
        // after a destination holds it sound again.
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);
        await SyncAsync(runtime);
        var (key, victim) = Rot(Staging);
        var vaultCopy = PathOf(ReplicaRoot(Vault), key);
        var sound = await File.ReadAllBytesAsync(vaultCopy, Timeout);
        RotFile(vaultCopy);

        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        var tried = Record(runtime);
        Assert.AreEqual(key, Assert.ContainsSingle(tried.DamagedKeys!), "the pass's circuit found it and could not repair it");
        Assert.AreEqual(1, tried.DamageChecks);

        await File.WriteAllBytesAsync(vaultCopy, sound, Timeout);
        await PassAsync(runtime, start.AddMinutes(1));
        Assert.IsNotNull(Record(runtime).DamagedKeys, "inside the back-off the damage is not tried again");

        await PassAsync(runtime, start.AddMinutes(3));
        Assert.IsNull(Record(runtime).DamagedKeys, "past the back-off it is tried again, and repaired");
        CollectionAssert.AreEqual(sound, await File.ReadAllBytesAsync(victim, Timeout));
    }

    [TestMethod]
    public async Task StagingSweep_DamageStillStandingAtTheNextCircuit_IsNotSaidAgain()
    {
        // Each finding is said once. The next circuit reading the same damage
        // still standing has found nothing new, so a person who acknowledged
        // it is not asked again; the set's status still counts it.
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);
        await SyncAsync(runtime);
        var (key, _) = Rot(Staging);
        RotFile(PathOf(ReplicaRoot(Vault), key));
        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        runtime.Notices.Acknowledge(StagingNotice(runtime).Id, (ulong)start.ToUnixTimeMilliseconds());
        var first = Record(runtime);

        await PassAsync(runtime, start.AddDays(StagingSweepJob.IntervalDays).AddMinutes(1));

        Assert.AreNotEqual(first.SweptAt, Record(runtime).SweptAt, "the next circuit must have read it again");
        Assert.AreEqual(key, Assert.ContainsSingle(Record(runtime).DamagedKeys!));
        Assert.IsFalse(
            runtime.Notices.Unacknowledged.Any(notice => notice.Key == $"staging-damaged:{_harness.DocsSetId}"),
            "damage already said is not said again");
        Assert.AreEqual(ProtectionState.Degraded, (await StatusAsync(runtime)).Status.State);
    }

    [TestMethod]
    public async Task StagingSweep_ClosesItsCircuitOverConsecutivePasses_AndRestsAWeekBetweenCircuits()
    {
        ReplicaSweepJob.SegmentBudget = 1;
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/second.txt", "a second capture, so the staging archive spans more blobs");
        await BackUpAsync(runtime);

        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        var blobs = BlobFiles(Staging).Count;
        Assert.IsGreaterThanOrEqualTo(3, blobs, "the staging archive must span several blobs for segmenting to mean anything");
        var first = Record(runtime);
        Assert.IsNotNull(first.SweptAt, "the first segment ran in the first pass");
        Assert.IsNull(first.SweepCompletedAt, "one blob of several cannot close a circuit");

        for (var minute = 1; minute <= blobs && Record(runtime).SweepCompletedAt is null; minute++)
        {
            await PassAsync(runtime, start.AddMinutes(minute));
        }

        var closed = Record(runtime);
        Assert.IsNotNull(closed.SweepCompletedAt, "a circuit that has begun is carried on each pass until it closes");
        Assert.IsNull(closed.SweepCursor);

        var closedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)closed.SweepCompletedAt.Value);
        await PassAsync(runtime, closedAt.AddDays(1));
        Assert.AreEqual(closed.SweptAt, Record(runtime).SweptAt, "a closed circuit rests a week");

        await PassAsync(runtime, closedAt.AddDays(StagingSweepJob.IntervalDays).AddMinutes(1));
        Assert.AreNotEqual(closed.SweptAt, Record(runtime).SweptAt, "past the week the next circuit begins");
    }

    [TestMethod]
    public async Task StagingSweep_ADirectShipSet_HasNoStagingArchiveToSweep_AndReportsNone()
    {
        await using var runtime = await StartAsync(directShip: true);
        await BackUpAsync(runtime);

        await PassAsync(runtime, DateTimeOffset.Now);

        Assert.IsNull(runtime.StagingSweeps.Find(_harness.DocsSetId), "a set with no staging archive has no sweep of one");
        Assert.AreEqual(0, (await SweepStagingAsync(runtime)).Examined);
        Assert.IsNull((await StatusAsync(runtime)).StagingSweep, "no staging archive, so no sweep to report");
    }

    [TestMethod]
    public async Task Status_ReportsTheStagingCircuit_AsADestinationsIsReported()
    {
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);

        await PassAsync(runtime, DateTimeOffset.Now);

        var sweep = (await StatusAsync(runtime)).StagingSweep;
        Assert.IsNotNull(sweep);
        Assert.AreEqual(StagingSweepJob.IntervalDays, sweep.IntervalDays);
        Assert.AreEqual(Record(runtime).SweepCompletedAt, sweep.CircuitClosedAt, "a small archive closes its circuit in one segment");
        Assert.IsNotNull(sweep.CircuitClosedAt);
        Assert.AreEqual(0, sweep.ReadThisCircuit);
        Assert.AreEqual(Record(runtime).SweptAt, sweep.LastReadAt);
        Assert.AreEqual(0, sweep.Stalls);
        Assert.IsNull(sweep.StalledOn);
    }

    [TestMethod]
    public async Task StagingSweep_AStagingBlobThatWillNotRead_StallsUnderBackOff_AndIsSaidAfterThree()
    {
        // As at a destination: a read that fails is not a finding, so the
        // segment stops at the blob, keeps what it read before it, and waits
        // the back-off; three in a row are said, and a segment that reads
        // past it withdraws the saying.
        string? unreadable = null;
        StagingSweepJob.StagingDecorator = staging => new UnreadableObjectStore(staging, key => key == unreadable);
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);
        await SyncAsync(runtime);
        var keys = BlobKeys(Staging);
        Assert.IsGreaterThanOrEqualTo(2, keys.Count, "the staging archive must span several blobs");
        unreadable = keys[1];

        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        var stalled = Record(runtime);
        Assert.AreEqual(1, stalled.SweepStalls);
        Assert.AreEqual(unreadable, stalled.SweepStalledOn);
        Assert.AreEqual(1, stalled.SweptThisCircuit, "what was read before the blob stands");
        Assert.IsNull(stalled.DamagedKeys, "a blob that would not be read has not been shown to be altered");

        await PassAsync(runtime, start.AddSeconds(30));
        Assert.AreEqual(stalled.SweepStalledAt, Record(runtime).SweepStalledAt, "inside the back-off it is not read again");
        Assert.IsNull(StallNotice(runtime), "one stall is not yet said");

        await SweepStagingAsync(runtime);
        await SweepStagingAsync(runtime);
        Assert.AreEqual(3, Record(runtime).SweepStalls);
        Assert.Contains(unreadable, StallNotice(runtime)!.Message, StringComparison.Ordinal);

        unreadable = "nothing";
        await SweepStagingAsync(runtime);
        Assert.AreEqual(0, Record(runtime).SweepStalls);
        Assert.IsNull(StallNotice(runtime), "a segment that read past it withdraws the stall");
    }

    [TestMethod]
    public async Task DestinationSweep_ALengthThatDiffersBecauseStagingsCopyIsDamaged_RepairsStaging_NotTheDestination()
    {
        // The destination's blob is the one its key names, unaltered, so the
        // staging copy of another length is the one that is wrong. The sweep
        // holds the set's gate and reads both, and it repairs staging there
        // and then rather than failing the destination it read sound.
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);
        await SyncAsync(runtime);
        var victim = FirstDataBlob(Staging);
        var key = KeyOf(Staging, victim);
        var whole = await File.ReadAllBytesAsync(victim, Timeout);
        await File.WriteAllBytesAsync(victim, whole[..^100], Timeout);
        var held = await File.ReadAllBytesAsync(PathOf(ReplicaRoot(Vault), key), Timeout);

        var outcome = await ReplicaSweepJob.RunAsync(
            runtime, runtime.Configuration.BackupSets.Single(), "vault",
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), userInitiated: false, Timeout);

        Assert.AreEqual(0, outcome.Damaged, "the destination's copy is the blob");
        Assert.AreEqual(DestinationSyncState.InSync, Row(runtime).State, Row(runtime).LastError);
        Assert.IsFalse(
            runtime.Notices.Unacknowledged.Any(notice => notice.Key.StartsWith("deep-verify-failed:", StringComparison.Ordinal)),
            "nothing is wrong at the destination");
        CollectionAssert.AreEqual(held, await File.ReadAllBytesAsync(victim, Timeout), "staging is repaired from the copy it was compared against");
        Assert.IsNull(Record(runtime).DamagedKeys);
        Assert.Contains("destination 'vault' was swept", StagingNotice(runtime).Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task StagingSweep_ARepairCutShortBetweenDeleteAndPut_RefillsTheKey_AndLetsGoOfAKeyNoRepairEmptied()
    {
        // A repair deletes the damaged copy before it puts the sound one, and
        // a backup running beside it may already reference the key; a service
        // stopped between the two leaves it empty. That key is refilled. A key
        // nothing here emptied is one the trim let go, and history does not
        // come back.
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/second.txt", new string('s', 200_000) + "a second capture, a second data blob");
        await BackUpAsync(runtime);
        await SyncAsync(runtime);
        var data = DataBlobKeys(Staging);
        Assert.IsGreaterThanOrEqualTo(2, data.Count, "the staging archive must hold two data blobs");
        var (cut, trimmed) = (data[0], data[1]);
        var held = await File.ReadAllBytesAsync(PathOf(ReplicaRoot(Vault), cut), Timeout);

        var setId = _harness.DocsSetId;
        runtime.StagingSweeps.RecordDamage(setId, unrepaired: [cut, trimmed], resolved: []);
        runtime.StagingSweeps.RecordRepairing(setId, cut);
        File.Delete(PathOf(Staging, cut));
        File.Delete(PathOf(Staging, trimmed));

        await SweepStagingAsync(runtime);

        CollectionAssert.AreEqual(held, await File.ReadAllBytesAsync(PathOf(Staging, cut), Timeout), "the emptied key is refilled");
        Assert.IsFalse(File.Exists(PathOf(Staging, trimmed)), "a key no repair emptied is not brought back");
        Assert.IsNull(Record(runtime).DamagedKeys);
        Assert.IsNull(Record(runtime).RepairingKeys);
    }

    [TestMethod]
    public async Task StagingSweep_ARepairWhosePutIsRefused_IsFinishedByALaterTry_ThoughTheTryRepairedMoreAfterIt()
    {
        // A disk that takes the delete and refuses the put leaves the key
        // empty, and the try carries on to the rest of the damage. Every key a
        // repair emptied is remembered, not only the last one begun.
        string? refused = null;
        StagingSweepJob.StagingDecorator = staging =>
        {
            var store = new PutFaultingObjectStore(staging);
            if (refused is { } key)
            {
                store.Arm(candidate => candidate == key);
            }

            return store;
        };
        await using var runtime = await StartAsync(directShip: false);
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/second.txt", new string('s', 200_000) + "a second capture, a second data blob");
        await BackUpAsync(runtime);
        await SyncAsync(runtime);
        var data = DataBlobKeys(Staging);
        Assert.IsGreaterThanOrEqualTo(2, data.Count, "the staging archive must hold two data blobs");
        var (cut, after) = (data[0], data[1]);
        var cutHeld = await File.ReadAllBytesAsync(PathOf(ReplicaRoot(Vault), cut), Timeout);
        var afterHeld = await File.ReadAllBytesAsync(PathOf(ReplicaRoot(Vault), after), Timeout);
        RotFile(PathOf(Staging, cut));
        RotFile(PathOf(Staging, after));
        refused = cut;

        var start = DateTimeOffset.UtcNow;
        await SweepStagingAsync(runtime, start);

        Assert.IsFalse(File.Exists(PathOf(Staging, cut)), "the delete was taken and the put refused");
        CollectionAssert.AreEqual(afterHeld, await File.ReadAllBytesAsync(PathOf(Staging, after), Timeout), "the try carried on");
        Assert.AreEqual(cut, Assert.ContainsSingle(Record(runtime).RepairingKeys!), "the repair after it returned; this one did not");

        refused = null;
        await SweepStagingAsync(runtime, start.AddHours(1));

        Assert.IsTrue(File.Exists(PathOf(Staging, cut)), "the key the refused put left empty is owed back");
        CollectionAssert.AreEqual(cutHeld, await File.ReadAllBytesAsync(PathOf(Staging, cut), Timeout));
        Assert.IsNull(Record(runtime).DamagedKeys);
        Assert.IsNull(Record(runtime).RepairingKeys);
    }

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    private async Task<BackupSetStatusDescriptor> StatusAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        return status.Sets.Single();
    }

    private StagingSweepRecord Record(ServiceRuntime runtime) =>
        runtime.StagingSweeps.Find(_harness.DocsSetId)
        ?? throw new AssertFailedException("the set has no staging sweep record");

    private static DestinationSyncRecord Row(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(runtime.Configuration.BackupSets.Single().Id, "vault")
        ?? throw new AssertFailedException("'vault' has no ledger row");

    private Notice StagingNotice(ServiceRuntime runtime) =>
        runtime.Notices.Unacknowledged.SingleOrDefault(notice => notice.Key == $"staging-damaged:{_harness.DocsSetId}")
        ?? throw new AssertFailedException(
            "no staging notice stands: " + string.Join(" | ", runtime.Notices.Unacknowledged.Select(notice => notice.Key)));

    private Notice? StallNotice(ServiceRuntime runtime) =>
        runtime.Notices.Unacknowledged.SingleOrDefault(notice => notice.Key == $"staging-sweep-stalled:{_harness.DocsSetId}");

    private Task<StagingSweepJob.SegmentOutcome> SweepStagingAsync(ServiceRuntime runtime, DateTimeOffset? now = null) =>
        StagingSweepJob.RunAsync(
            runtime, runtime.Configuration.BackupSets.Single(),
            (ulong)(now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(), userInitiated: false, Timeout);

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task PassAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var pass = await Scheduler.RunPassAsync(runtime, now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
    }

    /// <summary>One sync and no sweep, so a test can choose what the sweeps will meet.</summary>
    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var sync = FanOut.Enqueue(
            runtime, runtime.Configuration.BackupSets.Single(), "vault", DateTimeOffset.Now, userInitiated: true);
        Assert.IsNotNull(sync, "nothing else was syncing, so the sync cannot have been coalesced away");
        await sync.WaitAsync(Timeout);
        Assert.AreEqual(DestinationSyncState.InSync, Row(runtime).State, Row(runtime).LastError);
    }

    /// <summary>The one repository directory a destination path holds.</summary>
    private static string ReplicaRoot(string destinationPath) =>
        Assert.ContainsSingle(Directory.GetDirectories(destinationPath));

    private static List<string> BlobFiles(string root) =>
        [.. Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    /// <summary>The store's blob keys, in the order a circuit reads them.</summary>
    private static List<string> BlobKeys(string root) =>
        [.. BlobFiles(root).Select(path => KeyOf(root, path)).Order(StringComparer.Ordinal)];

    private static List<string> DataBlobKeys(string root) =>
        [.. BlobKeys(root).Where(key => key.StartsWith("blobs/data/", StringComparison.Ordinal))];

    private static string KeyOf(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string PathOf(string root, string key) =>
        Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The store's first data blob, in key order — the one <see cref="Rot(string)"/> damages.</summary>
    private static string FirstDataBlob(string root) =>
        Directory.GetFiles(Path.Combine(root, "blobs", "data"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .First();

    /// <summary>Flips one byte in the middle of the store's first data blob.</summary>
    private static (string Key, string Path) Rot(string root)
    {
        var victim = FirstDataBlob(root);
        RotFile(victim);
        return (KeyOf(root, victim), victim);
    }

    private static void RotFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private async Task<ServiceRuntime> StartAsync(bool directShip)
    {
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");
        _harness.WriteSourceFile("docs/big.bin", new string('b', 300_000));
        Directory.CreateDirectory(Vault);

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('1', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }
}

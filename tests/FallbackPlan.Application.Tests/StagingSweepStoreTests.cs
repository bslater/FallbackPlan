using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// Where each set's sweep of its staging archive stands (FR-VER-009): the
/// circuit carried across segments, a stall waited out, and the damage found
/// there outliving the segment that found it until a sound copy replaces it.
/// These pin the record those decisions are made from, as
/// <c>DestinationSyncStoreTests</c> pins a destination's.
/// </summary>
[TestClass]
public sealed class StagingSweepStoreTests
{
    private const string SetId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSetId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private string _state = null!;

    private string FilePath => Path.Combine(_state, "staging-sweeps.json");

    [TestInitialize]
    public void SetUp()
    {
        _state = Path.Combine(Path.GetTempPath(), $"fbp-staging-sweeps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_state);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public void RecordSweep_CarriesACircuitAcrossSegments_AndOnlyTheLastClosesIt()
    {
        // The tally answers how far through the circuit under way, and only
        // the segment that reached the end may say every blob was read.
        var store = StagingSweepStore.Open(_state);

        store.RecordSweep(SetId, "blobs/data/b", examined: 2, completedCircuit: false, 1_000);
        var open = store.RecordSweep(SetId, "blobs/data/d", examined: 2, completedCircuit: false, 2_000);
        Assert.AreEqual("blobs/data/d", open.SweepCursor);
        Assert.AreEqual(4, open.SweptThisCircuit);
        Assert.AreEqual(2_000UL, open.SweptAt);
        Assert.IsNull(open.SweepCompletedAt, "a circuit under way has closed nothing");

        var closed = store.RecordSweep(SetId, cursor: null, examined: 1, completedCircuit: true, 3_000);
        Assert.IsNull(closed.SweepCursor);
        Assert.AreEqual(0, closed.SweptThisCircuit, "the tally starts again with the next circuit");
        Assert.AreEqual(3_000UL, closed.SweepCompletedAt);

        var reopened = StagingSweepStore.Open(_state).Find(SetId)!;
        Assert.AreEqual(3_000UL, reopened.SweepCompletedAt, "a restart must not forget the circuit");
    }

    [TestMethod]
    public void RecordSweepStall_ThatReadNothing_CountsAnotherStall_AndMovesNoCursor()
    {
        var store = StagingSweepStore.Open(_state);
        store.RecordSweep(SetId, "blobs/data/a", examined: 1, completedCircuit: false, 1_000);

        store.RecordSweepStall(SetId, "blobs/data/a", examined: 0, stalledOn: "blobs/data/b", 2_000);
        var twice = store.RecordSweepStall(SetId, "blobs/data/a", examined: 0, stalledOn: "blobs/data/b", 3_000);

        Assert.AreEqual(2, twice.SweepStalls);
        Assert.AreEqual(3_000UL, twice.SweepStalledAt);
        Assert.AreEqual("blobs/data/b", twice.SweepStalledOn);
        Assert.AreEqual("blobs/data/a", twice.SweepCursor);
        Assert.AreEqual(1_000UL, twice.SweptAt, "a stall that read nothing is not a read");
        Assert.AreEqual(1, twice.SweptThisCircuit);
        Assert.AreEqual(2, StagingSweepStore.Open(_state).Find(SetId)!.SweepStalls, "a restart must not reset the back-off");
    }

    [TestMethod]
    public void RecordSweepStall_ThatReadSomething_KeepsItsProgress_AndCountsFromOne()
    {
        var store = StagingSweepStore.Open(_state);
        store.RecordSweepStall(SetId, cursor: null, examined: 0, stalledOn: "blobs/data/a", 1_000);
        store.RecordSweepStall(SetId, cursor: null, examined: 0, stalledOn: "blobs/data/a", 2_000);

        var further = store.RecordSweepStall(SetId, "blobs/data/b", examined: 2, stalledOn: "blobs/data/c", 3_000);

        Assert.AreEqual(1, further.SweepStalls);
        Assert.AreEqual("blobs/data/b", further.SweepCursor);
        Assert.AreEqual(3_000UL, further.SweptAt);
        Assert.AreEqual(2, further.SweptThisCircuit);
        Assert.AreEqual("blobs/data/c", further.SweepStalledOn);
    }

    [TestMethod]
    public void RecordSweep_ASegmentThatFinished_ClearsTheStall()
    {
        var store = StagingSweepStore.Open(_state);
        store.RecordSweepStall(SetId, cursor: null, examined: 0, stalledOn: "blobs/data/a", 1_000);

        var finished = store.RecordSweep(SetId, "blobs/data/a", examined: 1, completedCircuit: false, 2_000);

        Assert.AreEqual(0, finished.SweepStalls);
        Assert.IsNull(finished.SweepStalledAt);
        Assert.IsNull(finished.SweepStalledOn);
        Assert.DoesNotContain(
            "sweep_stall", File.ReadAllText(FilePath), StringComparison.Ordinal,
            "a set that is not stalled carries no stall columns");
    }

    [TestMethod]
    public void RecordDamage_AddsWhatWasFound_AndClearsWhatWasResolved()
    {
        // The keys outlive the segment that found them: a repair cut short,
        // or one with no sound copy to draw on, leaves them for the next.
        var store = StagingSweepStore.Open(_state);

        store.RecordDamage(SetId, unrepaired: ["blobs/data/a", "blobs/data/b"], resolved: []);
        CollectionAssert.AreEquivalent(
            new[] { "blobs/data/a", "blobs/data/b" },
            StagingSweepStore.Open(_state).Find(SetId)!.DamagedKeys!.ToList());

        store.RecordDamage(SetId, unrepaired: ["blobs/data/b"], resolved: ["blobs/data/a"]);
        Assert.AreEqual("blobs/data/b", Assert.ContainsSingle(store.Find(SetId)!.DamagedKeys!));

        var whole = store.RecordDamage(SetId, unrepaired: [], resolved: ["blobs/data/b"]);
        Assert.IsNull(whole.DamagedKeys, "a set with nothing outstanding carries no list at all");
        Assert.DoesNotContain("damage", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [TestMethod]
    public void RecordDamageChecked_CountsTheTriesThatLeftDamage_AndNewDamageIsDueAtOnce()
    {
        // A try that leaves damage standing waits longer before the next, as
        // a sync's failures do; damage nobody has tried yet is tried at once,
        // and so is everything outstanding with it.
        var store = StagingSweepStore.Open(_state);
        var found = store.RecordDamage(SetId, unrepaired: ["blobs/data/a"], resolved: []);
        Assert.IsNull(found.DamageCheckedAt, "damage nothing has tried to repair is due at once");
        Assert.AreEqual(0, found.DamageChecks);

        store.RecordDamageChecked(SetId, 1_000);
        var twice = store.RecordDamageChecked(SetId, 2_000);
        Assert.AreEqual(2_000UL, twice.DamageCheckedAt);
        Assert.AreEqual(2, twice.DamageChecks);
        Assert.AreEqual(2, StagingSweepStore.Open(_state).Find(SetId)!.DamageChecks, "a restart must not reset the back-off");

        var again = store.RecordDamage(SetId, unrepaired: ["blobs/data/a"], resolved: []);
        Assert.AreEqual(2_000UL, again.DamageCheckedAt, "damage already outstanding keeps its back-off");

        var more = store.RecordDamage(SetId, unrepaired: ["blobs/data/b"], resolved: []);
        Assert.IsNull(more.DamageCheckedAt, "new damage is due at once");
        Assert.AreEqual(0, more.DamageChecks);
    }

    [TestMethod]
    public void RecordDamageChecked_WithNothingOutstanding_StampsNothing()
    {
        var store = StagingSweepStore.Open(_state);
        store.RecordDamage(SetId, unrepaired: ["blobs/data/a"], resolved: []);
        store.RecordDamage(SetId, unrepaired: [], resolved: ["blobs/data/a"]);

        var row = store.RecordDamageChecked(SetId, 1_000);

        Assert.IsNull(row.DamageCheckedAt);
        Assert.AreEqual(0, row.DamageChecks);
    }

    [TestMethod]
    public void RecordRepairing_KeepsEveryKeyUnderRepair_ThroughARestart_UntilItsRepairReturns()
    {
        // A repair deletes the damaged copy before it puts the sound one, so a
        // service stopped between the two, or a put the disk refused, leaves
        // the key empty. Each key so left must still be known after a restart,
        // and after the repairs that followed it returned: it is a missing key
        // the next try refills rather than lets go.
        var store = StagingSweepStore.Open(_state);
        store.RecordDamage(SetId, unrepaired: ["blobs/data/a", "blobs/data/b"], resolved: []);

        store.RecordRepairing(SetId, "blobs/data/b");
        store.RecordRepairing(SetId, "blobs/data/a");
        store.RecordRepairReturned(SetId, "blobs/data/a");
        Assert.AreEqual("blobs/data/b", Assert.ContainsSingle(StagingSweepStore.Open(_state).Find(SetId)!.RepairingKeys!));

        var returned = store.RecordRepairReturned(SetId, "blobs/data/b");
        Assert.IsNull(returned.RepairingKeys);
        Assert.DoesNotContain("repairing", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [TestMethod]
    public void RecordDamage_AKeyResolved_IsNoLongerOwedBack()
    {
        // A key whose repair did not return is owed back only while it is
        // outstanding. One read sound since, rewritten by a backup that found
        // it absent, is not refilled again when the trim lets it go.
        var store = StagingSweepStore.Open(_state);
        store.RecordDamage(SetId, unrepaired: ["blobs/data/a", "blobs/data/b"], resolved: []);
        store.RecordRepairing(SetId, "blobs/data/a");
        store.RecordRepairing(SetId, "blobs/data/b");

        var row = store.RecordDamage(SetId, unrepaired: [], resolved: ["blobs/data/a"]);
        Assert.AreEqual("blobs/data/b", Assert.ContainsSingle(row.RepairingKeys!));

        row = store.RecordDamage(SetId, unrepaired: [], resolved: ["blobs/data/b"]);
        Assert.IsNull(row.DamagedKeys);
        Assert.IsNull(row.RepairingKeys);
    }

    [TestMethod]
    public void EachSet_KeepsItsOwnRecord()
    {
        var store = StagingSweepStore.Open(_state);
        store.RecordSweep(SetId, "blobs/data/a", examined: 1, completedCircuit: false, 1_000);
        store.RecordDamage(OtherSetId, unrepaired: ["blobs/data/z"], resolved: []);

        var reopened = StagingSweepStore.Open(_state);

        Assert.AreEqual("blobs/data/a", reopened.Find(SetId)!.SweepCursor);
        Assert.IsNull(reopened.Find(SetId)!.DamagedKeys);
        Assert.AreEqual("blobs/data/z", Assert.ContainsSingle(reopened.Find(OtherSetId)!.DamagedKeys!));
        Assert.IsNull(reopened.Find(OtherSetId)!.SweptAt);
        Assert.HasCount(2, reopened.Records);
    }

    [TestMethod]
    public void ConcurrentDamageAndSweep_LosesNeitherSideOfTheRecord()
    {
        // A restore records what it found from a command's thread while a
        // segment records its progress on the transfer lane, so each write
        // must carry forward what the other just set.
        var store = StagingSweepStore.Open(_state);
        const int rounds = 200;

        var sweeps = Task.Run(() =>
        {
            for (var round = 1; round <= rounds; round++)
            {
                store.RecordSweep(SetId, $"blobs/data/{round:D4}", examined: 1, completedCircuit: false, (ulong)round);
            }
        });
        var finds = Task.Run(() =>
        {
            for (var round = 1; round <= rounds; round++)
            {
                store.RecordDamage(SetId, unrepaired: [$"blobs/data/{round:D4}"], resolved: []);
            }
        });
        Task.WaitAll(sweeps, finds);

        var row = StagingSweepStore.Open(_state).Find(SetId)!;
        Assert.AreEqual(rounds, row.SweptThisCircuit);
        Assert.HasCount(rounds, row.DamagedKeys!);
    }

    [TestMethod]
    [FallbackPlan.TestSupport.PlatformCondition(FallbackPlan.TestSupport.TestPlatforms.Windows,
        "a rename holds the file it renamed open for deletion until it returns, and only Windows refuses "
        + "a reader that does not share deletion with that handle")]
    public void Open_WhileAWriteHoldsTheFileForDeletion_StillReadsIt()
    {
        // As every state file beside it: `status` may read while the service
        // replaces the file, and the read must not fail the command.
        StagingSweepStore.Open(_state).RecordSweep(SetId, "blobs/data/a", examined: 1, completedCircuit: false, 1_000);

        using (File.OpenHandle(
            FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose))
        {
            Assert.Throws<IOException>(
                () => File.ReadAllText(FilePath),
                "the held handle must refuse a reader that does not share deletion, or this test proves nothing");
            Assert.AreEqual("blobs/data/a", StagingSweepStore.Open(_state).Find(SetId)?.SweepCursor);
        }
    }

    [TestMethod]
    public void Open_UnreadableGarbage_SetsTheFileAsideAndStartsEmpty()
    {
        // Sacrificial, as the ledger is: an empty record starts the circuit
        // again, and damage it forgot is found again by the next circuit.
        File.WriteAllText(FilePath, "{{{ this was never JSON");

        var store = StagingSweepStore.Open(_state);

        Assert.IsNull(store.Find(SetId));
        Assert.IsTrue(File.Exists(FilePath + ".corrupt"), "the unreadable bytes must be preserved for inspection");
        store.RecordSweep(SetId, "blobs/data/a", examined: 1, completedCircuit: false, 1_000);
        Assert.IsNotNull(StagingSweepStore.Open(_state).Find(SetId));
    }

    [TestMethod]
    public void Open_AFileOneSchemaAhead_IsSetAside()
    {
        File.WriteAllText(FilePath, """
            { "schema_version": 2, "sets": [
                { "set": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sweep_cursor": "blobs/data/a" } ] }
            """);

        var store = StagingSweepStore.Open(_state);

        Assert.IsNull(store.Find(SetId));
        Assert.IsTrue(File.Exists(FilePath + ".corrupt"), "the newer file's bytes must be preserved");
    }

    [TestMethod]
    public void SaveThenOpen_RoundTripsThroughTheVersionedShape()
    {
        StagingSweepStore.Open(_state).RecordDamage(SetId, unrepaired: ["blobs/data/a"], resolved: []);

        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"schema_version\": 1", text, StringComparison.Ordinal);
        Assert.Contains("\"damaged_keys\"", text, StringComparison.Ordinal);
        Assert.AreEqual("blobs/data/a", Assert.ContainsSingle(StagingSweepStore.Open(_state).Find(SetId)!.DamagedKeys!));
    }
}

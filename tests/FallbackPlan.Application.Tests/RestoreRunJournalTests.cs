using FallbackPlan.Application;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The service's record of each restore run it began and has not seen
/// finish (FR-RST-008, ADR-0115): written before the run writes anything,
/// kept while it is under way and after it ends unfinished, and dropped
/// once a run of it finishes or a person lets it go. It outlives the process
/// that wrote it, because a run cut short by the machine stopping is the one
/// nobody heard end.
/// </summary>
[TestClass]
public sealed class RestoreRunJournalTests
{
    private const string Run = "0123456789abcdef";
    private const string Other = "fedcba9876543210";

    private string _state = null!;

    private string FilePath => Path.Combine(_state, "restore-runs.json");

    [TestInitialize]
    public void SetUp()
    {
        _state = Path.Combine(Path.GetTempPath(), $"fbp-restore-runs-{Guid.NewGuid():N}");
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
    public void ARunBegun_IsKeptWhole_AndOutlivesTheProcessThatBeganIt()
    {
        var journal = RestoreRunJournal.Open(_state);

        journal.Begin(Record(Run) with
        {
            SetName = "docs",
            Path = null,
            Paths = ["docs/a.txt", "docs/b"],
            Target = "folder",
            Existing = "rename",
            InPlace = true,
        });

        var reopened = Assert.ContainsSingle(RestoreRunJournal.Open(_state).Runs);
        Assert.AreEqual(Run, reopened.Run);
        Assert.AreEqual("docs", reopened.SetName);
        Assert.AreEqual(new string('e', 32), reopened.SnapshotId);
        SequenceAssert.AreEqual(["docs/a.txt", "docs/b"], reopened.Paths!);
        Assert.AreEqual("/srv/restored", reopened.OutputDirectory);
        Assert.AreEqual("folder", reopened.Target);
        Assert.AreEqual("rename", reopened.Existing);
        Assert.IsTrue(reopened.InPlace);
        Assert.AreEqual(1_000UL, reopened.BeganAt);
    }

    [TestMethod]
    public void ARunUnderWay_IsNotUnfinished_UntilItEndsSo()
    {
        // A run the service is still writing has not been cut short, and
        // naming it as one would invite a second run into its folder.
        var journal = RestoreRunJournal.Open(_state);
        journal.Begin(Record(Run));

        Assert.IsTrue(journal.IsLive(Run));
        Assert.IsEmpty(journal.Unfinished);

        journal.Leave(Run);

        Assert.IsFalse(journal.IsLive(Run));
        Assert.AreEqual(Run, Assert.ContainsSingle(journal.Unfinished).Run);
    }

    [TestMethod]
    public void EveryRunOnRecordAtOpening_IsUnfinished()
    {
        // The process that was writing them has gone.
        RestoreRunJournal.Open(_state).Begin(Record(Run));

        var reopened = RestoreRunJournal.Open(_state);

        Assert.IsFalse(reopened.IsLive(Run));
        Assert.AreEqual(Run, Assert.ContainsSingle(reopened.Unfinished).Run);
    }

    [TestMethod]
    public void ARunFinished_IsDropped_AndStaysDropped()
    {
        var journal = RestoreRunJournal.Open(_state);
        journal.Begin(Record(Run));
        journal.Begin(Record(Other));

        journal.Finish(Run);

        Assert.IsNull(journal.Find(Run));
        Assert.IsFalse(journal.IsLive(Run));
        Assert.AreEqual(Other, Assert.ContainsSingle(RestoreRunJournal.Open(_state).Runs).Run);
    }

    [TestMethod]
    public void ARunBegunAgainUnderItsName_IsOneRun_AsLastAsked()
    {
        // A quarantined run resumed lands in its own folder under its own
        // name, so it is the same run, begun again.
        var journal = RestoreRunJournal.Open(_state);
        journal.Begin(Record(Run));
        journal.Leave(Run);

        journal.Begin(Record(Run) with { Existing = "overwrite", BeganAt = 2_000 });

        var run = Assert.ContainsSingle(journal.Runs);
        Assert.AreEqual("overwrite", run.Existing);
        Assert.AreEqual(2_000UL, run.BeganAt);
        Assert.IsTrue(journal.IsLive(Run));
    }

    [TestMethod]
    public void ARunThatContinuesAnother_UnderANameOfItsOwn_TakesItsPlace()
    {
        // A run in place resumed names itself, as its receipt does (ADR-0110),
        // so what is left is the new run's, and the one it continues is gone.
        var journal = RestoreRunJournal.Open(_state);
        journal.Begin(Record(Run) with { InPlace = true });
        journal.Leave(Run);

        journal.Begin(Record(Other) with { InPlace = true }, continues: Run);

        Assert.AreEqual(Other, Assert.ContainsSingle(RestoreRunJournal.Open(_state).Runs).Run);
    }

    [TestMethod]
    public void ARunLetGo_IsDropped_ButNotOneUnderWay()
    {
        var journal = RestoreRunJournal.Open(_state);
        journal.Begin(Record(Run));
        journal.Leave(Run);
        journal.Begin(Record(Other));

        Assert.IsTrue(journal.Forget(Run));
        Assert.IsFalse(journal.Forget(Other), "a run under way is the service's to finish");
        Assert.IsFalse(journal.Forget("0000000000000000"), "a run never begun is not on record to let go");

        Assert.AreEqual(Other, Assert.ContainsSingle(RestoreRunJournal.Open(_state).Runs).Run);
    }

    [TestMethod]
    public void AJournalThatWillNotRead_IsSetAside_AndRecordsAfresh()
    {
        // Sacrificial, like everything beside it: a run it forgot is still
        // resumed by its folder's name (ADR-0109).
        File.WriteAllText(FilePath, "{ not json");

        var journal = RestoreRunJournal.Open(_state);

        Assert.IsEmpty(journal.Runs);
        Assert.IsTrue(File.Exists(FilePath + ".corrupt"));
        journal.Begin(Record(Run));
        Assert.ContainsSingle(RestoreRunJournal.Open(_state).Runs);
    }

    [TestMethod]
    public void AJournalANewerBuildWrote_IsSetAside_NotMisread()
    {
        File.WriteAllText(FilePath, """{"schema_version":99,"runs":[]}""");

        Assert.IsEmpty(RestoreRunJournal.Open(_state).Runs);
        Assert.IsTrue(File.Exists(FilePath + ".corrupt"));
    }

    private static RestoreRunRecord Record(string run) => new()
    {
        Run = run,
        SnapshotId = new string('e', 32),
        OutputDirectory = "/srv/restored",
        BeganAt = 1_000,
    };
}

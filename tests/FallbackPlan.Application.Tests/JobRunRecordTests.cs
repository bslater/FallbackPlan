using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The run-statistics block on a job's journal row (ADR-0027 §2, ADR-0050).
/// The live progress stream forgets its numbers the moment a job settles —
/// deliberately, a finished job must not replay as live — so the journal row
/// is the only place a run's shape can survive: how many files it did, reused
/// and failed, and how many bytes it read and stored. The block is additive:
/// a row written before it existed reads back with none, never as an error.
/// </summary>
[TestClass]
public sealed class JobRunRecordTests
{
    private string _directory = null!;

    private string JournalPath => Path.Combine(_directory, "jobs.json");

    [TestInitialize]
    public void Initialize() =>
        _directory = Directory.CreateTempSubdirectory("fp-run-record-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private static JobRunStats SampleStats => new()
    {
        FilesSeen = 120,
        FilesDone = 118,
        FilesReused = 100,
        FilesFailed = 2,
        BytesSeen = 4_096_000,
        BytesStored = 512_000,
        TotalFiles = 120,
        TotalBytes = 4_096_000,
    };

    /// <summary>
    /// The stats ride the terminal transition and survive a reopen — the whole
    /// point, since the process that knew them is allowed to die.
    /// </summary>
    [TestMethod]
    public void Transition_WithStats_PersistsThemOnTheRow()
    {
        var store = JobStateStore.Open(_directory);
        var job = store.Begin("set-1", 1_000);
        store.Transition(job.Id, JobState.Complete, 2_000, detail: "120 file(s)", stats: SampleStats);

        var reopened = Assert.ContainsSingle(JobStateStore.Open(_directory).Jobs);
        Assert.IsNotNull(reopened.Stats, "the terminal numbers were not persisted — the run's shape died with the process");
        Assert.AreEqual(118, reopened.Stats.FilesDone);
        Assert.AreEqual(100, reopened.Stats.FilesReused);
        Assert.AreEqual(2, reopened.Stats.FilesFailed);
        Assert.AreEqual(4_096_000, reopened.Stats.BytesSeen);
        Assert.AreEqual(512_000, reopened.Stats.BytesStored);
        Assert.AreEqual(120, reopened.Stats.TotalFiles);
    }

    /// <summary>
    /// Null preserves, exactly as it does for detail and snapshot id: a later
    /// bookkeeping transition must not erase the numbers the terminal one
    /// recorded.
    /// </summary>
    [TestMethod]
    public void Transition_NullStats_PreservesThePriorStats()
    {
        var store = JobStateStore.Open(_directory);
        var job = store.Begin("set-1", 1_000);
        store.Transition(job.Id, JobState.CompletedWithFailures, 2_000, stats: SampleStats);
        store.Transition(job.Id, JobState.CompletedWithFailures, 3_000, detail: "amended detail");

        var row = Assert.ContainsSingle(store.Jobs);
        Assert.IsNotNull(row.Stats);
        Assert.AreEqual(2, row.Stats.FilesFailed);
    }

    /// <summary>
    /// A journal written before the block existed reads back whole, with no
    /// stats — additive means an old file is never the corrupt-and-restart
    /// case.
    /// </summary>
    [TestMethod]
    public void Open_ARowWrittenBeforeStatsExisted_ReadsWithNullStats()
    {
        File.WriteAllText(
            JournalPath,
            """
            [
              {
                "id": "0123456789abcdef0123456789abcdef",
                "backup_set_id": "set-1",
                "state": "Complete",
                "started_at": 1000,
                "updated_at": 2000,
                "snapshot_id": null,
                "detail": "8 file(s), 8 unchanged"
              }
            ]
            """);

        var row = Assert.ContainsSingle(JobStateStore.Open(_directory).Jobs);
        Assert.AreEqual(JobState.Complete, row.State);
        Assert.IsNull(row.Stats);
    }
}

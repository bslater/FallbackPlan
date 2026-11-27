using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The job journal's file as its writers and a second process meet it
/// (ADR-0027 §2). <c>status</c> given a repository opens the journal without
/// the writer role, so a running service may be replacing <c>jobs.json</c>
/// under it; inside the service the scheduler and the queue's workers settle
/// their jobs at once, and every one of those settlements must reach the
/// file. Establishes the concurrent half of FR-SVC-018's terminal
/// transitions.
/// </summary>
[TestClass]
public sealed class JobStateStoreTests
{
    private const int Writers = 8;

    private const int Rounds = 3;

    private static readonly JobState[] Steps =
        [JobState.Pending, JobState.Scanning, JobState.Reading, JobState.FailedPermanent];

    private string _directory = null!;

    [TestInitialize]
    public void Initialize() =>
        _directory = Directory.CreateTempSubdirectory("fp-job-journal-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    [FallbackPlan.TestSupport.PlatformCondition(FallbackPlan.TestSupport.TestPlatforms.Windows,
        "a rename holds the file it renamed open for deletion until it returns, and only Windows refuses "
        + "a reader that does not share deletion with that handle")]
    public void Open_WhileAWriteHoldsTheJournalForDeletion_StillReadsIt()
    {
        // A replace renames the new journal into place and holds it open for
        // deletion until the rename returns, and an open in that moment must
        // still read the old rows or the new ones rather than fail the
        // command that asked.
        //
        // The handle below is that moment held still, as in AtomicFileTests;
        // delete-on-close is how .NET asks for deletion access. The first
        // assertion is the control: a read that does not share deletion is
        // refused under that handle, so the second one proves something.
        var job = JobStateStore.Open(_directory).Begin("set-1", 1_000);
        var path = Path.Combine(_directory, "jobs.json");

        using (File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose))
        {
            Assert.Throws<IOException>(
                () => File.ReadAllText(path),
                "the held handle must refuse a reader that does not share deletion, or this test proves nothing");
            Assert.AreEqual(job.Id, Assert.ContainsSingle(JobStateStore.Open(_directory).Jobs).Id);
        }
    }

    [TestMethod]
    public async Task BeginAndTransition_FromSeveralThreadsAtOnce_EachReachTheJournalOnDisk()
    {
        // A pass settles a set it cannot schedule on its own thread while the
        // queue's workers settle theirs, and each write replaces the file with
        // the whole list. Two writes that land in the opposite order to the
        // changes they carry leave the older list as the file: a job the pass
        // reported failed reads back Pending.
        //
        // The writers have threads of their own and take each step together,
        // so every step's writes are in flight at once, and between steps,
        // with every writer stopped, the journal is read back from the disk as
        // the next start and status read it. Nothing here can hold one write
        // between its snapshot and its rename, so the lock's absence shows on
        // most runs rather than every one, and the rounds are what make it
        // most.
        var journal = JobStateStore.Open(_directory);
        var readings = new List<string>();
        using var start = new ManualResetEventSlim();
        using var step = new Barrier(Writers, _ => readings.Add(
            Rows(JobStateStore.Open(_directory).Jobs.Select(job => (job.BackupSetId, job.State)))));
        var writers = Enumerable.Range(0, Writers).Select(writer => Task.Factory.StartNew(
            () =>
            {
                try
                {
                    start.Wait();
                    for (var round = 0; round < Rounds; round++)
                    {
                        var job = journal.Begin($"set-{writer}-{round}", 1_000);
                        step.SignalAndWait();
                        foreach (var state in Steps.Skip(1))
                        {
                            journal.Transition(job.Id, state, 2_000);
                            step.SignalAndWait();
                        }
                    }
                }
                catch
                {
                    // The others are waiting at the barrier for this one.
                    step.RemoveParticipant();
                    throw;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToList();
        start.Set();
        await Task.WhenAll(writers);

        Assert.HasCount(Rounds * Steps.Length, readings);
        for (var reading = 0; reading < readings.Count; reading++)
        {
            var (round, state) = (reading / Steps.Length, Steps[reading % Steps.Length]);
            Assert.AreEqual(
                Rows(
                    from writer in Enumerable.Range(0, Writers)
                    from earlier in Enumerable.Range(0, round + 1)
                    select ($"set-{writer}-{earlier}", earlier < round ? JobState.FailedPermanent : state)),
                readings[reading],
                $"the journal on disk once every writer's job of round {round} was {state}");
        }
    }

    private static string Rows(IEnumerable<(string Set, JobState State)> rows) =>
        string.Join(", ", rows.Select(row => $"{row.Set} {row.State}").Order(StringComparer.Ordinal));
}

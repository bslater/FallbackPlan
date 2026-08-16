using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// What the job journal does with a backup that committed a snapshot without
/// capturing everything (ADR-0027 §1, §2).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JobState.CompletedWithFailures"/> exists so a caller can tell a
/// partial backup from a clean one without reading English. Adding a terminal
/// state is cheap; teaching every place that <em>enumerates</em> terminal
/// states about it is the part that gets missed, and each omission fails
/// silently in its own way. This class pins the two that live here.
/// </para>
/// <para>
/// The live-lock is the one worth stating twice. <see cref="JobStateStore.LastCompleted"/>
/// is the schedule anchor. If a partial run does not count as the set's last
/// backup, the set looks as though it has never run — so it backs up on every
/// scheduler pass, for ever, on a set with one permanently unreadable file.
/// </para>
/// </remarks>
[TestClass]
public sealed class PartialCaptureJournalTests
{
    private string _directory = null!;

    private string JournalPath => Path.Combine(_directory, "jobs.json");

    [TestInitialize]
    public void Initialize() =>
        _directory = Directory.CreateTempSubdirectory("fp-partial-journal-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private static JobRecord Finish(JobStateStore store, JobState state, ulong at)
    {
        var job = store.Begin("set-1", at);
        return store.Transition(job.Id, state, at, snapshotId: new string('a', 64));
    }

    /// <summary>
    /// The anchor. A partial run committed a snapshot and is genuinely the
    /// set's most recent backup.
    /// </summary>
    [TestMethod]
    public void APartialRun_IsTheSchedulesAnchor()
    {
        var store = JobStateStore.Open(_directory);
        var partial = Finish(store, JobState.CompletedWithFailures, 1_755_000_000_000);

        var anchor = store.LastCompleted("set-1");

        Assert.IsNotNull(anchor, "a partial backup did not anchor the schedule — the set will run every pass");
        Assert.AreEqual(partial.Id, anchor.Id);
    }

    /// <summary>
    /// And it anchors even when a clean run precedes it: the anchor is the
    /// latest committed run, not the latest flawless one.
    /// </summary>
    [TestMethod]
    public void APartialRunAfterACleanOne_BecomesTheAnchor()
    {
        var store = JobStateStore.Open(_directory);
        Finish(store, JobState.Complete, 1_755_000_000_000);
        var partial = Finish(store, JobState.CompletedWithFailures, 1_755_003_600_000);

        Assert.AreEqual(partial.Id, store.LastCompleted("set-1")!.Id);
    }

    /// <summary>
    /// A run that committed nothing is still not an anchor. Stated so the two
    /// tests above read as "a snapshot was committed" rather than as "anything
    /// terminal counts".
    /// </summary>
    [TestMethod]
    [DataRow(JobState.FailedPermanent)]
    [DataRow(JobState.FailedRecoverable)]
    [DataRow(JobState.Cancelled)]
    [DataRow(JobState.Paused)]
    public void ARunThatCommittedNothing_IsNotTheAnchor(JobState state)
    {
        var store = JobStateStore.Open(_directory);
        Finish(store, state, 1_755_000_000_000);

        Assert.IsNull(store.LastCompleted("set-1"), $"{state} was treated as a completed backup");
    }

    /// <summary>The predicate the anchor rests on, asserted over the whole vocabulary.</summary>
    [TestMethod]
    public void OnlyTheTwoCommittedStates_CountAsCommitted()
    {
        foreach (var state in Enum.GetValues<JobState>())
        {
            var expected = state is JobState.Complete or JobState.CompletedWithFailures;
            Assert.AreEqual(expected, JobStateStore.IsCommitted(state), $"{state}");
        }
    }

    /// <summary>
    /// The anchor comes back in the operator's wall-clock frame, which is what
    /// <see cref="Schedule.IsDue"/> requires and what nothing in its signature
    /// can enforce.
    /// </summary>
    /// <remarks>
    /// The stored value is Unix milliseconds — an instant with no frame. Every
    /// caller used to convert it with
    /// <c>DateTimeOffset.FromUnixTimeMilliseconds</c>, which carries offset
    /// <c>+00:00</c>, and hand it to a comparison whose other side came from
    /// <c>DateTimeOffset.Now</c>. That mismatch is RN-F1.
    /// </remarks>
    [TestMethod]
    public void TheScheduleAnchor_ComesBackInTheLocalFrame()
    {
        const ulong At = 1_755_000_000_000;
        var store = JobStateStore.Open(_directory);
        Finish(store, JobState.Complete, At);

        var anchor = store.ScheduleAnchor("set-1");

        Assert.IsNotNull(anchor);

        // Same instant, expressed in the frame the scheduler compares against.
        Assert.AreEqual(At, (ulong)anchor.Value.ToUnixTimeMilliseconds());
        Assert.AreEqual(
            TimeZoneInfo.Local.GetUtcOffset(anchor.Value),
            anchor.Value.Offset,
            "the anchor is not in the operator's frame, so a daily comparison would read the wrong wall clock");
    }

    /// <summary>A set that has never completed a run has no anchor.</summary>
    [TestMethod]
    public void ASetThatHasNeverCompleted_HasNoAnchor()
    {
        var store = JobStateStore.Open(_directory);
        Finish(store, JobState.FailedPermanent, 1_755_000_000_000);

        Assert.IsNull(store.ScheduleAnchor("set-1"));
        Assert.IsNull(store.ScheduleAnchor("no-such-set"));
    }

    /// <summary>
    /// A journal naming a state this build does not know loads anyway, with
    /// the unknown run read as recoverable. Without this a downgrade discards
    /// the whole journal — and with it every set's anchor, so every set
    /// becomes due at once.
    /// </summary>
    [TestMethod]
    public void AJournalNamingAnUnknownState_LoadsRatherThanBeingSetAside()
    {
        File.WriteAllText(JournalPath, """
            [
              {
                "id": "0123456789abcdef0123456789abcdef",
                "backup_set_id": "set-1",
                "state": "SomethingAFutureBuildInvented",
                "started_at": 1755000000000,
                "updated_at": 1755000000000
              }
            ]
            """);

        var store = JobStateStore.Open(_directory);
        var job = Assert.ContainsSingle(store.Jobs);

        Assert.AreEqual(JobState.FailedRecoverable, job.State, "an unknown state should read as one the service retries");
        Assert.IsFalse(File.Exists(JournalPath + ".corrupt"), "the journal was set aside instead of being read");
    }

    /// <summary>
    /// The tolerance is for unknown <em>values</em>, not for a wrecked file.
    /// A journal that is not JSON is still sacrificial (ADR-0027 §2).
    /// </summary>
    [TestMethod]
    public void AJournalThatIsNotJson_IsStillSetAside()
    {
        File.WriteAllText(JournalPath, "this is not a journal");

        Assert.IsEmpty(JobStateStore.Open(_directory).Jobs);
        Assert.IsTrue(File.Exists(JournalPath + ".corrupt"), "the wreck was not kept for diagnosis");
    }

    /// <summary>
    /// And the new state round-trips by name, so the journal stays readable by
    /// eye — which is what it is for.
    /// </summary>
    [TestMethod]
    public void ThePartialStateRoundTripsByName()
    {
        var store = JobStateStore.Open(_directory);
        Finish(store, JobState.CompletedWithFailures, 1_755_000_000_000);

        Assert.Contains("CompletedWithFailures", File.ReadAllText(JournalPath), StringComparison.Ordinal);
        Assert.AreEqual(
            JobState.CompletedWithFailures,
            Assert.ContainsSingle(JobStateStore.Open(_directory).Jobs).State);
    }
}

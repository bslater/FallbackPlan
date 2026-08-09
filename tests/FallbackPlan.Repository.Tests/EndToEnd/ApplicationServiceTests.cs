using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The push-2 Application layer (ADR-0027): schedule arithmetic as pure
/// functions with missed runs coalescing to one, the sacrificial job-state
/// journal, and status derivation with the never-merge rules.
/// </summary>
[TestClass]
public sealed class ApplicationServiceTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-app-tests", Guid.NewGuid().ToString("n"));

    public ApplicationServiceTests() => Directory.CreateDirectory(_stateDirectory);

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 4, hour, minute, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("every 4h")]
    [DataRow("every 30m")]
    [DataRow("every 1d")]
    [DataRow("daily at 02:30")]
    public void Valid_schedules_parse(string text)
    {
        Assert.IsTrue(Schedule.TryParse(text, out var schedule, out var defect), defect);
        Assert.AreEqual(text, schedule!.Text);
    }

    [TestMethod]
    [DataRow("hourly")]
    [DataRow("every 0h")]
    [DataRow("every 4x")]
    [DataRow("daily at 25:00")]
    [DataRow("daily at noon")]
    public void Invalid_schedules_are_refused_with_the_defect_named(string text)
    {
        Assert.IsFalse(Schedule.TryParse(text, out _, out var defect));
        Assert.Contains(text, defect!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void An_interval_schedule_is_due_once_per_interval_and_missed_runs_coalesce()
    {
        Assert.IsTrue(Schedule.TryParse("every 4h", out var schedule, out _));

        // Never run: due.
        Assert.IsTrue(schedule!.IsDue(lastCompleted: null, At(10)));

        // Ran at 08:00 — not due at 10:00, due at 12:00.
        Assert.IsFalse(schedule.IsDue(At(8), At(10)));
        Assert.IsTrue(schedule.IsDue(At(8), At(12)));

        // The Agent slept through five intervals: IsDue is a boolean, so
        // the answer at wake-up is "one run", never a backlog (ADR-0027 §1).
        Assert.IsTrue(schedule.IsDue(At(8), At(8).AddDays(2)));
        var completed = At(8).AddDays(2).AddMinutes(20);
        Assert.IsFalse(schedule.IsDue(completed, completed.AddHours(1)));
    }

    [TestMethod]
    public void A_daily_schedule_runs_once_per_calendar_day_at_its_time()
    {
        Assert.IsTrue(Schedule.TryParse("daily at 02:30", out var schedule, out _));

        // Before today's occurrence: not due (last ran yesterday).
        Assert.IsFalse(schedule!.IsDue(At(2, 45).AddDays(-1), At(1)));

        // After today's occurrence: due exactly once.
        Assert.IsTrue(schedule.IsDue(At(2, 45).AddDays(-1), At(3)));
        Assert.IsFalse(schedule.IsDue(At(3, 10), At(9)));

        // Next-run display: before the time, today; after, tomorrow.
        Assert.AreEqual(At(2, 30), schedule.NextRun(At(2, 45).AddDays(-1), At(1)).Add(TimeSpan.Zero));
        Assert.AreEqual(At(2, 30).AddDays(1).DateTime, schedule.NextRun(At(3, 10), At(9)).DateTime);
    }

    /// <summary>
    /// The offsets a schedule answer must be identical in. The machine's own
    /// timezone is deliberately NOT one of the inputs: it is the thing that
    /// must not matter (NFR-TIME-001). Making the offset an explicit test
    /// dimension is what stops a UTC build agent from masking the defect —
    /// this repository shipped exactly that bug, correct on UTC and a day
    /// wrong everywhere else, and green CI never saw it.
    /// </summary>
    public static IEnumerable<object[]> ScheduleOffsets =>
    [
        [0, 0],  // UTC — the offset a build agent usually runs in
        [10, 0],  // UTC+10, no DST (Brisbane) — where the defect surfaced
        [-7, 0],  // UTC-07
        [5, 45],  // UTC+05:45 (Kathmandu) — a non-hour offset
        [13, 0],  // UTC+13 — past the date line, so "today" differs from UTC's
        [-11, 0],  // UTC-11 — the other extreme
    ];

    [TestMethod]
    [DynamicData(nameof(ScheduleOffsets))]
    public void Daily_schedule_answers_do_not_depend_on_the_machine_timezone(int offsetHours, int offsetMinutes)
    {
        // IsDue is a pure function of its arguments, so the same wall-clock
        // scenario expressed in ANY offset gives the answers asserted in UTC
        // above. Each case runs the full daily contract in one offset.
        var offset = new TimeSpan(offsetHours, offsetMinutes, 0);
        Assert.IsTrue(Schedule.TryParse("daily at 02:30", out var schedule, out _));

        DateTimeOffset Local(int day, int hour, int minute = 0) =>
            new(2026, 8, day, hour, minute, 0, offset);

        // Before today's occurrence, last run yesterday: not due.
        Assert.IsFalse(schedule!.IsDue(Local(3, 2, 45), Local(4, 1)));

        // After it: due exactly once, then not again the same day.
        Assert.IsTrue(schedule.IsDue(Local(3, 2, 45), Local(4, 3)));
        Assert.IsFalse(schedule.IsDue(Local(4, 3, 10), Local(4, 9)));

        // Next-run display, in the caller's own offset.
        Assert.AreEqual(Local(4, 2, 30), schedule.NextRun(Local(3, 2, 45), Local(4, 1)));
        Assert.AreEqual(Local(5, 2, 30), schedule.NextRun(Local(4, 3, 10), Local(4, 9)));

        // Mixed offsets: the anchor arrives in UTC, as a journal timestamp
        // does, while now carries the Agent's local offset. The comparison
        // must be by instant, not by the digits on either clock.
        Assert.IsFalse(schedule.IsDue(Local(4, 3, 10).ToUniversalTime(), Local(4, 9)));
        Assert.IsTrue(schedule.IsDue(Local(3, 2, 45).ToUniversalTime(), Local(4, 3)));
    }

    [TestMethod]
    [DynamicData(nameof(ScheduleOffsets))]
    public void Interval_schedule_answers_do_not_depend_on_the_machine_timezone(int offsetHours, int offsetMinutes)
    {
        var offset = new TimeSpan(offsetHours, offsetMinutes, 0);
        Assert.IsTrue(Schedule.TryParse("every 4h", out var schedule, out _));

        DateTimeOffset Local(int day, int hour, int minute = 0) =>
            new(2026, 8, day, hour, minute, 0, offset);

        Assert.IsFalse(schedule!.IsDue(Local(4, 8), Local(4, 9)));
        Assert.IsTrue(schedule.IsDue(Local(4, 8), Local(4, 12)));

        // An anchor in UTC against a local now — the elapsed interval is an
        // absolute quantity, so the offsets must cancel.
        Assert.IsFalse(schedule.IsDue(Local(4, 8).ToUniversalTime(), Local(4, 9)));
        Assert.IsTrue(schedule.IsDue(Local(4, 8).ToUniversalTime(), Local(4, 12)));

        Assert.AreEqual(Local(4, 12), schedule.NextRun(Local(4, 8), Local(4, 9)));
    }

    [TestMethod]
    public void The_job_journal_records_transitions_and_anchors_the_schedule()
    {
        var store = JobStateStore.Open(_stateDirectory);
        var job = store.Begin("set-1", 1_000);
        Assert.AreEqual(JobState.Pending, job.State);

        store.Transition(job.Id, JobState.Scanning, 1_100);
        store.Transition(job.Id, JobState.Publishing, 1_200);
        store.Transition(job.Id, JobState.Complete, 1_300, snapshotId: "aa");

        var reloaded = JobStateStore.Open(_stateDirectory);
        var anchor = reloaded.LastCompleted("set-1");
        Assert.IsNotNull(anchor);
        Assert.AreEqual(1_300ul, anchor!.UpdatedAt);
        Assert.AreEqual("aa", anchor.SnapshotId);
        Assert.IsNull(reloaded.LastCompleted("set-2"));
    }

    [TestMethod]
    public void Recoverable_and_permanent_failures_stay_distinct()
    {
        var store = JobStateStore.Open(_stateDirectory);
        var recoverable = store.Begin("set-1", 1_000);
        store.Transition(recoverable.Id, JobState.FailedRecoverable, 1_100, "destination offline");
        var permanent = store.Begin("set-1", 2_000);
        store.Transition(permanent.Id, JobState.FailedPermanent, 2_100, "invalid rules");

        // The Agent retries exactly the recoverable one (10 §3).
        var retry = Assert.ContainsSingle(store.RecoverableFailures("set-1"));
        Assert.AreEqual(recoverable.Id, retry.Id);
    }

    [TestMethod]
    public void A_corrupt_job_journal_is_set_aside_and_never_fatal()
    {
        File.WriteAllText(Path.Combine(_stateDirectory, "jobs.json"), "{ not json");

        // Sacrificial by design (ADR-0027 §2) — unlike state.json, whose
        // corruption is refused loudly because identity is not guessable.
        var store = JobStateStore.Open(_stateDirectory);
        Assert.IsEmpty(store.Jobs);
        Assert.IsTrue(File.Exists(Path.Combine(_stateDirectory, "jobs.json.corrupt")));
    }

    private static StatusInputs HealthyInputs() => new()
    {
        LatestSnapshotAt = 1_722_600_000_000,
        LatestCaptureStatus = 1,
        DestinationReachable = true,
        SameFailureDomain = false,
        DamageFindings = 0,
        RequiredObjectsMissing = false,
    };

    [TestMethod]
    public void A_same_device_store_is_captured_and_never_protected()
    {
        // PT-8: the most common consumer configuration — repository on the
        // same disk as the source — is real protection against mistakes and
        // none against losing the disk. Never merged with `protected`.
        var status = StatusDeriver.Derive(HealthyInputs() with { SameFailureDomain = true });
        Assert.AreEqual(ProtectionState.Captured, status.State);
        Assert.Contains(warning => warning.Contains("failure domain", StringComparison.Ordinal), status.Warnings);

        Assert.AreEqual(ProtectionState.Protected, StatusDeriver.Derive(HealthyInputs()).State);
    }

    [TestMethod]
    public void Degraded_and_unrecoverable_are_never_merged()
    {
        var degraded = StatusDeriver.Derive(HealthyInputs() with { DestinationReachable = false });
        var unrecoverable = StatusDeriver.Derive(HealthyInputs() with { RequiredObjectsMissing = true });

        Assert.AreEqual(ProtectionState.Degraded, degraded.State);
        Assert.AreEqual(ProtectionState.Unrecoverable, unrecoverable.State);
        Assert.AreNotEqual(degraded.State, unrecoverable.State);

        // Unrecoverable outranks everything else that is also true.
        var both = StatusDeriver.Derive(HealthyInputs() with
        {
            RequiredObjectsMissing = true,
            DestinationReachable = false,
            DamageFindings = 3,
        });
        Assert.AreEqual(ProtectionState.Unrecoverable, both.State);
    }

    [TestMethod]
    public void Verified_always_carries_coverage_and_age_never_a_bare_tick()
    {
        var detail = new VerificationDetail(Coverage: 0.35, VerifiedAtUnixMilliseconds: 1_722_500_000_000);
        var verified = StatusDeriver.Derive(HealthyInputs() with { LastVerification = detail });

        Assert.AreEqual(ProtectionState.Verified, verified.State);
        Assert.AreEqual(detail, verified.Verification);

        // Without a verification record the state is Protected, not an
        // unverified "verified" (10 §1.2).
        Assert.AreEqual(ProtectionState.Protected, StatusDeriver.Derive(HealthyInputs()).State);
    }

    [TestMethod]
    public void A_partial_snapshot_is_a_warning_and_never_silently_green()
    {
        var status = StatusDeriver.Derive(HealthyInputs() with { LatestCaptureStatus = 2 });
        Assert.Contains(warning => warning.Contains("PARTIAL", StringComparison.Ordinal), status.Warnings);
    }

    [TestMethod]
    public void No_snapshot_means_never_backed_up_not_an_error()
    {
        var status = StatusDeriver.Derive(HealthyInputs() with { LatestSnapshotAt = null });
        Assert.AreEqual(ProtectionState.NeverBackedUp, status.State);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}

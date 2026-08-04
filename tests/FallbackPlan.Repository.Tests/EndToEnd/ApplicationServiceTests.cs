using FallbackPlan.Application;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The push-2 Application layer (ADR-0027): schedule arithmetic as pure
/// functions with missed runs coalescing to one, the sacrificial job-state
/// journal, and status derivation with the never-merge rules.
/// </summary>
public sealed class ApplicationServiceTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-app-tests", Guid.NewGuid().ToString("n"));

    public ApplicationServiceTests() => Directory.CreateDirectory(_stateDirectory);

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 4, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("every 4h")]
    [InlineData("every 30m")]
    [InlineData("every 1d")]
    [InlineData("daily at 02:30")]
    public void Valid_schedules_parse(string text)
    {
        Assert.True(Schedule.TryParse(text, out var schedule, out var defect), defect);
        Assert.Equal(text, schedule!.Text);
    }

    [Theory]
    [InlineData("hourly")]
    [InlineData("every 0h")]
    [InlineData("every 4x")]
    [InlineData("daily at 25:00")]
    [InlineData("daily at noon")]
    public void Invalid_schedules_are_refused_with_the_defect_named(string text)
    {
        Assert.False(Schedule.TryParse(text, out _, out var defect));
        Assert.Contains(text, defect!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interval_schedule_is_due_once_per_interval_and_missed_runs_coalesce()
    {
        Assert.True(Schedule.TryParse("every 4h", out var schedule, out _));

        // Never run: due.
        Assert.True(schedule!.IsDue(lastCompleted: null, At(10)));

        // Ran at 08:00 — not due at 10:00, due at 12:00.
        Assert.False(schedule.IsDue(At(8), At(10)));
        Assert.True(schedule.IsDue(At(8), At(12)));

        // The Agent slept through five intervals: IsDue is a boolean, so
        // the answer at wake-up is "one run", never a backlog (ADR-0027 §1).
        Assert.True(schedule.IsDue(At(8), At(8).AddDays(2)));
        var completed = At(8).AddDays(2).AddMinutes(20);
        Assert.False(schedule.IsDue(completed, completed.AddHours(1)));
    }

    [Fact]
    public void A_daily_schedule_runs_once_per_calendar_day_at_its_time()
    {
        Assert.True(Schedule.TryParse("daily at 02:30", out var schedule, out _));

        // Before today's occurrence: not due (last ran yesterday).
        Assert.False(schedule!.IsDue(At(2, 45).AddDays(-1), At(1)));

        // After today's occurrence: due exactly once.
        Assert.True(schedule.IsDue(At(2, 45).AddDays(-1), At(3)));
        Assert.False(schedule.IsDue(At(3, 10), At(9)));

        // Next-run display: before the time, today; after, tomorrow.
        Assert.Equal(At(2, 30), schedule.NextRun(At(2, 45).AddDays(-1), At(1)).Add(TimeSpan.Zero));
        Assert.Equal(At(2, 30).AddDays(1).DateTime, schedule.NextRun(At(3, 10), At(9)).DateTime);
    }

    [Fact]
    public void Daily_schedule_answers_do_not_depend_on_the_machine_timezone()
    {
        // NFR-TIME-001: IsDue is a pure function of its arguments. The same
        // instants expressed in any offset — here UTC+10, chosen because a
        // UTC machine masks the difference — must give the same answers as
        // the UTC assertions above.
        Assert.True(Schedule.TryParse("daily at 02:30", out var schedule, out _));

        static DateTimeOffset Local(int day, int hour, int minute = 0) =>
            new(2026, 8, day, hour, minute, 0, TimeSpan.FromHours(10));

        Assert.False(schedule!.IsDue(Local(3, 2, 45), Local(4, 1)));
        Assert.True(schedule.IsDue(Local(3, 2, 45), Local(4, 3)));
        Assert.False(schedule.IsDue(Local(4, 3, 10), Local(4, 9)));

        // Mixed offsets: the anchor arrives in UTC (as a journal timestamp
        // would), now in the Agent's local offset. 2026-08-03T16:45Z is
        // 02:45 on the 4th in UTC+10 — today's occurrence already ran.
        Assert.False(schedule.IsDue(
            new DateTimeOffset(2026, 8, 3, 16, 45, 0, TimeSpan.Zero), Local(4, 9)));

        Assert.Equal(Local(4, 2, 30), schedule.NextRun(Local(3, 2, 45), Local(4, 1)));
        Assert.Equal(Local(5, 2, 30), schedule.NextRun(Local(4, 3, 10), Local(4, 9)));
    }

    [Fact]
    public void The_job_journal_records_transitions_and_anchors_the_schedule()
    {
        var store = JobStateStore.Open(_stateDirectory);
        var job = store.Begin("set-1", 1_000);
        Assert.Equal(JobState.Pending, job.State);

        store.Transition(job.Id, JobState.Scanning, 1_100);
        store.Transition(job.Id, JobState.Publishing, 1_200);
        store.Transition(job.Id, JobState.Complete, 1_300, snapshotId: "aa");

        var reloaded = JobStateStore.Open(_stateDirectory);
        var anchor = reloaded.LastCompleted("set-1");
        Assert.NotNull(anchor);
        Assert.Equal(1_300ul, anchor!.UpdatedAt);
        Assert.Equal("aa", anchor.SnapshotId);
        Assert.Null(reloaded.LastCompleted("set-2"));
    }

    [Fact]
    public void Recoverable_and_permanent_failures_stay_distinct()
    {
        var store = JobStateStore.Open(_stateDirectory);
        var recoverable = store.Begin("set-1", 1_000);
        store.Transition(recoverable.Id, JobState.FailedRecoverable, 1_100, "destination offline");
        var permanent = store.Begin("set-1", 2_000);
        store.Transition(permanent.Id, JobState.FailedPermanent, 2_100, "invalid rules");

        // The Agent retries exactly the recoverable one (10 §3).
        var retry = Assert.Single(store.RecoverableFailures("set-1"));
        Assert.Equal(recoverable.Id, retry.Id);
    }

    [Fact]
    public void A_corrupt_job_journal_is_set_aside_and_never_fatal()
    {
        File.WriteAllText(Path.Combine(_stateDirectory, "jobs.json"), "{ not json");

        // Sacrificial by design (ADR-0027 §2) — unlike state.json, whose
        // corruption is refused loudly because identity is not guessable.
        var store = JobStateStore.Open(_stateDirectory);
        Assert.Empty(store.Jobs);
        Assert.True(File.Exists(Path.Combine(_stateDirectory, "jobs.json.corrupt")));
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

    [Fact]
    public void A_same_device_store_is_captured_and_never_protected()
    {
        // PT-8: the most common consumer configuration — repository on the
        // same disk as the source — is real protection against mistakes and
        // none against losing the disk. Never merged with `protected`.
        var status = StatusDeriver.Derive(HealthyInputs() with { SameFailureDomain = true });
        Assert.Equal(ProtectionState.Captured, status.State);
        Assert.Contains(status.Warnings, warning => warning.Contains("failure domain", StringComparison.Ordinal));

        Assert.Equal(ProtectionState.Protected, StatusDeriver.Derive(HealthyInputs()).State);
    }

    [Fact]
    public void Degraded_and_unrecoverable_are_never_merged()
    {
        var degraded = StatusDeriver.Derive(HealthyInputs() with { DestinationReachable = false });
        var unrecoverable = StatusDeriver.Derive(HealthyInputs() with { RequiredObjectsMissing = true });

        Assert.Equal(ProtectionState.Degraded, degraded.State);
        Assert.Equal(ProtectionState.Unrecoverable, unrecoverable.State);
        Assert.NotEqual(degraded.State, unrecoverable.State);

        // Unrecoverable outranks everything else that is also true.
        var both = StatusDeriver.Derive(HealthyInputs() with
        {
            RequiredObjectsMissing = true,
            DestinationReachable = false,
            DamageFindings = 3,
        });
        Assert.Equal(ProtectionState.Unrecoverable, both.State);
    }

    [Fact]
    public void Verified_always_carries_coverage_and_age_never_a_bare_tick()
    {
        var detail = new VerificationDetail(Coverage: 0.35, VerifiedAtUnixMilliseconds: 1_722_500_000_000);
        var verified = StatusDeriver.Derive(HealthyInputs() with { LastVerification = detail });

        Assert.Equal(ProtectionState.Verified, verified.State);
        Assert.Equal(detail, verified.Verification);

        // Without a verification record the state is Protected, not an
        // unverified "verified" (10 §1.2).
        Assert.Equal(ProtectionState.Protected, StatusDeriver.Derive(HealthyInputs()).State);
    }

    [Fact]
    public void A_partial_snapshot_is_a_warning_and_never_silently_green()
    {
        var status = StatusDeriver.Derive(HealthyInputs() with { LatestCaptureStatus = 2 });
        Assert.Contains(status.Warnings, warning => warning.Contains("PARTIAL", StringComparison.Ordinal));
    }

    [Fact]
    public void No_snapshot_means_never_backed_up_not_an_error()
    {
        var status = StatusDeriver.Derive(HealthyInputs() with { LatestSnapshotAt = null });
        Assert.Equal(ProtectionState.NeverBackedUp, status.State);
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

using FallbackPlan.TestSupport;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// Schedules across the clock boundaries a real machine actually crosses
/// (ADR-0027 §1, NFR-TIME-001).
/// </summary>
/// <remarks>
/// <para>
/// Duplicati shipped "issue with DST changes causing schedule time-of-day to
/// change" (v2.1.0.100) and, separately, "a case where backups could run
/// immediately and ignore the scheduled time" (v2.0.5.106). Both are the same
/// underlying difficulty: a daily schedule is stated in wall-clock terms, and
/// wall clocks are not monotonic. Twice a year an hour does not exist, and
/// twice a year an hour happens twice.
/// </para>
/// <para>
/// The two failure modes are opposite and both matter. A backup that fires
/// twice on the fall-back night is noise. A backup that is <em>skipped</em> on
/// the spring-forward night is a missing day, and a backup skipped every day
/// after it is a machine that quietly stopped being backed up while its status
/// display kept saying "next run 02:30".
/// </para>
/// <para>
/// The clock is stepped the way the agent actually steps it — `Scheduler`
/// passes `DateTimeOffset.Now`, so these tests pass wall-clock instants with
/// the offset the zone really had, taken from tzdata rather than assumed.
/// </para>
/// </remarks>
[TestClass]
public sealed class ScheduleClockBoundaryTests
{
    /// <summary>
    /// Southern-hemisphere on purpose: its transitions fall in the opposite
    /// months to the northern ones, so a test that only ever crossed a March
    /// boundary would not be crossing a boundary at all here.
    /// </summary>
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static Schedule Parse(string text)
    {
        Assert.IsTrue(Schedule.TryParse(text, out var schedule, out var defect), defect);
        return schedule!;
    }

    /// <summary>The instant, rendered as the wall clock a machine in that zone would show.</summary>
    private static DateTimeOffset WallClock(TimeZoneInfo zone, DateTimeOffset instant) =>
        instant.ToOffset(zone.GetUtcOffset(instant));

    /// <summary>
    /// Steps a clock across <paramref name="hours"/> from
    /// <paramref name="fromUtc"/> at one-minute resolution, exactly as the
    /// agent's poll would see it, and records the wall-clock times at which a
    /// run fired. Each firing updates the anchor, as a completed job does.
    /// </summary>
    private static List<DateTimeOffset> Firings(
        Schedule schedule, TimeZoneInfo zone, DateTimeOffset fromUtc, int hours, DateTimeOffset? anchor = null)
    {
        var fired = new List<DateTimeOffset>();

        // The seed anchor arrives from the caller as a plain UTC instant, and
        // `IsDue` requires the operator's wall-clock frame. Normalised here
        // rather than at each caller because getting this wrong is silent —
        // writing this class, a UTC-framed seed produced one spurious firing
        // at the very start of the window and nothing said why.
        anchor = anchor is { } seed ? WallClock(zone, seed) : null;

        for (var minute = 0; minute < hours * 60; minute++)
        {
            var instant = fromUtc.AddMinutes(minute);
            var now = WallClock(zone, instant);

            if (!schedule.IsDue(anchor, now))
            {
                continue;
            }

            fired.Add(now);

            // The agent records completion as Unix milliseconds and reads it
            // back through `JobStateStore.ScheduleAnchor`, which converts to
            // the operator's wall-clock frame — the frame `IsDue` requires.
            // Reconstructed the same way here, in the zone under test rather
            // than the container's, which is UTC and would exercise nothing.
            anchor = WallClock(zone, instant);
        }

        return fired;
    }

    /// <summary>
    /// The ordinary case, stated so the boundary cases have a baseline: a
    /// daily schedule fires once per day and not more.
    /// </summary>
    [TestMethod]
    public void ADailySchedule_OverAnUneventfulWeek_FiresOncePerDay()
    {
        // A week of Sydney winter — no transition anywhere near it.
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var fired = Firings(Parse("daily at 07:05"), Sydney, start, hours: 24 * 7,
            anchor: start.AddMinutes(-1));

        Assert.AreEqual(7, fired.Count, $"fired at: {string.Join(", ", fired)}");
        Assert.IsTrue(fired.TrueForAll(at => at.Hour == 7 && at.Minute == 5), $"fired at: {string.Join(", ", fired)}");
    }

    /// <summary>
    /// Fall back: 02:30 local happens twice on the transition night. It must
    /// produce one backup, not two.
    /// </summary>
    [TestMethod]
    public void ADailySchedule_OnTheNightAnHourRepeats_FiresOnce()
    {
        // Sydney leaves daylight saving at 03:00 AEDT on 5 April 2026, so
        // 02:00-02:59 local occurs twice: once at +11:00 and again at +10:00.
        var start = new DateTimeOffset(2026, 4, 4, 4, 0, 0, TimeSpan.Zero);
        var fired = Firings(Parse("daily at 02:30"), Sydney, start, hours: 24,
            anchor: start.AddMinutes(-1));

        Assert.AreEqual(1, fired.Count, $"the repeated hour fired {fired.Count} times: {string.Join(", ", fired)}");
    }

    /// <summary>
    /// Spring forward: 02:30 local does not exist on the transition night.
    /// The day must still get its backup — skipping it is a missing day, and
    /// skipping it permanently is a machine that stopped backing up.
    /// </summary>
    [TestMethod]
    public void ADailySchedule_OnTheNightAnHourDoesNotExist_StillFiresThatDay()
    {
        // Sydney enters daylight saving at 02:00 AEST on 4 October 2026:
        // the clock jumps to 03:00 and 02:00-02:59 local never happens.
        var start = new DateTimeOffset(2026, 10, 3, 4, 0, 0, TimeSpan.Zero);
        var fired = Firings(Parse("daily at 02:30"), Sydney, start, hours: 24,
            anchor: start.AddMinutes(-1));

        Assert.AreEqual(1, fired.Count, $"the vanished hour fired {fired.Count} times: {string.Join(", ", fired)}");
    }

    /// <summary>
    /// And the day after the vanished hour must recover: a schedule that
    /// silently stops is far worse than one that fires late.
    /// </summary>
    [TestMethod]
    public void ADailySchedule_TheWeekAroundASpringForward_FiresEveryDay()
    {
        var start = new DateTimeOffset(2026, 10, 1, 4, 0, 0, TimeSpan.Zero);
        var fired = Firings(Parse("daily at 02:30"), Sydney, start, hours: 24 * 7,
            anchor: start.AddMinutes(-1));

        Assert.AreEqual(7, fired.Count, $"fired at: {string.Join(", ", fired)}");
    }

    /// <summary>
    /// The northern spring forward, in case anything keys off the sign of the
    /// transition rather than the transition itself.
    /// </summary>
    [TestMethod]
    public void ADailySchedule_AcrossTheNorthernSpringForward_FiresEveryDay()
    {
        // New York springs forward on 8 March 2026.
        var start = new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero);
        var fired = Firings(Parse("daily at 02:30"), NewYork, start, hours: 24 * 4,
            anchor: start.AddMinutes(-1));

        Assert.AreEqual(4, fired.Count, $"fired at: {string.Join(", ", fired)}");
    }

    /// <summary>
    /// The northern fall back — the same defect as the southern one, kept as
    /// its own case so the fix is shown to work in both hemispheres.
    /// </summary>
    [TestMethod]
    public void ADailySchedule_AcrossTheNorthernFallBack_FiresEveryDay()
    {
        // New York falls back on 1 November 2026.
        var start = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var fired = Firings(Parse("daily at 02:30"), NewYork, start, hours: 24 * 4,
            anchor: start.AddMinutes(-1));

        Assert.AreEqual(4, fired.Count, $"fired at: {string.Join(", ", fired)}");
    }

    /// <summary>
    /// An interval schedule is absolute by construction — "every 12h" means
    /// twelve elapsed hours, not a wall-clock position — so a transition must
    /// neither compress nor stretch the gap. Stated because it is the property
    /// that makes the interval form the one to recommend to somebody who cares
    /// more about cadence than about a particular hour.
    /// </summary>
    [TestMethod]
    public void AnIntervalSchedule_AcrossATransition_CountsElapsedTimeNotWallClock()
    {
        var start = new DateTimeOffset(2026, 4, 4, 4, 0, 0, TimeSpan.Zero);
        var fired = Firings(Parse("every 12h"), Sydney, start, hours: 48, anchor: start);

        // The count is a property of the half-open window and not the point;
        // the spacing is. Sydney leaves daylight saving inside this window, so
        // a wall-clock-based interval would show an 11- or 13-hour gap here.
        Assert.IsNotEmpty(fired);
        for (var i = 1; i < fired.Count; i++)
        {
            Assert.AreEqual(
                TimeSpan.FromHours(12),
                fired[i] - fired[i - 1],
                $"gap {i} was not twelve elapsed hours: {string.Join(", ", fired)}");
        }
    }

    /// <summary>
    /// "Backups could run immediately and ignore the scheduled time" — a set
    /// that has just completed must not be due again on the very next poll.
    /// </summary>
    [TestMethod]
    [DataRow("daily at 07:05")]
    [DataRow("every 30m")]
    [DataRow("every 12h")]
    [DataRow("every 7d")]
    public void ASetThatJustCompleted_IsNotImmediatelyDueAgain(string text)
    {
        var schedule = Parse(text);
        var now = new DateTimeOffset(2026, 6, 1, 7, 5, 0, TimeSpan.FromHours(10));

        Assert.IsFalse(schedule.IsDue(now, now), "due again at the instant it completed");
        Assert.IsFalse(schedule.IsDue(now, now.AddSeconds(30)), "due again half a minute after completing");
    }

    /// <summary>
    /// A set that has never run is due immediately — the one case where
    /// running at once is correct, kept distinct from the bug above.
    /// </summary>
    [TestMethod]
    public void ASetThatHasNeverRun_IsDueAtOnce()
    {
        var now = new DateTimeOffset(2026, 6, 1, 3, 17, 0, TimeSpan.FromHours(10));

        Assert.IsTrue(Parse("daily at 07:05").IsDue(lastCompleted: null, now));
        Assert.IsTrue(Parse("every 7d").IsDue(lastCompleted: null, now));
    }

    /// <summary>
    /// Missed occurrences coalesce: an agent off for a week owes one run, not
    /// seven. This is stated in <c>Schedule</c>'s own contract and is the
    /// property that keeps a laptop returning from holiday from starting a
    /// week's worth of backups at once.
    /// </summary>
    [TestMethod]
    public void AnAgentOffForAWeek_OwesExactlyOneRun()
    {
        var schedule = Parse("daily at 07:05");
        var lastCompleted = new DateTimeOffset(2026, 6, 1, 7, 5, 0, TimeSpan.FromHours(10));
        var backAgain = lastCompleted.AddDays(7);

        Assert.IsTrue(schedule.IsDue(lastCompleted, backAgain));

        // One run discharges the whole backlog.
        Assert.IsFalse(schedule.IsDue(backAgain, backAgain.AddMinutes(1)));
    }

    /// <summary>
    /// A clock corrected by months, in either direction, must produce one run
    /// and no exception.
    /// </summary>
    /// <remarks>
    /// Duplicati shipped "a crash that happened in the scheduler if the clock
    /// was changed with more than 3 months" (2017-08-30). A machine whose CMOS
    /// battery died boots in 1970 or 2000 and is corrected by NTP a minute
    /// later, so the jump is not a contrived input — it is what an old laptop
    /// does every time it is switched on.
    /// </remarks>
    [TestMethod]
    [DataRow("daily at 07:05")]
    [DataRow("every 12h")]
    [DataRow("every 7d")]
    public void TheClockJumpsByMonths_TheScheduleFiresOnceAndDoesNotThrow(string text)
    {
        var schedule = Parse(text);
        var completed = new DateTimeOffset(2026, 6, 10, 7, 5, 0, TimeSpan.FromHours(10));

        // Forwards: months of missed occurrences coalesce into one run.
        var jumpedForward = completed.AddMonths(4);
        Assert.IsTrue(schedule.IsDue(completed, jumpedForward));
        Assert.IsFalse(schedule.IsDue(jumpedForward, jumpedForward.AddMinutes(1)), "the backlog did not discharge");

        // Backwards: nothing is due, and nothing throws computing the next run.
        var jumpedBack = completed.AddMonths(-4);
        Assert.IsFalse(schedule.IsDue(completed, jumpedBack), "a months-backwards clock made the schedule storm");
        _ = schedule.NextRun(completed, jumpedBack);
    }

    /// <summary>
    /// The clock going backwards — an NTP correction, or a user fixing a wrong
    /// date — must not make a schedule fire continuously, and must not wedge it
    /// permanently either.
    /// </summary>
    [TestMethod]
    public void TheClockJumpsBackwards_TheScheduleNeitherStormsNorWedges()
    {
        var schedule = Parse("daily at 07:05");
        var completed = new DateTimeOffset(2026, 6, 10, 7, 5, 0, TimeSpan.FromHours(10));

        // The clock is corrected back by three days. Nothing is due, because
        // no occurrence has passed that the last run predates.
        var corrected = completed.AddDays(-3);
        Assert.IsFalse(schedule.IsDue(completed, corrected), "a backwards clock made the schedule storm");

        // And once real time passes the last completion again, it recovers.
        Assert.IsTrue(schedule.IsDue(completed, completed.AddDays(1)), "the schedule wedged after a backwards clock");
    }
}

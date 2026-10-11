using System.Globalization;
using FallbackPlan.Application;
using FallbackPlan.Retention;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A retention rule's day, week and month are the Gregorian calendar's,
/// whatever calendar the process culture defaults to (NFR-PORT-001,
/// architecture 07 §2): a daily rule keeps the newest snapshot of each UTC
/// day, a weekly rule of each ISO 8601 week and a monthly rule of each
/// Gregorian month, and the dry-run report names each bucket in that form.
/// </summary>
/// <remarks>
/// <c>ar-SA</c> defaults to the Umm al-Qura calendar, whose lunar months end
/// on other days than the Gregorian months do, and <c>th-TH</c> to the Thai
/// Buddhist calendar, whose years run 543 ahead. A bucket named in either
/// would group or label snapshots by that calendar. The culture is set on
/// this thread only, because the planner runs on it and starts no other.
/// </remarks>
[TestClass]
public sealed class RetentionCalendarTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 30, 13, 0, 0, TimeSpan.Zero);

    /// <summary>Two cultures whose default calendar is not Gregorian, and two whose is, as controls.</summary>
    private static readonly string[] Cultures = ["ar-SA", "th-TH", "en-US", "tr-TR"];

    [TestMethod]
    public void Select_AMonthlyRule_KeepsTheNewestSnapshotOfEachGregorianMonth_InEveryCulture()
    {
        // One capture a day from September. The newest of each Gregorian
        // month is its last day's. Bucketed by the Umm al-Qura calendar, the
        // same window holds parts of four lunar months, whose newest captures
        // are on 11 September, 11 October, 10 November and 30 November, so
        // the ends of September and October would go.
        var snapshots = Daily(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        const string Expected = """
            20261130: monthly 2026-11
            20261031: monthly 2026-10
            20260930: monthly 2026-09
            """;

        foreach (var culture in Cultures)
        {
            var selection = InCulture(
                culture, () => RetentionPlanner.Select(snapshots, new RetentionConfiguration { KeepMonthly = 3 }, Now));
            Assert.AreEqual(Expected, Kept(selection), culture);
        }
    }

    [TestMethod]
    public void Select_EachRuleNamesItsBucket_AsTheGregorianCalendarDoes_InEveryCulture()
    {
        var snapshots = Daily(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        const string Expected = """
            20261130: daily 2026-11-30, weekly 2026-W49, monthly 2026-11
            20261129: daily 2026-11-29, weekly 2026-W48
            20261122: weekly 2026-W47
            """;

        foreach (var culture in Cultures)
        {
            var selection = InCulture(
                culture,
                () => RetentionPlanner.Select(
                    snapshots, new RetentionConfiguration { KeepDaily = 2, KeepWeekly = 2, KeepMonthly = 1 }, Now));
            Assert.AreEqual(Expected, Kept(selection), culture);
        }
    }

    /// <summary>One capture a day at noon UTC, from <paramref name="first"/> to <see cref="Now"/>.</summary>
    private static List<SnapshotFact> Daily(DateTimeOffset first)
    {
        var snapshots = new List<SnapshotFact>();
        for (var day = first; day <= Now; day = day.AddDays(1))
        {
            snapshots.Add(new SnapshotFact(
                day.ToString("yyyyMMdd", CultureInfo.InvariantCulture).PadRight(64, '0'),
                (ulong)day.ToUnixTimeMilliseconds()));
        }

        return snapshots;
    }

    /// <summary>
    /// The kept snapshots, newest first, each as the day it was captured and
    /// its reasons, a line apiece as the expected text is checked out.
    /// </summary>
    private static string Kept(RetentionSelection selection) =>
        string.Join(
            '\n',
            selection.Keep
                .OrderByDescending(keep => keep.Snapshot.SnapshotId, StringComparer.Ordinal)
                .Select(keep => $"{keep.Snapshot.SnapshotId[..8]}: {string.Join(", ", keep.Reasons)}"));

    private static T InCulture<T>(string name, Func<T> select)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try
        {
            return select();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

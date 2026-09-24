using System.Globalization;
namespace FallbackPlan.Application.Tests;

/// <summary>
/// The background window's arithmetic (NFR-PERF-013, NFR-OPS-004): which
/// positions on the clock face background activity may start in, and when the
/// window next changes.
/// </summary>
/// <remarks>
/// <para>
/// Pure, so every case states the time rather than waiting for it. The case
/// that matters most is the one a person actually writes — <c>22:00-06:00</c>,
/// which crosses midnight — because a window read as an ordinary
/// <c>start ≤ t &lt; end</c> range is shut all night and a machine quietly
/// stops being backed up while its status display says nothing is wrong.
/// </para>
/// <para>
/// Does not establish NFR-TIME-001: a window is a policy about one machine's
/// own clock and no correctness property depends on it, which is why these
/// cases assert wall-clock positions rather than instants.
/// </para>
/// </remarks>
[TestClass]
public sealed class BackgroundWindowTests
{
    [TestMethod]
    public void AWindow_WithinTheDay_IsOpenBetweenItsEndsAndShutOutside()
    {
        var window = Parse("09:00-17:00");

        Assert.IsFalse(window.CrossesMidnight);
        Assert.IsFalse(window.IsOpen(At("08:59")));
        Assert.IsTrue(window.IsOpen(At("09:00")), "open at the moment it opens");
        Assert.IsTrue(window.IsOpen(At("16:59")));

        // Half open at the end: a window that is still open at its closing
        // minute is a window that never closes on the dot, and "until 17:00"
        // is how everyone reads it.
        Assert.IsFalse(window.IsOpen(At("17:00")));
        Assert.IsFalse(window.IsOpen(At("23:59")));
    }

    [TestMethod]
    public void AWindow_ThatCrossesMidnight_IsOpenOnBothSidesOfIt()
    {
        // The overnight window, and the reason this type exists rather than a
        // pair of comparisons at the call site: read as start ≤ t < end it is
        // shut at every hour of the night it was written for.
        var window = Parse("22:00-06:00");

        Assert.IsTrue(window.CrossesMidnight);
        Assert.IsTrue(window.IsOpen(At("22:00")));
        Assert.IsTrue(window.IsOpen(At("23:59")));
        Assert.IsTrue(window.IsOpen(At("00:00")));
        Assert.IsTrue(window.IsOpen(At("05:59")));
        Assert.IsFalse(window.IsOpen(At("06:00")));
        Assert.IsFalse(window.IsOpen(At("12:00")));
        Assert.IsFalse(window.IsOpen(At("21:59")));
    }

    [TestMethod]
    public void AWindow_ReportsWhenItNextOpensAndCloses()
    {
        var window = Parse("22:00-06:00");

        // Shut at noon: it opens tonight and it is already shut.
        var noon = At("12:00");
        Assert.AreEqual(At("22:00"), window.NextOpen(noon));
        Assert.AreEqual(noon, window.NextClose(noon));

        // Open at midnight: it is already open and closes this morning.
        var midnight = At("00:00");
        Assert.AreEqual(midnight, window.NextOpen(midnight));
        Assert.AreEqual(At("06:00"), window.NextClose(midnight));

        // And across the boundary the answer wraps to tomorrow rather than
        // going backwards, which is what a naive subtraction does.
        var evening = At("23:00");
        Assert.AreEqual(At("06:00") + TimeSpan.FromDays(1), window.NextClose(evening));
    }

    [TestMethod]
    public void AWindow_WhoseEndsAreEqual_IsRefusedRatherThanGuessed()
    {
        // Always open and never open are equally defensible readings, and one
        // of them silently stops every backup. Refused by name, with the way
        // to say "always" in the refusal.
        Assert.IsFalse(BackgroundWindow.TryParse("03:00-03:00", out var window, out var defect));
        Assert.IsNull(window);
        Assert.IsNotNull(defect);
        Assert.Contains("omit it", defect, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("22:00")]
    [DataRow("22-06")]
    [DataRow("22:00 to 06:00")]
    [DataRow("25:00-06:00")]
    [DataRow("22:00-06:61")]
    [DataRow("10pm-6am")]
    public void AWindow_ThatIsNotTwoTimes_IsRefusedByName(string text)
    {
        Assert.IsFalse(BackgroundWindow.TryParse(text, out var window, out var defect));
        Assert.IsNull(window);
        Assert.IsNotNull(defect);
        Assert.Contains("HH:mm", defect, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AWindow_KeepsItsTextForDisplay()
    {
        var window = Parse("  22:00-06:00  ");
        Assert.AreEqual("22:00-06:00", window.Text);
    }

    [TestMethod]
    public void AConfiguration_WrittenBeforeTheWindowExisted_LoadsAndSaysAnyHour()
    {
        // The compatibility rule the whole migration rests on. Every file on
        // every existing installation says "no window" by not mentioning one,
        // and reading that as anything but "any hour" stops those
        // installations backing up.
        var directory = Directory.CreateTempSubdirectory("fbp-window-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, """{ "schema_version": 5, "destinations": [], "backup_sets": [] }""");

            var loaded = ClientConfiguration.Load(path);

            Assert.AreEqual(ClientConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.IsNull(loaded.BackgroundWindow);
            Assert.IsNull(loaded.EffectiveBackgroundWindow);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void AConfiguration_WithAWindowThisBuildCannotRead_IsRefusedRatherThanIgnored()
    {
        // A window nobody honours is a machine backing up at the one time its
        // operator asked it not to, which is worse than a refusal at load.
        var directory = Directory.CreateTempSubdirectory("fbp-window-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(
                path,
                $$"""
                { "schema_version": {{ClientConfiguration.CurrentSchemaVersion}}, "background_window": "10pm-6am",
                  "destinations": [], "backup_sets": [] }
                """);

            var refusal = Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(path));
            Assert.Contains("background_window", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void AWindow_ReadsTheWallClockOfTheOffsetItIsGiven_NotTheInstant()
    {
        // The rule every caller has to honour and none of them can see from
        // the call site. A window is a position on a clock face (ADR-0069),
        // so IsOpen asks what the clock said where the offset says it is —
        // which means one instant, handed over in two offsets, gets two
        // answers. Hand it DateTimeOffset.UtcNow and it answers for UTC's
        // clock face: on any machine east or west of Greenwich the status
        // surface would then disagree with the pass by the machine's offset,
        // and on a machine running UTC nothing would ever reveal it.
        var window = Parse("22:00-06:00");

        // One instant. 23:00 in Sydney is 13:00 in London on the same day.
        var sydney = new DateTimeOffset(2026, 9, 22, 23, 0, 0, TimeSpan.FromHours(10));
        var london = sydney.ToOffset(TimeSpan.FromHours(1));
        Assert.AreEqual(sydney.UtcDateTime, london.UtcDateTime, "the two must name the same instant");

        Assert.IsTrue(window.IsOpen(sydney), "23:00 on the Sydney clock is inside 22:00-06:00");
        Assert.IsFalse(window.IsOpen(london), "13:00 on the London clock is not");
    }

    [TestMethod]
    public void AWindow_PairsItsStateWithTheBoundaryAStatusShouldShow()
    {
        // What a reporting surface has to get right: the next change is
        // NextOpen when shut and NextClose when open. Swapping them yields a
        // status whose state and whose "next change" contradict each other,
        // which is worse than reporting neither.
        var window = Parse("22:00-06:00");

        var noon = At("12:00");
        Assert.IsFalse(window.IsOpen(noon));
        Assert.IsGreaterThan(noon, window.NextOpen(noon), "shut: the boundary to show is the opening");

        var midnight = At("00:00");
        Assert.IsTrue(window.IsOpen(midnight));
        Assert.IsGreaterThan(midnight, window.NextClose(midnight), "open: the boundary to show is the closing");
    }

    private static BackgroundWindow Parse(string text)
    {
        Assert.IsTrue(BackgroundWindow.TryParse(text, out var window, out var defect), defect);
        return window!;
    }

    /// <summary>A wall-clock instant on one fixed day, which is all these cases need.</summary>
    private static DateTimeOffset At(string time) =>
        DateTimeOffset.ParseExact(
            $"2026-09-22T{time}:00+10:00", "yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
}

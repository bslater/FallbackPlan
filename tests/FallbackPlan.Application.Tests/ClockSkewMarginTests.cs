using FallbackPlan.Domain;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The clock skew margin a collector adds to a write intent's declared
/// duration before the intent may expire (NFR-TIME-002, specification 08 §7,
/// ADR-0009 Amendment 7): <c>clock_skew_margin_hours</c>, installation-wide in
/// the configuration file, an hour to a year, and a day when absent.
/// </summary>
/// <remarks>
/// A day, because once an intent's generation has passed the margin is all
/// that stands between a clock set wrong and an intent expired while its
/// writer still runs, and a day is the skew NFR-TIME-001 is proved against.
/// The five minutes it replaces absorbed a clock drifting, not a clock set
/// wrong. Strict at load, as every installation setting is: a margin this
/// build cannot honour is refused by name, never approximated.
/// </remarks>
[TestClass]
public sealed class ClockSkewMarginTests
{
    private string _directory = null!;

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [TestInitialize]
    public void Initialize()
    {
        _directory = Directory.CreateTempSubdirectory("fbp-skew-margin-").FullName;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public void AFileWrittenBeforeTheMarginExisted_LoadsAndMeansADay()
    {
        File.WriteAllText(ConfigPath, """{ "schema_version": 7, "destinations": [], "backup_sets": [] }""");

        var loaded = ClientConfiguration.Load(ConfigPath);

        Assert.AreEqual(ClientConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.IsNull(loaded.ClockSkewMarginHours);
        Assert.AreEqual(TimeSpan.FromDays(1), loaded.EffectiveClockSkewMargin);
        Assert.AreEqual(TimeSpan.FromDays(1), ClientConfiguration.Default.EffectiveClockSkewMargin);
    }

    [TestMethod]
    public void TheSchemaRises_WithTheMargin()
    {
        // Unknown fields are refused, so a file carrying the margin is a
        // compatibility event for any build that cannot read it, and the
        // version has to say so. Schema 8 carried it first; 9 moved on for
        // the S3 fields (ADR-0091), and nothing may move it back.
        Assert.IsGreaterThanOrEqualTo(8, ClientConfiguration.CurrentSchemaVersion);
    }

    [TestMethod]
    public void ADeclaredMargin_RoundTripsThroughSaveAndLoad()
    {
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            ClockSkewMarginHours = 6,
        }.Save(ConfigPath);

        var loaded = ClientConfiguration.Load(ConfigPath);

        Assert.AreEqual(6, loaded.ClockSkewMarginHours);
        Assert.AreEqual(TimeSpan.FromHours(6), loaded.EffectiveClockSkewMargin);
        Assert.Contains("\"clock_skew_margin_hours\": 6", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnAbsentMargin_IsNotWritten()
    {
        // Absent is the default, and a file that states the default in full
        // would pin it: the next build's default would never reach it.
        Assert.DoesNotContain("clock_skew_margin_hours", ClientConfiguration.Default.ExportJson(), StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(8761)]
    public void AMarginOutsideAnHourToAYear_IsRefusedByName(int hours)
    {
        // Zero is the assumption that every clock agrees, which is what the
        // margin exists not to make; negative expires an intent before its
        // writer's own declared duration; and past a year it no longer
        // absorbs skew but switches expiry off, which is the audited
        // force-expire's business in reverse.
        File.WriteAllText(
            ConfigPath,
            $$"""
            { "schema_version": {{ClientConfiguration.CurrentSchemaVersion}}, "clock_skew_margin_hours": {{hours}},
              "destinations": [], "backup_sets": [] }
            """);

        var refusal = Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
        Assert.Contains("clock_skew_margin_hours", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(8760)]
    public void TheBoundsThemselves_AreAccepted(int hours)
    {
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            ClockSkewMarginHours = hours,
        }.Save(ConfigPath);

        Assert.AreEqual(TimeSpan.FromHours(hours), ClientConfiguration.Load(ConfigPath).EffectiveClockSkewMargin);
    }
}

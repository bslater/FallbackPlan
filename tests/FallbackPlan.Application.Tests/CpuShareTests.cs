using FallbackPlan.Domain;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The CPU limit background activity is held to (NFR-PERF-013, ADR-0200): the
/// grammar a person writes in the configuration file, a whole percentage of
/// the machine's processors, and the one place it is written, the
/// installation's <c>background_cpu_limit</c>.
/// </summary>
/// <remarks>
/// <para>
/// A share of the whole machine rather than of one core, because the two
/// conventions people meet disagree by the core count, and the configuration
/// has to mean one of them. On a four-core laptop 25% is one core's worth.
/// </para>
/// <para>
/// Strict, with the defect named, as the window and the byte rates are: a
/// limit this build misreads is a limit nobody honours, so a form it cannot
/// read is refused at load. The compatibility case matters most. Every file
/// written before schema 12 says "unlimited" by not mentioning the limit, and
/// reading that as anything else would slow every installation at once.
/// </para>
/// </remarks>
[TestClass]
public sealed class CpuShareTests
{
    private string _directory = null!;

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [TestInitialize]
    public void Initialize() => _directory = Directory.CreateTempSubdirectory("fbp-cpu-share-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    [DataRow("25%", 25)]
    [DataRow("1%", 1)]
    [DataRow("99%", 99)]
    [DataRow("50 %", 50)]
    [DataRow("  10% ", 10)]
    public void AShare_ReadsAsAWholePercentageOfTheMachine(string text, int percent)
    {
        Assert.AreEqual(percent, Parse(text).Percent);
    }

    [TestMethod]
    public void AShare_KeepsItsTextForDisplay()
    {
        Assert.AreEqual("25%", Parse(" 25% ").Text);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("fast")]
    [DataRow("25")]
    [DataRow("%")]
    [DataRow("2.5%")]
    [DataRow("-5%")]
    [DataRow("25 percent")]
    [DataRow("1 core")]
    public void AShare_ThatIsNotAWholePercentage_IsRefusedByName(string text)
    {
        Assert.IsFalse(CpuShare.TryParse(text, out var share, out var defect));
        Assert.IsNull(share);
        Assert.IsNotNull(defect);
        Assert.Contains("25%", defect, StringComparison.Ordinal, "the refusal names a form that would be read");
    }

    [TestMethod]
    public void AShareOfNothing_IsRefusedAsAStopRatherThanALimit()
    {
        Assert.IsFalse(CpuShare.TryParse("0%", out _, out var defect));
        Assert.Contains("stop", defect!, StringComparison.Ordinal);
        Assert.Contains("leave the setting out", defect!, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("100%")]
    [DataRow("150%")]
    [DataRow("99999999999999999999%")]
    public void AShareOfTheWholeMachineOrMore_IsRefusedAsNoLimitAtAll(string text)
    {
        // A share no machine can exceed limits nothing, and writing it would
        // look like a limit while being none. Leaving the setting out is how
        // "unlimited" is said.
        Assert.IsFalse(CpuShare.TryParse(text, out _, out var defect));
        Assert.Contains("leave the setting out", defect!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AConfiguration_WrittenBeforeTheLimitExisted_LoadsAndLimitsNothing()
    {
        File.WriteAllText(
            ConfigPath,
            """
            { "schema_version": 11, "background_read_limit": "40 MiB/s", "destinations": [], "backup_sets": [] }
            """);

        var loaded = ClientConfiguration.Load(ConfigPath);

        Assert.AreEqual(ClientConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.IsNull(loaded.BackgroundCpuLimit);
        Assert.IsNull(loaded.EffectiveBackgroundCpuLimit);
        Assert.AreEqual("40 MiB/s", loaded.BackgroundReadLimit, "the limits a schema-11 file states are kept");
    }

    [TestMethod]
    public void AConfiguration_WithACpuLimit_RoundTripsThroughSaveAndLoad()
    {
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            BackgroundCpuLimit = "25%",
        }.Save(ConfigPath);

        var loaded = ClientConfiguration.Load(ConfigPath);

        Assert.AreEqual("25%", loaded.BackgroundCpuLimit);
        Assert.AreEqual(25, loaded.EffectiveBackgroundCpuLimit!.Percent);
        Assert.Contains("\"background_cpu_limit\"", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [TestMethod]
    public void AConfiguration_WithoutACpuLimit_WritesNone()
    {
        ClientConfiguration.Default.Save(ConfigPath);

        Assert.DoesNotContain("background_cpu_limit", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [TestMethod]
    public void AConfiguration_WithACpuLimitThisBuildCannotRead_IsRefusedByName()
    {
        File.WriteAllText(
            ConfigPath,
            $$"""
            { "schema_version": {{ClientConfiguration.CurrentSchemaVersion}}, "background_cpu_limit": "a quarter",
              "destinations": [], "backup_sets": [] }
            """);

        var refusal = Assert.ThrowsExactly<ClientStateException>(() => ClientConfiguration.Load(ConfigPath));
        Assert.Contains("background_cpu_limit", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("25%", refusal.Message, StringComparison.Ordinal, "the refusal carries the grammar's defect");
    }

    [TestMethod]
    public void AConfiguration_WithACpuLimitThisBuildCannotRead_IsNotSaved()
    {
        var configuration = new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            BackgroundCpuLimit = "100%",
        };

        Assert.ThrowsExactly<ClientStateException>(() => configuration.Save(ConfigPath));
        Assert.IsFalse(File.Exists(ConfigPath), "a refused configuration must not reach the disk");
    }

    [TestMethod]
    public void TheSchemaRises_WithTheCpuLimit()
    {
        // Unknown fields are refused, so a file carrying the limit is a
        // compatibility event for any build that cannot read it, and the
        // version has to say so.
        Assert.AreEqual(12, ClientConfiguration.CurrentSchemaVersion);
    }

    private static CpuShare Parse(string text)
    {
        Assert.IsTrue(CpuShare.TryParse(text, out var share, out var defect), defect);
        return share!;
    }
}

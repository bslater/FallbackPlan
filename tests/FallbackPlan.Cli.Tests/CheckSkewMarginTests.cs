using FallbackPlan.Application;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The check counts live write intents as the collector would, under the
/// installation's configured clock skew margin, and says which margin decided
/// them (NFR-TIME-002, ADR-0009 Amendment 7). A count taken under a margin of
/// its own would be a second copy of the rule, and a check that calls an
/// intent expired while the collector still honours it disagrees with the
/// thing it checks.
/// </summary>
[TestClass]
public sealed class CheckSkewMarginTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [TestMethod]
    public async Task Check_CountsIntentsUnderTheConfiguredMargin()
    {
        await _cli.InitAsync();
        Directory.CreateDirectory(_cli.StatePath);
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            ClockSkewMarginHours = 6,
        }.Save(Path.Combine(_cli.StatePath, "config.json"));

        var check = await _cli.RunAsync("check");

        Assert.AreEqual(0, check.ExitCode, check.All);
        Assert.Contains("skew margin 6 h", JournalLine(check.Output), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Check_WhereTheFileStatesNoMargin_CountsUnderADay()
    {
        await _cli.InitAsync();

        var check = await _cli.RunAsync("check");

        Assert.AreEqual(0, check.ExitCode, check.All);
        Assert.Contains("skew margin 24 h", JournalLine(check.Output), StringComparison.Ordinal);
    }

    private static string JournalLine(string output) =>
        Assert.ContainsSingle(output.Split('\n').Where(line => line.StartsWith("journal", StringComparison.Ordinal)));
}

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The <c>logs</c> verb (ADR-0043 §6, FR-SVC-010).
/// </summary>
/// <remarks>
/// It is the one read verb with no local fallback, and that is what these tests
/// are about. Every other read command can open the repository itself when no
/// service is listening; this one cannot, because the records live in the
/// running service's memory and a service that is not running has no log to
/// show. So it has to be told which service to ask, and say so plainly when it
/// has not been — a verb that answered "no records" because it did not know
/// where to look would be lying in the one place people go when something has
/// already gone wrong.
/// </remarks>
[TestClass]
public sealed class LogsVerbTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [TestMethod]
    public async Task Logs_WithNeitherStateNorConnect_RefusesNamingBothWaysToSayWhichService()
    {
        var result = await CliHarness.RunRawAsync("logs");

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("--state", result.All, StringComparison.Ordinal);
        Assert.Contains("--connect", result.All, StringComparison.Ordinal);

        // And says why there is no third option, since "just read it locally"
        // is the first thing anybody will reach for.
        Assert.Contains("--direct", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Logs_WithAStateDirectoryButNoServiceListening_FailsSayingSoRatherThanHanging()
    {
        // Nothing is listening on this state directory. The connection refusal
        // has to surface as a stated reason and a non-zero code — never a hang,
        // and never a stack trace.
        var result = await CliHarness.RunRawAsync("logs", "--state", _cli.WorkPath);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsFalse(
            result.All.Contains("Unhandled exception", StringComparison.Ordinal),
            $"A missing service is an ordinary condition, not a crash: {result.All}");
    }

    [TestMethod]
    public async Task Logs_Help_DescribesItsFlagsAndThatItNeedsARunningService()
    {
        var result = await CliHarness.RunRawAsync("logs", "--help");

        Assert.AreEqual(0, result.ExitCode);
        foreach (var flag in new[] { "--level", "--since", "--tail", "--follow" })
        {
            Assert.Contains(flag, result.All, StringComparison.Ordinal);
        }

        Assert.Contains("running service", result.All, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _cli.Dispose();
}

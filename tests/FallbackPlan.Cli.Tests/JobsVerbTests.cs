namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The <c>jobs</c> verb (FR-SVC-018; ADR-0050): the job journal from the
/// command line — the history list, and one run's report by id.
/// </summary>
/// <remarks>
/// Like <c>logs</c>, it is a read verb with no local fallback: the journal
/// belongs to the service's state directory and is written by the running
/// service, so the honest answer with no service listening is a refusal,
/// never an empty history invented here.
/// </remarks>
[TestClass]
public sealed class JobsVerbTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [TestMethod]
    public async Task Jobs_Help_DescribesItsFlagsAndThatItNeedsARunningService()
    {
        var result = await CliHarness.RunRawAsync("jobs", "--help");

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("--limit", result.All, StringComparison.Ordinal);
        Assert.Contains("--changes", result.All, StringComparison.Ordinal);
        Assert.Contains("--failures", result.All, StringComparison.Ordinal);
        Assert.Contains("running service", result.All, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Jobs_DetailFlagsWithoutAJobId_RefuseNamingTheArgument()
    {
        // --changes and --failures describe one run; asking for them across
        // the whole history is not a thing — the refusal names the missing
        // job id rather than silently ignoring the flag.
        var result = await CliHarness.RunRawAsync("jobs", "--changes", "--state", _cli.WorkPath);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("job", result.All, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Jobs_WithAStateDirectoryButNoServiceListening_FailsSayingSoRatherThanHanging()
    {
        var result = await CliHarness.RunRawAsync("jobs", "--state", _cli.WorkPath);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsFalse(
            result.All.Contains("Unhandled exception", StringComparison.Ordinal),
            $"A missing service is an ordinary condition, not a crash: {result.All}");
    }

    public void Dispose() => _cli.Dispose();
}

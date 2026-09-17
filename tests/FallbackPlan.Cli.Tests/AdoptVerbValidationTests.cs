namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The refusals the <c>adopt</c> and <c>discover</c> verbs give before they
/// reach any service (ADR-0061; FR-WOR-006's headless half). Does not
/// establish the adoption itself, which needs a running service and lives
/// in the Hosts tests.
/// </summary>
/// <remarks>
/// Each is a mistake a person makes on the worst morning of their computing
/// life, so each says what to do next rather than what went wrong — and
/// each is reached without a service, so nothing here can be mistaken for a
/// connection failure.
/// </remarks>
[TestClass]
public sealed class AdoptVerbValidationTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "fbp-adopt-validation", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public async Task Adopt_WithoutAPassphraseVariable_NamesTheFlagAndWhereTheDerivationRuns()
    {
        var result = await CliHarness.RunRawAsync(
            "adopt", "--destination", "vault", "--repository", new string('c', 32), "--state", _state);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--passphrase-env", result.Error, StringComparison.Ordinal);
        Assert.Contains("never sent", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Adopt_WithAMalformedRepositoryId_SaysWhatShapeItWantedAndHowToListThem()
    {
        var result = await CliHarness.RunRawAsync(
            "adopt", "--destination", "vault", "--repository", "not-an-id", "--state", _state,
            "--passphrase-env", "NOT_SET");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("thirty-two hex", result.Error, StringComparison.Ordinal);
        Assert.Contains("discover --destination vault", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Discover_WithNothingListening_SaysTheServiceDoesThis()
    {
        var result = await CliHarness.RunRawAsync("discover", "--destination", "vault", "--state", _state);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("no service is listening", result.Error, StringComparison.Ordinal);
        Assert.Contains("fallbackplan-agent", result.Error, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }
}

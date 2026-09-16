namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The refusals the <c>claim</c> verb gives before it dials anything
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md);
/// FR-KIT-006). Does not establish FR-REP-001, which is the ceremony itself.
/// </summary>
/// <remarks>
/// These are the mistakes a person makes on the worst morning of their
/// computing life, so each has to say what to do next rather than what went
/// wrong. Nothing here needs a peer: they are all reached from this machine's
/// own state directory.
/// </remarks>
[TestClass]
public sealed class ClaimVerbValidationTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "fbp-claim-validation", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public async Task Claim_WithNoPeerPinned_SaysToPairFirst()
    {
        var result = await CliHarness.RunRawAsync(
            "claim", "127.0.0.1:9", "--state", _state, "--kit", "kit.bin", "--passphrase-env", "NOT_SET");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("Pair with the peer first", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Claim_NamingAFingerprintThatIsNotPinned_SaysSo()
    {
        // The explicit form is a filter over the same pinned list, so its
        // failure has to be as legible as the empty-list one.
        var result = await CliHarness.RunRawAsync(
            "claim", "127.0.0.1:9", "--state", _state, "--kit", "kit.bin",
            "--passphrase-env", "NOT_SET", "--fingerprint", "deadbeef");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("deadbeef", result.Error, StringComparison.Ordinal);
        Assert.Contains("Pair with the peer first", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Claim_WithoutAKit_IsRefusedByTheParser()
    {
        // The kit is not optional and never can be: it carries the salt the
        // claim key is derived with, and nothing else on a rebuilt machine
        // knows it.
        var result = await CliHarness.RunRawAsync(
            "claim", "127.0.0.1:9", "--state", _state, "--passphrase-env", "NOT_SET");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--kit", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Claim_WithAMalformedEndpoint_SaysWhatShapeItWanted()
    {
        var result = await CliHarness.RunRawAsync(
            "claim", "not-an-endpoint", "--state", _state, "--kit", "kit.bin", "--passphrase-env", "NOT_SET");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("host:port", result.Error, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }
}

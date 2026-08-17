namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The option validation on <c>--connect</c> (ADR-0028 §5): the combinations
/// that cannot mean anything are refused with a stated reason, before any
/// socket is dialled. None of these needs a service — they are the errors a
/// human meets by mistyping the command.
/// </summary>
[TestClass]
public sealed class RemoteConnectValidationTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "fbp-connect-validation", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public async Task Connect_WithDirect_IsRefusedAsContradictory()
    {
        // --direct only exists on the gateway verbs; verify is one.
        var result = await CliHarness.RunRawAsync(
            "verify", "--connect", "127.0.0.1:9", "--direct");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("cannot be combined", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Connect_WithoutState_IsRefused()
    {
        var result = await CliHarness.RunRawAsync(
            "snapshots", "--connect", "127.0.0.1:9", "--fingerprint", "abcdef");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--state", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Connect_WithoutFingerprint_IsRefused()
    {
        var result = await CliHarness.RunRawAsync(
            "snapshots", "--connect", "127.0.0.1:9", "--state", _state);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--fingerprint", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Connect_WithAMalformedEndpoint_IsRefused()
    {
        var result = await CliHarness.RunRawAsync(
            "snapshots", "--connect", "not-an-endpoint", "--state", _state, "--fingerprint", "abcdef");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("is not host:port", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Connect_WithAFingerprintThatMatchesNoPairing_IsRefusedBeforeDialling()
    {
        // The state directory holds no pairing for this fingerprint, so
        // resolution fails before a socket is ever opened — the endpoint here
        // does not have to be reachable.
        var result = await CliHarness.RunRawAsync(
            "snapshots", "--connect", "127.0.0.1:9", "--state", _state, "--fingerprint", "zzzzzzzz");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("no pairing matches", result.Error, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }
}

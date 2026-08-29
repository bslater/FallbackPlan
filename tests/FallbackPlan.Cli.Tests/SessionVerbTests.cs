using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The CLI's half of authentication (FR-USR-003, FR-USR-006; ADR-0045 §5, §8):
/// `login` and `logout`, and the session this end caches.
/// </summary>
[TestClass]
public sealed class SessionVerbTests : IDisposable
{
    private readonly CliHarness _harness = new();
    private readonly string _passwordVariable = "FBP_CLI_SESSION_" + Guid.NewGuid().ToString("N");

    public SessionVerbTests()
    {
        Directory.CreateDirectory(_harness.StatePath);
        Environment.SetEnvironmentVariable(_passwordVariable, "The-0wner-passw0rd");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_passwordVariable, null);
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Login_WithNoServiceListening_SaysSoRatherThanFallingBackToDirectMode()
    {
        // Every other read verb answers "no service" by doing the work in this
        // process instead. A session cannot: it is minted by a running service
        // and means nothing without one, so this is the honest answer rather
        // than an omission.
        var result = await CliHarness.RunRawAsync(
            "login", "--state", _harness.StatePath, "--user", "ben",
            "--password-env", _passwordVariable);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("sign in to", result.All, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Login_WithAVariableThatIsNotSet_RefusesRatherThanReadingTheCommandLine()
    {
        var result = await CliHarness.RunRawAsync(
            "login", "--state", _harness.StatePath, "--user", "ben",
            "--password-env", "FBP_NOT_SET_ANYWHERE");

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("FBP_NOT_SET_ANYWHERE", result.All, StringComparison.Ordinal);
        Assert.Contains("never on the command line", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Logout_WithNoServiceListening_StillForgetsTheSessionHere()
    {
        // A service that is down cannot revoke, and leaving a token behind that
        // the operator believes they signed out of is the worse of the two
        // failures — the session died with that process anyway.
        var cache = new SessionCache(_harness.StatePath);
        cache.Save(new SessionResult(new string('a', 64), "ben", "Owner", 0, 0));
        Assert.IsNotNull(cache.Token);

        var result = await CliHarness.RunRawAsync("logout", "--state", _harness.StatePath);

        Assert.AreEqual(0, result.ExitCode, result.All);
        Assert.IsNull(cache.Token);
        Assert.Contains("could not be reached", result.All, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Restart_WithNoServiceListening_SaysSoRatherThanPretending()
    {
        // Like login: only a running service can restart, so "no service" is
        // the honest answer rather than a reason to do anything here.
        var result = await CliHarness.RunRawAsync("restart", "--state", _harness.StatePath);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("running service", result.All, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void SessionCache_OnDisk_IsOwnerOnlyAndCarriesOnlyTheToken()
    {
        var cache = new SessionCache(_harness.StatePath);
        cache.Save(new SessionResult(new string('b', 64), "ben", "Owner", 0, 0));

        var path = Path.Combine(_harness.StatePath, "session.json");
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual("ben", cache.User);

        // No password reaches this file, because none was ever given to it.
        Assert.DoesNotContain(
            "password", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path),
                "a session token another local user can read is a way to act as somebody else");
        }
    }

    [TestMethod]
    public void SessionCache_ThatDoesNotParse_ReadsAsNoSessionRatherThanThrowing()
    {
        // The worst a malformed convenience file may cost is one login, and
        // refusing to run because of one would be a poor trade.
        File.WriteAllText(Path.Combine(_harness.StatePath, "session.json"), "{ not json");

        var cache = new SessionCache(_harness.StatePath);

        Assert.IsNull(cache.Token);
        Assert.IsNull(cache.User);
    }

    [TestMethod]
    public async Task DirectMode_NeedsNoSession()
    {
        // FR-USR-006's second carve-out: --direct holds the installation
        // passphrase, which derives the keys and is therefore strictly stronger
        // than any password. Asking for a weaker secret in addition to a
        // stronger one would be ceremony, not security.
        await _harness.InitAsync();
        _harness.WriteFile("a.txt");

        var result = await _harness.RunAsync("backup", _harness.WorkPath, "--direct");

        Assert.AreEqual(0, result.ExitCode, result.All);
    }
}

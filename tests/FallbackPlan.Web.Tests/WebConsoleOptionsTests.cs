using FallbackPlan.Web;

namespace FallbackPlan.Web.Tests;

/// <summary>The console's tiny command line: a state directory, and optionally a port.</summary>
[TestClass]
public sealed class WebConsoleOptionsTests
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "fbp-web-opt", Guid.NewGuid().ToString("n")[..12]);

    public WebConsoleOptionsTests() => Directory.CreateDirectory(_state);

    [TestMethod]
    public void Parse_StateAndPort_Succeeds()
    {
        Assert.IsTrue(WebConsoleOptions.TryParse(["--state", _state, "--port", "8123"], out var options, out _));
        Assert.AreEqual(Path.GetFullPath(_state), options!.StateDirectory);
        Assert.AreEqual(8123, options.Port);
    }

    [TestMethod]
    public void Parse_NoPort_DefaultsToEphemeral()
    {
        Assert.IsTrue(WebConsoleOptions.TryParse(["--state", _state], out var options, out _));
        Assert.AreEqual(0, options!.Port);
    }

    [TestMethod]
    public void Parse_NoState_ResolvesTheSharedInstallationDefault()
    {
        // The same default every process of the installation resolves
        // (FR-SVC-016): a console started with no --state watches the state a
        // bare `fallbackplan-agent` serves — created on first touch, so a
        // console started before the service still comes up and waits.
        var shared = Path.Combine(_state, "shared-default");
        Environment.SetEnvironmentVariable(FallbackPlan.Api.InstallationDefaults.StateVariable, shared);
        try
        {
            Assert.IsTrue(WebConsoleOptions.TryParse(["--port", "8123"], out var options, out var failure), failure);
            Assert.AreEqual(Path.GetFullPath(shared), options!.StateDirectory);
            Assert.IsTrue(Directory.Exists(shared), "the default state is created on first touch");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FallbackPlan.Api.InstallationDefaults.StateVariable, null);
        }
    }

    [TestMethod]
    public void Parse_LogLevel_IsAcceptedRatherThanBouncedAsUnknown()
    {
        // The host reads --log-level itself, before the parse — but the parse
        // sees the same argv, and it refused the flag the usage advertises,
        // which made it unusable end to end.
        Assert.IsTrue(
            WebConsoleOptions.TryParse(["--state", _state, "--log-level", "debug"], out var options, out var failure),
            failure);
        Assert.AreEqual(Path.GetFullPath(_state), options!.StateDirectory);
    }

    [TestMethod]
    public void Parse_AbsentStateDirectory_FailsNamingThePath()
    {
        var absent = Path.Combine(_state, "not-there");
        Assert.IsFalse(WebConsoleOptions.TryParse(["--state", absent], out _, out var failure));
        StringAssert.Contains(failure, "not-there");
    }

    [TestMethod]
    public void Parse_PortOutOfRange_FailsNamingTheValue()
    {
        Assert.IsFalse(WebConsoleOptions.TryParse(["--state", _state, "--port", "70000"], out _, out var failure));
        StringAssert.Contains(failure, "70000");
    }

    [TestMethod]
    public void Parse_UnknownFlag_FailsNamingIt()
    {
        // There is deliberately no --interface: loopback is not a choice
        // (ADR-0036 §2), so the flag that would widen it does not parse.
        Assert.IsFalse(WebConsoleOptions.TryParse(["--state", _state, "--interface", "0.0.0.0"], out _, out var failure));
        StringAssert.Contains(failure, "--interface");
    }

    [TestMethod]
    public void Parse_FlagWithoutValue_FailsNamingIt()
    {
        Assert.IsFalse(WebConsoleOptions.TryParse(["--state"], out _, out var failure));
        StringAssert.Contains(failure, "--state");
    }
}

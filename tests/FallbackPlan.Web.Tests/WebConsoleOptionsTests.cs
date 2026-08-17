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
    public void Parse_MissingState_FailsNamingTheFlag()
    {
        Assert.IsFalse(WebConsoleOptions.TryParse(["--port", "8123"], out _, out var failure));
        StringAssert.Contains(failure, "--state");
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

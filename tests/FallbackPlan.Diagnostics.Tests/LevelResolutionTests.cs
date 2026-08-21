using FallbackPlan.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Diagnostics.Tests;

/// <summary>
/// Where the level in force comes from (ADR-0043 §6): the flag, then the
/// environment, then the configuration, then the host's own fallback.
/// </summary>
/// <remarks>
/// The sources are passed in rather than read here, which is the point: one
/// function decides the order for every host, and a test can prove the order
/// without setting a process-wide environment variable that every other test
/// running beside it would inherit.
/// </remarks>
[TestClass]
public sealed class LevelResolutionTests
{
    [TestMethod]
    public void Level_WhenTheFlagNamesOne_OutranksEverythingElse()
    {
        Assert.IsTrue(LoggingOptions.TryResolveLevel(
            "trace", "error", LogLevel.Critical, out var level, out var refusal));
        Assert.AreEqual(LogLevel.Trace, level);
        Assert.IsNull(refusal);
    }

    [TestMethod]
    public void Level_WithNoFlag_TakesTheEnvironmentOverTheConfiguration()
    {
        Assert.IsTrue(LoggingOptions.TryResolveLevel(
            null, "debug", LogLevel.Critical, out var level, out _));
        Assert.AreEqual(LogLevel.Debug, level);
    }

    [TestMethod]
    public void Level_WithNeitherFlagNorEnvironment_TakesTheConfiguration()
    {
        Assert.IsTrue(LoggingOptions.TryResolveLevel(
            null, null, LogLevel.Error, out var level, out _));
        Assert.AreEqual(LogLevel.Error, level);
    }

    [TestMethod]
    public void Level_WithNoSourceAtAll_IsTheHostsFallback()
    {
        Assert.IsTrue(LoggingOptions.TryResolveLevel(null, null, configured: null, out var service, out _));
        Assert.AreEqual(LogLevel.Information, service);

        // A console passes Warning: its output is the answer to the command,
        // and lifecycle chatter in the middle of a report is noise.
        Assert.IsTrue(LoggingOptions.TryResolveLevel(
            null, null, configured: null, out var console, out _, fallback: LogLevel.Warning));
        Assert.AreEqual(LogLevel.Warning, console);
    }

    [TestMethod]
    public void Level_WhenAnEmptyStringIsPassed_IsNotTreatedAsNamingOne()
    {
        // An unset variable and a variable set to nothing must behave the same,
        // because a shell that exports an empty value is indistinguishable from
        // one that exported nothing.
        Assert.IsTrue(LoggingOptions.TryResolveLevel("", "", LogLevel.Error, out var level, out _));
        Assert.AreEqual(LogLevel.Error, level);
    }

    [TestMethod]
    public void Level_WhenTheFlagNamesNothingReal_IsRefusedByName()
    {
        Assert.IsFalse(LoggingOptions.TryResolveLevel(
            "dbeug", null, configured: null, out _, out var refusal));
        Assert.IsNotNull(refusal);
        StringAssert.Contains(refusal, "--log-level");
        StringAssert.Contains(refusal, "dbeug");

        // Every accepted name is listed, so the refusal is also the help.
        foreach (var name in LoggingOptions.LevelNames)
        {
            StringAssert.Contains(refusal, name);
        }
    }

    [TestMethod]
    public void Level_WhenTheEnvironmentNamesNothingReal_IsRefusedNamingTheVariable()
    {
        // Silently falling back after a typo in an exported variable is how
        // somebody spends an afternoon wondering why nothing changed.
        Assert.IsFalse(LoggingOptions.TryResolveLevel(
            null, "chatty", configured: null, out _, out var refusal));
        Assert.IsNotNull(refusal);
        StringAssert.Contains(refusal, LoggingOptions.LevelVariable);
    }

    [TestMethod]
    public void Level_WhenTheFlagIsGood_TheEnvironmentIsNotEvenConsulted()
    {
        // Precedence is short-circuiting, not "collect them all and pick": a
        // machine with a stale FALLBACKPLAN_LOG_LEVEL must not refuse a command
        // that named a perfectly good level on its own command line.
        Assert.IsTrue(LoggingOptions.TryResolveLevel(
            "warning", "nonsense", configured: null, out var level, out var refusal));
        Assert.AreEqual(LogLevel.Warning, level);
        Assert.IsNull(refusal);
    }

    [TestMethod]
    public void Level_EverySpellingTheHelpOffers_Parses()
    {
        foreach (var name in LoggingOptions.LevelNames)
        {
            Assert.IsTrue(LoggingOptions.TryParseLevel(name, out var level), name);
            Assert.AreEqual(name, LoggingOptions.NameOf(level));
        }
    }
}

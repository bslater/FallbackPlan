using System.Globalization;
using FallbackPlan.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Diagnostics.Tests;

/// <summary>
/// What one captured record turns into: a sequenced entry in the ring, and a
/// line in the file carrying the same sequence (ADR-0043 §6).
/// </summary>
/// <remarks>
/// The sequence on the file line is the whole point of this suite. A support
/// log and the records a client reads over the contract are two renderings of
/// one capture, and once one of them is redacted the sequence is the only
/// thing they still have in common — so "record 4,192 is where it went wrong"
/// has to mean the same thing on both sides.
/// </remarks>
[TestClass]
public sealed class LoggerProviderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fbp-log-provider-tests", Guid.NewGuid().ToString("n"));

    /// <summary>The composition under test, cleared once a test has drained it.</summary>
    private LoggingComposition? _logging;

    public void Dispose()
    {
        _logging?.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private LoggingComposition Compose(LogLevel level = LogLevel.Trace) =>
        LoggingComposition.Create(new LoggingOptions
        {
            Default = level,
            Directory = _directory,
            RingCapacity = 64,
        });

    /// <summary>
    /// Drains the sink, then reads what it wrote.
    /// </summary>
    /// <param name="logging">The composition to shut down first.</param>
    /// <remarks>
    /// Disposing is the synchronisation, not a poll with a generous timeout.
    /// The file sink writes off the caller's thread and
    /// <c>RollingFileSink.DisposeAsync</c> is documented to put whatever is
    /// queued on disk before returning, so shutting it down makes the read
    /// deterministic. A timeout-based wait passes on an idle machine and fails
    /// under load, which is a test that reports the machine's mood rather than
    /// the code's behaviour.
    /// </remarks>
    private string FileTextAfterDraining(LoggingComposition logging)
    {
        logging.Dispose();
        _logging = null;

        var path = Path.Combine(_directory, "fallbackplan-current.log");
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [TestMethod]
    public void FileLine_ForEveryRecord_LeadsWithTheSameSequenceTheRingAssigned()
    {
        var logging = Compose();
        _logging = logging;
        var logger = logging.Factory.CreateLogger("FallbackPlan.Test");

#pragma warning disable CA1848 // A test is not a hot path, and the point here is the sink, not the call.
        logger.LogWarning("first");
        logger.LogWarning("second");
#pragma warning restore CA1848

        var page = logging.Ring.Read(sinceSequence: 0, maximum: 10, minimumLevel: LogLevel.Trace);
        var text = FileTextAfterDraining(logging);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, lines);
        Assert.HasCount(2, page.Records);

        foreach (var record in page.Records)
        {
            var line = lines.Single(candidate =>
                candidate.Contains(record.Values[^1].Value?.ToString() ?? "?", StringComparison.Ordinal));

            Assert.IsTrue(
                line.StartsWith(record.Sequence.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                $"The file line must lead with sequence {record.Sequence}: '{line}'");
        }
    }

    [TestMethod]
    public void FileLine_ForOneRecord_CarriesLevelEventIdAndCategory()
    {
        var logging = Compose();
        _logging = logging;
        var logger = logging.Factory.CreateLogger("FallbackPlan.Test.Category");

#pragma warning disable CA1848
        logger.Log(LogLevel.Error, new EventId(4242), "the sky fell");
#pragma warning restore CA1848

        var line = FileTextAfterDraining(logging).Trim();
        StringAssert.Contains(line, "error");
        StringAssert.Contains(line, "4242");
        StringAssert.Contains(line, "FallbackPlan.Test.Category");
        StringAssert.Contains(line, "the sky fell");
    }

    [TestMethod]
    public void Records_BeneathTheLevelInForce_ReachNeitherSink()
    {
        var logging = Compose(LogLevel.Warning);
        _logging = logging;
        var logger = logging.Factory.CreateLogger("FallbackPlan.Test");

#pragma warning disable CA1848
        logger.LogInformation("beneath the floor");
        logger.LogWarning("above it");
#pragma warning restore CA1848

        var page = logging.Ring.Read(sinceSequence: 0, maximum: 10, minimumLevel: LogLevel.Trace);
        Assert.ContainsSingle(page.Records);

        var text = FileTextAfterDraining(logging);
        Assert.DoesNotContain("beneath the floor", text);
        StringAssert.Contains(text, "above it");
    }

    [TestMethod]
    public void LevelSwitch_WhenTheLevelIsRaised_TakesEffectWithoutRebuildingTheFactory()
    {
        // The level a machine needs is only known once it has already
        // misbehaved; requiring a restart to raise it is requiring somebody to
        // destroy the evidence first (ADR-0043 §6, FR-SVC-010).
        using var logging = Compose(LogLevel.Warning);
        var logger = logging.Factory.CreateLogger("FallbackPlan.Test");
        Assert.IsFalse(logger.IsEnabled(LogLevel.Debug));

        logging.Levels.Set(logging.Levels.Current with { Default = LogLevel.Debug });

        Assert.IsTrue(logger.IsEnabled(LogLevel.Debug), "The same logger instance must see the new level.");
    }
}

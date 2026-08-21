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

    public void Dispose()
    {
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

    private async Task<string> FileTextAsync()
    {
        var path = Path.Combine(_directory, "fallbackplan-current.log");

        // The file sink writes off the caller's thread, so the line is not
        // there the instant the call returns.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (text.Length > 0)
                {
                    return text;
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return string.Empty;
    }

    [TestMethod]
    public async Task FileLine_ForEveryRecord_LeadsWithTheSameSequenceTheRingAssigned()
    {
        using var logging = Compose();
        var logger = logging.Factory.CreateLogger("FallbackPlan.Test");

#pragma warning disable CA1848 // A test is not a hot path, and the point here is the sink, not the call.
        logger.LogWarning("first");
        logger.LogWarning("second");
#pragma warning restore CA1848

        var text = await FileTextAsync().ConfigureAwait(false);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, lines);

        var page = logging.Ring.Read(sinceSequence: 0, maximum: 10, minimumLevel: LogLevel.Trace);
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
    public async Task FileLine_ForOneRecord_CarriesLevelEventIdAndCategory()
    {
        using var logging = Compose();
        var logger = logging.Factory.CreateLogger("FallbackPlan.Test.Category");

#pragma warning disable CA1848
        logger.Log(LogLevel.Error, new EventId(4242), "the sky fell");
#pragma warning restore CA1848

        var line = (await FileTextAsync().ConfigureAwait(false)).Trim();
        StringAssert.Contains(line, "error");
        StringAssert.Contains(line, "4242");
        StringAssert.Contains(line, "FallbackPlan.Test.Category");
        StringAssert.Contains(line, "the sky fell");
    }

    [TestMethod]
    public async Task Records_BeneathTheLevelInForce_ReachNeitherSink()
    {
        using var logging = Compose(LogLevel.Warning);
        var logger = logging.Factory.CreateLogger("FallbackPlan.Test");

#pragma warning disable CA1848
        logger.LogInformation("beneath the floor");
        logger.LogWarning("above it");
#pragma warning restore CA1848

        var text = await FileTextAsync().ConfigureAwait(false);
        Assert.DoesNotContain("beneath the floor", text);
        StringAssert.Contains(text, "above it");

        var page = logging.Ring.Read(sinceSequence: 0, maximum: 10, minimumLevel: LogLevel.Trace);
        Assert.ContainsSingle(page.Records);
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

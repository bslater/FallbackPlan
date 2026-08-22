using FallbackPlan.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Diagnostics.Tests;

/// <summary>
/// What happens when the log directory cannot be had at all (ADR-0043 §6).
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because the opposite behaviour shipped and took five CI
/// jobs down with it. Composing logging was the first thing every agent verb
/// did, and it created its directory eagerly — so a state directory the current
/// user cannot write turned every verb, including the one whose entire job is
/// to set up the account that could write it, into an access-denied stack
/// trace.
/// </para>
/// <para>
/// The directory here is unopenable <b>by construction rather than by
/// permission</b>: its parent is an ordinary file, which no user can descend
/// into, root included. A drill that relied on a permission bit would pass on
/// every developer machine and in the container this suite usually runs in,
/// which is precisely how the defect reached CI in the first place.
/// </para>
/// </remarks>
[TestClass]
public sealed class DurableSinkRefusalTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-sink-refusal-tests", Guid.NewGuid().ToString("n"));

    public DurableSinkRefusalTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(NotADirectory, "this is a file, and a file has no children");
    }

    /// <summary>A path that exists and is not a directory.</summary>
    private string NotADirectory => Path.Combine(_root, "occupied");

    /// <summary>A log directory nothing can create, because its parent is a file.</summary>
    private string UnopenableDirectory => Path.Combine(NotADirectory, "logs");

    /// <summary>A log directory that opens perfectly well.</summary>
    private string WritableDirectory => Path.Combine(_root, "logs");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void Provider_WhenTheDirectoryCannotBeOpened_StillCaptures()
    {
        using var logging = LoggingComposition.Create(new LoggingOptions
        {
            Default = LogLevel.Trace,
            Directory = UnopenableDirectory,
            RingCapacity = 8,
        });

        var logger = logging.Factory.CreateLogger("FallbackPlan.Test");

#pragma warning disable CA1848
        logger.LogWarning("the backup is what matters");
#pragma warning restore CA1848

        var page = logging.Ring.Read(sinceSequence: 0, maximum: 10, minimumLevel: LogLevel.Trace);
        Assert.ContainsSingle(page.Records);
    }

    [TestMethod]
    public void Provider_WhenTheDirectoryCannotBeOpened_SaysSoRatherThanThrowing()
    {
        using var logging = LoggingComposition.Create(new LoggingOptions
        {
            Default = LogLevel.Trace,
            Directory = UnopenableDirectory,
        });

        Assert.IsFalse(logging.DurableSink, "Nothing opened, so nothing durable exists to claim.");
        Assert.IsNotNull(logging.DurableSinkRefusal);
        Assert.Contains(UnopenableDirectory, logging.DurableSinkRefusal, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Provider_WhenTheDirectoryOpens_RefusesNothing()
    {
        // The anti-vacuity half: a refusal that is always present says nothing
        // about the failure it was written for.
        using var logging = LoggingComposition.Create(new LoggingOptions
        {
            Default = LogLevel.Trace,
            Directory = WritableDirectory,
        });

        Assert.IsTrue(logging.DurableSink);
        Assert.IsNull(logging.DurableSinkRefusal);
    }

    [TestMethod]
    public void Provider_WhenNoDirectoryWasAskedFor_RefusesNothing()
    {
        // Not wanting a file and not being able to have one are different
        // answers, and only the second is worth telling somebody about.
        using var logging = LoggingComposition.Create(new LoggingOptions
        {
            Default = LogLevel.Trace,
            Directory = null,
        });

        Assert.IsFalse(logging.DurableSink);
        Assert.IsNull(logging.DurableSinkRefusal);
    }
}

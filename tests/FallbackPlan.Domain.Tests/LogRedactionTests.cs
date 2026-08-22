using FallbackPlan.Domain.Diagnostics;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// What the two redaction wrappers actually render (ADR-0043 §4,
/// NFR-PRIV-002, NFR-PRIV-003).
/// </summary>
/// <remarks>
/// <para>
/// These types are the whole of the privacy guarantee at the call site: a
/// value wrapped in one is a value the renderer knows to shorten before it
/// travels, and a value not wrapped is one that goes out as written. The
/// wrapping is checked elsewhere by an architecture test; what is checked here
/// is that the shortening does something — that
/// <see cref="IRedactedValue.ToRedactedString"/> is not quietly the same
/// string as <see cref="object.ToString"/> with a prefix bolted on.
/// </para>
/// <para>
/// The redacted form is deliberately a <em>correlation handle</em> rather than
/// a secret: it must be stable, so "the same file failed twice" is still
/// legible, and it must not carry the original. Both halves are asserted,
/// because a rendering that satisfies only the first is a leak and one that
/// satisfies only the second is useless.
/// </para>
/// </remarks>
[TestClass]
public sealed class LogRedactionTests
{
    private static byte[] Bytes(byte seed) =>
        [.. Enumerable.Range(0, 32).Select(index => (byte)(index + seed))];

    [TestMethod]
    public void LogId_Rendered_IsFullHexInsideTheBoundary()
    {
        var identifier = LogId.Snapshot(Bytes(1));

        // The service's own file sits inside the trust boundary and gets the
        // whole thing: 32 bytes, lowercase, no prefix, nothing elided.
        Assert.HasCount(64, identifier.ToString());
        Assert.AreEqual(identifier.ToString().ToLowerInvariant(), identifier.ToString());
    }

    [TestMethod]
    public void LogId_Redacted_KeepsTheKindAndDropsTheRest()
    {
        var identifier = LogId.Snapshot(Bytes(1));
        var full = identifier.ToString();
        var redacted = identifier.ToRedactedString();

        Assert.StartsWith("snap#", redacted, StringComparison.Ordinal);
        Assert.IsLessThan(full.Length, redacted.Length, "A redaction no shorter than the original redacts nothing.");
        Assert.DoesNotContain(redacted, full, StringComparison.Ordinal);
    }

    [TestMethod]
    public void LogId_EachKind_SaysWhichKindItIs()
    {
        // The prefix is what survives redaction, so a remote reader can still
        // tell a snapshot from a device without learning either identifier.
        var bytes = Bytes(1);

        Assert.StartsWith("snap#", LogId.Snapshot(bytes).ToRedactedString(), StringComparison.Ordinal);
        Assert.StartsWith("set#", LogId.BackupSet(bytes).ToRedactedString(), StringComparison.Ordinal);
        Assert.StartsWith("device#", LogId.Device(bytes).ToRedactedString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public void LogId_TheSameBytesUnderDifferentKinds_AreStillTheSameIdentifier()
    {
        // Equality is over the bytes, not the label. Two records naming one
        // snapshot must correlate whichever call site wrapped it.
        var bytes = Bytes(1);

        Assert.AreEqual(LogId.Snapshot(bytes), LogId.BackupSet(bytes));
    }

    [TestMethod]
    public void LogId_WithNoBytes_RendersNoneRatherThanNothing()
    {
        var absent = new LogId("snap", default);

        Assert.AreEqual("(none)", absent.ToString());
        Assert.AreEqual("(none)", absent.ToRedactedString());
    }

    [TestMethod]
    public void LogPath_Rendered_IsThePathInsideTheBoundaryAndADigestOutsideIt()
    {
        const string Path = "/home/someone/Documents/tax return 2025.pdf";
        var wrapped = LogPath.FromString(Path);

        Assert.AreEqual(Path, wrapped.ToString());

        var redacted = wrapped.ToRedactedString();
        Assert.StartsWith("path#", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("tax", redacted, StringComparison.Ordinal);
    }

    [TestMethod]
    public void LogPath_Redacted_KeepsTheExtensionAndIsStable()
    {
        var first = LogPath.FromString("/home/someone/report.pdf").ToRedactedString();
        var again = LogPath.FromString("/home/someone/report.pdf").ToRedactedString();
        var other = LogPath.FromString("/home/someone/notes.pdf").ToRedactedString();

        // Stable, so two failures on one file read as one file; distinct, so
        // two files do not read as one. Without both, a redacted feed is
        // either a leak or noise.
        Assert.AreEqual(first, again);
        Assert.AreNotEqual(first, other);
        Assert.EndsWith(".pdf", first, StringComparison.Ordinal);
    }

    [TestMethod]
    public void LogPath_WithAnAbsurdExtension_DropsItRatherThanCarryingIt()
    {
        // The extension is kept because it is a useful, low-entropy hint. A
        // "file.thisIsNotReallyAnExtension" would carry the name itself
        // through the digest, so past sixteen characters it is dropped.
        var redacted = LogPath.FromString("/home/someone/archive.averylongextensionindeed").ToRedactedString();

        Assert.DoesNotContain("averylong", redacted, StringComparison.Ordinal);
        Assert.HasCount("path#".Length + 8, redacted);
    }

    [TestMethod]
    public void LogPath_WithNoPath_RendersNoneOnBothSides()
    {
        var absent = LogPath.FromString(null);

        Assert.AreEqual("(none)", absent.ToString());
        Assert.AreEqual("(none)", absent.ToRedactedString());
    }

    [TestMethod]
    public void LogPath_FromAPlainString_ConvertsImplicitly()
    {
        // The implicit conversion is what keeps call sites readable, so it is
        // the form most of the engine uses and the one worth pinning.
        LogPath converted = "/home/someone/report.pdf";

        Assert.AreEqual(LogPath.FromString("/home/someone/report.pdf"), converted);
    }
}

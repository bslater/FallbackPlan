using System.Globalization;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The rule a value renders by once it leaves the machine (ADR-0043 §4 as
/// amended by ADR-0081, at its home beside the types it reads by ADR-0082).
/// Establishes NFR-PRIV-003's "renders a value only when its declared type
/// clears it" for every executable, the recovery tool included.
/// </summary>
/// <remarks>
/// The service's log renderer and the recovery tool's bundle both apply this
/// rule, and the recovery tool may not reference the project the renderer
/// lives in. So the rule lives in Domain, and these tests pin it there: one
/// rule, read by type, fail-closed, with paths the only thing an opt-in
/// releases.
/// </remarks>
[TestClass]
public sealed class RedactedRenderingRuleTests
{
    private const string Folder = "/home/someone/medical/scan-2026.pdf";

    private static readonly RenderMode[] Boundary = [RenderMode.Redacted, RenderMode.RedactedWithPaths];

    [TestMethod]
    public void Render_InsideTheBoundary_IsEveryValueAsWritten()
    {
        Assert.AreEqual(Folder, RedactedRendering.Render(new LogPath(Folder), RenderMode.Full));
        Assert.AreEqual("free text", RedactedRendering.Render("free text", RenderMode.Full));
        Assert.AreEqual("1.5", RedactedRendering.Render(1.5, RenderMode.Full));
    }

    [TestMethod]
    public void Render_APath_IsADigestUnlessThePersonOptedInToPaths()
    {
        var path = new LogPath(Folder);

        var redacted = RedactedRendering.Render(path, RenderMode.Redacted);
        Assert.AreEqual(path.ToRedactedString(), redacted);
        Assert.DoesNotContain("medical", redacted, StringComparison.Ordinal);

        Assert.AreEqual(Folder, RedactedRendering.Render(path, RenderMode.RedactedWithPaths));
    }

    [TestMethod]
    public void Render_AnIdentifier_ShortensInEveryRenderingThatLeaves()
    {
        // The opt-in is to paths, never to correlation.
        var repository = RepositoryId.FromBytes([.. Enumerable.Range(0x40, RepositoryId.Size).Select(value => (byte)value)]);

        foreach (var mode in Boundary)
        {
            Assert.AreEqual("repo#40414243", RedactedRendering.Render(repository, mode), mode.ToString());
        }
    }

    [TestMethod]
    public void Render_ALabel_CrossesAsWritten()
    {
        foreach (var mode in Boundary)
        {
            Assert.AreEqual("docs", RedactedRendering.Render(LogLabel.Of("docs"), mode), mode.ToString());
        }
    }

    [TestMethod]
    public void Render_NumbersEnumsAndTimes_CrossAsWritten()
    {
        var at = new DateTimeOffset(2026, 10, 4, 7, 0, 0, TimeSpan.Zero);

        foreach (var mode in Boundary)
        {
            Assert.AreEqual("42", RedactedRendering.Render(42, mode));
            Assert.AreEqual("True", RedactedRendering.Render(true, mode));
            Assert.AreEqual(nameof(DayOfWeek.Sunday), RedactedRendering.Render(DayOfWeek.Sunday, mode));
            Assert.AreEqual(at.ToString(null, CultureInfo.InvariantCulture), RedactedRendering.Render(at, mode));
        }
    }

    [TestMethod]
    public void Render_AStringOrAnUnclassifiedType_IsWithheldUnlessThePersonOptedInToPaths()
    {
        // Fail-closed: a string declares nothing, and neither does a type
        // nobody classified. The opt-in releases them because the text no
        // type classifies is chiefly errors, and errors repeat paths.
        Assert.AreEqual(RedactedRendering.Withheld, RedactedRendering.Render(Folder, RenderMode.Redacted));
        Assert.AreEqual(RedactedRendering.Withheld, RedactedRendering.Render(new Uri("https://example.test/x"), RenderMode.Redacted));

        Assert.AreEqual(Folder, RedactedRendering.Render(Folder, RenderMode.RedactedWithPaths));
    }

    [TestMethod]
    public void Render_Nothing_SaysSoInEveryMode()
    {
        foreach (var mode in new[] { RenderMode.Full, RenderMode.Redacted, RenderMode.RedactedWithPaths })
        {
            Assert.AreEqual("(null)", RedactedRendering.Render(null, mode));
        }
    }
}

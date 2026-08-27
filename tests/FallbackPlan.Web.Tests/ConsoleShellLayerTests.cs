using System.Text.RegularExpressions;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's overlay layers — the modal dialog, the toasts, and the
/// full-screen gates — must be direct children of <c>&lt;body&gt;</c>, never
/// descendants of a container the page ever hides.
/// </summary>
/// <remarks>
/// <para>
/// This pins a defect that has now happened twice in this page's life: an
/// element shown by <c>showModal()</c> from inside a <c>[hidden]</c> subtree
/// renders at zero size — invisible — while its modal backdrop still makes
/// the whole document inert. During first-run setup the app shell is hidden
/// and the setup gate shown, so the "Passphrase accepted" report opened an
/// invisible modal over the recovery-kit step: the page looked normal and
/// every button was dead, the download included. Toasts share the trap in a
/// quieter form: a toast raised while the shell is hidden simply never
/// appears.
/// </para>
/// <para>
/// The rule is structural, so the test is structural: nothing here needs a
/// browser, and any future move of these elements back inside a hideable
/// container fails with the reason spelled out rather than as a wizard
/// nobody can click.
/// </para>
/// </remarks>
[TestClass]
public sealed class ConsoleShellLayerTests
{
    private static string IndexHtml()
    {
        // Walk up from the test assembly to the repository root, the same
        // way a person finds the file: the solution file marks the root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FallbackPlan.slnx")))
        {
            directory = directory.Parent!;
        }

        Assert.IsNotNull(directory, "the repository root (FallbackPlan.slnx) was not found above the test assembly");
        return File.ReadAllText(Path.Combine(
            directory.FullName, "src", "FallbackPlan.Web", "wwwroot", "index.html"));
    }

    /// <summary>
    /// How many unclosed <c>&lt;div&gt;</c> elements enclose
    /// <paramref name="elementMarker"/> — 0 means a direct child of
    /// <c>&lt;body&gt;</c>, because this page nests exclusively with divs.
    /// </summary>
    private static int DivDepthOf(string html, string elementMarker)
    {
        // Comments can legally contain markup; they are not structure.
        var text = Regex.Replace(html, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var at = text.IndexOf(elementMarker, StringComparison.Ordinal);
        Assert.IsTrue(at >= 0, $"index.html no longer contains '{elementMarker}'");

        // Depth is measured at the element's own opening '<', so a div-based
        // element does not count its own tag as an enclosure.
        var prefix = text[..text.LastIndexOf('<', at)];
        return Regex.Count(prefix, "<div[\\s>]") - Regex.Count(prefix, "</div>");
    }

    [TestMethod]
    public void TheModalDialog_IsADirectChildOfBody_NeverInsideTheHideableShell()
    {
        Assert.AreEqual(
            0, DivDepthOf(IndexHtml(), "id=\"dialog\""),
            "the <dialog> sits inside a container the page can hide. showModal() from a hidden "
            + "subtree is an invisible modal whose backdrop swallows every click — the setup "
            + "wizard's recovery-kit step went dead exactly this way. Keep it a direct child of "
            + "<body>.");
    }

    [TestMethod]
    public void TheToasts_AreADirectChildOfBody_NeverInsideTheHideableShell()
    {
        Assert.AreEqual(
            0, DivDepthOf(IndexHtml(), "id=\"toasts\""),
            "the toast layer sits inside a container the page can hide, so a toast raised during "
            + "setup or sign-in silently never appears. Keep it a direct child of <body>.");
    }

    [TestMethod]
    public void TheGates_AreDirectChildrenOfBody()
    {
        // The inverse arrangement of the same rule: a gate inside the shell
        // it replaces could never be shown while the shell is hidden.
        foreach (var gate in new[] { "id=\"setup\"", "id=\"signin\"", "id=\"gate\"" })
        {
            Assert.AreEqual(0, DivDepthOf(IndexHtml(), gate), $"{gate} must be a direct child of <body>");
        }
    }
}

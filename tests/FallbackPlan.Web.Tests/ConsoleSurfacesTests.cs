using System.Net;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The page-level surfaces — the shared dialog and the toast host — must not
/// live inside <c>#app</c>. The app panel is hidden whenever a gate owns the
/// screen (first-run setup, sign-in), and a modal dialog inside a
/// <c>display:none</c> ancestor is the worst of both worlds: the top layer
/// paints nothing, but the modal state still makes the entire document inert.
/// That exact nesting froze the setup ceremony's kit page and then the
/// sign-in screen — enabled-looking buttons that swallowed every click, with
/// no visible cause. The toasts share the constraint for the simpler reason:
/// a warning nobody can see is not a warning.
/// </summary>
[TestClass]
public sealed class ConsoleSurfacesTests
{
    [TestMethod]
    public async Task IndexPage_DialogAndToasts_LiveOutsideTheHideableAppContainer()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", harness.Auth.Token);
        using var response = await harness.Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var appSpan = ElementSpan(html, "<div id=\"app\"");

        Assert.DoesNotContain("id=\"dialog\"", appSpan, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"toasts\"", appSpan, StringComparison.Ordinal);

        // And both still exist — outside, where no gate can hide them.
        Assert.Contains("id=\"dialog\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"toasts\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>div</c> element starting at <paramref name="opening"/> with its
    /// entire subtree, by balanced tag count. A depth scan over the static
    /// page rather than an HTML parser: the page is ours, every div is
    /// closed, and <c>&lt;div</c> does not prefix <c>&lt;dialog</c>.
    /// </summary>
    private static string ElementSpan(string html, string opening)
    {
        var start = html.IndexOf(opening, StringComparison.Ordinal);
        Assert.AreNotEqual(-1, start, $"the page no longer contains '{opening}'");

        var depth = 0;
        var index = start;
        while (index < html.Length)
        {
            var open = html.IndexOf("<div", index, StringComparison.Ordinal);
            var close = html.IndexOf("</div>", index, StringComparison.Ordinal);
            Assert.AreNotEqual(-1, close, "an unclosed <div> means the scan — or the page — is broken");

            if (open >= 0 && open < close)
            {
                depth++;
                index = open + "<div".Length;
                continue;
            }

            depth--;
            index = close + "</div>".Length;
            if (depth == 0)
            {
                return html[start..index];
            }
        }

        Assert.Fail("the element never closed");
        return string.Empty;
    }
}

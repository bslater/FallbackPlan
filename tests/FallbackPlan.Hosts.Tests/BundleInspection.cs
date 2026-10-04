using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Reads a diagnostic bundle back the way whoever receives it would, for the
/// tests that prove what a bundle may carry (NFR-PRIV-003): the service's
/// (ADR-0081) and the recovery tool's (ADR-0082).
/// </summary>
internal static class BundleInspection
{
    /// <summary>Every entry of a bundle, by name, as text.</summary>
    /// <param name="zip">The bundle's bytes.</param>
    public static Dictionary<string, string> Open(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            },
            StringComparer.Ordinal);
    }

    /// <summary>
    /// An entry as a recipient reads it: its text and, for a JSON entry, every
    /// string the text decodes to. JSON escapes each separator of a Windows
    /// path, so a search of the raw text alone never finds the path it holds.
    /// </summary>
    /// <param name="entries">The bundle's entries.</param>
    /// <param name="name">The entry to read.</param>
    public static string Readable(Dictionary<string, string> entries, string name)
    {
        var text = entries[name];
        if (!name.EndsWith(".json", StringComparison.Ordinal))
        {
            return text;
        }

        using var document = JsonDocument.Parse(text);
        return string.Join('\n', [text, .. Decoded(document.RootElement)]);
    }

    /// <summary>
    /// Asserts that <paramref name="value"/> is in no entry, read either way,
    /// in no entry's name, and not in the bundle's own file name when the
    /// bundle chose one.
    /// </summary>
    /// <param name="what">What the value is, for the failure message.</param>
    /// <param name="value">The value that must not travel.</param>
    /// <param name="fileName">The name the bundle gave itself, if it gave one.</param>
    /// <param name="entries">The bundle's entries.</param>
    public static void AssertNowhere(string what, string value, string? fileName, Dictionary<string, string> entries)
    {
        if (fileName is not null)
        {
            Assert.DoesNotContain(value, fileName, StringComparison.Ordinal, $"The file name carries the {what}.");
        }

        foreach (var name in entries.Keys)
        {
            Assert.DoesNotContain(value, name, StringComparison.Ordinal, $"An entry's name carries the {what}.");
            Assert.DoesNotContain(value, Readable(entries, name), StringComparison.Ordinal, $"{name} carries the {what}.");
        }
    }

    private static IEnumerable<string> Decoded(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Decoded),
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Decoded(property.Value).Prepend(property.Name)),
        _ => [],
    };
}

using System.Globalization;
using System.Text;
using Bodu;
using FallbackPlan.Domain.Diagnostics;

namespace FallbackPlan.Diagnostics;

/// <summary>How much of a record the destination is allowed to see.</summary>
public enum RenderMode
{
    /// <summary>
    /// Everything, as logged. For a sink inside the trust boundary: the
    /// service's own log file, on the machine that already holds the files.
    /// </summary>
    Full = 0,

    /// <summary>
    /// Values whose declared type implements
    /// <see cref="IRedactedValue"/> render through it. For anything crossing
    /// the boundary — a client feed, an exported bundle (NFR-PRIV-003).
    /// </summary>
    Redacted = 1,
}

/// <summary>
/// Turns a <see cref="LogRecord"/> back into a line, applying the destination's
/// rule (ADR-0043 §4).
/// </summary>
/// <remarks>
/// This is the single place the redaction decision is taken. It is deliberately
/// not a filter over finished text: it substitutes into the template from
/// typed values, so a value is redacted because of what it <em>is</em>, never
/// because of what it looks like.
/// </remarks>
public static class LogRecordRenderer
{
    /// <summary>Renders one record for a destination.</summary>
    /// <param name="record">The captured record.</param>
    /// <param name="mode">What the destination may see.</param>
    public static string Render(LogRecord record, RenderMode mode)
    {
        ThrowHelper.ThrowIfNull(record);

        var template = FindTemplate(record.Values);
        var message = template is null
            ? DescribeWithoutTemplate(record.Values, mode)
            : Substitute(template, record.Values, mode);

        if (record.ExceptionType is null)
        {
            return message;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{message} [{record.ExceptionType}: {record.ExceptionMessage}]");
    }

    /// <summary>
    /// Renders one value under the destination's rule. Public because the
    /// wire projection renders values individually rather than as a line.
    /// </summary>
    /// <param name="value">The value as logged.</param>
    /// <param name="mode">What the destination may see.</param>
    public static string RenderValue(object? value, RenderMode mode) => value switch
    {
        null => "(null)",

        // Redaction by DECLARED TYPE. A secret is not handled here at all: its
        // own ToString already redacts, at the point it was declared, so it is
        // safe in either mode without this method knowing it exists.
        IRedactedValue redactable when mode == RenderMode.Redacted => redactable.ToRedactedString(),

        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "(null)",
    };

    private static string? FindTemplate(IReadOnlyList<KeyValuePair<string, object?>> values)
    {
        for (var index = values.Count - 1; index >= 0; index--)
        {
            if (string.Equals(values[index].Key, LogRecord.OriginalFormatKey, StringComparison.Ordinal))
            {
                return values[index].Value as string;
            }
        }

        return null;
    }

    /// <summary>
    /// Fills the template's named holes from the record's values. Unmatched
    /// holes are left as written rather than dropped: a diagnostic that
    /// silently loses a field is worse than one that shows which field it could
    /// not fill.
    /// </summary>
    private static string Substitute(
        string template, IReadOnlyList<KeyValuePair<string, object?>> values, RenderMode mode)
    {
        var builder = new StringBuilder(template.Length + 32);
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open);
            if (close < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);

            // A hole may carry a format specifier — {Fill:P1} — which is the
            // name up to the colon.
            var hole = template.AsSpan(open + 1, close - open - 1);
            var colon = hole.IndexOf(':');
            var name = colon < 0 ? hole : hole[..colon];

            if (TryFind(values, name, out var value))
            {
                builder.Append(RenderValue(value, mode));
            }
            else
            {
                builder.Append(template, open, close - open + 1);
            }

            index = close + 1;
        }

        return builder.ToString();
    }

    private static bool TryFind(
        IReadOnlyList<KeyValuePair<string, object?>> values, ReadOnlySpan<char> name, out object? value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (name.Equals(values[index].Key, StringComparison.Ordinal))
            {
                value = values[index].Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// A record logged without a template — nothing in this codebase does, but
    /// a third-party library sharing the factory might, and dropping it would
    /// be the wrong kind of quiet.
    /// </summary>
    private static string DescribeWithoutTemplate(
        IReadOnlyList<KeyValuePair<string, object?>> values, RenderMode mode)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index].Key, LogRecord.OriginalFormatKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(values[index].Key).Append('=').Append(RenderValue(values[index].Value, mode));
        }

        return builder.ToString();
    }
}

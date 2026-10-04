using System.Globalization;
using System.Text;
using Bodu;
using FallbackPlan.Domain.Diagnostics;

namespace FallbackPlan.Diagnostics;

/// <summary>
/// Turns a <see cref="LogRecord"/> back into a line, applying the destination's
/// rule (ADR-0043 §4 as amended by ADR-0081).
/// </summary>
/// <remarks>
/// The redaction decision is <see cref="RedactedRendering"/>'s, in Domain
/// beside the types it reads, so that the recovery tool's bundle applies the
/// same rule (ADR-0082). This applies it to records: it substitutes into the
/// template from typed values, so a value is redacted because of what it
/// <em>is</em>, never because of what it looks like.
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
            $"{message} [{record.ExceptionType}: {RenderExceptionMessage(record, mode)}]");
    }

    /// <summary>
    /// Renders a record as one line of the service's own log file: sequence,
    /// time, level, event id, category, message. The diagnostic bundle's log
    /// uses the same line, so the two line up record for record — once one of
    /// them is redacted, the sequence leading each line is all they share.
    /// </summary>
    /// <param name="record">The captured record.</param>
    /// <param name="mode">What the destination may see.</param>
    public static string RenderLine(LogRecord record, RenderMode mode)
    {
        ThrowHelper.ThrowIfNull(record);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{record.Sequence,-8}  " +
            $"{DateTimeOffset.FromUnixTimeMilliseconds(record.TimestampUnixMilliseconds):u}  " +
            $"{LoggingOptions.NameOf(record.Level),-11}  {record.EventId,-5}  {record.Category}  " +
            $"{Render(record, mode)}");
    }

    /// <summary>
    /// An exception's message under the destination's rule, or null when the
    /// record carries no exception.
    /// </summary>
    /// <remarks>
    /// The message is text no type classifies, and the platform's own words
    /// for an unreadable folder are "Access to the path '…' is denied." — a
    /// full path, beside a hole that just hashed it. Only the exception's
    /// type, which is the code's, crosses a redacted boundary.
    /// </remarks>
    /// <param name="record">The captured record.</param>
    /// <param name="mode">What the destination may see.</param>
    public static string? RenderExceptionMessage(LogRecord record, RenderMode mode)
    {
        ThrowHelper.ThrowIfNull(record);

        if (record.ExceptionType is null)
        {
            return null;
        }

        return mode == RenderMode.Redacted ? RedactedRendering.Withheld : record.ExceptionMessage;
    }

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
                builder.Append(RedactedRendering.Render(value, mode));
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

            builder.Append(values[index].Key).Append('=').Append(RedactedRendering.Render(values[index].Value, mode));
        }

        return builder.ToString();
    }
}

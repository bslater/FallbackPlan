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
    /// Only what a declared type clears (ADR-0081): values whose type
    /// implements <see cref="IRedactedValue"/> render through it, a
    /// <see cref="LogLabel"/>, numbers, enums and times render as written,
    /// and anything else — a bare string, an exception's message — is
    /// withheld. For anything crossing the boundary: a paired console's
    /// feed, a diagnostic bundle (NFR-PRIV-003).
    /// </summary>
    Redacted = 1,

    /// <summary>
    /// <see cref="Redacted"/>, except that paths and the text no type
    /// classifies render as written. For a diagnostic bundle whose person
    /// opted in to paths for that bundle (architecture 10 §4, ADR-0081):
    /// identifiers still shorten, because the opt-in is to paths and not
    /// to correlation.
    /// </summary>
    RedactedWithPaths = 2,
}

/// <summary>
/// Turns a <see cref="LogRecord"/> back into a line, applying the destination's
/// rule (ADR-0043 §4 as amended by ADR-0081).
/// </summary>
/// <remarks>
/// This is the single place the redaction decision is taken. It is deliberately
/// not a filter over finished text: it substitutes into the template from
/// typed values, so a value is redacted because of what it <em>is</em>, never
/// because of what it looks like.
/// </remarks>
public static class LogRecordRenderer
{
    /// <summary>
    /// What a redacted rendering writes in place of a value no type cleared to
    /// cross: a bare string, a value of an unclassified type, an exception's
    /// message.
    /// </summary>
    public const string Withheld = "(withheld)";

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

        return mode == RenderMode.Redacted ? Withheld : record.ExceptionMessage;
    }

    /// <summary>
    /// Renders one value under the destination's rule. Public because the
    /// wire projection and the diagnostic bundle render values individually
    /// rather than as a line.
    /// </summary>
    /// <param name="value">The value as logged.</param>
    /// <param name="mode">What the destination may see.</param>
    /// <remarks>
    /// Fail-closed (ADR-0081): across the boundary a value renders only when
    /// its declared type says how. A secret is not handled here at all: its
    /// own ToString already redacts, at the point it was declared, so it is
    /// safe in every mode without this method knowing it exists.
    /// </remarks>
    public static string RenderValue(object? value, RenderMode mode) => value switch
    {
        null => "(null)",
        _ when mode == RenderMode.Full => AsWritten(value),

        // Redaction by DECLARED TYPE.
        LogPath path when mode == RenderMode.RedactedWithPaths => path.ToString(),
        IRedactedValue redactable => redactable.ToRedactedString(),
        LogLabel label => label.ToString(),
        bool or Enum or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or TimeSpan or DateTime or DateTimeOffset => AsWritten(value),

        // A string declares nothing, and neither does a type nobody
        // classified; only a person's opt-in to paths lets them through.
        _ when mode == RenderMode.RedactedWithPaths => AsWritten(value),
        _ => Withheld,
    };

    private static string AsWritten(object value) => value switch
    {
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

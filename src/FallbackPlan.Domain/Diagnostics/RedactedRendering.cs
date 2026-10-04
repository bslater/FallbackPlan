using System.Globalization;

namespace FallbackPlan.Domain.Diagnostics;

/// <summary>How much of a value the destination is allowed to see.</summary>
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
/// The rule a value renders by once it leaves the machine (ADR-0043 §4 as
/// amended by ADR-0081 and ADR-0082).
/// </summary>
/// <remarks>
/// <para>
/// This is the single place the redaction decision is taken. It lives here,
/// beside the types it reads, because two executables apply it: the service,
/// whose log renderer substitutes it into each record's template, and the
/// standalone recovery tool, whose diagnostic bundle renders each field
/// through it. The recovery tool may not reference the project that renders
/// the log (architecture 11 §2), and a second copy of the rule would be free
/// to drift from the first.
/// </para>
/// <para>
/// It is deliberately not a filter over finished text: a value is redacted
/// because of what it <em>is</em>, never because of what it looks like.
/// </para>
/// </remarks>
public static class RedactedRendering
{
    /// <summary>
    /// What a redacted rendering writes in place of a value no type cleared to
    /// cross: a bare string, a value of an unclassified type, an exception's
    /// message.
    /// </summary>
    public const string Withheld = "(withheld)";

    /// <summary>Renders one value under the destination's rule.</summary>
    /// <param name="value">The value as logged or recorded.</param>
    /// <param name="mode">What the destination may see.</param>
    /// <remarks>
    /// Fail-closed (ADR-0081): across the boundary a value renders only when
    /// its declared type says how. A secret is not handled here at all: its
    /// own ToString already redacts, at the point it was declared, so it is
    /// safe in every mode without this method knowing it exists.
    /// </remarks>
    public static string Render(object? value, RenderMode mode) => value switch
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
}

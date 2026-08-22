using Microsoft.Extensions.Logging;

namespace FallbackPlan.Diagnostics;

/// <summary>
/// One captured diagnostic, held as <b>structured state</b> rather than as a
/// rendered line (ADR-0043 §4).
/// </summary>
/// <remarks>
/// <para>
/// Keeping the name/value pairs instead of a string is what lets one buffer
/// serve two destinations with different rules: the service's own log file
/// renders plaintext because it sits inside the trust boundary, and anything
/// travelling to a client renders redacted. A record that had already been
/// flattened to a sentence could only be redacted by pattern-matching it back
/// apart, which is exactly the string-matching approach NFR-SEC-006 forbids.
/// </para>
/// <para>
/// <see cref="Values"/> holds the values as logged, so a value whose declared
/// type implements <see cref="Domain.Diagnostics.IRedactedValue"/> is still an
/// instance of that type when the renderer reaches it. Secrets are already
/// safe by their own <see cref="object.ToString"/> and need no handling here.
/// </para>
/// </remarks>
/// <param name="Sequence">Monotonic within one buffer; a gap means records were dropped.</param>
/// <param name="TimestampUnixMilliseconds">When the record was written.</param>
/// <param name="Level">Its severity.</param>
/// <param name="EventId">The stable identifier from its <c>[LoggerMessage]</c> declaration.</param>
/// <param name="Category">The logger category, normally the declaring type's full name.</param>
/// <param name="Values">The template's named holes and their values, as logged.</param>
/// <param name="ExceptionType">The exception's type name, when one was attached.</param>
/// <param name="ExceptionMessage">The exception's message, when one was attached.</param>
public sealed record LogRecord(
    long Sequence,
    long TimestampUnixMilliseconds,
    LogLevel Level,
    int EventId,
    string Category,
    IReadOnlyList<KeyValuePair<string, object?>> Values,
    string? ExceptionType,
    string? ExceptionMessage)
{
    /// <summary>
    /// The message template's own hole, as the logging source generator writes
    /// it into structured state.
    /// </summary>
    public const string OriginalFormatKey = "{OriginalFormat}";
}

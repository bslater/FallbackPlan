namespace FallbackPlan.Domain.Diagnostics;

/// <summary>
/// An identifier on its way into a log record, rendered only if the record is
/// actually written (ADR-0043 §4).
/// </summary>
/// <remarks>
/// <para>
/// Several identifiers travel as bare <see cref="ReadOnlyMemory{T}"/> rather
/// than as one of Domain's identifier structs — a snapshot id, a device id, a
/// backup-set id. Hex-encoding one at the call site costs an allocation whether
/// or not the level is enabled, which CA1873 flags and which matters in a loop
/// that runs once per file. Wrapping defers the encoding to
/// <see cref="ToString"/>, which the logging pipeline calls only after deciding
/// to keep the record.
/// </para>
/// <para>
/// Others are already held as text — a set's hex id from the configuration, a
/// peer's base32 fingerprint (ADR-0081). Wrapping the text classifies it the
/// same way, and an identifier wrapped from its text equals and redacts
/// exactly as the same identifier wrapped from its bytes.
/// </para>
/// <para>
/// It is also the declaration that makes such an identifier redactable: these
/// are exactly the correlatable identifiers NFR-PRIV-002 keeps off the wire, so
/// the wrapper carries the classification the raw value could not.
/// </para>
/// </remarks>
public readonly struct LogId : IRedactedValue, IEquatable<LogId>
{
    private const int RedactedChars = 8;

    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly string? _text;
    private readonly string _prefix;

    /// <summary>Wraps raw identifier bytes for logging.</summary>
    /// <param name="prefix">The short kind name used when redacted, e.g. <c>snap</c>.</param>
    /// <param name="bytes">The identifier's bytes.</param>
    public LogId(string prefix, ReadOnlyMemory<byte> bytes)
    {
        _prefix = prefix;
        _bytes = bytes;
    }

    private LogId(string prefix, string text)
    {
        _prefix = prefix;
        _text = text;
    }

    /// <summary>Wraps an identifier already rendered as text for logging.</summary>
    /// <param name="prefix">The short kind name used when redacted, e.g. <c>peer</c>.</param>
    /// <param name="text">The identifier's rendering, or <see langword="null"/> when there is none.</param>
    /// <remarks>
    /// A factory rather than a public constructor, so <c>new LogId(prefix, default)</c>
    /// keeps meaning the empty byte form it always meant.
    /// </remarks>
    public static LogId FromText(string prefix, string? text) => new(prefix, text ?? string.Empty);

    /// <summary>A snapshot identifier.</summary>
    /// <param name="bytes">The identifier's bytes.</param>
    public static LogId Snapshot(ReadOnlyMemory<byte> bytes) => new("snap", bytes);

    /// <summary>A snapshot identifier held as hex.</summary>
    /// <param name="hex">The identifier's rendering.</param>
    public static LogId Snapshot(string? hex) => FromText("snap", hex);

    /// <summary>A backup-set identifier.</summary>
    /// <param name="bytes">The identifier's bytes.</param>
    public static LogId BackupSet(ReadOnlyMemory<byte> bytes) => new("set", bytes);

    /// <summary>A backup-set identifier held as hex, as the configuration holds it.</summary>
    /// <param name="hex">The identifier's rendering.</param>
    public static LogId BackupSet(string? hex) => FromText("set", hex);

    /// <summary>A device identifier.</summary>
    /// <param name="bytes">The identifier's bytes.</param>
    public static LogId Device(ReadOnlyMemory<byte> bytes) => new("device", bytes);

    /// <summary>A paired device's fingerprint, as the pairing records hold it.</summary>
    /// <param name="fingerprint">The fingerprint's rendering.</param>
    public static LogId Fingerprint(string? fingerprint) => FromText("peer", fingerprint);

    /// <summary>A repository identity held as hex.</summary>
    /// <param name="hex">The identifier's rendering.</param>
    public static LogId Repository(string? hex) => FromText("repo", hex);

    /// <summary>A writer identity held as hex.</summary>
    /// <param name="hex">The identifier's rendering.</param>
    public static LogId Writer(string? hex) => FromText("writer", hex);

    /// <summary>A destination's configured identity held as hex.</summary>
    /// <param name="hex">The identifier's rendering.</param>
    public static LogId Destination(string? hex) => FromText("dest", hex);

    private bool IsEmpty => _text is null ? _bytes.IsEmpty : _text.Length == 0;

    /// <summary>The full rendering, for a sink inside the trust boundary.</summary>
    public override string ToString() =>
        IsEmpty ? "(none)" : _text ?? Convert.ToHexStringLower(_bytes.Span);

    /// <inheritdoc />
    public string ToRedactedString() =>
        IsEmpty ? "(none)" : Redaction.Shorten(_prefix ?? "id", ToString(), RedactedChars);

    /// <inheritdoc />
    public bool Equals(LogId other) =>
        _text is null && other._text is null
            ? _bytes.Span.SequenceEqual(other._bytes.Span)
            : string.Equals(ToString(), other.ToString(), StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LogId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    /// <summary>Whether two wrapped identifiers are the same identifier.</summary>
    public static bool operator ==(LogId left, LogId right) => left.Equals(right);

    /// <summary>Whether two wrapped identifiers are different identifiers.</summary>
    public static bool operator !=(LogId left, LogId right) => !left.Equals(right);
}

namespace FallbackPlan.Domain.Diagnostics;

/// <summary>
/// A raw byte identifier on its way into a log record, rendered only if the
/// record is actually written (ADR-0043 §4).
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
/// It is also the declaration that makes such an identifier redactable: these
/// are exactly the correlatable identifiers NFR-PRIV-002 keeps off the wire, so
/// the wrapper carries the classification the raw bytes could not.
/// </para>
/// </remarks>
public readonly struct LogId : IRedactedValue, IEquatable<LogId>
{
    private const int RedactedChars = 8;

    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly string _prefix;

    /// <summary>Wraps raw identifier bytes for logging.</summary>
    /// <param name="prefix">The short kind name used when redacted, e.g. <c>snap</c>.</param>
    /// <param name="bytes">The identifier's bytes.</param>
    public LogId(string prefix, ReadOnlyMemory<byte> bytes)
    {
        _prefix = prefix;
        _bytes = bytes;
    }

    /// <summary>A snapshot identifier.</summary>
    /// <param name="bytes">The identifier's bytes.</param>
    public static LogId Snapshot(ReadOnlyMemory<byte> bytes) => new("snap", bytes);

    /// <summary>A backup-set identifier.</summary>
    /// <param name="bytes">The identifier's bytes.</param>
    public static LogId BackupSet(ReadOnlyMemory<byte> bytes) => new("set", bytes);

    /// <summary>A device identifier.</summary>
    /// <param name="bytes">The identifier's bytes.</param>
    public static LogId Device(ReadOnlyMemory<byte> bytes) => new("device", bytes);

    /// <summary>The full lowercase hex rendering, for a sink inside the trust boundary.</summary>
    public override string ToString() =>
        _bytes.IsEmpty ? "(none)" : Convert.ToHexStringLower(_bytes.Span);

    /// <inheritdoc />
    public string ToRedactedString() =>
        _bytes.IsEmpty ? "(none)" : Redaction.Shorten(_prefix ?? "id", ToString(), RedactedChars);

    /// <inheritdoc />
    public bool Equals(LogId other) => _bytes.Span.SequenceEqual(other._bytes.Span);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LogId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes.Span);
        return hash.ToHashCode();
    }

    /// <summary>Whether two wrapped identifiers hold the same bytes.</summary>
    public static bool operator ==(LogId left, LogId right) => left.Equals(right);

    /// <summary>Whether two wrapped identifiers hold different bytes.</summary>
    public static bool operator !=(LogId left, LogId right) => !left.Equals(right);
}

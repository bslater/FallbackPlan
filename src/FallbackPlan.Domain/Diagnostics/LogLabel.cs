namespace FallbackPlan.Domain.Diagnostics;

/// <summary>
/// A short string a call site declares safe in every rendering (ADR-0043 §4
/// as amended by ADR-0081): a word from the code's own vocabulary — an
/// outcome, a role, a command's name, a schedule as written — or a name a
/// person gave something: a set, a destination, a paired device, an account.
/// </summary>
/// <remarks>
/// <para>
/// A redacted rendering withholds every <see cref="string"/>, because a string
/// declares nothing about what it holds, and the commonest one in a log is an
/// exception's message with somebody's path inside it. Wrapping a string in
/// this type is how a call site says it knows better: these words come from
/// the code, or are the names the person chose to see on a screen, and a
/// paired console or a diagnostic bundle may have them as written.
/// </para>
/// <para>
/// There is deliberately no implicit conversion from <see cref="string"/>.
/// Declaring a string safe to send off the machine is a decision, and an
/// implicit conversion would make it one nobody took. Nor is this an
/// <see cref="IRedactedValue"/>: it has no redacted form, because it needs
/// none, and a reader of that interface should never be told a label hides
/// anything.
/// </para>
/// </remarks>
public readonly struct LogLabel : IEquatable<LogLabel>
{
    private readonly string? _text;

    /// <summary>Wraps words safe in every rendering.</summary>
    /// <param name="text">The words, or <see langword="null"/> when there are none.</param>
    public LogLabel(string? text) => _text = text;

    /// <summary>Wraps words safe in every rendering.</summary>
    /// <param name="text">The words, or <see langword="null"/> when there are none.</param>
    public static LogLabel Of(string? text) => new(text);

    /// <summary>The words as written, in every rendering.</summary>
    public override string ToString() => _text ?? "(none)";

    /// <inheritdoc />
    public bool Equals(LogLabel other) => string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LogLabel other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _text is null ? 0 : StringComparer.Ordinal.GetHashCode(_text);

    /// <summary>Whether two labels hold the same words.</summary>
    public static bool operator ==(LogLabel left, LogLabel right) => left.Equals(right);

    /// <summary>Whether two labels hold different words.</summary>
    public static bool operator !=(LogLabel left, LogLabel right) => !left.Equals(right);
}

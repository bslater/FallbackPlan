using FallbackPlan.Storage.Abstractions.Resources;

namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// A listing prefix over the flat store namespace (specification 01 §2). A
/// prefix matches every key whose string form starts with it — it may end
/// mid-component, and the empty prefix matches everything.
/// </summary>
public readonly struct ObjectPrefix : IEquatable<ObjectPrefix>
{
    /// <summary>The prefix matching every key.</summary>
    public static ObjectPrefix All => default;

    private readonly string? _value;

    private ObjectPrefix(string value) => _value = value;

    /// <summary>The prefix string.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Parses a prefix, throwing on characters no key can contain.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> cannot prefix any valid key.</exception>
    public static ObjectPrefix Parse(string value)
    {
        if (!TryParse(value, out var prefix))
        {
            throw new ArgumentException(Strings.FormatObjectPrefix_CannotPrefixAnyValidObject(value), nameof(value));
        }

        return prefix;
    }

    /// <summary>Attempts to parse a prefix.</summary>
    public static bool TryParse(string? value, out ObjectPrefix prefix)
    {
        prefix = default;

        if (value is null || value.Length > ObjectKey.MaximumLength)
        {
            return false;
        }

        if (value.StartsWith('/'))
        {
            return false;
        }

        foreach (var character in value)
        {
            var valid = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' or '/';
            if (!valid)
            {
                return false;
            }
        }

        prefix = new ObjectPrefix(value);
        return true;
    }

    /// <summary>Returns whether <paramref name="key"/> falls under this prefix.</summary>
    public bool Matches(ObjectKey key) => key.Value.StartsWith(Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public bool Equals(ObjectPrefix other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ObjectPrefix other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator ==(ObjectPrefix left, ObjectPrefix right) => left.Equals(right);

    public static bool operator !=(ObjectPrefix left, ObjectPrefix right) => !left.Equals(right);
}

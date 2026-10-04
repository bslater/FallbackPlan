using FallbackPlan.Storage.Abstractions.Resources;

namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// A validated store key: the address of one immutable object in the flat
/// store namespace (specification 01 §2). The grammar is deliberately narrow —
/// lowercase alphanumerics plus <c>.</c>, <c>_</c>, <c>-</c> in
/// <c>/</c>-separated components, no component starting with a dot — so path
/// traversal (<c>..</c>) and hidden-file collisions are unconstructible rather
/// than filtered (NFR-PORT-004).
/// </summary>
public readonly struct ObjectKey : IEquatable<ObjectKey>, IComparable<ObjectKey>, Domain.Diagnostics.IRedactedValue
{
    /// <summary>The maximum total key length in characters.</summary>
    public const int MaximumLength = 1024;

    /// <summary>The maximum length of one component.</summary>
    public const int MaximumComponentLength = 255;

    private readonly string? _value;

    private ObjectKey(string value) => _value = value;

    /// <summary>The canonical relative key, <c>/</c>-separated.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>The key's components in order.</summary>
    public IReadOnlyList<string> Components => Value.Length == 0 ? [] : Value.Split('/');

    /// <summary>Parses a key, throwing on any grammar violation.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> violates the key grammar.</exception>
    public static ObjectKey Parse(string value)
    {
        if (!TryParse(value, out var key))
        {
            throw new ArgumentException(Strings.FormatObjectKey_NotValidObjectKey(value), nameof(value));
        }

        return key;
    }

    /// <summary>Attempts to parse a key.</summary>
    public static bool TryParse(string? value, out ObjectKey key)
    {
        key = default;

        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength)
        {
            return false;
        }

        foreach (var component in value.Split('/'))
        {
            if (component.Length is 0 or > MaximumComponentLength)
            {
                return false;
            }

            if (component[0] == '.')
            {
                return false;
            }

            foreach (var character in component)
            {
                var valid = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
                if (!valid)
                {
                    return false;
                }
            }
        }

        key = new ObjectKey(value);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(ObjectKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ObjectKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public int CompareTo(ObjectKey other) => string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    /// The rendering that may leave the machine (ADR-0043 §4, ADR-0081): the
    /// key's kind and a short handle on its leaf. The leaf is a blob's or a
    /// delta's identity, which correlates this store with every other copy
    /// of it (NFR-PRIV-002); the components between are layout, not data.
    /// </summary>
    public string ToRedactedString()
    {
        var value = Value;
        var first = value.IndexOf('/', StringComparison.Ordinal);
        if (first < 0)
        {
            return Domain.Diagnostics.Redaction.Shorten("key", value, 8);
        }

        var leaf = value.LastIndexOf('/');
        return Domain.Diagnostics.Redaction.Shorten(value[..first], value[(leaf + 1)..], 8);
    }

    public static bool operator ==(ObjectKey left, ObjectKey right) => left.Equals(right);

    public static bool operator !=(ObjectKey left, ObjectKey right) => !left.Equals(right);

    public static bool operator <(ObjectKey left, ObjectKey right) => left.CompareTo(right) < 0;

    public static bool operator <=(ObjectKey left, ObjectKey right) => left.CompareTo(right) <= 0;

    public static bool operator >(ObjectKey left, ObjectKey right) => left.CompareTo(right) > 0;

    public static bool operator >=(ObjectKey left, ObjectKey right) => left.CompareTo(right) >= 0;
}

using System.Security.Cryptography;
using System.Text;

namespace FallbackPlan.Domain.Diagnostics;

/// <summary>
/// A filesystem path on its way into a log record (ADR-0043 §4).
/// </summary>
/// <remarks>
/// <para>
/// Paths are simultaneously the most useful thing a diagnostic can say and,
/// as architecture 10 §4 puts it, the thing that "frequently reveal[s] more
/// about a person than file contents do". Wrapping one in this type is how a
/// call site declares which it is dealing with, so the decision of what to
/// render is taken once, at the boundary, rather than at every
/// <c>[LoggerMessage]</c> hole.
/// </para>
/// <para>
/// <see cref="ToString"/> is the full path and is what the service's own log
/// file records — that file sits inside the trust boundary, on the machine
/// that already holds the files themselves.
/// <see cref="ToRedactedString"/> is what travels: a stable short digest of
/// the path with the extension kept, so an operator reading a redacted feed
/// can still tell "the same file failed twice" from "two different files
/// failed" without learning either name.
/// </para>
/// <para>
/// The digest is <b>not</b> a security boundary against a determined guesser —
/// a short hash of a guessable path is guessable. It is a correlation handle
/// that removes casual exposure, which is the exposure that actually happens
/// when a support log is pasted into a ticket.
/// </para>
/// </remarks>
public readonly struct LogPath : IRedactedValue, IEquatable<LogPath>
{
    /// <summary>The number of hex characters in the redacted digest.</summary>
    private const int DigestChars = 8;

    private readonly string? _path;

    /// <summary>Wraps a path for logging.</summary>
    /// <param name="path">The path, or <see langword="null"/> when there is none.</param>
    public LogPath(string? path) => _path = path;

    /// <summary>The path as written, for a sink inside the trust boundary.</summary>
    public override string ToString() => _path ?? "(none)";

    /// <inheritdoc />
    public string ToRedactedString()
    {
        if (string.IsNullOrEmpty(_path))
        {
            return "(none)";
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(_path), digest);

        var extension = Path.GetExtension(_path);
        var handle = Convert.ToHexStringLower(digest[..(DigestChars / 2)]);

        return extension.Length is > 0 and <= 16
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"path#{handle}{extension}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"path#{handle}");
    }

    /// <inheritdoc />
    public bool Equals(LogPath other) => string.Equals(_path, other._path, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LogPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _path is null ? 0 : StringComparer.Ordinal.GetHashCode(_path);

    /// <summary>Whether two wrapped paths are the same path.</summary>
    public static bool operator ==(LogPath left, LogPath right) => left.Equals(right);

    /// <summary>Whether two wrapped paths are different paths.</summary>
    public static bool operator !=(LogPath left, LogPath right) => !left.Equals(right);

    /// <summary>Wraps a path for logging.</summary>
    public static implicit operator LogPath(string? path) => new(path);

    /// <summary>Wraps a path for logging, for callers that dislike implicit conversions.</summary>
    /// <param name="path">The path, or <see langword="null"/> when there is none.</param>
    public static LogPath FromString(string? path) => new(path);
}

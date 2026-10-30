using Bodu;
using System.Text;

namespace FallbackPlan.Domain;

/// <summary>
/// What a restore writes a file's alternate data streams back through
/// (specification 06 §4.1 key 9; FR-RST-004; ADR-0174), so a case can
/// stand calls of its own in for the platform's.
/// </summary>
public interface IAlternateStreams
{
    /// <summary>
    /// The stream <paramref name="name"/> of <paramref name="file"/> where it
    /// stands at exactly <paramref name="length"/> bytes, or null where it
    /// does not, or will not be read.
    /// </summary>
    /// <param name="file">A file the restore landed.</param>
    /// <param name="name">A name <see cref="AlternateStreams.TryName"/> gave.</param>
    /// <param name="length">The length the stream was captured at.</param>
    byte[]? Read(string file, string name, ulong length);

    /// <summary>
    /// Makes the stream <paramref name="name"/> of <paramref name="file"/>
    /// hold <paramref name="content"/> and nothing else.
    /// </summary>
    /// <param name="file">A file the restore landed.</param>
    /// <param name="name">A name <see cref="AlternateStreams.TryName"/> gave.</param>
    /// <param name="content">The stream's verified content.</param>
    /// <returns>Null when the stream holds it, or why it does not.</returns>
    string? Write(string file, string name, ReadOnlySpan<byte> content);
}

/// <summary>
/// The platform's calls for a file's alternate data streams, and the rule a
/// captured stream's name is held to before it is joined to a file's path.
/// </summary>
/// <remarks>
/// <para>
/// Only Windows keeps alternate streams, and .NET opens one as
/// <c>file:name</c>. Elsewhere a colon is a name's own character, so the
/// same path would make another file beside the one restored; these calls
/// write nothing off Windows, and say so.
/// </para>
/// <para>
/// A captured name is untrusted, and Windows resolves the joined path before
/// it opens it. <c>file:..\..\x</c> is three components, the second of them
/// <c>..</c>, so it names <c>x</c> beside the file, and each further
/// <c>..\</c> climbs a folder; a trailing period or space is stripped, so
/// <c>file:..</c> names the file itself. A
/// stream's name holds no separator, colon or NUL and ends in neither,
/// whatever else it holds: Windows names some streams with a control
/// character of their own.
/// </para>
/// <para>
/// Sits beside <see cref="HardLinks"/> for the reason it gives: the engine's
/// restore and the standalone recovery tool both write restored files, and
/// Domain is the one assembly both may reach.
/// </para>
/// </remarks>
public static class AlternateStreams
{
    /// <summary>The most UTF-16 units Windows holds in a stream's name.</summary>
    private const int MostNameUnits = 255;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Whether this platform keeps alternate data streams: Windows alone does.</summary>
    public static bool CanWrite { get; } = OperatingSystem.IsWindows();

    /// <summary>This platform's calls.</summary>
    public static IAlternateStreams OfThisPlatform { get; } = new Platform();

    /// <summary>
    /// Whether <paramref name="captured"/> is a name a stream of a file can be
    /// written under, joined to the file's path, without reaching anything
    /// else.
    /// </summary>
    /// <param name="captured">The name as captured, in UTF-8. Untrusted.</param>
    /// <param name="name">The name, where it is one.</param>
    /// <param name="why">Why it is not, in the code's own words.</param>
    public static bool TryName(ReadOnlySpan<byte> captured, out string name, out string why)
    {
        name = string.Empty;
        why = string.Empty;

        string decoded;
        try
        {
            decoded = Strict.GetString(captured);
        }
        catch (DecoderFallbackException)
        {
            why = "the name is not UTF-8";
            return false;
        }

        if (decoded.Length == 0)
        {
            why = "the name is empty";
            return false;
        }

        if (decoded.Length > MostNameUnits)
        {
            why = $"the name is longer than the {MostNameUnits} UTF-16 units Windows holds in a stream's name";
            return false;
        }

        if (decoded.AsSpan().IndexOfAny(@"\/:" + "\0") >= 0)
        {
            why = "the name holds a separator, a colon or a NUL, so joined to the file's path it names something else";
            return false;
        }

        if (decoded.EndsWith(' ') || decoded.EndsWith('.'))
        {
            why = "the name ends in a space or a period, which Windows strips from a path";
            return false;
        }

        name = decoded;
        return true;
    }

    private sealed class Platform : IAlternateStreams
    {
        public byte[]? Read(string file, string name, ulong length)
        {
            ThrowHelper.ThrowIfNullOrWhiteSpace(file);
            ThrowHelper.ThrowIfNullOrEmpty(name);

            // A stream is captured whole in one segment, so one longer than
            // the largest is none a capture made.
            if (!OperatingSystem.IsWindows() || length > SegmentSize.MaximumBytes)
            {
                return null;
            }

            try
            {
                using var stream = new FileStream(file + ":" + name, FileMode.Open, FileAccess.Read, FileShare.Read);
                if ((ulong)stream.Length != length)
                {
                    return null;
                }

                var content = new byte[length];
                stream.ReadExactly(content);
                return content;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }

        public string? Write(string file, string name, ReadOnlySpan<byte> content)
        {
            ThrowHelper.ThrowIfNullOrWhiteSpace(file);
            ThrowHelper.ThrowIfNullOrEmpty(name);

            if (!OperatingSystem.IsWindows())
            {
                return "alternate data streams are kept only on Windows";
            }

            try
            {
                using var stream = new FileStream(file + ":" + name, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.Write(content);
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return exception.Message;
            }
        }
    }
}

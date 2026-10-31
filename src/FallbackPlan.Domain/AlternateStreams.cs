using Bodu;
using System.Text;
using FallbackPlan.Domain.Identifiers;

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

    /// <summary>
    /// Writes each of <paramref name="streams"/> onto <paramref name="file"/>
    /// through <paramref name="calls"/>: its name judged by
    /// <see cref="TryName"/> before it is joined to the path, and its content
    /// as <paramref name="read"/> gives it. The rule the recovery tool and the
    /// single-file restore write a file's streams by (ADR-0174 Amendments 1
    /// and 2).
    /// </summary>
    /// <param name="file">A file whose content has landed and verified.</param>
    /// <param name="streams">The streams it was captured with.</param>
    /// <param name="read">
    /// A stream's verified content by its object identifier, or why it will
    /// not read.
    /// </param>
    /// <param name="calls">The calls that write a stream.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>Why each stream that did not land did not, in order; empty where every one landed.</returns>
    public static async ValueTask<IReadOnlyList<string>> WriteBackAsync(
        string file,
        IReadOnlyList<AlternateStreamEntry> streams,
        Func<ObjectId, CancellationToken, ValueTask<(byte[]? Content, string? Unread)>> read,
        IAlternateStreams calls,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(file);
        ThrowHelper.ThrowIfNull(streams);
        ThrowHelper.ThrowIfNull(read);
        ThrowHelper.ThrowIfNull(calls);

        List<string> left = [];
        foreach (var stream in streams)
        {
            if (!TryName(stream.Name.Span, out var name, out var refused))
            {
                left.Add($"'{Encoding.UTF8.GetString(stream.Name.Span)}' was not written back: {refused}");
                continue;
            }

            var (content, unread) = await read(stream.ObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                left.Add($"'{name}' was not written back: {unread}");
                continue;
            }

            if (calls.Write(file, name, content) is { } why)
            {
                left.Add($"'{name}' was not written back: {why}");
            }
        }

        return left;
    }

    /// <summary>
    /// Writes each stream <paramref name="metadata"/> carries onto
    /// <paramref name="file"/> as the extended attribute of its name, holding
    /// its content as <paramref name="read"/> gives it: the form a platform
    /// that keeps no streams and writes attributes keeps them in (ADR-0179).
    /// A stream with no name in the user namespace where names take one, or
    /// whose name the file's own captured attribute has, is neither read nor
    /// written; that attribute is the file's own here, and is written after.
    /// </summary>
    /// <param name="file">A file whose content has landed and verified.</param>
    /// <param name="metadata">What it was captured with: its streams, and its own attributes.</param>
    /// <param name="read">A stream's verified content by its object identifier, or why it will not read.</param>
    /// <param name="namespaced">Whether the platform takes an attribute's name only in a namespace.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>Why each stream that was not kept was not, in order; empty where every one was.</returns>
    public static async ValueTask<IReadOnlyList<string>> WriteBackAsAttributesAsync(
        string file,
        EntryMetadata metadata,
        Func<ObjectId, CancellationToken, ValueTask<(byte[]? Content, string? Unread)>> read,
        bool namespaced,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(file);
        ThrowHelper.ThrowIfNull(metadata);
        ThrowHelper.ThrowIfNull(read);

        List<string> left = [];
        foreach (var stream in metadata.AlternateStreams)
        {
            var name = Encoding.UTF8.GetString(stream.Name.Span);
            if (MetadataRules.RefusedStreamAsAttribute(stream.Name.Span, namespaced) is { } refused)
            {
                left.Add($"'{name}' was not kept as an attribute: {refused}");
                continue;
            }

            if (metadata.ExtendedAttributes.Any(attribute => attribute.Name.Span.SequenceEqual(stream.Name.Span)))
            {
                left.Add($"'{name}' was not kept as an attribute: the file's own captured attribute has its name");
                continue;
            }

            var (content, unread) = await read(stream.ObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                left.Add($"'{name}' was not kept as an attribute: {unread}");
                continue;
            }

            if (!ExtendedAttributes.TrySet(file, stream.Name.Span, content))
            {
                left.Add($"'{name}' was not kept as an attribute: the volume refused it");
            }
        }

        return left;
    }

    /// <summary>
    /// Writes each stream <paramref name="metadata"/> carries onto
    /// <paramref name="file"/> in the form this platform keeps them in: as
    /// streams through <paramref name="calls"/> or Windows' own
    /// (<see cref="WriteBackAsync"/>), or as attributes of the same names on
    /// Linux and macOS (<see cref="WriteBackAsAttributesAsync"/>). The rule
    /// the recovery tool and the single-file restore write a file's streams
    /// by (ADR-0179 Amendment 1).
    /// </summary>
    /// <param name="file">A file whose content has landed and verified.</param>
    /// <param name="metadata">What it was captured with.</param>
    /// <param name="read">A stream's verified content by its object identifier, or why it will not read.</param>
    /// <param name="calls">Calls a case stands in for a platform that keeps streams, or null for this platform's own.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>
    /// Why each stream that did not land did not, in order; empty where every
    /// one did; null where this platform keeps them in no form, and none was
    /// asked for.
    /// </returns>
    public static async ValueTask<IReadOnlyList<string>?> WriteBackOnThisPlatformAsync(
        string file,
        EntryMetadata metadata,
        Func<ObjectId, CancellationToken, ValueTask<(byte[]? Content, string? Unread)>> read,
        IAlternateStreams? calls,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(metadata);

        if (calls is not null || CanWrite)
        {
            return await WriteBackAsync(file, metadata.AlternateStreams, read, calls ?? OfThisPlatform, cancellationToken)
                .ConfigureAwait(false);
        }

        return MetadataRules.KeepsStreamsAsAttributes(EntryKind.File, CanWrite, ExtendedAttributes.CanSet)
            ? await WriteBackAsAttributesAsync(file, metadata, read, ExtendedAttributes.TakesNamespaces, cancellationToken)
                .ConfigureAwait(false)
            : null;
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

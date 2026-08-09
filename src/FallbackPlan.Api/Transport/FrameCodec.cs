using Bodu;
using System.Buffers.Binary;
using System.Text.Json;
using FallbackPlan.Api.Resources;

namespace FallbackPlan.Api.Transport;

/// <summary>
/// Length-prefixed JSON frames over any stream.
/// </summary>
/// <remarks>
/// The length prefix is bounded and checked before a single byte is allocated
/// ([T-7](../../../docs/threat-model.md)): a parser that trusts a length field
/// is a denial of service with extra steps, and this one faces local callers
/// today and remote ones later.
/// </remarks>
public static class FrameCodec
{
    /// <summary>The largest frame that will be read or written.</summary>
    public const int MaximumFrameBytes = 8 * 1024 * 1024;

    /// <summary>The serializer options both ends use.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>Writes one frame.</summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="frame">The frame.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the frame is flushed.</returns>
    public static async ValueTask WriteAsync(Stream stream, WireFrame frame, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(frame);

        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SerializerOptions);
        if (payload.Length > MaximumFrameBytes)
        {
            throw new InvalidOperationException(Strings.FormatFrameCodec_ByteFrameExceedsByteLimit(payload.Length, MaximumFrameBytes));
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame, or null when the peer closed cleanly.</summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The frame, or <see langword="null"/> at end of stream.</returns>
    /// <exception cref="InvalidDataException">The peer sent something that is not a frame.</exception>
    public static async ValueTask<WireFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(stream);

        var prefix = new byte[4];
        if (!await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException(Strings.FormatFrameCodec_FrameDeclaredBytesWhichOutside(length, MaximumFrameBytes));
        }

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(Strings.FrameCodec_FrameEndedBeforeDeclaredLength);
        }

        try
        {
            return JsonSerializer.Deserialize<WireFrame>(payload, SerializerOptions)
                ?? throw new InvalidDataException(Strings.FrameCodec_FrameDecodedNothing);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(Strings.FormatFrameCodec_FrameNotValidJSON(exception.Message), exception);
        }
    }

    private static async ValueTask<bool> ReadExactlyAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                return read != 0
                    ? throw new InvalidDataException(Strings.FrameCodec_PeerClosedMidFrame)
                    : false;
            }

            read += got;
        }

        return true;
    }
}

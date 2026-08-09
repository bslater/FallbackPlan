using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Repository.Crypto.Resources;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// The one construction that turns a source filesystem's file identity into
/// a 16-byte repository-side value:
/// <c>HMAC-SHA-256(content_id_key, label ‖ device_id ‖ u64(file_identity))[0..16]</c>.
/// </summary>
/// <remarks>
/// Two labels use it — <c>"fbp/hardlink/v1"</c> for the manifest's
/// <c>hardlink_group</c> (specification 06 §4 key 12) and
/// <c>"fbp/identity/v1"</c> for the source-identity map (06 §11) — and they
/// must stay unrelatable: a store that could match one against the other
/// would learn that a hardlinked file and a renamed file share an inode.
/// The label is inside the HMAC message rather than beside it precisely so
/// that separation is structural.
/// </remarks>
internal static class KeyedFileIdentity
{
    internal const int Length = 16;

    internal static byte[] Derive(
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> contentIdKey,
        ReadOnlySpan<byte> deviceId,
        ulong fileIdentity)
    {
        if (deviceId.Length != 16)
        {
            throw new ArgumentException(Strings.KeyedFileIdentity_DeviceIdentifierExactlyBytes, nameof(deviceId));
        }

        Span<byte> message = stackalloc byte[label.Length + 16 + 8];
        label.CopyTo(message);
        deviceId.CopyTo(message[label.Length..]);
        BinaryPrimitives.WriteUInt64BigEndian(message[(label.Length + 16)..], fileIdentity);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(contentIdKey, message, mac);
        return mac[..Length].ToArray();
    }
}

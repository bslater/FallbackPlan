using System.Security.Cryptography;
using System.Text;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Derives <c>hardlink_group</c> identifiers (specification 06 §4 key 12;
/// ADR-0026 §Decision 1): the first 16 bytes of
/// <c>HMAC-SHA-256(content_id_key, "fbp/hardlink/v1" ‖ device_id ‖
/// u64(file_identity))</c>. Keyed with the content-ID key so the group
/// leaks nothing about the source's inode space; deterministic so the same
/// inode yields the same group within a snapshot and across snapshots of
/// the same device.
/// </summary>
public sealed class HardlinkGrouper : IDisposable
{
    private static readonly byte[] Label = Encoding.ASCII.GetBytes("fbp/hardlink/v1");

    private readonly byte[] _contentIdKey;

    /// <summary>Creates a grouper over the repository's 32-byte content-ID key.</summary>
    /// <exception cref="ArgumentException"><paramref name="contentIdKey"/> is not exactly 32 bytes.</exception>
    public HardlinkGrouper(ReadOnlySpan<byte> contentIdKey)
    {
        if (contentIdKey.Length != 32)
        {
            throw new ArgumentException("The content-ID key is exactly 32 bytes.", nameof(contentIdKey));
        }

        _contentIdKey = contentIdKey.ToArray();
    }

    /// <summary>
    /// Derives the 16-byte group for one file identity on one device.
    /// <paramref name="fileIdentity"/> is the source filesystem's stable
    /// file identifier — inode on POSIX, FileId on Windows — encoded
    /// big-endian per specification 00 §2.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is not exactly 16 bytes.</exception>
    public byte[] Derive(ReadOnlySpan<byte> deviceId, ulong fileIdentity) =>
        KeyedFileIdentity.Derive(Label, _contentIdKey, deviceId, fileIdentity);

    /// <inheritdoc />
    public void Dispose() => CryptographicOperations.ZeroMemory(_contentIdKey);
}

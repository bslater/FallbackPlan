using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Repository.Crypto.Resources;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Derives <c>source_key</c> values for the source-identity map
/// (specification 06 §11): the first 16 bytes of
/// <c>HMAC-SHA-256(content_id_key, "fbp/identity/v1" ‖ device_id ‖
/// u64(file_identity))</c>.
/// </summary>
/// <remarks>
/// The same construction as <see cref="HardlinkGrouper"/> under a different
/// label, and for a different purpose: a hardlink group says two paths are
/// one file <em>within</em> a snapshot; a source key says a file version in
/// this snapshot is the same file as a version in the previous one, even
/// though it has been renamed or moved. Keyed, so the store never learns the
/// source's inode space; deterministic, so the answer survives the loss of
/// every local cache.
/// </remarks>
public sealed class SourceIdentityKeyDeriver : IDisposable
{
    private static readonly byte[] Label = Encoding.ASCII.GetBytes("fbp/identity/v1");

    private readonly byte[] _contentIdKey;

    /// <summary>Creates a deriver over the repository's 32-byte content-ID key.</summary>
    /// <exception cref="ArgumentException"><paramref name="contentIdKey"/> is not exactly 32 bytes.</exception>
    public SourceIdentityKeyDeriver(ReadOnlySpan<byte> contentIdKey)
    {
        if (contentIdKey.Length != 32)
        {
            throw new ArgumentException(Strings.HardlinkGrouper_ContentIDKeyExactlyBytes, nameof(contentIdKey));
        }

        _contentIdKey = contentIdKey.ToArray();
    }

    /// <summary>The length of a derived source key, in bytes.</summary>
    public const int KeyLength = KeyedFileIdentity.Length;

    /// <summary>
    /// Derives the 16-byte source key for one file identity on one device.
    /// <paramref name="fileIdentity"/> is the source filesystem's stable file
    /// identifier — inode on POSIX, FileId on Windows — encoded big-endian
    /// per specification 00 §2.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is not exactly 16 bytes.</exception>
    public byte[] Derive(ReadOnlySpan<byte> deviceId, ulong fileIdentity) =>
        KeyedFileIdentity.Derive(Label, _contentIdKey, deviceId, fileIdentity);

    /// <inheritdoc />
    public void Dispose() => CryptographicOperations.ZeroMemory(_contentIdKey);
}

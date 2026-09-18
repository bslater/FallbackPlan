using System.Text.Json;
using System.Text.Json.Serialization;
using Bodu;
using FallbackPlan.Application;

namespace FallbackPlan.Protocol;

/// <summary>Which side of the exchange filed a receipt.</summary>
public enum DeletionReceiptRole
{
    /// <summary>The destination that deleted and signed.</summary>
    Destination,

    /// <summary>The commander that instructed, received and verified.</summary>
    Commander,
}

/// <summary>
/// A deletion receipt as read back from disk: the envelope, the parsed
/// statement, and whether the signature holds.
/// </summary>
/// <param name="Path">Where it was read from.</param>
/// <param name="Role">Which side filed it.</param>
/// <param name="FiledAtUnixMilliseconds">When it was filed, by the filer's clock.</param>
/// <param name="SignerPublicKey">The device key the file names as the signer, lower-hex.</param>
/// <param name="SignerFingerprint">That key's fingerprint, for display.</param>
/// <param name="Set">The commander's set name, when the commander filed it.</param>
/// <param name="Destination">The commander's destination name, when the commander filed it.</param>
/// <param name="Receipt">The statement, parsed from the signed bytes; null when they do not parse.</param>
/// <param name="Verified">Whether the signature verifies under the named signer over the signed bytes.</param>
/// <param name="Problem">Why the file could not be read as a receipt, or null.</param>
public sealed record FiledDeletionReceipt(
    string Path,
    DeletionReceiptRole Role,
    ulong FiledAtUnixMilliseconds,
    string SignerPublicKey,
    string SignerFingerprint,
    string? Set,
    string? Destination,
    DeletionReceipt? Receipt,
    bool Verified,
    string? Problem);

/// <summary>
/// Where deletion receipts live: one immutable document per instruction
/// under <c>receipts/deletions/&lt;repository&gt;/</c> in a state directory
/// ([ADR-0063](../../docs/adr/0063-deletion-receipts.md)). The destination
/// files what it signed; the commander files what it verified. Neither
/// depends on the other's copy.
/// </summary>
/// <remarks>
/// The envelope is JSON around the signed bytes and the signature. Every
/// fact <see cref="List"/> reports about a deletion is parsed from the
/// signed bytes after the signature is checked, never taken from the
/// envelope, so a file edited after filing reads as unverified rather than
/// as a different attestation.
/// </remarks>
public sealed class DeletionReceiptStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;

    private DeletionReceiptStore(string root) => _root = root;

    /// <summary>Opens the store under <paramref name="stateDirectory"/>; the directory is created on first filing.</summary>
    public static DeletionReceiptStore Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        return new DeletionReceiptStore(System.IO.Path.Combine(stateDirectory, "receipts", "deletions"));
    }

    /// <summary>
    /// Files a receipt. The name carries the repository, the issue time and
    /// the session, so the destination's and the commander's copies of one
    /// exchange share a name across the two machines.
    /// </summary>
    /// <param name="role">Which side is filing.</param>
    /// <param name="signedBytes">The receipt's signed bytes.</param>
    /// <param name="signature">Its signature under <paramref name="signer"/>.</param>
    /// <param name="signer">The device that signed it.</param>
    /// <param name="set">The commander's set name, or null.</param>
    /// <param name="destination">The commander's destination name, or null.</param>
    /// <returns>The path filed.</returns>
    /// <exception cref="PeerProtocolException">The bytes are not a receipt.</exception>
    public string File(
        DeletionReceiptRole role,
        ReadOnlySpan<byte> signedBytes,
        ReadOnlySpan<byte> signature,
        PeerIdentity signer,
        string? set,
        string? destination)
    {
        ThrowHelper.ThrowIfNull(signer);
        var receipt = DeletionReceipt.Parse(signedBytes);

        var directory = System.IO.Path.Combine(_root, Convert.ToHexStringLower(receipt.RepositoryId.Span));
        Directory.CreateDirectory(directory);
        var name = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{receipt.IssuedAtUnixMilliseconds:D20}-{Convert.ToHexStringLower(receipt.SessionId.Span[..8])}.json");
        var path = System.IO.Path.Combine(directory, name);

        var envelope = new Envelope
        {
            Role = role == DeletionReceiptRole.Destination ? "destination" : "commander",
            FiledAt = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SignerPublicKey = Convert.ToHexStringLower(signer.PublicKey),
            Signature = Convert.ToHexStringLower(signature),
            SignedBytes = Convert.ToHexStringLower(signedBytes),
            Set = set,
            Destination = destination,
        };
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(envelope, SerializerOptions));
        return path;
    }

    /// <summary>
    /// Every filed receipt, newest first by the time the destination issued
    /// it; optionally only one repository's. A file that is not a receipt
    /// is reported with its problem rather than skipped.
    /// </summary>
    /// <param name="repositoryIdHex">The repository, lower-hex, or null for all.</param>
    public IReadOnlyList<FiledDeletionReceipt> List(string? repositoryIdHex = null)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var directories = repositoryIdHex is null
            ? Directory.GetDirectories(_root)
            : [System.IO.Path.Combine(_root, repositoryIdHex)];

        var filed = new List<FiledDeletionReceipt>();
        foreach (var directory in directories.Where(Directory.Exists))
        {
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                filed.Add(ReadOne(path));
            }
        }

        return
        [
            .. filed
                .OrderByDescending(entry => entry.Receipt?.IssuedAtUnixMilliseconds ?? 0)
                .ThenBy(entry => entry.Path, StringComparer.Ordinal),
        ];
    }

    private static FiledDeletionReceipt ReadOne(string path)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(System.IO.File.ReadAllText(path), SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return Unreadable(path, $"not a receipt envelope: {exception.Message}");
        }

        if (envelope is null || envelope.SignedBytes is null || envelope.Signature is null || envelope.SignerPublicKey is null)
        {
            return Unreadable(path, "the envelope is missing the signed bytes, the signature or the signer");
        }

        var role = envelope.Role == "commander" ? DeletionReceiptRole.Commander : DeletionReceiptRole.Destination;
        byte[] signedBytes;
        byte[] signature;
        byte[] signerKey;
        try
        {
            signedBytes = Convert.FromHexString(envelope.SignedBytes);
            signature = Convert.FromHexString(envelope.Signature);
            signerKey = Convert.FromHexString(envelope.SignerPublicKey);
        }
        catch (FormatException exception)
        {
            return Unreadable(path, $"the envelope's hex does not decode: {exception.Message}");
        }

        if (signerKey.Length != PeerIdentity.KeyLength)
        {
            return Unreadable(path, "the signer's key is not 32 bytes");
        }

        var signer = PeerIdentity.FromPublicKey(signerKey);
        var verified = signature.Length == DeletionReceipt.SignatureLength && signer.Verify(signedBytes, signature);

        DeletionReceipt? receipt = null;
        string? problem = null;
        try
        {
            receipt = DeletionReceipt.Parse(signedBytes);
        }
        catch (PeerProtocolException exception)
        {
            problem = exception.Message;
        }

        return new FiledDeletionReceipt(
            path, role, envelope.FiledAt, envelope.SignerPublicKey, signer.Fingerprint,
            envelope.Set, envelope.Destination, receipt, verified && receipt is not null, problem);
    }

    private static FiledDeletionReceipt Unreadable(string path, string problem) =>
        new(path, DeletionReceiptRole.Destination, 0, string.Empty, string.Empty, null, null, null, false, problem);

    private sealed class Envelope
    {
        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("filed_at")]
        public ulong FiledAt { get; init; }

        [JsonPropertyName("signer_public_key")]
        public string? SignerPublicKey { get; init; }

        [JsonPropertyName("signature")]
        public string? Signature { get; init; }

        [JsonPropertyName("signed_bytes")]
        public string? SignedBytes { get; init; }

        [JsonPropertyName("set")]
        public string? Set { get; init; }

        [JsonPropertyName("destination")]
        public string? Destination { get; init; }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Application;

namespace FallbackPlan.Protocol;

/// <summary>
/// The filing every kind of peer receipt shares: one immutable JSON envelope
/// per receipt under a kind's root, named by repository, issue time and
/// session so the two parties' copies of one exchange share a name; the
/// signature re-checked against the recorded signer on every read; a file
/// that is not an envelope reported rather than skipped. The envelope names
/// its kind, so a receipt of one kind filed among another's is reported as
/// such and never parsed as the wrong statement; an envelope written before
/// kinds existed is a deletion by where it sits.
/// </summary>
internal static class PeerReceiptFiles
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>What a receipt's envelope yields before its kind's parse runs.</summary>
    internal sealed record Reading(
        string Path,
        DeletionReceiptRole Role,
        ulong FiledAtUnixMilliseconds,
        string SignerPublicKey,
        string SignerFingerprint,
        string? Set,
        string? Destination,
        string? Kind,
        byte[] SignedBytes,
        bool SignatureValid,
        string? Problem);

    internal static string File(
        string root,
        string kind,
        DeletionReceiptRole role,
        ReadOnlySpan<byte> signedBytes,
        ReadOnlySpan<byte> signature,
        PeerIdentity signer,
        string? set,
        string? destination,
        ReadOnlySpan<byte> repositoryId,
        ulong issuedAtUnixMilliseconds,
        ReadOnlySpan<byte> sessionId)
    {
        var directory = System.IO.Path.Combine(root, Convert.ToHexStringLower(repositoryId));
        Directory.CreateDirectory(directory);
        var name = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{issuedAtUnixMilliseconds:D20}-{Convert.ToHexStringLower(sessionId[..8])}.json");
        var path = System.IO.Path.Combine(directory, name);

        var envelope = new Envelope
        {
            Kind = kind,
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

    /// <summary>Every envelope under the root, or one repository's, in no particular order.</summary>
    internal static IEnumerable<Reading> Read(string root, string? repositoryIdHex)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var directories = repositoryIdHex is null
            ? Directory.GetDirectories(root)
            : [System.IO.Path.Combine(root, repositoryIdHex)];
        foreach (var directory in directories.Where(Directory.Exists))
        {
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                yield return ReadOne(path);
            }
        }
    }

    private static Reading ReadOne(string path)
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
        var valid = signature.Length == PeerKeypair.SignatureLength && signer.Verify(signedBytes, signature);
        return new Reading(
            path, role, envelope.FiledAt, envelope.SignerPublicKey, signer.Fingerprint,
            envelope.Set, envelope.Destination, envelope.Kind, signedBytes, valid, null);
    }

    private static Reading Unreadable(string path, string problem) =>
        new(path, DeletionReceiptRole.Destination, 0, string.Empty, string.Empty, null, null, null, [], false, problem);

    private sealed class Envelope
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

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

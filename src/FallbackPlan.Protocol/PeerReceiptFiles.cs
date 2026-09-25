using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain;

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
    /// <summary>
    /// How many digits of issue time a file name begins with. Fixed width so
    /// an ordinal sort over the names is a sort by issue time.
    /// </summary>
    internal const int IssuedAtDigits = 20;

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
        ReadOnlySpan<byte> sessionId,
        ReceiptRetentionPolicy policy)
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

        // The bound is applied where the file is made, so a live pair stays
        // bounded between restarts rather than only at one. It costs a name
        // listing over a directory this same call keeps under the ceiling,
        // after a network round trip that cost far more.
        _ = SweepDirectory(directory, policy, DateTimeOffset.UtcNow);
        return path;
    }

    /// <summary>
    /// Applies <paramref name="policy"/> to every repository under
    /// <paramref name="root"/>, or to one.
    /// </summary>
    /// <remarks>
    /// Names only: no receipt is opened, no signature checked and no JSON
    /// parsed. So a pile that has gone unreadable is still bounded, and a
    /// receipt whose bytes are corrupt ages out by the same rule as a sound
    /// one rather than being immortal for being unparseable.
    /// </remarks>
    /// <param name="root">The kind's root.</param>
    /// <param name="policy">How many, and how long.</param>
    /// <param name="now">The clock.</param>
    /// <param name="repositoryIdHex">One repository, lower-hex, or null for all.</param>
    /// <returns>How many files were deleted.</returns>
    internal static int Sweep(string root, ReceiptRetentionPolicy policy, DateTimeOffset now, string? repositoryIdHex)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var directories = repositoryIdHex is null
            ? Directory.GetDirectories(root)
            : [System.IO.Path.Combine(root, repositoryIdHex)];
        var swept = 0;
        foreach (var directory in directories.Where(Directory.Exists))
        {
            swept += SweepDirectory(directory, policy, now);
        }

        return swept;
    }

    /// <summary>One repository's directory, oldest candidates first.</summary>
    private static int SweepDirectory(string directory, ReceiptRetentionPolicy policy, DateTimeOffset now)
    {
        string[] names;
        try
        {
            names = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        // The name begins with the issue time, zero-padded to a fixed width,
        // so an ordinal sort is newest-last by that time and costs no read.
        // The name is used to order and to bound, never to describe: every
        // fact a reader shows still comes from the signed bytes.
        var ours = names
            .Select(path => (Path: path, IssuedAt: IssuedAtFromName(path)))
            .Where(entry => entry.IssuedAt is not null)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

        var cutoff = now.AddDays(-policy.RetainedDays).ToUnixTimeMilliseconds();
        var swept = 0;
        for (var i = 0; i < ours.Length; i++)
        {
            // Rank from the newest end: 0 is the newest file here.
            var rank = ours.Length - 1 - i;
            if (rank < policy.MinimumRetained)
            {
                // Kept whatever its age: a pair that has gone quiet keeps a
                // history rather than ageing out of its own record entirely.
                continue;
            }

            var pastTheCeiling = rank >= policy.MaximumRetained;
            if (!pastTheCeiling && (long)ours[i].IssuedAt!.Value >= cutoff)
            {
                continue;
            }

            if (Delete(ours[i].Path))
            {
                swept++;
            }
        }

        return swept;
    }

    /// <summary>The issue time a receipt's file name carries, or null when the name is not one of ours.</summary>
    private static ulong? IssuedAtFromName(string path)
    {
        var name = System.IO.Path.GetFileName(path.AsSpan());
        var separator = name.IndexOf('-');
        return separator == IssuedAtDigits
            && ulong.TryParse(
                name[..separator], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var issuedAt)
            ? issuedAt
            : null;
    }

    private static bool Delete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Housekeeping never fails the work it rides on: a file that will
            // not go is tried again by the next filing or the next start.
            return false;
        }
    }

    /// <summary>
    /// The newest <paramref name="limit"/> envelopes under the root, or one
    /// repository's; every one when the limit is null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The limit bounds the <b>work</b> and not only the answer: reading and
    /// verifying every receipt ever filed in order to show the newest fifty
    /// would price a listing by the pile rather than by the question, and the
    /// pile grows by one per exchange for ever.
    /// </para>
    /// <para>
    /// The window is chosen by file name, whose leading digits are the issue
    /// time — so it costs a listing and no reads. That has a consequence
    /// worth stating: a receipt that has been tampered with cannot drop out
    /// of a bounded window by becoming unreadable, which taking the newest
    /// rows after reading would have let it do, an unreadable file having no
    /// issue time to sort by. The name orders and bounds; every fact a caller
    /// is shown still comes from the signed bytes.
    /// </para>
    /// </remarks>
    /// <param name="root">The kind's root.</param>
    /// <param name="repositoryIdHex">One repository, lower-hex, or null for all.</param>
    /// <param name="limit">At most this many of the newest, or null for every one.</param>
    internal static IEnumerable<Reading> Read(string root, string? repositoryIdHex, int? limit = null)
    {
        IEnumerable<string> paths = Names(root, repositoryIdHex);
        if (limit is { } newest)
        {
            paths = paths.OrderByDescending(path => System.IO.Path.GetFileName(path), StringComparer.Ordinal)
                .Take(newest);
        }

        foreach (var path in paths)
        {
            yield return ReadOne(path);
        }
    }

    /// <summary>How many envelopes are on file, counted from names alone.</summary>
    /// <param name="root">The kind's root.</param>
    /// <param name="repositoryIdHex">One repository, lower-hex, or null for all.</param>
    internal static int Count(string root, string? repositoryIdHex) => Names(root, repositoryIdHex).Count();

    /// <summary>Every envelope's path under the root, or one repository's, in no particular order.</summary>
    private static IEnumerable<string> Names(string root, string? repositoryIdHex)
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
                yield return path;
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

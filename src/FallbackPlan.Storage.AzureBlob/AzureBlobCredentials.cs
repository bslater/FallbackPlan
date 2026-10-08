using System.Globalization;
using System.Text;
using FallbackPlan.Storage.AzureBlob.Resources;

namespace FallbackPlan.Storage.AzureBlob;

/// <summary>Which of the two credentials an Azure Blob destination accepts is held (ADR-0093).</summary>
public enum AzureBlobCredentialKind
{
    /// <summary>The storage account's key, which signs each request (Shared Key).</summary>
    SharedKey,

    /// <summary>A shared access signature issued for the container, carried in each request's query.</summary>
    SharedAccessSignature,
}

/// <summary>
/// What a container's requests are authorised by (ADR-0093): the account key,
/// which signs each request and is never sent, or a shared access signature,
/// which is sent with each request as it was issued and says in clear when
/// it stops being honoured. What this record prints leaves either out.
/// </summary>
/// <remarks>
/// The two are judged where they arrive, before anything is stored or
/// sent: an account key must be the base64 the account issued, and a
/// signature must carry the fields every signature carries and an expiry
/// that reads as a time. Whether either is the right one, only the store can
/// say.
/// </remarks>
public sealed record AzureBlobCredentials
{
    /// <summary>The longest shared access signature accepted: several times what any account issues.</summary>
    public const int MaximumSignatureLength = 2048;

    private static readonly string[] ExpiryFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd",
    ];

    private AzureBlobCredentials(AzureBlobCredentialKind kind, string secret, DateTimeOffset? expires)
    {
        Kind = kind;
        Secret = secret;
        Expires = expires;
    }

    /// <summary>Which credential this is.</summary>
    public AzureBlobCredentialKind Kind { get; }

    /// <summary>
    /// The account key, base64, or the signature's query text without a
    /// leading question mark; never written anywhere but the credential store.
    /// </summary>
    public string Secret { get; }

    /// <summary>When a signature stops being honoured, as it states; null for an account key or a signature that names none.</summary>
    public DateTimeOffset? Expires { get; }

    /// <summary>Holds an account key.</summary>
    /// <param name="accountKey">The key, base64, as the account issued it.</param>
    /// <exception cref="ArgumentException">It is not base64, or holds a space.</exception>
    public static AzureBlobCredentials SharedKey(string accountKey)
    {
        if (AccountKeyDefect(accountKey) is { } defect)
        {
            throw new ArgumentException(defect, nameof(accountKey));
        }

        return new AzureBlobCredentials(AzureBlobCredentialKind.SharedKey, accountKey, expires: null);
    }

    /// <summary>Holds a shared access signature, reading when it lapses from the token itself.</summary>
    /// <param name="token">The signature's query text, with or without a leading question mark.</param>
    /// <exception cref="ArgumentException">
    /// It is not a token — an address, a space, too long — or lacks a version
    /// or a signature, or names an expiry that is not a time.
    /// </exception>
    public static AzureBlobCredentials SharedAccessSignature(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (SignatureDefect(token, out var text, out var expires) is { } defect)
        {
            throw new ArgumentException(defect, nameof(token));
        }

        return new AzureBlobCredentials(AzureBlobCredentialKind.SharedAccessSignature, text, expires);
    }

    /// <summary>
    /// What keeps <paramref name="secret"/> from being a credential of
    /// <paramref name="kind"/>, as a person is told it, or null when nothing
    /// does: the same judgement <see cref="SharedKey"/> and
    /// <see cref="SharedAccessSignature"/> refuse by, for a caller that
    /// answers with it rather than throws.
    /// </summary>
    /// <param name="kind">The credential it is meant to be.</param>
    /// <param name="secret">The key or the token, as given.</param>
    public static string? DefectOf(AzureBlobCredentialKind kind, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return kind == AzureBlobCredentialKind.SharedKey
            ? AccountKeyDefect(secret)
            : SignatureDefect(secret, out _, out _);
    }

    private static string? AccountKeyDefect(string accountKey) =>
        string.IsNullOrEmpty(accountKey)
        || accountKey.Any(char.IsWhiteSpace)
        || !Convert.TryFromBase64String(accountKey, new byte[accountKey.Length], out var written)
        || written == 0
            ? Strings.AzureBlobCredentials_AccountKeyMalformed
            : null;

    private static string? SignatureDefect(string token, out string text, out DateTimeOffset? expires)
    {
        text = token.StartsWith('?') ? token[1..] : token;
        expires = null;
        if (text.Length is 0 or > MaximumSignatureLength
            || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || text.Contains("://", StringComparison.Ordinal))
        {
            return Strings.FormatAzureBlobCredentials_SignatureNotAToken(MaximumSignatureLength);
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            fields[parts[0]] = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        foreach (var required in (ReadOnlySpan<string>)["sv", "sig"])
        {
            if (!fields.TryGetValue(required, out var value) || value.Length == 0)
            {
                return Strings.FormatAzureBlobCredentials_SignatureLacksField(required);
            }
        }

        if (fields.TryGetValue("se", out var stated))
        {
            if (!DateTimeOffset.TryParseExact(
                    stated, ExpiryFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return Strings.FormatAzureBlobCredentials_SignatureExpiryUnreadable(stated);
            }

            expires = parsed;
        }

        return null;
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Kind = ").Append(Kind).Append(", Secret = (withheld)");
        if (Expires is { } expires)
        {
            builder.Append(", Expires = ").Append(expires.ToString("u", CultureInfo.InvariantCulture));
        }

        return true;
    }
}

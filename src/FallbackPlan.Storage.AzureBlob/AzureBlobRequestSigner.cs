using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FallbackPlan.Storage.AzureBlob;

/// <summary>
/// Authorises a request as the Blob service requires (ADR-0093): under the
/// account key, by the Shared Key scheme — an HMAC over the method, the
/// fixed list of standard headers, every <c>x-ms-</c> header and the
/// account-qualified resource with its sorted query — or under a shared
/// access signature, by carrying the token in the query exactly as it was
/// issued. Either way the request names the API version it speaks and the
/// time it was sent.
/// </summary>
/// <remarks>
/// <para>
/// The range of a read is sent as <c>x-ms-range</c>, never <c>Range</c>, so
/// it is signed among the <c>x-ms-</c> headers and the scheme's own Range
/// line stays empty, as the API's reference client leaves it.
/// </para>
/// <para>
/// The <c>x-ms-</c> headers are sorted ordinally. The service sorts them by
/// a collation that passes over hyphens first; the headers this provider
/// sends — <c>x-ms-blob-type</c>, <c>x-ms-date</c>, <c>x-ms-range</c>,
/// <c>x-ms-version</c> — sort the same either way, and the signing vectors
/// hold it to that.
/// </para>
/// </remarks>
/// <param name="account">The storage account the requests are for.</param>
/// <param name="credentials">What authorises them.</param>
public sealed class AzureBlobRequestSigner(string account, AzureBlobCredentials credentials)
{
    /// <summary>The REST API version every request names: one the service and every store on this machine speak.</summary>
    public const string ApiVersion = "2024-11-04";

    /// <summary>
    /// Stamps <paramref name="request"/> with its version and time and
    /// authorises it, replacing any earlier signature — a retried request is
    /// signed again, at its own time.
    /// </summary>
    /// <param name="request">The request, with every header it will carry already set.</param>
    /// <param name="at">When the request is signed.</param>
    public void Sign(HttpRequestMessage request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RequestUri, nameof(request));

        request.Headers.Remove("Authorization");
        request.Headers.Remove("x-ms-date");
        request.Headers.Remove("x-ms-version");
        request.Headers.TryAddWithoutValidation(
            "x-ms-date", at.UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("x-ms-version", ApiVersion);

        if (credentials.Kind == AzureBlobCredentialKind.SharedAccessSignature)
        {
            CarrySignature(request, credentials.Secret);
            return;
        }

        var stringToSign = StringToSign(request, account);
        var key = Convert.FromBase64String(credentials.Secret);
        try
        {
            var signature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
            request.Headers.TryAddWithoutValidation("Authorization", $"SharedKey {account}:{signature}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Appends the token to the query once, however often the request is signed.</summary>
    private static void CarrySignature(HttpRequestMessage request, string token)
    {
        var target = request.RequestUri!.OriginalString;
        if (target.EndsWith("?" + token, StringComparison.Ordinal) || target.EndsWith("&" + token, StringComparison.Ordinal))
        {
            return;
        }

        request.RequestUri = new Uri(target + (request.RequestUri.Query.Length == 0 ? "?" : "&") + token);
    }

    private static string StringToSign(HttpRequestMessage request, string account)
    {
        var content = request.Content?.Headers;
        var length = content?.ContentLength is { } declared and > 0
            ? declared.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var builder = new StringBuilder()
            .Append(request.Method.Method).Append('\n')
            .Append(content is null ? string.Empty : string.Join(',', content.ContentEncoding)).Append('\n')
            .Append(content is null ? string.Empty : string.Join(',', content.ContentLanguage)).Append('\n')
            .Append(length).Append('\n')
            .Append(content?.ContentMD5 is { } digest ? Convert.ToBase64String(digest) : string.Empty).Append('\n')
            .Append(content?.ContentType?.ToString() ?? string.Empty).Append('\n')
            .Append(HeaderOf(request, "Date")).Append('\n')
            .Append(HeaderOf(request, "If-Modified-Since")).Append('\n')
            .Append(HeaderOf(request, "If-Match")).Append('\n')
            .Append(HeaderOf(request, "If-None-Match")).Append('\n')
            .Append(HeaderOf(request, "If-Unmodified-Since")).Append('\n')
            .Append(HeaderOf(request, "Range")).Append('\n');

        foreach (var (name, value) in request.Headers
            .Where(header => header.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
            .Select(header => (Name: header.Key.ToLowerInvariant(), Value: Collapse(string.Join(',', header.Value))))
            .OrderBy(header => header.Name, StringComparer.Ordinal))
        {
            builder.Append(name).Append(':').Append(value).Append('\n');
        }

        var uri = request.RequestUri!;
        builder.Append('/').Append(account).Append(uri.AbsolutePath);
        foreach (var (name, values) in uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .GroupBy(
                pair => Uri.UnescapeDataString(pair[0]).ToLowerInvariant(),
                pair => Uri.UnescapeDataString(pair.Length == 2 ? pair[1] : string.Empty),
                StringComparer.Ordinal)
            .Select(group => (group.Key, Values: group.Order(StringComparer.Ordinal)))
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            builder.Append('\n').Append(name).Append(':').Append(string.Join(',', values));
        }

        return builder.ToString();
    }

    private static string HeaderOf(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(',', values) : string.Empty;

    /// <summary>Trims a header value and folds each run of spaces inside it to one, as the canonical form requires.</summary>
    private static string Collapse(string value) =>
        string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

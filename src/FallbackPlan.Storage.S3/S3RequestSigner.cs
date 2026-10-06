using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FallbackPlan.Storage.S3;

/// <summary>
/// Signs a request as the S3 API's Signature Version 4 requires (ADR-0091):
/// the canonical request over the method, the path, the sorted query, every
/// header the request carries plus its host, and the payload's SHA-256;
/// then the string to sign, scoped to the day, the region and the service;
/// then an HMAC chain from the secret down to that scope.
/// </summary>
/// <remarks>
/// The path is encoded once, segment by segment, and never normalised: an S3
/// key is a string, and <c>a/../b</c> names an object, not a directory walk.
/// A content header is not signed — the payload hash already covers the body,
/// and a length the transport sets for itself must not be a signature input.
/// </remarks>
/// <param name="credentials">The access key to sign with.</param>
/// <param name="region">The region the signature is scoped to.</param>
public sealed class S3RequestSigner(S3Credentials credentials, string region)
{
    /// <summary>The SHA-256 of nothing: the payload hash of every request with no body.</summary>
    public const string EmptyPayloadSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>
    /// Stamps <paramref name="request"/> with its date, its payload hash and
    /// the <c>Authorization</c> header that signs them, replacing any earlier
    /// signature — a retried request is signed again, at its own time.
    /// </summary>
    /// <param name="request">The request, with every header it will carry already set.</param>
    /// <param name="payloadSha256">The body's SHA-256, lowercase hex.</param>
    /// <param name="at">When the request is signed.</param>
    public void Sign(HttpRequestMessage request, string payloadSha256, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        ArgumentNullException.ThrowIfNull(uri, nameof(request));

        var amzDate = at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        request.Headers.Remove("Authorization");
        request.Headers.Remove("x-amz-date");
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadSha256);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = request.Headers.Host ?? (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"),
        };
        foreach (var (name, values) in request.Headers)
        {
            if (!string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
            {
                headers[name.ToLowerInvariant()] = Collapse(string.Join(',', values));
            }
        }

        var signedHeaders = string.Join(';', headers.Keys);
        var canonical = new StringBuilder()
            .Append(request.Method.Method).Append('\n')
            .Append(CanonicalPath(uri)).Append('\n')
            .Append(CanonicalQuery(uri)).Append('\n');
        foreach (var (name, value) in headers)
        {
            canonical.Append(name).Append(':').Append(value).Append('\n');
        }

        canonical.Append('\n').Append(signedHeaders).Append('\n').Append(payloadSha256);

        var scope = $"{date}/{region}/s3/aws4_request";
        var stringToSign = $"{Algorithm}\n{amzDate}\n{scope}\n"
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));

        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey), Encoding.UTF8.GetBytes(date));
        try
        {
            foreach (var part in (ReadOnlySpan<string>)[region, "s3", "aws4_request"])
            {
                var next = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(part));
                CryptographicOperations.ZeroMemory(key);
                key = next;
            }

            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"{Algorithm} Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string CanonicalPath(Uri uri) =>
        string.Join('/', uri.AbsolutePath.Split('/').Select(segment => S3Location.Encode(Uri.UnescapeDataString(segment))));

    private static string CanonicalQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return string.Empty;
        }

        return string.Join('&', query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(pair => (
                Name: S3Location.Encode(Uri.UnescapeDataString(pair[0])),
                Value: S3Location.Encode(Uri.UnescapeDataString(pair.Length == 2 ? pair[1] : string.Empty))))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{pair.Name}={pair.Value}"));
    }

    /// <summary>Trims a header value and folds each run of spaces inside it to one, as the canonical form requires.</summary>
    private static string Collapse(string value) =>
        string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

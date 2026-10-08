using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace FallbackPlan.TestSupport;

/// <summary>
/// An S3-compatible object server on loopback, written from the S3 API's
/// specification for the S3-compatible provider's tests (ADR-0091): buckets
/// addressed path-style, every request's SigV4 signature checked by a
/// verifier of its own rather than the provider's signer, create-only puts by
/// <c>If-None-Match: *</c>, ranged reads answered as S3 answers them, and
/// ListObjectsV2 in pages. Faults are queued by a test and served in order.
/// </summary>
/// <remarks>
/// It answers as the API does where the provider has to cope, not where it
/// would be convenient: a range that runs past the end is a short 206 rather
/// than a 416, a delete of nothing is a 204, and an error is an XML body with
/// a code. Its listing is strongly consistent, as the API promises.
/// </remarks>
public sealed class S3CompatibleTestServer : ObjectStoreTestServer
{
    /// <summary>The access key id every request must be signed with unless another is given.</summary>
    public const string DefaultAccessKeyId = "FBPTESTACCESSKEY0001";

    /// <summary>The secret that signs for <see cref="DefaultAccessKeyId"/>.</summary>
    public const string DefaultSecretAccessKey = "fbp/test+secret=key/0123456789abcdefghij";

    /// <summary>The region the signatures are scoped to.</summary>
    public const string DefaultRegion = "test-region-1";

    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Starts a server on an ephemeral loopback port.</summary>
    /// <param name="accessKeyId">The one access key id it accepts.</param>
    /// <param name="secretAccessKey">The secret that signs for it.</param>
    /// <param name="region">The region signatures must be scoped to.</param>
    public S3CompatibleTestServer(
        string accessKeyId = DefaultAccessKeyId,
        string secretAccessKey = DefaultSecretAccessKey,
        string region = DefaultRegion)
    {
        AccessKeyId = accessKeyId;
        SecretAccessKey = secretAccessKey;
        Region = region;
    }

    /// <summary>Where the server listens: <c>http://127.0.0.1:port</c>.</summary>
    public Uri Endpoint => Origin;

    /// <summary>The access key id it accepts.</summary>
    public string AccessKeyId { get; }

    /// <summary>The secret that signs for <see cref="AccessKeyId"/>.</summary>
    public string SecretAccessKey { get; }

    /// <summary>The region signatures must be scoped to.</summary>
    public string Region { get; }

    /// <summary>Declares an empty bucket.</summary>
    /// <param name="name">The bucket's name.</param>
    public void CreateBucket(string name) => AddNamespace(name);

    /// <inheritdoc />
    protected override IEnumerable<KeyValuePair<string, string>> CommonHeaders =>
        [new("x-amz-request-id", "fbp-test")];

    /// <inheritdoc />
    protected override Response Handle(Request request)
    {
        if (Authenticate(request) is { } refused)
        {
            return refused;
        }

        var (path, query) = Split(request.Target);
        var segments = path.TrimStart('/');
        var slash = segments.IndexOf('/', StringComparison.Ordinal);
        var bucketName = Uri.UnescapeDataString(slash < 0 ? segments : segments[..slash]);
        var key = slash < 0 ? string.Empty : Uri.UnescapeDataString(segments[(slash + 1)..]);

        if (bucketName.Length == 0)
        {
            return Error(400, "InvalidRequest", "This server answers path-style requests only.");
        }

        if (!TryGetNamespace(bucketName, out var objects))
        {
            return Error(404, "NoSuchBucket", "The specified bucket does not exist.");
        }

        if (key.Length == 0)
        {
            return request.Method switch
            {
                "HEAD" => new Response(200),
                "GET" when query.TryGetValue("list-type", out var type) && type == "2" => List(bucketName, objects, query),
                _ => Error(501, "NotImplemented", "This server serves ListObjectsV2 and HEAD on a bucket."),
            };
        }

        lock (objects)
        {
            return request.Method switch
            {
                "PUT" => Put(request, objects, key),
                "GET" => Get(request, objects, key, head: false),
                "HEAD" => Get(request, objects, key, head: true),
                "DELETE" => Delete(objects, key),
                _ => Error(405, "MethodNotAllowed", "The method is not allowed against this resource."),
            };
        }
    }

    private static Response Put(Request request, SortedDictionary<string, StoredObject> objects, string key)
    {
        var declared = request.Header("x-amz-content-sha256");
        if (declared is not "UNSIGNED-PAYLOAD" && !string.Equals(
                declared, Convert.ToHexStringLower(SHA256.HashData(request.Body)), StringComparison.Ordinal))
        {
            return Error(400, "XAmzContentSHA256Mismatch", "The provided payload hash does not match the body.");
        }

        if (request.Header("if-none-match") is "*" && objects.ContainsKey(key))
        {
            return Error(412, "PreconditionFailed", "At least one of the preconditions you specified did not hold.");
        }

        objects[key] = new StoredObject(request.Body, DateTimeOffset.UtcNow);
        return new Response(200) { ETag = ETagOf(request.Body) };
    }

    private static Response Get(Request request, SortedDictionary<string, StoredObject> objects, string key, bool head)
    {
        if (!objects.TryGetValue(key, out var stored))
        {
            return head ? new Response(404) : Error(404, "NoSuchKey", "The specified key does not exist.");
        }

        var bytes = stored.Bytes;
        if (!head && request.Header("range") is { } range)
        {
            // bytes=first-last, the only form the provider sends. The API
            // answers a range that starts inside the object and runs past its
            // end with what there is, and only one starting past the end with
            // 416 — which is why a provider must read Content-Range.
            if (!range.StartsWith("bytes=", StringComparison.Ordinal)
                || range[6..].Split('-') is not [var firstText, var lastText]
                || !long.TryParse(firstText, CultureInfo.InvariantCulture, out var first)
                || !long.TryParse(lastText, CultureInfo.InvariantCulture, out var last)
                || last < first)
            {
                return Error(400, "InvalidArgument", "Unsupported range.");
            }

            if (first >= bytes.Length)
            {
                return Error(416, "InvalidRange", "The requested range is not satisfiable.");
            }

            var end = Math.Min(last, bytes.Length - 1L);
            return new Response(206)
            {
                Body = bytes[(int)first..(int)(end + 1)],
                ContentRange = $"bytes {first}-{end}/{bytes.Length}",
                LastModified = stored.LastModified,
                ETag = ETagOf(bytes),
            };
        }

        return new Response(200)
        {
            Body = head ? null : bytes,
            HeadLength = head ? bytes.Length : null,
            LastModified = stored.LastModified,
            ETag = ETagOf(bytes),
        };
    }

    private static Response Delete(SortedDictionary<string, StoredObject> objects, string key)
    {
        // 204 whether or not there was anything there, as the API answers.
        objects.Remove(key);
        return new Response(204);
    }

    private Response List(
        string bucketName, SortedDictionary<string, StoredObject> objects, IReadOnlyDictionary<string, string> query)
    {
        var prefix = query.GetValueOrDefault("prefix") ?? string.Empty;
        var delimiter = query.GetValueOrDefault("delimiter");
        var after = query.TryGetValue("continuation-token", out var token)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(token))
            : query.GetValueOrDefault("start-after") ?? string.Empty;
        var maxKeys = Math.Min(
            query.TryGetValue("max-keys", out var maxText) ? int.Parse(maxText, CultureInfo.InvariantCulture) : 1000,
            ListPageLimit);

        List<(string Key, StoredObject Stored)> contents = [];
        SortedSet<string> commonPrefixes = new(StringComparer.Ordinal);
        string? last = null;
        var truncated = false;
        lock (objects)
        {
            foreach (var (key, stored) in objects)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal) || string.CompareOrdinal(key, after) <= 0)
                {
                    continue;
                }

                var common = delimiter is { Length: > 0 }
                    && key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal) is var cut and >= 0
                        ? key[..(cut + delimiter.Length)]
                        : null;
                if (common is not null && commonPrefixes.Contains(common))
                {
                    continue;
                }

                if (contents.Count + commonPrefixes.Count >= maxKeys)
                {
                    truncated = true;
                    break;
                }

                if (common is not null)
                {
                    // Resumes past every key the common prefix stands for.
                    commonPrefixes.Add(common);
                    last = common + "￿";
                    continue;
                }

                contents.Add((key, stored));
                last = key;
            }
        }

        var result = new XElement("ListBucketResult",
            new XElement("Name", bucketName),
            new XElement("Prefix", prefix),
            new XElement("KeyCount", contents.Count + commonPrefixes.Count),
            new XElement("MaxKeys", maxKeys),
            new XElement("IsTruncated", truncated ? "true" : "false"),
            truncated && last is not null
                ? new XElement("NextContinuationToken", Convert.ToBase64String(Encoding.UTF8.GetBytes(last)))
                : null,
            contents.Select(entry => new XElement("Contents",
                new XElement("Key", entry.Key),
                new XElement("LastModified", entry.Stored.LastModified.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
                new XElement("ETag", ETagOf(entry.Stored.Bytes)),
                new XElement("Size", entry.Stored.Bytes.Length),
                new XElement("StorageClass", "STANDARD"))),
            commonPrefixes.Select(common => new XElement("CommonPrefixes", new XElement("Prefix", common))));

        return new Response(200)
        {
            Body = Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "UTF-8", null), result).ToString()),
            ContentType = "application/xml",
        };
    }

    /// <summary>
    /// Checks the request's SigV4 signature, independently of the provider's
    /// signer: the canonical request rebuilt from what arrived on the wire.
    /// </summary>
    private Response? Authenticate(Request request)
    {
        if (request.Header("authorization") is not { } authorization
            || !authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal))
        {
            return Error(403, "AccessDenied", "Access Denied.");
        }

        var fields = authorization["AWS4-HMAC-SHA256 ".Length..]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
        if (!fields.TryGetValue("Credential", out var credential)
            || !fields.TryGetValue("SignedHeaders", out var signedHeaders)
            || !fields.TryGetValue("Signature", out var signature)
            || credential.Split('/') is not [var accessKey, var date, var region, "s3", "aws4_request"])
        {
            return Error(400, "AuthorizationHeaderMalformed", "The authorization header is malformed.");
        }

        if (!string.Equals(accessKey, AccessKeyId, StringComparison.Ordinal))
        {
            return Error(403, "InvalidAccessKeyId", "The access key id you provided does not exist in our records.");
        }

        if (request.Header("x-amz-date") is not { } amzDate || !amzDate.StartsWith(date, StringComparison.Ordinal)
            || request.Header("x-amz-content-sha256") is not { } payloadHash
            || !string.Equals(region, Region, StringComparison.Ordinal))
        {
            return Error(403, "SignatureDoesNotMatch", "The request signature we calculated does not match.");
        }

        var (path, _) = Split(request.Target);
        var rawQuery = request.Target.Contains('?', StringComparison.Ordinal)
            ? request.Target[(request.Target.IndexOf('?', StringComparison.Ordinal) + 1)..]
            : string.Empty;

        var canonical = new StringBuilder()
            .Append(request.Method).Append('\n')
            .Append(string.Join('/', path.Split('/').Select(segment => Encode(Uri.UnescapeDataString(segment)))))
            .Append('\n')
            .Append(CanonicalQuery(rawQuery)).Append('\n');
        foreach (var name in signedHeaders.Split(';'))
        {
            var value = request.Header(name) ?? string.Empty;
            canonical.Append(name).Append(':')
                .Append(string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                .Append('\n');
        }

        canonical.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);

        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{date}/{region}/s3/aws4_request\n"
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + SecretAccessKey), Encoding.UTF8.GetBytes(date));
        foreach (var part in new[] { region, "s3", "aws4_request" })
        {
            key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(part));
        }

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
        return string.Equals(expected, signature, StringComparison.Ordinal)
            ? null
            : Error(403, "SignatureDoesNotMatch", "The request signature we calculated does not match.");
    }

    private static string CanonicalQuery(string rawQuery) =>
        string.Join('&', rawQuery
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(pair => (Key: Encode(Uri.UnescapeDataString(pair[0])),
                Value: Encode(Uri.UnescapeDataString(pair.Length == 2 ? pair[1] : string.Empty))))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));

    private static string Encode(string text)
    {
        var encoded = new StringBuilder();
        foreach (var value in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)value;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~')
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }

    /// <inheritdoc />
    protected override Response Refusal(int status, string code, string message) => Error(status, code, message);

    private static Response Error(int status, string code, string message) => new(status)
    {
        Body = Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("Error",
                new XElement("Code", code),
                new XElement("Message", message),
                new XElement("RequestId", "fbp-test"))).ToString()),
        ContentType = "application/xml",
    };
}

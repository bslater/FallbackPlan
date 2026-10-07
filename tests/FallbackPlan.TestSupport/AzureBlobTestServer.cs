using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace FallbackPlan.TestSupport;

/// <summary>
/// An Azure Blob server on loopback, written from the Blob service REST API's
/// specification for the Azure Blob provider's tests (ADR-0093): containers
/// addressed path-style under the account, as the API addresses a store on
/// this machine; every request authenticated either by its Shared Key
/// signature, checked by a verifier of its own rather than the provider's
/// signer, or by a shared access signature the server issued, whose
/// container, permissions and expiry it holds the request to; create-only
/// puts by <c>If-None-Match: *</c>; every body checked against its
/// <c>Content-MD5</c>; ranged reads answered as the API answers them; and
/// List Blobs in pages, each resumed from a marker only the server can read.
/// </summary>
/// <remarks>
/// It answers as the API does where the provider has to cope, not where it
/// would be convenient: a create of an existing blob is a 409 naming
/// <c>BlobAlreadyExists</c>, a range that runs past the end is a short 206,
/// one that starts past it a 416, a delete of nothing a 404, and an error an
/// XML body whose code is repeated in <c>x-ms-error-code</c> — the only place
/// a HEAD can carry it. Its listing is strongly consistent, as the API
/// promises.
/// </remarks>
public sealed class AzureBlobTestServer : ObjectStoreTestServer
{
    /// <summary>The storage account every request names unless another is given.</summary>
    public const string DefaultAccount = "fbptestaccount";

    /// <summary>The account key that signs for <see cref="DefaultAccount"/>: 64 bytes, base64, as the API issues one.</summary>
    public const string DefaultAccountKey =
        "BwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiMkJSYnKCkqKywtLi8wMTIzNDU2Nzg5Ojs8PT4/QEFCQ0RFRg==";

    /// <summary>The REST API version the server answers with.</summary>
    public const string ApiVersion = "2024-11-04";

    private readonly ConcurrentDictionary<string, IssuedSignature> _signatures = new(StringComparer.Ordinal);

    /// <summary>Starts a server on an ephemeral loopback port.</summary>
    /// <param name="account">The one storage account it serves.</param>
    /// <param name="accountKey">The account key that signs for it, base64.</param>
    public AzureBlobTestServer(string account = DefaultAccount, string accountKey = DefaultAccountKey)
    {
        Account = account;
        AccountKey = accountKey;
    }

    /// <summary>
    /// The account's blob endpoint, path-style as on this machine:
    /// <c>http://127.0.0.1:port/account</c>.
    /// </summary>
    public Uri Endpoint => new($"{Origin.GetLeftPart(UriPartial.Authority)}/{Account}");

    /// <summary>The storage account it serves.</summary>
    public string Account { get; }

    /// <summary>The account key that signs for <see cref="Account"/>, base64.</summary>
    public string AccountKey { get; }

    /// <summary>Declares an empty container.</summary>
    /// <param name="name">The container's name.</param>
    public void CreateContainer(string name) => AddNamespace(name);

    /// <summary>
    /// Issues a shared access signature for one container, as a person would
    /// from the account: the token, in the query form the API hands out.
    /// </summary>
    /// <param name="container">The container it grants access to.</param>
    /// <param name="permissions">What it allows, in the API's letters: r, a, c, w, d, l.</param>
    /// <param name="expires">When it stops being honoured; a day from now when null.</param>
    public string IssueSas(string container, string permissions = "racwdl", DateTimeOffset? expires = null)
    {
        var expiry = (expires ?? DateTimeOffset.UtcNow.AddDays(1)).ToUniversalTime();
        var signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["sv"] = ApiVersion,
            ["sr"] = "c",
            ["sp"] = permissions,
            ["se"] = expiry.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["spr"] = "https,http",
            ["sig"] = signature,
        };
        _signatures[signature] = new IssuedSignature(container, permissions, expiry, fields);

        string[] order = ["sv", "sr", "sp", "se", "spr", "sig"];
        return string.Join('&', order.Select(name => $"{name}={Uri.EscapeDataString(fields[name])}"));
    }

    /// <inheritdoc />
    protected override IEnumerable<KeyValuePair<string, string>> CommonHeaders =>
        [new("x-ms-request-id", "fbp-test"), new("x-ms-version", ApiVersion)];

    /// <inheritdoc />
    protected override Response Handle(Request request)
    {
        var (path, query) = Split(request.Target);
        var segments = path.TrimStart('/').Split('/', 3);
        if (segments[0] != Account)
        {
            return Error(400, "InvalidUri", "The requested URI does not represent any resource on the server.");
        }

        var containerName = segments.Length > 1 ? Uri.UnescapeDataString(segments[1]) : string.Empty;
        var blob = segments.Length > 2 ? Uri.UnescapeDataString(segments[2]) : string.Empty;

        if (Authenticate(request, path, query, containerName, blob) is { } refused)
        {
            return refused;
        }

        if (containerName.Length == 0)
        {
            return Error(400, "InvalidQueryParameterValue", "This server serves containers and blobs only.");
        }

        if (!TryGetNamespace(containerName, out var objects))
        {
            return Error(404, "ContainerNotFound", "The specified container does not exist.");
        }

        if (blob.Length == 0)
        {
            return (request.Method, query.GetValueOrDefault("restype"), query.GetValueOrDefault("comp")) switch
            {
                ("GET", "container", "list") => List(objects, query),
                ("GET" or "HEAD", "container", null) => new Response(200),
                _ => Error(400, "UnsupportedQueryParameter", "This server serves List Blobs on a container."),
            };
        }

        lock (objects)
        {
            return request.Method switch
            {
                "PUT" => Put(request, objects, blob),
                "GET" => Get(request, objects, blob, head: false),
                "HEAD" => Get(request, objects, blob, head: true),
                "DELETE" => Delete(objects, blob),
                _ => Error(405, "UnsupportedHttpVerb", "The resource doesn't support the specified HTTP verb."),
            };
        }
    }

    /// <inheritdoc />
    protected override Response Refusal(int status, string code, string message) => Error(status, code, message);

    private static Response Put(Request request, SortedDictionary<string, StoredObject> objects, string blob)
    {
        if (request.Header("x-ms-blob-type") is not "BlockBlob")
        {
            return Error(400, "MissingRequiredHeader", "An HTTP header that's mandatory for this request is not specified.");
        }

        if (request.Header("content-md5") is { } declared
            && !string.Equals(declared, Md5Of(request.Body), StringComparison.Ordinal))
        {
            return Error(400, "Md5Mismatch", "The MD5 value specified in the request did not match with the MD5 value calculated by the server.");
        }

        if (request.Header("if-none-match") is "*" && objects.ContainsKey(blob))
        {
            return Error(409, "BlobAlreadyExists", "The specified blob already exists.");
        }

        var stored = new StoredObject(request.Body, DateTimeOffset.UtcNow);
        objects[blob] = stored;
        return new Response(201)
        {
            ETag = ETagOf(request.Body),
            LastModified = stored.LastModified,
            Headers = new Dictionary<string, string>
            {
                ["Content-MD5"] = Md5Of(request.Body),
                ["x-ms-request-server-encrypted"] = "true",
            },
        };
    }

    private static Response Get(Request request, SortedDictionary<string, StoredObject> objects, string blob, bool head)
    {
        if (!objects.TryGetValue(blob, out var stored))
        {
            return head
                ? new Response(404) { Headers = new Dictionary<string, string> { ["x-ms-error-code"] = "BlobNotFound" } }
                : Error(404, "BlobNotFound", "The specified blob does not exist.");
        }

        var bytes = stored.Bytes;
        if (!head && (request.Header("x-ms-range") ?? request.Header("range")) is { } range)
        {
            // bytes=first-last, the only form the provider sends. The API
            // answers a range that starts inside the blob and runs past its
            // end with what there is, and one starting at or past the end with
            // 416 — which is why a provider must read Content-Range.
            if (!range.StartsWith("bytes=", StringComparison.Ordinal)
                || range[6..].Split('-') is not [var firstText, var lastText]
                || !long.TryParse(firstText, CultureInfo.InvariantCulture, out var first)
                || !long.TryParse(lastText, CultureInfo.InvariantCulture, out var last)
                || last < first)
            {
                return Error(400, "InvalidHeaderValue", "The value for one of the HTTP headers is not in the correct format.");
            }

            if (first >= bytes.Length)
            {
                var refused = Error(416, "InvalidRange", "The range specified is invalid for the current size of the resource.");
                return refused with { ContentRange = $"bytes */{bytes.Length}" };
            }

            var end = Math.Min(last, bytes.Length - 1L);
            return new Response(206)
            {
                Body = bytes[(int)first..(int)(end + 1)],
                ContentRange = $"bytes {first}-{end}/{bytes.Length}",
                LastModified = stored.LastModified,
                ETag = ETagOf(bytes),
                Headers = new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob" },
            };
        }

        return new Response(200)
        {
            Body = head ? null : bytes,
            HeadLength = head ? bytes.Length : null,
            ContentType = "application/octet-stream",
            LastModified = stored.LastModified,
            ETag = ETagOf(bytes),
            Headers = new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob" },
        };
    }

    private static Response Delete(SortedDictionary<string, StoredObject> objects, string blob) =>
        objects.Remove(blob)
            ? new Response(202)
            : Error(404, "BlobNotFound", "The specified blob does not exist.");

    private Response List(SortedDictionary<string, StoredObject> objects, IReadOnlyDictionary<string, string> query)
    {
        var prefix = query.GetValueOrDefault("prefix") ?? string.Empty;
        var delimiter = query.GetValueOrDefault("delimiter");
        var marker = query.GetValueOrDefault("marker") is { Length: > 0 } issued ? ReadMarker(issued) : string.Empty;
        if (marker is null)
        {
            return Error(400, "OutOfRangeInput", "One of the request inputs is out of range.");
        }

        var maxResults = Math.Min(
            query.TryGetValue("maxresults", out var maxText) ? int.Parse(maxText, CultureInfo.InvariantCulture) : 5000,
            ListPageLimit);

        List<(string Name, StoredObject Stored)> blobs = [];
        SortedSet<string> prefixes = new(StringComparer.Ordinal);
        string? last = null;
        var truncated = false;
        lock (objects)
        {
            foreach (var (name, stored) in objects)
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || string.CompareOrdinal(name, marker) <= 0)
                {
                    continue;
                }

                var common = delimiter is { Length: > 0 }
                    && name.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal) is var cut and >= 0
                        ? name[..(cut + delimiter.Length)]
                        : null;
                if (common is not null && prefixes.Contains(common))
                {
                    continue;
                }

                if (blobs.Count + prefixes.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                if (common is not null)
                {
                    // Resumes past every name the prefix stands for.
                    prefixes.Add(common);
                    last = common + "￿";
                    continue;
                }

                blobs.Add((name, stored));
                last = name;
            }
        }

        var entries = blobs
            .Select(entry => (entry.Name, Element: new XElement("Blob",
                new XElement("Name", entry.Name),
                new XElement("Properties",
                    new XElement("Last-Modified", entry.Stored.LastModified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
                    new XElement("Etag", ETagOf(entry.Stored.Bytes).Trim('"')),
                    new XElement("Content-Length", entry.Stored.Bytes.Length),
                    new XElement("Content-Type", "application/octet-stream"),
                    new XElement("BlobType", "BlockBlob")))))
            .Concat(prefixes.Select(common => (Name: common, Element: new XElement("BlobPrefix", new XElement("Name", common)))))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => entry.Element);

        var result = new XElement("EnumerationResults",
            new XAttribute("ServiceEndpoint", Endpoint.AbsoluteUri),
            new XElement("Prefix", prefix),
            query.ContainsKey("marker") ? new XElement("Marker", query["marker"]) : null,
            new XElement("MaxResults", maxResults),
            delimiter is null ? null : new XElement("Delimiter", delimiter),
            new XElement("Blobs", entries),
            new XElement("NextMarker", truncated && last is not null ? IssueMarker(last) : string.Empty));

        return new Response(200)
        {
            Body = Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "utf-8", null), result).ToString()),
            ContentType = "application/xml",
        };
    }

    /// <summary>
    /// A marker no client can build or read: the name a page ended on,
    /// sealed. The API promises nothing about its markers but that they
    /// resume the listing that issued them.
    /// </summary>
    private static string IssueMarker(string last) =>
        "2!" + Convert.ToBase64String(Encoding.UTF8.GetBytes("fbp-marker:" + last)).Replace('+', '-').Replace('/', '_');

    private static string? ReadMarker(string marker)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(marker[2..].Replace('-', '+').Replace('_', '/')));
            return marker.StartsWith("2!", StringComparison.Ordinal) && text.StartsWith("fbp-marker:", StringComparison.Ordinal)
                ? text["fbp-marker:".Length..]
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Authenticates a request by the signature it carries in its query, when
    /// it carries one, and otherwise by its Shared Key signature — the string
    /// to sign rebuilt from what arrived on the wire, independently of the
    /// provider's signer.
    /// </summary>
    private Response? Authenticate(
        Request request, string path, IReadOnlyDictionary<string, string> query, string container, string blob)
    {
        if (request.Header("x-ms-version") is null && !query.ContainsKey("sv"))
        {
            return Error(400, "MissingRequiredHeader", "An HTTP header that's mandatory for this request is not specified.");
        }

        if (query.TryGetValue("sig", out var sig))
        {
            return AuthenticateSas(request, query, sig, container, blob);
        }

        if (request.Header("authorization") is not { } authorization
            || !authorization.StartsWith("SharedKey ", StringComparison.Ordinal))
        {
            return Error(403, "NoAuthenticationInformation", "Server failed to authenticate the request. Please refer to the information in the www-authenticate header.");
        }

        var credential = authorization["SharedKey ".Length..];
        var colon = credential.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || credential[..colon] != Account || request.Header("x-ms-date") is null)
        {
            return Error(403, "AuthenticationFailed", "Server failed to authenticate the request. Make sure the value of Authorization header is formed correctly including the signature.");
        }

        var stringToSign = new StringBuilder()
            .Append(request.Method).Append('\n')
            .Append(request.Header("content-encoding")).Append('\n')
            .Append(request.Header("content-language")).Append('\n')
            .Append(request.Header("content-length") is "0" ? null : request.Header("content-length")).Append('\n')
            .Append(request.Header("content-md5")).Append('\n')
            .Append(request.Header("content-type")).Append('\n')
            .Append(request.Header("date")).Append('\n')
            .Append(request.Header("if-modified-since")).Append('\n')
            .Append(request.Header("if-match")).Append('\n')
            .Append(request.Header("if-none-match")).Append('\n')
            .Append(request.Header("if-unmodified-since")).Append('\n')
            .Append(request.Header("range")).Append('\n');
        foreach (var (name, value) in request.Headers
            .Where(header => header.Key.StartsWith("x-ms-", StringComparison.Ordinal))
            .OrderBy(header => header.Key, StringComparer.Ordinal))
        {
            stringToSign.Append(name).Append(':')
                .Append(string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                .Append('\n');
        }

        stringToSign.Append('/').Append(Account).Append(path);
        foreach (var (name, value) in query.OrderBy(pair => pair.Key.ToLowerInvariant(), StringComparer.Ordinal))
        {
            stringToSign.Append('\n').Append(name.ToLowerInvariant()).Append(':').Append(value);
        }

        var expected = Convert.ToBase64String(
            HMACSHA256.HashData(Convert.FromBase64String(AccountKey), Encoding.UTF8.GetBytes(stringToSign.ToString())));
        return string.Equals(expected, credential[(colon + 1)..], StringComparison.Ordinal)
            ? null
            : Error(403, "AuthenticationFailed", "Server failed to authenticate the request. Make sure the value of Authorization header is formed correctly including the signature.");
    }

    private Response? AuthenticateSas(
        Request request, IReadOnlyDictionary<string, string> query, string sig, string container, string blob)
    {
        // The token must arrive exactly as it was issued: every field the
        // signature covers, unchanged, and once each.
        if (!_signatures.TryGetValue(sig, out var issued)
            || issued.Fields.Any(field => query.GetValueOrDefault(field.Key) != field.Value))
        {
            return Error(403, "AuthenticationFailed", "Server failed to authenticate the request. Make sure the value of Authorization header is formed correctly including the signature.");
        }

        if (issued.Expires <= DateTimeOffset.UtcNow)
        {
            return Error(403, "AuthenticationFailed", "Signed expiry time has to be after signed start time; signature not valid in the specified time frame.");
        }

        var needed = (request.Method, blob.Length == 0) switch
        {
            ("GET", true) => "l",
            ("GET" or "HEAD", false) => "r",
            ("PUT", false) => "cw",
            ("DELETE", false) => "d",
            _ => "r",
        };
        return container == issued.Container && needed.Any(issued.Permissions.Contains)
            ? null
            : Error(403, "AuthorizationPermissionMismatch", "This request is not authorized to perform this operation using this permission.");
    }

    private static Response Error(int status, string code, string message) => new(status)
    {
        Body = Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("Error",
                new XElement("Code", code),
                new XElement("Message", $"{message}\nRequestId:fbp-test\nTime:2026-10-06T12:00:00.0000000Z"))).ToString()),
        ContentType = "application/xml",
        Headers = new Dictionary<string, string> { ["x-ms-error-code"] = code },
    };

    // The API's own check that a body arrived as it was sent, which it names
    // MD5; nothing here relies on it to keep anything secret.
#pragma warning disable CA5351
    private static string Md5Of(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
#pragma warning restore CA5351

    private sealed record IssuedSignature(
        string Container, string Permissions, DateTimeOffset Expires, IReadOnlyDictionary<string, string> Fields);
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
public sealed class S3CompatibleTestServer : IAsyncDisposable
{
    /// <summary>The access key id every request must be signed with unless another is given.</summary>
    public const string DefaultAccessKeyId = "FBPTESTACCESSKEY0001";

    /// <summary>The secret that signs for <see cref="DefaultAccessKeyId"/>.</summary>
    public const string DefaultSecretAccessKey = "fbp/test+secret=key/0123456789abcdefghij";

    /// <summary>The region the signatures are scoped to.</summary>
    public const string DefaultRegion = "test-region-1";

    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private readonly ConcurrentDictionary<string, SortedDictionary<string, StoredObject>> _buckets =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Lock _gate = new();
    private readonly Queue<Fault> _faults = new();

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
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _accepting = AcceptAsync();
    }

    /// <summary>Where the server listens: <c>http://127.0.0.1:port</c>.</summary>
    public Uri Endpoint { get; }

    /// <summary>The access key id it accepts.</summary>
    public string AccessKeyId { get; }

    /// <summary>The secret that signs for <see cref="AccessKeyId"/>.</summary>
    public string SecretAccessKey { get; }

    /// <summary>The region signatures must be scoped to.</summary>
    public string Region { get; }

    /// <summary>
    /// The most keys one listing page carries, whatever the request asked
    /// for; lowered by a test to make a short listing span several pages.
    /// </summary>
    public int ListPageLimit { get; set; } = 1000;

    /// <summary>Every request answered so far, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    /// <summary>Declares an empty bucket.</summary>
    /// <param name="name">The bucket's name.</param>
    public void CreateBucket(string name) =>
        _buckets.TryAdd(name, new SortedDictionary<string, StoredObject>(StringComparer.Ordinal));

    /// <summary>The keys a bucket holds, in the order a listing gives them.</summary>
    /// <param name="bucket">The bucket.</param>
    public IReadOnlyList<string> KeysIn(string bucket)
    {
        var objects = _buckets[bucket];
        lock (objects)
        {
            return [.. objects.Keys];
        }
    }

    /// <summary>An object's bytes, or null when the bucket holds no such key.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <param name="key">The object's full key.</param>
    public byte[]? ObjectIn(string bucket, string key)
    {
        var objects = _buckets[bucket];
        lock (objects)
        {
            return objects.TryGetValue(key, out var stored) ? stored.Bytes : null;
        }
    }

    /// <summary>Writes an object behind the API's back, as storage that altered it would.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <param name="key">The object's full key.</param>
    /// <param name="bytes">What it now holds.</param>
    public void Overwrite(string bucket, string key, byte[] bytes)
    {
        var objects = _buckets[bucket];
        lock (objects)
        {
            objects[key] = new StoredObject(bytes, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Deletes an object behind the API's back, as anyone else holding a key to the bucket could.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <param name="key">The object's full key.</param>
    public void Remove(string bucket, string key)
    {
        var objects = _buckets[bucket];
        lock (objects)
        {
            objects.Remove(key);
        }
    }

    /// <summary>
    /// Answers the next <paramref name="count"/> requests with an error of
    /// the API's own shape instead of serving them.
    /// </summary>
    /// <param name="count">How many requests to fail.</param>
    /// <param name="status">The HTTP status, e.g. 503.</param>
    /// <param name="code">The error code in the body, e.g. <c>SlowDown</c>.</param>
    public void FailNext(int count, int status, string code)
    {
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
            {
                _faults.Enqueue(new Fault(status, code, Drop: false, Truncate: false));
            }
        }
    }

    /// <summary>Closes the connection on the next request without answering it.</summary>
    public void DropNext()
    {
        lock (_gate)
        {
            _faults.Enqueue(new Fault(0, string.Empty, Drop: true, Truncate: false));
        }
    }

    /// <summary>
    /// Serves the next request's headers whole and then only half its body
    /// before closing the connection, as a link that died mid-transfer would.
    /// </summary>
    public void TruncateNextBody()
    {
        lock (_gate)
        {
            _faults.Enqueue(new Fault(0, string.Empty, Drop: false, Truncate: true));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        try
        {
            await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }

        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            _connections.Add(Task.Run(() => ServeAsync(client)));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new RequestReader(stream);
                while (!_stopping.IsCancellationRequested)
                {
                    var request = await reader.ReadAsync(_stopping.Token).ConfigureAwait(false);
                    if (request is null)
                    {
                        return;
                    }

                    var fault = NextFault();
                    if (fault is { Drop: true })
                    {
                        Record(request, 0);
                        return;
                    }

                    var response = fault is { Drop: false, Truncate: false }
                        ? Error(fault.Status, fault.Code, "Fault served by the test.")
                        : Handle(request);
                    Record(request, response.Status);
                    await WriteAsync(stream, request, response, truncate: fault is { Truncate: true }).ConfigureAwait(false);
                    if (fault is { Truncate: true } || request.Header("connection") is "close")
                    {
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // A client that hung up, or the server stopping: either way
                // this connection has nothing more to say.
            }
        }
    }

    private Fault? NextFault()
    {
        lock (_gate)
        {
            return _faults.TryDequeue(out var fault) ? fault : null;
        }
    }

    private void Record(Request request, int status) =>
        _requests.Enqueue(new RecordedRequest(request.Method, request.Target, request.Headers, status));

    private Response Handle(Request request)
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

        if (!_buckets.TryGetValue(bucketName, out var objects))
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

    private static (string Path, IReadOnlyDictionary<string, string> Query) Split(string target)
    {
        var mark = target.IndexOf('?', StringComparison.Ordinal);
        if (mark < 0)
        {
            return (target, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            query[Uri.UnescapeDataString(parts[0])] = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        return (target[..mark], query);
    }

    // Opaque to every client, so any digest will do; MD5 is what the API happens to use.
    private static string ETagOf(byte[] bytes) => $"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))[..32]}\"";

    private static Response Error(int status, string code, string message) => new(status)
    {
        Body = Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("Error",
                new XElement("Code", code),
                new XElement("Message", message),
                new XElement("RequestId", "fbp-test"))).ToString()),
        ContentType = "application/xml",
    };

    private static async Task WriteAsync(Stream stream, Request request, Response response, bool truncate)
    {
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {ReasonOf(response.Status)}\r\n")
            .Append("x-amz-request-id: fbp-test\r\n");
        var length = response.HeadLength ?? response.Body?.Length ?? 0;
        if (response.Status != 204)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Length: {length}\r\n");
        }

        if (response.ContentType is { } contentType)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Type: {contentType}\r\n");
        }

        if (response.ContentRange is { } contentRange)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Range: {contentRange}\r\n");
        }

        if (response.LastModified is { } lastModified)
        {
            head.Append(CultureInfo.InvariantCulture, $"Last-Modified: {lastModified.UtcDateTime:R}\r\n");
        }

        if (response.ETag is { } etag)
        {
            head.Append(CultureInfo.InvariantCulture, $"ETag: {etag}\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        if (request.Method != "HEAD" && response.Body is { Length: > 0 } body)
        {
            await stream.WriteAsync(truncate ? body.AsMemory(0, body.Length / 2) : body).ConfigureAwait(false);
        }

        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string ReasonOf(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        206 => "Partial Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        412 => "Precondition Failed",
        416 => "Requested Range Not Satisfiable",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        _ => "Status",
    };

    /// <summary>One request as the server answered it.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Target">The request target as sent: path and query, still encoded.</param>
    /// <param name="Headers">The request's headers, by lower-case name.</param>
    /// <param name="Status">The status it was answered with; 0 for a connection dropped unanswered.</param>
    public sealed record RecordedRequest(
        string Method, string Target, IReadOnlyDictionary<string, string> Headers, int Status);

    private sealed record StoredObject(byte[] Bytes, DateTimeOffset LastModified);

    private sealed record Fault(int Status, string Code, bool Drop, bool Truncate);

    private sealed record Response(int Status)
    {
        public byte[]? Body { get; init; }

        public long? HeadLength { get; init; }

        public string? ContentType { get; init; }

        public string? ContentRange { get; init; }

        public DateTimeOffset? LastModified { get; init; }

        public string? ETag { get; init; }
    }

    private sealed record Request(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    /// <summary>Reads HTTP/1.1 requests off one connection: a head, then a body by length or in chunks.</summary>
    private sealed class RequestReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        public async Task<Request?> ReadAsync(CancellationToken cancellationToken)
        {
            var requestLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (requestLine is null)
            {
                return null;
            }

            var parts = requestLine.Split(' ');
            if (parts.Length != 3)
            {
                throw new IOException($"Malformed request line '{requestLine}'.");
            }

            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            while (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } line)
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var name = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                headers[name] = headers.TryGetValue(name, out var earlier) ? $"{earlier},{value}" : value;
            }

            byte[] body;
            if (headers.GetValueOrDefault("transfer-encoding") is { } encoding
                && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using var collected = new MemoryStream();
                while (true)
                {
                    var sizeLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("The connection closed inside a chunked body.");
                    var size = int.Parse(sizeLine.Split(';')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (size == 0)
                    {
                        while (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 })
                        {
                        }

                        break;
                    }

                    collected.Write(await ReadExactlyAsync(size, cancellationToken).ConfigureAwait(false));
                    _ = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }

                body = collected.ToArray();
            }
            else
            {
                var length = headers.TryGetValue("content-length", out var lengthText)
                    ? int.Parse(lengthText, CultureInfo.InvariantCulture)
                    : 0;
                body = await ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
            }

            return new Request(parts[0], parts[1], headers, body);
        }

        private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new List<byte>();
            while (true)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    return line.Count == 0 ? null : throw new IOException("The connection closed mid-line.");
                }

                var value = _buffer[_start++];
                if (value == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }

                    return Encoding.ASCII.GetString([.. line]);
                }

                line.Add(value);
            }
        }

        private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
        {
            var result = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new IOException("The connection closed inside a body.");
                }

                var take = Math.Min(count - filled, _end - _start);
                Buffer.BlockCopy(_buffer, _start, result, filled, take);
                _start += take;
                filled += take;
            }

            return result;
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            return _end > 0;
        }
    }
}

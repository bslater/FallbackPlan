using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FallbackPlan.TestSupport;

/// <summary>
/// What every object-store test server shares (ADR-0091, ADR-0093): an
/// HTTP/1.1 listener on loopback, the objects it holds by bucket or container
/// and key, every request it answered, and the faults a test queues, served
/// in order. A subclass speaks one API over it — how a request is
/// authenticated, routed and answered, and what an error looks like.
/// </summary>
public abstract class ObjectStoreTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private readonly ConcurrentDictionary<string, SortedDictionary<string, StoredObject>> _namespaces =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Lock _gate = new();
    private readonly Queue<Fault> _faults = new();
    private StandingFault? _standing;
    private Dictionary<string, HashSet<string>>? _listedAsOf;
    private string _lagUnder = string.Empty;

    /// <summary>Starts listening on an ephemeral loopback port.</summary>
    protected ObjectStoreTestServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _accepting = AcceptAsync();
    }

    /// <summary>Where the server listens: <c>http://127.0.0.1:port</c>, with no path.</summary>
    public Uri Origin { get; }

    /// <summary>
    /// The most entries one listing page carries, whatever the request asked
    /// for; lowered by a test to make a short listing span several pages.
    /// </summary>
    public int ListPageLimit { get; set; } = 1000;

    /// <summary>Every request answered so far, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    /// <summary>Every listing page asked for so far, in order: each one a request the store's owner pays for.</summary>
    public IReadOnlyList<RecordedRequest> Listings => [.. _requests.Where(IsListing)];

    /// <summary>The keys a bucket or container holds, in the order a listing gives them.</summary>
    /// <param name="name">The bucket or container.</param>
    public IReadOnlyList<string> KeysIn(string name)
    {
        var objects = _namespaces[name];
        lock (objects)
        {
            return [.. objects.Keys];
        }
    }

    /// <summary>An object's bytes, or null when the bucket or container holds no such key.</summary>
    /// <param name="name">The bucket or container.</param>
    /// <param name="key">The object's full key.</param>
    public byte[]? ObjectIn(string name, string key)
    {
        var objects = _namespaces[name];
        lock (objects)
        {
            return objects.TryGetValue(key, out var stored) ? stored.Bytes : null;
        }
    }

    /// <summary>Writes an object behind the API's back, as storage that altered it would.</summary>
    /// <param name="name">The bucket or container.</param>
    /// <param name="key">The object's full key.</param>
    /// <param name="bytes">What it now holds.</param>
    public void Overwrite(string name, string key, byte[] bytes)
    {
        var objects = _namespaces[name];
        lock (objects)
        {
            objects[key] = new StoredObject(bytes, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Deletes an object behind the API's back, as anyone else holding a key to it could.</summary>
    /// <param name="name">The bucket or container.</param>
    /// <param name="key">The object's full key.</param>
    public void Remove(string name, string key)
    {
        var objects = _namespaces[name];
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
    /// <param name="code">The API's error code, e.g. <c>SlowDown</c> or <c>ServerBusy</c>.</param>
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

    /// <summary>
    /// Serves the next <paramref name="servedFirst"/> requests, then answers
    /// every one after them with an error of the API's own shape until
    /// <see cref="Recover"/>: a store that goes on refusing, as one that is
    /// throttling, full or no longer taking a credential does. Only
    /// <paramref name="method"/> requests count and are refused, when one is
    /// named; every other request is served throughout.
    /// </summary>
    /// <param name="servedFirst">How many matching requests to serve before the refusals begin.</param>
    /// <param name="status">The HTTP status, e.g. 503.</param>
    /// <param name="code">The API's error code.</param>
    /// <param name="method">The method refused, or null for every request.</param>
    public void FailFrom(int servedFirst, int status, string code, string? method = null)
    {
        lock (_gate)
        {
            _standing = new StandingFault(status, code, Drop: false, method, servedFirst);
        }
    }

    /// <summary>
    /// As <see cref="FailFrom"/>, but closes each refused request's
    /// connection without answering it: a link that stays down from that
    /// request on.
    /// </summary>
    /// <param name="servedFirst">How many matching requests to serve before the drops begin.</param>
    /// <param name="method">The method dropped, or null for every request.</param>
    public void DropFrom(int servedFirst, string? method = null)
    {
        lock (_gate)
        {
            _standing = new StandingFault(0, string.Empty, Drop: true, method, servedFirst);
        }
    }

    /// <summary>Ends a standing fault: every request is served again.</summary>
    public void Recover()
    {
        lock (_gate)
        {
            _standing = null;
        }
    }

    /// <summary>
    /// From now on, a listing leaves out every object written under
    /// <paramref name="under"/> after this call, as a store whose listings
    /// lag its writes does: the object reads, and a HEAD finds it, but no
    /// listing shows it until <see cref="CatchUpListings"/>.
    /// </summary>
    /// <param name="under">The key prefix whose new objects listings lag behind; every key when empty.</param>
    public void LagListings(string under = "")
    {
        lock (_gate)
        {
            _lagUnder = under;
            _listedAsOf = _namespaces.ToDictionary(
                pair => pair.Key,
                pair =>
                {
                    lock (pair.Value)
                    {
                        return new HashSet<string>(pair.Value.Keys, StringComparer.Ordinal);
                    }
                },
                StringComparer.Ordinal);
        }
    }

    /// <summary>Ends a listing lag: every listing shows every object again.</summary>
    public void CatchUpListings()
    {
        lock (_gate)
        {
            _listedAsOf = null;
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
        GC.SuppressFinalize(this);
    }

    /// <summary>Declares an empty bucket or container.</summary>
    /// <param name="name">Its name.</param>
    protected void AddNamespace(string name) =>
        _namespaces.TryAdd(name, new SortedDictionary<string, StoredObject>(StringComparer.Ordinal));

    /// <summary>The objects a bucket or container holds, or false when none is declared by that name.</summary>
    /// <param name="name">The bucket or container.</param>
    /// <param name="objects">Its objects, locked by the caller while it reads or writes them.</param>
    protected bool TryGetNamespace(string name, out SortedDictionary<string, StoredObject> objects) =>
        _namespaces.TryGetValue(name, out objects!);

    /// <summary>Whether a listing shows <paramref name="key"/> yet, under any lag a test began.</summary>
    /// <param name="name">The bucket or container.</param>
    /// <param name="key">The object's full key.</param>
    protected bool ListingShows(string name, string key)
    {
        lock (_gate)
        {
            return _listedAsOf is null
                || !key.StartsWith(_lagUnder, StringComparison.Ordinal)
                || (_listedAsOf.TryGetValue(name, out var listed) && listed.Contains(key));
        }
    }

    /// <summary>Whether a request asked for a listing page, in the API's own terms.</summary>
    /// <param name="request">The request as answered.</param>
    protected abstract bool IsListing(RecordedRequest request);

    /// <summary>Authenticates, routes and answers one request in the API's own terms.</summary>
    /// <param name="request">The request as it arrived.</param>
    protected abstract Response Handle(Request request);

    /// <summary>An error in the API's own shape: its status, its code and its message.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="code">The API's error code.</param>
    /// <param name="message">The message.</param>
    protected abstract Response Refusal(int status, string code, string message);

    /// <summary>The headers every answer carries, such as the API's request id.</summary>
    protected abstract IEnumerable<KeyValuePair<string, string>> CommonHeaders { get; }

    /// <summary>
    /// An opaque entity tag. Opaque to every client, so any digest will do;
    /// neither API promises what it is computed from.
    /// </summary>
    /// <param name="bytes">The object's bytes.</param>
    protected static string ETagOf(byte[] bytes) => $"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))[..32]}\"";

    /// <summary>Splits a request target into its path and its decoded query.</summary>
    /// <param name="target">The target as sent: path and query, still encoded.</param>
    protected static (string Path, IReadOnlyDictionary<string, string> Query) Split(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
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

                    var fault = NextFault(request);
                    if (fault is { Drop: true })
                    {
                        Record(request, 0);
                        return;
                    }

                    var response = fault is { Drop: false, Truncate: false }
                        ? Refusal(fault.Status, fault.Code, "Fault served by the test.")
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

    private Fault? NextFault(Request request)
    {
        lock (_gate)
        {
            if (_faults.TryDequeue(out var fault))
            {
                return fault;
            }

            if (_standing is not { } standing || (standing.Method is { } method && method != request.Method))
            {
                return null;
            }

            if (standing.ServedFirst > 0)
            {
                _standing = standing with { ServedFirst = standing.ServedFirst - 1 };
                return null;
            }

            return new Fault(standing.Status, standing.Code, standing.Drop, Truncate: false);
        }
    }

    private void Record(Request request, int status) =>
        _requests.Enqueue(new RecordedRequest(request.Method, request.Target, request.Headers, status));

    private async Task WriteAsync(Stream stream, Request request, Response response, bool truncate)
    {
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {ReasonOf(response.Status)}\r\n");
        foreach (var (name, value) in CommonHeaders)
        {
            head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }

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

        if (response.Headers is { } headers)
        {
            foreach (var (name, value) in headers)
            {
                head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
            }
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
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        206 => "Partial Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
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

    /// <summary>One stored object.</summary>
    /// <param name="Bytes">What it holds.</param>
    /// <param name="LastModified">When it was last written.</param>
    protected sealed record StoredObject(byte[] Bytes, DateTimeOffset LastModified);

    private sealed record Fault(int Status, string Code, bool Drop, bool Truncate);

    private sealed record StandingFault(int Status, string Code, bool Drop, string? Method, int ServedFirst);

    /// <summary>An answer, before it is written to the wire.</summary>
    /// <param name="Status">The HTTP status.</param>
    protected sealed record Response(int Status)
    {
        /// <summary>The body, or null for none.</summary>
        public byte[]? Body { get; init; }

        /// <summary>The length a HEAD answer declares for a body it does not send.</summary>
        public long? HeadLength { get; init; }

        /// <summary>The body's content type.</summary>
        public string? ContentType { get; init; }

        /// <summary>The range of the object a partial answer carries.</summary>
        public string? ContentRange { get; init; }

        /// <summary>When the object was last written.</summary>
        public DateTimeOffset? LastModified { get; init; }

        /// <summary>The object's entity tag.</summary>
        public string? ETag { get; init; }

        /// <summary>Any further headers the API answers with.</summary>
        public IReadOnlyDictionary<string, string>? Headers { get; init; }
    }

    /// <summary>A request as it arrived.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Target">The request target: path and query, still encoded.</param>
    /// <param name="Headers">Its headers, by lower-case name.</param>
    /// <param name="Body">Its body, whole.</param>
    protected sealed record Request(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        /// <summary>A header's value, or null when the request did not carry it.</summary>
        /// <param name="name">The header's lower-case name.</param>
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

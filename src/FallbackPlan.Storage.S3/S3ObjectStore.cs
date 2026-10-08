using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.S3.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Storage.S3;

/// <summary>
/// An <see cref="IObjectStore"/> over the S3 API (FR-REP-002, ADR-0091): one
/// bucket of an S3-compatible store, under a prefix, spoken to over the
/// platform's HTTP client with requests this assembly signs.
/// </summary>
/// <remarks>
/// <para>
/// The API differs from the contract in four places, and each is bridged
/// here rather than left to callers. A put to a live key overwrites unless
/// it asks not to, so every put is sent <c>If-None-Match: *</c>, whatever its
/// conditions, and a 412 is <see cref="PutOutcome.AlreadyExists"/>. A range
/// that runs past the end is answered short, so the range served is read
/// back from <c>Content-Range</c>. A delete of nothing succeeds, so a delete
/// looks first. And the content factory must be read once on a put that
/// does not fail (05 §2.1), while the signature needs the body's hash before
/// the body: content that can seek is hashed and rewound, and content that
/// cannot is spooled while it is hashed.
/// </para>
/// <para>
/// A refusal that may not last — a throttle, a server error, a connection
/// that died — is retried a few times, from the content already read; one
/// that will last, like refused credentials, is a fault at once. Faults are
/// <see cref="IOException"/>s, which is what every caller of a store already
/// survives, and a store nobody answers is an
/// <see cref="S3StoreUnreachableException"/>, so a caller can tell an outage
/// from a refusal.
/// </para>
/// </remarks>
public sealed class S3ObjectStore : IPrefixedObjectStore
{
    /// <summary>What one PUT may carry: five GiB, the API's single-request ceiling.</summary>
    public const long MaximumSinglePut = 5L * 1024 * 1024 * 1024;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    })
    {
        // Bounded by the caller's token rather than a clock: a 128 MiB blob
        // over a slow uplink legitimately takes longer than any one default.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly S3Location _location;
    private readonly S3RequestSigner _signer;
    private readonly ILogger _logger;
    private readonly S3StoreOptions _options;
    private readonly string _root;

    /// <summary>Opens a store over one bucket and prefix.</summary>
    /// <param name="location">Where the objects live.</param>
    /// <param name="credentials">The access key requests are signed with.</param>
    /// <param name="logger">Where diagnostics go; none when null.</param>
    /// <param name="options">How hard to try; <see cref="S3StoreOptions.Default"/> when null.</param>
    public S3ObjectStore(
        S3Location location, S3Credentials credentials, ILogger? logger = null, S3StoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(credentials);
        _location = location;
        _signer = new S3RequestSigner(credentials, location.Region);
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? S3StoreOptions.Default;
        _root = location.Prefix is null ? string.Empty : location.Prefix + "/";
    }

    /// <summary>
    /// What the API promises, which is what this store declares: conditional
    /// create and ranged reads, required of every provider (05 §1); listings
    /// strongly consistent with what was written, as the API has promised of
    /// every operation since 2020; and five GiB a request. Multipart upload
    /// is not used, so a blob is one request.
    /// </summary>
    public static StoreCapabilities DeclaredCapabilities { get; } = new()
    {
        ConditionalCreate = true,
        RangedReads = true,
        ListingConsistency = ListingConsistency.Strong,
        MaximumObjectSize = MaximumSinglePut,
    };

    /// <inheritdoc />
    public StoreCapabilities Capabilities => DeclaredCapabilities;

    /// <summary>Where this store's objects live.</summary>
    public S3Location Location => _location;

    /// <inheritdoc />
    public async ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            "head", key, () => new HttpRequestMessage(HttpMethod.Head, _location.ObjectUri(FullKey(key))),
            S3RequestSigner.EmptyPayloadSha256, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return response.StatusCode switch
        {
            HttpStatusCode.OK => new GetMetadataResult(new ObjectMetadata(
                response.Content.Headers.ContentLength ?? 0, response.Content.Headers.LastModified)),
            HttpStatusCode.NotFound => GetMetadataResult.NotFound,
            _ => throw await RefusedAsync(response, "head", key, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <inheritdoc />
    public async ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        Log.ObjectRead(_logger, key, new LogLabel(range is { } shown ? $" [{shown.Offset}+{shown.Length}]" : string.Empty));
        var response = await SendAsync(
            "get", key,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, _location.ObjectUri(FullKey(key)));
                if (range is { } wanted)
                {
                    request.Headers.Range = new RangeHeaderValue(wanted.Offset, wanted.Offset + wanted.Length - 1);
                }

                return request;
            },
            S3RequestSigner.EmptyPayloadSha256, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK when range is { } wanted:
                    // A store that ignored the range answered the whole object:
                    // serve the slice of it that was asked for, or say it is not
                    // there, rather than hand back more than the range.
                    var whole = response.Content.Headers.ContentLength;
                    if (whole is { } total && wanted.Offset + wanted.Length > total)
                    {
                        response.Dispose();
                        return OpenReadResult.RangeNotSatisfiable;
                    }

                    return new OpenReadResult(new ResponseStream(
                        response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                        wanted.Offset, wanted.Length));

                case HttpStatusCode.OK:
                    return new OpenReadResult(new ResponseStream(
                        response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                        skip: 0, length: null));

                case HttpStatusCode.PartialContent when range is { } wanted:
                    // The API answers a range that starts inside the object and
                    // runs past its end with what there is. The contract calls
                    // that range unsatisfiable, so the served range is checked
                    // against the asked one rather than trusted to match.
                    var served = response.Content.Headers.ContentRange;
                    if (served?.From != wanted.Offset || served.To != wanted.Offset + wanted.Length - 1)
                    {
                        response.Dispose();
                        return OpenReadResult.RangeNotSatisfiable;
                    }

                    return new OpenReadResult(new ResponseStream(
                        response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                        skip: 0, length: wanted.Length));

                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    response.Dispose();
                    return OpenReadResult.RangeNotSatisfiable;

                case HttpStatusCode.NotFound:
                    var (code, message) = await ErrorOfAsync(response, cancellationToken).ConfigureAwait(false);
                    if (code == "NoSuchBucket")
                    {
                        throw Refused(response.StatusCode, "get", key, code, message);
                    }

                    response.Dispose();
                    return OpenReadResult.NotFound;

                default:
                    throw await RefusedAsync(response, "get", key, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openContent);

        var source = await openContent(cancellationToken).ConfigureAwait(false);
        FileStream? spool = null;
        try
        {
            Stream body;
            long start;
            string hash;
            if (source.CanSeek)
            {
                start = source.Position;
                hash = await HashAsync(source, copyTo: null, cancellationToken).ConfigureAwait(false);
                body = source;
            }
            else
            {
                spool = new FileStream(
                    Path.Combine(_options.SpoolDirectory ?? Path.GetTempPath(), $"fallbackplan-s3-{Guid.NewGuid():n}.spool"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                    FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                hash = await HashAsync(source, spool, cancellationToken).ConfigureAwait(false);
                start = 0;
                body = spool;
            }

            var length = body.Length - start;
            if (length > MaximumSinglePut)
            {
                throw new IOException(Strings.FormatS3ObjectStore_ObjectTooLarge(key.Value, length, MaximumSinglePut));
            }

            // Every put asks for create-only, whatever its conditions: the API
            // would otherwise overwrite a live key, and nothing this product
            // stores may ever be overwritten (ADR-0012 Amendment 1).
            using var response = await SendAsync(
                "put", key,
                () =>
                {
                    body.Position = start;
                    var request = new HttpRequestMessage(HttpMethod.Put, _location.ObjectUri(FullKey(key)))
                    {
                        Content = new StreamContent(new KeptOpenStream(body)),
                    };
                    request.Content.Headers.ContentLength = length;
                    request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                    return request;
                },
                hash, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    Log.ObjectPut(_logger, key, length);
                    return new PutResult(PutOutcome.Created);

                case HttpStatusCode.PreconditionFailed:
                    return new PutResult(PutOutcome.AlreadyExists);

                default:
                    throw await RefusedAsync(response, "put", key, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
            if (spool is not null)
            {
                await spool.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix, ListOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var pageSize = Math.Clamp(options.PageSizeHint ?? 1000, 1, 1000);
        string? continuation = null;
        var startAfter = options.ResumeAfter is { Length: > 0 } resume ? _root + resume : null;

        do
        {
            var page = await ListPageAsync(
                _root + prefix.Value, delimiter: null, startAfter, continuation, pageSize, cancellationToken)
                .ConfigureAwait(false);
            foreach (var (full, size) in page.Contents)
            {
                // Something written under this prefix by another hand, in a
                // spelling no object of this product's can have, is not one
                // of its objects: left alone, never mistaken for one.
                if (ObjectKey.TryParse(full[_root.Length..], out var key))
                {
                    yield return new ObjectEntry(key, size, key.Value);
                }
            }

            continuation = page.NextContinuationToken;
            startAfter = null;
        }
        while (continuation is not null);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListChildrenAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuation = null;
        do
        {
            var page = await ListPageAsync(_root, "/", startAfter: null, continuation, 1000, cancellationToken)
                .ConfigureAwait(false);
            foreach (var common in page.CommonPrefixes)
            {
                yield return common[_root.Length..].TrimEnd('/');
            }

            continuation = page.NextContinuationToken;
        }
        while (continuation is not null);
    }

    /// <inheritdoc />
    public async ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
    {
        // The API deletes nothing successfully, so whether there was anything
        // to delete is asked first: the contract reports the two apart.
        if (!(await GetMetadataAsync(key, cancellationToken).ConfigureAwait(false)).Found)
        {
            return new DeleteResult(DeleteOutcome.NotFound);
        }

        using var response = await SendAsync(
            "delete", key, () => new HttpRequestMessage(HttpMethod.Delete, _location.ObjectUri(FullKey(key))),
            S3RequestSigner.EmptyPayloadSha256, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NoContent or HttpStatusCode.OK:
                Log.ObjectDeleted(_logger, key);
                return new DeleteResult(DeleteOutcome.Deleted);

            case HttpStatusCode.NotFound:
                return new DeleteResult(DeleteOutcome.NotFound);

            default:
                throw await RefusedAsync(response, "delete", key, cancellationToken).ConfigureAwait(false);
        }
    }

    private string FullKey(ObjectKey key) => _root + key.Value;

    private async ValueTask<ListPage> ListPageAsync(
        string prefix, string? delimiter, string? startAfter, string? continuation, int pageSize,
        CancellationToken cancellationToken)
    {
        var query = new List<string> { "list-type=2", $"max-keys={pageSize.ToString(CultureInfo.InvariantCulture)}" };
        if (prefix.Length > 0)
        {
            query.Add("prefix=" + S3Location.Encode(prefix));
        }

        if (delimiter is not null)
        {
            query.Add("delimiter=" + S3Location.Encode(delimiter));
        }

        if (continuation is not null)
        {
            query.Add("continuation-token=" + S3Location.Encode(continuation));
        }
        else if (startAfter is not null)
        {
            query.Add("start-after=" + S3Location.Encode(startAfter));
        }

        var uri = new Uri($"{_location.BucketUri.AbsoluteUri}?{string.Join('&', query)}");
        using var response = await SendAsync(
            "list", key: null, () => new HttpRequestMessage(HttpMethod.Get, uri),
            S3RequestSigner.EmptyPayloadSha256, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await RefusedAsync(response, "list", key: null, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                Async = true,
            });
            document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (XmlException exception)
        {
            throw new IOException(Strings.FormatS3ObjectStore_ListingUnreadable(exception.Message), exception);
        }

        // By local name: an S3-compatible store may or may not qualify its
        // answer with the API's namespace, and the names are what matter.
        var result = document.Root;
        if (result is null || result.Name.LocalName != "ListBucketResult")
        {
            throw new IOException(Strings.FormatS3ObjectStore_ListingUnreadable(result?.Name.LocalName ?? "(empty)"));
        }

        List<(string Key, long Size)> contents = [];
        foreach (var entry in Children(result, "Contents"))
        {
            var key = Child(entry, "Key")?.Value;
            if (key is null || !long.TryParse(Child(entry, "Size")?.Value, CultureInfo.InvariantCulture, out var size))
            {
                throw new IOException(Strings.FormatS3ObjectStore_ListingUnreadable("Contents"));
            }

            contents.Add((key, size));
        }

        List<string> commonPrefixes = [.. Children(result, "CommonPrefixes")
            .Select(common => Child(common, "Prefix")?.Value)
            .OfType<string>()];
        var truncated = string.Equals(Child(result, "IsTruncated")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        var next = Child(result, "NextContinuationToken")?.Value;
        if (truncated && string.IsNullOrEmpty(next))
        {
            // A page that says there is more and gives no way to it would
            // either loop or stop short; a listing that stops short silently
            // is how an object goes missing from a convergence.
            throw new IOException(Strings.FormatS3ObjectStore_ListingUnreadable("NextContinuationToken"));
        }

        return new ListPage(contents, commonPrefixes, truncated ? next : null);
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(element => element.Name.LocalName == localName);

    private static XElement? Child(XElement parent, string localName) => Children(parent, localName).FirstOrDefault();

    /// <summary>
    /// Sends one request, building and signing it afresh for each attempt,
    /// until it is answered with something other than a transient refusal or
    /// the attempts run out.
    /// </summary>
    private async ValueTask<HttpResponseMessage> SendAsync(
        string operation,
        ObjectKey? key,
        Func<HttpRequestMessage> build,
        string payloadSha256,
        HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            EngineDiagnostics.StoreRequests.Add(1, new KeyValuePair<string, object?>("operation", operation));
            HttpResponseMessage response;
            using (var request = build())
            {
                _signer.Sign(request, payloadSha256, DateTimeOffset.UtcNow);
                try
                {
                    response = await Client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    if (attempt >= _options.MaxAttempts)
                    {
                        throw new S3StoreUnreachableException(
                            Strings.FormatS3ObjectStore_Unreachable(operation, attempt, exception.Message), exception);
                    }

                    Log.Retrying(_logger, new LogLabel(operation), new LogLabel("no answer"), attempt, _options.MaxAttempts);
                    await Task.Delay(DelayBefore(attempt + 1), cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            if (IsTransient(response.StatusCode) && attempt < _options.MaxAttempts)
            {
                Log.Retrying(
                    _logger, new LogLabel(operation),
                    new LogLabel(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)),
                    attempt, _options.MaxAttempts);
                response.Dispose();
                await Task.Delay(DelayBefore(attempt + 1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode && key is { } failed && response.StatusCode != HttpStatusCode.NotFound
                && response.StatusCode != HttpStatusCode.PreconditionFailed)
            {
                Log.OperationFailed(
                    _logger, new LogLabel(operation), failed,
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            return response;
        }
    }

    private TimeSpan DelayBefore(int attempt) =>
        _options.RetryDelay * Math.Pow(2, attempt - 2) * (0.75 + (Random.Shared.NextDouble() / 2));

    /// <summary>
    /// A refusal the next attempt may not repeat: a throttle, a server that
    /// erred or timed out. A refusal of the request itself — its credentials,
    /// its signature, its bucket — will be refused again, so it is not.
    /// </summary>
    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private static async ValueTask<IOException> RefusedAsync(
        HttpResponseMessage response, string operation, ObjectKey? key, CancellationToken cancellationToken)
    {
        var (code, message) = await ErrorOfAsync(response, cancellationToken).ConfigureAwait(false);
        return Refused(response.StatusCode, operation, key, code, message);
    }

    /// <summary>
    /// The fault a refusal is, told apart where what a person can do differs
    /// (FR-QUOTA-001; ADR-0012 Amendment 5): a store with no room, a quota
    /// crossed, a store that stayed busy through every attempt, or a refusal
    /// of the request itself.
    /// </summary>
    private static IOException Refused(HttpStatusCode status, string operation, ObjectKey? key, string? code, string? message)
    {
        var subject = key?.Value ?? "(the bucket)";
        var answered = ((int)status).ToString(CultureInfo.InvariantCulture);
        var word = code ?? status.ToString();
        var said = message ?? string.Empty;

        // 507, Insufficient Storage, whatever word the store has for it:
        // XMinioStorageFull at one S3-compatible store, InsufficientCapacity
        // at another.
        if (status == HttpStatusCode.InsufficientStorage)
        {
            return new StoreFullException(Strings.FormatS3ObjectStore_Full(operation, subject, answered, word, said));
        }

        // The API itself has no quota; the S3-compatible stores that hold one
        // each name it, with a status of their own.
        if (code is "QuotaExceeded" or "XMinioAdminBucketQuotaExceeded")
        {
            return new StoreQuotaExceededException(
                Strings.FormatS3ObjectStore_QuotaExceeded(operation, subject, answered, word, said));
        }

        // A transient answer reaches here only once the attempts have run out.
        if (IsTransient(status))
        {
            return new StoreBusyException(Strings.FormatS3ObjectStore_Busy(operation, subject, answered, word, said));
        }

        return new IOException(Strings.FormatS3ObjectStore_Refused(operation, subject, answered, word, said));
    }

    /// <summary>The error code and message an error answer carries, read as the API shapes them.</summary>
    private static async ValueTask<(string? Code, string? Message)> ErrorOfAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length == 0)
            {
                return (null, null);
            }

            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            var error = XDocument.Load(reader).Root;
            return error is null ? (null, null) : (Child(error, "Code")?.Value, Child(error, "Message")?.Value);
        }
        catch (Exception exception) when (exception is XmlException or HttpRequestException or IOException)
        {
            // An error answer that is not the API's shape still says its
            // status, and that is enough to report the refusal.
            return (null, null);
        }
    }

    private static async ValueTask<string> HashAsync(Stream source, Stream? copyTo, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            if (copyTo is not null)
            {
                await copyTo.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        if (copyTo is not null)
        {
            await copyTo.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed record ListPage(
        IReadOnlyList<(string Key, long Size)> Contents, IReadOnlyList<string> CommonPrefixes, string? NextContinuationToken);

    /// <summary>
    /// The put's content as the HTTP request reads it: everything delegated,
    /// except that sending it does not close it — the next attempt sends it
    /// again, and the put disposes it once, at the end.
    /// </summary>
    private sealed class KeptOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// An object's body as the caller reads it: the response's stream, or the
    /// slice of it a store that ignored a range was asked for, owning the
    /// response so that disposing the stream releases the connection.
    /// </summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner, long skip, long? length) : Stream
    {
        private long _skip = skip;
        private long? _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_skip > 0)
            {
                var discard = new byte[(int)Math.Min(_skip, 81920)];
                var skipped = await inner.ReadAsync(discard, cancellationToken).ConfigureAwait(false);
                if (skipped == 0)
                {
                    throw new IOException(Strings.S3ObjectStore_BodyEndedEarly);
                }

                _skip -= skipped;
            }

            if (_remaining is 0)
            {
                return 0;
            }

            var wanted = _remaining is { } left ? (int)Math.Min(buffer.Length, left) : buffer.Length;
            var read = await inner.ReadAsync(buffer[..wanted], cancellationToken).ConfigureAwait(false);
            if (read == 0 && _remaining is > 0)
            {
                // The range was answered whole and then the body stopped: a
                // short object would be read as damage, and this is not.
                throw new IOException(Strings.S3ObjectStore_BodyEndedEarly);
            }

            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

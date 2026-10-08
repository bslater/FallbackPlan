using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.AzureBlob.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Storage.AzureBlob;

/// <summary>
/// An <see cref="IObjectStore"/> over the Azure Blob API (FR-REP-002,
/// ADR-0093): one container of a storage account, under a prefix, spoken to
/// over the platform's HTTP client with requests this assembly authorises.
/// </summary>
/// <remarks>
/// <para>
/// The API differs from the contract where the S3 API does, and in places
/// where it does not. A put to a live name overwrites unless it asks not to,
/// so every put is sent <c>If-None-Match: *</c> whatever its conditions, and
/// the conflict the API answers — <c>BlobAlreadyExists</c> — is
/// <see cref="PutOutcome.AlreadyExists"/>. A range that runs past the end is
/// answered short, so the range served is read back from
/// <c>Content-Range</c>. A delete of nothing is answered as one, so it is a
/// single request. A container that does not exist is a fault, never an
/// empty store. And every put carries its body's <c>Content-MD5</c>, which
/// the API checks before it stores anything, so the content is hashed before
/// it is sent: content that can seek is hashed and rewound, and content that
/// cannot is spooled while it is hashed, so the content factory is still
/// read once (05 §2.1).
/// </para>
/// <para>
/// A refusal that may not last — a busy server, a timeout, a connection that
/// died — is retried a few times, from the content already read; one that
/// will last, like a refused credential, is a fault at once. Faults are
/// <see cref="IOException"/>s, and a store nobody answers is an
/// <see cref="AzureBlobStoreUnreachableException"/>, so a caller can tell an
/// outage from a refusal. A shared access signature past the expiry it
/// states is refused here, before a request it would fail is sent.
/// </para>
/// </remarks>
public sealed class AzureBlobObjectStore : IPrefixedObjectStore
{
    /// <summary>What one Put Blob may carry: 5000 MiB, the API's single-request ceiling.</summary>
    public const long MaximumSinglePut = 5000L * 1024 * 1024;

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

    private readonly AzureBlobLocation _location;
    private readonly AzureBlobCredentials _credentials;
    private readonly AzureBlobRequestSigner _signer;
    private readonly ILogger _logger;
    private readonly AzureBlobStoreOptions _options;
    private readonly string _root;

    /// <summary>Opens a store over one container and prefix.</summary>
    /// <param name="location">Where the blobs live.</param>
    /// <param name="credentials">What authorises the requests.</param>
    /// <param name="logger">Where diagnostics go; none when null.</param>
    /// <param name="options">How hard to try; <see cref="AzureBlobStoreOptions.Default"/> when null.</param>
    public AzureBlobObjectStore(
        AzureBlobLocation location, AzureBlobCredentials credentials, ILogger? logger = null, AzureBlobStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(credentials);
        _location = location;
        _credentials = credentials;
        _signer = new AzureBlobRequestSigner(location.Account, credentials);
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? AzureBlobStoreOptions.Default;
        _root = location.Prefix is null ? string.Empty : location.Prefix + "/";
    }

    /// <summary>
    /// What the API promises, which is what this store declares: conditional
    /// create and ranged reads, required of every provider (05 §1); listings
    /// strongly consistent with what was written, as the API promises; and
    /// 5000 MiB a request. Staged blocks are not used, so a blob is one
    /// request.
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

    /// <summary>Where this store's blobs live.</summary>
    public AzureBlobLocation Location => _location;

    /// <inheritdoc />
    public async ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            "head", key, () => new HttpRequestMessage(HttpMethod.Head, _location.BlobUri(FullKey(key))),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return response.StatusCode switch
        {
            HttpStatusCode.OK => new GetMetadataResult(new ObjectMetadata(
                response.Content.Headers.ContentLength ?? 0, response.Content.Headers.LastModified)),
            HttpStatusCode.NotFound when ErrorCodeHeader(response) != "ContainerNotFound" => GetMetadataResult.NotFound,
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
                var request = new HttpRequestMessage(HttpMethod.Get, _location.BlobUri(FullKey(key)));
                if (range is { } wanted)
                {
                    request.Headers.TryAddWithoutValidation(
                        "x-ms-range",
                        string.Create(CultureInfo.InvariantCulture, $"bytes={wanted.Offset}-{wanted.Offset + wanted.Length - 1}"));
                }

                return request;
            },
            HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK when range is { } wanted:
                    // A store that ignored the range answered the whole blob:
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
                    // The API answers a range that starts inside the blob and
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

                case HttpStatusCode.NotFound when ErrorCodeHeader(response) != "ContainerNotFound":
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
            byte[] digest;
            if (source.CanSeek)
            {
                start = source.Position;
                digest = await DigestAsync(source, copyTo: null, cancellationToken).ConfigureAwait(false);
                body = source;
            }
            else
            {
                spool = new FileStream(
                    Path.Combine(_options.SpoolDirectory ?? Path.GetTempPath(), $"fallbackplan-azure-{Guid.NewGuid():n}.spool"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                    FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                digest = await DigestAsync(source, spool, cancellationToken).ConfigureAwait(false);
                start = 0;
                body = spool;
            }

            var length = body.Length - start;
            if (length > MaximumSinglePut)
            {
                throw new IOException(Strings.FormatAzureBlobObjectStore_ObjectTooLarge(key.Value, length, MaximumSinglePut));
            }

            // Every put asks for create-only, whatever its conditions: the API
            // would otherwise overwrite a live name, and nothing this product
            // stores may ever be overwritten (ADR-0012 Amendment 1).
            using var response = await SendAsync(
                "put", key,
                () =>
                {
                    body.Position = start;
                    var request = new HttpRequestMessage(HttpMethod.Put, _location.BlobUri(FullKey(key)))
                    {
                        Content = new StreamContent(new KeptOpenStream(body)),
                    };
                    request.Content.Headers.ContentLength = length;
                    request.Content.Headers.ContentMD5 = digest;
                    request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                    request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                    return request;
                },
                HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Created or HttpStatusCode.OK:
                    Log.ObjectPut(_logger, key, length);
                    return new PutResult(PutOutcome.Created);

                case HttpStatusCode.PreconditionFailed:
                case HttpStatusCode.Conflict when ErrorCodeHeader(response) == "BlobAlreadyExists":
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
    /// <remarks>
    /// The API resumes a listing only from a marker it issued, so a resume
    /// from a key reads the pages before it and passes over what they hold.
    /// </remarks>
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix, ListOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var pageSize = Math.Clamp(options.PageSizeHint ?? 1000, 1, 5000);
        string? marker = null;

        do
        {
            var page = await ListPageAsync(_root + prefix.Value, delimiter: null, marker, pageSize, cancellationToken)
                .ConfigureAwait(false);
            foreach (var (full, size) in page.Blobs)
            {
                // Something written under this prefix by another hand, in a
                // spelling no object of this product's can have, is not one
                // of its objects: left alone, never mistaken for one.
                if (ObjectKey.TryParse(full[_root.Length..], out var key)
                    && (options.ResumeAfter is not { } after || string.CompareOrdinal(key.Value, after) > 0))
                {
                    yield return new ObjectEntry(key, size, key.Value);
                }
            }

            marker = page.NextMarker;
        }
        while (marker is not null);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListChildrenAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? marker = null;
        do
        {
            var page = await ListPageAsync(_root, "/", marker, 5000, cancellationToken).ConfigureAwait(false);
            foreach (var common in page.Prefixes)
            {
                yield return common[_root.Length..].TrimEnd('/');
            }

            marker = page.NextMarker;
        }
        while (marker is not null);
    }

    /// <inheritdoc />
    public async ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            "delete", key, () => new HttpRequestMessage(HttpMethod.Delete, _location.BlobUri(FullKey(key))),
            HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        switch (response.StatusCode)
        {
            case HttpStatusCode.Accepted or HttpStatusCode.OK or HttpStatusCode.NoContent:
                Log.ObjectDeleted(_logger, key);
                return new DeleteResult(DeleteOutcome.Deleted);

            case HttpStatusCode.NotFound when ErrorCodeHeader(response) != "ContainerNotFound":
                return new DeleteResult(DeleteOutcome.NotFound);

            default:
                throw await RefusedAsync(response, "delete", key, cancellationToken).ConfigureAwait(false);
        }
    }

    private string FullKey(ObjectKey key) => _root + key.Value;

    private async ValueTask<ListPage> ListPageAsync(
        string prefix, string? delimiter, string? marker, int pageSize, CancellationToken cancellationToken)
    {
        var query = new List<string>
        {
            "restype=container", "comp=list", $"maxresults={pageSize.ToString(CultureInfo.InvariantCulture)}",
        };
        if (prefix.Length > 0)
        {
            query.Add("prefix=" + AzureBlobLocation.Encode(prefix));
        }

        if (delimiter is not null)
        {
            query.Add("delimiter=" + AzureBlobLocation.Encode(delimiter));
        }

        if (marker is not null)
        {
            query.Add("marker=" + AzureBlobLocation.Encode(marker));
        }

        var uri = new Uri($"{_location.ContainerUri.AbsoluteUri}?{string.Join('&', query)}");
        using var response = await SendAsync(
            "list", key: null, () => new HttpRequestMessage(HttpMethod.Get, uri),
            HttpCompletionOption.ResponseContentRead, cancellationToken)
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
            throw new IOException(Strings.FormatAzureBlobObjectStore_ListingUnreadable(exception.Message), exception);
        }

        var result = document.Root;
        if (result is null || result.Name.LocalName != "EnumerationResults")
        {
            throw new IOException(Strings.FormatAzureBlobObjectStore_ListingUnreadable(result?.Name.LocalName ?? "(empty)"));
        }

        List<(string Name, long Size)> blobs = [];
        List<string> prefixes = [];
        foreach (var entry in Child(result, "Blobs")?.Elements() ?? [])
        {
            var name = Child(entry, "Name")?.Value;
            switch (entry.Name.LocalName)
            {
                case "Blob" when name is not null
                    && Child(entry, "Properties") is { } properties
                    && long.TryParse(Child(properties, "Content-Length")?.Value, CultureInfo.InvariantCulture, out var size):
                    blobs.Add((name, size));
                    break;

                case "BlobPrefix" when name is not null:
                    prefixes.Add(name);
                    break;

                default:
                    throw new IOException(Strings.FormatAzureBlobObjectStore_ListingUnreadable(entry.Name.LocalName));
            }
        }

        var next = Child(result, "NextMarker")?.Value;
        return new ListPage(blobs, prefixes, string.IsNullOrEmpty(next) ? null : next);
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(element => element.Name.LocalName == localName);

    private static XElement? Child(XElement parent, string localName) => Children(parent, localName).FirstOrDefault();

    /// <summary>
    /// Sends one request, building and authorising it afresh for each
    /// attempt, until it is answered with something other than a transient
    /// refusal or the attempts run out.
    /// </summary>
    private async ValueTask<HttpResponseMessage> SendAsync(
        string operation,
        ObjectKey? key,
        Func<HttpRequestMessage> build,
        HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            // A signature past the expiry it states would be refused by the
            // store, every time: a request it is certain to fail is not one
            // worth paying for.
            if (_credentials.Expires is { } expires && expires <= DateTimeOffset.UtcNow)
            {
                throw new IOException(Strings.FormatAzureBlobObjectStore_SignatureExpired(
                    expires.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            }

            EngineDiagnostics.StoreRequests.Add(1, new KeyValuePair<string, object?>("operation", operation));
            HttpResponseMessage response;
            using (var request = build())
            {
                _signer.Sign(request, DateTimeOffset.UtcNow);
                try
                {
                    response = await Client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    if (attempt >= _options.MaxAttempts)
                    {
                        throw new AzureBlobStoreUnreachableException(
                            Strings.FormatAzureBlobObjectStore_Unreachable(operation, attempt, exception.Message), exception);
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

            if (!response.IsSuccessStatusCode && key is { } failed && response.StatusCode is not
                (HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict))
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
    /// A refusal the next attempt may not repeat: a busy or erring server, a
    /// timeout. A refusal of the request itself — its credential, its
    /// signature's permissions, its container — will be refused again, so it
    /// is not.
    /// </summary>
    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    /// <summary>The API's error code as its answer's header carries it — the one place a HEAD can.</summary>
    private static string? ErrorCodeHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ms-error-code", out var values) ? values.FirstOrDefault() : null;

    private static async ValueTask<IOException> RefusedAsync(
        HttpResponseMessage response, string operation, ObjectKey? key, CancellationToken cancellationToken)
    {
        var (code, message) = await ErrorOfAsync(response, cancellationToken).ConfigureAwait(false);
        return new IOException(Strings.FormatAzureBlobObjectStore_Refused(
            operation,
            key?.Value ?? "(the container)",
            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
            code ?? response.StatusCode.ToString(),
            message ?? string.Empty));
    }

    /// <summary>
    /// The error code and message an error answer carries: the code from its
    /// header, the message from its body when it has one, first line only —
    /// the rest is the store's request id and clock.
    /// </summary>
    private static async ValueTask<(string? Code, string? Message)> ErrorOfAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var code = ErrorCodeHeader(response);
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length == 0)
            {
                return (code, null);
            }

            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            var error = XDocument.Load(reader).Root;
            var message = error is null ? null : Child(error, "Message")?.Value.Split('\n')[0].Trim();
            return (code ?? (error is null ? null : Child(error, "Code")?.Value), message);
        }
        catch (Exception exception) when (exception is XmlException or HttpRequestException or IOException)
        {
            // An error answer that is not the API's shape still says its
            // status and its code header, and that is enough to report it.
            return (code, null);
        }
    }

    private static async ValueTask<byte[]> DigestAsync(Stream source, Stream? copyTo, CancellationToken cancellationToken)
    {
        // The API's own check that a body arrived as it was sent, which it
        // names MD5. The blobs are sealed already: nothing relies on it to
        // keep them secret or to prove where they came from.
#pragma warning disable CA5351
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
#pragma warning restore CA5351
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

        return hash.GetHashAndReset();
    }

    private sealed record ListPage(
        IReadOnlyList<(string Name, long Size)> Blobs, IReadOnlyList<string> Prefixes, string? NextMarker);

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
    /// A blob's body as the caller reads it: the response's stream, or the
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
                    throw new IOException(Strings.AzureBlobObjectStore_BodyEndedEarly);
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
                // short blob would be read as damage, and this is not.
                throw new IOException(Strings.AzureBlobObjectStore_BodyEndedEarly);
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

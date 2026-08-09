using Bodu;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using FallbackPlan.Api.Resources;

namespace FallbackPlan.Api.Transport;

/// <summary>Raised when a client and service cannot speak to one another.</summary>
public sealed class ServiceConnectionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ServiceConnectionException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public ServiceConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ServiceConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A client of a service on the local binding.
/// </summary>
/// <remarks>
/// Connecting is how a front end discovers whether a service is running at all
/// — there is no separate liveness protocol, because a socket that accepts is
/// the only proof that matters. The CLI uses that to decide between service
/// mode and direct mode (ADR-0028 §3).
/// </remarks>
public sealed class LocalServiceClient : IFallbackPlanClient
{
    private readonly Stream _stream;
    private readonly string _address;
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private long _nextRequestId;

    private LocalServiceClient(Stream stream, string address, ContractVersion serviceVersion)
    {
        _stream = stream;
        _address = address;
        ServiceContractVersion = serviceVersion;
    }

    /// <inheritdoc/>
    public ContractVersion ServiceContractVersion { get; }

    /// <summary>The address this client is connected to.</summary>
    public string Address => _address;

    /// <summary>
    /// Connects to the service for a state directory, or reports why not.
    /// </summary>
    /// <param name="stateDirectory">The state directory whose service is wanted.</param>
    /// <param name="clientName">What to call this client in the service's log.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>The connected client.</returns>
    /// <exception cref="ServiceConnectionException">No service answered, or it refused.</exception>
    public static async ValueTask<LocalServiceClient> ConnectAsync(
        string stateDirectory, string clientName, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        ThrowHelper.ThrowIfNullOrWhiteSpace(clientName);

        var address = LocalEndpoint.AddressFor(stateDirectory);
        var stream = await OpenAsync(address, cancellationToken).ConfigureAwait(false);

        try
        {
            await FrameCodec.WriteAsync(
                stream,
                new HelloFrame(ContractVersion.Current.ToString(), clientName),
                cancellationToken).ConfigureAwait(false);

            if (await FrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
                is not HelloAcknowledgementFrame acknowledgement)
            {
                throw new ServiceConnectionException(Strings.FormatLocalServiceClient_ServiceDidNotAnswerContract(address));
            }

            if (!acknowledgement.Accepted)
            {
                throw new ServiceConnectionException(
                    acknowledgement.Message ?? "The service refused the connection without saying why.");
            }

            if (!ContractVersion.TryParse(acknowledgement.ContractVersion, out var serviceVersion))
            {
                throw new ServiceConnectionException(Strings.FormatLocalServiceClient_ServiceReportedContractVersionWhich(acknowledgement.ContractVersion));
            }

            return new LocalServiceClient(stream, address, serviceVersion);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(command);

        var id = Interlocked.Increment(ref _nextRequestId);
        await _exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_stream, new RequestFrame(id, command), cancellationToken).ConfigureAwait(false);

            var frame = await FrameCodec.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
            return frame switch
            {
                ResponseFrame response when response.Id == id => response.Result,
                ResponseFrame response => throw new ServiceConnectionException(Strings.FormatLocalServiceClient_ServiceAnsweredRequestWhileOutstanding(response.Id, id)),
                null => throw new ServiceConnectionException(Strings.LocalServiceClient_ServiceClosedConnectionWithoutAnswering),
                _ => throw new ServiceConnectionException(Strings.LocalServiceClient_ServiceSentFrameNotResponse),
            };
        }
        finally
        {
            _exchange.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The connection is opened and the watch registered <b>here</b>, at the
    /// call, rather than in the streaming body below. An
    /// <c>async IAsyncEnumerable</c> runs none of its body until something
    /// pulls it, so a caller holding this enumerable would not yet be connected
    /// and anything the service reported before the first pull would be sent to
    /// nobody.
    /// </para>
    /// <para>
    /// What this deliberately does <b>not</b> do is report a failure to connect
    /// at the call. The eager half of a split iterator cannot await, because the
    /// method must hand back an <see cref="IAsyncEnumerable{T}"/> synchronously
    /// — the shape <c>ApiShapeTests</c> pins — so a
    /// <see cref="ServiceConnectionException"/> still surfaces from the
    /// consumer's first <c>await foreach</c> rather than from this call.
    /// Answering it here would mean changing the contract's return type, which
    /// is a decision worth taking on its own rather than as a side effect.
    /// </para>
    /// <para>
    /// A caller that asks to watch and never enumerates leaves the connection
    /// open until finalisation. That is the accepted cost of connecting when
    /// asked; the only reason to call this is to enumerate it.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken)
    {
        var opening = OpenWatchAsync(cancellationToken);
        return StreamAsync(opening, cancellationToken);
    }

    /// <summary>
    /// Opens the watch connection and completes its handshake, or returns
    /// <see langword="null"/> when the service refused it.
    /// </summary>
    private async Task<Stream?> OpenWatchAsync(CancellationToken cancellationToken)
    {
        // A watch takes its own connection: a stream and a request/response
        // exchange have different lifetimes, and a client that watches must
        // still be able to issue commands.
        var stream = await OpenAsync(_address, cancellationToken).ConfigureAwait(false);

        try
        {
            await FrameCodec.WriteAsync(
                stream,
                new HelloFrame(ContractVersion.Current.ToString(), "watch"),
                cancellationToken).ConfigureAwait(false);

            if (await FrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
                is not HelloAcknowledgementFrame { Accepted: true })
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            await FrameCodec.WriteAsync(stream, new WatchFrame(), cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            // The stream is this method's until it is handed to the enumerator,
            // so a handshake that throws closes it here rather than leaving it
            // to a caller who never received it.
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async IAsyncEnumerable<JobProgressEvent> StreamAsync(
        Task<Stream?> opening,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = await opening.ConfigureAwait(false);
        if (stream is null)
        {
            yield break;
        }

        await using var owned = stream.ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            WireFrame? frame;
            try
            {
                frame = await FrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                yield break;
            }

            if (frame is not ProgressFrame progress)
            {
                yield break;
            }

            yield return progress.Event;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _exchange.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// How long to wait for a Windows named pipe to appear before reporting it
    /// absent.
    /// </summary>
    /// <remarks>
    /// Windows has no "nothing is listening" error for a pipe: connecting waits
    /// for one to be created, and with no timeout it waits for as long as the
    /// caller allows — so a client asking a machine with no service running
    /// would hang rather than be told, and the stated reason this method exists
    /// to give would never be reached. A bounded wait is what turns absence
    /// into an answer, because the timeout surfaces as
    /// <see cref="TimeoutException"/>. The Unix path needs none: connecting to
    /// a socket path that does not exist fails immediately.
    /// <para>
    /// Two seconds because a local pipe that exists is connectable at once, so
    /// this bounds only the answer "no", and a person waiting for it should not
    /// wait long.
    /// </para>
    /// </remarks>
    private const int WindowsConnectTimeoutMilliseconds = 2_000;

    private static async ValueTask<Stream> OpenAsync(string address, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(
                ".", address, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(WindowsConnectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw new ServiceConnectionException(Strings.FormatLocalServiceClient_NoServiceListening(address), exception);
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            socket.Dispose();
            throw new ServiceConnectionException(Strings.FormatLocalServiceClient_NoServiceListening(address), exception);
        }
    }
}

using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

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
                throw new ServiceConnectionException(
                    $"The service at '{address}' did not answer the contract handshake.");
            }

            if (!acknowledgement.Accepted)
            {
                throw new ServiceConnectionException(
                    acknowledgement.Message ?? "The service refused the connection without saying why.");
            }

            if (!ContractVersion.TryParse(acknowledgement.ContractVersion, out var serviceVersion))
            {
                throw new ServiceConnectionException(
                    $"The service reported contract version '{acknowledgement.ContractVersion}', which is not a version.");
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
        ArgumentNullException.ThrowIfNull(command);

        var id = Interlocked.Increment(ref _nextRequestId);
        await _exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_stream, new RequestFrame(id, command), cancellationToken).ConfigureAwait(false);

            var frame = await FrameCodec.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
            return frame switch
            {
                ResponseFrame response when response.Id == id => response.Result,
                ResponseFrame response => throw new ServiceConnectionException(
                    $"The service answered request {response.Id} while {id} was outstanding."),
                null => throw new ServiceConnectionException("The service closed the connection without answering."),
                _ => throw new ServiceConnectionException("The service sent a frame that is not a response."),
            };
        }
        finally
        {
            _exchange.Release();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<JobProgressEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A watch takes its own connection: a stream and a request/response
        // exchange have different lifetimes, and a client that watches must
        // still be able to issue commands.
        var stream = await OpenAsync(_address, cancellationToken).ConfigureAwait(false);
        await using var owned = stream.ConfigureAwait(false);

        await FrameCodec.WriteAsync(
            stream,
            new HelloFrame(ContractVersion.Current.ToString(), "watch"),
            cancellationToken).ConfigureAwait(false);

        if (await FrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            is not HelloAcknowledgementFrame { Accepted: true })
        {
            yield break;
        }

        await FrameCodec.WriteAsync(stream, new WatchFrame(), cancellationToken).ConfigureAwait(false);

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

    private static async ValueTask<Stream> OpenAsync(string address, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(
                ".", address, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw new ServiceConnectionException($"No service is listening on '{address}'.", exception);
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
            throw new ServiceConnectionException($"No service is listening on '{address}'.", exception);
        }
    }
}

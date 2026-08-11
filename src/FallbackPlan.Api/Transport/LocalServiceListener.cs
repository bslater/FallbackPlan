using Bodu;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace FallbackPlan.Api.Transport;

/// <summary>
/// Hosts a service on the local binding: a Unix domain socket on POSIX, a named
/// pipe on Windows (ADR-0028 §5).
/// </summary>
/// <remarks>
/// The listener knows nothing about backups. It accepts connections, frames
/// bytes, and hands commands to an <see cref="IFallbackPlanService"/> — which is
/// what lets the same service implementation serve a future remote binding
/// without being rewritten for it.
/// </remarks>
public sealed class LocalServiceListener : IAsyncDisposable
{
    private readonly IFallbackPlanService _service;
    private readonly string _address;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _connections = [];
    private readonly Lock _gate = new();
    private Socket? _socket;
    private Task? _acceptLoop;

    private LocalServiceListener(IFallbackPlanService service, string address, Action<string>? log)
    {
        _service = service;
        _address = address;
        _log = log;
    }

    /// <summary>The address this listener is reachable at.</summary>
    public string Address => _address;

    /// <summary>
    /// Starts listening for the given state directory.
    /// </summary>
    /// <param name="service">The service to dispatch to.</param>
    /// <param name="stateDirectory">The state directory whose service this is.</param>
    /// <param name="log">Optional sink for connection-level notes.</param>
    /// <returns>The running listener; dispose to stop.</returns>
    public static LocalServiceListener Start(IFallbackPlanService service, string stateDirectory, Action<string>? log = null)
    {
        ThrowHelper.ThrowIfNull(service);
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);

        LocalEndpoint.PrepareDirectory(stateDirectory);
        var address = LocalEndpoint.AddressFor(stateDirectory);
        var listener = new LocalServiceListener(service, address, log);

        if (OperatingSystem.IsWindows())
        {
            listener._acceptLoop = listener.AcceptPipesAsync();
        }
        else
        {
            listener.BindSocket(address);
            listener._acceptLoop = listener.AcceptSocketsAsync();
        }

        return listener;
    }

    /// <summary>Stops listening and waits for in-flight connections to finish.</summary>
    /// <returns>A task that completes when the listener is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _socket?.Dispose();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping is not a failure.
            }
        }

        Task[] outstanding;
        lock (_gate)
        {
            outstanding = [.. _connections];
        }

        try
        {
            await Task.WhenAll(outstanding).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // A client that vanished while we were closing is not an error.
        }

        if (!OperatingSystem.IsWindows())
        {
            TryDelete(_address);
        }

        _stopping.Dispose();
    }

    [UnsupportedOSPlatform("windows")]
    private void BindSocket(string address)
    {
        // A leftover socket file from a killed service would refuse the bind.
        // Deleting it is safe here and only here: the writer lock has already
        // established that no other service holds this state directory, so the
        // file cannot belong to a live peer.
        TryDelete(address);

        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Bind(new UnixDomainSocketEndPoint(address));
        _socket.Listen(16);

        File.SetUnixFileMode(address, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task AcceptSocketsAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await _socket!.AcceptAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            var peer = PeerCredentials.Read(accepted);
            Track(ServeAsync(new NetworkStream(accepted, ownsSocket: true), peer));
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task AcceptPipesAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _address,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }

            Track(ServeAsync(pipe, PeerCredentials.Read(pipe)));
        }
    }

    private void Track(Task connection)
    {
        lock (_gate)
        {
            _connections.RemoveAll(task => task.IsCompleted);
            _connections.Add(connection);
        }
    }

    private async Task ServeAsync(Stream stream, PeerIdentity peer)
    {
        await using var owned = stream.ConfigureAwait(false);

        // The local binding's authentication is the operating system's: the
        // socket's mode and the pipe's owner-only flag already decided who may
        // connect, and PeerCredentials is identification for the log, not a
        // gate. Everything past this point is the same command contract the
        // remote binding runs once its peer session has opened.
        await ServiceConnectionPump.RunAsync(stream, _service, peer.ToString(), _log, _stopping.Token)
            .ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}

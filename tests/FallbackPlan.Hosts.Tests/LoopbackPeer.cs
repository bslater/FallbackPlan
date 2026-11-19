using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A replication peer listening on loopback, with its own state directory: it
/// holds replicas for an installation once it admits it, and its replicas can
/// be read off its disk to see what it took.
/// </summary>
internal sealed class LoopbackPeer : IAsyncDisposable
{
    private readonly PeerKeypair _keypair;
    private readonly PeerGrantStore _grants;
    private RemoteServiceListener? _listener;

    private LoopbackPeer(string name, string state, PeerKeypair keypair, PeerGrantStore grants, RemoteServiceListener listener)
    {
        Name = name;
        State = state;
        _keypair = keypair;
        _grants = grants;
        _listener = listener;
        Endpoint = listener.Endpoint;
    }

    public string Name { get; }

    public string State { get; }

    public IPEndPoint Endpoint { get; }

    public Protocol.PeerIdentity Identity => _keypair.Identity;

    public string Fingerprint => _keypair.Identity.Fingerprint;

    /// <summary>Starts a peer listening, admitting no one yet.</summary>
    public static LoopbackPeer Start(string name, string state)
    {
        var keypair = PeerKeypairStore.Open(state);
        var grants = PeerGrantStore.Open(state);
        var listener = RemoteServiceListener.Start(
            keypair, grants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: state);
        listener.Bind(new UnusedService());
        return new LoopbackPeer(name, state, keypair, grants, listener);
    }

    /// <summary>Admits the installation whose state is at <paramref name="sourceState"/> to store here.</summary>
    public void Admit(string sourceState)
    {
        using var source = PeerKeypairStore.Open(sourceState);
        _grants.Pin(new PeerGrant(source.Identity, "source", PeerRole.StoresHere, PeerTerms.None, 1_722_600_000_000));
    }

    /// <summary>Each blob this peer holds for the installation, by its key from <c>blobs/</c> on.</summary>
    public List<string> Blobs() => [.. Replicas().SelectMany(replica => FilesUnder(replica, "blobs"))];

    /// <summary>Each snapshot object this peer holds for the installation.</summary>
    public List<string> Snapshots() => [.. Replicas().SelectMany(replica => FilesUnder(replica, "snapshots"))];

    /// <summary>Deletes the object at <paramref name="key"/> from every replica this peer holds, as a disk might lose it.</summary>
    public void Lose(string key)
    {
        foreach (var replica in Replicas())
        {
            var path = Path.Combine(replica, key.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public async Task StopAsync()
    {
        if (_listener is { } listener)
        {
            _listener = null;
            await listener.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _keypair.Dispose();
    }

    private string[] Replicas()
    {
        var replicas = Path.Combine(State, "replicas");
        return Directory.Exists(replicas) ? Directory.GetDirectories(replicas) : [];
    }

    private static IEnumerable<string> FilesUnder(string replica, string plane)
    {
        var root = Path.Combine(replica, plane);
        return Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(replica, path).Replace('\\', '/'))
            : [];
    }

    /// <summary>The Bind contract needs a service; a replication peer never reaches it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }
}

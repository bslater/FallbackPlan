using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Recovery;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// One household in a test of several: a live service over its own
/// <see cref="HostHarness"/>, a listener serving invites, commands and
/// replication on one socket as <c>AgentHost</c> wires it, and the local
/// command surface its operator uses. Pairing goes by spoken invite and
/// configuration over the contract, so nothing the sites share is anything
/// the product would not have them share.
/// </summary>
internal sealed class PeerSite : IAsyncDisposable
{
    private readonly PeerKeypair _keypair;
    private readonly PeerGrantStore _grants;
    private readonly ServiceCommandHandler _remote;
    private readonly CancellationToken _cancellationToken;
    private RemoteServiceListener? _listener;

    private PeerSite(HostHarness harness, ServiceRuntime runtime, CancellationToken cancellationToken)
    {
        Harness = harness;
        Runtime = runtime;
        _cancellationToken = cancellationToken;
        _keypair = PeerKeypairStore.Open(harness.StateDirectory);
        _grants = PeerGrantStore.Open(harness.StateDirectory);
        _listener = Listen(new IPEndPoint(IPAddress.Loopback, 0));
        Port = _listener.Endpoint.Port;

        var binding = RemoteBindingState.On(_listener.Endpoint.ToString());
        _remote = new ServiceCommandHandler(runtime, binding, CallerScope.Remote);
        Operator = new ServiceCommandHandler(runtime, binding, CallerScope.Local);
        _listener.Bind(_remote);
    }

    public HostHarness Harness { get; }

    public ServiceRuntime Runtime { get; }

    /// <summary>The command surface this site's own operator reaches over the local binding.</summary>
    public ServiceCommandHandler Operator { get; }

    /// <summary>The loopback port the listener holds, kept across an outage so peers find it again.</summary>
    public int Port { get; }

    public string Fingerprint => _keypair.Identity.Fingerprint;

    /// <summary>Sets the installation up, creates its "docs" archive and starts its service and listener.</summary>
    public static async Task<PeerSite> StartAsync(HostHarness harness, CancellationToken cancellationToken)
    {
        await harness.CreateRepositoryAsync();
        var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = harness.ArchivesRoot,
                StateDirectory = harness.StateDirectory,
            },
            cancellationToken);
        return new PeerSite(harness, runtime, cancellationToken);
    }

    /// <summary>
    /// Pairs this site to store at <paramref name="destination"/>: the
    /// destination's operator issues an invite, this site's enters it, and the
    /// pairing result becomes a peer destination named <paramref name="name"/>.
    /// </summary>
    public async Task PairToStoreAtAsync(PeerSite destination, string name)
    {
        Assert.IsInstanceOfType<PairingInviteResult>(
            await destination.Operator.ExecuteAsync(
                new CreatePairingInviteCommand($"guest of {name}", "stores-here", QuotaBytes: null, TimeToLiveMinutes: 60),
                _cancellationToken),
            out var invite);
        Assert.IsInstanceOfType<PairingCompletedResult>(
            await Operator.ExecuteAsync(
                new PairWithInviteCommand(invite.Code, "127.0.0.1", destination.Port, name), _cancellationToken),
            out var paired);
        Assert.AreEqual(destination.Fingerprint, paired.Fingerprint);

        Assert.IsInstanceOfType<AcknowledgedResult>(await Operator.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                Id: null, Name: name, Kind: "peer", Path: null,
                Fingerprint: paired.Fingerprint, Endpoint: $"127.0.0.1:{destination.Port}")),
            _cancellationToken));
    }

    /// <summary>
    /// Declares the "docs" set over this site's sources, copied to
    /// <paramref name="destinations"/>. Saving it queues its first capture
    /// (ADR-0047), and a completed capture fans out on its own.
    /// </summary>
    public async Task DeclareDocsAsync(params string[] destinations) =>
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await Operator.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                Harness.DocsSetId, "docs", Harness.SourceRoot, Schedule: null, [], [], destinations)),
            _cancellationToken));

    public Task WaitForFirstBackupAsync() => WaitForAsync(() => Runtime.Jobs.Jobs.Any(job =>
        job.BackupSetId == Harness.DocsSetId && job.State == JobState.Complete));

    /// <summary>
    /// Waits until <paramref name="destination"/> holds, and has proved it
    /// holds, a publication later than <paramref name="after"/>.
    /// </summary>
    public async Task<DestinationSyncRecord> WaitForProvenAsync(string destination, ulong after = 0)
    {
        // The verification stamps land with the in-sync write, so the wait
        // watches for the proven state, not merely the synced one.
        await WaitForAsync(() =>
            Runtime.DestinationSync.Find(Harness.DocsSetId, destination) is
            { State: DestinationSyncState.InSync, VerifiedAt: not null } row
            && row.SyncedSequence > after
            && row.VerifiedSequence == row.SyncedSequence);
        return Runtime.DestinationSync.Find(Harness.DocsSetId, destination)!;
    }

    /// <summary>Commands a backup of "docs" and waits for the job to complete.</summary>
    public async Task RunBackupAsync()
    {
        // Subscribed before the backup is commanded, so the completion cannot
        // pass unseen between the command and the watch.
        var progress = Runtime.Progress.WatchAsync(_cancellationToken);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (observation.Progress.State == JobState.Complete)
                    {
                        return;
                    }
                }
            },
            _cancellationToken);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await Operator.ExecuteAsync(new RunBackupCommand("docs", Full: false), _cancellationToken));
        await watching;
    }

    /// <summary>Takes the listener away, as a site that has gone offline.</summary>
    public async Task GoOfflineAsync()
    {
        Assert.IsNotNull(_listener, "the site is already offline");
        await _listener.DisposeAsync();
        _listener = null;
    }

    /// <summary>Brings the listener back on the port the site's peers paired with.</summary>
    public void ComeBackOnline()
    {
        Assert.IsNull(_listener, "the site is already online");
        _listener = Listen(new IPEndPoint(IPAddress.Loopback, Port));
        _listener.Bind(_remote);
    }

    /// <summary>The replica this site holds of <paramref name="owner"/>'s archive.</summary>
    /// <remarks>
    /// Which directory holds which owner is the listener's business (03 §8);
    /// the replica of an archive is the one holding its descriptor, byte for
    /// byte.
    /// </remarks>
    public string ReplicaOf(PeerSite owner)
    {
        var descriptor = File.ReadAllBytes(Path.Combine(owner.Harness.RepositoryPath, RepositoryLifecycle.DescriptorKey.Value));
        return Assert.ContainsSingle(Replicas().Where(replica =>
            File.Exists(Path.Combine(replica, RepositoryLifecycle.DescriptorKey.Value))
            && File.ReadAllBytes(Path.Combine(replica, RepositoryLifecycle.DescriptorKey.Value)).AsSpan().SequenceEqual(descriptor)));
    }

    /// <summary>Every replica this site holds for others.</summary>
    public IReadOnlyList<string> Replicas()
    {
        var root = Path.Combine(Harness.StateDirectory, "replicas");
        return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
    }

    /// <summary>
    /// The snapshots the recovery tool lists in <paramref name="replicaPath"/>
    /// under this site's passphrase, or the failed invocation's exit code.
    /// </summary>
    public async Task<(int ExitCode, IReadOnlyList<string> Snapshots, string Error)> ListSnapshotsInAsync(string replicaPath)
    {
        var listing = await HostHarness.RunAsync(
            RecoveryHost.RunAsync,
            "snapshots", "--repo", replicaPath, "--passphrase-env", Harness.PassphraseVariable);
        var snapshots = listing.ExitCode != 0
            ? []
            : listing.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
                .ToList();
        return (listing.ExitCode, snapshots, listing.Error);
    }

    /// <summary>Restores one snapshot of <paramref name="replicaPath"/> with the recovery tool and this site's passphrase alone.</summary>
    public async Task<string> RecoverFromAsync(string replicaPath, string snapshot, string name)
    {
        var output = Path.Combine(Harness.WorkPath, name);
        var restore = await HostHarness.RunAsync(
            RecoveryHost.RunAsync,
            "restore", "--repo", replicaPath, "--passphrase-env", Harness.PassphraseVariable,
            "--snapshot", snapshot, "--output", output);
        Assert.AreEqual(0, restore.ExitCode, restore.Error);
        return output;
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync();
        }

        await Runtime.DisposeAsync();
        _keypair.Dispose();
    }

    /// <summary>
    /// Every object of the store at <paramref name="root"/> but the staging-only
    /// planes (tombstones, leases), which never cross to a copy.
    /// </summary>
    public static async Task<Dictionary<string, byte[]>> ReadReplicableAsync(string root, CancellationToken cancellationToken)
    {
        var store = new LocalFileSystemObjectStore(root);
        Dictionary<string, byte[]> objects = [];
        await foreach (var entry in store.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken))
        {
            if (entry.Key.Value.StartsWith("tombstones/", StringComparison.Ordinal)
                || entry.Key.Value.StartsWith("leases/", StringComparison.Ordinal))
            {
                continue;
            }

            using var read = await store.OpenReadAsync(entry.Key, range: null, cancellationToken);
            using var buffer = new MemoryStream();
            await read.Content!.CopyToAsync(buffer, cancellationToken);
            objects[entry.Key.Value] = buffer.ToArray();
        }

        return objects;
    }

    /// <summary>The replica holds exactly the archive's replicable objects, byte for byte.</summary>
    public static async Task AssertReplicaMatchesAsync(string archivePath, string replicaPath, CancellationToken cancellationToken)
    {
        var archive = await ReadReplicableAsync(archivePath, cancellationToken);
        var replica = await ReadReplicableAsync(replicaPath, cancellationToken);

        Assert.AreEqual(archive.Count, replica.Count,
            $"replica holds {replica.Count} object(s); the archive holds {archive.Count}");
        foreach (var (key, bytes) in archive)
        {
            Assert.IsTrue(replica.TryGetValue(key, out var copied), $"replica lacks {key}");
            Assert.IsTrue(bytes.AsSpan().SequenceEqual(copied), $"replica differs at {key}");
        }
    }

    /// <summary>The data blobs of a store's object map, by key.</summary>
    public static Dictionary<string, byte[]> DataBlobs(Dictionary<string, byte[]> objects) =>
        objects.Where(pair => pair.Key.StartsWith("blobs/data/", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Locates a restored file by name, wherever the executor placed the run under its root.</summary>
    public static string? FindFile(string root, string fileName)
    {
        var found = Directory.GetFiles(root, fileName, SearchOption.AllDirectories);
        return found.Length == 1 ? found[0] : null;
    }

    /// <summary>Holds a restored file byte-identical to what was captured.</summary>
    public static async Task AssertFileRestoredAsync(string restoredRoot, string fileName, byte[] expected)
    {
        var path = FindFile(restoredRoot, fileName);
        Assert.IsNotNull(path, $"{fileName} did not restore under {restoredRoot}");
        var actual = await File.ReadAllBytesAsync(path);
        Assert.IsTrue(expected.AsSpan().SequenceEqual(actual), $"{fileName} did not restore byte-identical");
    }

    /// <summary>Writes a source file of raw bytes the "docs" set will capture.</summary>
    public string WriteSourceBytes(string relativePath, byte[] content)
    {
        var full = Path.Combine(Harness.SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>Bytes no compressor shrinks and no two seeds share, reproducible from the seed.</summary>
    public static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, _cancellationToken);
        }
    }

    private RemoteServiceListener Listen(IPEndPoint endpoint) => RemoteServiceListener.Start(
        _keypair, _grants, endpoint, "fallbackplan-agent/test",
        log: null, replicationStateDirectory: Harness.StateDirectory,
        owners: Runtime.ReplicaOwners, notices: Runtime.Notices);
}

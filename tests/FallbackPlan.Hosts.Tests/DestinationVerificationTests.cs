using System.Net;
using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Destination verification end to end (peer-protocol 04, FR-VER-001/005):
/// "verified" traces to bytes read back off the destination's own disk, so a
/// byte that rots or is withheld there — at a peer or at a plain directory —
/// is caught by the next sync, recorded as a failure, and surfaced as a
/// durable notice. The destination's word is never the evidence.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DestinationVerificationTests : IDisposable
{
    private readonly HostHarness _source = new();
    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-verification-dest", Guid.NewGuid().ToString("n"));

    private const ulong PairedAt = 1_722_600_000_000;

    [TestMethod]
    public async Task Sync_ALocalPathReplicaProvingItsBytes_EarnsTheVerificationStamp()
    {
        // The stamp is coverage and age, never a boolean (FR-VER-003): the
        // ledger row says when bytes were last proven, how many ranges the
        // pass proved, and which publication sequence the sample covered —
        // the same currency the replication gate speaks.
        _source.WriteSourceFile("notes.txt", "stamp me");
        await _source.CreateRepositoryAsync();
        _source.WriteConfiguration("every 4h");
        Directory.CreateDirectory(Path.Combine(_source.StateDirectory, "vault"));
        await _source.BackUpAsync();

        var sync = await RunSyncAsync();
        Assert.AreEqual(0, sync.ExitCode, sync.Error);

        var record = DestinationSyncStore.Open(_source.StateDirectory).Find(_source.DocsSetId, "vault");
        Assert.IsNotNull(record);
        Assert.AreEqual(DestinationSyncState.InSync, record.State, $"state={record.State} error={record.LastError}");
        Assert.IsNotNull(record.VerifiedAt, "a passed verification must stamp the row");
        Assert.IsGreaterThanOrEqualTo(1UL, record.VerifiedSequence, "the sample covered the newest publication");
        Assert.AreEqual(record.SyncedSequence, record.VerifiedSequence,
            "what was verified is exactly what the sync claimed to deliver");
        Assert.IsGreaterThanOrEqualTo(1, record.VerifiedObjects, "at least the newest snapshot was proven");
    }

    [TestMethod]
    public async Task Sync_APeerProvingItsProofOverTheWire_EarnsTheVerificationStamp()
    {
        // The peer twin of the stamp: the proof is an HMAC over bytes the
        // responder read off its own disk (04 §2), and both destination
        // kinds earn the identical ledger fact.
        _source.WriteSourceFile("notes.txt", "stamp me remotely");
        var destinationFingerprint = await StartDestinationAsync(PeerRole.StoresHere);
        WritePeerConfiguration(destinationFingerprint);

        // The agent pass captures under the configured set and fans out —
        // the same path the schedule drives (FR-DEST-002).
        var run = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        var sync = await RunSyncAsync();
        Assert.AreEqual(0, sync.ExitCode, sync.Error);

        var record = DestinationSyncStore.Open(_source.StateDirectory).Find(_source.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(DestinationSyncState.InSync, record.State, $"state={record.State} error={record.LastError}");
        Assert.IsNotNull(record.VerifiedAt, "a passed challenge run must stamp the row");
        Assert.AreEqual(record.SyncedSequence, record.VerifiedSequence,
            "what was verified is exactly what the sync claimed to deliver");
        Assert.IsGreaterThanOrEqualTo(1, record.VerifiedObjects, "at least the newest snapshot was proven");

        // The stamp reaches the user: an off-domain, in-sync, proof-covered
        // destination earns `verified`, rendered with its coverage and age —
        // never a bare tick (10 §1.2, ADR-0027 §4).
        var status = await RunStatusAsync();
        Assert.AreEqual(0, status.ExitCode, status.Error);
        Assert.Contains("Verified", status.Output, StringComparison.Ordinal);
        Assert.Contains("% of objects at", status.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sync_APeerThatCannotProveItself_IsRefusedRatherThanQuietlyTrusted()
    {
        // FR-VER-006, and the reversal of an earlier posture: a destination
        // that will not answer challenges used to be synced to anyway and
        // simply left unstamped. But verification is the stated mitigation
        // for a destination that discards data and says otherwise (T-8), and
        // a mitigation the defended-against party can decline in silence is
        // no mitigation — declining was strictly cheaper than failing a
        // challenge. So the hub now refuses to build on it at all.
        _source.WriteSourceFile("notes.txt", "prove it or lose me");
        var destinationFingerprint = await StartDestinationAsync(
            PeerRole.StoresHere, offeredFeatures: FeaturesWithoutVerification);
        WritePeerConfiguration(destinationFingerprint);

        var run = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        // Exit 0: the backup itself succeeded. The destination's state is the
        // ledger's and the notice's to carry, never the pass's.
        var sync = await RunSyncAsync();
        Assert.AreEqual(0, sync.ExitCode, sync.Error);

        var record = DestinationSyncStore.Open(_source.StateDirectory).Find(_source.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(DestinationSyncState.Failed, record.State, $"state={record.State} error={record.LastError}");
        Assert.Contains("FeatureUnsupported", record.LastError!, StringComparison.Ordinal);
        Assert.Contains("destination-verification", record.LastError!, StringComparison.Ordinal);

        // Refused at negotiation, so not one byte was written there.
        Assert.IsFalse(
            Directory.Exists(Path.Combine(_destinationState, "replicas"))
            && Directory.GetDirectories(Path.Combine(_destinationState, "replicas")).Length > 0,
            "a destination refused for being unprovable must hold nothing");

        // And the human is told, durably, with both ways out named.
        var notice = Assert.ContainsSingle(NoticeStore.Open(_source.StateDirectory).Unacknowledged);
        Assert.Contains("cannot answer verification challenges", notice.Message, StringComparison.Ordinal);
        Assert.Contains("acknowledged-none", notice.Message, StringComparison.Ordinal);

        // Nothing off-domain holds the data, so nothing claims protection.
        var status = await RunStatusAsync();
        Assert.AreEqual(0, status.ExitCode, status.Error);
        Assert.DoesNotContain("Protected", status.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Verified", status.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sync_AnAcknowledgedUnprovableDestination_SyncsButClaimsNothing()
    {
        // The escape hatch, and the shape of it: keeping an unprovable
        // destination takes a word in the configuration that nobody types by
        // accident. Having typed it, the copy is made and counts as a copy —
        // but it is never dressed up as proven.
        _source.WriteSourceFile("notes.txt", "knowingly unprovable");
        var destinationFingerprint = await StartDestinationAsync(
            PeerRole.StoresHere, offeredFeatures: FeaturesWithoutVerification);
        WritePeerConfiguration(destinationFingerprint, VerificationPolicy.AcknowledgedNone);

        var run = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        var sync = await RunSyncAsync();
        Assert.AreEqual(0, sync.ExitCode, sync.Error);
        Assert.Contains("docs -> friend: in sync", sync.Output, StringComparison.Ordinal);

        // A real replica, not a silent no-op that would satisfy the rest for free.
        var replica = Directory.GetDirectories(Path.Combine(_destinationState, "replicas")).Single();
        Assert.IsNotEmpty(Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories));

        var record = DestinationSyncStore.Open(_source.StateDirectory).Find(_source.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(DestinationSyncState.InSync, record.State, $"state={record.State} error={record.LastError}");
        Assert.IsGreaterThanOrEqualTo(1UL, record.SyncedSequence);

        // Acknowledged means excused from proving, never counted as proven.
        Assert.IsNull(record.VerifiedAt, "an unchallenged destination must carry no proof date");
        Assert.AreEqual(0UL, record.VerifiedSequence);
        Assert.AreEqual(0, record.VerifiedObjects);
        Assert.IsEmpty(NoticeStore.Open(_source.StateDirectory).Unacknowledged);

        var status = await RunStatusAsync();
        Assert.AreEqual(0, status.ExitCode, status.Error);
        Assert.Contains("Protected", status.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Verified", status.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("% of objects at", status.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sync_ATamperedByteAtTheLocalPathReplica_FailsVerificationDurably()
    {
        // A local-path replica converges clean, then a snapshot object rots
        // on the destination's disk. The copy diff would never notice — the
        // key still exists at its full length — but the read-back comparison
        // must (FR-VER-001), and the finding must outlive the pass
        // (FR-VER-005).
        _source.WriteSourceFile("notes.txt", "verify me");
        await _source.CreateRepositoryAsync();
        _source.WriteConfiguration("every 4h");
        var vault = Path.Combine(_source.StateDirectory, "vault");
        Directory.CreateDirectory(vault);
        await _source.BackUpAsync();

        var first = await RunSyncAsync();
        Assert.AreEqual(0, first.ExitCode, first.Error);
        Assert.Contains("docs -> vault: in sync", first.Output, StringComparison.Ordinal);

        TamperNewestSnapshot(Directory.GetDirectories(vault).Single());

        var second = await RunSyncAsync();
        Assert.AreEqual(0, second.ExitCode, second.Error);

        var record = DestinationSyncStore.Open(_source.StateDirectory).Find(_source.DocsSetId, "vault");
        Assert.IsNotNull(record);
        Assert.AreEqual(DestinationSyncState.Failed, record.State, $"state={record.State} error={record.LastError}");
        Assert.Contains("verification failed", record.LastError!, StringComparison.Ordinal);

        // The stamp from the clean pass survives: it says when bytes were
        // LAST proven, which the failure dates rather than erases.
        Assert.IsNotNull(record.VerifiedAt);

        var notice = Assert.ContainsSingle(NoticeStore.Open(_source.StateDirectory).Unacknowledged);
        Assert.Contains("failed verification", notice.Message, StringComparison.Ordinal);
        Assert.Contains("vault", notice.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sync_ATamperedByteAtThePeerReplica_FailsVerificationDurably()
    {
        // The peer twin: the challenge crosses the wire (04 §2), and a peer
        // whose replica no longer matches cannot produce the proof — however
        // sincerely its software answers "held".
        _source.WriteSourceFile("notes.txt", "verify me remotely");
        await _source.CreateRepositoryAsync();
        await _source.BackUpAsync();
        var destinationFingerprint = await StartDestinationAsync(PeerRole.StoresHere);
        WritePeerConfiguration(destinationFingerprint);

        var first = await RunSyncAsync();
        Assert.AreEqual(0, first.ExitCode, first.Error);
        Assert.Contains("docs -> friend: in sync", first.Output, StringComparison.Ordinal);

        TamperNewestSnapshot(
            Directory.GetDirectories(Path.Combine(_destinationState, "replicas")).Single());

        var second = await RunSyncAsync();
        Assert.AreEqual(0, second.ExitCode, second.Error);

        var record = DestinationSyncStore.Open(_source.StateDirectory).Find(_source.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(DestinationSyncState.Failed, record.State, $"state={record.State} error={record.LastError}");
        Assert.Contains("verification failed", record.LastError!, StringComparison.Ordinal);

        var notice = Assert.ContainsSingle(NoticeStore.Open(_source.StateDirectory).Unacknowledged);
        Assert.Contains("failed verification", notice.Message, StringComparison.Ordinal);
        Assert.Contains("friend", notice.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Inverts every byte of the replica's one snapshot object, in place and
    /// length-preserving: the corruption a copy diff cannot see, because the
    /// key still lists at the size the source expects.
    /// </summary>
    private static void TamperNewestSnapshot(string replicaRoot)
    {
        var snapshot = Directory
            .GetFiles(Path.Combine(replicaRoot, "snapshots"), "*", SearchOption.AllDirectories)
            .Single();
        var bytes = File.ReadAllBytes(snapshot);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= 0xFF;
        }

        File.WriteAllBytes(snapshot, bytes);
    }

    private IPEndPoint? _endpoint;
    private Stopper? _stop;
    private PeerGrantStore? _destinationGrants;

    /// <summary>Declares the running peer as the docs set's one destination.</summary>
    /// <param name="destinationFingerprint">The peer's pinned fingerprint.</param>
    /// <param name="verification">
    /// The destination's verification policy; null leaves the field out of the
    /// file entirely, which is the default a user gets by not thinking about
    /// it — and must therefore be the safe one.
    /// </param>
    private void WritePeerConfiguration(
        string destinationFingerprint, VerificationPolicy? verification = null) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = destinationFingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
                Verification = verification,
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _source.DocsSetId,
                Name = "docs",
                Root = _source.SourceRoot,
                Schedule = "every 4h",
                Destinations = [new SetDestinationReference { Ref = "friend" }],
            },
        ],
    }.Save(Path.Combine(_source.StateDirectory, "config.json"));

    /// <summary>Runs the agent `sync` verb against the harness's archives and state.</summary>
    private Task<HostHarness.Invocation> RunSyncAsync() => HostHarness.RunAsync(
        AgentHost.RunAsync,
        "sync", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
        "--passphrase-env", _source.PassphraseVariable);

    /// <summary>Runs the CLI `status` verb in direct mode, output captured.</summary>
    private async Task<(int ExitCode, string Output, string Error)> RunStatusAsync()
    {
        var output = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var error = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var exit = await Cli.CliApplication.RunAsync(
            [
                "status", "--repo", _source.RepositoryPath,
                "--passphrase-env", _source.PassphraseVariable, "--state", _source.StateDirectory,
            ],
            new System.CommandLine.InvocationConfiguration
            {
                Output = output, Error = error, EnableDefaultExceptionHandler = false,
            });
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>
    /// What a build predating destination verification offers: everything in
    /// the 02 §6 registry except the one feature under test.
    /// </summary>
    private static readonly string[] FeaturesWithoutVerification =
    [
        PeerSessionNegotiation.RetentionInstructionFeature,
        PeerSessionNegotiation.TerminationNoticeFeature,
    ];

    /// <summary>Stands up the destination listener with reciprocal pinned grants.</summary>
    /// <param name="destinationRoleForSource">The role the destination grants the source.</param>
    /// <param name="offeredFeatures">
    /// What the destination announces in its hello; null offers everything
    /// this build supports, which is what a current peer does.
    /// </param>
    private async Task<string> StartDestinationAsync(
        PeerRole destinationRoleForSource, IReadOnlyList<string>? offeredFeatures = null)
    {
        using var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);

        _destinationGrants = PeerGrantStore.Open(_destinationState);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", destinationRoleForSource, PeerTerms.None, PairedAt));

        PeerGrantStore.Open(_source.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        var listenerKeypair = PeerKeypairStore.Open(_destinationState);
        var listener = RemoteServiceListener.Start(
            listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState, offeredFeatures: offeredFeatures);
        listener.Bind(new UnusedService());
        _endpoint = listener.Endpoint;
        _stop = new Stopper(listener, listenerKeypair);

        await Task.CompletedTask;
        return destinationKeypair.Identity.Fingerprint;
    }

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }

    private sealed class Stopper(RemoteServiceListener listener, PeerKeypair keypair) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await listener.DisposeAsync();
            keypair.Dispose();
        }
    }

    public void Dispose()
    {
        _stop?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _source.Dispose();
        if (Directory.Exists(_destinationState))
        {
            try
            {
                Directory.Delete(_destinationState, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}

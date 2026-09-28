using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The destination operator's acknowledgement of a claim (FR-DR-005,
/// peer-protocol 06 §3 and 07 §5.9): a claim that moved a replica here is held
/// until this machine's owner acknowledges it, through the command surface or
/// the agent's <c>acknowledge-claim</c> verb, and the listing says which
/// replicas are held.
/// </summary>
/// <remarks>
/// The acknowledgement has to land in the attribution the listener serves
/// from, or the hold would be lifted in a file while the live retention gate
/// went on refusing. So the runtime owns the store and the listener borrows
/// it, exactly as the host wires them and as the re-attribution suite does.
/// </remarks>
[TestClass]
public sealed class ClaimAcknowledgementTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private const ulong PairedAt = 1_722_600_000_000;
    private const string ClaimedIdHex = "0123456789abcdef0123456789abcdef";
    private const string UnclaimedIdHex = "fedcba9876543210fedcba9876543210";

    private CancellationToken Timeout => _timeout.Token;

    private ServiceRuntime? _runtime;
    private RemoteServiceListener? _listener;
    private ServiceCommandHandler? _local;
    private ServiceCommandHandler? _remote;
    private PeerKeypair? _serviceKeypair;
    private PeerGrantStore? _serviceGrants;
    private readonly List<PeerKeypair> _keypairs = [];

    [TestMethod]
    public async Task Acknowledging_AHeldClaim_ReleasesItAndResolvesItsNotice()
    {
        var rebuilt = await StartServiceWithAHeldClaimAsync();

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await ExecuteAsync(new AcknowledgeReplicaClaimCommand(ClaimedIdHex)), out var changed);
        Assert.IsTrue(
            changed.Lines.Any(line => line.Contains(rebuilt.Identity.Fingerprint, StringComparison.Ordinal)),
            string.Join("\n", changed.Lines));

        // Released in the store the listener serves from, durably, and the
        // claim still stands: acknowledging it confirms it.
        var owner = _runtime!.ReplicaOwners.Find(ClaimedIdHex)!;
        Assert.IsFalse(owner.ClaimAwaitingAcknowledgement);
        Assert.AreEqual(rebuilt.Identity.Fingerprint, owner.Fingerprint);
        Assert.IsFalse(ReplicaOwnerStore.Open(_harness.StateDirectory).Find(ClaimedIdHex)!.ClaimAwaitingAcknowledgement);

        // The notice that asked for the acknowledgement stops surfacing, and
        // stays on record.
        Assert.IsFalse(_runtime.Notices.Unacknowledged.Any(notice => notice.Key == $"replica-claimed:{ClaimedIdHex}"));
        Assert.IsTrue(_runtime.Notices.Notices.Any(notice => notice.Key == $"replica-claimed:{ClaimedIdHex}"));
    }

    [TestMethod]
    public async Task TheAcknowledgement_SaysWhatItCannotAcknowledge()
    {
        _ = await StartServiceWithAHeldClaimAsync();

        var malformed = (ServiceError)await ExecuteAsync(new AcknowledgeReplicaClaimCommand("not-a-repository"));
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, malformed.Reason);

        var unknown = (ServiceError)await ExecuteAsync(new AcknowledgeReplicaClaimCommand(new string('c', 32)));
        Assert.AreEqual(ServiceErrorReason.NotFound, unknown.Reason);

        // A replica nobody claimed has nothing to acknowledge, and saying so
        // is an answer rather than a failure, as a re-point to the current
        // owner is.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await ExecuteAsync(new AcknowledgeReplicaClaimCommand(UnclaimedIdHex)), out var nothing);
        Assert.IsTrue(nothing.Lines.Any(line => line.Contains("nothing changed", StringComparison.Ordinal)));
        Assert.IsTrue(_runtime!.ReplicaOwners.Find(ClaimedIdHex)!.ClaimAwaitingAcknowledgement);
    }

    [TestMethod]
    public async Task APairedRemoteConsole_MayNotAcknowledgeAClaim()
    {
        // Whose deletions this machine obeys is its own operator's decision
        // (ADR-0053 §3), not a paired console's.
        _ = await StartServiceWithAHeldClaimAsync();

        var refused = (ServiceError)await _remote!.ExecuteAsync(
            new AcknowledgeReplicaClaimCommand(ClaimedIdHex), Timeout);

        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.IsTrue(_runtime!.ReplicaOwners.Find(ClaimedIdHex)!.ClaimAwaitingAcknowledgement);
    }

    [TestMethod]
    public async Task TheListing_SaysWhichClaimsAwaitAcknowledgement()
    {
        _ = await StartServiceWithAHeldClaimAsync();

        Assert.IsInstanceOfType<ReplicaAttributionsResult>(
            await ExecuteAsync(new ListReplicaAttributionsCommand()), out var listed);

        Assert.IsTrue(listed.Attributions.Single(row => row.RepositoryId == ClaimedIdHex).ClaimAwaitingAcknowledgement);
        Assert.IsFalse(listed.Attributions.Single(row => row.RepositoryId == UnclaimedIdHex).ClaimAwaitingAcknowledgement);
    }

    [TestMethod]
    public async Task TheAgentVerb_AServiceIsListening_AcknowledgesThroughItsLiveStore()
    {
        // Routed as `reattribute` is: with a service holding the state
        // directory, the acknowledgement reaches the service's own ledger,
        // never the file beside it, which a second writer would race.
        _ = await StartServiceWithAHeldClaimAsync();
        await using var socket = FallbackPlan.Api.Transport.LocalServiceListener.Start(_local!, _harness.StateDirectory);

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "acknowledge-claim", "--state", _harness.StateDirectory, "--repository", ClaimedIdHex);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains(ClaimedIdHex, result.Output, StringComparison.Ordinal);
        Assert.IsFalse(_runtime!.ReplicaOwners.Find(ClaimedIdHex)!.ClaimAwaitingAcknowledgement);
    }

    [TestMethod]
    public async Task TheAgentVerb_NoServiceIsListening_AcknowledgesOnTheLedger()
    {
        // With no service holding the state directory, the ledger and the
        // notices are the verb's own to change.
        _ = await StartServiceWithAHeldClaimAsync();
        await StopServiceAsync();

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "acknowledge-claim", "--state", _harness.StateDirectory, "--repository", ClaimedIdHex);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.IsFalse(ReplicaOwnerStore.Open(_harness.StateDirectory).Find(ClaimedIdHex)!.ClaimAwaitingAcknowledgement);
        Assert.IsFalse(NoticeStore.Open(_harness.StateDirectory).Unacknowledged
            .Any(notice => notice.Key == $"replica-claimed:{ClaimedIdHex}"));
    }

    [TestMethod]
    public async Task TheAgentVerb_WithoutARepository_SaysHowItIsUsed()
    {
        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "acknowledge-claim", "--state", _harness.StateDirectory);

        Assert.AreEqual(1, result.ExitCode, result.Output);
        Assert.Contains("acknowledge-claim --state <dir> --repository <hex>", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TheAgentVerb_AServiceWantsASignedInOwner_SaysSoAndTouchesNothing()
    {
        // The installation has accounts, so the service's socket answers only
        // a signed-in owner (FR-USR-001), and this verb carries no session.
        // The honest outcome is the refusal with the two ways on named, never
        // a fallback to the file.
        _ = await StartServiceWithAHeldClaimAsync();
        var users = UserStore.Open(_harness.StateDirectory);
        var sessions = new SessionRegistry();
        await using var socket = FallbackPlan.Api.Transport.LocalServiceListener.Start(
            () => new AuthenticatingService(_local!, users, sessions, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance),
            _harness.StateDirectory);

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "acknowledge-claim", "--state", _harness.StateDirectory, "--repository", ClaimedIdHex);

        Assert.AreEqual(1, result.ExitCode, result.Output);
        Assert.Contains("signed in", result.Error, StringComparison.Ordinal);
        Assert.Contains("console", result.Error, StringComparison.Ordinal);
        Assert.IsTrue(_runtime!.ReplicaOwners.Find(ClaimedIdHex)!.ClaimAwaitingAcknowledgement);
        Assert.IsTrue(ReplicaOwnerStore.Open(_harness.StateDirectory).Find(ClaimedIdHex)!.ClaimAwaitingAcknowledgement);
    }

    private async ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command) =>
        await _local!.ExecuteAsync(command, Timeout);

    /// <summary>
    /// Starts the runtime and, over it, the remote binding as the host wires
    /// them, with two replicas stored here: one a claim moved to a rebuilt
    /// machine and not yet acknowledged, with the notice that asks for the
    /// acknowledgement, and one never claimed.
    /// </summary>
    /// <returns>The rebuilt machine that made the claim.</returns>
    private async Task<PeerKeypair> StartServiceWithAHeldClaimAsync()
    {
        await _harness.SetupAsync();
        _runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            Timeout);

        _serviceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        _serviceGrants = PeerGrantStore.Open(_harness.StateDirectory);
        _listener = RemoteServiceListener.Start(
            _serviceKeypair, _serviceGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _harness.StateDirectory, owners: _runtime.ReplicaOwners);
        var binding = RemoteBindingState.On(_listener.Endpoint.ToString());
        _listener.Bind(new ServiceCommandHandler(_runtime, binding, CallerScope.Remote));
        _local = new ServiceCommandHandler(_runtime, binding, CallerScope.Local);
        _remote = new ServiceCommandHandler(_runtime, binding, CallerScope.Remote);

        var gone = PairPeer("gone");
        var rebuilt = PairPeer("rebuilt");
        Assert.IsTrue(_runtime.ReplicaOwners.TryAttribute(
            ClaimedIdHex, gone.Identity.Fingerprint, claimPublicKey: new string('e', 64)));
        Assert.AreEqual(gone.Identity.Fingerprint, _runtime.ReplicaOwners.Claim(ClaimedIdHex, rebuilt.Identity.Fingerprint));
        Assert.IsTrue(_runtime.ReplicaOwners.TryAttribute(UnclaimedIdHex, gone.Identity.Fingerprint));
        _runtime.Notices.Raise(
            $"replica-claimed:{ClaimedIdHex}", "A claim moved a replica here and awaits acknowledgement.", PairedAt);

        return rebuilt;
    }

    /// <summary>Stops the service, leaving its state directory to whoever comes next.</summary>
    private async Task StopServiceAsync()
    {
        await _listener!.DisposeAsync();
        _listener = null;
        await _runtime!.DisposeAsync();
        _runtime = null;
    }

    /// <summary>A peer that stores here, pinned both ways.</summary>
    private PeerKeypair PairPeer(string label)
    {
        var state = Path.Combine(_harness.WorkPath, label);
        Directory.CreateDirectory(state);
        var keypair = PeerKeypairStore.Open(state);
        _keypairs.Add(keypair);

        _serviceGrants!.Pin(new PeerGrant(keypair.Identity, label, PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(state).Pin(new PeerGrant(
            _serviceKeypair!.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));
        return keypair;
    }

    public void Dispose()
    {
        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _serviceKeypair?.Dispose();
        foreach (var keypair in _keypairs)
        {
            keypair.Dispose();
        }

        _timeout.Dispose();
        _harness.Dispose();
    }
}

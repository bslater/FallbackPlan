using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The invite verbs end to end (ADR-0030 Amendment 3, ADR-0037): two real
/// services on loopback — one issues the invite over its command surface, the
/// other redeems it over its own — and the trust lands where the ceremony says
/// it does: a grant on each side, the invite spent, the notice raised.
/// </summary>
[TestClass]
public sealed class InvitePairingCommandTests : IDisposable
{
    private readonly HostHarness _siteOne = new();
    private readonly HostHarness _siteTwo = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _siteOne.Dispose();
        _siteTwo.Dispose();
    }

    [TestMethod]
    public async Task PairWithInvite_TwoServicesAndOneSpokenCode_PinBothSides()
    {
        await _siteOne.CreateRepositoryAsync();
        _siteOne.WriteConfiguration("every 1h");
        await _siteTwo.CreateRepositoryAsync();
        _siteTwo.WriteConfiguration("every 1h");

        // Site 2 — the household's other machine — listens, and its operator
        // issues the invite: label, role and quota committed here, before any
        // connection exists. That act is site 2's approval.
        await using var runtimeTwo = await StartAsync(_siteTwo);
        using var keypairTwo = PeerKeypairStore.Open(_siteTwo.StateDirectory);
        var grantsTwo = PeerGrantStore.Open(_siteTwo.StateDirectory);
        await using var listener = RemoteServiceListener.Start(
            keypairTwo, grantsTwo, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            replicationStateDirectory: _siteTwo.StateDirectory);
        var handlerTwo = new ServiceCommandHandler(
            runtimeTwo, RemoteBindingState.On(listener.Endpoint.ToString()));
        listener.Bind(handlerTwo);

        Assert.IsInstanceOfType<PairingInviteResult>(
            await handlerTwo.ExecuteAsync(
                new CreatePairingInviteCommand("the laptop", "stores-here", 5_000_000, 60), _timeout.Token),
            out var invite);
        Assert.IsNull(invite.Warning, "the binding is on, so nothing stands between the invite and a dial");
        Assert.AreEqual(listener.Endpoint.ToString(), invite.ListeningEndpoint);

        // The listing knows the invite; it does not know the code.
        Assert.IsInstanceOfType<PairingInvitesResult>(
            await handlerTwo.ExecuteAsync(new ListPairingInvitesCommand(), _timeout.Token), out var listed);
        var pending = Assert.ContainsSingle(listed.Invites);
        Assert.AreEqual(invite.InviteId, pending.InviteId);
        Assert.IsNull(pending.ConsumedBy);

        // Site 1's operator types what was spoken: the code, the address, and
        // their own name for the far service. That act is site 1's approval.
        await using var runtimeOne = await StartAsync(_siteOne);
        var handlerOne = new ServiceCommandHandler(runtimeOne, RemoteBindingState.Off);

        Assert.IsInstanceOfType<PairingCompletedResult>(
            await handlerOne.ExecuteAsync(
                new PairWithInviteCommand(
                    invite.Code, "127.0.0.1", listener.Endpoint.Port, "the vault"),
                _timeout.Token),
            out var completed);

        using var keypairOne = PeerKeypairStore.Open(_siteOne.StateDirectory);
        Assert.AreEqual(keypairTwo.Identity.Fingerprint, completed.Fingerprint);
        Assert.AreEqual("the vault", completed.Label);
        Assert.AreEqual("stores-here", completed.TheirRole);

        // Site 1 pinned the complement under the operator's own name…
        var grantsOne = PeerGrantStore.Open(_siteOne.StateDirectory);
        var atOne = grantsOne.Find(keypairTwo.Identity)!;
        Assert.AreEqual(PeerRole.StoresForUs, atOne.Role);
        Assert.AreEqual("the vault", atOne.Label);

        // …site 2 pinned the redeemer under the invite's commitments…
        var atTwo = grantsTwo.Find(keypairOne.Identity)!;
        Assert.AreEqual(PeerRole.StoresHere, atTwo.Role);
        Assert.AreEqual("the laptop", atTwo.Label);
        Assert.AreEqual(5_000_000UL, atTwo.Terms.QuotaBytes);

        // …the invite is spent and says by whom…
        Assert.IsInstanceOfType<PairingInvitesResult>(
            await handlerTwo.ExecuteAsync(new ListPairingInvitesCommand(), _timeout.Token), out var after);
        Assert.AreEqual(keypairOne.Identity.Fingerprint, Assert.ContainsSingle(after.Invites).ConsumedBy);

        // …and a second redemption of the spent code is a stated refusal.
        Assert.IsInstanceOfType<ServiceError>(
            await handlerOne.ExecuteAsync(
                new PairWithInviteCommand(invite.Code, "127.0.0.1", listener.Endpoint.Port, "again"),
                _timeout.Token),
            out var replayed);
        Assert.AreEqual(ServiceErrorReason.Refused, replayed.Reason);
    }

    [TestMethod]
    public async Task CreatePairingInvite_WithTheBindingOff_SaysWhatStandsInTheWay()
    {
        await _siteTwo.CreateRepositoryAsync();
        _siteTwo.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync(_siteTwo);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<PairingInviteResult>(
            await handler.ExecuteAsync(
                new CreatePairingInviteCommand("the laptop", "stores-here", null, null), _timeout.Token),
            out var invite);

        Assert.IsNull(invite.ListeningEndpoint);
        Assert.Contains("--remote-interface", invite.Warning!, StringComparison.Ordinal);

        // Issued all the same — the operator may enable the binding next —
        // and revocable before anyone ever dials.
        Assert.IsInstanceOfType<AcknowledgedResult>(
            await handler.ExecuteAsync(new RevokePairingInviteCommand(invite.InviteId), _timeout.Token));
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RevokePairingInviteCommand(invite.InviteId), _timeout.Token),
            out var missing);
        Assert.AreEqual(ServiceErrorReason.NotFound, missing.Reason);
    }

    [TestMethod]
    public async Task CreatePairingInvite_QuotaForAPureDestination_IsRefused()
    {
        await _siteTwo.CreateRepositoryAsync();
        _siteTwo.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync(_siteTwo);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new CreatePairingInviteCommand("them", "stores-for-us", 1_000, null), _timeout.Token),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refused.Reason);
    }

    private async Task<ServiceRuntime> StartAsync(HostHarness harness)
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = harness.ArchivesRoot,
                StateDirectory = harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }
}

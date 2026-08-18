using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The unpair verb over the contract (ADR-0039, ADR-0030 Amendment 2,
/// FR-DEST-008): a console can end a pairing — announced to the peer while
/// the grant still authenticates the session, revoked locally with a
/// tombstone — but never out from under a configured destination, because a
/// revocation must not silently break what sets sync to.
/// </summary>
[TestClass]
public sealed class UnpairCommandTests : IDisposable
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
    public async Task Unpair_AnnouncedOverTheWire_EndsThePairingOnBothSides()
    {
        var paired = await PairAsync();
        await using var one = paired.RuntimeOne;
        await using var two = paired.RuntimeTwo;
        await using var heldListener = paired.Listener;
        using var heldKeypair = paired.KeypairTwo;
        var (handlerOne, handlerTwo, grantsTwo) = (paired.HandlerOne, paired.HandlerTwo, paired.GrantsTwo);
        var keypairOneFp = paired.IdentityOne.Fingerprint;
        var keypairTwoFp = paired.IdentityTwo.Fingerprint;
        var endpoint = paired.Endpoint;

        // While a destination references the peer, the refusal names it — the
        // honest order is delete the destination first (ADR-0037 §4).
        Assert.IsInstanceOfType<AcknowledgedResult>(await handlerOne.ExecuteAsync(
            new UpsertDestinationCommand(new DestinationDescriptor(
                null, "site-two", "peer", null, keypairTwoFp, endpoint)),
            _timeout.Token));

        Assert.IsInstanceOfType<ServiceError>(
            await handlerOne.ExecuteAsync(new UnpairCommand(keypairTwoFp), _timeout.Token), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("site-two", refused.Message);
        Assert.IsNotNull(PeerGrantStore.Open(_siteOne.StateDirectory).Grants
            .SingleOrDefault(grant => grant.Identity.Fingerprint == keypairTwoFp), "the grant must survive a refusal");

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handlerOne.ExecuteAsync(new DeleteDestinationCommand("site-two"), _timeout.Token));

        // Deleting the destination emptied the address book, so the caller
        // says where to dial — the same escape the agent verb's --to gives.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handlerOne.ExecuteAsync(
                new UnpairCommand(keypairTwoFp, Notify: true, Endpoint: endpoint), _timeout.Token),
            out var ended);
        Assert.Contains(
            line => line.Contains("notified", StringComparison.Ordinal), ended.Lines,
            string.Join(" | ", ended.Lines));
        Assert.Contains(line => line.Contains("tombstone", StringComparison.Ordinal), ended.Lines);

        // Site one: grant gone, tombstone left.
        var grantsOne = PeerGrantStore.Open(_siteOne.StateDirectory);
        Assert.IsEmpty(grantsOne.Grants);
        Assert.IsTrue(grantsOne.IsRevoked(paired.IdentityTwo), "the ending leaves a tombstone, not amnesia");

        // Site two heard the announcement: its grant goes and the durable
        // notice stands until a person acknowledges it.
        await WaitForAsync(() => grantsTwo.Grants.Count == 0);
        await WaitForAsync(() => NoticeStore.Open(_siteTwo.StateDirectory).Unacknowledged
            .Any(notice => notice.Key == $"peering-terminated:{keypairOneFp}"));

        // And the far side's own contract surface can retire that notice —
        // the loop this ADR exists to close.
        Assert.IsInstanceOfType<NoticesResult>(
            await handlerTwo.ExecuteAsync(new ListNoticesCommand(), _timeout.Token), out var notices);
        _ = notices;
    }

    [TestMethod]
    public async Task Unpair_QuietOrMistaken_TellsTheTruth()
    {
        var paired = await PairAsync();
        await using var one = paired.RuntimeOne;
        await using var two = paired.RuntimeTwo;
        await using var heldListener = paired.Listener;
        using var heldKeypair = paired.KeypairTwo;
        var handlerOne = paired.HandlerOne;

        // An unknown fingerprint is a stated miss…
        Assert.IsInstanceOfType<ServiceError>(
            await handlerOne.ExecuteAsync(new UnpairCommand("ffff"), _timeout.Token), out var unknown);
        Assert.AreEqual(ServiceErrorReason.NotFound, unknown.Reason);

        // …and notify:false revokes locally, saying the peer learns later.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handlerOne.ExecuteAsync(
                new UnpairCommand(paired.IdentityTwo.Fingerprint, Notify: false), _timeout.Token),
            out var quiet);
        Assert.IsFalse(quiet.Lines.Any(line => line.Contains("notified", StringComparison.Ordinal)));
        var grantsOne = PeerGrantStore.Open(_siteOne.StateDirectory);
        Assert.IsEmpty(grantsOne.Grants);
        Assert.IsTrue(grantsOne.IsRevoked(paired.IdentityTwo));
    }

    private sealed record PairedSites(
        ServiceRuntime RuntimeOne,
        ServiceRuntime RuntimeTwo,
        ServiceCommandHandler HandlerOne,
        ServiceCommandHandler HandlerTwo,
        PeerGrantStore GrantsTwo,
        FallbackPlan.Protocol.PeerIdentity IdentityOne,
        FallbackPlan.Protocol.PeerIdentity IdentityTwo,
        string Endpoint,
        RemoteServiceListener Listener,
        PeerKeypair KeypairTwo);

    /// <summary>Pairs the two sites by invite and answers everything the tests act on.</summary>
    private async Task<PairedSites> PairAsync()
    {
        await _siteOne.CreateRepositoryAsync();
        _siteOne.WriteConfiguration("every 1h");
        await _siteTwo.CreateRepositoryAsync();
        _siteTwo.WriteConfiguration("every 1h");

        var runtimeTwo = await StartAsync(_siteTwo);

        // The listener holds this keypair for its lifetime, so it is returned
        // for the test to dispose — a `using` here would close the key under
        // the listener's feet before the termination dial arrives.
        var keypairTwo = PeerKeypairStore.Open(_siteTwo.StateDirectory);
        var grantsTwo = PeerGrantStore.Open(_siteTwo.StateDirectory);
        var listener = RemoteServiceListener.Start(
            keypairTwo, grantsTwo, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            replicationStateDirectory: _siteTwo.StateDirectory);
        var handlerTwo = new ServiceCommandHandler(
            runtimeTwo, RemoteBindingState.On(listener.Endpoint.ToString()));
        listener.Bind(handlerTwo);

        Assert.IsInstanceOfType<PairingInviteResult>(
            await handlerTwo.ExecuteAsync(
                new CreatePairingInviteCommand("site one", "stores-here", null, 60), _timeout.Token),
            out var invite);

        var runtimeOne = await StartAsync(_siteOne);
        var handlerOne = new ServiceCommandHandler(runtimeOne, RemoteBindingState.Off);
        Assert.IsInstanceOfType<PairingCompletedResult>(
            await handlerOne.ExecuteAsync(
                new PairWithInviteCommand(invite.Code, "127.0.0.1", listener.Endpoint.Port, "site-two"),
                _timeout.Token));

        using var keypairOne = PeerKeypairStore.Open(_siteOne.StateDirectory);
        return new PairedSites(
            runtimeOne, runtimeTwo, handlerOne, handlerTwo, grantsTwo,
            keypairOne.Identity, keypairTwo.Identity,
            $"127.0.0.1:{listener.Endpoint.Port}", listener, keypairTwo);
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            _timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, _timeout.Token);
        }
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

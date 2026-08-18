using System.Net;
using System.Net.Sockets;
using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// Invite-authenticated pairing (01 §2.7, ADR-0030 Amendment 3): the code one
/// operator speaks to another stands in for the string comparison, so every
/// property the comparison carried — mutual approval, relay defeat, nothing
/// pinned on failure — is re-proven here for the code, over real sockets where
/// the wire is what is claimed.
/// </summary>
[TestClass]
public sealed class InvitePairingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    private const ulong NowMs = 1_722_600_000_000;

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-invite-tests", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

    private readonly PeerKeypair _service = PeerKeypair.Generate();
    private readonly PeerKeypair _laptop = PeerKeypair.Generate();

    private CancellationToken Cancellation => _cts.Token;

    public void Dispose()
    {
        _cts.Dispose();
        _service.Dispose();
        _laptop.Dispose();
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }

    private PeerGrantStore Grants(string name) => PeerGrantStore.Open(Path.Combine(_stateDirectory, name));

    private PairingInviteStore Invites(string name) => PairingInviteStore.Open(Path.Combine(_stateDirectory, name));

    private static (Socket Listener, IPEndPoint Endpoint) Listen()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        return (listener, (IPEndPoint)listener.LocalEndPoint!);
    }

    [TestMethod]
    public void InviteCode_RenderedForAHuman_ParsesBackThroughTheSeparatorsTheyAdd()
    {
        var code = PairingInviteCode.Create(PeerRole.StoresHere);
        var rendered = code.Render();

        StringAssert.Contains(rendered, "-"); // grouped for reading aloud

        Assert.IsTrue(PairingInviteCode.TryParse(rendered, out var parsed, out _));
        Assert.AreEqual(code.InviteIdHex, parsed!.InviteIdHex);
        Assert.AreEqual(PeerRole.StoresHere, parsed.IssuerRole);
        CollectionAssert.AreEqual(code.Secret.ToArray(), parsed.Secret.ToArray());

        // The forms humans actually produce: spaces for hyphens, stray case.
        Assert.IsTrue(PairingInviteCode.TryParse(
            rendered.Replace('-', ' ').ToUpperInvariant(), out var respoken, out _));
        Assert.AreEqual(code.InviteIdHex, respoken!.InviteIdHex);

        Assert.IsFalse(PairingInviteCode.TryParse("not a code", out _, out var defect));
        Assert.IsNotNull(defect);
        Assert.IsFalse(PairingInviteCode.TryParse(rendered[..^4], out _, out _), "a truncated code is refused");
    }

    [TestMethod]
    public void InviteStore_IssueConsumeAndExpiry_AreExactlyOnceAndClockDriven()
    {
        var store = Invites("store");
        var (code, invite) = store.Issue(
            "laptop", PeerRole.StoresHere, new PeerTerms(1_000, string.Empty, 0), NowMs, 60_000);

        Assert.IsTrue(store.HasPending(NowMs));
        Assert.IsNotNull(store.FindPending(code.InviteId.Span, NowMs));
        Assert.IsNull(store.FindPending(code.InviteId.Span, NowMs + 60_001), "expiry is the clock's, exactly");

        // First consumption wins; the second finds nothing to spend.
        Assert.IsTrue(store.Consume(invite.InviteIdHex, "fp-1", NowMs));
        Assert.IsFalse(store.Consume(invite.InviteIdHex, "fp-2", NowMs));
        Assert.IsNull(store.FindPending(code.InviteId.Span, NowMs));
        Assert.AreEqual("fp-1", store.Invites.Single().ConsumedBy);

        // And the store is durable: a reopened store knows all of it.
        var reopened = Invites("store");
        Assert.AreEqual("fp-1", reopened.Invites.Single().ConsumedBy);

        var (revocable, revoked) = reopened.Issue("desk", PeerRole.Both, PeerTerms.None, NowMs, 60_000);
        Assert.IsTrue(reopened.Revoke(revoked.InviteIdHex));
        Assert.IsNull(reopened.FindPending(revocable.InviteId.Span, NowMs));
    }

    [TestMethod]
    public async Task InvitePairing_ACodeSpokenBetweenOperators_PinsBothSidesWithNoPrompt()
    {
        var serviceGrants = Grants("svc-ok");
        var laptopGrants = Grants("lap-ok");
        var invites = Invites("inv-ok");

        // The issuing operator's approval happens HERE, with the role, label
        // and quota committed before any connection exists.
        var (code, issued) = invites.Issue(
            "the laptop", PeerRole.StoresHere, new PeerTerms(5_000_000, string.Empty, 2), NowMs, 3_600_000);
        var spoken = code.Render();

        var (listener, endpoint) = Listen();
        using var heldListener = listener;

        var serviceSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                await listener.AcceptAsync(Cancellation), Now, Cancellation);
            var first = await PeerFrame.ReadAsync(connection.Stream, Cancellation);
            Assert.AreEqual(PeerMessageType.PairOffer, first!.Value.Type);
            return await PairingCeremony.AcceptWithInviteAsync(
                connection.Stream, PairOffer.Read(first.Value.Body), _service, serviceGrants, "service",
                invites, connection.RemoteBinding, connection.LocalBinding, NowMs, Cancellation);
        });

        var laptopSide = Task.Run(async () =>
        {
            Assert.IsTrue(PairingInviteCode.TryParse(spoken, out var parsed, out _));
            await using var connection = await PeerTlsConnection.DialAsync(
                endpoint.Address.ToString(), endpoint.Port, Now, Cancellation);
            return await PairingCeremony.OfferWithInviteAsync(
                connection.Stream, _laptop, laptopGrants, "laptop", parsed!,
                connection.LocalBinding, connection.RemoteBinding, NowMs, Cancellation);
        });

        var serviceResult = await serviceSide;
        var laptopResult = await laptopSide;

        Assert.IsTrue(serviceResult.Approved);
        Assert.IsTrue(laptopResult.Approved);

        // The issuer pinned the redeemer under the invite's own commitments…
        var atService = serviceGrants.Find(_laptop.Identity)!;
        Assert.AreEqual("the laptop", atService.Label);
        Assert.AreEqual(PeerRole.StoresHere, atService.Role);
        Assert.AreEqual(5_000_000UL, atService.Terms.QuotaBytes);

        // …the redeemer pinned the complement, terms adopted from the wire…
        var atLaptop = laptopGrants.Find(_service.Identity)!;
        Assert.AreEqual(PeerRole.StoresForUs, atLaptop.Role);
        Assert.AreEqual(5_000_000UL, atLaptop.Terms.QuotaBytes);

        // …and the invite is spent, attributed to who spent it.
        Assert.AreEqual(_laptop.Identity.Fingerprint, invites.Invites
            .Single(candidate => candidate.InviteIdHex == issued.InviteIdHex).ConsumedBy);
    }

    [TestMethod]
    public async Task InvitePairing_TheWrongSecret_IsRefusedAndSpendsNothing()
    {
        var serviceGrants = Grants("svc-wrong");
        var laptopGrants = Grants("lap-wrong");
        var invites = Invites("inv-wrong");

        var (code, _) = invites.Issue("the laptop", PeerRole.StoresHere, PeerTerms.None, NowMs, 3_600_000);

        // The right identifier under the wrong secret — a mistyped code whose
        // typo landed in the secret half, or a guesser who scraped the id.
        var forged = ForgeCode(code.InviteIdHex, PeerRole.StoresHere);

        var (listener, endpoint) = Listen();
        using var heldListener = listener;

        var serviceSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                await listener.AcceptAsync(Cancellation), Now, Cancellation);
            var first = await PeerFrame.ReadAsync(connection.Stream, Cancellation);
            return await PairingCeremony.AcceptWithInviteAsync(
                connection.Stream, PairOffer.Read(first!.Value.Body), _service, serviceGrants, "service",
                invites, connection.RemoteBinding, connection.LocalBinding, NowMs, Cancellation);
        });

        var laptopSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.DialAsync(
                endpoint.Address.ToString(), endpoint.Port, Now, Cancellation);
            return await PairingCeremony.OfferWithInviteAsync(
                connection.Stream, _laptop, laptopGrants, "laptop", forged,
                connection.LocalBinding, connection.RemoteBinding, NowMs, Cancellation);
        });

        var serviceResult = await serviceSide;
        var laptopResult = await laptopSide;

        Assert.IsFalse(serviceResult.Approved);
        Assert.AreEqual(PeerRefusalReason.InviteUnknown, serviceResult.Refusal!.Reason);
        Assert.IsFalse(laptopResult.Approved);

        // Nothing pinned anywhere, and the invite survives for its real owner.
        Assert.IsNull(serviceGrants.Find(_laptop.Identity));
        Assert.IsNull(laptopGrants.Find(_service.Identity));
        Assert.IsNotNull(invites.FindPending(code.InviteId.Span, NowMs));
    }

    [TestMethod]
    public async Task InvitePairing_ASecondRedemption_FindsTheInviteSpent()
    {
        var invites = Invites("inv-replay");
        var (code, _) = invites.Issue("the laptop", PeerRole.StoresHere, PeerTerms.None, NowMs, 3_600_000);
        var spoken = code.Render();

        await RunPairingAsync(spoken, invites, "svc-replay-1", "lap-replay-1");

        // The same code again, from a fresh keypair: the invite is consumed,
        // so the second dialler is refused with the one undistinguishing code.
        using var second = PeerKeypair.Generate();
        var result = await RunPairingAsync(
            spoken, invites, "svc-replay-2", "lap-replay-2", offererKeypair: second);

        Assert.IsFalse(result.Offerer.Approved);
        Assert.AreEqual(PeerRefusalReason.InviteUnknown, result.Offerer.Refusal!.Reason);
        Assert.IsNull(Grants("svc-replay-2").Find(second.Identity));
    }

    [TestMethod]
    public async Task InvitePairing_ARelayBetweenTwoConnections_FailsBothSidesAndSpendsNothing()
    {
        var serviceGrants = Grants("svc-mitm");
        var laptopGrants = Grants("lap-mitm");
        var invites = Invites("inv-mitm");

        var (code, _) = invites.Issue("the laptop", PeerRole.StoresHere, PeerTerms.None, NowMs, 3_600_000);
        var spoken = code.Render();

        var (serviceListener, serviceEndpoint) = Listen();
        var (malloryFront, malloryEndpoint) = Listen();
        using var heldService = serviceListener;
        using var heldFront = malloryFront;

        var serviceSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                await serviceListener.AcceptAsync(Cancellation), Now, Cancellation);
            var first = await PeerFrame.ReadAsync(connection.Stream, Cancellation);
            return await PairingCeremony.AcceptWithInviteAsync(
                connection.Stream, PairOffer.Read(first!.Value.Body), _service, serviceGrants, "service",
                invites, connection.RemoteBinding, connection.LocalBinding, NowMs, Cancellation);
        });

        var laptopSide = Task.Run(async () =>
        {
            Assert.IsTrue(PairingInviteCode.TryParse(spoken, out var parsed, out _));
            await using var connection = await PeerTlsConnection.DialAsync(
                malloryEndpoint.Address.ToString(), malloryEndpoint.Port, Now, Cancellation);
            return await PairingCeremony.OfferWithInviteAsync(
                connection.Stream, _laptop, laptopGrants, "laptop", parsed!,
                connection.LocalBinding, connection.RemoteBinding, NowMs, Cancellation);
        });

        // Mallory terminates TLS on both legs and relays frames verbatim —
        // the shape channel binding exists to kill (02 §3.2, here 01 §2.7).
        var relay = Task.Run(async () =>
        {
            await using var fromLaptop = await PeerTlsConnection.AcceptAsync(
                await malloryFront.AcceptAsync(Cancellation), Now, Cancellation);
            await using var toService = await PeerTlsConnection.DialAsync(
                serviceEndpoint.Address.ToString(), serviceEndpoint.Port, Now, Cancellation);
            await Task.WhenAll(
                PumpAsync(fromLaptop.Stream, toService.Stream), PumpAsync(toService.Stream, fromLaptop.Stream));
        });

        var serviceResult = await serviceSide;
        var laptopOutcome = await CaptureAsync(laptopSide);
        await IgnoreFaultAsync(relay);

        // The service saw a MAC computed over the OTHER connection's bindings.
        Assert.IsFalse(serviceResult.Approved);
        Assert.AreEqual(PeerRefusalReason.InviteUnknown, serviceResult.Refusal!.Reason);
        Assert.IsTrue(
            laptopOutcome.Refusal is { Reason: PeerRefusalReason.InviteUnknown } || laptopOutcome.Threw,
            "the dialling side must not complete either");

        Assert.IsNull(serviceGrants.Find(_laptop.Identity));
        Assert.IsNull(laptopGrants.Find(_service.Identity));
        Assert.IsNotNull(invites.FindPending(code.InviteId.Span, NowMs), "a defeated relay spends nothing");
    }

    [TestMethod]
    public async Task InvitePairing_AnOfferWithNoInvite_IsRefusedNotPrompted()
    {
        // A SAS-ceremony dialler reaching the invite acceptor: there is no
        // human here to compare strings with, so the answer is a stated
        // refusal, never a hang and never a silent downgrade.
        var invites = Invites("inv-none");
        invites.Issue("someone", PeerRole.StoresHere, PeerTerms.None, NowMs, 3_600_000);

        var (listener, endpoint) = Listen();
        using var heldListener = listener;

        var serviceSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                await listener.AcceptAsync(Cancellation), Now, Cancellation);
            var first = await PeerFrame.ReadAsync(connection.Stream, Cancellation);
            return await PairingCeremony.AcceptWithInviteAsync(
                connection.Stream, PairOffer.Read(first!.Value.Body), _service, Grants("svc-none"), "service",
                invites, connection.RemoteBinding, connection.LocalBinding, NowMs, Cancellation);
        });

        var laptopSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.DialAsync(
                endpoint.Address.ToString(), endpoint.Port, Now, Cancellation);
            return await PairingCeremony.OfferAsync(
                connection.Stream, _laptop, Grants("lap-none"), "laptop", PeerRole.StoresForUs,
                (_, _) => ValueTask.FromResult(true), NowMs, Cancellation);
        });

        var serviceResult = await serviceSide;
        var laptopResult = await laptopSide;

        Assert.IsFalse(serviceResult.Approved);
        Assert.AreEqual(PeerRefusalReason.InviteUnknown, serviceResult.Refusal!.Reason);
        Assert.IsFalse(laptopResult.Approved);
    }

    [TestMethod]
    public async Task InviteMessages_TheNewKeys_RoundTripThroughFrames()
    {
        var contribution = new PairingContribution(
            _laptop.Identity, new byte[32], Enumerable.Repeat((byte)7, 32).ToArray());
        var inviteId = new byte[] { 1, 2, 3, 4 };
        var mac = Enumerable.Repeat((byte)9, 32).ToArray();

        using var stream = new MemoryStream();
        await PeerFrame.WriteAsync(
            stream, new PairOffer(contribution, "laptop", 1, PeerRole.StoresForUs, inviteId), Cancellation);
        await PeerFrame.WriteAsync(
            stream, new PairConfirm(new byte[64], mac), Cancellation);
        await PeerFrame.WriteAsync(
            stream, new PairConfirm(new byte[64]), Cancellation);
        stream.Position = 0;

        var offerFrame = await PeerFrame.ReadAsync(stream, Cancellation);
        var offer = PairOffer.Read(offerFrame!.Value.Body);
        CollectionAssert.AreEqual(inviteId, offer.InviteId!.Value.ToArray());

        var confirmFrame = await PeerFrame.ReadAsync(stream, Cancellation);
        var confirm = PairConfirm.Read(confirmFrame!.Value.Body);
        CollectionAssert.AreEqual(mac, confirm.InviteMac!.Value.ToArray());

        var bareFrame = await PeerFrame.ReadAsync(stream, Cancellation);
        Assert.IsNull(PairConfirm.Read(bareFrame!.Value.Body).InviteMac, "absent must stay absent");
    }

    private static PairingInviteCode ForgeCode(string inviteIdHex, PeerRole role)
    {
        Span<byte> bytes = stackalloc byte[PairingInviteCode.InviteIdLength + 1 + PairingInviteCode.SecretLength];
        Convert.FromHexString(inviteIdHex).CopyTo(bytes);
        bytes[PairingInviteCode.InviteIdLength] = (byte)role;
        System.Security.Cryptography.RandomNumberGenerator.Fill(
            bytes[(PairingInviteCode.InviteIdLength + 1)..]);

        Assert.IsTrue(PairingInviteCode.TryParse(FallbackPlan.Domain.Base32.Encode(bytes), out var forged, out _));
        return forged!;
    }

    private async Task<(PairingResult Responder, PairingResult Offerer)> RunPairingAsync(
        string spokenCode,
        PairingInviteStore invites,
        string serviceGrantsName,
        string laptopGrantsName,
        PeerKeypair? offererKeypair = null)
    {
        var (listener, endpoint) = Listen();
        using var heldListener = listener;

        var serviceSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                await listener.AcceptAsync(Cancellation), Now, Cancellation);
            var first = await PeerFrame.ReadAsync(connection.Stream, Cancellation);
            return await PairingCeremony.AcceptWithInviteAsync(
                connection.Stream, PairOffer.Read(first!.Value.Body), _service, Grants(serviceGrantsName),
                "service", invites, connection.RemoteBinding, connection.LocalBinding, NowMs, Cancellation);
        });

        var laptopSide = Task.Run(async () =>
        {
            Assert.IsTrue(PairingInviteCode.TryParse(spokenCode, out var parsed, out _));
            await using var connection = await PeerTlsConnection.DialAsync(
                endpoint.Address.ToString(), endpoint.Port, Now, Cancellation);
            return await PairingCeremony.OfferWithInviteAsync(
                connection.Stream, offererKeypair ?? _laptop, Grants(laptopGrantsName), "laptop", parsed!,
                connection.LocalBinding, connection.RemoteBinding, NowMs, Cancellation);
        });

        return (await serviceSide, await laptopSide);
    }

    private static async Task<(PairRefuse? Refusal, bool Threw)> CaptureAsync(Task<PairingResult> task)
    {
        try
        {
            return ((await task).Refusal, false);
        }
        catch (PeerProtocolException)
        {
            return (null, true);
        }
    }

    private async Task PumpAsync(Stream from, Stream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, Cancellation)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), Cancellation);
                await to.FlushAsync(Cancellation);
            }
        }
        catch (Exception exception)
            when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // A relayed connection closing under us is the pump's ordinary end.
        }
    }

    private static async Task IgnoreFaultAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The caller is surfacing a different failure; this side's is noise.
        }
    }
}

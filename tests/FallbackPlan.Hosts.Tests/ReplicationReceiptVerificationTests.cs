using FallbackPlan.Agent;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// What a commander checks before it believes a replication receipt
/// (FR-DEST-004; [ADR-0064](../../docs/adr/0064-replication-receipts.md)).
/// A receipt is the destination's statement of what a push created and what
/// its replica holds afterwards, and it is only worth filing — and only
/// worth counting the destination complete on — if it was made by the peer
/// this session was opened to, for this session, about this repository, to
/// this commander, over keys this commander actually sent, with a count the
/// acknowledgement agrees with and a held figure no smaller than what the
/// destination declared plus what it committed. Each check is held
/// separately, because a receipt that fails any one of them is a different
/// lie.
/// </summary>
/// <remarks>
/// Held at the pure seam rather than over a live listener: a real destination
/// signs honest receipts, and a dishonest one is exactly what cannot be built
/// from this repository's own responder without teaching it to lie.
/// </remarks>
[TestClass]
public sealed class ReplicationReceiptVerificationTests : IDisposable
{
    private static readonly byte[] RepositoryId =
        Enumerable.Repeat((byte)0x11, ReplicationOffer.RepositoryIdLength).ToArray();

    private static readonly byte[] Session =
        Enumerable.Repeat((byte)0x22, ReplicationReceipt.SessionIdLength).ToArray();

    private static readonly string[] Sent = ["blobs/data/aa/one", "snapshots/aa/two"];

    /// <summary>What the destination declared before the push.</summary>
    private const long HeldAtStart = 5;

    private readonly PeerKeypair _destination = PeerKeypair.Generate();
    private readonly PeerKeypair _commander = PeerKeypair.Generate();

    [TestMethod]
    public void AReceiptTheDestinationSigned_ForThisSessionOverTheseKeys_IsAccepted()
    {
        var (verified, problem) = Verify(Ack(Receipt()));

        Assert.IsNull(problem);
        Assert.IsNotNull(verified);
        Assert.AreEqual(_destination.Identity, verified.Signer);
        Assert.AreEqual(Receipt(), verified.Receipt);
        Assert.IsTrue(verified.Signer.Verify(verified.SignedBytes.Span, verified.Signature.Span));
    }

    [TestMethod]
    public void NoReceipt_IsAFactAndNotAFault()
    {
        // A destination that predates receipts acknowledges with the count
        // alone; that is less, not wrong.
        var (verified, problem) = Verify(new ReplicationAck(2));

        Assert.IsNull(verified);
        Assert.IsNull(problem);
    }

    [TestMethod]
    public void AReceiptSignedByAnotherDevice_IsRejected()
    {
        using var impostor = PeerKeypair.Generate();

        AssertRejected(Ack(Receipt(), signer: impostor), "signature");
    }

    [TestMethod]
    public void AReceiptForAnotherSession_IsRejected()
    {
        // The same peer, the same keys, a different session: a recording of
        // an earlier push replayed into this one.
        var other = Enumerable.Repeat((byte)0x33, ReplicationReceipt.SessionIdLength).ToArray();

        AssertRejected(Ack(Receipt() with { SessionId = other }), "session");
    }

    [TestMethod]
    public void AReceiptNamingAnotherRepository_IsRejected()
    {
        var other = Enumerable.Repeat((byte)0x44, ReplicationOffer.RepositoryIdLength).ToArray();

        AssertRejected(Ack(Receipt() with { RepositoryId = other }), "repository");
    }

    [TestMethod]
    public void AReceiptAddressedToAnotherCommander_IsRejected()
    {
        using var someoneElse = PeerKeypair.Generate();

        AssertRejected(
            Ack(Receipt() with { CommanderPublicKey = someoneElse.Identity.PublicKey.ToArray() }),
            "commander");
    }

    [TestMethod]
    public void AReceiptListingAKeyThatWasNeverSent_IsRejected()
    {
        // A statement that something arrived which this commander never
        // sent: the receipt would attest an object the replica may hold from
        // somewhere else, or not at all.
        AssertRejected(
            Ack(Receipt() with { Committed = ["blobs/data/aa/one", "blobs/data/zz/never"] }),
            "never sent");
    }

    [TestMethod]
    public void AReceiptWhoseCountDisagreesWithTheAcknowledgement_IsRejected()
    {
        AssertRejected(Ack(Receipt(), count: 3), "disagrees");
    }

    [TestMethod]
    public void AReceiptHoldingFewerThanDeclaredPlusCommitted_IsRejected()
    {
        // The destination declared five and committed two, so it holds at
        // least seven or something it declared has gone since — either way
        // not a figure to count the destination complete on.
        AssertRejected(Ack(Receipt() with { HeldObjects = 6 }), "fewer");
    }

    public void Dispose()
    {
        _destination.Dispose();
        _commander.Dispose();
    }

    private void AssertRejected(ReplicationAck ack, string naming)
    {
        var (verified, problem) = Verify(ack);

        Assert.IsNull(verified, "a receipt that fails a check must not be handed on for filing");
        Assert.IsNotNull(problem, "a rejected receipt is rejected for a stated reason");
        Assert.Contains(naming, problem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An honest receipt: both sent keys committed, held after = declared + committed.</summary>
    private ReplicationReceipt Receipt() => new(
        SessionId: Session,
        RepositoryId: RepositoryId,
        CommanderPublicKey: _commander.Identity.PublicKey.ToArray(),
        IssuedAtUnixMilliseconds: 1_000,
        CommittedCount: 2,
        Committed: Sent,
        HeldObjects: 7,
        HeldBytes: 1_234);

    private ReplicationAck Ack(ReplicationReceipt receipt, PeerKeypair? signer = null, ulong? count = null)
    {
        var signed = receipt.EncodeForSigning();
        return new ReplicationAck(count ?? receipt.CommittedCount, signed, (signer ?? _destination).Sign(signed));
    }

    private (ReplicationInitiator.VerifiedReplicationReceipt? Receipt, string? Problem) Verify(ReplicationAck ack) =>
        ReplicationInitiator.VerifyReplicationReceipt(
            ack,
            new ReplicationInitiator.ReceiptExpectation(
                _destination.Identity, Session, _commander.Identity.PublicKey.ToArray()),
            RepositoryId,
            Sent.ToHashSet(StringComparer.Ordinal),
            HeldAtStart);
}

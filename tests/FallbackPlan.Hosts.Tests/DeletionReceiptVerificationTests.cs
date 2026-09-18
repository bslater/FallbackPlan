using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// What a commander checks before it believes a deletion receipt (FR-GC-008;
/// [ADR-0063](../../docs/adr/0063-deletion-receipts.md)). A receipt is the
/// destination's statement of what it deleted on whose instruction, and it is
/// only worth filing if it was made by the peer this session was opened to,
/// for this session, about the pages this commander actually sent, and over
/// keys it actually instructed. Each check is held separately, because a
/// receipt that fails any one of them is a different lie.
/// </summary>
/// <remarks>
/// Held at the pure seam rather than over a live listener: a real destination
/// signs honest receipts, and a dishonest one is exactly what cannot be built
/// from this repository's own responder without teaching it to lie.
/// </remarks>
[TestClass]
public sealed class DeletionReceiptVerificationTests : IDisposable
{
    private static readonly byte[] RepositoryId =
        Enumerable.Repeat((byte)0x11, ReplicationOffer.RepositoryIdLength).ToArray();

    private static readonly byte[] Session = Enumerable.Repeat((byte)0x22, DeletionReceipt.SessionIdLength).ToArray();

    private static readonly string[] Instructed = ["snapshots/aa/one", "snapshots/aa/two", "blobs/data/zz/gone"];

    private readonly PeerKeypair _spoke = PeerKeypair.Generate();
    private readonly PeerKeypair _commander = PeerKeypair.Generate();
    private readonly byte[][] _pages = [SHA256.HashData("page one"u8), SHA256.HashData("page two"u8)];

    [TestMethod]
    public void AReceiptTheSpokeSigned_ForThisSessionOverThesePages_IsAccepted()
    {
        var (verified, problem) = Verify(Ack(Receipt()));

        Assert.IsNull(problem);
        Assert.IsNotNull(verified);
        Assert.AreEqual(_spoke.Identity, verified.Signer);
        Assert.AreEqual(Receipt(), verified.Receipt);
        Assert.IsTrue(verified.Signer.Verify(verified.SignedBytes.Span, verified.Signature.Span));
    }

    [TestMethod]
    public void NoReceipt_IsAFactAndNotAFault()
    {
        // A destination that predates receipts acknowledges with the count
        // alone; that is less, not wrong.
        var (verified, problem) = Verify(new RetentionAck(2));

        Assert.IsNull(verified);
        Assert.IsNull(problem);
    }

    [TestMethod]
    public void AReceiptSignedByAnotherDevice_IsRejected()
    {
        using var impostor = PeerKeypair.Generate();

        AssertRejected(Ack(Receipt(), signer: impostor), naming: "sign");
    }

    [TestMethod]
    public void AReceiptForAnotherSession_IsRejected()
    {
        // The receipt that a recording of one session's exchange would carry
        // into another: everything about it verifies except when.
        var stale = Receipt() with
        {
            SessionId = Enumerable.Repeat((byte)0x23, DeletionReceipt.SessionIdLength).ToArray(),
        };

        AssertRejected(Ack(stale), naming: "session");
    }

    [TestMethod]
    public void AReceiptNamingAnotherRepository_IsRejected()
    {
        var elsewhere = Receipt() with
        {
            RepositoryId = Enumerable.Repeat((byte)0x12, ReplicationOffer.RepositoryIdLength).ToArray(),
        };

        AssertRejected(Ack(elsewhere), naming: "repository");
    }

    [TestMethod]
    public void AReceiptAddressedToAnotherCommander_IsRejected()
    {
        using var someoneElse = PeerKeypair.Generate();
        var misaddressed = Receipt() with { CommanderPublicKey = someoneElse.Identity.PublicKey.ToArray() };

        AssertRejected(Ack(misaddressed), naming: "commander");
    }

    [TestMethod]
    public void AReceiptOverPagesThatWereNotSent_IsRejected()
    {
        // Same pages, other order: the receipt commits to the instruction as
        // it was accepted, page by page, and this commander sent it the
        // other way round.
        var reordered = Receipt() with
        {
            PageDigests = [_pages[1], _pages[0]],
        };

        AssertRejected(Ack(reordered), naming: "page");
    }

    [TestMethod]
    public void AReceiptListingAKeyThatWasNeverInstructed_IsRejected()
    {
        var overreach = Receipt() with
        {
            Deleted = ["snapshots/aa/one", "snapshots/aa/never-instructed"],
        };

        AssertRejected(Ack(overreach), naming: "instruct");
    }

    [TestMethod]
    public void AReceiptWhoseCountDisagreesWithTheAcknowledgement_IsRejected()
    {
        AssertRejected(Ack(Receipt(), deleted: 5), naming: "count");
    }

    public void Dispose()
    {
        _spoke.Dispose();
        _commander.Dispose();
    }

    private void AssertRejected(RetentionAck ack, string naming)
    {
        var (verified, problem) = Verify(ack);

        Assert.IsNull(verified, "a receipt that fails a check must not be handed on for filing");
        Assert.IsNotNull(problem, "a rejected receipt is rejected for a stated reason");
        Assert.Contains(naming, problem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An honest receipt: two pages, two of the three instructed keys removed, one never held.</summary>
    private DeletionReceipt Receipt() => new(
        SessionId: Session,
        RepositoryId: RepositoryId,
        CommanderPublicKey: _commander.Identity.PublicKey.ToArray(),
        IssuedAtUnixMilliseconds: 1_000,
        FloorGenerations: 0,
        ReclaimPublicKey: ReadOnlyMemory<byte>.Empty,
        PageDigests: [_pages[0], _pages[1]],
        DeletedCount: 2,
        Deleted: ["snapshots/aa/one", "blobs/data/zz/gone"],
        NotHeld: 1);

    private RetentionAck Ack(DeletionReceipt receipt, PeerKeypair? signer = null, ulong? deleted = null)
    {
        var signed = receipt.EncodeForSigning();
        return new RetentionAck(deleted ?? receipt.DeletedCount, signed, (signer ?? _spoke).Sign(signed));
    }

    private (ReplicationInitiator.VerifiedReceipt? Receipt, string? Problem) Verify(RetentionAck ack) =>
        ReplicationInitiator.VerifyReceipt(
            ack,
            new ReplicationInitiator.ReceiptExpectation(_spoke.Identity, Session, _commander.Identity.PublicKey.ToArray()),
            RepositoryId,
            _pages,
            Instructed.ToHashSet(StringComparer.Ordinal));
}

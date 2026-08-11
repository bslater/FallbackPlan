using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The replication messages (specification peer-protocol 03 §3): each frame
/// round-trips through the codec, the state machine admits them only when the
/// session is Open, and malformed or oversized bodies are refused.
/// </summary>
[TestClass]
public sealed class ReplicationMessageTests
{
    private static TMessage RoundTrip<TMessage>(IPeerMessage message, Func<System.Formats.Cbor.CborReader, TMessage> read)
    {
        var (_, body) = PeerFrame.Decode(PeerFrame.Encode(message));
        return read(body);
    }

    [TestMethod]
    public void Offer_RoundTrips()
    {
        var offer = new ReplicationOffer(new byte[16], 5, "all");
        Assert.AreEqual(offer, RoundTrip(offer, ReplicationOffer.Read));
    }

    [TestMethod]
    public void Inventory_WithKeysAndEmpty_RoundTrips()
    {
        var page = new ReplicationInventory(["blobs/data/aaaa/one", "snapshots/x/y/z"], More: true);
        Assert.AreEqual(page, RoundTrip(page, ReplicationInventory.Read));

        var empty = new ReplicationInventory([], More: false);
        var read = RoundTrip(empty, ReplicationInventory.Read);
        Assert.AreEqual(0, read.Keys.Count);
        Assert.IsFalse(read.More);
    }

    [TestMethod]
    public void ObjectHeaderAndChunk_RoundTrip()
    {
        var header = new ReplicationObject("blobs/data/aaaa/one", 4096);
        Assert.AreEqual(header, RoundTrip(header, ReplicationObject.Read));

        var chunk = new ReplicationChunk(1024, new byte[] { 1, 2, 3, 4 });
        Assert.AreEqual(chunk, RoundTrip(chunk, ReplicationChunk.Read));
    }

    [TestMethod]
    public void CompleteAndAck_RoundTrip()
    {
        Assert.AreEqual(new ReplicationComplete(42), RoundTrip(new ReplicationComplete(42), ReplicationComplete.Read));
        Assert.AreEqual(new ReplicationAck(41), RoundTrip(new ReplicationAck(41), ReplicationAck.Read));
    }

    [TestMethod]
    public void ReplicationTypes_ArePermittedOnlyWhenOpen()
    {
        foreach (var type in new[]
        {
            PeerMessageType.ReplicationOffer, PeerMessageType.ReplicationInventory,
            PeerMessageType.ReplicationObject, PeerMessageType.ReplicationChunk,
            PeerMessageType.ReplicationComplete, PeerMessageType.ReplicationAck,
        })
        {
            Assert.IsTrue(PeerAuthenticator.Permits(PeerSessionState.Open, type), $"{type} should be permitted when Open");
            Assert.IsFalse(
                PeerAuthenticator.Permits(PeerSessionState.Authenticated, type),
                $"{type} must not be permitted before Open");
            Assert.IsFalse(
                PeerAuthenticator.Permits(PeerSessionState.Encrypted, type),
                $"{type} must not be permitted in Encrypted");
        }
    }

    [TestMethod]
    public void Offer_WithAWrongLengthRepositoryId_IsRefused()
    {
        var offer = new ReplicationOffer(new byte[8], 5, "all");
        var body = PeerFrame.Decode(PeerFrame.Encode(offer)).Body;

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => ReplicationOffer.Read(body));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void Chunk_OverTheByteLimit_IsRefusedOnWrite()
    {
        var chunk = new ReplicationChunk(0, new byte[ReplicationChunk.MaximumBytes + 1]);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(chunk));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void Inventory_OverTheKeyLimit_IsRefusedOnWrite()
    {
        var page = new ReplicationInventory(
            Enumerable.Range(0, ReplicationInventory.MaximumKeys + 1).Select(i => $"k{i}").ToList(), More: false);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(page));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }
}

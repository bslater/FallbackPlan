using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The retrieval messages (specification peer-protocol 07 §3): each frame
/// round-trips through the codec, the state machine admits them only when
/// the session is Open, and oversized bodies are refused at encode.
/// </summary>
[TestClass]
public sealed class RetrievalMessageTests
{
    private static TMessage RoundTrip<TMessage>(IPeerMessage message, Func<System.Formats.Cbor.CborReader, TMessage> read)
    {
        var (_, body) = PeerFrame.Decode(PeerFrame.Encode(message));
        return read(body);
    }

    [TestMethod]
    public void OpenAndReady_RoundTrip()
    {
        var open = new RetrieveOpen(new byte[16], 1);
        Assert.AreEqual(open, RoundTrip(open, RetrieveOpen.Read));

        Assert.IsNotNull(RoundTrip(new RetrieveReady(), RetrieveReady.Read));
    }

    [TestMethod]
    public void Open_RepositoryIdOfTheWrongWidth_IsRefused()
    {
        var frame = PeerFrame.Encode(new RetrieveOpen(new byte[15], 1));
        var (_, body) = PeerFrame.Decode(frame);
        Assert.ThrowsExactly<PeerProtocolException>(() => RetrieveOpen.Read(body));
    }

    [TestMethod]
    public void ListAndPage_RoundTrip()
    {
        var list = new RetrieveList("blobs/meta/", "blobs/meta/aaaa/last");
        Assert.AreEqual(list, RoundTrip(list, RetrieveList.Read));

        var page = RoundTrip(
            new RetrieveListPage(["keys/0", "repository-format"], [42UL, 7UL], More: true),
            RetrieveListPage.Read);
        Assert.AreEqual("repository-format", page.Keys[1]);
        Assert.AreEqual(7UL, page.Lengths[1]);
        Assert.IsTrue(page.More);

        var empty = RoundTrip(new RetrieveListPage([], [], More: false), RetrieveListPage.Read);
        Assert.AreEqual(0, empty.Keys.Count);
    }

    [TestMethod]
    public void ReadAndData_RoundTrip()
    {
        var read = new RetrieveRead("blobs/data/aaaa/one", 4_096, 65_536);
        Assert.AreEqual(read, RoundTrip(read, RetrieveRead.Read));

        var stat = new RetrieveRead("repository-format", 0, 0);
        Assert.AreEqual(stat, RoundTrip(stat, RetrieveRead.Read));

        var data = new RetrieveData(Found: true, 1_000_000, new byte[] { 1, 2, 3 });
        Assert.AreEqual(data, RoundTrip(data, RetrieveData.Read));

        var miss = RoundTrip(new RetrieveData(Found: false, 0, ReadOnlyMemory<byte>.Empty), RetrieveData.Read);
        Assert.IsFalse(miss.Found);
        Assert.AreEqual(0, miss.Bytes.Length);
    }

    [TestMethod]
    public void Read_BeyondTheChunkLimit_IsRefusedAtEncode()
    {
        Assert.ThrowsExactly<PeerProtocolException>(() =>
            PeerFrame.Encode(new RetrieveRead("blobs/data/aaaa/one", 0, ReplicationChunk.MaximumBytes + 1UL)));
    }

    [TestMethod]
    public void RetrievalFrames_ArePermittedOnlyOnceOpen()
    {
        // The same wall the replication payload stands behind (02 §2): a
        // retrieval frame from a stranger mid-handshake is refused by state.
        foreach (var type in new[]
        {
            PeerMessageType.RetrieveOpen, PeerMessageType.RetrieveReady,
            PeerMessageType.RetrieveList, PeerMessageType.RetrieveListPage,
            PeerMessageType.RetrieveRead, PeerMessageType.RetrieveData,
        })
        {
            Assert.IsTrue(PeerAuthenticator.Permits(PeerSessionState.Open, type), $"{type} once open");
            Assert.IsFalse(PeerAuthenticator.Permits(PeerSessionState.Encrypted, type), $"{type} before auth");
            Assert.IsFalse(PeerAuthenticator.Permits(PeerSessionState.Authenticated, type), $"{type} before accept");
        }
    }

    [TestMethod]
    public void RetrievalFeature_IsOfferedByDefault()
    {
        Assert.Contains(PeerSessionNegotiation.RetrievalFeature, PeerSessionNegotiation.SupportedFeatures);
    }
}

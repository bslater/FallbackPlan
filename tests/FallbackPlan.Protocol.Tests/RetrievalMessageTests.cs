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

    [TestMethod]
    public void ChunkPossessionFeature_IsOfferedAndNeverRequired()
    {
        // 02 §6 forbids a feature being the sole gate on a check that
        // defends one side against the other. This one survives that rule
        // because its absence costs the destination more, not less: a peer
        // that does not offer it is read back whole, as every peer is today.
        // So it is offered and never required.
        Assert.Contains(PeerSessionNegotiation.ChunkPossessionFeature, PeerSessionNegotiation.SupportedFeatures);

        // An old peer that has never heard of it is not refused: the
        // intersection simply drops the feature and the session continues.
        var ours = new SessionHello(1, 1, PeerSessionNegotiation.SupportedFeatures, [], "1.0.0", Terms: null);
        var theirs = new SessionHello(
            1, 1,
            [.. PeerSessionNegotiation.SupportedFeatures.Where(
                feature => feature != PeerSessionNegotiation.ChunkPossessionFeature)],
            [], "1.0.0", Terms: null);

        var effective = PeerSessionNegotiation.SelectFeatures(ours, theirs);
        Assert.DoesNotContain(PeerSessionNegotiation.ChunkPossessionFeature, effective);
        Assert.Contains(PeerSessionNegotiation.RetrievalFeature, effective);
    }

    [TestMethod]
    public void MerkleChallengeAndProof_RoundTrip()
    {
        var challenge = new MerkleChallenge(new byte[16], "blobs/data/ab/abcdef", 7);
        Assert.AreEqual(challenge, RoundTrip(challenge, MerkleChallenge.Read));

        var held = new MerkleProof(
            true,
            "the leaf's bytes"u8.ToArray(),
            [new byte[32], Enumerable.Repeat((byte)0x5A, 32).ToArray()]);
        Assert.AreEqual(held, RoundTrip(held, MerkleProof.Read));

        // A one-leaf blob's path is legitimately empty, which must still
        // read as held rather than as a disagreement with the status.
        var single = new MerkleProof(true, "x"u8.ToArray(), []);
        var decodedSingle = RoundTrip(single, MerkleProof.Read);
        Assert.IsTrue(decodedSingle.Held);
        Assert.IsEmpty(decodedSingle.Path);

        var absent = new MerkleProof(false, ReadOnlyMemory<byte>.Empty, []);
        var decodedAbsent = RoundTrip(absent, MerkleProof.Read);
        Assert.IsFalse(decodedAbsent.Held);
        Assert.IsEmpty(decodedAbsent.Leaf.ToArray());
    }

    [TestMethod]
    public void MerkleChallenge_ARepositoryIdOfTheWrongWidth_IsRefused()
    {
        Assert.ThrowsExactly<PeerProtocolException>(
            () => PeerFrame.Encode(new MerkleChallenge(new byte[15], "blobs/data/ab/abcdef", 0)));
    }

    [TestMethod]
    public void MerkleProof_AStatusAndPayloadThatDisagree_AreRefused()
    {
        // The answer's shape is the check: a "cannot prove" carrying bytes,
        // or a proof carrying none, is a peer that does not mean what this
        // message means (07 §3.5).
        Assert.ThrowsExactly<PeerProtocolException>(
            () => MerkleProof.Read(Body(writer =>
            {
                writer.WriteStartMap(3);
                writer.WriteInt32(0);
                writer.WriteUInt32((uint)PeerMessageType.MerkleProof);
                writer.WriteInt32(1);
                writer.WriteUInt32(1);
                writer.WriteInt32(2);
                writer.WriteByteString("bytes a refusal must not carry"u8);
                writer.WriteEndMap();
            })));

        Assert.ThrowsExactly<PeerProtocolException>(
            () => MerkleProof.Read(Body(writer =>
            {
                writer.WriteStartMap(2);
                writer.WriteInt32(0);
                writer.WriteUInt32((uint)PeerMessageType.MerkleProof);
                writer.WriteInt32(1);
                writer.WriteUInt32(0);
                writer.WriteEndMap();
            })));
    }

    [TestMethod]
    public void MerkleProof_APathStepOfTheWrongWidth_IsRefusedRatherThanDropped()
    {
        // ReplicationPartial's rule: a field that gates a decision arriving
        // broken is fatal, because dropping it turns a verification into a
        // shrug.
        Assert.ThrowsExactly<PeerProtocolException>(
            () => PeerFrame.Encode(new MerkleProof(true, "x"u8.ToArray(), [new byte[31]])));

        Assert.ThrowsExactly<PeerProtocolException>(
            () => MerkleProof.Read(Body(writer =>
            {
                writer.WriteStartMap(4);
                writer.WriteInt32(0);
                writer.WriteUInt32((uint)PeerMessageType.MerkleProof);
                writer.WriteInt32(1);
                writer.WriteUInt32(0);
                writer.WriteInt32(2);
                writer.WriteByteString("x"u8);
                writer.WriteInt32(3);
                writer.WriteStartArray(1);
                writer.WriteByteString(new byte[31]);
                writer.WriteEndArray();
                writer.WriteEndMap();
            })));
    }

    [TestMethod]
    public void MerkleProof_APathBeyondTheStepBound_IsRefused()
    {
        var steps = Enumerable.Range(0, MerkleProof.MaximumSteps + 1)
            .Select(_ => (ReadOnlyMemory<byte>)new byte[32])
            .ToList();

        Assert.ThrowsExactly<PeerProtocolException>(
            () => PeerFrame.Encode(new MerkleProof(true, "x"u8.ToArray(), steps)));
    }

    [TestMethod]
    public void MerkleProof_ALeafBeyondTheLeafBound_IsRefused()
    {
        Assert.ThrowsExactly<PeerProtocolException>(
            () => PeerFrame.Encode(
                new MerkleProof(true, new byte[MerkleProof.MaximumLeafBytes + 1], [])));
    }

    [TestMethod]
    public void MerkleFrames_BeforeTheSessionIsOpen_AreNotPermitted()
    {
        foreach (var type in new[] { PeerMessageType.MerkleChallenge, PeerMessageType.MerkleProof })
        {
            Assert.IsTrue(PeerAuthenticator.Permits(PeerSessionState.Open, type));
            Assert.IsFalse(PeerAuthenticator.Permits(PeerSessionState.Encrypted, type));
            Assert.IsFalse(PeerAuthenticator.Permits(PeerSessionState.Authenticated, type));
        }
    }

    private static System.Formats.Cbor.CborReader Body(Action<System.Formats.Cbor.CborWriter> write)
    {
        var writer = new System.Formats.Cbor.CborWriter(System.Formats.Cbor.CborConformanceMode.Canonical);
        write(writer);
        var (_, body) = PeerFrame.Decode(writer.Encode());
        return body;
    }
}

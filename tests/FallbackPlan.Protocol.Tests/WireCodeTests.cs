using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The numbers that cross the wire, pinned against the specification tables
/// that define them (peer-protocol 02 §7 and §8).
/// </summary>
/// <remarks>
/// <para>
/// These are the least interesting assertions in the repository and among the
/// most load-bearing. A refusal reason is a <c>u16</c> on the wire; the enum
/// that produces it is ordinary C#, and inserting a value in the middle of an
/// enum is a thing people do without thinking. Do that here and two builds
/// stop meaning the same thing by the same number — a <c>revoked</c> read as
/// <c>version_unsupported</c>, a <c>storage_exhausted</c> read as
/// <c>pairing_declined</c> — with no compile error, no test failure, and no
/// symptom until two households on different versions try to talk.
/// </para>
/// <para>
/// The existing refusal tests all compare enum value to enum value, which is
/// true no matter what the numbers are. These are the only assertions that
/// tie the vocabulary to the numbers the specification publishes.
/// </para>
/// </remarks>
[TestClass]
public sealed class WireCodeTests
{
    /// <summary>The refusal codes of peer-protocol 02 §8, transcribed from the specification's table.</summary>
    public static IEnumerable<object[]> RefusalCodes =>
    [
        [1, PeerRefusalReason.IdentityChanged],
        [2, PeerRefusalReason.NotPaired],
        [3, PeerRefusalReason.Revoked],
        [4, PeerRefusalReason.VersionUnsupported],
        [5, PeerRefusalReason.FeatureUnsupported],
        [6, PeerRefusalReason.MessageUnknown],
        [7, PeerRefusalReason.Malformed],
        [8, PeerRefusalReason.TermsRefused],
        [9, PeerRefusalReason.Busy],
        [10, PeerRefusalReason.PairingDeclined],
        [11, PeerRefusalReason.AuthenticationFailed],
        [12, PeerRefusalReason.StorageExhausted],
    ];

    [TestMethod]
    [DynamicData(nameof(RefusalCodes))]
    public void RefusalReason_EachName_CarriesTheCodeTheSpecificationPublishes(int code, PeerRefusalReason reason) =>
        Assert.AreEqual(code, (int)reason);

    [TestMethod]
    public void RefusalReason_TheVocabulary_IsExactlyTheSpecificationsTwelve()
    {
        // A thirteenth value added here without a row in 02 §8 would be a
        // number this side sends and the specification does not define, which
        // is how a peer ends up answering "malformed" to a refusal.
        var declared = Enum.GetValues<PeerRefusalReason>().Select(reason => (int)reason).OrderBy(code => code);
        var published = RefusalCodes.Select(row => (int)row[0]).OrderBy(code => code);

        CollectionAssert.AreEqual(published.ToArray(), declared.ToArray());
    }

    /// <summary>The frame types of peer-protocol 02 §7, for the pairing and session ranges.</summary>
    public static IEnumerable<object[]> MessageTypes =>
    [
        [1, PeerMessageType.PairOffer],
        [2, PeerMessageType.PairAccept],
        [3, PeerMessageType.PairConfirm],
        [4, PeerMessageType.PairRefuse],
        [5, PeerMessageType.SessionHello],
        [6, PeerMessageType.SessionAccept],
        [7, PeerMessageType.SessionRefuse],
        [8, PeerMessageType.SessionAuth],
        [9, PeerMessageType.SessionAuthProof],
        [10, PeerMessageType.PeeringTermination],
    ];

    [TestMethod]
    [DynamicData(nameof(MessageTypes))]
    public void MessageType_EachFrame_CarriesTheTypeTheSpecificationPublishes(int code, PeerMessageType type) =>
        Assert.AreEqual(code, (int)type);
}

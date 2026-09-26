using FallbackPlan.Domain.Status;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The status vocabulary is normative and closed (NFR-OPS-002 as amended,
/// architecture 10 §1.1): exactly the states the deriver can emit, no dead
/// entries for a surface to drift around. Establishes the vocabulary half of
/// NFR-OPS-002; the never-merge derivation rules themselves are established
/// by <c>Repository.Tests/ApplicationServiceTests</c> and
/// <c>Application.Tests/DestinationStatusTests</c>.
/// </summary>
[TestClass]
public sealed class ProtectionStateTests
{
    [TestMethod]
    public void TheVocabulary_IsExactlyTheDerivedLadder()
    {
        // Two states once lived here without ever being emitted — Replicated
        // (subsumed by the Captured/Protected failure-domain distinction) and
        // PolicyCompliant (a durability policy this product does not have).
        // A normative vocabulary with dead entries invites every surface to
        // disagree about what can actually appear.
        CollectionAssert.AreEquivalent(
            new[]
            {
                "NeverBackedUp", "Captured", "Protected", "Verified", "Degraded", "Unrecoverable",
            },
            Enum.GetNames<ProtectionState>());
    }

    [TestMethod]
    public void TheWireNumbers_SurviveTheRetirement()
    {
        // The contract serializer carries enums numerically (FrameCodec has
        // no string converter), so retired members leave their numbers
        // reserved: 3 and 5 are never reassigned, and every surviving state
        // keeps the value every deployed client already maps.
        Assert.AreEqual(0, (int)ProtectionState.NeverBackedUp);
        Assert.AreEqual(1, (int)ProtectionState.Captured);
        Assert.AreEqual(2, (int)ProtectionState.Protected);
        Assert.AreEqual(4, (int)ProtectionState.Verified);
        Assert.AreEqual(6, (int)ProtectionState.Degraded);
        Assert.AreEqual(7, (int)ProtectionState.Unrecoverable);
    }
}

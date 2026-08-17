using FallbackPlan.Application;
using FallbackPlan.Retention;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// Retention must not outrun replication (FR-GC-009): the PT-9 story — a
/// destination offline past the retention window must not cause history to
/// vanish from every replica silently.
/// </summary>
[TestClass]
public sealed class ReplicationGateTests
{
    private const ulong Now = 1_700_000_000_000;
    private const ulong Day = 24 * 3_600_000UL;

    private static readonly SnapshotFact Old = new(new string('a', 64), Now - 40 * Day, PublicationSequence: 5);

    [TestMethod]
    public void Apply_EveryDestinationSyncedPastTheSnapshot_ExpiresIt()
    {
        var result = ReplicationGate.Apply(
            [Old], ["vault", "friend"],
            _ => Synced(generation: 9),
            deferralDays: null, Now);

        Assert.AreEqual(Old, Assert.ContainsSingle(result.Expirable));
        Assert.IsEmpty(result.Held);
    }

    [TestMethod]
    public void Apply_OneDestinationBehindTheSnapshot_HoldsItAndNamesTheLaggard()
    {
        var result = ReplicationGate.Apply(
            [Old], ["vault", "friend"],
            name => Synced(generation: name == "friend" ? 3UL : 9UL, lastSuccessAt: Now - Day),
            deferralDays: null, Now);

        // Held, not expired: expiring costs history that exists nowhere
        // else, holding costs disk (ADR-0011 Amendment 2). The laggard is
        // named so the report explains rather than stalls.
        Assert.IsEmpty(result.Expirable);
        var held = Assert.ContainsSingle(result.Held);
        Assert.AreEqual("friend", Assert.ContainsSingle(held.AwaitingDestinations));

        // Behind by a day, bound thirty: a quiet hold, not yet a warning.
        Assert.IsFalse(held.DeferralExceeded);
    }

    [TestMethod]
    public void Apply_ALaggardBeyondTheBound_MarksTheHoldAsAWarning()
    {
        // Never synced, and the snapshot has existed forty days: past the
        // thirty-day default, holding quietly becomes hiding a problem.
        var result = ReplicationGate.Apply(
            [Old], ["friend"], _ => null, deferralDays: null, Now);

        var held = Assert.ContainsSingle(result.Held);
        Assert.IsTrue(held.DeferralExceeded);
    }

    [TestMethod]
    public void Apply_TheBoundIsConfigured_ItIsTheOneApplied()
    {
        // The same laggard under a wider bound stays a quiet hold.
        var result = ReplicationGate.Apply(
            [Old], ["friend"], _ => null, deferralDays: 90, Now);

        Assert.IsFalse(Assert.ContainsSingle(result.Held).DeferralExceeded);
    }

    private static DestinationSyncRecord Synced(ulong generation, ulong? lastSuccessAt = null) => new()
    {
        SetId = new string('a', 32),
        Destination = "any",
        State = DestinationSyncState.InSync,
        LastAttemptAt = lastSuccessAt ?? Now,
        LastSuccessAt = lastSuccessAt ?? Now,
        SyncedSequence = generation,
    };
}

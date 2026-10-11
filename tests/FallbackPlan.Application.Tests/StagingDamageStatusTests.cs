using FallbackPlan.Application;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// Damage found in a set's staging archive and not yet replaced holds the set
/// degraded and says so (FR-VER-009). Every destination may read in sync
/// while it stands, because the copy each was filled from is the one that
/// lost the objects, and the sweep could find no destination to give them
/// back from.
/// </summary>
[TestClass]
public sealed class StagingDamageStatusTests
{
    private static StatusInputs Healthy() => new()
    {
        LatestSnapshotAt = 1_722_600_000_000,
        LatestCaptureStatus = 1,
        Destinations =
        [
            new DestinationStatusInput
            {
                Name = "vault",
                Kind = DestinationKind.LocalPath,
                Sync = DestinationSyncState.InSync,
                Domain = FailureDomain.Independent,
            },
        ],
        DamageFindings = 0,
        StagingDamage = 0,
        RequiredObjectsMissing = false,
    };

    [TestMethod]
    public void OutstandingStagingDamage_DegradesASetEveryDestinationHoldsInSync_AndSaysHowMuch()
    {
        Assert.AreEqual(ProtectionState.Protected, StatusDeriver.Derive(Healthy()).State, "the control must be protected");

        var status = StatusDeriver.Derive(Healthy() with { StagingDamage = 2 });

        Assert.AreEqual(ProtectionState.Degraded, status.State);
        var warning = Assert.ContainsSingle(status.Warnings);
        Assert.Contains("2 object(s)", warning, StringComparison.Ordinal);
        Assert.Contains("staging archive", warning, StringComparison.Ordinal);
    }

    [TestMethod]
    public void StagingDamage_BesideOpenCatalogueFindings_SaysBoth()
    {
        var status = StatusDeriver.Derive(Healthy() with { StagingDamage = 1, DamageFindings = 3 });

        Assert.AreEqual(ProtectionState.Degraded, status.State);
        Assert.HasCount(2, status.Warnings);
        Assert.Contains(warning => warning.Contains("staging archive", StringComparison.Ordinal), status.Warnings);
        Assert.Contains(warning => warning.Contains("`check`", StringComparison.Ordinal), status.Warnings);
    }

    [TestMethod]
    public void StagingDamage_NeverSoftensUnrecoverable()
    {
        var status = StatusDeriver.Derive(Healthy() with { StagingDamage = 1, RequiredObjectsMissing = true });

        Assert.AreEqual(ProtectionState.Unrecoverable, status.State);
    }

    [TestMethod]
    public void StagingDamage_OnASetNeverBackedUp_IsNotWhatItIsToldFirst()
    {
        // Nothing has committed, so there is nothing a damaged object could
        // be part of: the first thing to say is still that.
        var status = StatusDeriver.Derive(Healthy() with { LatestSnapshotAt = null, StagingDamage = 1 });

        Assert.AreEqual(ProtectionState.NeverBackedUp, status.State);
    }
}
